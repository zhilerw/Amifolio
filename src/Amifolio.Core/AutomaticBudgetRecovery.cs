using System.Globalization;
using System.Text.Json;

namespace Amifolio.Core;

/// <summary>Preserve automatic-request accounting independently of restored consent.</summary>
internal static class AutomaticBudgetRecovery
{
    private sealed record Budget(string Day, int Requests, DateTimeOffset? LastAttempt);
    internal static byte[] Capture(string path) => JsonSerializer.SerializeToUtf8Bytes(Read(path));
    internal static void Merge(string staged, string current)
    {
        ArchiveSafety.NoReparse(staged); ArchiveSafety.NoReparse(current);
        var incoming = File.Exists(staged) ? Read(staged) : null;
        var existing = File.Exists(current) ? Read(current) : null;
        if (incoming is null && existing is null) return;
        var selected = incoming is null ? existing! : existing is null ? incoming :
            string.CompareOrdinal(incoming.Day, existing.Day) > 0 ? incoming : existing;
        if (incoming is not null && existing is not null && incoming.Day == existing.Day)
            selected = selected with { Requests = Math.Max(incoming.Requests, existing.Requests) };
        var last = new[] { incoming?.LastAttempt, existing?.LastAttempt }.Where(x => x is not null).Max();
        selected = selected with { LastAttempt = last };
        StorageFiles.Write(staged, JsonSerializer.SerializeToUtf8Bytes(selected));
    }
    private static Budget Read(string path)
    {
        using var stream = ArchiveSafety.OpenRead(path);
        if (stream.Length > 4096) throw new IOException("自动请求计数超过大小上限。");
        Budget? value;
        try { value = JsonSerializer.Deserialize<Budget>(stream); }
        catch (JsonException error) { throw new IOException("自动请求计数损坏，请保留原库并检查。", error); }
        if (value is null || value.Requests < 0 || value.Requests > 8 || !DateOnly.TryParseExact(value.Day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) throw new IOException("自动请求计数无效。");
        return value;
    }
}
