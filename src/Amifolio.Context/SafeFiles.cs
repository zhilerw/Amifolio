using System.Security.Cryptography;
using System.Text;

namespace Amifolio.Context;

internal static class SafeFiles
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal static string Hash(string text) => Hash(Utf8.GetBytes(text));

    // Do not follow junctions/symlinks, including any ancestor of a user-supplied path.
    internal static void CheckPath(string path)
    {
        for (var part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
        {
            try
            {
                if ((File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Linked paths are not supported for managed context or AGENTS files.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    internal static async Task<FileStream> LockAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        CheckPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33 or 11)
            { await Task.Delay(30, ct).ConfigureAwait(false); }
        }
    }

    internal static async Task<byte[]> ReadBoundedAsync(string path, int maxBytes, CancellationToken ct)
    {
        CheckPath(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, true);
        return await ReadBoundedAsync(stream, maxBytes, ct).ConfigureAwait(false);
    }

    internal static async Task<byte[]> ReadBoundedAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maxBytes + 1 - (int)output.Length)), ct).ConfigureAwait(false)) != 0)
        {
            output.Write(buffer, 0, count);
            if (output.Length > maxBytes) throw new InvalidDataException("The document or response exceeds its size limit.");
        }
        return output.ToArray();
    }

    internal static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        CheckPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await WriteNewAsync(temp, bytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            CheckPath(path);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken ct)
    {
        CheckPath(path);
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(true);
    }

    internal static void DeleteTree(string path)
    {
        CheckPath(path);
        if (!Directory.Exists(path)) return;
        // Enumerate only one level at a time so a reparse point is rejected before traversal.
        foreach (var file in Directory.GetFiles(path)) { CheckPath(file); File.Delete(file); }
        foreach (var directory in Directory.GetDirectories(path)) DeleteTree(directory);
        Directory.Delete(path);
    }
}
