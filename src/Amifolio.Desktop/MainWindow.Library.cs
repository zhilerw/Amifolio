using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private long libraryRequest;
    private long detailRequest;
    private bool parsingFiles;
    private Task? parsingWork;
    private Func<CancellationToken,Task>? smokeBeforeParsing;
    private CodexProject? browserProject;
    private string? browserDirectory;
    private PortfolioStore SourceStore => (PortfolioStore)store;
    private sealed record BrowserLocation(CodexProject? Project,string? Path);
    private sealed record BreadcrumbItem(string Label,BrowserLocation Location);
    private async Task RefreshProjectsAsync()
    {
        // Cached projects are display-only and must never become deployment or sharing scopes.
        projects=ShowingBrowserCache?[]:await store.ProjectsAsync();updating=true;
        try{ProjectsList.ItemsSource=ShowingBrowserCache?DisplayProjects.Select(p=>new ProjectRecord(p.Id,p.Name,p.RootPath,DateTimeOffset.UnixEpoch)).ToArray():projects;if(!contextEdit.IsEditing)SetScopes(ContextScope,"全局");if(!agentsEdit.IsEditing)SetScopes(AgentsScope,"全局 Codex");SetScopes(IntegrationScope,"全局 Codex");if(!editingLearning&&!editingLearningSettings)SetScopes(LearningPage.Scope,"全局偏好");}
        finally{updating=false;}
    }
    private void SetScopes(ComboBox box,string globalLabel)
    {
        var previousChoice=SelectedScope(box);var previous=previousChoice?.Id;
        var duplicateNames = projects.GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Skip(1).Any()).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var choices = new List<ScopeChoice> { new(null, globalLabel) };
        choices.AddRange(projects.Select(project => new ScopeChoice(project.Id,
            duplicateNames.Contains(project.Name) || string.Equals(project.Name, globalLabel, StringComparison.OrdinalIgnoreCase)
                ? $"{project.Name} · {project.RootPath ?? project.Id}" : project.Name)));
        // Do not silently relabel a loaded project editor as global when a scan
        // removes its root. Keep the identity until the user chooses another scope.
        if(previous is not null&&!choices.Any(x=>x.Id==previous))
            choices.Add(new(previous,previousChoice!.Name.EndsWith("（未发现）",StringComparison.Ordinal)?previousChoice.Name:previousChoice.Name+"（未发现）"));
        box.ItemsSource=choices;box.SelectedItem=choices.FirstOrDefault(x=>x.Id==previous)??choices[0];
    }
    private async Task RefreshLibraryAsync()
    {
        var request=++libraryRequest;
        var selection=ArtifactList.SelectedItems.OfType<ArtifactItem>().Select(x=>x.Record.Id).ToHashSet(StringComparer.Ordinal);
        var selectedPaths=ArtifactList.SelectedItems.OfType<ArtifactItem>().Select(x=>x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var primary=selectedArtifactId;
        var selectionRevision=browserSelectionRevision;
        var requested=new BrowserLocation(browserProject,browserDirectory);
        var search=SearchBox.Text;var kind=KindFilter.SelectedItem as string;var sort=browserSort;var descending=browserDescending;
        if(!backgroundBrowserEnabled)await SourceStore.RefreshSourceStatesAsync();
        var resolved=await ResolveBrowserAsync(requested,true);
        if(closing||request!=libraryRequest||requested.Project!=browserProject||requested.Path!=browserDirectory)return;
        var location=resolved!.Value.Location;
        var changedLocation=!BrowserLocationEquality.Equals(requested,location);
        if(changedLocation)
        {
            browserHistory.ReplaceCurrent(location);
            selection.Clear();selectedPaths.Clear();primary=null;
        }
        // Discovery can update a name/session count without changing the history identity.
        browserProject=location.Project;browserDirectory=location.Path;
        if(!BrowserLocationEquality.Equals(browserHistory.Current,location))browserHistory.ReplaceCurrent(location);
        var items=new List<ArtifactItem>();var warnings=new List<string>();
        if(browserProject is null)
        {
            foreach(var p in DisplayProjects)
            {
                if(!string.IsNullOrWhiteSpace(search)&&!p.Name.Contains(search,StringComparison.OrdinalIgnoreCase))continue;
                var record=new ArtifactRecord(p.Id,p.Name,p.Name,"项目","Codex",p.Id,"",p.RootPath,"",0,DateTimeOffset.UnixEpoch,0,"只读目录","",true);
                items.Add(new(record,p.Name,"项目","","",true,p.RootPath,p));
            }
            warnings.AddRange(SourceStore.DiscoveryWarnings);
        }
        else
        {
            var result=resolved.Value.Result!;
            var indexed=ShowingBrowserCache?Array.Empty<ArtifactRecord>():await SourceStore.ListDirectoryAsync(browserProject.Id,browserDirectory??browserProject.RootPath);
            var byPath=indexed.GroupBy(a=>a.ObjectPath,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.First(),StringComparer.OrdinalIgnoreCase);
            foreach(var entry in result.Items)
            {
                byPath.TryGetValue(entry.Path,out var saved);
                if(!string.IsNullOrWhiteSpace(search)&&!entry.Name.Contains(search,StringComparison.OrdinalIgnoreCase)&&!(saved?.Preview.Contains(search,StringComparison.OrdinalIgnoreCase)??false))continue;
                if(!entry.IsDirectory&&kind is not null and not "全部类型"&&kind!=entry.Kind)continue;
                var state=ShowingBrowserCache?"待核对":entry.State switch{"ready"=>entry.IsDirectory?"只读目录":"未解析","excluded"=>"已排除","unreadable"=>"不可读","missing"=>"源文件缺失","too_large"=>"超过解析上限",_=>"不可读"};
                // A saved preview is retained, but sorting always uses current source metadata.
                var record=saved is null?new ArtifactRecord("file-"+entry.Path,entry.Name,entry.Name,entry.IsDirectory?"目录":entry.Kind,"Codex",browserProject.Id,"",entry.Path,"",entry.Bytes,entry.ModifiedAt,0,state,"",true):saved with {Bytes=entry.Bytes,CreatedAt=entry.ModifiedAt};
                items.Add(new(record,entry.Name,entry.IsDirectory?"文件夹":record.ExtractionStatus,"",FormatBytes(entry.Bytes),entry.IsDirectory,entry.Path,browserProject));
            }
            warnings.AddRange(result.Warnings);if(result.Truncated)warnings.Add("当前目录超过显示上限，请缩小范围。");
        }
        if(closing||request!=libraryRequest||search!=SearchBox.Text||sort!=browserSort||descending!=browserDescending||location.Project!=browserProject||location.Path!=browserDirectory)return;
        if(!changedLocation&&selectionRevision!=browserSelectionRevision)
        {
            selection=ArtifactList.SelectedItems.OfType<ArtifactItem>().Select(x=>x.Record.Id).ToHashSet(StringComparer.Ordinal);
            selectedPaths=ArtifactList.SelectedItems.OfType<ArtifactItem>().Select(x=>x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            primary=selectedArtifactId;
        }
        var sorted=BrowserSort.Apply(items,x=>new(x.IsDirectory,x.Title,x.Record.Kind,x.Record.CreatedAt,x.Record.Bytes),sort,descending);
        // Rebinding otherwise raises a transient empty selection and clears the primary detail.
        restoringBrowserSelection=true;
        try
        {
            ArtifactList.ItemsSource=sorted;
            foreach(var item in sorted.Where(x=>selection.Contains(x.Record.Id)||selectedPaths.Contains(x.Path)))ArtifactList.SelectedItems.Add(item);
        }
        finally{restoringBrowserSelection=false;}
        var selected=ArtifactList.SelectedItems.OfType<ArtifactItem>().ToArray();
        await ShowArtifactAsync(selected.FirstOrDefault(x=>x.Record.Id==primary)??selected.LastOrDefault());
        if(closing||request!=libraryRequest||location.Project!=browserProject||location.Path!=browserDirectory)return;
        EmptyLibrary.Visibility=items.Count==0?Visibility.Visible:Visibility.Collapsed;
        EmptyTitle.Text=string.IsNullOrWhiteSpace(search)?browserProject is null?"尚未发现Codex项目":"当前目录为空":"无匹配项";
        EmptyHint.Text=warnings.FirstOrDefault()??(browserProject is null?"点击扫描，读取本机Codex项目。":"只显示当前目录，不移动或复制文件。");
        UpdateBrowserControls();
        var crumbs=new List<BreadcrumbItem>{new("所有项目",new(null,null))};
        if(browserProject is not null)
        {
            crumbs.Add(new(browserProject.Name,new(browserProject,browserProject.RootPath)));
            var relative=Path.GetRelativePath(browserProject.RootPath,browserDirectory??browserProject.RootPath);var path=browserProject.RootPath;
            if(relative!=".")foreach(var segment in relative.Split(Path.DirectorySeparatorChar)){path=Path.Combine(path,segment);crumbs.Add(new(segment,new(browserProject,path)));}
        }
        Breadcrumbs.ItemsSource=crumbs;
        ScanStatus.Text=ShowingBrowserCache?"上次目录记录 · 后台核对后可解析":scanStatus;
        await RememberBrowserAsync(resolved.Value.Result);
        if(warnings.Count>0)StatusText.Text=string.Join("；",warnings.Take(2));
    }
    private async Task ShowBrowserAsync(CodexProject? project,string? path=null)
    {
        var resolved=await ResolveBrowserAsync(new(project,path??project?.RootPath),false);
        if(resolved is null){StatusText.Text="目录不在已发现的可用项目范围内。";await RefreshLibraryAsync();return;}
        browserHistory.Visit(resolved.Value.Location);await DisplayBrowserAsync(resolved.Value.Location);
    }
    private async void BreadcrumbClicked(object sender,RoutedEventArgs e)
    {if(sender is Button{Tag:BrowserLocation location}&&operation is null)await RunAsync("打开目录",_=>ShowBrowserAsync(location.Project,location.Path));}
    private async void BrowserUp(object sender,RoutedEventArgs e)
    {await ExecuteBrowserCommandAsync(BrowserCommand.Up);}
    private async void ProjectItemClicked(object sender,ItemClickEventArgs e)
    {
        if(e.ClickedItem is not ProjectRecord p||operation is not null)return;
        var project=DisplayProjects.FirstOrDefault(x=>x.Id==p.Id);if(project is null)return;
        if(await RequestNavigationAsync("library"))await ShowBrowserAsync(project);
    }
    private async void SearchChanged(object sender,TextChangedEventArgs e)
    {
        if(!ready||updating)return;searchDelay?.Cancel();var delay=new CancellationTokenSource();searchDelay=delay;
        try{await Task.Delay(220,delay.Token);await RefreshLibraryAsync();}catch(OperationCanceledException){}catch(Exception ex){StatusText.Text="检索失败："+ex.Message;}finally{if(searchDelay==delay)searchDelay=null;delay.Dispose();}
    }
    private async void FiltersChanged(object sender,SelectionChangedEventArgs e)
    {if(ready&&!updating&&operation is null)await RunAsync("筛选当前目录",_=>RefreshLibraryAsync());}
    private async void ArtifactSelected(object sender,SelectionChangedEventArgs e)
    {
        if(restoringBrowserSelection)return;
        browserSelectionRevision++;
        UpdateBrowserControls();
        await ShowArtifactAsync(e.AddedItems.OfType<ArtifactItem>().LastOrDefault()??ArtifactList.SelectedItems.OfType<ArtifactItem>().LastOrDefault());
    }
    private async Task ShowArtifactAsync(ArtifactItem? item)
    {
        var request=++detailRequest;
        if(item is null){ClearDetail();return;}
        selectedArtifactId=item.Record.Id;DetailTitle.Text=item.Title;DetailMetadata.Text=$"{item.Record.Kind} · {item.Size}\n{item.Path}\n\n{(item.IsDirectory?"只读目录":item.Record.ExtractionStatus)}";
        DetailPreview.Text=string.IsNullOrWhiteSpace(item.Record.Preview)?(item.IsDirectory?"打开查看原目录结构。":"点击解析读取内容，不复制或修改原文件。"):item.Record.Preview[..Math.Min(4000,item.Record.Preview.Length)];
        DetailPreviewHeading.Visibility=DetailDivider.Visibility=Visibility.Visible;DetailActions.Visibility=item.IsDirectory?Visibility.Collapsed:Visibility.Visible;if(!loadingPage)motion?.Enter(DetailContent,MotionKind.Detail);
        if(!item.IsDirectory&&item.Record.Hash.Length==64)
        {
            DetailPreview.Text="正在核对来源…";
            try
            {
                var live=await store.GetAsync(item.Record.Id);
                if(request!=detailRequest||selectedArtifactId!=item.Record.Id)return;
                if(live is not null){DetailMetadata.Text=$"{live.Kind} · {item.Size}\n{item.Path}\n\n{live.ExtractionStatus}";DetailPreview.Text=string.IsNullOrWhiteSpace(live.Preview)?"点击解析读取内容，不复制或修改原文件。":live.Preview[..Math.Min(4000,live.Preview.Length)];}
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){if(request==detailRequest&&selectedArtifactId==item.Record.Id){DetailPreview.Text="";StatusText.Text="文件不可读："+ex.Message;}}
        }
    }
    private void ClearDetail(){detailRequest++;selectedArtifactId=null;DetailTitle.Text="未选择文件";DetailMetadata.Text="";DetailPreview.Text="";DetailPreviewHeading.Visibility=DetailDivider.Visibility=DetailActions.Visibility=Visibility.Collapsed;}
    private async void ParseSelectedFiles(object sender,RoutedEventArgs e)
    {
        if(ShowingBrowserCache){StatusText.Text="目录核对完成后可解析文件。";return;}
        if(parsingFiles||operation is not null)return;
        var selected=ArtifactList.SelectedItems.OfType<ArtifactItem>().Where(x=>!x.IsDirectory).ToArray();if(selected.Length==0){StatusText.Text="先选择需要解析的文件。";return;}
        parsingWork=ParseFilesInBackgroundAsync(selected);await parsingWork;
    }
    private async Task ParseFilesInBackgroundAsync(ArtifactItem[] selected)
    {
        parsingFiles=true;UpdateBrowserControls();var token=backgroundTasks.Token;
        try
        {
            if(Option("--smoke-report") is not null&&smokeBeforeParsing is not null)await smokeBeforeParsing(token);
            var failures=new List<string>();var count=0;
            foreach(var item in selected)
            {
                token.ThrowIfCancellationRequested();if(closing)return;StatusText.Text=$"解析 {++count}/{selected.Length} · {item.Title}";
                try
                {
                    var result=await Task.Run(async ()=>
                    {
                        var indexed=await SourceStore.ReferenceFileAsync(item.Path!,item.Project?.Id,token);
                        await SourceStore.ParseReferenceAsync(indexed.Artifact.Id,token);
                        return await store.GetAsync(indexed.Artifact.Id,token);
                    },token);
                    if(result?.ExtractionStatus!="已提取")failures.Add(item.Title+"："+(result?.ExtractionStatus??"未完成"));
                }
                catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){failures.Add(item.Title+"："+ex.Message);}
            }
            if(closing)return;
            await RefreshLibraryAsync();StatusText.Text=failures.Count==0?"解析完成，原文件未更改":$"解析结束，{failures.Count} 项未完成，请查看设置中的日志";
            if(currentPage=="tasks")await LoadTasksAsync();
        }
        catch(OperationCanceledException)when(backgroundTasks.IsCancellationRequested){}
        catch(Exception){if(!closing)StatusText.Text="解析未完成，请在设置中的日志查看并重试。";}
        finally{parsingFiles=false;if(!closing)UpdateBrowserControls();}
    }
}
