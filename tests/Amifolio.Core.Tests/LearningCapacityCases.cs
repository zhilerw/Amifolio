using System.Text.Json;
using Amifolio.Core;
using Microsoft.Data.Sqlite;

internal static class LearningCapacityCases
{
    internal static (string,Func<Fixture,Task>)[] All =>
    [
        ("learning hot capacity processed receipts make room without losing permanent identities",async f=>
        {
            var s=await Ready(f);SeedReceipts(f,10000,1);var added=await s.CaptureAsync(Request("new"),Proof("new"));
            Check(added.Sequence==10001,"sequence was recycled");using var db=Open(f);
            Check(Number(db,"SELECT COUNT(*) FROM receipts")==10001&&Number(db,"SELECT COUNT(*) FROM receipt_cold")>0,"bodies were not cold stored or identities lost");
            Check(Number(db,"SELECT COUNT(*) FROM receipts r WHERE NOT EXISTS(SELECT 1 FROM receipt_cold c WHERE c.id=r.id)")<=10000,"hot receipt cap exceeded");
        }),
        ("learning hot capacity deleted receipt identities do not consume hot slots",async f=>
        {
            var s=await Ready(f);SeedReceipts(f,10000,1,"deleted");var added=await s.CaptureAsync(Request("new"),Proof("new"));
            Check(added.Sequence==10001,"tombstones lost sequence fence");using var db=Open(f);
            Check(Number(db,"SELECT COUNT(*) FROM receipts WHERE json_extract(payload,'$.state')='deleted'")==10000,"deleted identities dropped");
        }),
        ("learning hot capacity automatic inactive profiles make room without deleting history",async f=>
        {
            var s=await Ready(f);await s.CaptureAsync(Request("new"),Proof("new"));var job=(await s.ClaimAsync("p","chat",true))!;
            SeedProfiles(f,2000,"candidate",false);await s.CompleteAsync(job.Id,job.Token,[],job.Evidence.Select(e=>e.Evidence.Id).ToHashSet());
            using var db=Open(f);Check(Number(db,"SELECT COUNT(*) FROM profiles")==2001&&Number(db,"SELECT COUNT(*) FROM profile_cold")>0,"profile identity or cold body lost");
        }),
        ("learning hot capacity pending receipts reject recoverably without evicting retry input",async f=>
        {
            var s=await Ready(f);SeedReceipts(f,10000,0);var error=await Rejected(()=>s.CaptureAsync(Request("new"),Proof("new")));
            Check(error.Contains("热",StringComparison.Ordinal)&&error.Contains("10000",StringComparison.Ordinal),"capacity refusal is not a hot/protected-data limit");
            using var db=Open(f);Check(Number(db,"SELECT COUNT(*) FROM receipts")==10000&&Number(db,"SELECT COUNT(*) FROM receipt_cold")==0,"protected inputs evicted");
        }),
        ("learning hot capacity active manual profiles reject without pretending deleted slots are full",async f=>
        {
            var s=await Ready(f);await s.CaptureAsync(Request("new"),Proof("new"));var job=(await s.ClaimAsync("p","chat",true))!;SeedProfiles(f,2000,"active",true);
            var error=await Rejected(()=>s.CompleteAsync(job.Id,job.Token,[],job.Evidence.Select(e=>e.Evidence.Id).ToHashSet()));
            Check(error.Contains("热",StringComparison.Ordinal)&&error.Contains("2000",StringComparison.Ordinal),"manual capacity refusal is not truthful");
            using var db=Open(f);Check(Number(db,"SELECT COUNT(*) FROM profiles")==2000&&Number(db,"SELECT COUNT(*) FROM profile_cold")==0,"manual/active records moved");
        }),
        ("learning cold batches preserve policies metadata watermark and page original bodies",async f=>
        {
            var s=await Ready(f);var policy=await s.PolicyAsync("p");SeedReceipts(f,9,1);SeedProfiles(f,4,"candidate",false);
            var result=await s.ArchiveColdAsync(3);var status=await s.CapacityAsync();
            Check(result==new LearningArchiveResult(3,3)&&status.HotReceipts==6&&status.ColdReceipts==3&&status.HotProfiles==1&&status.ColdProfiles==3,"batch/count mismatch");
            Check(await s.PolicyAsync("p")==policy&&(await s.SnapshotAsync("p")).Watermark==9,"archive changed authority or sequence watermarks");
            var seen=new List<long>();string? cursor=null;
            do{var page=await s.ReadReceiptHistoryAsync("p",cursor,2);seen.AddRange(page.Items.Select(x=>x.Summary.Sequence));Check(page.Items.All(x=>x.Summary.Summary=="合成冷藏正文"),"cold body unavailable");cursor=page.NextCursor;}while(cursor is not null);
            Check(seen.SequenceEqual(Enumerable.Range(1,9).Reverse().Select(x=>(long)x)),"keyset history lost/duplicated identities");
            var first=await s.ReadReceiptHistoryAsync("p",pageSize:2);await Argument(()=>s.ReadReceiptHistoryAsync("q",first.NextCursor,2));
        }),
        ("learning cold receipt replay stays cold and revision reuses identity sequence and fingerprint",async f=>
        {
            var s=await Ready(f);var original=await s.CaptureAsync(Request("one"),Proof("one"));var job=(await s.ClaimAsync("p","chat",true))!;await s.CompleteAsync(job.Id,job.Token,[],job.Evidence.Select(e=>e.Evidence.Id).ToHashSet());
            await s.ArchiveColdAsync();var before=await s.CapacityAsync();var replay=await s.CaptureAsync(Request("one"),Proof("one"));
            Check(replay.Duplicate&&replay.Sequence==original.Sequence&&(await s.CapacityAsync()).ColdReceipts==before.ColdReceipts,"replay changed cold identity/tier");
            var revised=await s.CaptureAsync(Request("one") with{Summary="修订摘要。"},Proof("one"));
            Check(revised.Sequence==original.Sequence&&revised.Version==2&&!revised.Duplicate&&(await s.SnapshotAsync("p")).Pending==1,"cold revision not requeued explicitly");
            using var db=Open(f);Check(Number(db,"SELECT COUNT(*) FROM receipts")==1&&Number(db,"SELECT COUNT(*) FROM receipt_cold")==0,"cold revision duplicated identity");
        }),
        ("learning cold source withdrawal updates cold body and retains provenance without automatic restore",async f=>
        {
            var s=await Ready(f);await s.CaptureAsync(Request("one"),Proof("one"));var job=(await s.ClaimAsync("p","chat",true))!;await s.CompleteAsync(job.Id,job.Token,[],job.Evidence.Select(e=>e.Evidence.Id).ToHashSet());await s.ArchiveColdAsync();
            await s.WithdrawEvidenceAsync(job.Evidence[0].Evidence.Id);var page=await s.ReadReceiptHistoryAsync("p");
            Check(page.Items.Single().IsCold&&page.Items[0].Summary.State=="pending_evidence"&&page.Items[0].Summary.Summary==Request("one").Summary,"cold withdrawal lost body or failed to invalidate state");
            Check(await s.ClaimAsync("p","chat",true) is null&&(await s.CapacityAsync()).HotReceipts==0,"withdrawal automatically reheated or enqueued work");
            await s.CompactAsync();using var db=Open(f);Check(Number(db,"SELECT COUNT(*) FROM evidence")==1,"GC lost cold provenance root");
        }),
        ("learning cold deleted summary scrubs cold bodies and job copies and blocks replay",async f=>
        {
            var s=await Ready(f);var request=Request("secret") with{Summary="PRIVATE_COLD_SUMMARY"};var receipt=await s.CaptureAsync(request,Proof("secret"));var job=(await s.ClaimAsync("p","chat",true))!;await s.CompleteAsync(job.Id,job.Token,[],job.Evidence.Select(e=>e.Evidence.Id).ToHashSet());await s.ArchiveColdAsync();
            await s.DeleteSummaryAsync(receipt.Sequence);await Rejected(()=>s.CaptureAsync(request,Proof("secret")));await Rejected(()=>s.RestoreReceiptAsync(receipt.Sequence));
            var page=await s.ReadReceiptHistoryAsync("p");Check(page.Items[0].Summary.State=="deleted"&&page.Items[0].Summary.Summary.Length==0,"deleted cold body returned");
            using var db=Open(f);Check(Number(db,"SELECT COUNT(*) FROM receipt_cold")==0&&Number(db,"SELECT COUNT(*) FROM jobs WHERE instr(payload,'PRIVATE_COLD_SUMMARY')>0")==0,"deleted content survived a cold/job copy");
        }),
        ("learning cold profile restoration is explicit and deletion purges cold and undo copies",async f=>
        {
            var s=await Ready(f);SeedProfiles(f,2,"candidate",false);await s.ArchiveColdAsync();
            var restored=await s.RestoreEntryAsync("seed-0");Check(restored.State=="candidate"&&!restored.Manual&&!(await s.PolicyAsync("p")).Share,"restore activated or granted sharing");
            await s.ArchiveColdAsync();var deleted=await s.SetEntryStateAsync("seed-0","deleted",1);await Rejected(()=>s.RestoreEntryAsync("seed-0"));
            using var db=Open(f);Check(deleted.Text.Length==0&&Number(db,"SELECT COUNT(*) FROM profile_cold WHERE id='seed-0'")==0&&Number(db,"SELECT COUNT(*) FROM history WHERE entry_id='seed-0'")==0,"profile deletion kept cold/undo body");
        }),
        ("learning cold body restore refuses a fully protected hot tier without losing the cold record",async f=>
        {
            var s=await Ready(f);SeedProfiles(f,1,"candidate",false);await s.ArchiveColdAsync();
            using(var db=Open(f))Exec(db,"UPDATE profiles SET id='archived' WHERE id='seed-0';UPDATE profile_cold SET id='archived',payload=json_set(payload,'$.id','archived');UPDATE profiles SET payload=json_set(payload,'$.id','archived') WHERE id='archived'");
            SeedProfiles(f,2000,"active",true);await Rejected(()=>s.RestoreEntryAsync("archived"));
            Check((await s.CapacityAsync()).ColdProfiles==1&&(await s.CapacityAsync()).HotProfiles==2000,"failed restore evicted protected data");
        }),
        ("learning cold archive cancellation rolls back all moved bodies and metadata",async f=>
        {
            var s=await Ready(f);SeedReceipts(f,10,1);using var cancel=new CancellationTokenSource();var prior=StorageFaults.Handler.Value;
            StorageFaults.Handler.Value=stage=>{if(stage=="learning-receipt-cold")cancel.Cancel();};
            try{try{await s.ArchiveColdAsync(10,cancel.Token);throw new Exception("cancel ignored");}catch(OperationCanceledException){}}
            finally{StorageFaults.Handler.Value=prior;}
            var stats=await s.CapacityAsync();Check(stats.HotReceipts==10&&stats.ColdReceipts==0,"cancel committed partial archive");
        }),
        ("learning cold schema v1 upgrade cancellation leaves recoverable v1 and retries transactionally",async f=>
        {
            await Ready(f);using(var db=Open(f))Exec(db,"DROP TABLE receipt_cold;DROP TABLE profile_cold;PRAGMA user_version=1");
            using var cancel=new CancellationTokenSource();var prior=StorageFaults.Handler.Value;StorageFaults.Handler.Value=stage=>{if(stage=="learning-schema-upgraded")cancel.Cancel();};
            try{try{await new LearningStore(f.DataRoot).PolicyAsync("p",cancel.Token);throw new Exception("upgrade cancel ignored");}catch(OperationCanceledException){}}
            finally{StorageFaults.Handler.Value=prior;}
            using(var db=Open(f))Check(Number(db,"PRAGMA user_version")==1&&Number(db,"SELECT COUNT(*) FROM sqlite_master WHERE name='receipt_cold'")==0,"cancel committed partial schema");
            Check(System.IO.File.Exists(Path.Combine(f.DataRoot,"learning","schema-v1-recovery.sqlite")),"v1 recovery point missing");
            Check((await new LearningStore(f.DataRoot).PolicyAsync("p")).Capture,"upgrade lost existing explicit consent");using var upgraded=Open(f);Check(Number(upgraded,"PRAGMA user_version")==2,"upgrade did not resume");
        }),
        ("learning cold backup restore carries both tiers and cannot revive later deletions",async f=>
        {
            await f.Store.InitializeAsync();var s=await Ready(f);var receipt=await s.CaptureAsync(Request("one"),Proof("one"));var job=(await s.ClaimAsync("p","chat",true))!;await s.CompleteAsync(job.Id,job.Token,[],job.Evidence.Select(e=>e.Evidence.Id).ToHashSet());SeedProfiles(f,1,"candidate",false);await s.ArchiveColdAsync();
            var zip=Path.Combine(f.Root,"cold.zip");await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            await s.DeleteSummaryAsync(receipt.Sequence);await s.SetEntryStateAsync("seed-0","deleted",1);await new PortfolioMaintenance(f.DataRoot).RestoreAsync(zip);
            var restored=new LearningStore(f.DataRoot);Check(!(await restored.PolicyAsync("p")).Capture&&!(await restored.PolicyAsync("p")).Share,"restore upgraded authorization");
            Check((await restored.ReadReceiptHistoryAsync("p")).Items.Single().Summary.State=="deleted","old backup revived cold receipt");
            await Rejected(()=>restored.RestoreEntryAsync("seed-0"));using var db=Open(f);Check(Number(db,"SELECT COUNT(*) FROM profile_cold WHERE id='seed-0'")==0&&Number(db,"SELECT COUNT(*) FROM receipt_cold")==0,"old cold table survived tombstone union");
        }),
        ("learning cold old backup preserves later permanent identities fingerprints and completion watermark",async f=>
        {
            await f.Store.InitializeAsync();var s=await Ready(f);await Complete(s,"one");await s.ArchiveColdAsync();var zip=Path.Combine(f.Root,"old.zip");await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            var later=await Complete(s,"two");await s.ArchiveColdAsync();string fingerprint;using(var db=Open(f)){using var c=db.CreateCommand();c.CommandText="SELECT fingerprint FROM receipts WHERE seq=$seq";c.Parameters.AddWithValue("$seq",later.Sequence);fingerprint=(string)c.ExecuteScalar()!;}
            await new PortfolioMaintenance(f.DataRoot).RestoreAsync(zip);var restored=new LearningStore(f.DataRoot);
            Check(await restored.HasReceiptAsync("p","session","two"),"old backup lost later permanent identity");
            Check((await restored.SnapshotAsync("p")).Watermark>=later.Sequence,"completion watermark moved backwards");
            var page=await restored.ReadReceiptHistoryAsync("p");Check(page.Items.Any(x=>x.Summary.Sequence==later.Sequence&&x.Summary.TurnId=="two"&&x.IsCold),"later cold body/sequence lost");
            using var check=Open(f);using var query=check.CreateCommand();query.CommandText="SELECT COUNT(*) FROM receipts WHERE seq=$seq AND fingerprint=$hash";query.Parameters.AddWithValue("$seq",later.Sequence);query.Parameters.AddWithValue("$hash",fingerprint);Check(Convert.ToInt64(query.ExecuteScalar())==1,"restore changed permanent fingerprint");
        }),
        ("learning cold undo dependencies are not discarded after a thousand unrelated revisions",async f=>
        {
            var s=await Ready(f);SeedProfiles(f,1,"active",true);
            using(var db=Open(f))Exec(db,"""
                WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<1001)
                INSERT INTO history(entry_id,payload) SELECT 'seed-0',json_set((SELECT payload FROM profiles WHERE id='seed-0'),'$.text','undo-'||x,'$.revision',x) FROM n;
                """);
            var edited=await s.EditEntryAsync("seed-0","人工修改正文",1);using(var db=Open(f))Check(Number(db,"SELECT COUNT(*) FROM history")==1002,"undo roots were truncated globally");
            var undo=await s.UndoEntryAsync(edited.Id,edited.Revision);var older=await s.UndoEntryAsync(undo.Id,undo.Revision);
            Check(older.Text=="undo-1001"&&older.Manual,"old undo body/lock no longer recoverable");
        }),
        ("learning cold backup to a fresh root keeps recoverable cold bodies without sharing or remote grants",async f=>
        {
            await f.Store.InitializeAsync();var s=await Ready(f);await Complete(s,"one");SeedProfiles(f,1,"candidate",false);await s.ArchiveColdAsync();
            var zip=Path.Combine(f.Root,"both-tiers.zip");await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);var target=Path.Combine(f.Root,"cold-restored");await new PortfolioMaintenance(target).RestoreAsync(zip);
            var restored=new LearningStore(target);var policy=await restored.PolicyAsync("p");Check(!policy.Capture&&!policy.Learn&&!policy.Share&&!policy.Remote,"cold restore imported grants");
            var receipts=await restored.ReadReceiptHistoryAsync("p");var profiles=await restored.ReadProfileHistoryAsync("p");
            Check(receipts.Items.Single().IsCold&&receipts.Items[0].Summary.Summary==Request("one").Summary&&profiles.Items.Any(x=>x.IsCold&&x.Entry.Text=="合成画像正文"),"cold body not preserved in backup");
            Check(await restored.ClaimAsync("p","chat",true) is null,"restored cold input automatically retried");
        }),
        ("learning cold retryable receipts remain hot and no-change receipts can be cold",async f=>
        {
            var s=await Ready(f);SeedReceipts(f,5,-1);Check((await s.ArchiveColdAsync()).ReceiptsArchived==0,"retry inputs were cold stored");
            await s.CaptureAsync(Request("no-change") with{Outcome="no_change"},Proof("no-change"));Check((await s.ArchiveColdAsync()).ReceiptsArchived==1,"terminal no-change input permanently consumes a hot slot");
            var duplicate=await s.CaptureAsync(Request("no-change") with{Outcome="no_change"},Proof("no-change"));Check(duplicate.Duplicate,"cold no-change replay lost identity");
        }),
        ("learning cold retention follows cold profile and undo evidence roots",async f=>
        {
            var s=await Ready(f);await s.CaptureAsync(Request("one"),Proof("one"));var job=(await s.ClaimAsync("p","chat",true))!;await s.CompleteAsync(job.Id,job.Token,[],job.Evidence.Select(e=>e.Evidence.Id).ToHashSet());
            var summary=(await s.SnapshotAsync("p")).Entries.Single();await s.SetEntryStateAsync(summary.Id,"disabled",summary.Revision);await s.ArchiveColdAsync();await s.CompactAsync();
            using var db=Open(f);Check(Number(db,"SELECT COUNT(*) FROM evidence")==1&&Number(db,"SELECT COUNT(*) FROM receipt_cold")==1&&Number(db,"SELECT COUNT(*) FROM profile_cold")==1,"GC lost cold evidence/undo root");
        }),
        ("learning cold full hot tier allows direct cold deletion and hot deletion releases exactly one slot",async f=>
        {
            var s=await Ready(f);SeedProfiles(f,1,"candidate",false);await s.ArchiveColdAsync();
            using(var db=Open(f))Exec(db,"UPDATE profiles SET id='cold-delete',payload=json_set(payload,'$.id','cold-delete');UPDATE profile_cold SET id='cold-delete',payload=json_set(payload,'$.id','cold-delete')");
            SeedProfiles(f,2000,"active",true);var policy=await s.PolicyAsync("p");
            await s.SetEntryStateAsync("cold-delete","deleted",1);Check((await s.CapacityAsync()).HotProfiles==2000&&(await s.CapacityAsync()).ColdProfiles==0,"direct cold deletion warmed or evicted hot data");
            await s.SetEntryStateAsync("seed-0","deleted",1);Check((await s.CapacityAsync()).HotProfiles==1999,"hot deletion did not release one slot");
            await Complete(s,"new");Check((await s.CapacityAsync()).HotProfiles==2000&&await s.PolicyAsync("p")==policy,"admission after deletion changed capacity/permissions");
        }),
        ("learning cold restored source needs explicit new grant and proof even with same permanent fingerprint",async f=>
        {
            await f.Store.InitializeAsync();var s=await Ready(f);var receipt=await Complete(s,"one");await s.ArchiveColdAsync();var zip=Path.Combine(f.Root,"proof.zip");await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            var target=Path.Combine(f.Root,"proof-restored");await new PortfolioMaintenance(target).RestoreAsync(zip);var restored=new LearningStore(target);
            await Rejected(()=>restored.CaptureAsync(Request("one"),Proof("one")));Check(await restored.ClaimAsync("p","chat",true) is null,"cold restore revived work without grant");
            var before=await restored.PolicyAsync("p");await restored.SetPolicyAsync("p",before with{Capture=true,Learn=true,BatchSize=1},before.Revision);
            var reproof=await restored.CaptureAsync(Request("one"),Proof("one"));Check(reproof.Sequence==receipt.Sequence&&reproof.Version==2&&!reproof.Duplicate,"same-hash fresh proof lost/reused the permanent identity");
            Check(!(await restored.PolicyAsync("p")).Share&&!(await restored.PolicyAsync("p")).Remote,"reproof escalated sharing/remote");
        }),
        ("learning cold current schema missing a cold table fails closed without recreating it",async f=>
        {
            await Ready(f);using(var db=Open(f))Exec(db,"DROP TABLE receipt_cold");var failed=false;
            try{await new LearningStore(f.DataRoot).PolicyAsync("p");}catch(Exception e)when(e is IOException or SqliteException){failed=true;}
            Check(failed,"corrupt authoritative schema was repaired as an empty cold table");using var check=Open(f);Check(Number(check,"SELECT COUNT(*) FROM sqlite_master WHERE name='receipt_cold'")==0,"missing cold body store recreated");
        }),
        ("learning cold profile pagination is scoped bounded and read does not warm or grant",async f=>
        {
            var s=await Ready(f);SeedProfiles(f,7,"candidate",false);await s.ArchiveColdAsync();var before=await s.CapacityAsync();var policy=await s.PolicyAsync("p");
            var ids=new List<string>();string? cursor=null;
            do{var page=await s.ReadProfileHistoryAsync("p",cursor,2);ids.AddRange(page.Items.Select(x=>x.Entry.Id));Check(page.Items.All(x=>x.IsCold&&x.Entry.State=="candidate"&&!x.Entry.Manual),"read-only page warmed or activated a profile");cursor=page.NextCursor;}while(cursor is not null);
            Check(ids.Count==7&&ids.Distinct().Count()==7&&await s.CapacityAsync()==before&&await s.PolicyAsync("p")==policy,"profile paging changed authoritative metadata");
            await Argument(()=>s.ReadProfileHistoryAsync("p","invalid-base64"));await Argument(()=>s.ReadProfileHistoryAsync("p",pageSize:101));
        }),
        ("learning cold old writer receipt deletion fails closed on the initialized store",async f=>
        {
            var s=await Ready(f);var receipt=await Complete(s,"one");await s.ArchiveColdAsync();
            using(var db=Open(f))Exec(db,"UPDATE receipts SET payload=json_set(payload,'$.state','deleted','$.version',2),eligible=0,processed=1,job_id=NULL");
            await Inconsistent(()=>s.SnapshotAsync("p"));
            await Inconsistent(()=>s.ReadReceiptHistoryAsync("p"));
            await Inconsistent(()=>s.RestoreReceiptAsync(receipt.Sequence));
            await Inconsistent(()=>s.CaptureAsync(Request("one"),Proof("one")));
            await Inconsistent(()=>s.HasReceiptAsync("p","session","one"));
            await Inconsistent(()=>new LearningStore(f.DataRoot).SnapshotAsync("p"));
            using var check=Open(f);Check(Number(check,"SELECT COUNT(*) FROM receipts WHERE json_extract(payload,'$.state')='deleted'")==1,"old deletion was overwritten by the cold body");
        }),
        ("learning cold old writer manual edit fails closed on the initialized store",async f=>
        {
            var s=await Ready(f);SeedProfiles(f,1,"candidate",false);await s.ArchiveColdAsync();
            using(var db=Open(f))Exec(db,"UPDATE profiles SET payload=json_set(payload,'$.text','人工优先正文','$.manual',json('true'),'$.state','active','$.revision',2)");
            await Inconsistent(()=>s.ReadProfileHistoryAsync("p"));
            await Inconsistent(()=>s.SharedAsync("p","style"));
            await Inconsistent(()=>s.EditEntryAsync("seed-0","覆盖人工修改",1));
            await Inconsistent(()=>s.RestoreEntryAsync("seed-0"));
            await Inconsistent(()=>s.ArchiveColdAsync());
            await Inconsistent(()=>s.CapacityAsync());
            using var check=Open(f);Check(Number(check,"SELECT COUNT(*) FROM profiles WHERE json_extract(payload,'$.text')='人工优先正文' AND json_extract(payload,'$.manual')=1")==1,"old manual edit was overwritten");
        }),
        ("learning cold old writer revised receipt cannot enqueue old cold input",async f=>
        {
            var s=await Ready(f);await Complete(s,"one");await s.ArchiveColdAsync();
            using(var db=Open(f))Exec(db,"UPDATE receipts SET payload=json_set(payload,'$.summary','旧写入器的新正文','$.version',2),fingerprint='new-old-writer-hash',processed=0,eligible=1,job_id=NULL");
            await Inconsistent(()=>s.ClaimAsync("p","chat",true));
            await Inconsistent(()=>s.PolicyAsync("p"));
            await Inconsistent(()=>s.CompactAsync());
            using var check=Open(f);Check(Number(check,"SELECT COUNT(*) FROM jobs WHERE state='running'")==0,"stale cold input acquired a lease");
        }),
        ("learning cold retention corrupt cold evidence fails closed before deleting unrelated evidence",async f=>
        {
            var s=await Ready(f);SeedReceipts(f,1,1);await s.ArchiveColdAsync();
            using(var db=Open(f))
            {
                Exec(db,"UPDATE receipt_cold SET payload=json_set(payload,'$.evidenceIds',json('[\"cold-proof\"]'))");
                var proof=new LearningEvidence(new("cold-proof","合成引文","session","turn","p",true,DateTimeOffset.UtcNow),"hash","source",true);
                Exec(db,"INSERT INTO evidence(id,scope,valid,payload) VALUES('cold-proof','p',1,$p)",("$p",JsonSerializer.Serialize(proof,Json)));
            }
            var failed=false;try{await s.CompactAsync();}catch(IOException){failed=true;}
            Check(failed,"GC trusted a mismatched cold reference ledger");using var check=Open(f);Check(Number(check,"SELECT COUNT(*) FROM evidence")==1,"failed cold validation committed a deletion");
        })
    ];
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web);
    private static async Task Inconsistent(Func<Task> action)
    {try{await action();}catch(IOException){return;}throw new Exception("inconsistent old-writer ledger was consumed instead of failing closed");}
    private static LearningCapture Request(string turn)=>new("p","session",turn,"合成回合摘要。","completed",["以后请简洁回答"],[]);
    private static LearningVerifiedTurn Proof(string turn)=>new("session",turn,Path.GetTempPath(),"user-hash","以后请简洁回答",DateTimeOffset.UtcNow,"source-hash");
    private static async Task<LearningStore> Ready(Fixture f)
    {var s=new LearningStore(f.DataRoot);await s.SetPolicyAsync("p",new(){Capture=true,Learn=true,BatchSize=1});return s;}
    private static SqliteConnection Open(Fixture f)=>OpenRoot(f.DataRoot);
    private static SqliteConnection OpenRoot(string root)
    {var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"learning","state.sqlite"),Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());db.Open();return db;}
    private static long Number(SqliteConnection db,string sql){using var c=db.CreateCommand();c.CommandText=sql;return Convert.ToInt64(c.ExecuteScalar());}
    private static void Exec(SqliteConnection db,string sql,params (string,object?)[] args)
    {using var c=db.CreateCommand();c.CommandText=sql;foreach(var(k,v)in args)c.Parameters.AddWithValue(k,v??DBNull.Value);c.ExecuteNonQuery();}
    private static void SeedReceipts(Fixture f,int count,int processed,string state="verified")
    {
        using var db=Open(f);var start=Number(db,"SELECT COALESCE(MAX(seq),0) FROM receipts");
        Exec(db,"""
            WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<$count)
            INSERT INTO receipts(seq,id,scope,fingerprint,payload,eligible,processed)
            SELECT $start+x,'seed-'||($start+x),'p','stable-fingerprint',
            json_object('sequence',$start+x,'version',1,'projectId','p','sessionId','seed','turnId','seed-'||($start+x),'summary',CASE WHEN $state='deleted' THEN '' ELSE '合成冷藏正文' END,'outcome','completed','state',$state,'recordedAt','2026-11-01T00:00:00Z','evidenceIds',json('[]'),'outputs',json('[]')),1,$processed FROM n;
            """,("$count",count),("$start",start),("$processed",processed),("$state",state));
    }
    private static void SeedProfiles(Fixture f,int count,string state,bool manual)
    {
        using var db=Open(f);Exec(db,"BEGIN IMMEDIATE");
        for(var i=0;i<count;i++)
        {
            var entry=new LearningEntry("seed-"+i,"p","style","verbosity","brief",state=="deleted"?"":"合成画像正文","",state,"seed",manual,1,DateTimeOffset.UtcNow.AddDays(-10),[]);
            Exec(db,"INSERT INTO profiles(id,scope,updated,payload) VALUES($id,'p',1,$p)",("$id",entry.Id),("$p",JsonSerializer.Serialize(entry,Json)));
        }
        Exec(db,"COMMIT");
    }
    private static async Task<string> Rejected(Func<Task> action)
    {try{await action();}catch(InvalidOperationException e){return e.Message;}throw new Exception("protected capacity unexpectedly accepted work");}
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    private static async Task<LearningReceipt> Complete(LearningStore s,string turn)
    {var receipt=await s.CaptureAsync(Request(turn),Proof(turn));var job=(await s.ClaimAsync("p","chat",true))!;await s.CompleteAsync(job.Id,job.Token,[],job.Evidence.Select(e=>e.Evidence.Id).ToHashSet());return receipt;}
    private static async Task Argument(Func<Task> action){try{await action();}catch(ArgumentException){return;}throw new Exception("invalid history query accepted");}
}
