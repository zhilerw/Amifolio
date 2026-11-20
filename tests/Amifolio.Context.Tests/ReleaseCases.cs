using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Amifolio.Context;

internal static class ReleaseCases
{
    internal static (string, Func<Task>)[] All =>
    [
        ("release constructor performs no request", NoStartupRequest),
        ("release uses one anonymous bounded GitHub GET with required headers", RequestContract),
        ("release stable major beats informational commit version", MajorUpgrade),
        ("release compares numeric components rather than strings", NumericComparison),
        ("release ignores build metadata for equality", MetadataEquality),
        ("release compares optional fourth revision", RevisionComparison),
        ("release local newer is reported separately", LocalAhead),
        ("release invalid current version stops before network", InvalidCurrent),
        ("release rejects invalid and unsafe remote tags", InvalidTags),
        ("release rejects prerelease draft and ambiguous flags", Unstable),
        ("release rejects malformed incomplete and ambiguous JSON", Malformed),
        ("release never trusts remote html or asset URLs", UntrustedUrls),
        ("release derives trusted page for revision and build tag", TagPage),
        ("release 404 cannot claim latest or up to date", NotFound),
        ("release 401 is explicit anonymous access uncertainty", Unauthorized),
        ("release 403 distinguishes explicit rate limit", Forbidden),
        ("release 429 is rate limited without retry", RateLimited),
        ("release server errors remain uncertain", ServerErrors),
        ("release rejects every redirect without following location", Redirects),
        ("release detects transport supplied unexpected final URI", UnexpectedResponseUri),
        ("release rejects unexpected success status", UnexpectedStatus),
        ("release offline diagnostics do not expose exception details", Offline),
        ("release declared overflow is rejected before reading", DeclaredOverflow),
        ("release streamed overflow reads at most 64 KiB plus one byte", StreamOverflow),
        ("release accepts exactly 64 KiB JSON", ExactLimit),
        ("release body read failure is safe and disposes response", BrokenBody),
        ("release ten second deadline covers headers", HeaderTimeout),
        ("release ten second deadline covers streamed body", BodyTimeout),
        ("release honors pre canceled caller before network", PreCanceled),
        ("release honors caller cancellation during headers", CancelHeaders),
        ("release honors caller cancellation during body", CancelBody),
        ("release refuses clients carrying auth defaults without changing them", AuthDefaults),
        ("release does not mutate or dispose supplied client", ClientOwnership),
        ("release concurrent checks keep independent cancellation", ConcurrentChecks)
    ];

    private const string Page = "https://github.com/zhilerw/Amifolio/releases";
    private const string Api = "https://api.github.com/repos/zhilerw/Amifolio/releases/latest";
    private static string Json(string tag = "v1.0.0") => JsonSerializer.Serialize(new { tag_name = tag, draft = false, prerelease = false });
    private static HttpResponseMessage Response(string? json = null, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json ?? Json(), Encoding.UTF8, "application/json") };

    private static Task NoStartupRequest()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("Unexpected startup network"));
        using var client = new HttpClient(handler);
        _ = new GitHubReleaseService(client);
        _ = new GitHubReleaseService();
        Check(handler.Calls == 0, "constructor contacted network");
        Check(GitHubReleaseService.RepoReleasesPage.AbsoluteUri == Page, "wrong trusted page");
        return Task.CompletedTask;
    }

    private static async Task RequestContract()
    {
        using var handler = new FakeHandler((request, ct) =>
        {
            Check(request.Method == HttpMethod.Get && request.RequestUri?.AbsoluteUri == Api, "wrong method/endpoint");
            Check(request.Content is null && ct.CanBeCanceled, "request has body or no deadline");
            Check(request.Headers.UserAgent.ToString().Contains("Amifolio", StringComparison.Ordinal), "missing user agent");
            Check(request.Headers.GetValues("X-GitHub-Api-Version").Single() == "2026-03-10", "wrong API version");
            Check(request.Headers.Accept.Any(x => x.MediaType == "application/vnd.github+json"), "missing JSON accept");
            Check(!request.Headers.Contains("Authorization") && !request.Headers.Contains("Proxy-Authorization") &&
                !request.Headers.Contains("Cookie"), "credentials sent");
            return Task.FromResult(Response());
        });
        using var client = new HttpClient(handler);
        Result(await new GitHubReleaseService(client).CheckAsync("0.1.3+commit"), "update-available", "1.0.0", Page + "/tag/v1.0.0");
        Check(handler.Calls == 1, "check retried or followed another endpoint");
    }

    private static async Task MajorUpgrade() => Result(await Run(Json(), current: "0.1.3+abc123"), "update-available", "1.0.0", Page + "/tag/v1.0.0");
    private static async Task NumericComparison()
    {
        Result(await Run(Json("v1.10.0"), current: "1.9.99"), "update-available", "1.10.0", Page + "/tag/v1.10.0");
        Result(await Run(Json("v2.0.0"), current: "10.0.0"), "ahead", "2.0.0", Page + "/tag/v2.0.0");
    }
    private static async Task MetadataEquality() => Result(await Run(Json("v1.0.0+release.42"), current: "1.0.0+commit.3"),
        "up-to-date", "1.0.0", Page + "/tag/v1.0.0%2Brelease.42");
    private static async Task RevisionComparison()
    {
        Result(await Run(Json("v1.2.3.4"), current: "1.2.3.3+commit"), "update-available", "1.2.3.4", Page + "/tag/v1.2.3.4");
        Result(await Run(Json("v1.2.3"), current: "1.2.3.0"), "up-to-date", "1.2.3", Page + "/tag/v1.2.3");
    }
    private static async Task LocalAhead() => Result(await Run(Json(), current: "2.0.0"), "ahead", "1.0.0", Page + "/tag/v1.0.0");

    private static async Task InvalidCurrent()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("invalid version contacted network"));
        using var client = new HttpClient(handler);
        foreach (var version in new[] { "", "1", "1.2", "1.2.3-beta", "01.2.3", "1.2.3+", "1.2.3.4.5", " 1.2.3", "1.2.3\n", null })
            Result(await new GitHubReleaseService(client).CheckAsync(version!), "invalid-version");
        Check(handler.Calls == 0, "invalid input sent request");
    }
    private static async Task InvalidTags()
    {
        foreach (var tag in new[] { "", "latest", "1.2", "v01.2.3", "v1.2.3-beta", "v1.2.3/../../evil", "v1.2.3?x=1",
            "https://evil.invalid", "v1.2.3#x", "v1.2.3%2f", "v1.2.3\n", "v１.2.3", "v1.2.3+", "v1.2.3+a..b", new string('9', 200) })
            Result(await Run(Json(tag)), "invalid-response");
    }
    private static async Task Unstable()
    {
        foreach (var json in new[] {
            "{\"tag_name\":\"v2.0.0\",\"draft\":true,\"prerelease\":false}",
            "{\"tag_name\":\"v2.0.0\",\"draft\":false,\"prerelease\":true}",
            "{\"tag_name\":\"v2.0.0\",\"draft\":false}",
            "{\"tag_name\":\"v2.0.0\",\"draft\":\"false\",\"prerelease\":false}",
            "{\"tag_name\":\"v2.0.0\",\"draft\":false,\"prerelease\":null}" })
            Result(await Run(json), "invalid-response");
    }
    private static async Task Malformed()
    {
        foreach (var json in new[] { "", "<html>SECRET</html>", "null", "[]", "42", "{}", "{", Json() + "[]",
            "{\"tag_name\":4,\"draft\":false,\"prerelease\":false}",
            "{\"tag_name\":\"v1.0.0\",\"tag_name\":\"v2.0.0\",\"draft\":false,\"prerelease\":false}",
            "{\"tag_name\":\"v1.0.0\",\"draft\":true,\"draft\":false,\"prerelease\":false}",
            "{\"tag_name\":\"v1.0.0\",\"draft\":false,\"prerelease\":true,\"prerelease\":false}",
            "{\"extra\":" + new string('[', 40) + "0" + new string(']', 40) + ",\"tag_name\":\"v1.0.0\",\"draft\":false,\"prerelease\":false}" })
            Result(await Run(json), "invalid-response");
    }
    private static async Task UntrustedUrls()
    {
        foreach (var url in new[] { "https://evil.invalid/malware", "file:///C:/secret", "javascript:alert(1)", "https://github.com.evil.invalid/" })
        {
            var json = JsonSerializer.Serialize(new { tag_name = "v1.0.0", draft = false, prerelease = false, html_url = url,
                url, assets = new[] { new { browser_download_url = url } } });
            Result(await Run(json), "update-available", "1.0.0", Page + "/tag/v1.0.0");
        }
    }
    private static async Task TagPage() => Result(await Run(Json("1.2.3.4+build.7")), "update-available", "1.2.3.4", Page + "/tag/1.2.3.4%2Bbuild.7");
    private static async Task NotFound() => Result(await Run(status: HttpStatusCode.NotFound), "unavailable");
    private static async Task Unauthorized() => Result(await Run(status: HttpStatusCode.Unauthorized), "access-limited");
    private static async Task Forbidden()
    {
        Result(await Run(status: HttpStatusCode.Forbidden), "access-limited");
        Result(await WithResponse(() => { var r = Response(status: HttpStatusCode.Forbidden); r.Headers.Add("X-RateLimit-Remaining", "0"); return r; }), "rate-limited");
    }
    private static async Task RateLimited() => Result(await Run(status: HttpStatusCode.TooManyRequests), "rate-limited");
    private static async Task ServerErrors()
    {
        foreach (var status in new[] { HttpStatusCode.InternalServerError, HttpStatusCode.BadGateway, HttpStatusCode.ServiceUnavailable })
            Result(await Run(status: status), "server-error");
    }
    private static async Task Redirects()
    {
        foreach (var code in new[] { 300, 301, 302, 303, 304, 307, 308 })
            Result(await WithResponse(() => { var r = Response(status: (HttpStatusCode)code); r.Headers.Location = new("https://evil.invalid/SECRET"); return r; }), "redirect-blocked");
    }
    private static async Task UnexpectedResponseUri() => Result(await WithResponse(() =>
    {
        var r = Response(); r.RequestMessage = new(HttpMethod.Get, "https://evil.invalid/final"); return r;
    }), "redirect-blocked");
    private static async Task UnexpectedStatus()
    {
        foreach (var code in new[] { 201, 204, 400, 418 }) Result(await Run(status: (HttpStatusCode)code), "http-error");
    }
    private static async Task Offline()
    {
        using var handler = new FakeHandler((_, _) => throw new HttpRequestException("SECRET https://evil.invalid/"));
        using var client = new HttpClient(handler);
        Result(await new GitHubReleaseService(client).CheckAsync("0.1.3"), "offline");
    }
    private static async Task DeclaredOverflow()
    {
        using var content = new NeverReadContent();
        Result(await WithResponse(() => new(HttpStatusCode.OK) { Content = content }), "response-too-large");
        Check(!content.Read && content.Disposed, "oversized declared body read or not disposed");
    }
    private static async Task StreamOverflow()
    {
        using var stream = new ProbeStream(Encoding.UTF8.GetBytes(Json() + new string(' ', 100000)));
        Result(await WithResponse(() => new(HttpStatusCode.OK) { Content = new StreamContent(stream) }), "response-too-large");
        Check(stream.BytesRead <= 65537 && stream.Disposed, "unbounded body read or response leak");
    }
    private static async Task ExactLimit()
    {
        var json = Json(); using var stream = new ProbeStream(Encoding.UTF8.GetBytes(json + new string(' ', 65536 - Encoding.UTF8.GetByteCount(json))));
        Result(await WithResponse(() => new(HttpStatusCode.OK) { Content = new StreamContent(stream) }), "update-available", "1.0.0", Page + "/tag/v1.0.0");
        Check(stream.BytesRead == 65536 && stream.Disposed, "boundary not read/disposed correctly");
    }
    private static async Task BrokenBody()
    {
        using var stream = new ProbeStream([], broken: true);
        Result(await WithResponse(() => new(HttpStatusCode.OK) { Content = new StreamContent(stream) }), "offline");
        Check(stream.Disposed, "failed response not disposed");
    }
    private static async Task HeaderTimeout()
    {
        var timer = Stopwatch.StartNew();
        using var handler = new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return Response(); });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        Result(await new GitHubReleaseService(client).CheckAsync("0.1.3").WaitAsync(TimeSpan.FromSeconds(15)), "timeout");
        Check(timer.Elapsed >= TimeSpan.FromSeconds(9) && timer.Elapsed < TimeSpan.FromSeconds(14), "missing ten second header deadline");
    }
    private static async Task BodyTimeout()
    {
        using var stream = new ProbeStream([], stall: true);
        var timer = Stopwatch.StartNew();
        Result(await WithResponse(() => new(HttpStatusCode.OK) { Content = new StreamContent(stream) }).WaitAsync(TimeSpan.FromSeconds(15)), "timeout");
        Check(timer.Elapsed >= TimeSpan.FromSeconds(9) && timer.Elapsed < TimeSpan.FromSeconds(14) && stream.Disposed, "body deadline or disposal missing");
    }
    private static async Task PreCanceled()
    {
        using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("canceled check sent network"));
        using var client = new HttpClient(handler); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Canceled(() => new GitHubReleaseService(client).CheckAsync("0.1.3", cancel.Token), cancel.Token);
        Check(handler.Calls == 0, "pre cancellation ignored");
    }
    private static async Task CancelHeaders()
    {
        using var cancel = new CancellationTokenSource();
        using var handler = new FakeHandler(async (_, ct) => { cancel.Cancel(); await Task.Delay(Timeout.Infinite, ct); return Response(); });
        using var client = new HttpClient(handler);
        await Canceled(() => new GitHubReleaseService(client).CheckAsync("0.1.3", cancel.Token), cancel.Token);
    }
    private static async Task CancelBody()
    {
        using var cancel = new CancellationTokenSource();
        using var stream = new ProbeStream([], stall: true, onRead: cancel.Cancel);
        await Canceled(() => WithResponse(() => new(HttpStatusCode.OK) { Content = new StreamContent(stream) }, cancel.Token), cancel.Token);
        Check(stream.Disposed, "cancellation leaked response");
    }
    private static async Task AuthDefaults()
    {
        foreach (var header in new[] { "Authorization", "Proxy-Authorization", "Cookie" })
        {
            using var handler = new FakeHandler((_, _) => throw new InvalidOperationException("auth default sent"));
            using var client = new HttpClient(handler); client.DefaultRequestHeaders.TryAddWithoutValidation(header, "Bearer SECRET");
            Result(await new GitHubReleaseService(client).CheckAsync("0.1.3"), "unsafe-client");
            Check(handler.Calls == 0 && client.DefaultRequestHeaders.GetValues(header).Single() == "Bearer SECRET", "auth client sent or mutated");
        }
    }
    private static async Task ClientOwnership()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(Response()));
        using var client = new HttpClient(handler); var timeout = client.Timeout;
        var service = new GitHubReleaseService(client);
        await service.CheckAsync("0.1.3"); await service.CheckAsync("0.1.3");
        using var direct = await client.GetAsync("https://local-test.invalid/unused");
        Check(handler.Calls == 3 && client.Timeout == timeout && !client.DefaultRequestHeaders.Any(), "supplied client changed/disposed");
    }
    private static async Task ConcurrentChecks()
    {
        using var cancel = new CancellationTokenSource(); var count = 0;
        using var handler = new FakeHandler(async (_, ct) =>
        {
            if (Interlocked.Increment(ref count) == 1) { cancel.Cancel(); await Task.Delay(Timeout.Infinite, ct); }
            return Response();
        });
        using var client = new HttpClient(handler); var service = new GitHubReleaseService(client);
        var canceled = service.CheckAsync("0.1.3", cancel.Token); var success = service.CheckAsync("0.1.3");
        await Canceled(() => canceled, cancel.Token);
        Result(await success, "update-available", "1.0.0", Page + "/tag/v1.0.0");
    }

    private static Task<ReleaseCheckResult> Run(string? json = null, HttpStatusCode status = HttpStatusCode.OK, string current = "0.1.3") =>
        WithResponse(() => Response(json, status), current: current);
    private static async Task<ReleaseCheckResult> WithResponse(Func<HttpResponseMessage> response, CancellationToken ct = default, string current = "0.1.3")
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(response()));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var result = await new GitHubReleaseService(client).CheckAsync(current, ct);
        Check(handler.Calls == 1, "request repeated"); return result;
    }
    private static void Result(ReleaseCheckResult result, string state, string? version = null, string page = Page)
    {
        Check(result.State == state, $"expected {state}, got {result.State}");
        Check(result.Version == version, $"expected version {version}, got {result.Version}");
        Check(result.ReleasePage.AbsoluteUri == page, "untrusted/wrong release page: " + result.ReleasePage);
        Check(result.Message.Length is > 0 and <= 100 && result.Message.Any(c => c >= '\u4e00' && c <= '\u9fff'), "missing concise Chinese message");
        Check(!result.Message.Contains("SECRET", StringComparison.Ordinal) && !result.Message.Contains("evil.invalid", StringComparison.Ordinal), "untrusted detail exposed");
        if (version is null) Check(!result.Message.Contains("已是最新", StringComparison.Ordinal) && !result.Message.Contains("无需更新", StringComparison.Ordinal), "uncertain result claims up to date");
    }
    private static async Task Canceled(Func<Task> run, CancellationToken expected)
    {
        try { await run(); } catch (OperationCanceledException error) { Check(error.CancellationToken == expected, "caller token not preserved"); return; }
        throw new InvalidOperationException("expected caller cancellation, not a result");
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return send(request, cancellationToken); }
    }
    private sealed class NeverReadContent : HttpContent
    {
        internal bool Read, Disposed;
        internal NeverReadContent() { Headers.ContentLength = 65537; }
        protected override bool TryComputeLength(out long length) { length = 65537; return true; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        { Read = true; throw new IOException("oversized content should never be read"); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
    private sealed class ProbeStream(byte[] data, bool stall = false, bool broken = false, Action? onRead = null) : Stream
    {
        internal int BytesRead;
        internal bool Disposed;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            onRead?.Invoke();
            if (stall) await Task.Delay(Timeout.Infinite, cancellationToken);
            if (broken) throw new IOException("SECRET body failure");
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, data.Length - BytesRead);
            data.AsMemory(BytesRead, count).CopyTo(buffer); BytesRead += count; return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("use cancellable async read");
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }
}
