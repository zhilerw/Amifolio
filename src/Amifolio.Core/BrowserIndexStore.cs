using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Amifolio.Core;

public sealed record BrowserDirectorySnapshot(string ProjectId, string Path, IReadOnlyList<CatalogEntry> Entries, bool Truncated);
public sealed record BrowserIndexSnapshot(int Version, string CodexHome, DateTimeOffset SavedAt,
    IReadOnlyList<CodexProject> Projects, IReadOnlyList<BrowserDirectorySnapshot> Directories,
    string? LastProjectId, string? LastDirectory);

/// <summary>
/// Disposable, display-only metadata. Never supplies a CodexCatalog allowlist or reads source bodies.
/// Version 1 is independent of StoreSchema. Any invalid member (including an unsafe directory or
/// last location) discards the entire snapshot; missing/unreadable/oversized caches return null.
/// Invalid or failed saves return false and leave the previous snapshot intact. Cancellation always propagates.
/// </summary>
public sealed class BrowserIndexStore(string dataRoot, string codexHome)
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const int MaximumProjects = 2048;
    private const int MaximumDirectories = 16;
    private const int MaximumEntries = 5000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 32,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<BrowserIndexSnapshot?> LoadAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var root = Absolute(dataRoot);
            var path = CachePath(root);
            await using var input = ArchiveSafety.OpenRead(path);
            if (input.Length > MaximumBytes) return Discard();
            using var bytes = new BoundedBuffer();
            // Bounded even if another process grows the file after the length check.
            await input.CopyToAsync(bytes, ct).ConfigureAwait(false);
            bytes.Position = 0;
            var snapshot = await JsonSerializer.DeserializeAsync<BrowserIndexSnapshot>(bytes, Json, ct).ConfigureAwait(false);
            return Validate(snapshot, ct);
        }
        catch (Exception ex) when (Discardable(ex)) { return Discard(); }

        BrowserIndexSnapshot? Discard() { ct.ThrowIfCancellationRequested(); return null; }
    }

    public async Task<bool> SaveAsync(BrowserIndexSnapshot snapshot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        string? temporary = null;
        DirectoryPins? pins = null;
        try
        {
            // Copy and validate caller-owned collections before touching storage.
            var safe = Validate(snapshot, ct);
            using var bytes = new BoundedBuffer();
            await JsonSerializer.SerializeAsync(bytes, safe, Json, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var root = Absolute(dataRoot);
            var path = CachePath(root);
            pins = DirectoryPins.Create(root);
            ArchiveSafety.NoReparse(path);
            temporary = Path.Combine(root, "browser-index-" + Guid.NewGuid().ToString("N") + ".partial");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                bytes.Position = 0;
                await bytes.CopyToAsync(output, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
            }
            ArchiveSafety.NoReparse(path);
            ct.ThrowIfCancellationRequested();
            // Same-directory rename is the commit point. There is no delete-old/write-new gap.
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            return true;
        }
        catch (Exception ex) when (Discardable(ex)) { ct.ThrowIfCancellationRequested(); return false; }
        finally
        {
            // Keep directory handles pinned until staging cleanup is complete.
            try
            {
                if (temporary is not null)
                {
                    try { ArchiveSafety.NoReparse(temporary); File.Delete(temporary); }
                    catch (Exception ex) when (Discardable(ex)) { }
                }
            }
            finally { pins?.Dispose(); }
        }
    }

    private BrowserIndexSnapshot Validate(BrowserIndexSnapshot? snapshot, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (snapshot is null || snapshot.Version != 1 || snapshot.Projects is null || snapshot.Directories is null ||
            snapshot.Projects.Count > MaximumProjects || snapshot.Directories.Count > MaximumDirectories ||
            !Absolute(snapshot.CodexHome).Equals(Absolute(codexHome), ArchiveSafety.PathComparison))
            throw Invalid();

        var projects = new Dictionary<string, CodexProject>(StringComparer.Ordinal);
        var roots = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var project in snapshot.Projects)
        {
            ct.ThrowIfCancellationRequested();
            if (project is null || string.IsNullOrWhiteSpace(project.Id) || project.Name is null || project.SessionCount < 0 || projects.Count >= MaximumProjects)
                throw Invalid();
            var root = Absolute(project.RootPath);
            ArchiveSafety.NoReparse(root);
            if (!roots.Add(root) || !projects.TryAdd(project.Id, project with { RootPath = root })) throw Invalid();
        }

        var directories = new List<BrowserDirectorySnapshot>();
        var seenDirectories = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var totalEntries = 0;
        foreach (var directory in snapshot.Directories)
        {
            ct.ThrowIfCancellationRequested();
            if (directory is null || directory.ProjectId is null || !projects.TryGetValue(directory.ProjectId, out var project) ||
                directory.Entries is null || directory.Entries.Count > MaximumEntries - totalEntries || directories.Count >= MaximumDirectories)
                throw Invalid();
            var path = Scoped(directory.Path, project.RootPath);
            ArchiveSafety.NoReparse(path);
            // Include the project in the identity: nested projects may browse the same directory.
            if (!seenDirectories.Add(directory.ProjectId + "\0" + path)) throw Invalid();
            var entries = new List<CatalogEntry>();
            var names = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            foreach (var entry in directory.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry is null || entry.Name is null || entry.Kind is null || entry.State is null || entry.Bytes < 0 || ++totalEntries > MaximumEntries)
                    throw Invalid();
                var full = Scoped(entry.Path, path);
                if (!string.Equals(Path.GetDirectoryName(full), path, ArchiveSafety.PathComparison) ||
                    !string.Equals(Path.GetFileName(full), entry.Name, StringComparison.Ordinal) || !names.Add(full)) throw Invalid();
                // Paths are display metadata; do not open the entry or resolve source file contents.
                entries.Add(entry with { Path = full });
            }
            directories.Add(directory with { Path = path, Entries = entries.ToArray() });
        }

        string? lastDirectory = null;
        if (snapshot.LastProjectId is not null)
        {
            if (!projects.TryGetValue(snapshot.LastProjectId, out var project)) throw Invalid();
            if (snapshot.LastDirectory is not null)
            {
                lastDirectory = Scoped(snapshot.LastDirectory, project.RootPath);
                ArchiveSafety.NoReparse(lastDirectory);
            }
        }
        else if (snapshot.LastDirectory is not null) throw Invalid();
        ct.ThrowIfCancellationRequested();
        return snapshot with { CodexHome = Absolute(codexHome), Projects = projects.Values.ToArray(), Directories = directories.ToArray(), LastDirectory = lastDirectory };
    }

    private static string CachePath(string root)
    {
        ArchiveSafety.NoReparse(root);
        var path = Absolute(Path.Combine(root, "browser-index.json"));
        if (!ArchiveSafety.Within(path, root)) throw Invalid();
        ArchiveSafety.NoReparse(path);
        return path;
    }

    private static string Scoped(string path, string root)
    {
        var full = Absolute(path);
        if (!ArchiveSafety.Within(full, root)) throw Invalid();
        return full;
    }

    private static string Absolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw Invalid();
        if (OperatingSystem.IsWindows() && (path.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Split('\\', '/').Skip(1).Any(p => p is not ("." or "..") && (p.EndsWith('.') || p.EndsWith(' '))))) throw Invalid();
        return ArchiveSafety.LocalPath(path);
    }

    private static IOException Invalid() => new("Invalid or oversized browser metadata cache.");
    private static bool Discardable(Exception ex) => ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException;

    private sealed class BoundedBuffer : MemoryStream
    {
        private void CheckLength(int count) { if (Position + count > MaximumBytes) throw Invalid(); }
        public override void Write(byte[] buffer, int offset, int count) { CheckLength(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { CheckLength(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { CheckLength(1); base.WriteByte(value); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); CheckLength(count);
            return base.WriteAsync(buffer, offset, count, cancellationToken);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); CheckLength(buffer.Length);
            return base.WriteAsync(buffer, cancellationToken);
        }
    }

    // On Windows, deny directory rename/deletion while creating and replacing files. Pin each
    // ancestor before creating its child so a concurrent junction swap cannot redirect writes.
    private sealed class DirectoryPins : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];
        internal static DirectoryPins Create(string root)
        {
            var pins = new DirectoryPins();
            try
            {
                var ancestors = new Stack<string>();
                for (string? p = root; p is not null; p = Path.GetDirectoryName(p)) ancestors.Push(p);
                foreach (var directory in ancestors)
                {
                    ArchiveSafety.NoReparse(directory);
                    Directory.CreateDirectory(directory);
                    if (OperatingSystem.IsWindows())
                    {
                        var handle = CreateFileW(directory, 0, FileShare.Read | FileShare.Write, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Cannot pin browser cache directory."); }
                        pins.handles.Add(handle);
                    }
                    ArchiveSafety.NoReparse(directory);
                }
                return pins;
            }
            catch { pins.Dispose(); throw; }
        }
        public void Dispose() { for (var i = handles.Count - 1; i >= 0; i--) handles[i].Dispose(); }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, uint disposition, uint flags, IntPtr template);
    }
}
