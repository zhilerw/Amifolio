using System.Diagnostics;
using System.Diagnostics.Tracing;
using Amifolio.Context;
using Amifolio.Core;

internal static class StorageFaultCases
{
    internal static async Task<int> Worker(string[] args)
    {
        var store=new PortfolioStore(args[1]);await store.InitializeAsync();await store.ImportFileAsync(args[2]);
        using var handler=new ProviderHandler();using var client=new HttpClient(handler);var service=new ContextService(store,client);
        await service.SetEnabledAsync(null,true);await service.SaveDocumentAsync(null,"memory.md","MANUAL_KEEP");
        using var pause=new CheckpointPause(args[3],args[4]);
        if(args[3] is "candidate-ready" or "candidate-saved")await service.GenerateAsync(null,new("https://example.com/v1","test",true),"dummy");
        else if(args[3]=="revocation-committed")await store.RemoveArtifactAsync((await store.ListAsync())[0].Id);
        else await service.SaveDocumentAsync(null,"memory.md","MANUAL_AFTER");
        return 0;
    }

    internal static readonly (string Name,Func<Task> Run)[] All =
    new[]{"candidate-ready","candidate-saved","pointer-prepared","pointer-published","revocation-committed"}.Select(stage=>($"stage2 context process kill at {stage} recovers safely",(Func<Task>)(async()=>{
        using var f=new DataFixture();var marker=Path.Combine(f.Base,"checkpoint");
        var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
        foreach(var arg in new[]{typeof(StorageFaultCases).Assembly.Location,"--stage2-crash",f.Root,f.Source,stage,marker})start.ArgumentList.Add(arg);
        using var child=Process.Start(start)!;
        try
        {
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while(!File.Exists(marker)){if(child.HasExited)throw new Exception("fault worker failed: "+await child.StandardError.ReadToEndAsync());await Task.Delay(10,timeout.Token);}
            child.Kill(true);await child.WaitForExitAsync();
            var service=new ContextService(new PortfolioStore(f.Root));var snapshot=await service.ReadAsync();
            Check((snapshot.CandidateDocuments.Count>0)==(stage=="candidate-saved"),"interrupted generation candidate receipt mismatch");
            if(stage is "candidate-ready" or "candidate-saved")
            {
                await new TaskStore(f.Root).RecoverAsync();using var h=new ProviderHandler();using var c=new HttpClient(h);var recovery=new ContextService(new PortfolioStore(f.Root),c);
                await recovery.ReconcileGenerationTasksAsync();Check(h.Calls==0,"recovery resent a model request");
                var task=(await new TaskStore(f.Root).ListAsync()).Single(x=>x.Kind=="generate");Check(task.State==(stage=="candidate-saved"?"completed":"interrupted"),"recovery receipt state wrong");
            }
            Check(snapshot.Documents["memory.md"]==(stage.StartsWith("pointer-")?"MANUAL_AFTER":"MANUAL_KEEP"),"manual edit lost");
            if(stage=="revocation-committed")Check((await new PortfolioStore(f.Root).ListAsync()).Count==0,"removed source revived");
            else
            {
                var pointer=await File.ReadAllTextAsync(Path.Combine(snapshot.Directory,"START.md"));
                foreach(System.Text.RegularExpressions.Match link in System.Text.RegularExpressions.Regex.Matches(pointer,@"\]\((generations/[^)]+)\)"))
                    Check(File.Exists(Path.Combine(snapshot.Directory,link.Groups[1].Value.Replace('/',Path.DirectorySeparatorChar))),"published pointer references missing content");
            }
        }
        finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}
    }))).Concat(new (string,Func<Task>)[]{
        ("stage2 real store deletion discards in-flight generation and retains manual memory",()=>LateResult(false)),
        ("stage2 moving project discards in-flight global generation",()=>LateResult(true)),
        ("stage2 revocation retains direct human edits before any context read",async()=>{
            using var f=new DataFixture();var store=new PortfolioStore(f.Root);var artifact=await store.ImportFileAsync(f.Source);var service=new ContextService(store);
            await service.SetEnabledAsync(null,true);await service.SaveDocumentAsync(null,"memory.md","BEFORE");var snapshot=await service.ReadAsync();
            var start=await File.ReadAllTextAsync(Path.Combine(snapshot.Directory,"START.md"));var match=System.Text.RegularExpressions.Regex.Match(start,@"\[memory.md\]\(([^)]+)\)");
            await File.WriteAllTextAsync(Path.Combine(snapshot.Directory,match.Groups[1].Value.Replace('/',Path.DirectorySeparatorChar)),"DIRECT_EDIT_MUST_SURVIVE");
            await store.RemoveArtifactAsync(artifact.Artifact.Id);
            Check(!Directory.Exists(snapshot.Directory),"old publication not withdrawn");
            var current=await new ContextService(new PortfolioStore(f.Root)).ReadAsync();Check(current.Documents["memory.md"]=="DIRECT_EDIT_MUST_SURVIVE","direct manual edit lost during revocation");
        }),
        ("stage2 backup preserves direct human edits to published Markdown",async()=>{
            using var f=new DataFixture();var store=new PortfolioStore(f.Root);await store.ImportFileAsync(f.Source);var service=new ContextService(store);
            await service.SetEnabledAsync(null,true);await service.SaveDocumentAsync(null,"memory.md","BEFORE");var snapshot=await service.ReadAsync();
            var start=await File.ReadAllTextAsync(Path.Combine(snapshot.Directory,"START.md"));
            var match=System.Text.RegularExpressions.Regex.Match(start,@"\[memory.md\]\(([^)]+)\)");
            await File.WriteAllTextAsync(Path.Combine(snapshot.Directory,match.Groups[1].Value.Replace('/',Path.DirectorySeparatorChar)),"EXTERNAL_HUMAN_EDIT");
            var zip=Path.Combine(f.Base,"backup.zip");await new PortfolioMaintenance(f.Root).BackupAsync(zip);
            var target=Path.Combine(f.Base,"restored");await new PortfolioMaintenance(target).RestoreAsync(zip);
            var restored=await new ContextService(new PortfolioStore(target)).ReadAsync();
            Check(restored.Documents["memory.md"]=="EXTERNAL_HUMAN_EDIT"&&!restored.Enabled,"external edit lost or restore shared it");
        }),
    }).ToArray();

    private static async Task LateResult(bool move)
    {
        using var f=new DataFixture();var store=new PortfolioStore(f.Root);var item=await store.ImportFileAsync(f.Source);
        var project=await store.CreateProjectAsync("目标项目");using var handler=new ProviderHandler{Block=true};using var client=new HttpClient(handler);var service=new ContextService(store,client);
        await service.SetEnabledAsync(null,true);await service.SaveDocumentAsync(null,"memory.md","MANUAL_KEEP");
        var request=service.GenerateAsync(null,new("https://example.com/v1","test",true),"dummy");await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if(move)await store.UpdateArtifactAsync(item.Artifact.Id,item.Artifact.Title,"",project.Id);else await store.RemoveArtifactAsync(item.Artifact.Id);
        Check(!Directory.Exists(Path.Combine(f.Root,"context","global")),"stale export survived mutation");handler.Release.TrySetResult();
        var rejected=false;try{await request;}catch(Exception e)when(e is InvalidOperationException or OperationCanceledException){rejected=true;}Check(rejected,"late candidate accepted");
        var current=await service.ReadAsync();Check(current.CandidateDocuments.Count==0 && current.Documents["memory.md"]=="MANUAL_KEEP","candidate revived/manual lost");
    }
    private static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
    private sealed class DataFixture:IDisposable
    {
        internal string Base=Path.Combine(Path.GetTempPath(),"amifolio-stage2-context-"+Guid.NewGuid().ToString("N"));
        internal string Root=>Path.Combine(Base,"data");internal string Source=>Path.Combine(Base,"source.md");
        internal DataFixture(){Directory.CreateDirectory(Base);File.WriteAllText(Source,"source evidence");}
        public void Dispose(){if(!Directory.Exists(Base))return;foreach(var path in Directory.GetFiles(Base,"*",SearchOption.AllDirectories))File.SetAttributes(path,FileAttributes.Normal);Directory.Delete(Base,true);}
    }
    private sealed class CheckpointPause:EventListener
    {
        private readonly string stage,marker;
        internal CheckpointPause(string stage,string marker){this.stage=stage;this.marker=marker;EnableEvents(StorageEvents.Log,EventLevel.Informational);}
        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if(e.EventId!=1 || e.Payload?[0] as string!=stage)return;
            using(var stream=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){stream.WriteByte(1);stream.Flush(true);}
            using var gate=new ManualResetEventSlim();gate.Wait();
        }
    }
}
