using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Amifolio.Context;

internal static class NativeMemoryFiles
{
    private const int MaxLocalPathLength = 4096;
    private const FileAttributes DisallowedAttributes = FileAttributes.ReparsePoint | FileAttributes.Device;
    private const uint ListDirectoryAndReadAttributes = 0x81;
    private const uint OpenDirectoryWithoutFollowingFinalLink = 0x02200000;
    private const int ErrorNoMoreFiles = 18;
    private const int FileFullDirectoryInfo = 14;
    private const int FileFullDirectoryRestartInfo = 15;
    private const int DirectoryEntryHeaderBytes = 68;
    private const int DirectoryEntryNameLengthOffset = 60;
    internal static StringComparer PathComparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static string? NormalizeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxLocalPathLength || path.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(path)) return null;

        try
        {
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (OperatingSystem.IsWindows() && (full.StartsWith(@"\\", StringComparison.Ordinal) || full.AsSpan(2).Contains(':')))
                return null;
            return full;
        }
        catch (Exception ex) when (IsPathFailure(ex)) { return null; }
    }

    internal static bool IsDeviceName(string name)
    {
        var dot = name.IndexOf('.');
        var stem = (dot < 0 ? name : name[..dot]).ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                (stem[3] is >= '0' and <= '9' or '¹' or '²' or '³'));
    }

    internal static NativeMemoryReadStatus ProbeHome(string home)
    {
        // This Windows service verifies the opened file's identity with the filesystem.
        if (!OperatingSystem.IsWindows()) return NativeMemoryReadStatus.Unreadable;
        try
        {
            if (new DriveInfo(Path.GetPathRoot(home)!).DriveType == DriveType.Network) return NativeMemoryReadStatus.Untrusted;
            return Probe(home, directory: true);
        }
        catch (Exception ex) when (IsPathFailure(ex)) { return StatusOf(ex); }
    }

    internal static NativeMemoryReadStatus Probe(string path, bool directory)
    {
        var ancestors = new Stack<string>();
        for (var current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current))
            ancestors.Push(current);

        try
        {
            foreach (var ancestor in ancestors)
                if (!IsOrdinary(File.GetAttributes(ancestor), directory: true)) return NativeMemoryReadStatus.Untrusted;
            return IsOrdinary(File.GetAttributes(path), directory) ? NativeMemoryReadStatus.Available : NativeMemoryReadStatus.Untrusted;
        }
        catch (Exception ex) when (IsPathFailure(ex)) { return StatusOf(ex); }
    }

    private static bool IsOrdinary(FileAttributes attributes, bool directory) => (attributes & DisallowedAttributes) == 0 &&
        ((attributes & FileAttributes.Directory) != 0) == directory;

    internal static bool IsPathFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or
        NotSupportedException or SecurityException;

    internal static NativeMemoryReadStatus StatusOf(Exception ex) => ex is FileNotFoundException or DirectoryNotFoundException
        ? NativeMemoryReadStatus.Missing : NativeMemoryReadStatus.Unreadable;

    internal static NativeMemorySource Unavailable(string path, NativeMemorySourceKind kind, NativeMemoryReadStatus status) =>
        new(kind, path, status, null, null, false, NativeMemoryProjectMatch.Unknown);

    internal static SafeFileHandle? OpenDirectory(string path, out NativeMemoryReadStatus status)
    {
        status = Probe(path, directory: true);
        if (status != NativeMemoryReadStatus.Available) return null;

        // Omit delete sharing so the enumerated directory stays open at its name during source reads.
        var handle = CreateFile(@"\\?\" + path, ListDirectoryAndReadAttributes, FileShare.ReadWrite, IntPtr.Zero,
            FileMode.Open, OpenDirectoryWithoutFollowingFinalLink, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            status = error is 2 or 3 ? NativeMemoryReadStatus.Missing : NativeMemoryReadStatus.Unreadable;
            return null;
        }
        if (IsVerifiedDirectory(handle, path)) return handle;
        handle.Dispose();
        status = NativeMemoryReadStatus.Untrusted;
        return null;
    }

    internal static bool IsVerifiedDirectory(SafeFileHandle handle, string expected) =>
        GetFileInformationByHandle(handle, out var info) && IsOrdinary(info.Attributes, directory: true) && IsExpectedPath(handle, expected);

    internal static IEnumerable<string> DirectoryNames(SafeFileHandle handle, CancellationToken token)
    {
        var buffer = new byte[16 * 1024];
        var informationClass = FileFullDirectoryRestartInfo;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            if (!GetFileInformationByHandleEx(handle, informationClass, buffer, (uint)buffer.Length))
            {
                if (Marshal.GetLastPInvokeError() == ErrorNoMoreFiles) yield break;
                throw new IOException("Could not enumerate the verified memory directory.");
            }
            informationClass = FileFullDirectoryInfo;
            var offset = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var remaining = buffer.Length - offset;
                if (remaining < DirectoryEntryHeaderBytes) throw new IOException("Invalid directory entry header.");
                var next = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset));
                var nameLength = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + DirectoryEntryNameLengthOffset));
                if (nameLength == 0 || (nameLength & 1) != 0 || nameLength > remaining - DirectoryEntryHeaderBytes ||
                    (next != 0 && (next < DirectoryEntryHeaderBytes + nameLength || next > remaining - DirectoryEntryHeaderBytes)))
                    throw new IOException("Invalid directory entry bounds.");
                var name = Encoding.Unicode.GetString(buffer, offset + DirectoryEntryHeaderBytes, (int)nameLength);
                if (name is not ("." or "..")) yield return name;
                if (next == 0) break;
                offset += (int)next;
            }
        }
    }

    internal static async Task<NativeMemorySource> ReadAsync(string path, NativeMemorySourceKind kind, int limit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var status = Probe(path, directory: false);
        if (status != NativeMemoryReadStatus.Available) return Unavailable(path, kind, status);

        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (!IsVerifiedFile(stream.SafeFileHandle, path)) return Unavailable(path, kind, NativeMemoryReadStatus.Untrusted);

            var bytes = new byte[(int)Math.Min((long)limit + 1, stream.Length + 1)];
            var count = await stream.ReadAtLeastAsync(bytes, bytes.Length, false, token).ConfigureAwait(false);
            var modified = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
            if (!IsVerifiedFile(stream.SafeFileHandle, path)) return Unavailable(path, kind, NativeMemoryReadStatus.Untrusted);
            status = Probe(path, directory: false);
            if (status != NativeMemoryReadStatus.Available) return Unavailable(path, kind, status);

            var truncated = count > limit;
            var prefix = bytes.AsSpan(0, Math.Min(count, limit));
            if (prefix.StartsWith(Encoding.UTF8.Preamble)) prefix = prefix[Encoding.UTF8.Preamble.Length..];
            var chars = new char[prefix.Length];
            // An incomplete final UTF-8 character can be left out of a bounded preview.
            var charsRead = SafeFiles.Utf8.GetDecoder().GetChars(prefix, chars, flush: !truncated);
            return new(kind, path, NativeMemoryReadStatus.Available, new string(chars, 0, charsRead), modified,
                truncated, NativeMemoryProjectMatch.Unknown);
        }
        catch (Exception ex) when (IsPathFailure(ex)) { return Unavailable(path, kind, StatusOf(ex)); }
    }

    private static bool IsVerifiedFile(SafeFileHandle handle, string expected)
    {
        // A hard link can name data outside the memory store without being a reparse point.
        if (!GetFileInformationByHandle(handle, out var info) || info.LinkCount != 1 || !IsOrdinary(info.Attributes, directory: false))
            return false;

        return IsExpectedPath(handle, expected);
    }

    private static bool IsExpectedPath(SafeFileHandle handle, string expected)
    {
        // Reject a redirected parent or file even if it was swapped after the attribute check.
        var buffer = new StringBuilder(MaxLocalPathLength + 5);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return false;
        var actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        return PathComparer.Equals(NormalizeLocalPath(actual), expected);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        internal FileAttributes Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Created;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Accessed;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Modified;
        internal uint VolumeSerialNumber;
        internal uint SizeHigh;
        internal uint SizeLow;
        internal uint LinkCount;
        internal uint IndexHigh;
        internal uint IndexLow;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, FileShare share, IntPtr security,
        FileMode creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass, [Out] byte[] buffer, uint size);
}
