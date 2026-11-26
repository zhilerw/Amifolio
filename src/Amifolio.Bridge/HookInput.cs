using System.Text.Json;
using Amifolio.Core;

namespace Amifolio.Bridge;

internal static class HookInput
{
    internal static async Task<JsonDocument> ReadAsync(string expectedEvent, CancellationToken ct)
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        using var input = Console.OpenStandardInput();
        int count;
        while ((count = await input.ReadAsync(buffer, ct)) > 0)
        {
            bytes.Write(buffer, 0, count);
            if (bytes.Length > 65536) throw new IOException("Hook input limit.");
        }

        var json = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        var root = json.RootElement;
        if (root.ValueKind == JsonValueKind.Object &&
            root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == root.EnumerateObject().Count() &&
            ReadString(root, "hook_event_name", 50) == expectedEvent)
            return json;

        json.Dispose();
        throw new IOException("Invalid hook event.");
    }

    internal static string? ReadString(JsonElement element, string key, int maximum) =>
        element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String &&
        value.GetString() is { Length: > 0 } str && str.Length <= maximum && !str.Contains('\0') ? str : null;

    internal static CodexProject? ResolveProject(IEnumerable<CodexProject> projects, string cwd)
    {
        var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        // Require a real local directory. In particular, a junction cannot borrow another project's consent.
        if (!Directory.Exists(current)) return null;
        for (var path = current; path is not null; path = Path.GetDirectoryName(path))
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) return null;
        return projects.Where(p => current.Equals(Path.TrimEndingDirectorySeparator(p.RootPath), comparison) ||
                current.StartsWith(Path.TrimEndingDirectorySeparator(p.RootPath) + Path.DirectorySeparatorChar, comparison))
            .OrderByDescending(p => p.RootPath.Length).FirstOrDefault();
    }

    internal static bool ExpectedFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException
        or InvalidOperationException or JsonException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException;
}
