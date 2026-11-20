using System.Net;
using System.Text;
using Amifolio.Core;
using Amifolio.Context;

internal static class TaskCases
{
    internal static readonly (string Name,Func<Task> Run)[] All=
    [
        ("stage3 saved receipt repairs a transient completion write failure",async()=>{
            using var f=new Fixture();await f.Ready();var once=true;StorageFaults.Handler.Value=stage=>{if(stage=="generation-complete"&&once){once=false;throw new IOException("synthetic completion IO failure");}};
            try{await f.Service.GenerateAsync(null,f.Options,"dummy");}finally{StorageFaults.Handler.Value=null;}
            var jobs=new TaskStore(f.Root);var item=(await jobs.ListAsync()).Single(x=>x.Kind=="generate");Check(item.State=="completed"&&f.Handler.Calls==1,"receipt did not repair completion");
            await using(var root=await StorageLease.ExclusiveAsync(f.Root)){using var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(f.Root,"portfolio.db")+";Pooling=false");db.Open();using var c=db.CreateCommand();c.CommandText="UPDATE work_items SET state='failed' WHERE kind='generate'";c.ExecuteNonQuery();}
            await f.Service.RetryGenerationAsync(item.Id,f.Options,"dummy");Check((await jobs.GetAsync(item.Id))?.State=="completed"&&f.Handler.Calls==1,"failed receipt was resent or not repaired");
        }),
        ("stage3 remote revocation stops queued generation before dispatch",async()=>{
            using var f=new Fixture();await f.Ready();var jobs=new TaskStore(f.Root);await jobs.SetRemoteAllowedAsync(true);await using var slot=await jobs.ModelSlotAsync();
            var request=f.Service.GenerateAsync(null,f.Options,"dummy");using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)))while(!(await jobs.ListAsync()).Any(x=>x.Kind=="generate"&&x.State=="queued"))await Task.Delay(10,timeout.Token);
            await jobs.RecoverAsync();Check((await jobs.ListAsync()).Single(x=>x.Kind=="generate").State=="queued","live queue was mistaken for an orphan");await jobs.SetRemoteAllowedAsync(false);
            try{await request.WaitAsync(TimeSpan.FromSeconds(5));throw new Exception("revoked request accepted");}catch(OperationCanceledException){}
            Check(f.Handler.Calls==0&&(await jobs.ListAsync()).Single(x=>x.Kind=="generate").ErrorCode=="permission_revoked","revoked queue sent or reason lost");
        }),
        ("stage3 durable cancellation fences candidate commit before watcher fires",async()=>{
            using var f=new Fixture();await f.Ready();f.Handler.OnRespond=async()=>{
                await using var root=await StorageLease.ExclusiveAsync(f.Root);using var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(f.Root,"portfolio.db")+";Pooling=false");db.Open();using var c=db.CreateCommand();c.CommandText="UPDATE work_items SET state='cancelled',error_code='cancelled',owner=NULL WHERE kind='generate'";c.ExecuteNonQuery();
            };
            var rejected=false;try{await f.Service.GenerateAsync(null,f.Options,"dummy");}catch(OperationCanceledException){rejected=true;}
            Check(rejected&&(await f.Service.ReadAsync()).CandidateDocuments.Count==0,"durably cancelled result was saved");
        }),
        ("stage3 one provider request per library while other scopes stay queued",async()=>{
            using var f=new Fixture();await f.Ready();var project=await f.Store.CreateProjectAsync("虚构项目");var source=Path.Combine(f.Base,"project.md");await File.WriteAllTextAsync(source,"项目合成证据");await f.Store.ImportFileAsync(source,project.Id);await f.Service.SetEnabledAsync(project.Id,true);
            f.Handler.Block=true;var first=f.Service.GenerateAsync(null,f.Options,"dummy");await f.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second=new ContextService(new PortfolioStore(f.Root),f.Client).GenerateAsync(project.Id,f.Options,"dummy");
            await Task.Delay(350);Check(f.Handler.Calls==1,"provider requests overlap");Check((await new TaskStore(f.Root).ListAsync()).Any(x=>x.Kind=="generate"&&x.State=="queued"),"waiting request not visible");
            Check((await f.Store.ListAsync()).Count==2,"read blocked during HTTP");f.Handler.Release.TrySetResult();await Task.WhenAll(first,second);
            Check(f.Handler.Calls==2,"queued request did not run");
        }),
        ("stage3 retry rejects changed inputs without another provider request",async()=>{
            using var f=new Fixture();await f.Ready();f.Handler.Fail=true;try{await f.Service.GenerateAsync(null,f.Options,"dummy");}catch(HttpRequestException){}
            var task=(await new TaskStore(f.Root).ListAsync()).Single(x=>x.Kind=="generate");var artifact=(await f.Store.ListAsync()).Single();await f.Store.UpdateArtifactAsync(artifact.Id,"changed-title","",null);
            try{await f.Service.RetryGenerationAsync(task.Id,f.Options,"dummy");throw new Exception("changed retry accepted");}catch(InvalidOperationException){}
            Check(f.Handler.Calls==1,"changed retry sent HTTP");
        }),
        ("stage3 real generation has durable completed receipt and no credentials",async()=>{
            using var f=new Fixture();await f.Ready();await f.Service.GenerateAsync(null,f.Options,"SYNTHETIC_KEY");
            var jobs=await new TaskStore(f.Root).ListAsync();Check(jobs.Any(x=>x.Kind=="generate"&&x.State=="completed"),"generation receipt missing");
            Check((await f.Service.ReadAsync()).CandidateDocuments.Count>0,"candidate missing");
            Check(!Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(f.Root,"portfolio.db"))).Contains("SYNTHETIC_KEY"),"key in database");
        }),
        ("stage3 provider failure is visible and never auto-retried",async()=>{
            using var f=new Fixture();await f.Ready();f.Handler.Fail=true;
            try{await f.Service.GenerateAsync(null,f.Options,"dummy");throw new Exception("failure missing");}catch(HttpRequestException){}
            await new PortfolioStore(f.Root).ResumePendingAsync();
            Check(f.Handler.Calls==1,"unexpected automatic model retry");
            Check((await new TaskStore(f.Root).ListAsync()).Any(x=>x.Kind=="generate"&&x.State=="failed"),"failure not recorded");
        }),
        ("stage3 cancel generation from another client prevents candidate",async()=>{
            using var f=new Fixture();await f.Ready();f.Handler.Block=true;var request=f.Service.GenerateAsync(null,f.Options,"dummy");
            await f.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));var jobs=new TaskStore(f.Root);var job=(await jobs.ListAsync()).Single(x=>x.Kind=="generate");
            await jobs.CancelAsync(job.Id);f.Handler.Release.TrySetResult();try{await request;throw new Exception("cancel ignored");}catch(OperationCanceledException){}
            Check((await f.Service.ReadAsync()).CandidateDocuments.Count==0,"cancelled result adopted");
        }),
    ];
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private sealed class Fixture:IDisposable
    {
        internal readonly string Base=Path.Combine(Path.GetTempPath(),"amifolio-stage3-context-"+Guid.NewGuid().ToString("N"));
        internal string Root=>Path.Combine(Base,"data");internal readonly Handler Handler=new();private readonly HttpClient client;
        internal readonly PortfolioStore Store;internal readonly ContextService Service;internal HttpClient Client=>client;internal ModelOptions Options=>new("https://example.com/v1","synthetic",true);
        internal Fixture(){Directory.CreateDirectory(Base);Store=new(Root);client=new(Handler);Service=new(Store,client);}
        internal async Task Ready(){var source=Path.Combine(Base,"synthetic.md");await File.WriteAllTextAsync(source,"用户明确要求：文案简洁。虚构测试项目，不含个人资料。");await Store.ImportFileAsync(source);await Service.SetEnabledAsync(null,true);}
        public void Dispose(){client.Dispose();foreach(var file in Directory.GetFiles(Base,"*",SearchOption.AllDirectories))File.SetAttributes(file,FileAttributes.Normal);Directory.Delete(Base,true);}
    }
    private sealed class Handler:HttpMessageHandler
    {
        internal bool Fail,Block;internal int Calls;internal Func<Task>? OnRespond;internal TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously),Release=new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Calls++;Started.TrySetResult();if(Block)await Release.Task.WaitAsync(ct);if(OnRespond is not null)await OnRespond();return new(Fail?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK){Content=new StringContent("{\"choices\":[{\"message\":{\"content\":\"{\\\"summary.md\\\":\\\"Synthetic summary\\\"}\"}}]}")};}
    }
}
