namespace Amifolio.Core;

public sealed partial class PortfolioStore
{
    private async Task<ImportResult> AnalyzeArchivedAsync(ImportResult result,CancellationToken ct)
    {
        var tasks=new TaskStore(this);
        try
        {
            var pending=await tasks.PendingExtractionAsync(result.Artifact.Id,CancellationToken.None).ConfigureAwait(false);
            if(pending.Count>0)await ProcessTaskAsync(pending[0].Id,ct).ConfigureAwait(false);
            var current=await GetAsync(result.Artifact.Id,CancellationToken.None).ConfigureAwait(false);
            var job=pending.Count>0?await tasks.GetAsync(pending[0].Id,CancellationToken.None).ConfigureAwait(false):null;
            return result with{Artifact=current??result.Artifact,Warnings=job?.State is "failed" or "cancelled"?[..result.Warnings,"原件已保存；"+job.ErrorLabel]:result.Warnings};
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException)
        {
            return result with{Warnings=[..result.Warnings,"原件已保存；可在任务中查看提取状态。"]};
        }
    }

    public async Task ProcessTaskAsync(string id,CancellationToken ct=default)
    {
        var tasks=new TaskStore(this);var item=await tasks.GetAsync(id,ct).ConfigureAwait(false)??throw new KeyNotFoundException("任务不存在。");
        if(item.Kind!="extract")throw new InvalidOperationException("模型任务需在预览并确认后重新发起。");
            await using var slot=await StorageLease.ResourceAsync(DataRoot,"parser",ct).ConfigureAwait(false);
            using var lease=await tasks.TryClaimAsync(id,ct).ConfigureAwait(false);if(lease is null)return;
            lease.WatchCancellation();using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lease.Cancellation);
            try
            {
                var artifact=await GetAsync(item.ArtifactId!,linked.Token).ConfigureAwait(false)??throw new IOException("sources_changed");
                if(artifact.IsReference&&ReadOnlySources)await ValidateReferenceScopeAsync(artifact,linked.Token).ConfigureAwait(false);
                using var sourceGuard=artifact.IsReference?ArchiveSafety.OpenRead(artifact.ObjectPath):null;
                if(sourceGuard is not null&&(sourceGuard.Length!=artifact.Bytes||!Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(sourceGuard)).Equals(item.InputHash,StringComparison.OrdinalIgnoreCase)))throw new IOException("sources_changed");
                StorageEvents.Log.Checkpoint("extract");
                var result=await new ExtractionProcess().RunAsync(artifact,linked.Token).ConfigureAwait(false);
                await tasks.CompleteExtractionAsync(lease,result,CancellationToken.None).ConfigureAwait(false);
            }
            catch(OperationCanceledException){await tasks.CancelOwnedAsync(lease,CancellationToken.None).ConfigureAwait(false);}
            catch(Exception e)when(e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {var code=e is IOException&&(e.Message.StartsWith("worker_",StringComparison.Ordinal)||e.Message=="sources_changed")?e.Message:"io_error";await tasks.FailAsync(lease,code,CancellationToken.None).ConfigureAwait(false);}
    }

    /// <summary>One finite startup drain; no polling or provider requests.</summary>
    public async Task ResumePendingAsync(CancellationToken ct=default)
    {
        var tasks=new TaskStore(this);await tasks.RecoverAsync(ct).ConfigureAwait(false);
        while(!ct.IsCancellationRequested)
        {
            var pending=await tasks.PendingExtractionAsync(null,ct).ConfigureAwait(false);if(pending.Count==0)return;
            foreach(var item in pending){ct.ThrowIfCancellationRequested();await ProcessTaskAsync(item.Id,ct).ConfigureAwait(false);}
        }
    }
}
