using Amifolio.Core;
using System.Text.Json;
using Microsoft.Data.Sqlite;

internal static class LearningCases
{
    internal static (string,Func<Fixture,Task>)[] All=>
    [
        ("project now learning picks a scoped active preference and a reviewable candidate without enabling sharing",async f=>
        {
            var s=Store(f);
            var empty=await s.ReadPreferenceHighlightsAsync("p");
            Check(empty.Active.Count==0&&empty.Candidate is null&&!s.HasState,"empty overview initialized a learning store");
            await s.SetPolicyAsync("p",new(){Capture=true,Learn=true,BatchSize=1});
            await s.CaptureAsync(Request(),Proof());
            var job=(await s.ClaimAsync("p","chat",true))!;
            var evidence=job.Evidence.Select(e=>e.Evidence.Id).ToArray();
            await s.CompleteAsync(job.Id,job.Token,[new("style","verbosity","brief","偏好简洁回答","project","",evidence)],evidence.ToHashSet());
            using(var db=CapacityDb(f))
            {
                CapacityEntry(db,"visible-active","active",DateTimeOffset.UtcNow,"保留的已启用偏好");
                CapacityEntry(db,"unrelated-summary","active",DateTimeOffset.UtcNow,"非偏好概述",kind:"summary");
            }
            var project=await s.ReadPreferenceHighlightsAsync("p");
            Check(project.Active.Single().Id=="visible-active"&&project.Candidate is{State:"candidate",Scope:"p"}&&!project.Shared,"highlight mixed evidence, state, or sharing");
            var other=await s.ReadPreferenceHighlightsAsync("other");
            Check(other.Active.Count==0&&other.Candidate is null,"project preference leaked to another scope");
            await s.WithdrawEvidenceAsync(evidence[0]);
            Check((await s.ReadPreferenceHighlightsAsync("p")).Candidate is null,"withdrawn evidence left a review shortcut");
        }),
        ("learning new permissions default off and no legacy consent upgrade",async f=>
        {
            var p=await Store(f).PolicyAsync("p");Check(!p.Capture&&!p.Learn&&!p.Share&&!p.Remote&&!p.AutoActivate,"new consent enabled");
            await Throws(()=>Store(f).CaptureAsync(Request(),Proof()));
        }),
        ("learning immediate receipt idempotence and revised payload versions",async f=>
        {
            var s=Store(f);await Enable(s);var a=await s.CaptureAsync(Request(),Proof());var b=await s.CaptureAsync(Request(),Proof());
            var c=await s.CaptureAsync(Request() with{Summary="修订概述"},Proof());
            Check(a.Sequence==b.Sequence&&b.Duplicate&&c.Sequence==a.Sequence&&c.Version==2,"receipt id/version not stable");
            Check((await new LearningStore(f.DataRoot).SnapshotAsync("p")).Summaries.Count==1,"reopen duplicated receipt");
        }),
        ("learning unverified and pregrant turns cannot advance processing",async f=>
        {
            var s=Store(f);await Enable(s);await s.CaptureAsync(Request(),null);
            Check((await s.SnapshotAsync("p")).Pending==0&&await s.ClaimAsync("p","chat",true) is null,"unverified became pending");
            await s.CaptureAsync(Request() with{TurnId="old"},Proof() with{TurnId="old",ObservedAt=DateTimeOffset.UtcNow.AddDays(-1)});
            Check((await s.SnapshotAsync("p")).WaitingEvidence==2,"pregrant evidence authorized retroactively");
        }),
        ("learning queue has single durable owner and mode fences late completion",async f=>
        {
            var s=Store(f);await Enable(s);await s.CaptureAsync(Request(),Proof());
            var job=await s.ClaimAsync("p","chat",true)??throw new Exception("no lease");
            Check(await new LearningStore(f.DataRoot).ClaimAsync("p","chat",true) is null,"double claimed");
            var p=await s.PolicyAsync("p");await s.SetPolicyAsync("p",p with{Mode="background",Remote=true,Endpoint="https://example.com/v1"},p.Revision);
            await Throws(()=>s.CompleteAsync(job.Id,job.Token,[],job.Evidence.Select(e=>e.Evidence.Id).ToHashSet()));
            Check((await s.SnapshotAsync("p")).Pending==1,"mode switch lost backlog");
        }),
        ("learning completion commits profile and watermark atomically",async f=>
        {
            var s=Store(f);await Enable(s);await s.CaptureAsync(Request(),Proof());var j=(await s.ClaimAsync("p","chat",true))!;
            var p=new MemoryProposal("style","verbosity","brief","偏好简洁表达","project","",j.Evidence.Select(e=>e.Evidence.Id).ToArray());
            var r=await s.CompleteAsync(j.Id,j.Token,[p],j.Evidence.Select(e=>e.Evidence.Id).ToHashSet());
            var again=await s.CompleteAsync(j.Id,j.Token,[p],j.Evidence.Select(e=>e.Evidence.Id).ToHashSet());
            var snap=await s.SnapshotAsync("p");Check(r==again&&snap.Watermark==1&&snap.Pending==0&&snap.Entries.Count(e=>e.Kind=="style")==1&&snap.Entries.Count(e=>e.Kind=="summary")==1,"commit not idempotent");
        }),
        ("learning manual edit locks text and undo restores prior revision",async f=>
        {
            var (s,e)=await Completed(f);var edited=await s.EditEntryAsync(e.Id,"我修改的简洁偏好",e.Revision);
            Check(edited.Manual&&edited.Text=="我修改的简洁偏好","manual lock absent");
            await Throws(()=>s.EditEntryAsync(e.Id,"过时修改",e.Revision));
            var undone=await s.UndoEntryAsync(e.Id,edited.Revision);Check(undone.Text==e.Text&&undone.Revision>edited.Revision,"undo did not restore");
        }),
        ("learning deleted dimension cannot return with new model wording",async f=>
        {
            var(s,e)=await Completed(f);await s.SetEntryStateAsync(e.Id,"deleted",e.Revision);
            var req=Request() with{TurnId="turn2",Summary="再次讨论表达"};var proof=Proof() with{TurnId="turn2"};await s.CaptureAsync(req,proof);
            var j=(await s.ClaimAsync("p","chat",true))!;await s.CompleteAsync(j.Id,j.Token,[new("style","verbosity","brief","日后少说废话，简练些","project","",j.Evidence.Select(x=>x.Evidence.Id).ToArray())],j.Evidence.Select(x=>x.Evidence.Id).ToHashSet());
            Check(!(await s.SnapshotAsync("p")).Entries.Any(x=>x.Dimension=="verbosity"&&x.State=="active"),"deleted preference resurrected");
        }),
        ("learning sharing remains separate and revocation withdraws active data",async f=>
        {
            var(s,e)=await Completed(f);Check((await s.SharedAsync("p","style")).Count==0,"learning implicitly shared");
            var p=await s.PolicyAsync("p");await s.SetPolicyAsync("p",p with{Share=true},p.Revision);
            Check((await s.SharedAsync("p","style")).Count==1,"active style not shared");
            await s.WithdrawEvidenceAsync(e.EvidenceIds[0]);Check((await s.SharedAsync("p","style")).Count==0,"withdrawn source still shares");
        }),
        ("learning canonical rendering cannot activate invented model prose",async f=>
        {
            var s=Store(f);await Enable(s);await s.CaptureAsync(Request(),Proof());var j=(await s.ClaimAsync("p","chat",true))!;
            await s.CompleteAsync(j.Id,j.Token,[new("style","verbosity","brief","用户住在虚构城市，永远喜欢冗长回答","project","",j.Evidence.Select(x=>x.Evidence.Id).ToArray())],j.Evidence.Select(x=>x.Evidence.Id).ToHashSet());
            var entry=(await s.SnapshotAsync("p")).Entries.Single(e=>e.Kind=="style");
            Check(entry.Text=="偏好简洁回答","model prose bypassed canonical value evidence");
        }),
        ("learning substring quote cannot discard user negation and quotation context",async f=>
        {
            var s=Store(f);await Enable(s);var source=Proof() with{UserText="请把“以后请简洁回答”翻译成英文，这不是我的偏好。"};
            await s.CaptureAsync(Request(),source);var j=(await s.ClaimAsync("p","chat",true))!;
            await s.CompleteAsync(j.Id,j.Token,[new("style","verbosity","brief","偏好简洁回答","project","",j.Evidence.Select(e=>e.Evidence.Id).ToArray())],j.Evidence.Select(e=>e.Evidence.Id).ToHashSet());
            Check(!(await s.SnapshotAsync("p")).Entries.Any(e=>e.Kind=="style"&&e.State=="active"),"substring quote became a user preference");
        }),
        ("learning capacity rejects oversized serialized capture before committing evidence",async f=>
        {
            var s=Store(f);await Enable(s);var (request,source)=OversizedCapture();
            var message=await CapacityReject(()=>s.CaptureAsync(request,source));
            Check(message.Contains("24",StringComparison.Ordinal),"payload rejection omitted its actual limit");
            using(var db=CapacityDb(f))Check(CapacityCount(db,"receipts")==0&&CapacityCount(db,"evidence")==0,"rejected payload committed receipt/evidence");
            var accepted=await s.CaptureAsync(Request(),Proof());Check(accepted.Version==1&&accepted.Pending==1,"rejected capture consumed its identity/version");
        }),
        ("learning capacity oversized revision preserves receipt lease evidence and projection",async f=>
        {
            var(s,entry)=await Completed(f);var policy=await s.PolicyAsync("p");await s.SetPolicyAsync("p",policy with{Share=true},policy.Revision);
            var pointer=await s.PublishProjectionAsync("p",true);var original=await s.CaptureAsync(Request() with{Summary="合法修订"},Proof());
            var job=(await s.ClaimAsync("p","chat",true))!;var (request,source)=OversizedCapture();
            await CapacityReject(()=>s.CaptureAsync(request,source));
            var snapshot=await s.SnapshotAsync("p");Check(snapshot.Summaries.Single().Version==original.Version&&snapshot.Summaries.Single().Summary=="合法修订","rejected revision overwrote receipt");
            Check((await s.OwnedJobAsync(job.Id,job.Token)).State=="running","rejected revision cancelled a valid lease");
            Check((await s.EvidenceAsync(entry.EvidenceIds)).Count==entry.EvidenceIds.Count&&File.Exists(pointer!),"rejected revision withdrew evidence/projection");
            using(var db=CapacityDb(f))Check(CapacityCount(db,"evidence")==1,"rejected revision leaked additional evidence");
            var corrected=await s.CaptureAsync(Request() with{Summary="精简后的修订"},Proof());Check(corrected.Version==original.Version+1,"corrected revision was not accepted");
        }),
        ("learning capacity legacy oversized head fails explicitly and later valid receipt progresses",async f=>
        {
            var s=Store(f);await Enable(s);var first=await s.CaptureAsync(Request(),Proof());MakeLegacyOversized(f,first.Sequence);
            var later=await s.CaptureAsync(Request() with{TurnId="later"},Proof() with{TurnId="later"});
            var job=await s.ClaimAsync("p","chat",true);Check(job is not null&&job.Summaries.Single().Sequence==later.Sequence,"oversized head blocked a later valid receipt");
            var snapshot=await s.SnapshotAsync("p");Check(snapshot.Failed==1&&snapshot.Summaries.Single(x=>x.Sequence==first.Sequence).State=="failed","oversized receipt did not enter explicit failure");
            await s.CompleteAsync(job!.Id,job.Token,[],job.Evidence.Select(x=>x.Evidence.Id).ToHashSet());
            Check(await s.ClaimAsync("p","chat",true) is null,"oversized receipt was automatically requeued");
            await s.CaptureAsync(Request() with{Summary="修正旧超限记录"},Proof());
            Check((await s.ClaimAsync("p","chat",true))?.Summaries.Single().Sequence==first.Sequence,"failed oversized receipt cannot be repaired by same-turn revision");
        }),
        ("learning capacity all oversized legacy items freeze without phantom jobs",async f=>
        {
            var s=Store(f);await Enable(s);var receipt=await s.CaptureAsync(Request(),Proof());MakeLegacyOversized(f,receipt.Sequence);
            Check(await s.ClaimAsync("p","chat",true) is null,"oversized-only batch claimed a job");
            var snapshot=await s.SnapshotAsync("p");Check(snapshot.Failed==1&&snapshot.Pending==0&&snapshot.Jobs.Count==0,"oversized-only batch did not persist failure without a phantom job");
            Check(await s.ClaimAsync("p","chat",true) is null,"frozen oversized input became eligible again");
        }),
        ("learning capacity active context survives 500 newer candidates and stays bounded",async f=>
        {
            var s=Store(f);await Enable(s);var policy=await s.PolicyAsync("p");await s.SetPolicyAsync("p",policy with{Share=true},policy.Revision);
            using(var db=CapacityDb(f))
            {
                CapacityEntry(db,"old-active","active",DateTimeOffset.UtcNow.AddDays(-3),"retained active style");
                CapacityEntry(db,"old-conditional","active",DateTimeOffset.UtcNow.AddDays(-2),"retained conditional style","when explaining math");
                SeedCapacityEntries(db,500,"candidate");
            }
            var docs=await s.SharedAsync("p","style");Check(docs.Count==1,"newer candidates hid active context");
            Check(docs.Values.Single().Contains("retained active style",StringComparison.Ordinal)&&!docs.Values.Single().Contains("retained conditional style",StringComparison.Ordinal),"active context text or condition changed");
            Check((await s.SharedAsync("p","style","when explaining math")).Values.Single().Contains("retained conditional style",StringComparison.Ordinal),"matching conditional active entry was hidden");
            Check((await s.SnapshotAsync("p")).Entries.Any(e=>e.Id=="old-active"),"source verification snapshot still omits shared active entry");
            using(var db=CapacityDb(f))for(var i=0;i<30;i++)CapacityEntry(db,"active-"+i,"active",DateTimeOffset.UtcNow.AddDays(-1),"bounded style "+i);
            Check((await s.SharedAsync("p","style")).Values.Single().Split('\n').Count(line=>line.StartsWith("- ",StringComparison.Ordinal))==24,"shared context exceeded 24 entries");
        }),
        ("learning capacity layer filtering precedes limit while overview keeps its total cap",async f=>
        {
            var s=Store(f);await Enable(s);var policy=await s.PolicyAsync("p");await s.SetPolicyAsync("p",policy with{Share=true},policy.Revision);
            using(var db=CapacityDb(f))
            {
                CapacityEntry(db,"older-style","active",DateTimeOffset.UtcNow.AddDays(-1),"older layer-specific style");
                for(var i=0;i<24;i++)CapacityEntry(db,"newer-memory-"+i,"active",DateTimeOffset.UtcNow,"newer memory "+i,kind:"memory");
            }
            var style=await s.SharedAsync("p","style");Check(style.Count==1&&style.Values.Single().Contains("older layer-specific style",StringComparison.Ordinal),"24 newer memories hid the active style layer");
            var memory=(await s.SharedAsync("p","memory")).Values.Single();
            Check(memory.Split('\n').Count(line=>line.StartsWith("- ",StringComparison.Ordinal))==24&&!memory.Contains("older layer-specific style",StringComparison.Ordinal),"memory layer changed its filter or cap");
            var overview=(await s.SharedAsync("p","overview")).Values.Single();
            Check(overview.Contains("可用风格 0 项",StringComparison.Ordinal)&&overview.Contains("可用记忆 24 项",StringComparison.Ordinal),"overview no longer keeps a total cap of 24");
        }),
        ("learning capacity receipt tombstones preserve identities without consuming hot slots",async f=>
        {
            var s=Store(f);await Enable(s);
            using(var db=CapacityDb(f))CapacityExec(db,"""
                WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<10000)
                INSERT INTO receipts(id,scope,fingerprint,payload,eligible,processed) SELECT 'deleted-'||x,'p','deleted',json_object('sequence',x,'version',1,'projectId','p','sessionId','seed','turnId','deleted-'||x,'summary','','outcome','completed','state','deleted','recordedAt','2026-11-14T00:00:00Z','evidenceIds',json('[]'),'outputs',json('[]')),0,1 FROM n;
                """);
            var receipt=await s.CaptureAsync(Request(),Proof());
            Check(receipt.Sequence>10000,"deleted identities reused a sequence");
            using(var db=CapacityDb(f))Check(CapacityCount(db,"receipts")==10001,"admission removed tombstone identities");
        }),
        ("learning capacity summary insertion respects profile quota and existing updates remain allowed",async f=>
        {
            var s=Store(f);await Enable(s);await s.CaptureAsync(Request(),Proof());var job=(await s.ClaimAsync("p","chat",true))!;
            using(var db=CapacityDb(f))SeedCapacityEntries(db,2000,"active");
            var message=await CapacityReject(()=>s.CompleteAsync(job.Id,job.Token,[],job.Evidence.Select(x=>x.Evidence.Id).ToHashSet()));
            Check(message.Contains("2000",StringComparison.Ordinal)&&message.Contains("热",StringComparison.Ordinal),"protected hot profile quota error is not truthful");
            using(var db=CapacityDb(f))
            {
                Check(CapacityCount(db,"profiles")==2000,"summary bypassed profile quota");
                CapacityExec(db,"DELETE FROM profiles WHERE id='capacity-0'");
                var id=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("p\nconversation-summary")));
                CapacityEntry(db,id,"active",DateTimeOffset.UtcNow,"existing automatic summary",kind:"summary",manual:false);
            }
            await s.CompleteAsync(job.Id,job.Token,[],job.Evidence.Select(x=>x.Evidence.Id).ToHashSet());
            using(var db=CapacityDb(f))Check(CapacityCount(db,"profiles")==2000,"updating existing summary increased profile count");
        }),
    ];
    private static readonly JsonSerializerOptions CapacityJson=new(JsonSerializerDefaults.Web);
    private static (LearningCapture,LearningVerifiedTurn) OversizedCapture()
    {
        var quotes=new[]{new string('甲',1000),new string('乙',1000),new string('丙',1000),new string('丁',1000)};
        return(Request() with{Summary=new string('中',1500),Quotes=quotes},Proof() with{UserText=string.Join("。",quotes),UserTextHash="large-user-hash",SourceFingerprint="large-source-fingerprint"});
    }
    private static SqliteConnection CapacityDb(Fixture f)
    {
        var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(f.DataRoot,"learning","state.sqlite"),Pooling=false}.ToString());db.Open();return db;
    }
    private static void CapacityExec(SqliteConnection db,string sql,params (string Key,object Value)[] args)
    {using var command=db.CreateCommand();command.CommandText=sql;foreach(var(key,value)in args)command.Parameters.AddWithValue(key,value);command.ExecuteNonQuery();}
    private static long CapacityCount(SqliteConnection db,string table)
    {using var command=db.CreateCommand();command.CommandText="SELECT COUNT(*) FROM "+table;return Convert.ToInt64(command.ExecuteScalar());}
    private static void CapacityEntry(SqliteConnection db,string id,string state,DateTimeOffset at,string text,string condition="",string kind="style",bool manual=true)
    {
        var entry=new LearningEntry(id,"p",kind,"verbosity","brief",text,condition,state,"synthetic",manual,1,at,[]);
        CapacityExec(db,"INSERT INTO profiles(id,scope,updated,payload) VALUES($id,'p',$at,$payload)",("$id",id),("$at",at.ToUnixTimeMilliseconds()),("$payload",JsonSerializer.Serialize(entry,CapacityJson)));
    }
    private static void SeedCapacityEntries(SqliteConnection db,int count,string state)
    {
        CapacityExec(db,"BEGIN IMMEDIATE");
        for(var i=0;i<count;i++)CapacityEntry(db,"capacity-"+i,state,DateTimeOffset.UtcNow,"synthetic entry "+i);
        CapacityExec(db,"COMMIT");
    }
    private static void MakeLegacyOversized(Fixture f,long sequence)
    {
        using var db=CapacityDb(f);using var command=db.CreateCommand();command.CommandText="SELECT payload FROM receipts WHERE seq=$seq";command.Parameters.AddWithValue("$seq",sequence);
        var summary=JsonSerializer.Deserialize<LearningSummary>((string)command.ExecuteScalar()!,CapacityJson)!;
        summary=summary with{Summary=new string('中',1500),Outputs=Enumerable.Range(0,12).Select(i=>new LearningOutput("item"+i+".md",new string('文',300))).ToArray()};
        CapacityExec(db,"UPDATE receipts SET fingerprint='legacy-oversized',payload=$payload WHERE seq=$seq",("$payload",JsonSerializer.Serialize(summary,CapacityJson)),("$seq",sequence));
    }
    private static async Task<string> CapacityReject(Func<Task> run)
    {try{await run();}catch(Exception e)when(e is ArgumentException or InvalidOperationException){return e.Message;}throw new Exception("capacity admission unexpectedly succeeded");}
    private static LearningStore Store(Fixture f)=>new(f.DataRoot);
    private static async Task<(LearningStore,LearningEntry)> Completed(Fixture f)
    {
        var s=Store(f);await Enable(s);await s.CaptureAsync(Request(),Proof());var j=(await s.ClaimAsync("p","chat",true))!;
        await s.CompleteAsync(j.Id,j.Token,[new("style","verbosity","brief","偏好简洁表达","project","",j.Evidence.Select(x=>x.Evidence.Id).ToArray())],j.Evidence.Select(x=>x.Evidence.Id).ToHashSet());
        return(s,(await s.SnapshotAsync("p")).Entries.Single(e=>e.Kind=="style"));
    }
    internal static LearningCapture Request()=>new("p","session","turn","讨论界面文案，要求简洁。","completed",["以后请简洁回答"],[]);
    internal static LearningVerifiedTurn Proof()=>new("session","turn",Path.GetTempPath(),"user-hash","以后请简洁回答",DateTimeOffset.UtcNow,"fingerprint");
    internal static Task<LearningPolicy> Enable(LearningStore s)=>s.SetPolicyAsync("p",new(){Capture=true,Learn=true,AutoActivate=true,BatchSize=1});
    internal static void Check(bool value,string message){if(!value)throw new Exception(message);}
    internal static async Task Throws(Func<Task> run){try{await run();}catch(Exception e)when(e is InvalidOperationException or ArgumentException or IOException){return;}throw new Exception("unsafe operation succeeded");}
}
