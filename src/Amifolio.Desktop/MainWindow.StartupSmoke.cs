using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private async Task<object> RunStartupRegressionAsync(bool reopened)
    {
        var checks=new List<string>();
        void Check(bool value,string name){if(!value)throw new InvalidOperationException("Startup regression: "+name);checks.Add(name);}
        if(reopened)
        {
            smokeSourceRoot=browserSnapshot?.Projects.FirstOrDefault()?.RootPath;
            Check(ShowingBrowserCache&&browserSnapshot is not null,"reopen-loads-disk-snapshot-before-discovery");
            Check(browserProject is not null&&Path.GetFileName(browserDirectory)=="reports","reopen-restores-last-directory");
            Check(ArtifactList.Items.OfType<ArtifactItem>().Any(x=>x.Title=="nested.md"),"reopen-displays-cached-directory");
            Check(SourceStore.CodexProjects.Count==0&&!ParseSelectedButton.IsEnabled&&projects.Count==0&&IntegrationScope.Items.Count==1&&AgentsScope.Items.Count==1,"reopen-cache-does-not-authorize-source-or-deployment");
            await File.WriteAllTextAsync(Path.Combine(browserProject!.RootPath,"reports","new.md"),"synthetic new file");
            await RefreshCatalogInBackgroundAsync();
            Check(catalogVerified&&ArtifactList.Items.OfType<ArtifactItem>().Any(x=>x.Title=="new.md"),"reopen-background-refresh-sees-new-file");
            Check(ParseSelectedButton.IsEnabled&&browserDirectory==Path.Combine(browserProject!.RootPath,"reports"),"reopen-refresh-retains-location");
            return new{checks,cacheLoadMilliseconds};
        }
        Check(backgroundBrowserEnabled&&ShowingBrowserCache&&ready,"empty-cache-opens-without-discovery");
        Check(!navigation.ContainsKey("tasks")&&navigation.Count==7,"logs-removed-from-sidebar");
        Check(Root.FindName("CancelButton") is null,"transient-progress-has-no-cancel-control");
        await RequestNavigationAsync("settings");OpenLogs(this,new RoutedEventArgs());
        using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)))while(currentPage!="tasks"||operation is not null)await Task.Delay(20,timeout.Token);
        Check(currentPage=="tasks"&&SettingsNav.Style==(Style)Application.Current.Resources["SelectedNavButton"],"logs-nested-under-settings");
        await RequestNavigationAsync("library");
        // A fixture under this checkout would sit below .codex, which the real
        // source adapter correctly rejects. Keep the cross-process source in Temp.
        smokeSourceRoot=Path.Combine(Path.GetTempPath(),"Amifolio-native-startup-"+Guid.NewGuid().ToString("N"));
        var project=await SetupCatalogSmokeAsync();catalogVerified=true;await RefreshProjectsAsync();
        await ShowBrowserAsync(project,Path.Combine(project.RootPath,"reports"));
        var persisted=await new BrowserIndexStore(dataRoot,integration.CodexHome).LoadAsync();
        Check(persisted is not null&&persisted.LastDirectory==browserDirectory&&persisted.Directories.Any(d=>d.Entries.Any(e=>e.Name=="nested.md")),"directory-and-location-persisted");
        catalogVerified=false;browserSnapshot=persisted;await RefreshLibraryAsync();
        Check(!ParseSelectedButton.IsEnabled&&ScanStatus.Text.Contains("上次目录"),"cached-view-marked-and-parse-disabled");
        Check(await ValidBrowserPathAsync(ArtifactList.Items.OfType<ArtifactItem>().First()) is null,"cache-cannot-open-originals");
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        smokeBeforeDiscovery=ct=>gate.Task.WaitAsync(ct);
        var scan=RefreshCatalogInBackgroundAsync();
        Check(ReferenceEquals(scan,RefreshCatalogInBackgroundAsync()),"repeated-scans-coalesced");
        Check(operation is null&&KindFilter.IsEnabled,"background-scan-does-not-disable-controls");
        await RequestNavigationAsync("help");ModelName.Text="synthetic-unsaved-model";
        Check(currentPage=="help"&&ModelName.IsEnabled,"navigation-and-input-work-during-scan");
        await RequestNavigationAsync("library");SearchBox.Text="nothing-matches";
        using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)))while(ArtifactList.Items.Count!=0)await Task.Delay(20,timeout.Token);
        Check(!scan.IsCompleted&&ArtifactList.Items.Count==0,"search-works-while-discovery-pending");
        await RequestNavigationAsync("settings");gate.SetResult();await scan;
        Check(currentPage=="settings"&&ModelName.Text=="synthetic-unsaved-model","late-scan-does-not-steal-page-or-input");
        Check(SearchBox.Text=="nothing-matches"&&ArtifactList.Items.Count==0,"late-scan-preserves-filter");
        smokeBeforeDiscovery=_=>throw new IOException("synthetic discovery failure");
        await RefreshCatalogInBackgroundAsync();
        Check(ScanRetryButton.IsEnabled&&scanStatus.Contains("重试"),"failed-scan-offers-retry");
        smokeBeforeDiscovery=null;await RefreshCatalogInBackgroundAsync();
        Check(catalogVerified&&scanStatus.Contains("完成"),"retry-completes-without-restart");
        await RequestNavigationAsync("library");await ShowBrowserAsync(project);
        ArtifactList.SelectedItem=ArtifactList.Items.OfType<ArtifactItem>().Single(x=>x.Title=="notes.md");
        var parseGate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        smokeBeforeParsing=ct=>parseGate.Task.WaitAsync(ct);ParseSelectedFiles(this,new RoutedEventArgs());
        Check(parsingFiles&&operation is null&&KindFilter.IsEnabled,"parse-does-not-take-global-operation-lock");
        await RequestNavigationAsync("settings");parseGate.SetResult();await parsingWork!;smokeBeforeParsing=null;
        Check(currentPage=="settings"&&!parsingFiles&&(await store.ListAsync()).Any(a=>a.Preview.Contains("needle")),"parse-finishes-without-stealing-page");
        await RequestNavigationAsync("context");
        updating=true;try{LearningPage.Scope.SelectedItem=LearningPage.Scope.Items.OfType<ScopeChoice>().Single(s=>s.Id==project.Id);}finally{updating=false;}
        await RefreshLearningPageAsync();var loadedScope=loadedLearningScope;
        var registry=Path.Combine(integration.CodexHome,".codex-global-state.json");var registryBytes=await File.ReadAllBytesAsync(registry);
        await File.WriteAllTextAsync(registry,"{\"local-projects\":{}}");await RefreshCatalogInBackgroundAsync();
        Check(LearningScopeId==project.Id&&loadedLearningScope==loadedScope&&SelectedScope(LearningPage.Scope)!.Name.Contains("未发现"),"removed-project-never-relabels-loaded-editor-as-global");
        await File.WriteAllBytesAsync(registry,registryBytes);await RefreshCatalogInBackgroundAsync();
        var previousDirectories=cachedDirectories.ToArray();cachedDirectories.Clear();
        for(var i=0;i<17;i++)
        {
            var path=Path.Combine(project.RootPath,"cache-test-"+i);
            var entries=Enumerable.Range(0,350).Select(n=>new CatalogEntry(Path.Combine(path,n+".txt"),n+".txt",false,"文本",1,DateTimeOffset.UnixEpoch,"ready")).ToArray();
            cachedDirectories.Add(new(project.Id,path,entries,false));
        }
        await RememberBrowserAsync(null);
        Check(cachedDirectories.Count<=16&&cachedDirectories.Sum(d=>d.Entries.Count)<=5000,"memory-cache-has-same-directory-and-entry-bounds-as-disk");
        cachedDirectories.Clear();cachedDirectories.AddRange(previousDirectories);await RememberBrowserAsync(null);
        using(var locked=new FileStream(Path.Combine(dataRoot,"browser-index.json"),FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            await RememberBrowserAsync(null);
            Check(cacheSaveFailed&&ScanLog.Text.Contains("缓存未保存"),"failed-cache-save-is-visible");
        }
        await RememberBrowserAsync(null);
        await RequestNavigationAsync("library");SearchBox.Text="";await RefreshLibraryAsync();
        await ShowBrowserAsync(project,Path.Combine(project.RootPath,"reports"));
        Check(browserDirectory==Path.Combine(project.RootPath,"reports"),"last-location-ready-for-process-reopen");
        return new{checks,cacheLoadMilliseconds};
    }
}
