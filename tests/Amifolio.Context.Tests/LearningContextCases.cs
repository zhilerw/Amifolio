using System.Text.Json;
using Amifolio.Core;
using Amifolio.Context;

internal static class LearningContextCases
{
    internal static (string,Func<Task>)[] All=>
    [
        ("learning context validates real user turn rather than model proof",async()=>
        {
            using var f=new LearningFixture();await f.Initialize();await f.Enable();var capture=await f.Turn();
            var receipt=await f.Context.CaptureLearningAsync(capture,f.Home);
            Check(receipt.State=="verified"&&receipt.Pending==1,"verified host turn not accepted");
            await Throws(()=>f.Context.CaptureLearningAsync(capture with{Quotes=["模型声称用户喜欢红色"]},f.Home));
        }),
        ("learning context cannot read user transcript before capture authorization",async()=>
        {
            using var f=new LearningFixture();await f.Initialize();var capture=await f.Turn();
            await Throws(()=>f.Context.CaptureLearningAsync(capture,f.Home));
            Check((await f.Context.Learning.SnapshotAsync(f.Project!.Id)).Summaries.Count==0,"unauthorized turn persisted");
        }),
        ("learning context live source withdrawal prevents publication",async()=>
        {
            using var f=new LearningFixture();await f.Initialize();await f.Enable();var capture=await f.Turn();
            await f.Context.CaptureLearningAsync(capture,f.Home);var j=(await f.Context.Learning.ClaimAsync(f.Project!.Id,"chat",true))!;
            await f.Context.CompleteLearningAsync(j.Id,j.Token,[new("style","verbosity","brief","偏好简洁表达","project","",j.Evidence.Select(x=>x.Evidence.Id).ToArray())],f.Home);
            await f.Context.SetEnabledAsync(f.Project.Id,true);
            var p=await f.Context.Learning.PolicyAsync(f.Project.Id);await f.Context.Learning.SetPolicyAsync(f.Project.Id,p with{Share=true},p.Revision);
            Check((await f.Context.ReadLearningAsync(f.Project.Id,"style","",f.Home)).Count==1,"active context not published");
            File.Delete(f.Rollout!);
            Check((await f.Context.ReadLearningAsync(f.Project.Id,"style","",f.Home)).Count==0,"deleted user proof still shared");
        }),
        ("learning background sends only authorized summary and minimal quotes",async()=>
        {
            using var provider=new LearningProvider();using var http=new HttpClient(provider);using var f=new LearningFixture(http);await f.Initialize();await f.Enable();
            var capture=await f.Turn("以后请简洁回答\nsource-original-private-body");capture=capture with{Quotes=["以后请简洁回答"]};
            await f.Context.CaptureLearningAsync(capture,f.Home);await Background(f);
            Check(await f.Context.RunLearningBackgroundAsync(f.Project!.Id,f.Home,new("https://example.com/v1","synthetic-model",true),"synthetic-private-key",true),"background did not complete");
            Check(provider.Calls==1&&provider.Body.Contains("以后请简洁回答")&&!provider.Body.Contains("source-original-private-body")&&!provider.Body.Contains("synthetic-private-key")&&!provider.Body.Contains(f.Source),"provider payload exceeded authorized input");
            Check((await f.Context.Learning.SnapshotAsync(f.Project.Id)).Pending==0,"background watermark not advanced");
        }),
        ("learning background budget and exact provider authorization are enforced",async()=>
        {
            using var provider=new LearningProvider();using var http=new HttpClient(provider);using var f=new LearningFixture(http);await f.Initialize();await f.Enable();await f.Context.CaptureLearningAsync(await f.Turn(),f.Home);await Background(f);
            Check(!await f.Context.RunLearningBackgroundAsync(f.Project!.Id,f.Home,new("https://other.example/v1","synthetic-model",true),"synthetic-private-key",true),"changed provider inherited authorization");
            var budget=Path.Combine(f.Data,"context-state","automation-budget.json");Directory.CreateDirectory(Path.GetDirectoryName(budget)!);
            await File.WriteAllTextAsync(budget,JsonSerializer.Serialize(new{Day=DateTime.UtcNow.ToString("yyyy-MM-dd"),Requests=8,LastAttempt=(DateTimeOffset?)null}));
            Check(!await f.Context.RunLearningBackgroundAsync(f.Project.Id,f.Home,new("https://example.com/v1","synthetic-model",true),"synthetic-private-key",true)&&provider.Calls==0,"daily budget bypassed");
        }),
        ("learning master sharing off fences stale projection writers",async()=>
        {
            using var f=new LearningFixture();await f.Initialize();await f.Enable();await f.Context.SetEnabledAsync(f.Project!.Id,true);
            var p=await f.Context.Learning.PolicyAsync(f.Project.Id);await f.Context.Learning.SetPolicyAsync(f.Project.Id,p with{Share=true},p.Revision);
            await f.Context.Learning.PublishProjectionAsync(f.Project.Id,true);
            await f.Context.SetEnabledAsync(f.Project.Id,false);
            Check(!(await f.Context.Learning.PolicyAsync(f.Project.Id)).Share,"master revocation did not fence independent learning sharing");
            Check(await f.Context.Learning.PublishProjectionAsync(f.Project.Id,true) is null,"stale master snapshot resurrected projection");
        }),
        ("learning background queued work cannot send to superseded service",async()=>
        {
            using var provider=new LearningProvider();using var http=new HttpClient(provider);using var f=new LearningFixture(http);await f.Initialize();await f.Enable();await f.Context.CaptureLearningAsync(await f.Turn(),f.Home);await Background(f);
            var slot=await new TaskStore(f.Data).ModelSlotAsync();using var observer=new SlotObserver();
            try
            {
                var work=f.Context.RunLearningBackgroundAsync(f.Project!.Id,f.Home,new("https://example.com/v1","synthetic-model",true),"synthetic-private-key",true);
                await observer.Observed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var p=await f.Context.Learning.PolicyAsync(f.Project.Id);await f.Context.Learning.SetPolicyAsync(f.Project.Id,p with{Endpoint="https://other.example/v1"},p.Revision);
                await slot.DisposeAsync();await work;
                Check(provider.Calls==0,"queued request inherited new revision but sent to old endpoint");
            }
            finally{await slot.DisposeAsync();}
        }),
        ("learning context whole message prevents quoted example activation",async()=>
        {
            using var f=new LearningFixture();await f.Initialize();await f.Enable();
            var capture=await f.Turn("请把“以后请简洁回答”翻译成英文，这不是我的偏好。");capture=capture with{Quotes=["以后请简洁回答"]};
            await f.Context.CaptureLearningAsync(capture,f.Home);var j=(await f.Context.Learning.ClaimAsync(f.Project!.Id,"chat",true))!;
            await f.Context.CompleteLearningAsync(j.Id,j.Token,[new("style","verbosity","brief","偏好简洁回答","project","",j.Evidence.Select(e=>e.Evidence.Id).ToArray())],f.Home);
            Check(!(await f.Context.Learning.SnapshotAsync(f.Project.Id)).Entries.Any(e=>e.Kind=="style"&&e.State=="active"),"example became preference");
        }),
        ("learning context independent evidence accumulates across batches",async()=>
        {
            using var f=new LearningFixture();await f.Initialize();await f.Enable();
            foreach(var text in new[]{"请简洁回答","请简短回答","请简明回答"})await CompleteRound(f,text);
            Check((await f.Context.Learning.SnapshotAsync(f.Project!.Id)).Entries.Any(e=>e.Kind=="style"&&e.State=="active"&&e.EvidenceIds.Count==3),"independent evidence was not accumulated");
        }),
        ("learning context case variants cannot bypass live validation of old support",async()=>
        {
            using var f=new LearningFixture();await f.Initialize();await f.Enable();
            await CompleteRound(f,"请简洁回答");var first=f.Rollout!;await CompleteRound(f,"请简短回答");File.Delete(first);
            try{await CompleteRound(f,"请简明回答","VERBOSITY");}catch(InvalidOperationException){}
            Check(!(await f.Context.Learning.SnapshotAsync(f.Project!.Id)).Entries.Any(e=>e.Kind=="style"&&e.State=="active"),"missing old evidence activated through alternate dimension case");
        }),
    ];
    internal static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    internal static async Task Throws(Func<Task> run){try{await run();}catch(Exception e)when(e is InvalidOperationException or ArgumentException or IOException){return;}throw new Exception("unsafe action succeeded");}
    internal sealed class LearningFixture:IDisposable
    {
        internal string Root{get;}=Path.Combine(Path.GetTempPath(),"Amifolio-learning-context-"+Guid.NewGuid().ToString("N"));
        internal string Data=>Path.Combine(Root,"data");internal string Home=>Path.Combine(Root,"host");internal string Source=>Path.Combine(Root,"project");
        internal PortfolioStore Store{get;}internal ContextService Context{get;}internal CodexProject? Project;internal string? Rollout;
        internal LearningFixture(HttpClient? http=null){Store=new(Data);Context=http is null?new(Store):new(Store,http);}
        internal async Task Initialize()
        {
            Directory.CreateDirectory(Home);Directory.CreateDirectory(Source);await Store.InitializeAsync();Store.EnableCodexReferenceMode(Home);
            await File.WriteAllTextAsync(Path.Combine(Home,".codex-global-state.json"),JsonSerializer.Serialize(new Dictionary<string,object>{{"local-projects",new Dictionary<string,object>{{"p",new{id="p",name="synthetic",rootPaths=new[]{Source}}}}}}));
            await Store.RefreshCodexProjectsAsync();Project=Store.CodexProjects.Single();
        }
        internal Task<LearningPolicy> Enable()=>Context.Learning.SetPolicyAsync(Project!.Id,new(){Capture=true,Learn=true,AutoActivate=true,BatchSize=1});
        internal async Task<LearningCapture> Turn(string text="以后请简洁回答",string? session=null,string turn="turn-1")
        {
            session??=Guid.NewGuid().ToString();var now=DateTimeOffset.UtcNow.ToString("O");var dir=Path.Combine(Home,"sessions",DateTime.UtcNow.ToString("yyyy"),DateTime.UtcNow.ToString("MM"),DateTime.UtcNow.ToString("dd"));Directory.CreateDirectory(dir);
            Rollout=Path.Combine(dir,"rollout-"+session+".jsonl");
            var rows=new object[]{new{timestamp=now,type="session_meta",payload=new{id=session,cwd=Source}},new{timestamp=now,type="turn_context",payload=new{turn_id=turn,cwd=Source}},new{timestamp=now,type="event_msg",payload=new{type="user_message",message=text}}};
            await File.WriteAllLinesAsync(Rollout,rows.Select(x=>JsonSerializer.Serialize(x)));
            return new(Project!.Id,session,turn,"讨论表达方式，记录用户要求。","completed",[text],[]);
        }
        public void Dispose(){if(Directory.Exists(Root))Directory.Delete(Root,true);}
    }
    private static async Task Background(LearningFixture f)
    {
        var p=await f.Context.Learning.PolicyAsync(f.Project!.Id);await f.Context.Learning.SetPolicyAsync(f.Project.Id,p with{Mode="background",Remote=true,Endpoint="https://example.com/v1"},p.Revision);
        await new TaskStore(f.Data).SetRemoteAllowedAsync(true);
    }
    private static async Task CompleteRound(LearningFixture f,string text,string dimension="verbosity")
    {
        await f.Context.CaptureLearningAsync(await f.Turn(text),f.Home);var job=(await f.Context.Learning.ClaimAsync(f.Project!.Id,"chat",true))!;
        await f.Context.CompleteLearningAsync(job.Id,job.Token,[new("style",dimension,"brief","偏好简洁回答","project","",job.Evidence.Select(e=>e.Evidence.Id).ToArray())],f.Home);
    }
    private sealed class LearningProvider:HttpMessageHandler
    {
        internal int Calls;internal string Body="";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {
            Calls++;Body=await request.Content!.ReadAsStringAsync(ct);
            using var outer=JsonDocument.Parse(Body);using var input=JsonDocument.Parse(outer.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
            var ids=input.RootElement.GetProperty("evidence").EnumerateArray().Select(e=>e.GetProperty("id").GetString()!).ToArray();
            var content=JsonSerializer.Serialize(new{proposals=new[]{new MemoryProposal("style","verbosity","brief","偏好简洁表达","project","",ids)}});
            return new(System.Net.HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{content}}}}))};
        }
    }
    private sealed class SlotObserver:System.Diagnostics.Tracing.EventListener
    {
        internal readonly TaskCompletionSource Observed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override void OnEventSourceCreated(System.Diagnostics.Tracing.EventSource source){if(source.Name=="Amifolio-Storage")EnableEvents(source,System.Diagnostics.Tracing.EventLevel.Informational);}
        protected override void OnEventWritten(System.Diagnostics.Tracing.EventWrittenEventArgs e){if(e.Payload?.FirstOrDefault() as string=="learning-await-model-slot")Observed?.TrySetResult();}
    }
}
