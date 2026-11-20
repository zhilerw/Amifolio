using System.Diagnostics;

namespace Amifolio.Core;

/// <summary>Short lived cross-process leases. Sidecars stay outside the swappable data directory.</summary>
public static class StorageLease
{
    public static Task<FileStream> SharedAsync(string root, CancellationToken ct = default) => AcquireAsync(root, false, ct);
    public static Task<FileStream> ExclusiveAsync(string root, CancellationToken ct = default) => AcquireAsync(root, true, ct);
    public static Task<FileStream> SessionAsync(string root, CancellationToken ct = default) => OpenAsync(ArchiveSafety.LocalPath(root) + ".session.lock", false, ct);
    internal static Task<FileStream> ResourceAsync(string root,string resource,CancellationToken ct)=>OpenAsync(ArchiveSafety.LocalPath(root)+"."+(resource=="model"?"model":"parser")+".lock",true,ct,600);
    internal static Task<FileStream> RestoreSessionAsync(string root, CancellationToken ct) => OpenAsync(root + ".session.lock", true, ct, 2);

    private static async Task<FileStream> AcquireAsync(string root, bool exclusive, CancellationToken ct)
    {
        root = ArchiveSafety.LocalPath(root);
        while (true)
        {
            var lease = await OpenAsync(root + ".store.lock", exclusive, ct).ConfigureAwait(false);
            if (exclusive)
            {
                try { PortfolioMaintenance.RecoverInterruptedRestore(root); RevocationJournal.Replay(root); return lease; }
                catch { lease.Dispose(); throw; }
            }
            if (!File.Exists(root + ".restore.json") && !File.Exists(Path.Combine(root, ".revocations.pending"))) return lease;
            lease.Dispose();
            await using var repair = await AcquireAsync(root, true, ct).ConfigureAwait(false);
        }
    }

    private static async Task<FileStream> OpenAsync(string path, bool exclusive, CancellationToken ct, int seconds = 30)
    {
        ArchiveSafety.NoReparse(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var timer = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested(); ArchiveSafety.NoReparse(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, exclusive ? FileShare.None : FileShare.ReadWrite); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33 or 11)
            {
                if (timer.Elapsed.TotalSeconds >= seconds) throw new IOException("作品库正在使用，请关闭相关应用或稍后重试。", e);
                await Task.Delay(30, ct).ConfigureAwait(false);
            }
        }
    }
}

internal static class StorageFiles
{
    internal static void Write(string path, byte[] bytes)
    {
        ArchiveSafety.NoReparse(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){stream.Write(bytes);stream.Flush(true);}
            ArchiveSafety.NoReparse(path);File.Move(temp,path,true);
        }
        finally { if(File.Exists(temp))File.Delete(temp); }
    }
    internal static void DeleteTree(string path)
    {
        ArchiveSafety.NoReparse(path);if(!Directory.Exists(path))return;
        foreach(var file in Directory.GetFiles(path)){ArchiveSafety.NoReparse(file);File.SetAttributes(file,FileAttributes.Normal);File.Delete(file);}
        foreach(var dir in Directory.GetDirectories(path))DeleteTree(dir);
        Directory.Delete(path);
    }
}
