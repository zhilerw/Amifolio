using System.Collections.Concurrent;

namespace Amifolio.Core;

public sealed record WorkItem(string Id,string Kind,string? ArtifactId,string? ProjectId,string InputHash,long InputRevision,
    string State,int Attempts,int MaxAttempts,string? ErrorCode,DateTimeOffset CreatedAt,DateTimeOffset UpdatedAt)
{
    public string KindLabel=>Kind=="extract"?"内容提取":"生成候选";
    public string StateLabel=>State switch{"queued"=>Kind=="extract"?"待提取":"待生成","running"=>"处理中","completed"=>"完成","failed"=>"失败","cancelled"=>"已取消","interrupted"=>"已中断",_=>"未知"};
    public string ErrorLabel=>ErrorCode switch{
        null=>"","cancelled"=>"已取消，原件保留","sources_changed"=>"来源已变化，请重新发起",
        "attempt_limit"=>"恢复次数已用尽，可手动重试","interrupted"=>"请求可能已发送，请确认后重试",
        "worker_timeout"=>"提取超时，原件保留","worker_crash"=>"解析进程退出，原件保留",
        "worker_unavailable"=>"解析组件缺失，请检查运行目录","worker_limits"=>"无法应用解析资源限制",
        "worker_protocol"=>"解析结果无效","provider_http"=>"模型服务返回错误","provider_timeout"=>"模型服务超时",
        "provider_invalid"=>"模型结果格式无效","permission_revoked"=>"许可已关闭","io_error"=>"无法访问文件或磁盘",_=>"处理未完成"};
}

public sealed class WorkLease : IDisposable
{
    private static readonly ConcurrentDictionary<string,CancellationTokenSource> Active=new(StringComparer.OrdinalIgnoreCase);
    internal readonly string Owner;
    internal readonly WorkItem Item;
    private readonly TaskStore store;
    private readonly FileStream session,file;
    private readonly CancellationTokenSource cancellation=new();
    private readonly string key;
    private bool disposed;
    internal WorkLease(TaskStore store,WorkItem item,string owner,FileStream session,FileStream file)
    {this.store=store;Item=item;Owner=owner;this.session=session;this.file=file;key=store.DataRoot+"|"+item.Id;Active[key]=cancellation;}
    public WorkItem Task=>Item;
    public CancellationToken Cancellation=>cancellation.Token;
    public void WatchCancellation()=>_ = WatchAsync();
    private async Task WatchAsync()
    {
        try{while(!cancellation.IsCancellationRequested){await System.Threading.Tasks.Task.Delay(250,cancellation.Token).ConfigureAwait(false);if(!await store.IsOwnerAsync(this,cancellation.Token).ConfigureAwait(false)){cancellation.Cancel();break;}}}
        catch(OperationCanceledException){}
        catch(IOException){cancellation.Cancel();}
        catch(Microsoft.Data.Sqlite.SqliteException){cancellation.Cancel();}
    }
    internal static void CancelLocal(string root,string id){if(Active.TryGetValue(root+"|"+id,out var cancellation))try{cancellation.Cancel();}catch(ObjectDisposedException){}}
    public void Dispose(){if(disposed)return;disposed=true;Active.TryRemove(key,out _);cancellation.Cancel();file.Dispose();session.Dispose();}
}
