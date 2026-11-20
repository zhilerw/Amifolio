using Amifolio.Core;

namespace Amifolio.Context;

public sealed partial class ContextService
{
    public Task GenerateAsync(string? projectId,ModelOptions options,string apiKey,CancellationToken cancellationToken=default)
        =>LegacyRetired?Task.FromException(new InvalidOperationException("旧版文件生成已停用，请使用会话学习。")):GenerateTrackedAsync(projectId,options,apiKey,null,cancellationToken);

    /// <summary>Caller must show a fresh preview and obtain explicit retry consent.</summary>
    public async Task RetryGenerationAsync(string taskId,ModelOptions options,string apiKey,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();ThrowIfLegacyRetired();
        var tasks=new TaskStore(store.DataRoot);await tasks.RecoverAsync(ct).ConfigureAwait(false);
        await ReconcileGenerationTasksAsync(ct).ConfigureAwait(false);
        var task=await tasks.GetAsync(taskId,ct).ConfigureAwait(false)??throw new KeyNotFoundException("任务不存在。");
        if(task.Kind!="generate")throw new InvalidOperationException("此任务不是模型请求。");
        if(task.State=="completed")return;
        if(task.State is not("interrupted" or "failed" or "cancelled"))throw new InvalidOperationException("任务尚在等待或处理中。");
        await GenerateTrackedAsync(task.ProjectId,options,apiKey,task,ct).ConfigureAwait(false);
    }

    private async Task GenerateTrackedAsync(string? projectId,ModelOptions options,string apiKey,WorkItem? retry,CancellationToken ct,AutomaticAttempt? automatic=null,bool modelSlotHeld=false)
    {
        ct.ThrowIfCancellationRequested();ThrowIfLegacyRetired();
        if(store is not PortfolioStore){await GenerateCoreAsync(projectId,options,apiKey,ct,automatic:automatic).ConfigureAwait(false);return;}
        ArgumentNullException.ThrowIfNull(options);if(!options.RemoteAllowed)throw new InvalidOperationException("请先允许发送资料片段。");
        _=Endpoint(options.Endpoint);ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        if(options.Model.Length>256||apiKey.Any(char.IsControl))throw new ArgumentException("模型或密钥无效。");
        await VerifyGenerationSourcesAsync(projectId,ct).ConfigureAwait(false);
        await using var session=await StorageLease.SessionAsync(store.DataRoot,ct).ConfigureAwait(false);
        var scope=Scope(projectId);GenerationInput input;long revision;
        await using(var guard=await LockScopeAsync(scope.Lock,ct).ConfigureAwait(false))
        {
            var state=await LoadAsync(scope,ct).ConfigureAwait(false);await ReconcileAsync(scope,state,ct).ConfigureAwait(false);
            CheckAutomaticAttempt(state,automatic);
            input=await InputAsync(projectId,ct).ConfigureAwait(false);revision=state.Revision;
            if(automatic is not null&&automatic.Fingerprint!=input.Fingerprint)throw new InvalidOperationException("自动生成来源已变化。");
            if(input.SourceIds.Count==0)throw new InvalidOperationException("此作用域没有可用作品。");
            if(retry is not null&&(retry.InputHash!=input.Fingerprint||retry.InputRevision!=revision))throw new InvalidOperationException("来源或许可已变化，请重新预览后生成。");
        }
        var tasks=new TaskStore(store.DataRoot);
        WorkLease? lease=null;
        try
        {
            lease=retry is null?await tasks.CreateGenerationLeaseAsync(projectId,input.Fingerprint,revision,ct).ConfigureAwait(false):await tasks.ReserveGenerationRetryAsync(retry.Id,ct).ConfigureAwait(false);
            lease.WatchCancellation();using var linked=CancellationTokenSource.CreateLinkedTokenSource(ct,lease.Cancellation);
            await using var slot=modelSlotHeld?null:await tasks.ModelSlotAsync(linked.Token).ConfigureAwait(false);
            // Retirement may have happened while this request waited for the model slot.
            ThrowIfLegacyRetired();
            if(!await tasks.StartGenerationAsync(lease,linked.Token).ConfigureAwait(false))throw new OperationCanceledException("任务已取消。",linked.Token);
            await GenerateCoreAsync(projectId,options,apiKey,linked.Token,lease,tasks,automatic).ConfigureAwait(false);
            if(!await tasks.CompleteAsync(lease,CancellationToken.None).ConfigureAwait(false)&&!await ReconcileReceiptAsync(lease.Task,CancellationToken.None).ConfigureAwait(false))throw new OperationCanceledException("任务已取消。",linked.Token);
        }
        catch(OperationCanceledException)when(ct.IsCancellationRequested||lease?.Cancellation.IsCancellationRequested==true)
        {
            if(lease is not null){if(await ReconcileReceiptAsync(lease.Task,CancellationToken.None).ConfigureAwait(false))return;await tasks.CancelOwnedAsync(lease,CancellationToken.None).ConfigureAwait(false);}throw;
        }
        catch(Exception e)
        {
            if(lease is not null)
            {
                if(await ReconcileReceiptAsync(lease.Task,CancellationToken.None).ConfigureAwait(false))return;
                await tasks.FailAsync(lease,e switch{OperationCanceledException=>"provider_timeout",HttpRequestException=>"provider_http",InvalidDataException=>"provider_invalid",InvalidOperationException=>"sources_changed",IOException=>"io_error",_=>"processing_failed"},CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally{lease?.Dispose();}
    }

    /// <summary>Reconcile saved completion receipts without any provider request.</summary>
    public async Task ReconcileGenerationTasksAsync(CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();if(LegacyRetired)return;
        if(store is not PortfolioStore)return;var tasks=new TaskStore(store.DataRoot);
        foreach(var task in await tasks.GenerationRecoveryAsync(ct).ConfigureAwait(false))await ReconcileReceiptAsync(task,ct).ConfigureAwait(false);
    }
    private async Task<bool> ReconcileReceiptAsync(WorkItem task,CancellationToken ct)
    {
        if(LegacyRetired)return false;
        var scope=Scope(task.ProjectId);bool completed;
        await using(var guard=await LockScopeAsync(scope.Lock,ct).ConfigureAwait(false))
        {var state=await LoadAsync(scope,ct).ConfigureAwait(false);completed=state.CompletedTaskIds.Contains(task.Id,StringComparer.Ordinal);}
        if(completed)await new TaskStore(store.DataRoot).ReconcileCompletedGenerationAsync(task.Id,ct).ConfigureAwait(false);
        return completed;
    }
}
