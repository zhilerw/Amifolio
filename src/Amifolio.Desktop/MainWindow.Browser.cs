using System.Diagnostics;
using Amifolio.Core;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI.Core;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private static readonly BrowserLocationComparer BrowserLocationEquality=new();
    private readonly BrowserHistory<BrowserLocation> browserHistory=new(new(null,null),comparer:BrowserLocationEquality);
    private BrowserSortField browserSort;
    private bool browserDescending;
    private bool restoringBrowserSelection;
    private long browserSelectionRevision;
    private bool browserInputAttached;
    private PointerEventHandler? browserPointerHandler;
    private KeyEventHandler? browserKeyHandler;

    // MainWindow owns constructor/Closed wiring; registration is deliberately idempotent.
    private void InitializeBrowser()
    {
        if(browserInputAttached)return;
        browserPointerHandler=BrowserPointerPressed;browserKeyHandler=BrowserKeyDown;
        Root.AddHandler(UIElement.PointerPressedEvent,browserPointerHandler,true);
        // Handle browser commands before GridView's built-in key handling changes
        // selection (notably Enter in multiple-selection mode).
        Root.AddHandler(UIElement.PreviewKeyDownEvent,browserKeyHandler,true);
        browserInputAttached=true;UpdateBrowserControls();
    }
    private void DisposeBrowser()
    {
        if(!browserInputAttached)return;
        Root.RemoveHandler(UIElement.PointerPressedEvent,browserPointerHandler);
        Root.RemoveHandler(UIElement.PreviewKeyDownEvent,browserKeyHandler);
        browserInputAttached=false;
    }
    private sealed class BrowserLocationComparer:IEqualityComparer<BrowserLocation>
    {
        public bool Equals(BrowserLocation? a,BrowserLocation? b)=>a?.Project?.Id==b?.Project?.Id&&
            StringComparer.OrdinalIgnoreCase.Equals(a?.Project?.RootPath,b?.Project?.RootPath)&&StringComparer.OrdinalIgnoreCase.Equals(a?.Path,b?.Path);
        public int GetHashCode(BrowserLocation value)=>HashCode.Combine(value.Project?.Id,value.Path is null?0:StringComparer.OrdinalIgnoreCase.GetHashCode(value.Path));
    }
    private async Task<(BrowserLocation Location,CatalogResult<CatalogEntry>? Result)?> ResolveBrowserAsync(BrowserLocation requested,bool fallback)
    {
        if(requested.Project is null)return(new(null,null),null);
        if(ShowingBrowserCache)
        {
            var cached=DisplayProjects.FirstOrDefault(p=>p.Id==requested.Project.Id&&StringComparer.OrdinalIgnoreCase.Equals(p.RootPath,requested.Project.RootPath));
            if(cached is null)return fallback?(new(null,null),null):null;
            var location=new BrowserLocation(cached,requested.Path??cached.RootPath);
            return(location,CachedDirectory(location));
        }
        var project=SourceStore.CodexProjects.FirstOrDefault(p=>p.Id==requested.Project.Id&&StringComparer.OrdinalIgnoreCase.Equals(p.RootPath,requested.Project.RootPath));
        if(project is null)return fallback?(new(null,null),null):null;
        string path;
        try
        {
            path=Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested.Path??project.RootPath));
            // This preliminary check bounds parent fallback; catalog checks exclusions/reparse/readability.
            var relative=Path.GetRelativePath(project.RootPath,path);
            if(Path.IsPathRooted(relative)||relative==".."||relative.StartsWith(".."+Path.DirectorySeparatorChar,StringComparison.Ordinal))return fallback?(new(null,null),null):null;
        }
        catch(Exception ex)when(ex is ArgumentException or NotSupportedException or IOException){return fallback?(new(null,null),null):null;}
        while(true)
        {
            try{return(new(project,path),await SourceStore.Catalog.BrowseAsync(project,path));}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                if(!fallback)return null;
                if(StringComparer.OrdinalIgnoreCase.Equals(path,project.RootPath))return(new(null,null),null);
                path=Path.GetDirectoryName(path)??project.RootPath;
            }
        }
    }
    private async Task DisplayBrowserAsync(BrowserLocation location)
    {
        browserProject=location.Project;browserDirectory=location.Path;selectedArtifactId=null;
        restoringBrowserSelection=true;try{ArtifactList.SelectedItems.Clear();}finally{restoringBrowserSelection=false;}
        var wasUpdating=updating;updating=true;try{SearchBox.Text="";}finally{updating=wasUpdating;}
        await RefreshLibraryAsync();
    }
    private async Task MoveBrowserHistoryAsync(int direction)
    {
        if(await browserHistory.MoveAsync(direction,async location=>await ResolveBrowserAsync(location,false) is not null))await DisplayBrowserAsync(browserHistory.Current);
        else await RefreshLibraryAsync();
        UpdateBrowserControls();
    }
    private async Task<bool> ExecuteBrowserCommandAsync(BrowserCommand command)
    {
        if(command==BrowserCommand.None||!ready||closing||currentPage!="library"||operation is not null)return false;
        await RunAsync("浏览目录",async _=>
        {
            switch(command)
            {
                case BrowserCommand.Back:await MoveBrowserHistoryAsync(-1);break;
                case BrowserCommand.Forward:await MoveBrowserHistoryAsync(1);break;
                case BrowserCommand.Open:
                    var selected=ArtifactList.SelectedItems.OfType<ArtifactItem>().ToArray();
                    if(selected is [ {IsDirectory:true} directory ])await ShowBrowserAsync(directory.Project,directory.Path);
                    break;
                case BrowserCommand.Up:
                    if(browserProject is not null)
                        await ShowBrowserAsync(StringComparer.OrdinalIgnoreCase.Equals(browserDirectory,browserProject.RootPath)?null:browserProject,Path.GetDirectoryName(browserDirectory));
                    break;
                case BrowserCommand.Refresh:await RefreshLibraryAsync();break;
                case BrowserCommand.SelectAll:ArtifactList.SelectAll();break;
                case BrowserCommand.CopyPaths:await CopyBrowserPathsAsync();break;
            }
        });
        UpdateBrowserControls();return true;
    }
    private async void BrowserBack(object sender,RoutedEventArgs e)=>await ExecuteBrowserCommandAsync(BrowserCommand.Back);
    private async void BrowserForward(object sender,RoutedEventArgs e)=>await ExecuteBrowserCommandAsync(BrowserCommand.Forward);
    private async void BrowserRefresh(object sender,RoutedEventArgs e)=>await ExecuteBrowserCommandAsync(BrowserCommand.Refresh);
    private async void CopyBrowserPaths(object sender,RoutedEventArgs e)=>await ExecuteBrowserCommandAsync(BrowserCommand.CopyPaths);
    private async void BrowserSortChanged(object sender,SelectionChangedEventArgs e)
    {
        if(!ready||updating)return;
        if(operation is not null)
        {updating=true;try{BrowserSortChoice.SelectedIndex=(int)browserSort;}finally{updating=false;}return;}
        browserSort=(BrowserSortField)Math.Max(0,BrowserSortChoice.SelectedIndex);
        await RunAsync("排序当前目录",_=>RefreshLibraryAsync());
    }
    private async void BrowserSortDirection(object sender,RoutedEventArgs e)
    {
        if(!ready||operation is not null)return;
        browserDescending=!browserDescending;SortDirectionButton.Content=browserDescending?"降序":"升序";
        await RunAsync("排序当前目录",_=>RefreshLibraryAsync());
    }
    private void UpdateBrowserControls()
    {
        ParseSelectedButton.IsEnabled=operation is null&&!ShowingBrowserCache&&!parsingFiles;
        BrowserBackButton.IsEnabled=browserHistory.CanGoBack;BrowserForwardButton.IsEnabled=browserHistory.CanGoForward;BrowserUpButton.IsEnabled=browserProject is not null;
        foreach(var button in new[]{BrowserBackButton,BrowserForwardButton,BrowserUpButton})button.Opacity=button.IsEnabled?1:.35;
        var count=ArtifactList.Items.Count;var selected=ArtifactList.SelectedItems.Count;
        LibraryCount.Text=selected==0?$"{count} 项":$"{count} 项 · 已选 {selected} 项";
    }
    private async void BrowserDoubleTapped(object sender,DoubleTappedRoutedEventArgs e)
    {
        if(operation is not null||BrowserItemFromSource(e.OriginalSource as DependencyObject) is not {IsDirectory:true} item)return;
        e.Handled=true;
        await RunAsync("打开目录",_=>ShowBrowserAsync(item.Project,item.Path));
    }
    private ArtifactItem? BrowserItemFromSource(DependencyObject? source)
    {
        for(var current=source;current is not null;current=VisualTreeHelper.GetParent(current))
        {
            if(current is GridViewItem container)return ArtifactList.ItemFromContainer(container) as ArtifactItem;
            if(ReferenceEquals(current,ArtifactList))break;
        }
        return null;
    }
    private async void BrowserPointerPressed(object sender,PointerRoutedEventArgs e)
    {
        var kind=e.GetCurrentPoint(Root).Properties.PointerUpdateKind;
        var command=BrowserInput.SideButton(kind==PointerUpdateKind.XButton1Pressed,kind==PointerUpdateKind.XButton2Pressed);
        if(!CanRouteBrowserInput(e.OriginalSource as DependencyObject,false)||command==BrowserCommand.None)return;
        e.Handled=true;await ExecuteBrowserCommandAsync(command);
    }
    private async void BrowserKeyDown(object sender,KeyRoutedEventArgs e)
    {
        var focused=FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
        TraceBrowserSmokeKey(e,focused);
        if(!CanRouteBrowserInput(focused,true))return;
        var focusAncestor=focused;
        while(focusAncestor is not null&&!ReferenceEquals(focusAncestor,ArtifactList))focusAncestor=VisualTreeHelper.GetParent(focusAncestor);
        var restoreGridFocus=focusAncestor is not null;
        static bool Down(VirtualKey key)=>(InputKeyboardSource.GetKeyStateForCurrentThread(key)&CoreVirtualKeyStates.Down)!=0;
        var command=BrowserInput.Key((int)e.Key,Down(VirtualKey.Menu),Down(VirtualKey.Control),Down(VirtualKey.Shift),false);
        if(command==BrowserCommand.None)return;
        if(command==BrowserCommand.Open)
        {
            // Enter on a toolbar button belongs to that button, not the grid selection.
            var target=focused;
            while(target is not null&&!ReferenceEquals(target,ArtifactList))target=VisualTreeHelper.GetParent(target);
            if(target is null)return;
        }
        e.Handled=true;await ExecuteBrowserCommandAsync(command);
        // Replacing grid items can move focus to a neighbouring filter. Keep repeated
        // keyboard navigation in the browser, unless the user started editing text.
        if(restoreGridFocus&&!closing&&currentPage=="library"&&command is BrowserCommand.Back or BrowserCommand.Forward or BrowserCommand.Up or BrowserCommand.Open or BrowserCommand.Refresh)
        {
            var next=FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject;
            var textInput=false;
            for(var node=next;node is not null;node=VisualTreeHelper.GetParent(node))
                if(node is TextBox or PasswordBox or RichEditBox or AutoSuggestBox){textInput=true;break;}
            if(!textInput)
            {
                ArtifactList.UpdateLayout();
                var item=ArtifactList.SelectedItems.Cast<object>().LastOrDefault()??ArtifactList.Items.Cast<object>().FirstOrDefault();
                if(item is not null)
                {
                    ArtifactList.ScrollIntoView(item);ArtifactList.UpdateLayout();
                    if(ArtifactList.ContainerFromItem(item) is Control container)container.Focus(FocusState.Programmatic);
                }
                else ArtifactList.Focus(FocusState.Programmatic);
            }
        }
    }
    private bool CanRouteBrowserInput(DependencyObject? source,bool keyboard)
    {
        if(!ready||closing||currentPage!="library"||operation is not null||VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot).Any(x=>x.IsOpen&&x.Child is not ToolTip))return false;
        for(var current=source;current is not null;current=VisualTreeHelper.GetParent(current))
        {
            if(keyboard&&(current is TextBox or PasswordBox or RichEditBox or AutoSuggestBox or ComboBox||current is TextBlock{IsTextSelectionEnabled:true}))return false;
            if(ReferenceEquals(current,LibraryPage))return true;
        }
        return false;
    }
    private async Task<string?> ValidBrowserPathAsync(ArtifactItem item)
    {
        if(ShowingBrowserCache||item.Project is null||item.Path is null)return null;
        if(item.IsDirectory)
        {
            var resolved=await ResolveBrowserAsync(new(item.Project,item.Path),false);
            return resolved?.Location.Path;
        }
        return await SourceStore.Catalog.ValidateFileAsync(item.Project,item.Path);
    }
    private async Task CopyBrowserPathsAsync()
    {
        var paths=new List<string>();
        foreach(var item in ArtifactList.SelectedItems.OfType<ArtifactItem>())
        {
            try{if(await ValidBrowserPathAsync(item) is { } path)paths.Add(path);}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){ }
        }
        if(paths.Count==0){StatusText.Text="先选择可用的文件或目录。";return;}
        var package=new DataPackage();package.SetText(string.Join(Environment.NewLine,paths));Clipboard.SetContent(package);
        StatusText.Text=$"已复制 {paths.Count} 个路径";
    }
    private async void RevealArtifact(object sender,RoutedEventArgs e)
    {
        var item=ArtifactList.SelectedItems.OfType<ArtifactItem>().LastOrDefault();if(item is null||operation is not null)return;
        await RunAsync("定位原件",async _=>
        {
            if(await ValidBrowserPathAsync(item) is not { } path){StatusText.Text="原件已不可用，请刷新目录。";return;}
            Process.Start(new ProcessStartInfo("explorer.exe"){ArgumentList={"/select,",path},UseShellExecute=false});StatusText.Text="已在资源管理器定位";
        });
    }
    private void BrowserContextRequested(UIElement sender,ContextRequestedEventArgs e)
    {
        if(operation is not null)return;
        if(BrowserItemFromSource(e.OriginalSource as DependencyObject) is { } item&&!ArtifactList.SelectedItems.Contains(item))
        {ArtifactList.SelectedItems.Clear();ArtifactList.SelectedItems.Add(item);}
        var selected=ArtifactList.SelectedItems.OfType<ArtifactItem>().ToArray();if(selected.Length==0)return;
        var menu=new MenuFlyout();
        void Add(string text,RoutedEventHandler action,bool enabled=true){var entry=new MenuFlyoutItem{Text=text,IsEnabled=enabled};entry.Click+=action;menu.Items.Add(entry);}
        Add("复制路径",CopyBrowserPaths);Add("在资源管理器中定位",RevealArtifact,selected.Length==1);Add("解析所选文件",ParseSelectedFiles,selected.Any(x=>!x.IsDirectory));
        if(e.TryGetPosition(ArtifactList,out var point))menu.ShowAt(ArtifactList,point);else menu.ShowAt(ArtifactList);
        e.Handled=true;
    }
}
