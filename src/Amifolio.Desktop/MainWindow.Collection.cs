using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private void ShowCollectionActions(object sender,RoutedEventArgs e)
    {
        var flyout=new MenuFlyout();
        void Item(string text,RoutedEventHandler handler){var item=new MenuFlyoutItem{Text=text};item.Click+=handler;flyout.Items.Add(item);}
        Item("扫描Codex项目",ScanCodexProjects);Item("刷新当前目录",RefreshDirectory);Item("解析所选文件",ParseSelectedFiles);
        flyout.Items.Add(new MenuFlyoutSeparator());Item("风格与记忆",ApplyLibraryMemory);
        flyout.ShowAt((FrameworkElement)sender);
    }
    private async void ScanCodexProjects(object sender,RoutedEventArgs e)=>await RefreshCatalogInBackgroundAsync();
    private async void RefreshDirectory(object sender,RoutedEventArgs e)=>await RunAsync("刷新当前目录",_=>RefreshLibraryAsync());
    private Task LoadCollectionGrantStatusAsync()
    {
        CollectionGrantStatus.Text=$"按本机Codex项目结构只读扫描；当前 {SourceStore.CodexProjects.Count} 个项目。解析由用户选择，不复制原件。结束钩子只刷新索引。";
        return Task.CompletedTask;
    }
    private async Task<bool> OpenLibraryContextAsync()
    {
        if(operation is not null)return false;
        if(ShowingBrowserCache){StatusText.Text="目录核对完成后可使用项目学习。";return false;}
        // The browser location owns the scope, independently of any previously viewed page.
        var project=browserProject?.Id;
        return await OpenLearningPageAsync(false,project,true);
    }
    private async void GenerateLibraryMemory(object sender,RoutedEventArgs e)=>await ApplyLibraryMemoryAsync();
    private async void ApplyLibraryMemory(object sender,RoutedEventArgs e)=>await ApplyLibraryMemoryAsync();
    private async Task ApplyLibraryMemoryAsync()
    {
        try{await OpenLibraryContextAsync();}catch(Exception ex){await ShowMessageAsync("操作未完成",ex.Message);}
    }
    private async void ApplyStyleMemory(object sender,RoutedEventArgs e)=>await OpenLearningPageAsync(false);
}
