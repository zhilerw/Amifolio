using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Amifolio.Core;

public sealed partial class LearningStore(string dataRoot,TimeProvider? clock=null)
{
    private readonly string root=ArchiveSafety.LocalPath(dataRoot);
    private readonly TimeProvider time=clock??TimeProvider.System;
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){MaxDepth=24};
    private const int LearningPayloadLimit=24*1024;
    private volatile bool initialized;
    public string DataRoot=>root;
    public bool HasState{get{var path=Path.Combine(root,"learning","state.sqlite");ArchiveSafety.NoReparse(path);return File.Exists(path);}}
    public string ProjectionPath(string? scope)=>Path.Combine(root,"learning","shared",ProjectionKey(Key(scope)),"START.md");
    private static string ProjectionKey(string scope)=>scope=="global"?scope:"project-"+Hash(scope);
    public Task WithdrawSharingAsync(string? scope,CancellationToken ct=default)=>UseAsync(db=>
    {
        var key=Key(scope);var current=Policy(db,key);
        Put(db,"policies",key,current with{Share=false,Revision=current.Revision+1});CancelJobs(db,key,"sharing_revoked");WithdrawExports(key);return true;
    },ct);
    public Task<bool> HasReceiptAsync(string projectId,string sessionId,string turnId,CancellationToken ct=default)
    {
        var key=Key(projectId);Text(sessionId,128);Text(turnId,128);
        return UseAsync(db=>db.Scalar("SELECT id FROM receipts WHERE id=$id",("$id",Hash(key+"\n"+sessionId+"\n"+turnId))) is not null,ct);
    }
    public async Task<IReadOnlyList<CodexTurnIdentity>> MissingReceiptsAsync(string projectId,IReadOnlyList<CodexTurnIdentity> observed,CancellationToken ct=default)
    {
        if(observed.Count>256)throw new ArgumentException("回合索引超限。");var key=Key(projectId);
        return await UseAsync<IReadOnlyList<CodexTurnIdentity>>(db=>observed.Where(t=>db.Scalar("SELECT id FROM receipts WHERE id=$id",("$id",Hash(key+"\n"+t.SessionId+"\n"+t.TurnId))) is null).ToArray(),ct).ConfigureAwait(false);
    }
    public Task<LearningPolicy> PolicyAsync(string? scope,CancellationToken ct=default)=>UseAsync(db=>Policy(db,Key(scope)),ct);
    public Task<LearningPolicy> SetPolicyAsync(string? scope,LearningPolicy value,long expectedRevision=-1,CancellationToken ct=default)
    {
        var key=Key(scope);ValidatePolicy(value);
        return UseAsync(db=>
        {
            var old=Policy(db,key);
            if(expectedRevision>=0&&old.Revision!=expectedRevision)throw new InvalidOperationException("学习设置已变化，请刷新后重试。");
            var next=value with{Revision=old.Revision+1,GrantedAt=value.Capture?(old.Capture?old.GrantedAt:Now):old.GrantedAt};
            Put(db,"policies",key,next);
            CancelJobs(db,key,"permission_changed");
            if(key=="global")CancelJobs(db,null,"global_permission_changed");
            if(!next.Share)WithdrawExports(key);
            if(old.ContributeGlobal&&!next.ContributeGlobal)
            {
                foreach(var entry in ProfileBatches(db,"scope='global' AND json_extract(payload,'$.manual')=0"))
                    if(!entry.Manual&&EntryEvidence(db,entry).Any(e=>e.Evidence.ProjectId==key))
                        PutEntry(db,entry with{State="disabled",Reason="participation_withdrawn",Revision=entry.Revision+1,UpdatedAt=Now});
                WithdrawExports("global");
            }
            return next;
        },ct);
    }
    public Task<LearningReceipt> CaptureAsync(LearningCapture request,LearningVerifiedTurn? source,CancellationToken ct=default)
    {
        var key=Key(request.ProjectId);if(key=="global")throw new ArgumentException("会话记录必须绑定已发现的项目。");
        Text(request.SessionId,128);Text(request.TurnId,128);Text(request.Summary,1500);
        if(request.Outcome is not("completed" or "partial" or "failed" or "no_change")||request.Quotes.Count>4||request.Outputs.Count>12)throw new ArgumentException("记录格式或数量超限。");
        SafeText(request.Summary);
        foreach(var quote in request.Quotes){Text(quote,1000);SafeText(quote);}
        foreach(var output in request.Outputs)
        {
            Text(output.Name,128);Text(output.Purpose,300);SafeText(output.Name);SafeText(output.Purpose);
            if(output.Name!=Path.GetFileName(output.Name)||output.Name.Contains('/')||output.Name.Contains('\\'))throw new ArgumentException("产物仅记录文件名，不接收任意路径。");
            if(output.ReferenceId is not null)Text(output.ReferenceId,128);
        }
        return UseAsync(db=>
        {
            var policy=Policy(db,key);if(!policy.Capture)throw new InvalidOperationException("此项目尚未授权记录会话。");
            var verified=source is not null&&source.SessionId==request.SessionId&&source.TurnId==request.TurnId&&
                source.ObservedAt>=policy.GrantedAt&&source.ObservedAt<=Now.AddMinutes(2)&&!string.IsNullOrWhiteSpace(source.UserTextHash)&&!string.IsNullOrWhiteSpace(source.SourceFingerprint);
            var evidence=new List<string>();var verifiedEvidence=new List<LearningEvidence>();
            if(verified)
                foreach(var quote in request.Quotes.Distinct(StringComparer.Ordinal))
                {
                    if(!source!.UserText.Replace("\r\n","\n",StringComparison.Ordinal).Contains(quote.Replace("\r\n","\n",StringComparison.Ordinal),StringComparison.Ordinal))
                        throw new InvalidOperationException("引文与可核对的用户消息不符。");
                    var id=Hash(key+"\n"+request.SessionId+"\n"+request.TurnId+"\n"+source.UserTextHash+"\n"+quote);
                    var item=new LearningEvidence(new(id,quote,request.SessionId,request.TurnId,key,true,source.ObservedAt),source.UserTextHash,source.SourceFingerprint,
                        quote.Trim().Equals(source.UserText.Trim(),StringComparison.Ordinal));
                    verifiedEvidence.Add(item);evidence.Add(id);
                }
            var identity=Hash(key+"\n"+request.SessionId+"\n"+request.TurnId);
            var fingerprint=Hash(Serialize(request)+"\n"+(verified?source!.UserTextHash:"unverified"));
            var old=One<LearningSummary>(db,"SELECT payload FROM receipts WHERE id=$id",("$id",identity));
            var oldHash=db.Scalar("SELECT fingerprint FROM receipts WHERE id=$id",("$id",identity)) as string;
            if(old?.State=="deleted")throw new InvalidOperationException("此回合记录已由用户删除，不能由Agent恢复。");
            if(old is not null&&oldHash==fingerprint&&!(old.State=="pending_evidence"&&verified))return new LearningReceipt(old.Sequence,old.Version,old.State,Pending(db,key),true);
            if(old is null)EnsureHotRoom(db,"receipts");
            var state=verified?request.Outcome=="no_change"?"no_change":"verified":"pending_evidence";
            if(old is null)
                db.Exec("INSERT INTO receipts(id,scope,fingerprint,payload,eligible,processed) VALUES($id,$scope,$hash,'{}',$eligible,0)",
                    ("$id",identity),("$scope",key),("$hash",fingerprint),("$eligible",state=="verified"?1:0));
            var sequence=old?.Sequence??Convert.ToInt64(db.Scalar("SELECT seq FROM receipts WHERE id=$id",("$id",identity)));
            var summary=new LearningSummary(sequence,(old?.Version??0)+1,key,request.SessionId,request.TurnId,request.Summary,request.Outcome,state,Now,evidence,request.Outputs.ToArray());
            // Validate exactly the persisted summary and evidence representation used by Claim.
            // A rejection must precede lease cancellation, source invalidation and projection withdrawal.
            if(LearningPayloadBytes(summary,verifiedEvidence)>LearningPayloadLimit)throw new ArgumentException("单条记录的摘要、产物和引文序列化后超过24 KiB；请精简后提交或修订同一回合，原记录未更改。");
            if(old is not null)
            {
                Warm(db,"receipts",identity);
                CancelJobs(db,key,"record_revised");
                foreach(var id in old.EvidenceIds.Except(evidence,StringComparer.Ordinal))InvalidateEvidence(db,id,"source_revised");
            }
            foreach(var item in verifiedEvidence)PutEvidence(db,item);
            db.Exec("UPDATE receipts SET payload=$json,fingerprint=$hash,eligible=$eligible,processed=0,job_id=NULL WHERE id=$id",
                ("$json",Serialize(summary)),("$hash",fingerprint),("$eligible",state=="verified"?1:0),("$id",identity));
            db.Exec("INSERT OR IGNORE INTO receipt_sequence_origins(id,sequence) VALUES($id,$seq)",("$id",identity),("$seq",sequence));
            return new LearningReceipt(sequence,summary.Version,state,Pending(db,key),false);
        },ct);
    }
    public Task<LearningSnapshot> SnapshotAsync(string? scope,CancellationToken ct=default)=>UseAsync(db=>
    {
        var key=Key(scope);RecoverJobs(db);var policy=Policy(db,key);
        var summaries=Rows<LearningSummary>(db,"receipts","scope=$scope ORDER BY seq DESC LIMIT 50",("$scope",key));
        // Include the same bounded active set as Shared so callers can revalidate its sources.
        // Preserve chronological display order after reserving room for active entries.
        var entries=Rows<LearningEntry>(db,"profiles","id IN(SELECT id FROM profiles WHERE scope=$scope ORDER BY (json_extract(payload,'$.state')='active') DESC,updated DESC LIMIT 500) ORDER BY (json_extract(payload,'$.state')='active') DESC,updated DESC",("$scope",key))
            .OrderByDescending(e=>e.UpdatedAt).ToArray();
        var jobs=Rows<LearningJob>(db,"jobs","scope=$scope ORDER BY created DESC LIMIT 50",("$scope",key)).Select(j=>j with{Token=""}).ToArray();
        var pending=Pending(db,key);var waiting=Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM receipts WHERE scope=$scope AND eligible=0 AND processed=0",("$scope",key)));
        var failed=Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM receipts WHERE scope=$scope AND processed=-1",("$scope",key)));
        return new LearningSnapshot(policy,summaries,entries,jobs,pending,waiting,failed,Watermark(db,key),!policy.Capture?"disabled":failed>0?"retry_required":pending>=policy.BatchSize?policy.Mode=="chat"?"waiting_for_codex":"waiting_for_idle":waiting>0?"waiting_for_evidence":"waiting_for_turns");
    },ct);

    private DateTimeOffset Now=>time.GetUtcNow();
    private static string Key(string? scope)
    {
        var key=scope??"global";
        if(!Regex.IsMatch(key,"^[a-zA-Z0-9_-]{1,128}$",RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(50)))throw new ArgumentException("作用域无效。");
        return key;
    }
    private static void Text(string? value,int max){if(string.IsNullOrWhiteSpace(value)||value.Length>max||value.Contains('\0'))throw new ArgumentException("内容缺失或超限。");}
    private static void SafeText(string text){if(MemoryPolicy.ContainsSensitiveText(text))throw new ArgumentException("内容包含敏感信息或不允许的指令，未保存。");}
    private static string Hash(string value)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Serialize<T>(T value)=>JsonSerializer.Serialize(value,Json);
    private static int LearningPayloadBytes(LearningSummary summary,IReadOnlyList<LearningEvidence> evidence)
        =>Encoding.UTF8.GetByteCount(Serialize(summary))+Encoding.UTF8.GetByteCount(Serialize(evidence));
    private static T? One<T>(Db db,string sql,params (string,object?)[] args) where T:class
    {
        if(typeof(T)==typeof(LearningSummary))sql=sql.Replace("FROM receipts","FROM "+LearningColdStorage.Source("receipts"),StringComparison.Ordinal);
        if(typeof(T)==typeof(LearningEntry))sql=sql.Replace("FROM profiles","FROM "+LearningColdStorage.Source("profiles"),StringComparison.Ordinal);
        return db.Scalar(sql,args) is string json?JsonSerializer.Deserialize<T>(json,Json)??throw new IOException("学习数据无效。"):null;
    }
    private static List<T> Rows<T>(Db db,string table,string where,params (string,object?)[] args)
    {
        using var c=db.Command("SELECT payload FROM "+LearningColdStorage.Source(table)+" WHERE "+where,args);using var r=c.ExecuteReader();var result=new List<T>();
        while(r.Read())result.Add(JsonSerializer.Deserialize<T>(r.GetString(0),Json)??throw new IOException("学习数据无效。"));return result;
    }
    private static LearningPolicy Policy(Db db,string key)=>One<LearningPolicy>(db,"SELECT payload FROM policies WHERE id=$id",("$id",key))??new();
    private static IEnumerable<LearningEntry> ProfileBatches(Db db,string where,params (string,object?)[] args)
    {
        string? after=null;
        while(true)
        {
            db.Cancellation.ThrowIfCancellationRequested();
            var page=Rows<LearningEntry>(db,"profiles","("+where+") AND ($after IS NULL OR id>$after) ORDER BY id LIMIT 256",[..args,("$after",after)]);
            if(page.Count==0)yield break;foreach(var item in page)yield return item;after=page[^1].Id;
        }
    }
    private static void Put<T>(Db db,string table,string id,T value)=>db.Exec("INSERT INTO "+table+"(id,payload) VALUES($id,$json) ON CONFLICT(id) DO UPDATE SET payload=$json",("$id",id),("$json",Serialize(value)));
    private static void PutEvidence(Db db,LearningEvidence item)=>db.Exec("INSERT INTO evidence(id,scope,valid,payload) VALUES($id,$scope,1,$json) ON CONFLICT(id) DO UPDATE SET valid=1,payload=$json",
        ("$id",item.Evidence.Id),("$scope",item.Evidence.ProjectId),("$json",Serialize(item)));
    private static IReadOnlyList<LearningEvidence> EntryEvidence(Db db,LearningEntry entry)=>entry.EvidenceIds.Select(id=>One<LearningEvidence>(db,"SELECT payload FROM evidence WHERE id=$id AND valid=1",("$id",id))).OfType<LearningEvidence>().ToArray();
    private static int Pending(Db db,string key)=>Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM receipts WHERE scope=$scope AND eligible=1 AND processed=0",("$scope",key)));
    private static long Watermark(Db db,string key)
    {
        var completed=Convert.ToInt64(db.Scalar("SELECT COALESCE(MAX(seq),0) FROM receipts WHERE scope=$scope AND processed=1",("$scope",key)));
        db.Exec("INSERT INTO learning_watermarks(scope,sequence) VALUES($scope,$seq) ON CONFLICT(scope) DO UPDATE SET sequence=MAX(sequence,excluded.sequence)",("$scope",key),("$seq",completed));
        return Convert.ToInt64(db.Scalar("SELECT sequence FROM learning_watermarks WHERE scope=$scope",("$scope",key)));
    }
    private static void PutEntry(Db db,LearningEntry entry)
    {
        var existing=db.Scalar("SELECT payload FROM profiles WHERE id=$id",("$id",entry.Id)) as string;
        var cold=Cold(db,"profiles",entry.Id);var deleted=entry.State=="deleted";
        if(!deleted&&cold&&(entry.Manual||entry.State=="active")){Warm(db,"profiles",entry.Id);cold=false;}
        else if(!deleted&&!cold&&(existing is null||LearningColdStorage.Object(existing)["state"]?.GetValue<string>()=="deleted"))EnsureHotRoom(db,"profiles");
        var payload=Serialize(entry);
        if(deleted){db.Exec("DELETE FROM profile_cold WHERE id=$id",("$id",entry.Id));cold=false;}
        else if(cold)db.Exec("UPDATE profile_cold SET payload=$p WHERE id=$id",("$id",entry.Id),("$p",payload));
        db.Exec("INSERT INTO profiles(id,scope,updated,payload) VALUES($id,$scope,$updated,$json) ON CONFLICT(id) DO UPDATE SET updated=$updated,payload=$json",("$id",entry.Id),("$scope",entry.Scope),("$updated",entry.UpdatedAt.ToUnixTimeMilliseconds()),("$json",Serialize(entry)));
        if(cold)db.Exec("UPDATE profiles SET payload=$p WHERE id=$id",("$id",entry.Id),("$p",LearningColdStorage.Ledger("profiles",payload)));
    }
    private static void ValidatePolicy(LearningPolicy value)
    {
        if(value.Mode is not("chat" or "background")||value.BatchSize is <1 or >20)throw new ArgumentException("处理方式或回合间隔无效。");
        if(value.Remote)
        {
            if(!Uri.TryCreate(value.Endpoint,UriKind.Absolute,out var uri)||uri.Scheme!="https"&&!(uri.Scheme=="http"&&uri.IsLoopback)||uri.UserInfo.Length>0||uri.Query.Length>0||uri.Fragment.Length>0)throw new ArgumentException("远程服务须为无凭据的HTTPS地址，本机测试允许HTTP。");
        }
    }
    private async Task<T> UseAsync<T>(Func<Db,T> action,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        await using var lease=await StorageLease.SharedAsync(root,ct).ConfigureAwait(false);
        return await Task.Run(()=>
        {
            ct.ThrowIfCancellationRequested();var dir=Path.Combine(root,"learning");ArchiveSafety.NoReparse(dir);Directory.CreateDirectory(dir);
            var path=Path.Combine(dir,"state.sqlite");ArchiveSafety.NoReparse(path);
            var marker=Path.Combine(dir,"initialized.marker");ArchiveSafety.NoReparse(marker);
            if(!File.Exists(path)&&(initialized||File.Exists(marker)))throw new IOException("学习数据库缺失，未创建空库。");
            using var connection=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false,DefaultTimeout=5}.ToString());
            connection.Open();if(!initialized)
            {
                LearningSchema.Ensure(connection,path,ct);StorageFiles.Write(marker,"1"u8.ToArray());initialized=true;
            }
            using var transaction=connection.BeginTransaction(deferred:false);var db=new Db(connection,transaction,ct);
            // An already-running v1 writer can change a ledger after our initialization.
            // Hold the writer lock while validating and consuming both tiers, so a cold
            // body can never mask a later deletion, manual edit or source revision.
            LearningColdStorage.Validate(connection,transaction,ct);
            var result=action(db);ct.ThrowIfCancellationRequested();transaction.Commit();return result;
        },ct).ConfigureAwait(false);
    }
    private sealed class Db(SqliteConnection connection,SqliteTransaction transaction,CancellationToken cancellation)
    {
        internal SqliteConnection Connection=>connection;
        internal SqliteTransaction Transaction=>transaction;
        internal CancellationToken Cancellation=>cancellation;
        internal SqliteCommand Command(string sql,params (string,object?)[] values){var c=connection.CreateCommand();c.Transaction=transaction;c.CommandText=sql;foreach(var(k,v)in values)c.Parameters.AddWithValue(k,v??DBNull.Value);return c;}
        internal object? Scalar(string sql,params (string,object?)[] values){using var c=Command(sql,values);return c.ExecuteScalar();}
        internal int Exec(string sql,params (string,object?)[] values){using var c=Command(sql,values);return c.ExecuteNonQuery();}
    }
}
