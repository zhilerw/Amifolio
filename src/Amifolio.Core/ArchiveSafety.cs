using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Amifolio.Core;

internal static class ArchiveSafety
{
    internal const long MaxFileBytes = 256L * 1024 * 1024;
    internal const long MaxRepositoryBytes = 512L * 1024 * 1024;
    internal const int MaxRepositoryEntries = 20_000;
    internal const int MaxDepth = 64;
    internal const int PreviewChars = 65_536;
    internal static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static string LocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        if (OperatingSystem.IsWindows() && (full.StartsWith(@"\\", StringComparison.Ordinal) ||
            full[2..].Contains(':') || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network))
            throw new IOException("仅支持本机普通路径，不支持网络、设备路径或备用数据流。");
        return full;
    }

    internal static bool Within(string path, string root) => path.Equals(root, PathComparison) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, PathComparison);

    internal static bool ManagedPath(string path, string root)
    {
        if (Within(path, root)) return true;
        if (new[] { ".store.lock", ".session.lock", ".restore.json", ".parser.lock", ".model.lock" }.Any(s => path.Equals(root + s, PathComparison))) return true;
        foreach (var suffix in new[] { ".before-restore-", ".restore-", ".backup-" })
        {
            var prefix = root + suffix;
            if (!path.StartsWith(prefix, PathComparison)) continue;
            var tail = path[prefix.Length..].Split(Path.DirectorySeparatorChar)[0];
            if (Guid.TryParseExact(tail, "N", out _)) return true;
        }
        return false;
    }

    internal static void NoReparse(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                // One attribute read avoids Exists/GetAttributes races as SQLite creates/removes WAL/SHM files.
                // GetAttributes also detects dangling links, unlike Exists.
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("拒绝符号链接或目录联接：" + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static FileStream OpenRead(string path)
    {
        NoReparse(path);
        var attrs = File.GetAttributes(path);
        if ((attrs & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("只能归档普通文件。");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            // Validate the opened handle as well as the path: a renamed junction cannot redirect a read outside the selected root.
            if (OperatingSystem.IsWindows())
            {
                var resolved = new StringBuilder(32_768);
                var length = GetFinalPathNameByHandle(stream.SafeFileHandle, resolved, (uint)resolved.Capacity, 0);
                if (length == 0 || length >= resolved.Capacity) throw new IOException("无法核实文件真实路径。");
                var actual = resolved.ToString();
                if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
                if (!LocalPath(actual).Equals(path, PathComparison)) throw new IOException("文件真实路径与所选路径不同。");
            }
            NoReparse(path);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    internal static async Task<long> CopyAsync(Stream input, Stream output, long maximum, CancellationToken token)
    {
        var buffer = new byte[64 * 1024]; long copied = 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var count = await input.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) return copied;
            copied += count;
            if (copied > maximum) throw new IOException("文件或仓库超过归档大小上限。");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
        }
    }

    internal static void Name(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 240 || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains('/') || name.Contains('\\') || name.Contains(':') || name.EndsWith('.') || name.EndsWith(' '))
            throw new ArgumentException("需要普通文件名，不能包含目录或设备路径。", nameof(name));
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}
