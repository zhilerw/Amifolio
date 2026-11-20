namespace Amifolio.Core;

public sealed partial class LearningStore
{
    public async Task CompactIfDueAsync(CancellationToken ct=default)
    {
        if(!HasState)return;
        var stamp=Path.Combine(root,"learning","maintenance.marker");ArchiveSafety.NoReparse(stamp);
        if(File.Exists(stamp)&&DateTime.UtcNow-File.GetLastWriteTimeUtc(stamp)<TimeSpan.FromDays(1))return;
        await Task.Run(()=>CompactAsync(ct),ct).ConfigureAwait(false);
        StorageFiles.Write(stamp,"retention-v1"u8.ToArray());
    }
}
