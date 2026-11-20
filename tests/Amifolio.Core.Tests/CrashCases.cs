using System.Diagnostics;
using System.Diagnostics.Tracing;
using Amifolio.Core;

internal static class CrashCases
{
    internal static async Task<int> Worker(string[] args)
    {
        var store = new PortfolioStore(args[1]); await store.InitializeAsync();
        using var pause = new PauseAtCheckpoint(args[3], args[4]);
        await store.ImportFileAsync(args[2]); return 0;
    }
    internal static readonly (string Name, Func<Fixture, Task> Run)[] All = new[] { "import-copy", "extract", "object-published", "db-committed" }
        .Select(stage => ($"stage2 process kill at {stage} leaves recoverable idempotent storage", (Func<Fixture, Task>)(async f => {
            var source=f.File("crash.md","durable crash content");var marker=Path.Combine(f.Root,"checkpoint");
            var start=new ProcessStartInfo("dotnet"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardError=true,RedirectStandardOutput=true};
            foreach(var arg in new[]{typeof(CrashCases).Assembly.Location,"--stage2-crash",f.DataRoot,source,stage,marker})start.ArgumentList.Add(arg);
            using var child=Process.Start(start)!;
            try
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while(!System.IO.File.Exists(marker)){if(child.HasExited)throw new Exception("crash worker exited: "+await child.StandardError.ReadToEndAsync());await Task.Delay(10,timeout.Token);}
                child.Kill(true);await child.WaitForExitAsync();
                var restart=f.Restart();var before=await restart.ListAsync();
                if(before.Count!=(stage is "db-committed" or "extract"?1:0))throw new Exception("partial import acknowledged or committed data lost");
                if(stage=="extract"){await ((PortfolioStore)restart).ResumePendingAsync();if(!(await restart.ListAsync()).Single().Preview.Contains("durable crash content"))throw new Exception("pending extraction did not resume");}
                var retried=await restart.ImportFileAsync(source);var after=await restart.ListAsync();
                if(after.Count!=1 || retried.Artifact.Version!=1 || await System.IO.File.ReadAllTextAsync(retried.Artifact.ObjectPath)!="durable crash content")throw new Exception("restart lost bytes or duplicated version");
            }
            finally {if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}
        }))).ToArray();
}

internal sealed class PauseAtCheckpoint : EventListener
{
    private readonly string stage;
    private readonly string marker;
    internal PauseAtCheckpoint(string stage,string marker){this.stage=stage;this.marker=marker;EnableEvents(StorageEvents.Log,EventLevel.Informational);}
    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if(e.EventId!=1 || e.Payload?[0] as string!=stage)return;
        using(var stream=new FileStream(marker,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){stream.WriteByte(1);stream.Flush(true);}
        using var gate=new ManualResetEventSlim();gate.Wait();
    }
}
