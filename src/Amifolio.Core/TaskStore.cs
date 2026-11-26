using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Amifolio.Core;

/// <summary>Durable state only. No payloads, provider credentials or unbounded automatic retries.</summary>
public sealed class TaskStore
{
    private readonly PortfolioStore store;
    public string DataRoot=>store.DataRoot;
    public TaskStore(string root){store=new(root);}
    internal TaskStore(PortfolioStore store){this.store=store;}
    private const string Columns="id,kind,artifact_id,project_id,input_hash,input_revision,state,attempts,max_attempts,error_code,created_at,updated_at";

    public Task<IReadOnlyList<WorkItem>> ListAsync(string? state=null,CancellationToken ct=default)=>store.DbAsync<IReadOnlyList<WorkItem>>(db=>{
        using var cmd=Command(db,$"SELECT {Columns} FROM work_items WHERE ($state IS NULL OR state=$state) ORDER BY created_at DESC,id LIMIT 200",("$state",state));
        using var r=cmd.ExecuteReader();var items=new List<WorkItem>();while(r.Read())items.Add(Read(r));return items;
    },ct);
    public Task<WorkItem?> GetAsync(string id,CancellationToken ct=default)=>store.DbAsync(db=>Find(db,id),ct);
    internal Task<IReadOnlyList<WorkItem>> PendingExtractionAsync(string? artifact,CancellationToken ct)=>store.DbAsync<IReadOnlyList<WorkItem>>(db=>{
        using var cmd=Command(db,$"SELECT {Columns} FROM work_items WHERE kind='extract' AND state='queued' AND ($artifact IS NULL OR artifact_id=$artifact) ORDER BY created_at,id LIMIT 200",("$artifact",artifact));
        using var r=cmd.ExecuteReader();var result=new List<WorkItem>();while(r.Read())result.Add(Read(r));return result;
    },ct);
    public Task<FileStream> ModelSlotAsync(CancellationToken ct=default)=>StorageLease.ResourceAsync(DataRoot,"model",ct);
    public Task ReconcileCompletedGenerationAsync(string id,CancellationToken ct=default)=>store.WriteAsync(db=>{using var tx=db.BeginTransaction(deferred:false);var task=Find(db,id,tx);if(task?.Kind=="generate"&&task.State!="completed")SetState(db,tx,id,"completed",null);tx.Commit();return true;},ct);
    public Task<IReadOnlyList<WorkItem>> GenerationRecoveryAsync(CancellationToken ct=default)=>store.DbAsync<IReadOnlyList<WorkItem>>(db=>{using var c=Command(db,$"SELECT {Columns} FROM work_items WHERE kind='generate' AND state<>'completed'");using var r=c.ExecuteReader();var result=new List<WorkItem>();while(r.Read())result.Add(Read(r));return result;},ct);
    public Task<bool?> RemoteAllowedAsync(CancellationToken ct=default)=>store.DbAsync<bool?>(db=>{using var c=Command(db,"SELECT value FROM store_meta WHERE key='remote_allowed'");return c.ExecuteScalar() is string value?value=="1":null;},ct);
    public Task SetRemoteAllowedAsync(bool allowed,CancellationToken ct=default)=>store.WriteAsync(db=>{
        using var tx=db.BeginTransaction(deferred:false);using var c=Command(db,"INSERT INTO store_meta(key,value) VALUES('remote_allowed',$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value",("$value",allowed?"1":"0"));c.Transaction=tx;c.ExecuteNonQuery();
        if(!allowed){c.Parameters.Clear();c.CommandText="UPDATE work_items SET state='cancelled',error_code='permission_revoked',owner=NULL,updated_at=$date WHERE kind='generate' AND state IN ('queued','running','interrupted')";c.Parameters.AddWithValue("$date",Now());c.ExecuteNonQuery();}tx.Commit();return true;
    },ct);
    public Task CancelGenerationScopeAsync(string? projectId,CancellationToken ct=default)=>store.WriteAsync(db=>{using var tx=db.BeginTransaction(deferred:false);CancelScope(db,tx,projectId);tx.Commit();return true;},ct);

    public Task<string> EnqueueExtractionAsync(string artifactId,CancellationToken ct=default)=>store.WriteAsync(db=>{
        using var tx=db.BeginTransaction(deferred:false);var artifact=ReadArtifactInput(db,tx,artifactId)??throw new KeyNotFoundException("作品不存在或已移除。");
        using var existing=Command(db,"SELECT id FROM work_items WHERE artifact_id=$id AND kind='extract' AND state IN ('queued','running') AND project_id IS $project AND input_hash=$hash LIMIT 1",("$id",artifactId),("$project",artifact.Project),("$hash",artifact.Hash));existing.Transaction=tx;
        if(existing.ExecuteScalar() is string found)return found;
        var id=Insert(db,tx,"extract",artifactId,artifact.Project,artifact.Hash,artifact.Version);
        using var update=Command(db,"UPDATE artifacts SET extraction_status='待提取' WHERE id=$id",("$id",artifactId));update.Transaction=tx;update.ExecuteNonQuery();tx.Commit();return id;
    },ct);

    public Task<string> EnqueueGenerationAsync(string? projectId,string fingerprint,long revision,CancellationToken ct=default)
    {
        if(fingerprint.Length!=64 || fingerprint.Any(c=>!char.IsAsciiHexDigit(c)) || revision<0)throw new ArgumentException("Invalid generation input identity.");
        return store.WriteAsync(db=>{using var tx=db.BeginTransaction(deferred:false);if(projectId is not null){using var project=Command(db,"SELECT COUNT(*) FROM projects WHERE id=$id",("$id",projectId));project.Transaction=tx;if(Convert.ToInt32(project.ExecuteScalar())!=1)throw new KeyNotFoundException("项目不存在。");}
            var id=Insert(db,tx,"generate",null,projectId,fingerprint,revision);tx.Commit();return id;},ct);
    }

    public Task<WorkLease> CreateGenerationLeaseAsync(string? projectId,string fingerprint,long revision,CancellationToken ct=default)
        =>ReserveGenerationAsync(null,projectId,fingerprint,revision,ct);
    public Task<WorkLease> ReserveGenerationRetryAsync(string id,CancellationToken ct=default)
        =>ReserveGenerationAsync(id,null,null,0,ct);
    private async Task<WorkLease> ReserveGenerationAsync(string? retry,string? project,string? fingerprint,long revision,CancellationToken ct)
    {
        var session=await StorageLease.SessionAsync(DataRoot,ct).ConfigureAwait(false);FileStream? file=null;
        try
        {
            await store.InitializeAsync(ct).ConfigureAwait(false);var id=retry??Guid.NewGuid().ToString("N");file=TryLock(id)??throw new InvalidOperationException("任务已由其他进程处理。");var owner=Guid.NewGuid().ToString("N");
            var item=await store.WriteAsync(db=>{
                using var tx=db.BeginTransaction(deferred:false);CheckRemote(db,tx);
                if(retry is null)
                {
                    if(fingerprint is null||fingerprint.Length!=64||fingerprint.Any(c=>!char.IsAsciiHexDigit(c))||revision<0)throw new ArgumentException("Invalid generation identity.");
                    if(project is not null){using var p=Command(db,"SELECT COUNT(*) FROM projects WHERE id=$id",("$id",project));p.Transaction=tx;if(Convert.ToInt32(p.ExecuteScalar())!=1)throw new KeyNotFoundException("项目不存在。");}
                    Insert(db,tx,"generate",null,project,fingerprint,revision,id);
                }
                else{var old=Find(db,id,tx)??throw new KeyNotFoundException("任务不存在。");if(old.Kind!="generate"||old.State is not("failed" or "cancelled" or "interrupted"))throw new InvalidOperationException("此任务无需重试。");}
                using var c=Command(db,"UPDATE work_items SET state='queued',owner=$owner,error_code=NULL,max_attempts=attempts+2,updated_at=$date WHERE id=$id",("$owner",owner),("$date",Now()),("$id",id));c.Transaction=tx;c.ExecuteNonQuery();var current=Find(db,id,tx)!;tx.Commit();return current;
            },ct).ConfigureAwait(false);
            return new(this,item,owner,session,file);
        }
        catch{file?.Dispose();session.Dispose();throw;}
    }
    public Task<bool> StartGenerationAsync(WorkLease lease,CancellationToken ct=default)=>store.WriteAsync(db=>{
        using var tx=db.BeginTransaction(deferred:false);CheckRemote(db,tx);if(lease.Item.Kind!="generate"||!Owns(db,tx,lease))return false;
        using var c=Command(db,"UPDATE work_items SET state='running',attempts=attempts+1,updated_at=$date WHERE id=$id AND state='queued'",("$date",Now()),("$id",lease.Item.Id));c.Transaction=tx;var changed=c.ExecuteNonQuery()==1;tx.Commit();return changed;
    },ct);
    public Task<bool> CanDispatchAsync(WorkLease lease,CancellationToken ct=default)=>store.DbAsync(db=>{CheckRemote(db,null);return Owns(db,null,lease);},ct);

    public async Task<WorkLease?> TryClaimAsync(string id,CancellationToken ct=default)
    {
        await using var guard=await StorageLease.SessionAsync(DataRoot,ct).ConfigureAwait(false);
        await store.InitializeAsync(ct).ConfigureAwait(false);var file=TryLock(id);if(file is null)return null;
        FileStream? session=null;
        try
        {
            session=await StorageLease.SessionAsync(DataRoot,ct).ConfigureAwait(false);var owner=Guid.NewGuid().ToString("N");
            var item=await store.WriteAsync(db=>{
                using var tx=db.BeginTransaction(deferred:false);var old=Find(db,id,tx);
                if(old is null || old.State!="queued" || old.Attempts>=old.MaxAttempts)return null;
                if(old.Kind=="extract"&&!InputExists(db,tx,old)){SetState(db,tx,id,"cancelled","sources_changed");tx.Commit();return null;}
                using var command=Command(db,"UPDATE work_items SET state='running',attempts=attempts+1,owner=$owner,error_code=NULL,updated_at=$date WHERE id=$id AND state='queued'",("$owner",owner),("$date",Now()),("$id",id));command.Transaction=tx;command.ExecuteNonQuery();var current=Find(db,id,tx);tx.Commit();return current;
            },ct).ConfigureAwait(false);
            if(item is null){session.Dispose();file.Dispose();return null;}
            return new WorkLease(this,item,owner,session,file);
        }
        catch{session?.Dispose();file.Dispose();throw;}
    }

    public async Task CancelAsync(string id,CancellationToken ct=default)
    {
        await store.WriteAsync(db=>{using var tx=db.BeginTransaction(deferred:false);var item=Find(db,id,tx)??throw new KeyNotFoundException("任务不存在。");if(item.State is not("completed" or "cancelled")){SetState(db,tx,id,"cancelled","cancelled");UpdateExtractionStatus(db,tx,item,"已取消");}tx.Commit();return true;},ct).ConfigureAwait(false);
        WorkLease.CancelLocal(DataRoot,id);
    }

    public Task RetryAsync(string id,CancellationToken ct=default)=>store.WriteAsync(db=>{
        using var tx=db.BeginTransaction(deferred:false);var item=Find(db,id,tx)??throw new KeyNotFoundException("任务不存在。");
        if(item.State is not("failed" or "cancelled" or "interrupted"))throw new InvalidOperationException("此任务无需重试。");
        if(item.Kind=="extract"&&!InputExists(db,tx,item))throw new InvalidOperationException("来源已变化，请重新提取。");
        using var command=Command(db,"UPDATE work_items SET state='queued',error_code=NULL,owner=NULL,max_attempts=attempts+2,updated_at=$date WHERE id=$id",("$date",Now()),("$id",id));command.Transaction=tx;command.ExecuteNonQuery();UpdateExtractionStatus(db,tx,item,"待提取");tx.Commit();return true;
    },ct);

    public async Task RecoverAsync(CancellationToken ct=default)
    {
        var running=await store.DbAsync<IReadOnlyList<WorkItem>>(db=>{using var c=Command(db,$"SELECT {Columns} FROM work_items WHERE state='running' OR (kind='generate' AND state='queued')");using var r=c.ExecuteReader();var items=new List<WorkItem>();while(r.Read())items.Add(Read(r));return items;},ct).ConfigureAwait(false);
        foreach(var item in running)
        {
            ct.ThrowIfCancellationRequested();using var file=TryLock(item.Id);if(file is null)continue;
            await store.WriteAsync(db=>{using var tx=db.BeginTransaction(deferred:false);var current=Find(db,item.Id,tx);if(current is not null&&(current.State=="running"||current.Kind=="generate"&&current.State=="queued"))
                {SetState(db,tx,item.Id,current.Kind=="generate"?"interrupted":current.Attempts<current.MaxAttempts?"queued":"failed",current.Kind=="generate"?"interrupted":current.Attempts<current.MaxAttempts?null:"attempt_limit");if(current.Kind=="extract"&&current.Attempts>=current.MaxAttempts)UpdateExtractionStatus(db,tx,current,"提取失败");}tx.Commit();return true;},ct).ConfigureAwait(false);
        }
    }

    public Task<bool> IsOwnerAsync(WorkLease lease,CancellationToken ct=default)=>store.DbAsync(db=>Owns(db,null,lease),ct);
    public Task<bool> CancelOwnedAsync(WorkLease lease,CancellationToken ct=default)=>FinishAsync(lease,"cancelled","cancelled",ct);
    public Task<bool> CompleteAsync(WorkLease lease,CancellationToken ct=default)=>FinishAsync(lease,"completed",null,ct);
    public Task<bool> FailAsync(WorkLease lease,string error,CancellationToken ct=default)=>FinishAsync(lease,"failed",ErrorCode(error),ct);
    private Task<bool> FinishAsync(WorkLease lease,string state,string? error,CancellationToken ct)=>store.WriteAsync(db=>{
        using var tx=db.BeginTransaction(deferred:false);if(!Owns(db,tx,lease))return false;
        if(state=="completed"&&lease.Item.Kind=="generate")StorageEvents.Log.Checkpoint("generation-complete");
        SetState(db,tx,lease.Item.Id,state,error);if(state=="failed")UpdateExtractionStatus(db,tx,lease.Item,"提取失败");if(state=="cancelled")UpdateExtractionStatus(db,tx,lease.Item,"已取消");tx.Commit();return true;
    },ct);

    internal Task<bool> CompleteExtractionAsync(WorkLease lease,Extraction result,CancellationToken ct)=>store.WriteAsync(db=>{
        using var tx=db.BeginTransaction(deferred:false);if(!Owns(db,tx,lease)||!InputExists(db,tx,lease.Item))return false;
        using var command=Command(db,"UPDATE artifacts SET kind=$kind,extraction_status=$status,preview=$preview WHERE id=$id AND deleted=0",("$kind",result.Kind),("$status",result.Status),("$preview",result.Preview),("$id",lease.Item.ArtifactId));command.Transaction=tx;command.ExecuteNonQuery();
        var failed=result.Status.StartsWith("提取失败",StringComparison.Ordinal)||result.Status.StartsWith("提取受限",StringComparison.Ordinal);
        SetState(db,tx,lease.Item.Id,failed?"failed":"completed",failed?"processing_failed":null);tx.Commit();return true;
    },ct);

    internal static string Insert(SqliteConnection db,SqliteTransaction tx,string kind,string? artifact,string? project,string hash,long revision,string? requestedId=null)
    {
        var id=requestedId??Guid.NewGuid().ToString("N");var now=Now();using var cmd=Command(db,"INSERT INTO work_items(id,kind,artifact_id,project_id,input_hash,input_revision,state,created_at,updated_at) VALUES($id,$kind,$artifact,$project,$hash,$revision,'queued',$now,$now)",("$id",id),("$kind",kind),("$artifact",artifact),("$project",project),("$hash",hash),("$revision",revision),("$now",now));cmd.Transaction=tx;cmd.ExecuteNonQuery();return id;
    }
    internal static void CancelSource(SqliteConnection db,SqliteTransaction tx,string artifact)
    {using var c=Command(db,"UPDATE work_items SET state='cancelled',error_code='sources_changed',owner=NULL,updated_at=$date WHERE artifact_id=$id AND state IN ('queued','running','interrupted')",("$id",artifact),("$date",Now()));c.Transaction=tx;c.ExecuteNonQuery();}
    internal static void CancelScope(SqliteConnection db,SqliteTransaction tx,string? project)
    {using var c=Command(db,"UPDATE work_items SET state='cancelled',error_code='sources_changed',owner=NULL,updated_at=$date WHERE kind='generate' AND project_id IS $project AND state IN ('queued','running','interrupted')",("$project",project),("$date",Now()));c.Transaction=tx;c.ExecuteNonQuery();}
    internal static void SetState(SqliteConnection db,SqliteTransaction tx,string id,string state,string? error)
    {using var c=Command(db,"UPDATE work_items SET state=$state,error_code=$error,owner=NULL,updated_at=$date WHERE id=$id",("$state",state),("$error",error),("$date",Now()),("$id",id));c.Transaction=tx;c.ExecuteNonQuery();}
    private static void UpdateExtractionStatus(SqliteConnection db,SqliteTransaction tx,WorkItem item,string status)
    {if(item.Kind!="extract")return;using var c=Command(db,"UPDATE artifacts SET extraction_status=$status WHERE id=$id AND hash=$hash AND project_id IS $project AND deleted=0",("$id",item.ArtifactId),("$hash",item.InputHash),("$project",item.ProjectId),("$status",status));c.Transaction=tx;c.ExecuteNonQuery();}
    private static bool Owns(SqliteConnection db,SqliteTransaction? tx,WorkLease lease)
    {using var c=Command(db,"SELECT COUNT(*) FROM work_items WHERE id=$id AND owner=$owner AND state IN ('queued','running')",("$id",lease.Item.Id),("$owner",lease.Owner));c.Transaction=tx;return Convert.ToInt32(c.ExecuteScalar())==1;}
    private static void CheckRemote(SqliteConnection db,SqliteTransaction? tx){using var c=Command(db,"SELECT value FROM store_meta WHERE key='remote_allowed'");c.Transaction=tx;if(c.ExecuteScalar() as string=="0")throw new InvalidOperationException("远程发送已关闭。");}
    private static bool InputExists(SqliteConnection db,SqliteTransaction tx,WorkItem item)
    {using var c=Command(db,"SELECT COUNT(*) FROM artifacts WHERE id=$id AND deleted=0 AND hash=$hash AND version=$version AND project_id IS $project",("$id",item.ArtifactId),("$hash",item.InputHash),("$version",item.InputRevision),("$project",item.ProjectId));c.Transaction=tx;return Convert.ToInt32(c.ExecuteScalar())==1;}
    private static (string Hash,long Version,string? Project)? ReadArtifactInput(SqliteConnection db,SqliteTransaction tx,string id)
    {using var c=Command(db,"SELECT hash,version,project_id FROM artifacts WHERE id=$id AND deleted=0",("$id",id));c.Transaction=tx;using var r=c.ExecuteReader();return r.Read()?(r.GetString(0),r.GetInt64(1),r.IsDBNull(2)?null:r.GetString(2)):null;}
    private static WorkItem? Find(SqliteConnection db,string id,SqliteTransaction? tx=null)
    {using var c=Command(db,$"SELECT {Columns} FROM work_items WHERE id=$id",("$id",id));c.Transaction=tx;using var r=c.ExecuteReader();return r.Read()?Read(r):null;}
    private FileStream? TryLock(string id)
    {
        if(!Guid.TryParseExact(id,"N",out _))throw new ArgumentException("任务 ID 无效。");
        var path=Path.Combine(DataRoot,".work-locks",id+".lock");ArchiveSafety.NoReparse(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try{return new(path,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}catch(IOException e)when((e.HResult&0xffff)is 32 or 33 or 11){return null;}
    }
    private static WorkItem Read(SqliteDataReader r)=>new(r.GetString(0),r.GetString(1),r.IsDBNull(2)?null:r.GetString(2),r.IsDBNull(3)?null:r.GetString(3),r.GetString(4),r.GetInt64(5),r.GetString(6),r.GetInt32(7),r.GetInt32(8),r.IsDBNull(9)?null:r.GetString(9),DateTimeOffset.Parse(r.GetString(10),CultureInfo.InvariantCulture),DateTimeOffset.Parse(r.GetString(11),CultureInfo.InvariantCulture));
    private static SqliteCommand Command(SqliteConnection db,string text,params (string,object?)[] values){var c=db.CreateCommand();c.CommandText=text;foreach(var (name,value)in values)c.Parameters.AddWithValue(name,value??DBNull.Value);return c;}
    private static string Now()=>DateTimeOffset.UtcNow.ToString("O",CultureInfo.InvariantCulture);
    private static string ErrorCode(string value)=>value is "worker_timeout" or "worker_crash" or "worker_protocol" or "worker_unavailable" or "worker_limits" or "provider_http" or "provider_timeout" or "provider_invalid" or "permission_revoked" or "sources_changed" or "io_error" or "interrupted"?value:"processing_failed";
}
