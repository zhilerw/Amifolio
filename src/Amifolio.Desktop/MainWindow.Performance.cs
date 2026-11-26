using System.Diagnostics;
using System.Text.Json;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private static void ValidatePerformanceInvocation()
    {
        if(Option("--profile-report") is not{} report)return;
        var requested=Option("--data-root")??throw new InvalidOperationException("Isolated data root required.");
        var isolated=Path.GetFullPath(requested);report=Path.GetFullPath(report);
        var formal=Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Amifolio"));
        if(isolated.Equals(formal,StringComparison.OrdinalIgnoreCase)||!report.StartsWith(isolated+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Isolated report required.");
        for(var directory=new DirectoryInfo(isolated);directory is not null;directory=directory.Parent)
            if(directory.Exists&&directory.Attributes.HasFlag(FileAttributes.ReparsePoint))throw new InvalidOperationException("Linked fixture refused.");
        var marker=Path.Combine(isolated,"performance-fixture.json");
        if(!File.Exists(marker)||new FileInfo(marker).Attributes.HasFlag(FileAttributes.ReparsePoint)||new FileInfo(marker).Length>512)throw new InvalidOperationException("Owned fixture required.");
        using var json=JsonDocument.Parse(File.ReadAllBytes(marker));
        if(json.RootElement.GetProperty("owner").GetString()!="AmifolioStage9Performance")throw new InvalidOperationException("Owned fixture required.");
        var home=Environment.GetEnvironmentVariable("CODEX_HOME")??throw new InvalidOperationException("Owned Codex fixture required.");
        var homeMarker=Path.Combine(Path.GetFullPath(home),"performance-fixture.json");
        if(!File.Exists(homeMarker)||new FileInfo(homeMarker).Attributes.HasFlag(FileAttributes.ReparsePoint)||new FileInfo(homeMarker).Length>512)throw new InvalidOperationException("Owned Codex fixture required.");
        using var homeJson=JsonDocument.Parse(File.ReadAllBytes(homeMarker));
        if(homeJson.RootElement.GetProperty("owner").GetString()!="AmifolioStage9Performance")throw new InvalidOperationException("Owned Codex fixture required.");
    }
    // Explicit developer-only probe, always using an isolated local data root.
    private async Task RunPerformanceProbeAsync(string reportPath)
    {
        reportPath=Path.GetFullPath(reportPath);
        var formal=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Amifolio");
        if(Path.GetFullPath(dataRoot).Equals(Path.GetFullPath(formal),StringComparison.OrdinalIgnoreCase)||!reportPath.StartsWith(Path.GetFullPath(dataRoot)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("Performance probe requires isolated data.");
        StartupTiming.Mark("profile.probe.begin");
        var startup=StartupTiming.Snapshot();
        var readyAt=startup.marks.LastOrDefault(m=>m.stage=="startup.ready")?.timestampUtc
            ??throw new InvalidOperationException("Actual startup readiness has not been captured.");
        using(StartupTiming.Measure("profile.ready-report"))
            await File.WriteAllTextAsync(reportPath+".ready",JsonSerializer.Serialize(new{pid=Environment.ProcessId,cacheLoadMilliseconds,readyAt,reportWriteStartedAt=DateTimeOffset.UtcNow,startup,readyEvidence=startupReadyEvidence}));
        // External observers can now distinguish actual readiness from report publication and hiding.
        using(StartupTiming.Measure("profile.window-hide"))AppWindow.Hide();
        using(StartupTiming.Measure("profile.hidden-layout"))Root.UpdateLayout();
        try
        {
            var operationWatch=Stopwatch.StartNew();
            var work=RefreshCatalogInBackgroundAsync();
            var measured=work.ContinueWith(_=>operationWatch.Elapsed.TotalMilliseconds,TaskScheduler.Default);
            var latency=new List<double>();var duringScan=new List<double>();
            for(var i=0;i<100;i++)
            {
                await Task.Delay(2).ConfigureAwait(false);
                var inFlight=!work.IsCompleted;
                var queued=Stopwatch.StartNew();var delivered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if(!DispatcherQueue.TryEnqueue(()=>{var delay=queued.Elapsed.TotalMilliseconds;latency.Add(delay);if(inFlight)duringScan.Add(delay);delivered.SetResult();}))break;
                await delivered.Task.ConfigureAwait(false);
            }
            await work.ConfigureAwait(false);var scanMilliseconds=await measured.ConfigureAwait(false);
            if(!catalogVerified||SourceStore.CodexProjects.Count!=1||SourceStore.DiscoveryWarnings.Count!=0)throw new InvalidOperationException("Synthetic catalog not fully verified.");
            var sourceMarker=Path.Combine(SourceStore.CodexProjects[0].RootPath,"performance-fixture.json");
            using(var sourceJson=JsonDocument.Parse(await File.ReadAllBytesAsync(sourceMarker).ConfigureAwait(false)))
                if(sourceJson.RootElement.GetProperty("owner").GetString()!="AmifolioStage9Performance")throw new InvalidOperationException("Owned source fixture required.");
            var parseMilliseconds=0d;var parsed=0;
            if(Option("--profile-parse")=="true")
            {
                var project=SourceStore.CodexProjects.FirstOrDefault();
                if(project is not null)
                {
                    var entries=await SourceStore.Catalog.BrowseAsync(project,project.RootPath,backgroundTasks.Token).ConfigureAwait(false);
                    var candidate=entries.Items.FirstOrDefault(x=>!x.IsDirectory&&x.Name.EndsWith(".md",StringComparison.OrdinalIgnoreCase));
                    if(candidate is not null)
                    {
                        var parse=Stopwatch.StartNew();var reference=await SourceStore.ReferenceFileAsync(candidate.Path,project.Id,backgroundTasks.Token).ConfigureAwait(false);
                        await SourceStore.ParseReferenceAsync(reference.Artifact.Id,backgroundTasks.Token).ConfigureAwait(false);
                        if((await SourceStore.GetAsync(reference.Artifact.Id,backgroundTasks.Token).ConfigureAwait(false))?.Preview.Contains("synthetic performance input")!=true)throw new InvalidOperationException("Synthetic parse failed.");
                        parseMilliseconds=parse.Elapsed.TotalMilliseconds;parsed=1;
                    }
                }
            }
            if(Option("--profile-parse")=="true"&&parsed!=1)throw new InvalidOperationException("Synthetic parse required.");
            var backgroundReady=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if(!DispatcherQueue.TryEnqueue(async()=>
            {
                try{await Task.WhenAll(RetireLegacyInBackgroundAsync(),RefreshEnvironmentAsync(),ResumeStartupTasksAsync());StartLearningProcessing();backgroundReady.SetResult();}
                catch(Exception ex){backgroundReady.SetException(ex);}
            }))throw new InvalidOperationException("Dispatcher unavailable.");
            await backgroundReady.Task.ConfigureAwait(false);
            object? endurance=null;
            if(Option("--profile-stress")=="true")
            {
                var enduranceReady=new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                if(!DispatcherQueue.TryEnqueue(async()=>
                {
                    try{enduranceReady.SetResult(await RunEnduranceProbeAsync(reportPath));}
                    catch(Exception ex){enduranceReady.SetException(ex);}
                }))throw new InvalidOperationException("Endurance dispatcher unavailable.");
                endurance=await enduranceReady.Task.ConfigureAwait(false);
            }
            var ordered=duringScan.Order().ToArray();
            await File.WriteAllTextAsync(reportPath,JsonSerializer.Serialize(new{pid=Environment.ProcessId,startup=StartupTiming.Snapshot(),readyEvidence=startupReadyEvidence,scanMilliseconds,parseMilliseconds,parsed,productionBackgroundStarted=true,endurance,dispatcher=new{valid=ordered.Length>=10,samples=latency.Count,overlapping=ordered.Length,p95=ordered.Length==0?(double?)null:ordered[(int)Math.Ceiling(ordered.Length*.95)-1],max=ordered.Length==0?(double?)null:ordered[^1]},sourceProjects=SourceStore.CodexProjects.Count,limitations="Dispatcher latency uses only samples queued during real scan; fewer than 10 overlapping samples is invalid responsiveness evidence. Not physical input or frame pacing."},new JsonSerializerOptions{WriteIndented=true})).ConfigureAwait(false);
        }
        catch (Exception ex) { await File.WriteAllTextAsync(reportPath + ".error", ex.ToString()).ConfigureAwait(false); }
    }
}
