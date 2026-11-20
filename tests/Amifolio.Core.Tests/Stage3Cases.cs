using System.Reflection;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Amifolio.Core;

internal static class Stage3Cases
{
    internal static readonly (string Name,Func<Fixture,Task> Run)[] All =
    [
        ("stage3 abandoned unclaimed model queue becomes interrupted",async f=>{
            var tasks=new TaskStore(f.DataRoot);var id=await tasks.EnqueueGenerationAsync(null,new string('b',64),0);await tasks.RecoverAsync();
            Check((await tasks.GetAsync(id))?.State=="interrupted","unclaimed generation remained stuck");
        }),
        ("stage3 cancelling an unclaimed waiter cannot cancel another owner",async f=>{
            var a=(await f.Store.ImportFileAsync(f.File("waiter.md","work"))).Artifact;var tasks=new TaskStore(f.DataRoot);var id=await tasks.EnqueueExtractionAsync(a.Id);
            await using var slot=await StorageLease.ResourceAsync(f.DataRoot,"parser",CancellationToken.None);using var ct=new CancellationTokenSource();
            var waiting=((PortfolioStore)f.Store).ProcessTaskAsync(id,ct.Token);await Task.Delay(100);using var other=await tasks.TryClaimAsync(id);Check(other is not null,"other owner missing");ct.Cancel();try{await waiting;}catch(OperationCanceledException){}
            Check((await tasks.GetAsync(id))?.State=="running","waiter cancelled another owner");
        }),
        ("stage3 locked source leaves no partial archive or task",async f=>{
            var source=f.File("locked-source.md","locked source");using(var held=new FileStream(source,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){
                try{await f.Store.ImportFileAsync(source);throw new Exception("locked file imported");}catch(IOException){}
            }
            Check((await f.Store.ListAsync()).Count==0&&(await new TaskStore(f.DataRoot).ListAsync()).Count==0,"locked source was acknowledged");
            Check((await f.Store.ImportFileAsync(source)).Artifact.Version==1,"retry after unlock failed");
        }),
        ("stage3 denied source read preserves source and queue",async f=>{
            if(!OperatingSystem.IsWindows())return;
            var source=f.File("denied-source.md","access protected");var file=new FileInfo(source);var before=file.GetAccessControl();var denied=file.GetAccessControl();
            var rule=new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,FileSystemRights.ReadData,AccessControlType.Deny);denied.AddAccessRule(rule);file.SetAccessControl(denied);
            try{try{await f.Store.ImportFileAsync(source);throw new Exception("denied source imported");}catch(UnauthorizedAccessException){}}
            finally{denied.RemoveAccessRuleSpecific(rule);file.SetAccessControl(denied);}
            Check((await f.Store.ListAsync()).Count==0&&(await new TaskStore(f.DataRoot).ListAsync()).Count==0,"denied source acknowledged");
            Check(await System.IO.File.ReadAllTextAsync(source)=="access protected","source damaged");
        }),
        ("stage3 v1 migration retains originals and creates task schema",async f=>{
            var artifact=(await f.Store.ImportFileAsync(f.File("v1.md","v1 original"))).Artifact;
            using(var db=new Microsoft.Data.Sqlite.SqliteConnection("Data Source="+Path.Combine(f.DataRoot,"portfolio.db"))){db.Open();using var c=db.CreateCommand();c.CommandText="DROP TABLE work_items;PRAGMA user_version=1";c.ExecuteNonQuery();}
            await f.Restart().InitializeAsync();Check((await new TaskStore(f.DataRoot).ListAsync()).Count==0,"legacy tasks invented");
            Check(await System.IO.File.ReadAllTextAsync(artifact.ObjectPath)=="v1 original"&&Directory.GetFiles(Path.Combine(f.DataRoot,"recovery"),"migration-*.db").Length>0,"migration lost original or restore point");
        }),
        ("stage3 backup retains pending tasks without model replay",async f=>{
            var artifact=(await f.Store.ImportFileAsync(f.File("backup-task.md","restore work"))).Artifact;var tasks=new TaskStore(f.DataRoot);var extract=await tasks.EnqueueExtractionAsync(artifact.Id);var model=await tasks.EnqueueGenerationAsync(null,new string('a',64),0);
            using(var lease=await tasks.TryClaimAsync(model))Check(lease is not null,"model claim absent");
            var zip=Path.Combine(f.Root,"task-backup.zip");await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);var restored=Path.Combine(f.Root,"restored-tasks");await new PortfolioMaintenance(restored).RestoreAsync(zip);
            await new PortfolioStore(restored).ResumePendingAsync();var current=new TaskStore(restored);
            Check((await current.GetAsync(extract))?.State=="completed","restored extraction did not resume");Check((await current.GetAsync(model))?.State=="interrupted","restored model automatically resumed");
        }),
        ("stage3 separate processes cannot claim the same task",async f=>{
            var artifact=(await f.Store.ImportFileAsync(f.File("process-claim.md","evidence"))).Artifact;var tasks=new TaskStore(f.DataRoot);var id=await tasks.EnqueueExtractionAsync(artifact.Id);var marker=Path.Combine(f.Root,"claim-marker");
            using var first=Start("--claim-task",f.DataRoot,id,marker);try{
                await Marker(first,marker);using var second=Start("--claim-task",f.DataRoot,id,marker+"-2");await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));Check(second.ExitCode==5,"second process claimed task");
                first.Kill();await first.WaitForExitAsync();await tasks.RecoverAsync();Check((await tasks.GetAsync(id))?.State=="queued","orphan did not recover");
            }finally{if(!first.HasExited){first.Kill(true);await first.WaitForExitAsync();}}
        }),
        ("stage3 recovery rechecks a briefly busy orphan lock",async f=>{
            var artifact=(await f.Store.ImportFileAsync(f.File("recover.md","orphan"))).Artifact;
            var tasks=new TaskStore(f.DataRoot);var id=await tasks.EnqueueExtractionAsync(artifact.Id);
            using var owner=await tasks.TryClaimAsync(id);Check(owner is not null,"claim missing");
            await tasks.RecoverAsync();Check((await tasks.GetAsync(id))?.State=="running","active claim was reclaimed");
            StorageFaults.Handler.Value=step=>{if(step=="task-recovery-lock-busy")owner!.Dispose();};
            try { await tasks.RecoverAsync(); }
            finally { StorageFaults.Handler.Value=null; }
            Check((await tasks.GetAsync(id))?.State=="queued","briefly busy orphan remained running");
        }),
        ("stage3 parser exits when only its parent is killed",async f=>{
            var source=f.File("parent.md","owned child");var marker=Path.Combine(f.Root,"child-pid");using var parent=Start("--parent-worker",f.DataRoot,source,marker);Process? child=null;
            try{await Marker(parent,marker);child=Process.GetProcessById(int.Parse(await System.IO.File.ReadAllTextAsync(marker)));parent.Kill();await parent.WaitForExitAsync();await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));Check(child.HasExited,"parser remained after parent exit");}
            finally{if(!parent.HasExited){parent.Kill(true);await parent.WaitForExitAsync();}if(child is not null){if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}child.Dispose();}}
        }),
        ("stage3 post-commit cancellation retains receipt and original",async f=>{
            using var cts=new CancellationTokenSource();StorageFaults.Handler.Value=stage=>{if(stage=="extract")cts.Cancel();};
            ImportResult result;try{result=await f.Store.ImportFileAsync(f.File("after-commit.md","committed"),cancellationToken:cts.Token);}finally{StorageFaults.Handler.Value=null;}
            Check(await System.IO.File.ReadAllTextAsync(result.Artifact.ObjectPath)=="committed","committed original lost");
            Check((await new TaskStore(f.DataRoot).ListAsync()).Single().State=="cancelled","analysis cancellation not durable");
            Check(result.Artifact.ExtractionStatus=="已取消","artifact still appears queued");
        }),
        ("stage3 disk-full fault before commit has no false archive success",async f=>{
            StorageFaults.Handler.Value=stage=>{if(stage=="object-published")throw new IOException("Synthetic disk full",112);};
            try{await f.Store.ImportFileAsync(f.File("disk-full.md","durable source"));throw new Exception("fault not injected");}catch(IOException){}finally{StorageFaults.Handler.Value=null;}
            Check((await f.Store.ListAsync()).Count==0&&(await new TaskStore(f.DataRoot).ListAsync()).Count==0,"partial commit acknowledged");
            Check((await f.Store.ImportFileAsync(f.File("disk-full.md","durable source"))).Artifact.Version==1,"failed import consumed version");
        }),
        ("stage3 post-commit IO fault is visible and keeps original",async f=>{
            StorageFaults.Handler.Value=stage=>{if(stage=="extract")throw new IOException("Synthetic disk full",112);};
            ImportResult result;try{result=await f.Store.ImportFileAsync(f.File("analysis-io.md","saved"));}finally{StorageFaults.Handler.Value=null;}
            var task=(await new TaskStore(f.DataRoot).ListAsync()).Single();Check(task.State=="failed"&&task.ErrorCode=="io_error","IO failure not visible");
            Check(result.Artifact.ExtractionStatus=="提取失败"&&await System.IO.File.ReadAllTextAsync(result.Artifact.ObjectPath)=="saved","archive state incorrect");
            await ((PortfolioStore)f.Store).ResumePendingAsync();Check((await new TaskStore(f.DataRoot).ListAsync()).Single().Attempts==1,"failed task auto retried");
        }),
        ("stage3 moved source rejects old extraction completion",async f=>{
            var a=(await f.Store.ImportFileAsync(f.File("moved.md","original"))).Artifact;var tasks=new TaskStore(f.DataRoot);var id=await tasks.EnqueueExtractionAsync(a.Id);using var lease=await tasks.TryClaimAsync(id);
            var project=await f.Store.CreateProjectAsync("目标");await f.Store.UpdateArtifactAsync(a.Id,a.Title,"",project.Id);
            Check(!await tasks.CompleteExtractionAsync(lease!,new("笔记","已提取","STALE"),CancellationToken.None),"stale completion accepted");
            Check((await tasks.GetAsync(id))?.State=="cancelled","old task not cancelled");
        }),
        ("stage3 import creates completed extraction receipt",async f=>{
            var imported=await f.Store.ImportFileAsync(f.File("receipt.md","separate worker content"));
            var jobs=await new TaskStore(f.DataRoot).ListAsync();
            Check(jobs.Any(x=>x.ArtifactId==imported.Artifact.Id&&x.State=="completed"),"archive has no extraction receipt");
            Check(imported.Artifact.Preview.Contains("separate worker content"),"worker result missing");
        }),
        ("stage3 worker protocol extracts outside caller",async f=>{
            var a=(await f.Store.ImportFileAsync(f.File("worker.md","worker-process-evidence"))).Artifact;
            var result=await new ExtractionProcess{ObserveExit=(code,error)=>Console.WriteLine($"Synthetic worker exit={code}; {error}")}.RunAsync(a,CancellationToken.None);
            Check(result.Preview.Contains("worker-process-evidence"),"isolated worker failed");
        }),
        ("stage3 worker timeout is bounded",async f=>{
            var a=(await f.Store.ImportFileAsync(f.File("timeout.md","timeout"))).Artifact;
            try{await new ExtractionProcess(["dotnet",typeof(Stage3Cases).Assembly.Location,"--worker-fixture","hang"],TimeSpan.FromSeconds(2)).RunAsync(a,CancellationToken.None);throw new Exception("timeout missing");}
            catch(IOException e){Check(e.Message=="worker_timeout","wrong timeout classification: "+e.Message);}
        }),
        ("stage3 worker crash preserves original",async f=>{
            var a=(await f.Store.ImportFileAsync(f.File("crash-worker.md","preserved"))).Artifact;
            try{await new ExtractionProcess(["dotnet",typeof(Stage3Cases).Assembly.Location,"--worker-fixture","crash"]).RunAsync(a,CancellationToken.None);throw new Exception("crash missing");}
            catch(IOException e){Check(e.Message=="worker_crash","wrong crash classification: "+e.Message);}
            Check(await System.IO.File.ReadAllTextAsync(await f.Store.GetOriginalPathAsync(a.Id))=="preserved","original lost");
        }),
        ("stage3 worker rejects oversized response",async f=>{
            var a=(await f.Store.ImportFileAsync(f.File("overflow.md","bounded"))).Artifact;
            try{await new ExtractionProcess(["dotnet",typeof(Stage3Cases).Assembly.Location,"--worker-fixture","overflow"]).RunAsync(a,CancellationToken.None);throw new Exception("oversize accepted");}
            catch(IOException e){Check(e.Message=="worker_protocol","wrong protocol classification: "+e.Message);}
        }),
        ("stage3 queued task persists with no artifact contents",async f=>{
            var item=await f.Store.ImportFileAsync(f.File("queued.md","PRIVATE_ARTIFACT_BODY"));var id=(string)(await Call(f.DataRoot,"EnqueueExtractionAsync",item.Artifact.Id,CancellationToken.None))!;
            var tasks=await List(f.DataRoot);Check(tasks.EnumerateArray().Any(x=>x.GetProperty("Id").GetString()==id),"queued task lost");Check(!tasks.GetRawText().Contains("PRIVATE_ARTIFACT_BODY"),"task contains content");
        }),
        ("stage3 concurrent claim has one owner",async f=>{
            var item=await f.Store.ImportFileAsync(f.File("claim.md","claim"));var id=(string)(await Call(f.DataRoot,"EnqueueExtractionAsync",item.Artifact.Id,CancellationToken.None))!;
            using var owner=(IDisposable)(await Call(f.DataRoot,"TryClaimAsync",id,CancellationToken.None))!;Check(owner is not null,"claim failed");
            Check(await Call(f.DataRoot,"TryClaimAsync",id,CancellationToken.None) is null,"second owner accepted");
        }),
        ("stage3 orphan extraction resumes only within bounded attempts",async f=>{
            var item=await f.Store.ImportFileAsync(f.File("resume.md","resume"));var id=(string)(await Call(f.DataRoot,"EnqueueExtractionAsync",item.Artifact.Id,CancellationToken.None))!;
            for(var n=0;n<2;n++){using(var owner=(IDisposable)(await Call(f.DataRoot,"TryClaimAsync",id,CancellationToken.None))!){Check(owner is not null,"missing owner");}await Call(f.DataRoot,"RecoverAsync",CancellationToken.None);}
            var row=(await List(f.DataRoot)).EnumerateArray().Single(x=>x.GetProperty("Id").GetString()==id);Check(row.GetProperty("State").GetString()=="failed","unbounded automatic recovery");
        }),
        ("stage3 cancel is durable and stops future claims",async f=>{
            var item=await f.Store.ImportFileAsync(f.File("cancel-task.md","cancel"));var id=(string)(await Call(f.DataRoot,"EnqueueExtractionAsync",item.Artifact.Id,CancellationToken.None))!;
            await Call(f.DataRoot,"CancelAsync",id,CancellationToken.None);await Call(f.DataRoot,"RecoverAsync",CancellationToken.None);
            Check(await Call(f.DataRoot,"TryClaimAsync",id,CancellationToken.None) is null,"cancelled task reclaimed");
            var row=(await List(f.DataRoot)).EnumerateArray().Single(x=>x.GetProperty("Id").GetString()==id);Check(row.GetProperty("State").GetString()=="cancelled","cancel state lost");
        }),
        ("stage3 source deletion cancels extraction task",async f=>{
            var item=await f.Store.ImportFileAsync(f.File("remove-task.md","remove"));var id=(string)(await Call(f.DataRoot,"EnqueueExtractionAsync",item.Artifact.Id,CancellationToken.None))!;await f.Store.RemoveArtifactAsync(item.Artifact.Id);
            Check(await Call(f.DataRoot,"TryClaimAsync",id,CancellationToken.None) is null,"deleted source task claimed");
        }),
        ("stage3 generation orphan never automatically sends again",async f=>{
            var id=(string)(await Call(f.DataRoot,"EnqueueGenerationAsync",null!,new string('a',64),0L,CancellationToken.None))!;
            using(var owner=(IDisposable)(await Call(f.DataRoot,"TryClaimAsync",id,CancellationToken.None))!){Check(owner is not null,"generation claim missing");}
            await Call(f.DataRoot,"RecoverAsync",CancellationToken.None);var row=(await List(f.DataRoot)).EnumerateArray().Single(x=>x.GetProperty("Id").GetString()==id);
            Check(row.GetProperty("State").GetString()=="interrupted","generation auto-requeued");
        }),
    ];
    private static object Store(string root)=>Activator.CreateInstance(typeof(PortfolioStore).Assembly.GetType("Amifolio.Core.TaskStore")??throw new Exception("durable task store missing"),root)!;
    private static Process Start(params string[] args){var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};start.ArgumentList.Add(typeof(Stage3Cases).Assembly.Location);foreach(var arg in args)start.ArgumentList.Add(arg);return Process.Start(start)!;}
    private static async Task Marker(Process child,string marker){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(!System.IO.File.Exists(marker)){if(child.HasExited)throw new Exception("fixture exited: "+await child.StandardError.ReadToEndAsync());await Task.Delay(10,timeout.Token);}}
    private static async Task<object?> Call(string root,string method,params object[] args){var store=Store(root);try{var task=(Task)store.GetType().GetMethod(method)!.Invoke(store,args)!;await task;return task.GetType().GetProperty("Result")?.GetValue(task);}catch(TargetInvocationException e){throw e.InnerException!;}}
    private static async Task<JsonElement> List(string root)=>JsonSerializer.SerializeToElement(await Call(root,"ListAsync",null!,CancellationToken.None));
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
}
