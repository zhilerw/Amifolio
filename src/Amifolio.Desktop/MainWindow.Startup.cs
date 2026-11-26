using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private bool backgroundBrowserEnabled;
    private bool catalogVerified;
    private BrowserIndexStore? browserIndex;
    private BrowserIndexSnapshot? browserSnapshot;
    private readonly List<BrowserDirectorySnapshot> cachedDirectories=[];
    private Task? catalogRefresh;
    private readonly SemaphoreSlim cacheSaveGate=new(1,1);
    private long cacheSaveRevision;
    private long cacheLoadMilliseconds;
    private bool cacheSaveFailed;
    private string scanStatus="尚未扫描";
    private DateTimeOffset? scanCompleted;
    // Explicit native smoke mode only; production never supplies a hook.
    private Func<CancellationToken,Task>? smokeBeforeDiscovery;
    private bool ShowingBrowserCache=>backgroundBrowserEnabled&&!catalogVerified;
    private IReadOnlyList<CodexProject> DisplayProjects=>ShowingBrowserCache?browserSnapshot?.Projects??[]:SourceStore.CodexProjects;

    private bool startupStarted;
    private Task startupPreparation=Task.CompletedTask;
    private async Task PrepareStartupAsync(CancellationToken token)
    {
        using var phase=StartupTiming.Measure("startup.store-and-cache");
        // Set display-only/deferred discovery on the UI thread before the store worker starts.
        var cacheTask=Option("--smoke-report") is null||Option("--smoke-mode") is "startup" or "startup-reopen"?OpenBrowserCacheAsync(token):Task.CompletedTask;
        await Task.WhenAll(PrepareStartupStoreAsync(token),cacheTask);
    }

    private async Task PrepareStartupStoreAsync(CancellationToken token)
    {
        FileStream? session=null;
        try
        {
            session=await Task.Run(async()=>
            {
                FileStream acquired;
                using(StartupTiming.Measure("startup.session-lease"))acquired=await StorageLease.SessionAsync(dataRoot,token).ConfigureAwait(false);
                try
                {
                    using(StartupTiming.Measure("startup.store"))await store.InitializeAsync(token).ConfigureAwait(false);
                    using(StartupTiming.Measure("startup.session-only-marker"))await context.EnableSessionLearningOnlyAsync(token).ConfigureAwait(false);
                    return acquired;
                }
                catch{acquired.Dispose();throw;}
            },token);
            // Close may precede this UI continuation. Never publish a lease to a closed window.
            token.ThrowIfCancellationRequested();
            if(closing)throw new OperationCanceledException(token);
            storeSession=session;
            session=null;
        }
        finally{session?.Dispose();}
    }

    private object? startupReadyEvidence;
    private async Task CaptureStartupReadinessAsync()
    {
        using(StartupTiming.Measure("startup.layout"))Root.UpdateLayout();
        var controlsReady=LibraryNav.IsEnabled&&ContextNav.IsEnabled&&AgentsNav.IsEnabled&&SettingsNav.IsEnabled;
        if(closing||!startupPreparation.IsCompletedSuccessfully||storeSession is null||!ready||operation is not null||!controlsReady||!browserInputAttached||CreativePage.Visibility!=Visibility.Visible||CreativePage.ActualWidth<=0||CreativePage.ActualHeight<=0)
            throw new InvalidOperationException("Initial creative page is not ready for interaction.");
        var cached=ShowingBrowserCache;var count=ArtifactList.Items.Count;
        var heartbeat=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using(StartupTiming.Measure("startup.dispatcher-heartbeat"))
        {
            if(!DispatcherQueue.TryEnqueue(()=>heartbeat.TrySetResult()))throw new InvalidOperationException("Startup dispatcher unavailable.");
            await heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(5),backgroundTasks.Token);
        }
        backgroundTasks.Token.ThrowIfCancellationRequested();
        startupReadyEvidence = new
        {
            controlsEnabled = controlsReady, browserInputAttached, cachedView = cached, displayedItems = count,
            dispatcherResponsive = true, libraryWidth = CreativePage.ActualWidth, libraryHeight = CreativePage.ActualHeight,
            initialPage = currentPage,
            creativePreviewAtWindowReady = stylePreviewReady && creativeLoaded ? "interactive" :
                stylePreviewUnavailable ? "unavailable" : "loading",
            windowWidth = AppWindow.Size.Width, windowHeight = AppWindow.Size.Height,
            requestedWindowWidth = startupWindowSize.Width, requestedWindowHeight = startupWindowSize.Height,
            windowResizeRequests = startupResizeRequests
        };
        StartupTiming.Mark("startup.ready");
    }

    private async Task OpenBrowserCacheAsync(CancellationToken token)
    {
        using var phase=StartupTiming.Measure("startup.cache");
        backgroundBrowserEnabled=true;
        SourceStore.DeferCodexDiscovery();
        browserIndex=new BrowserIndexStore(dataRoot,integration.CodexHome);
        var watch=System.Diagnostics.Stopwatch.StartNew();
        BrowserIndexSnapshot? loadedSnapshot;
        using(StartupTiming.Measure("startup.cache-read"))loadedSnapshot=await Task.Run(()=>browserIndex.LoadAsync(token),token);
        token.ThrowIfCancellationRequested();
        if(closing)throw new OperationCanceledException(token);
        browserSnapshot=loadedSnapshot;
        cacheLoadMilliseconds=watch.ElapsedMilliseconds;
        if(browserSnapshot is not null)
        {
            cachedDirectories.AddRange(browserSnapshot.Directories);
            browserProject=browserSnapshot.Projects.FirstOrDefault(p=>p.Id==browserSnapshot.LastProjectId);
            browserDirectory=browserProject is null?null:browserSnapshot.LastDirectory??browserProject.RootPath;
            browserHistory.ReplaceCurrent(new(browserProject,browserDirectory));
        }
    }

    private CatalogResult<CatalogEntry> CachedDirectory(BrowserLocation location)
    {
        var directory=cachedDirectories.FirstOrDefault(d=>d.ProjectId==location.Project?.Id&&StringComparer.OrdinalIgnoreCase.Equals(d.Path,location.Path));
        return new(directory?.Entries??[],directory?.Truncated??false,["上次目录记录，等待后台核对。"]);
    }

    private async Task RememberBrowserAsync(CatalogResult<CatalogEntry>? result)
    {
        if(!backgroundBrowserEnabled||browserIndex is null||!catalogVerified||closing)return;
        if(browserProject is not null&&browserDirectory is not null&&result is not null)
        {
            cachedDirectories.RemoveAll(d=>d.ProjectId==browserProject.Id&&StringComparer.OrdinalIgnoreCase.Equals(d.Path,browserDirectory));
            cachedDirectories.Insert(0,new(browserProject.Id,browserDirectory,result.Items.Take(5000).ToArray(),result.Truncated));
        }
        var validProjects=SourceStore.CodexProjects.ToArray();
        cachedDirectories.RemoveAll(d=>!validProjects.Any(p=>p.Id==d.ProjectId));
        var bounded=new List<BrowserDirectorySnapshot>();var remaining=5000;
        foreach(var directory in cachedDirectories.Take(16))
        {
            var entries=directory.Entries.Take(remaining).ToArray();
            bounded.Add(directory with{Entries=entries,Truncated=directory.Truncated||entries.Length<directory.Entries.Count});
            remaining-=entries.Length;
        }
        // The disk limit is also the long-lived memory limit.
        cachedDirectories.Clear();cachedDirectories.AddRange(bounded);
        var snapshot=new BrowserIndexSnapshot(1,integration.CodexHome,DateTimeOffset.UtcNow,validProjects,bounded,browserProject?.Id,browserDirectory);
        var revision=++cacheSaveRevision;
        try
        {
            await cacheSaveGate.WaitAsync(backgroundTasks.Token);
            try
            {
                if(revision!=cacheSaveRevision)return;
                cacheSaveFailed=!await Task.Run(()=>browserIndex.SaveAsync(snapshot,backgroundTasks.Token),backgroundTasks.Token);
                if(!cacheSaveFailed)browserSnapshot=snapshot;
                if(!closing)UpdateScanLog();
            }
            finally{cacheSaveGate.Release();}
        }
        catch(OperationCanceledException){}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){cacheSaveFailed=true;if(!closing)UpdateScanLog();}
    }

    private Task RefreshCatalogInBackgroundAsync()
    {
        if(catalogRefresh is {IsCompleted:false})return catalogRefresh;
        catalogRefresh=RefreshCatalogCoreAsync();return catalogRefresh;
    }
    private async Task RefreshCatalogCoreAsync()
    {
        scanStatus="正在后台扫描项目目录";UpdateScanLog();
        ScanStatus.Text=scanStatus;ScanRetryButton.IsEnabled=false;
        try
        {
            if(Option("--smoke-report") is not null&&smokeBeforeDiscovery is not null)await smokeBeforeDiscovery(backgroundTasks.Token);
            await Task.Run(()=>SourceStore.RefreshCodexProjectsAsync(backgroundTasks.Token),backgroundTasks.Token);
            if(closing)return;
            catalogVerified=true;
            await RefreshProjectsAsync();
            if (currentPage == "creative") RefreshCreativeScopes();
            // Use the user's current location and filters. A late scan must not restore
            // the startup location after the user has already navigated elsewhere.
            await RefreshLibraryAsync();
            if (currentPage == "works") await RefreshWorksHomeAsync();
            await RefreshVisitedDirectoryCacheAsync(backgroundTasks.Token);
            await LoadCollectionGrantStatusAsync();
            scanCompleted=DateTimeOffset.Now;
            scanStatus=SourceStore.DiscoveryWarnings.Count==0?$"扫描完成 · {SourceStore.CodexProjects.Count} 个项目":string.Join("；",SourceStore.DiscoveryWarnings.Take(2));
        }
        catch(OperationCanceledException)when(backgroundTasks.IsCancellationRequested){}
        catch(Exception ex)
        {
            if(closing)return;
            scanStatus="扫描未完成，可重试。";
            System.Diagnostics.Trace.TraceWarning("Catalog refresh failed: {0}",ex.GetType().Name);
        }
        finally
        {
            if(!closing){ScanStatus.Text=scanStatus;ScanRetryButton.IsEnabled=true;UpdateScanLog();UpdateBrowserControls();}
        }
    }
    private void UpdateScanLog()
    {
        var cache=cacheSaveFailed?" · 目录缓存未保存，请检查数据目录权限或空间":"";
        ScanLog.Text=scanStatus+(scanCompleted is { } date?$" · {date:MM-dd HH:mm}":"")+cache;
        ScanStatus.Text=ShowingBrowserCache?"上次目录记录 · 后台核对后可解析"+cache:scanStatus+cache;
    }
    private async Task RefreshVisitedDirectoryCacheAsync(CancellationToken token)
    {
        var captured=cachedDirectories.Take(16).ToArray();
        var projects=SourceStore.CodexProjects.ToArray();
        var refreshed=await Task.Run(async ()=>
        {
            var result=new List<(BrowserDirectorySnapshot Old,BrowserDirectorySnapshot? Updated)>();
            foreach(var old in captured)
            {
                token.ThrowIfCancellationRequested();
                var project=projects.FirstOrDefault(p=>p.Id==old.ProjectId);
                if(project is null){result.Add((old,null));continue;}
                try
                {
                    var entries=await SourceStore.Catalog.BrowseAsync(project,old.Path,token);
                    result.Add((old,new(old.ProjectId,old.Path,entries.Items,entries.Truncated)));
                }
                catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){result.Add((old,null));}
            }
            return result;
        },token);
        if(closing)return;
        foreach(var (old,updated) in refreshed)
        {
            // A directory browsed since this scan began has a newer observation.
            var index=cachedDirectories.FindIndex(d=>ReferenceEquals(d,old));
            if(index<0)continue;
            if(updated is null)cachedDirectories.RemoveAt(index);else cachedDirectories[index]=updated;
        }
        await RememberBrowserAsync(null);
    }
    private async void RetryScan(object sender,RoutedEventArgs e)=>await RefreshCatalogInBackgroundAsync();
    private async void OpenLogs(object sender,RoutedEventArgs e)=>await RequestNavigationAsync("tasks");
    private async void CloseLogs(object sender,RoutedEventArgs e)=>await RequestNavigationAsync("settings");
}
