using System.Globalization;
using System.Net;
using System.Numerics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amifolio.Context;

/// <summary>Version is the stable numeric version (without v/build metadata); failures use null and the fixed releases page.</summary>
public record ReleaseCheckResult(string State, string Message, string? Version, Uri ReleasePage);

/// <summary>
/// Explicit, anonymous version checks only; never downloads assets or opens a browser.
/// States: update-available, up-to-date, ahead, invalid-version, unavailable, access-limited,
/// rate-limited, server-error, redirect-blocked, http-error, offline, timeout,
/// invalid-response, response-too-large, unsafe-client. Caller cancellation propagates.
/// </summary>
public sealed class GitHubReleaseService
{
    public static readonly Uri RepoReleasesPage = new("https://github.com/zhilerw/Amifolio/releases");

    private static readonly Uri LatestReleaseApi = new("https://api.github.com/repos/zhilerw/Amifolio/releases/latest");
    private const int MaxResponseBytes = 64 * 1024;
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(10);
    // Lazy construction avoids even constructing a transport until an explicit valid check.
    private static readonly Lazy<HttpClient> AnonymousClient = new(() => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        UseDefaultCredentials = false,
        Credentials = null,
        PreAuthenticate = false,
        AutomaticDecompression = DecompressionMethods.None
    }) { Timeout = Timeout.InfiniteTimeSpan });
    private static readonly Regex StableTag = new(
        @"\A[vV]?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly HttpClient? suppliedClient;

    /// <summary>
    /// An injected client is caller-owned and must have redirects, cookies and credentials disabled
    /// in its handler. HttpClient does not expose those handler settings; production uses the private
    /// anonymous transport above. Credential-bearing default headers are refused without sending.
    /// </summary>
    public GitHubReleaseService(HttpClient? client = null) => suppliedClient = client;

    public async Task<ReleaseCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!TryVersion(currentVersion, out var current))
            return Failure("invalid-version", "当前版本格式无效，未联网检查。");

        var client = suppliedClient ?? AnonymousClient.Value;
        // HttpClient merges default headers when sending. Refuse rather than mutate a caller's client
        // or accidentally inherit its Authorization/Proxy-Authorization/Cookie values.
        if (client.DefaultRequestHeaders.Contains("Authorization") || client.DefaultRequestHeaders.Contains("Proxy-Authorization") ||
            client.DefaultRequestHeaders.Contains("Cookie"))
            return Failure("unsafe-client", "检查客户端含身份凭据，已停止匿名检查。");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(CheckTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd("Amifolio-ReleaseCheck/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2026-03-10");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (response.RequestMessage?.RequestUri is { } finalUri && finalUri != LatestReleaseApi)
                return Failure("redirect-blocked", "发布检查地址发生跳转，已停止检查。");

            var status = (int)response.StatusCode;
            if (status is >= 300 and < 400)
                return Failure("redirect-blocked", "发布检查地址发生跳转，已停止检查。");
            if (response.StatusCode == HttpStatusCode.NotFound)
                return Failure("unavailable", "无法确认版本：仓库可能私有或暂无稳定发布。");
            if (response.StatusCode == HttpStatusCode.TooManyRequests ||
                (response.StatusCode == HttpStatusCode.Forbidden &&
                 (response.Headers.RetryAfter is not null ||
                  (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0")))))
                return Failure("rate-limited", "发布检查受到请求限额限制，请稍后手动重试。");
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return Failure("access-limited", "匿名访问受限，可能为私有仓库或请求限额；无法确认版本。");
            if (status >= 500)
                return Failure("server-error", "GitHub 服务暂时异常，无法确认版本。");
            if (response.StatusCode != HttpStatusCode.OK)
                return Failure("http-error", $"发布检查请求失败（HTTP {status}），无法确认版本。");

            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                return Failure("response-too-large", "发布信息超过 64 KiB，已停止检查。");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            // One extra byte distinguishes an exactly-full valid body from an oversized/chunked body.
            // Never buffer HttpContent with ReadAsStringAsync or trust its advertised Content-Length.
            var bytes = new byte[MaxResponseBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(count), deadline.Token).ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }
            ct.ThrowIfCancellationRequested();
            deadline.Token.ThrowIfCancellationRequested();
            if (count > MaxResponseBytes)
                return Failure("response-too-large", "发布信息超过 64 KiB，已停止检查。");

            using var json = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 16 });
            var root = json.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !SingleProperty(root, "tag_name", out var tagValue) ||
                tagValue.ValueKind != JsonValueKind.String || !SingleProperty(root, "draft", out var draft) ||
                draft.ValueKind != JsonValueKind.False || !SingleProperty(root, "prerelease", out var prerelease) ||
                prerelease.ValueKind != JsonValueKind.False || !TryVersion(tagValue.GetString(), out var latest))
                return InvalidResponse();

            // A validated ASCII stable tag becomes one escaped path segment under our fixed repository.
            // html_url, url, body and browser_download_url are never used as navigation targets or messages.
            var page = new Uri(RepoReleasesPage.AbsoluteUri + "/tag/" + Uri.EscapeDataString(tagValue.GetString()!));
            var comparison = latest.CompareTo(current);
            return comparison > 0
                ? new("update-available", "发现可用的新稳定版本。", latest.Display, page)
                : comparison == 0
                    ? new("up-to-date", "当前版本与最新稳定版相同。", latest.Display, page)
                    : new("ahead", "当前版本高于可查询的稳定版。", latest.Display, page);
        }
        catch (OperationCanceledException)
        {
            ct.ThrowIfCancellationRequested();
            return Failure("timeout", "发布检查超时，请稍后手动重试。");
        }
        catch (JsonException) { return InvalidResponse(); }
        catch (HttpRequestException) { return Failure("offline", "网络不可用或连接失败，无法确认版本。"); }
        catch (IOException) { return Failure("offline", "网络不可用或连接失败，无法确认版本。"); }
    }

    private static ReleaseCheckResult Failure(string state, string message) => new(state, message, null, RepoReleasesPage);
    private static ReleaseCheckResult InvalidResponse() => Failure("invalid-response", "发布信息无效或不是稳定版，无法确认版本。");

    private static bool SingleProperty(JsonElement root, string name, out JsonElement value)
    {
        value = default;
        var count = 0;
        foreach (var property in root.EnumerateObject())
            if (property.NameEquals(name)) { value = property.Value; count++; }
        return count == 1;
    }

    private static bool TryVersion(string? text, out StableVersion version)
    {
        version = default;
        if (text is null || text.Length is 0 or > 128) return false;
        var match = StableTag.Match(text);
        if (!match.Success) return false;
        var major = BigInteger.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minor = BigInteger.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var patch = BigInteger.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var revision = match.Groups[4].Success ? BigInteger.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : BigInteger.Zero;
        var display = string.Join('.', match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value);
        if (match.Groups[4].Success) display += "." + match.Groups[4].Value;
        version = new(major, minor, patch, revision, display);
        return true;
    }

    private readonly record struct StableVersion(BigInteger Major, BigInteger Minor, BigInteger Patch, BigInteger Revision, string Display)
    {
        internal int CompareTo(StableVersion other)
        {
            var result = Major.CompareTo(other.Major);
            if (result == 0) result = Minor.CompareTo(other.Minor);
            if (result == 0) result = Patch.CompareTo(other.Patch);
            return result == 0 ? Revision.CompareTo(other.Revision) : result;
        }
    }
}
