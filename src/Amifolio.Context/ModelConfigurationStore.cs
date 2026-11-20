using System.Security.Cryptography;
using System.Text.Json;
using Amifolio.Core;

namespace Amifolio.Context;

public sealed record SavedModelConfiguration(string Endpoint, string Model, bool RemoteAllowed, string ApiKey)
{
    public override string ToString() => "SavedModelConfiguration (API key hidden)";
}

/// <summary>User initiated persistence; loading or saving never contacts the provider.</summary>
public sealed class ModelConfigurationStore
{
    private readonly string root;
    private readonly string path;
    private sealed record Stored(int FormatVersion, string Endpoint, string Model, bool RemoteAllowed, string? ProtectedApiKey);
    private static SavedModelConfiguration Default => new("https://api.openai.com/v1", "", false, "");

    public ModelConfigurationStore(string dataRoot)
    {
        // Use the same local-path validation as the library, including its reparse checks.
        root = new PortfolioStore(dataRoot).DataRoot;
        path = Path.Combine(root, "settings", "ai.json");
    }

    public async Task<SavedModelConfiguration> LoadAsync(CancellationToken ct = default)
    {
        await using var lease = await StorageLease.SharedAsync(root, ct).ConfigureAwait(false);
        SafeFiles.CheckPath(path); if (!File.Exists(path)) return Default;
        try
        {
            var bytes = await SafeFiles.ReadBoundedAsync(path, 65_536, ct).ConfigureAwait(false);
            var stored = JsonSerializer.Deserialize<Stored>(bytes) ?? throw new JsonException();
            if (stored.FormatVersion != 1) throw new IOException("AI 配置版本不受支持，请重新保存。");
            var key = "";
            if (!string.IsNullOrEmpty(stored.ProtectedApiKey))
            {
                var plain = WindowsCredentialProtection.Unprotect(Convert.FromBase64String(stored.ProtectedApiKey));
                try { key = SafeFiles.Utf8.GetString(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
            }
            var configuration = new SavedModelConfiguration(stored.Endpoint, stored.Model, stored.RemoteAllowed, key);
            Validate(configuration); return configuration;
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or ArgumentException or InvalidDataException)
        { throw new IOException("无法读取已保存的 AI 配置，请重新填写并保存。", e); }
    }

    public async Task SaveAsync(SavedModelConfiguration configuration, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); Validate(configuration);
        var encrypted = "";
        if (configuration.ApiKey.Length != 0)
        {
            var bytes = SafeFiles.Utf8.GetBytes(configuration.ApiKey);
            try { encrypted = Convert.ToBase64String(WindowsCredentialProtection.Protect(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        var stored = new Stored(1, configuration.Endpoint.Trim().TrimEnd('/'), configuration.Model.Trim(), configuration.RemoteAllowed, encrypted);
        await using var lease = await StorageLease.ExclusiveAsync(root, ct).ConfigureAwait(false);
        await SafeFiles.AtomicWriteAsync(path, JsonSerializer.SerializeToUtf8Bytes(stored), ct).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken ct = default)
    {
        await using var lease = await StorageLease.ExclusiveAsync(root, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); SafeFiles.CheckPath(path);
        if (File.Exists(path)) File.Delete(path);
    }

    private static void Validate(SavedModelConfiguration value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Endpoint is null || value.Model is null || value.ApiKey is null || value.Endpoint.Length > 4096 || value.Model.Length > 256 || value.ApiKey.Length > 8192 ||
            value.Endpoint.Any(char.IsControl) || value.Model.Any(char.IsControl) || value.ApiKey.Any(char.IsControl) ||
            !Uri.TryCreate(value.Endpoint.Trim(), UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback))
            throw new ArgumentException("请填写有效的 HTTPS 服务地址；本机服务可使用 HTTP。模型与密钥不能包含换行。");
    }
}
