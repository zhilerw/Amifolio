using System.Text;

namespace Amifolio.Core;

public sealed partial class LearningStore
{
    public Task<LearningJob> OwnedJobAsync(string id,string token,CancellationToken ct=default)=>UseAsync(db=>
    {
        Text(id,128);Text(token,128);var job=LoadJob(db,id)??throw new InvalidOperationException("整合任务不存在。");
        if(job.Token!=token)throw new InvalidOperationException("任务租约不匹配。");return job;
    },ct);
    public Task<LearningJob?> ClaimAsync(string projectId,string mode,bool force=false,CancellationToken ct=default) =>
        ClaimBatchAsync(projectId,mode,force,null,null,ct);

    public Task<LearningJob?> ClaimExplicitTurnAsync(string projectId,long receipt,long policyRevision,CancellationToken ct=default) =>
        ClaimBatchAsync(projectId,"chat",true,receipt,policyRevision,ct);

    private Task<LearningJob?> ClaimBatchAsync(string projectId,string mode,bool force,long? receipt,long? revision,CancellationToken ct)=>UseAsync<LearningJob?>(db=>
    {
        var key=Key(projectId);RecoverJobs(db);var policy=Policy(db,key);
        if(!policy.Capture||!policy.Learn||policy.Mode!=mode||mode=="background"&&!policy.Remote)return null;
        if(Convert.ToInt32(db.Scalar("SELECT COUNT(*) FROM jobs WHERE scope=$scope AND state='running'",("$scope",key)))>0)return null;
        if(revision is not null&&policy.Revision!=revision)return null;
        var summaries=Rows<LearningSummary>(db,"receipts",
            "scope=$scope AND eligible=1 AND processed=0 AND job_id IS NULL AND ($seq IS NULL OR seq=$seq) ORDER BY seq LIMIT 20",
            ("$scope",key),("$seq",receipt));
        if(summaries.Count==0)return null;
        if(!force&&summaries.Count<policy.BatchSize&&Now-summaries[0].RecordedAt<TimeSpan.FromHours(24))return null;
        if(!force&&mode=="background")
        {
            var latest=Rows<LearningSummary>(db,"receipts","scope=$scope ORDER BY seq DESC LIMIT 1",("$scope",key)).FirstOrDefault();
            if(latest is not null&&Now-latest.RecordedAt<TimeSpan.FromSeconds(120))return null;
        }
        var batch=new List<LearningSummary>();var evidence=new List<LearningEvidence>();var bytes=0;
        foreach(var summary in summaries)
        {
            if(batch.Count>=policy.BatchSize)break;
            var ev=summary.EvidenceIds.Select(id=>One<LearningEvidence>(db,"SELECT payload FROM evidence WHERE id=$id AND valid=1",("$id",id))).OfType<LearningEvidence>().ToArray();
            var size=LearningPayloadBytes(summary,ev);
            if(size>LearningPayloadLimit)
            {
                // Legacy oversized records fail individually; do not create/retry a model job
                // or leave this input in front of every later valid receipt.
                db.Exec("UPDATE receipts SET processed=-1,job_id=NULL,payload=$json WHERE seq=$seq AND processed=0",
                    ("$json",Serialize(summary with{State="failed"})),("$seq",summary.Sequence));
                continue;
            }
            if(bytes+size>LearningPayloadLimit)break;
            batch.Add(summary);evidence.AddRange(ev);bytes+=size;
        }
        if(batch.Count==0)return null;
        var global=Policy(db,"global");
        var job=new LearningJob(Guid.NewGuid().ToString("N"),key,mode,Guid.NewGuid().ToString("N"),policy.Revision,
            policy.ContributeGlobal?global.Revision:0,"running",Now.AddMinutes(5),Hash(Serialize(batch)),batch,evidence.DistinctBy(e=>e.Evidence.Id).ToArray());
        SaveJob(db,job);
        foreach(var summary in batch)db.Exec("UPDATE receipts SET job_id=$job WHERE seq=$seq AND processed=0",("$job",job.Id),("$seq",summary.Sequence));
        return job;
    },ct);

    public Task<LearningCommitReceipt> CompleteAsync(string jobId,string token,IReadOnlyList<MemoryProposal> proposals,
        IReadOnlySet<string> liveEvidence,CancellationToken ct=default,IReadOnlyDictionary<string,MemoryEvidence>? sourceContexts=null)
    {
        Text(jobId,128);Text(token,128);if(proposals.Count>24)throw new ArgumentException("画像更新数量超过24项。");
        foreach(var p in proposals){if(p is null||p.Condition is null||p.EvidenceIds is null)throw new ArgumentException("条目字段缺失。");Text(p.Kind,24);Text(p.Dimension,32);Text(p.Scope,16);Text(p.Text,1000);Text(p.Value,256);SafeText(p.Text);SafeText(p.Value);if(p.Condition.Length>128||p.EvidenceIds.Count>24)throw new ArgumentException("条件或证据数量超限。");}
        var resultHash=Hash(Serialize(proposals));
        return UseAsync(db=>
        {
            RecoverJobs(db);var job=LoadJob(db,jobId)??throw new InvalidOperationException("整合任务不存在。");
            if(job.Token!=token)throw new InvalidOperationException("任务租约不匹配。");
            if(job.State=="completed")
            {
                var saved=One<SavedCommit>(db,"SELECT result FROM jobs WHERE id=$id",("$id",jobId));
                if(saved is null||saved.Hash!=resultHash)throw new InvalidOperationException("已提交任务不能改写结果。");
                return saved.Receipt;
            }
            var policy=Policy(db,job.Scope);var global=Policy(db,"global");
            if(job.State!="running"||Now>=job.LeaseUntil||!policy.Capture||!policy.Learn||policy.Revision!=job.PolicyRevision||policy.Mode!=job.Mode||
                job.GlobalRevision!=0&&global.Revision!=job.GlobalRevision)throw new InvalidOperationException("模式、授权或租约已变化，迟到结果未提交。");
            var current=Rows<LearningSummary>(db,"receipts","job_id=$job AND processed=0 ORDER BY seq",("$job",job.Id));
            if(Hash(Serialize(current))!=job.Fingerprint)throw new InvalidOperationException("输入记录已修订，未提交结果。");
            foreach(var item in job.Evidence)
                if(!liveEvidence.Contains(item.Evidence.Id)||One<LearningEvidence>(db,"SELECT payload FROM evidence WHERE id=$id AND valid=1",("$id",item.Evidence.Id))!=item)
                    throw new InvalidOperationException("用户证据已失效，未提交结果。");
            var active=0;var candidates=0;
            var allowed=new HashSet<string>(StringComparer.Ordinal);
            using(var command=db.Command("SELECT id,payload FROM policies WHERE id<>'global'"))
            using(var reader=command.ExecuteReader())
                while(reader.Read())
                {
                    var p=System.Text.Json.JsonSerializer.Deserialize<LearningPolicy>(reader.GetString(1),Json)!;
                    if(p.Capture&&p.Learn&&p.ContributeGlobal)allowed.Add(reader.GetString(0));
                }
            MemoryEvidence Admission(LearningEvidence row)
            {
                if(sourceContexts is not null&&sourceContexts.TryGetValue(row.Evidence.Id,out var full)&&full.Id==row.Evidence.Id&&full.SessionId==row.Evidence.SessionId&&full.TurnId==row.Evidence.TurnId&&full.ProjectId==row.Evidence.ProjectId)
                    return full;
                return row.Evidence with{Verified=row.Evidence.Verified&&row.SourceContextComplete};
            }
            foreach(var untrusted in proposals)
            {
                var proposal=untrusted with{Kind=untrusted.Kind.Trim().ToLowerInvariant(),Dimension=untrusted.Dimension.Trim().ToLowerInvariant(),Scope=untrusted.Scope.Trim().ToLowerInvariant()};
                // At least one fresh input must support each update. Old model output is never evidence.
                var fresh=job.Evidence.Where(e=>proposal.EvidenceIds.Contains(e.Evidence.Id,StringComparer.Ordinal)).ToArray();
                if(fresh.Length==0){candidates++;continue;}
                var target=proposal.Scope=="global"?"global":job.Scope;
                if(target=="global"&&(!policy.ContributeGlobal||!global.Learn)){candidates++;continue;}
                var applicable=Rows<LearningEvidence>(db,"evidence",target=="global"?"valid=1 ORDER BY rowid DESC LIMIT 128":"scope=$scope AND valid=1 ORDER BY rowid DESC LIMIT 128",target=="global"?[]:[("$scope",job.Scope)]);
                var prelimRows=fresh.Concat(applicable).DistinctBy(e=>e.Evidence.Id).Take(128).ToArray();
                var prelim=MemoryPolicy.Evaluate(proposal,prelimRows.Select(Admission).ToArray(),allowed);
                if(prelim.State=="blocked")continue;
                var id=Hash(target+"\n"+proposal.Kind+"\n"+prelim.Dimension+"\n"+prelim.NormalizedValue+"\n"+MemoryPolicy.Normalize(proposal.Condition));
                var old=One<LearningEntry>(db,"SELECT payload FROM profiles WHERE id=$id",("$id",id));
                if(old?.Manual==true||old?.State=="deleted")continue;
                var combined=fresh.Concat(old is null?[]:EntryEvidence(db,old)).DistinctBy(e=>e.Evidence.Id).Take(48).ToArray();
                if(combined.Any(e=>!liveEvidence.Contains(e.Evidence.Id)))throw new InvalidOperationException("旧支持证据未经本次核验，未提交结果。");
                var merged=proposal with{EvidenceIds=combined.Select(e=>e.Evidence.Id).ToArray()};
                var evaluated=combined.Concat(applicable).DistinctBy(e=>e.Evidence.Id).Take(128).ToArray();
                var decision=MemoryPolicy.Evaluate(merged,evaluated.Select(Admission).ToArray(),allowed);
                var suppression=Hash(target+"\n"+decision.Dimension);
                if(db.Scalar("SELECT id FROM suppressions WHERE id=$id",("$id",suppression)) is not null)continue;
                var state=decision.State;var reason=decision.Reason;
                if(!policy.AutoActivate||target=="global"&&!global.AutoActivate){state="candidate";reason="automatic_activation_off";}
                var peers=Rows<LearningEntry>(db,"profiles","scope=$scope AND json_extract(payload,'$.state')='active' AND json_extract(payload,'$.dimension')=$dim AND id<>$id",("$scope",target),("$dim",decision.Dimension),("$id",id)).ToArray();
                if(peers.Any(e=>e.Manual)){state="candidate";reason="manual_entry_has_priority";}
                if(proposal.Scope=="task"){state="candidate";reason="task_only";}
                var canonical=CanonicalPreference(decision.Dimension,decision.NormalizedValue);
                if(decision.Dimension=="project_fact"&&decision.Reason=="explicit_project_fact_quote")canonical=proposal.Text;
                if(state=="active"&&canonical is null){state="candidate";reason="unsupported_canonical_rendering";}
                // Supported values are rendered by the application. A model cannot attach
                // arbitrary invented prose to a genuine quote and thereby activate it.
                var entry=new LearningEntry(id,target,proposal.Kind,decision.Dimension,decision.NormalizedValue,canonical??proposal.Text,proposal.Condition,state,reason,false,(old?.Revision??0)+1,Now,combined.Select(e=>e.Evidence.Id).ToArray());
                if(reason=="contradictory_evidence")
                    foreach(var peer in peers.Where(e=>!e.Manual)){History(db,peer);PutEntry(db,peer with{State="disabled",Reason="conflicting_user_evidence",Revision=peer.Revision+1,UpdatedAt=Now});}
                if(old is not null)History(db,old);
                PutEntry(db,entry);
                if(state=="active")
                {
                    active++;
                    foreach(var peer in peers.Where(e=>!e.Manual&&e.Condition==entry.Condition))
                    {
                        var oldTime=EntryEvidence(db,peer).Select(e=>e.Evidence.ObservedAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
                        var newTime=combined.Select(e=>e.Evidence.ObservedAt).Max();
                        if(newTime>oldTime){History(db,peer);PutEntry(db,peer with{State="disabled",Reason="newer_user_evidence",Revision=peer.Revision+1,UpdatedAt=Now});}
                        else{PutEntry(db,entry with{State="candidate",Reason="conflicting_existing_evidence"});active--;candidates++;break;}
                    }
                }
                else candidates++;
            }
            // The conversational overview stays explicitly Agent-reported; it is never preference evidence.
            var summaryId=Hash(job.Scope+"\nconversation-summary");
            var existing=One<LearningEntry>(db,"SELECT payload FROM profiles WHERE id=$id",("$id",summaryId));
            if(existing?.Manual!=true&&existing?.State!="deleted")
            {
                if(existing is not null)History(db,existing);
                var text=string.Join("\n",job.Summaries.Select(s=>s.Summary));if(text.Length>6000)text=text[..6000];
                PutEntry(db,new(summaryId,job.Scope,"summary","task_status","reported",text,"","active","agent_report_only",false,(existing?.Revision??0)+1,Now,job.Evidence.Select(e=>e.Evidence.Id).ToArray()));
            }
            foreach(var item in current)db.Exec("UPDATE receipts SET processed=1 WHERE seq=$seq AND job_id=$job",("$seq",item.Sequence),("$job",job.Id));
            SaveJob(db,job with{State="completed"});
            var receipt=new LearningCommitReceipt(job.Id,"completed",Watermark(db,job.Scope),active,candidates);
            db.Exec("UPDATE jobs SET result=$result WHERE id=$id",("$result",Serialize(new SavedCommit(resultHash,receipt))),("$id",job.Id));
            return receipt;
        },ct);
    }

    public Task FailAsync(string jobId,string token,string code,CancellationToken ct=default)=>UseAsync(db=>
    {
        if(code is not("provider_failed" or "provider_timeout" or "invalid_output" or "interrupted" or "permission_changed" or "budget" or "cancelled"))code="processing_failed";
        var job=LoadJob(db,jobId);if(job is null||job.Token!=token||job.State!="running")return false;
        SaveJob(db,job with{State="failed",Error=code});
        db.Exec("UPDATE receipts SET processed=-1 WHERE job_id=$id AND processed=0",("$id",job.Id));return true;
    },ct);
    public Task DeferAsync(string jobId,string token,string code,CancellationToken ct=default)=>UseAsync(db=>
    {
        var job=LoadJob(db,jobId);if(job is null||job.Token!=token||job.State!="running")return false;
        SaveJob(db,job with{State="cancelled",Error=code=="budget"?code:"permission_changed"});
        db.Exec("UPDATE receipts SET processed=0,job_id=NULL WHERE job_id=$id AND processed=0",("$id",job.Id));return true;
    },ct);
    public Task RetryAsync(string jobId,CancellationToken ct=default)=>UseAsync(db=>
    {
        var job=LoadJob(db,jobId)??throw new InvalidOperationException("日志不存在。");
        if(job.State is not("failed" or "interrupted" or "cancelled"))throw new InvalidOperationException("此任务不能重试。");
        SaveJob(db,job with{State="cancelled",Error="explicit_retry"});
        db.Exec("UPDATE receipts SET processed=0,job_id=NULL WHERE job_id=$id AND processed<>1",("$id",job.Id));return true;
    },ct);
    private sealed record SavedCommit(string Hash,LearningCommitReceipt Receipt);
    private static string? CanonicalPreference(string dimension,string value)=>(dimension,value) switch
    {
        ("verbosity","brief")=>"偏好简洁回答",("verbosity","detailed")=>"偏好详细解释",
        ("tone","formal")=>"偏好正式语气",("tone","casual")=>"偏好轻松语气",
        ("typography","song")=>"偏好使用宋体",("color","turquoise")=>"偏好松石绿",("color","beige")=>"偏好米白色",
        ("language","chinese")=>"偏好使用中文",("language","english")=>"偏好使用英文",_=>null
    };
    private static LearningJob? LoadJob(Db db,string id)=>One<LearningJob>(db,"SELECT payload FROM jobs WHERE id=$id",("$id",id));
    private void SaveJob(Db db,LearningJob job)=>db.Exec("INSERT INTO jobs(id,scope,state,created,payload) VALUES($id,$scope,$state,$created,$json) ON CONFLICT(id) DO UPDATE SET state=$state,payload=$json",
        ("$id",job.Id),("$scope",job.Scope),("$state",job.State),("$created",Now.ToUnixTimeMilliseconds()),("$json",Serialize(job)));
    private void CancelJobs(Db db,string? scope,string reason)
    {
        foreach(var job in Rows<LearningJob>(db,"jobs",scope is null?"state='running'":"scope=$scope AND state='running'",scope is null?[]:[("$scope",scope)]))
        {
            SaveJob(db,job with{State="cancelled",Error=reason});
            db.Exec("UPDATE receipts SET processed=0,job_id=NULL WHERE job_id=$id AND processed=0",("$id",job.Id));
        }
    }
    private void RecoverJobs(Db db)
    {
        foreach(var job in Rows<LearningJob>(db,"jobs","state='running'"))
            if(Now>=job.LeaseUntil)
            {
                SaveJob(db,job with{State="interrupted",Error="lease_expired"});
                db.Exec("UPDATE receipts SET processed=-1 WHERE job_id=$id AND processed=0",("$id",job.Id));
            }
    }
    private void InvalidateEvidence(Db db,string id,string reason)
    {
        db.Exec("UPDATE evidence SET valid=0 WHERE id=$id",("$id",id));
        foreach(var entry in ProfileBatches(db,"json_extract(payload,'$.manual')=0"))
            if(!entry.Manual&&entry.EvidenceIds.Contains(id,StringComparer.Ordinal))
            {PutEntry(db,entry with{State="disabled",Reason=reason,Revision=entry.Revision+1,UpdatedAt=Now});WithdrawExports(entry.Scope);}
    }
    private static void History(Db db,LearningEntry entry)
    {
        db.Exec("INSERT INTO history(entry_id,payload) VALUES($id,$json)",("$id",entry.Id),("$json",Serialize(entry)));
    }
    private void WithdrawExports(string scope)
    {
        var directory=Path.GetDirectoryName(ProjectionPath(scope))!;ArchiveSafety.NoReparse(directory);
        if(Directory.Exists(directory))StorageFiles.DeleteTree(directory);
    }
}
