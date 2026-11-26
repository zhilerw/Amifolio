using System.Text.Json;

namespace Amifolio.Core;

public sealed partial class CodexCatalog
{
    // The caller supplies UUIDs from works already recorded for an enabled project.
    // This optional metadata file contains display names, not project or turn evidence.
    internal IReadOnlyDictionary<Guid, string?> ReadSessionTitles(HashSet<Guid> sessions, CancellationToken ct)
    {
        if (sessions.Count == 0) return new Dictionary<Guid, string?>();
        var path = Path.Combine(home, "session_index.jsonl");
        byte[] bytes;
        try
        {
            ct.ThrowIfCancellationRequested();
            ArchiveSafety.NoReparse(path);
            if ((File.GetAttributes(path) & FileAttributes.Offline) != 0) return new Dictionary<Guid, string?>();
            using var stream = ArchiveSafety.OpenRead(path);
            var length = stream.Length;
            if (length > MaxMetadataBytes) return new Dictionary<Guid, string?>();
            bytes = new byte[(int)length];
            stream.ReadExactly(bytes);
        }
        catch (Exception ex) when (PathFailure(ex))
        {
            return new Dictionary<Guid, string?>();
        }

        var latest = new Dictionary<Guid, (DateTimeOffset UpdatedAt, string? Title)>();
        ReadOnlyMemory<byte> remaining = CleanBom(bytes);
        while (!remaining.IsEmpty)
        {
            ct.ThrowIfCancellationRequested();
            var newline = remaining.Span.IndexOf((byte)'\n');
            var line = newline < 0 ? remaining : remaining[..newline];
            remaining = newline < 0 ? ReadOnlyMemory<byte>.Empty : remaining[(newline + 1)..];
            if (line.IsEmpty || line.Length > MaxSessionHeaderBytes) continue;

            try
            {
                using var json = JsonDocument.Parse(line, JsonOptions);
                var row = json.RootElement;
                if (row.ValueKind != JsonValueKind.Object ||
                    !row.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                    !Guid.TryParseExact(id.GetString(), "D", out var sessionId) || !sessions.Contains(sessionId) ||
                    !row.TryGetProperty("updated_at", out var timestamp) || timestamp.ValueKind != JsonValueKind.String ||
                    !timestamp.TryGetDateTimeOffset(out var updatedAt)) continue;

                var hasPrevious = latest.TryGetValue(sessionId, out var previous);
                if (hasPrevious && previous.UpdatedAt > updatedAt) continue;
                var title = row.TryGetProperty("thread_name", out var name) && name.ValueKind == JsonValueKind.String
                    ? name.GetString()?.Trim()
                    : null;
                if (title is not { Length: > 0 and <= 512 } || title.Any(char.IsControl) ||
                    Guid.TryParse(title, out _) || MemoryPolicy.ContainsSensitiveText(title))
                    title = null;
                if (hasPrevious && previous.UpdatedAt == updatedAt && previous.Title != title) title = null;
                latest[sessionId] = (updatedAt, title);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // An unrecognized metadata row does not block other source labels.
            }
        }
        ct.ThrowIfCancellationRequested();
        return latest.ToDictionary(pair => pair.Key, pair => pair.Value.Title);
    }
}
