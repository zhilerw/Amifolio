using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Amifolio.Core;

internal sealed record SnapshotResult(string Preview, IReadOnlyList<string> Warnings);

internal static class RepositorySnapshot
{
    private static readonly HashSet<string> ExcludedNames = new(StringComparer.OrdinalIgnoreCase)
    { ".git", ".hg", ".svn", "node_modules", "bin", "obj", "venv", ".venv", "__pycache__", ".ssh", ".aws", ".azure", "secrets", ".secrets", "keys", ".keys", "__amifolio__" };
    private static readonly HashSet<string> SecretExtensions = new(StringComparer.OrdinalIgnoreCase)
    { ".pem", ".key", ".pfx", ".p12", ".p8", ".keystore", ".jks", ".kdbx" };
    private static readonly DateTimeOffset ZipTime = new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private sealed record Exclusion(string Path, string Reason);
    private sealed record IncludedFile(string Path, long Bytes, string Sha256);
    private sealed record Candidate(string FullPath, string RelativePath, long Bytes);

    internal static async Task<SnapshotResult> CreateAsync(string root, string dataRoot, string outputPath, CancellationToken token)
    {
        var excluded = new List<Exclusion>(); var files = new List<Candidate>(); var visited = 0; long total = 0;
        // Preview inventory is completed before any source file is copied. No ignore/config files or Git hooks are executed.
        void Scan(string directory, int depth)
        {
            token.ThrowIfCancellationRequested(); ArchiveSafety.NoReparse(directory);
            if (depth > ArchiveSafety.MaxDepth) throw new IOException("仓库目录超过 64 层上限。");
            // Enumerate incrementally; never materialize an unbounded directory just to sort it.
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested();
                if (++visited > ArchiveSafety.MaxRepositoryEntries) throw new IOException("仓库超过 20000 个扫描条目上限。");
                var full = ArchiveSafety.LocalPath(path); var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                if (!ArchiveSafety.Within(full, root)) throw new IOException("仓库条目越过选定根目录。");
                if (ArchiveSafety.ManagedPath(full, dataRoot)) { excluded.Add(new(relative, "作品仓库或恢复资料，防止递归归档")); continue; }
                var attrs = File.GetAttributes(full);
                if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) { excluded.Add(new(relative, "符号链接、目录联接或设备")); continue; }
                var name = Path.GetFileName(full);
                if (ExcludedNames.Contains(name) || IsSecret(name)) { excluded.Add(new(relative, "版本控制、依赖、构建产物、密钥或保留清单目录")); continue; }
                if ((attrs & FileAttributes.Directory) != 0) { Scan(full, depth + 1); continue; }
                ArchiveSafety.NoReparse(full); var bytes = new FileInfo(full).Length;
                if (bytes > ArchiveSafety.MaxFileBytes) throw new IOException("仓库内单个文件超过 256 MiB 上限。");
                total = checked(total + bytes);
                if (total > ArchiveSafety.MaxRepositoryBytes) throw new IOException("仓库文件合计超过 512 MiB 上限。");
                files.Add(new(full, relative, bytes));
            }
        }
        // Scanning itself can be slow on large trees and must not block the UI thread.
        await Task.Run(() => Scan(root, 0), token).ConfigureAwait(false);
        files.Sort((x, y) => StringComparer.Ordinal.Compare(x.RelativePath, y.RelativePath));
        excluded.Sort((x, y) => StringComparer.Ordinal.Compare(x.Path, y.Path));
        var included = new List<IncludedFile>(); long actualTotal = 0;
        await using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
        {
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var candidate in files)
                {
                    token.ThrowIfCancellationRequested();
                    if (!ArchiveSafety.Within(candidate.FullPath, root) || ArchiveSafety.ManagedPath(candidate.FullPath, dataRoot)) throw new IOException("仓库路径已改变。");
                    await using var input = ArchiveSafety.OpenRead(candidate.FullPath);
                    if (input.Length != candidate.Bytes) throw new IOException("仓库文件在预览后改变，请重试。");
                    var entry = zip.CreateEntry(candidate.RelativePath, CompressionLevel.Fastest); entry.LastWriteTime = ZipTime;
                    await using var destination = entry.Open();
                    // Hash the bytes actually written, not a separate unlocked second read of the source.
                    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[64 * 1024]; long copied = 0;
                    while (true)
                    {
                        token.ThrowIfCancellationRequested(); var read = await input.ReadAsync(buffer, token).ConfigureAwait(false); if (read == 0) break;
                        copied += read; actualTotal += read;
                        if (copied > ArchiveSafety.MaxFileBytes || actualTotal > ArchiveSafety.MaxRepositoryBytes) throw new IOException("仓库内容超过归档上限。");
                        hasher.AppendData(buffer, 0, read); await destination.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                    if (copied != candidate.Bytes) throw new IOException("仓库文件在快照中改变，请重试。");
                    included.Add(new(candidate.RelativePath, copied, Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant()));
                }
                var manifest = zip.CreateEntry("__amifolio__/manifest.json", CompressionLevel.Fastest); manifest.LastWriteTime = ZipTime;
                await using var manifestStream = manifest.Open();
                await JsonSerializer.SerializeAsync(manifestStream, new
                {
                    format = "amifolio-repository-snapshot-v1", execution = "none", coherentPointInTime = false,
                    files = included, exclusions = excluded, bytes = actualTotal,
                    limitations = "静态路径规则不能识别任意源码内嵌的秘密；逐文件快照不保证正在修改的仓库跨文件一致性。"
                }, cancellationToken: token).ConfigureAwait(false);
            }
            await output.FlushAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); output.Flush(flushToDisk: true);
        }
        var preview = new StringBuilder($"快照清单：{included.Count} 文件，{actualTotal} 字节；排除 {excluded.Count} 条目。\n");
        foreach (var file in included)
        {
            var line = $"{file.Path} ({file.Bytes} bytes)\n";
            if (preview.Length + line.Length > ArchiveSafety.PreviewChars - 80) { preview.Append("清单预览已截断；完整清单见 ZIP 的 __amifolio__/manifest.json。\n"); break; }
            preview.Append(line);
        }
        var warnings = excluded.Select(x => $"排除 {x.Path}：{x.Reason}").ToArray();
        return new(preview.ToString(), warnings);
    }

    internal static bool IsSecret(string name)
    {
        var n = name.ToLowerInvariant();
        return n.StartsWith(".env", StringComparison.Ordinal) || n is "id_rsa" or "id_dsa" or "id_ecdsa" or "id_ed25519" or ".npmrc" or ".pypirc" or ".netrc" or "credentials" or "credentials.json" or "secrets.json" or "secret.json" ||
            n.StartsWith("credentials.", StringComparison.Ordinal) || n.StartsWith("secrets.", StringComparison.Ordinal) || SecretExtensions.Contains(Path.GetExtension(name));
    }
}
