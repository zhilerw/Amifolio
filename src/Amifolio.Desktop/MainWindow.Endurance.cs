using Amifolio.Core;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private async Task<object> RunEnduranceProbeAsync(string reportPath)
    {
        if(!DispatcherQueue.HasThreadAccess)
        {
            var result=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
            if(!DispatcherQueue.TryEnqueue(async()=>{try{result.SetResult(await RunEnduranceProbeAsync(reportPath));}catch(Exception e){result.SetException(e);}}))throw new InvalidOperationException("Dispatcher unavailable.");
            return await result.Task.ConfigureAwait(false);
        }
        if(Option("--profile-stress")!="true"||Option("--profile-report") is null)throw new InvalidOperationException("Explicit isolated probe required.");
        var project=SourceStore.CodexProjects.Single();
        var entries=await SourceStore.Catalog.BrowseAsync(project,project.RootPath,backgroundTasks.Token);
        var source=entries.Items.FirstOrDefault(e=>!e.IsDirectory&&e.Name.EndsWith(".docx",StringComparison.OrdinalIgnoreCase))??entries.Items.First(e=>!e.IsDirectory&&e.Name.EndsWith(".md",StringComparison.OrdinalIgnoreCase));
        var reference=await SourceStore.ReferenceFileAsync(source.Path,project.Id,backgroundTasks.Token);
        var pagesToVisit=new[]{"library","projects","learning","connections","settings"};
        foreach(var page in pagesToVisit)await RequestNavigationAsync(page);
        await ShowBrowserAsync(project,project.RootPath);await RequestNavigationAsync("library");
        await Task.Delay(1000);var before=EnduranceMemory();
        var navigation=new List<double>();var memory=new List<object>();var parserLatency=new ConcurrentBag<double>();
        var overlaps=0;using var workers=new EnduranceWorkers();
        for(var cycle=0;cycle<12;cycle++)
        {
            foreach(var page in pagesToVisit)
            {
                var watch=Stopwatch.StartNew();
                if(!await RequestNavigationAsync(page))throw new InvalidOperationException("Synthetic navigation refused.");
                Root.UpdateLayout();navigation.Add(watch.Elapsed.TotalMilliseconds);
            }
            await RequestNavigationAsync("library");
            if(cycle%3==0)
            {
                var parse=Task.Run(()=>SourceStore.ParseReferenceAsync(reference.Artifact.Id,backgroundTasks.Token),backgroundTasks.Token);
                var observing=Task.Run(async()=>
                {
                    while(!parse.IsCompleted)
                    {
                        var queued=Stopwatch.StartNew();var delivered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                        if(!DispatcherQueue.TryEnqueue(()=>{parserLatency.Add(queued.Elapsed.TotalMilliseconds);delivered.SetResult();}))break;
                        await delivered.Task.ConfigureAwait(false);Interlocked.Increment(ref overlaps);await Task.Delay(10).ConfigureAwait(false);
                    }
                });
                var changing=Stopwatch.StartNew();await RequestNavigationAsync("settings");navigation.Add(changing.Elapsed.TotalMilliseconds);
                if(operation is not null)throw new InvalidOperationException("Background parsing took the UI operation slot.");
                await parse;await observing;
                if((await SourceStore.GetAsync(reference.Artifact.Id))?.Preview.Length is not>0)throw new InvalidOperationException("Synthetic parse did not produce a preview.");
            }
            memory.Add(new{cycle=cycle+1,usage=EnduranceMemory()});
        }
        await workers.DrainAsync();await RequestNavigationAsync("library");
        if(ArtifactList.Items.Count>0){ArtifactList.ScrollIntoView(ArtifactList.Items[^1]);Root.UpdateLayout();ArtifactList.ScrollIntoView(ArtifactList.Items[0]);}
        await Task.Delay(3000);var after=EnduranceMemory();
        if(workers.Observed.Count<4||workers.Observed.Values.Any(w=>!w.Exited||w.Samples==0))throw new InvalidOperationException("Owned parser sampling incomplete.");
        var nav=navigation.Order().ToArray();var latency=parserLatency.Order().ToArray();
        return new{cycles=12,pageLoads=navigation.Count,parserRuns=workers.Observed.Count,before,after,memory,
            navigationP95=nav[(int)Math.Ceiling(nav.Length*.95)-1],parserDispatcher=new{valid=overlaps>=10,samples=overlaps,p95=latency.Length==0?(double?)null:latency[(int)Math.Ceiling(latency.Length*.95)-1]},
            workers=workers.Observed.OrderBy(p=>p.Key).Select(p=>new{pid=p.Key,p.Value.Samples,p.Value.Exited,p.Value.PeakWorkingSetBytes,p.Value.PeakPrivateCommitBytes}).ToArray(),
            activeTransitions=motion?.ActiveCount,credentialsUsed=false,forcedGc=false,
            limitations="Synthetic programmatic navigation/scroll and real parsing; parser RSS/private commit peaks sampled every 10ms, not private working set. No physical input/frame pacing or multi-day acceptance claim."};
    }
    private static object EnduranceMemory()
    {
        using var self=Process.GetCurrentProcess();self.Refresh();
        return new{workingSetBytes=self.WorkingSet64,privateCommitBytes=self.PrivateMemorySize64,managedBytes=GC.GetTotalMemory(false),handles=self.HandleCount,threads=self.Threads.Count};
    }
    private sealed class EnduranceWorkers:EventListener
    {
        internal sealed class Sample
        {
            public int Samples;public bool Exited;public long PeakWorkingSetBytes,PeakPrivateCommitBytes;
        }
        internal readonly ConcurrentDictionary<int,Sample> Observed=new();
        private readonly ConcurrentBag<Task> pending=new();
        internal EnduranceWorkers(){EnableEvents(StorageEvents.Log,EventLevel.Informational);}
        protected override void OnEventWritten(EventWrittenEventArgs e)
        {
            if(e.EventId!=1||e.Payload?[0] is not string stage||!stage.StartsWith("worker-started:",StringComparison.Ordinal)||!int.TryParse(stage[15..],out var pid))return;
            var sample=new Sample();if(!Observed.TryAdd(pid,sample))return;
            pending.Add(Task.Run(async()=>
            {
                try
                {
                    using var child=Process.GetProcessById(pid);
                    if(child.ProcessName!="Amifolio.Worker")throw new InvalidOperationException("Unexpected worker identity.");
                    var watch=Stopwatch.StartNew();
                    while(!child.HasExited&&watch.Elapsed<TimeSpan.FromSeconds(35))
                    {
                        child.Refresh();sample.PeakWorkingSetBytes=Math.Max(sample.PeakWorkingSetBytes,child.PeakWorkingSet64);
                        sample.PeakPrivateCommitBytes=Math.Max(sample.PeakPrivateCommitBytes,child.PrivateMemorySize64);sample.Samples++;
                        await Task.Delay(10).ConfigureAwait(false);
                    }
                    sample.Exited=child.HasExited;
                }
                catch(ArgumentException){sample.Exited=true;}
                catch(InvalidOperationException){sample.Exited=true;}
            }));
        }
        internal Task DrainAsync()=>Task.WhenAll(pending.ToArray());
    }
}
