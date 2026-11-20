using Amifolio.Core;
using Amifolio.Context;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private Task? legacyRetirement;
    private Uri releasePage=GitHubReleaseService.RepoReleasesPage;
    private Func<Task<ReleaseCheckResult>>? smokeReleaseCheck;
    private Task RetireLegacyInBackgroundAsync()
    {
        if(legacyRetirement is {IsCompleted:false})return legacyRetirement;
        legacyRetirement=RetireLegacyCoreAsync();return legacyRetirement;
    }
    private async Task RetireLegacyCoreAsync()
    {
        try
        {
            await new LegacyDataService(dataRoot).MigrateAsync(backgroundTasks.Token);
            if(!closing)StorageStatusText.Text="旧链路已停用，原有资料保留在只读历史中。";
            // At most once per day; never start an AI request or compact authoritative memory.
            await context.Learning.CompactIfDueAsync(backgroundTasks.Token);
        }
        catch(OperationCanceledException)when(backgroundTasks.IsCancellationRequested){}
        catch(Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Data retirement: {0}",ex.GetType().Name);
            if(!closing)StorageStatusText.Text="旧资料未整理完成，现有资料保留。请退出旧版 Amifolio 后重试。";
        }
    }
    private async void RetryLegacyMigration(object sender,RoutedEventArgs e)=>await RetireLegacyInBackgroundAsync();
    private async Task<IReadOnlyList<LegacyDocument>> LoadLegacyHistoryAsync()
    {
        await RetireLegacyInBackgroundAsync();
        return await new LegacyDataService(dataRoot).ReadHistoryAsync(backgroundTasks.Token);
    }
    private async void OpenLegacyHistory(object sender,RoutedEventArgs e)=>await OpenLegacyHistoryAsync();
    private async Task OpenLegacyHistoryAsync()
    {
        HistoryButton.IsEnabled=false;
        try
        {
            var documents=await LoadLegacyHistoryAsync();if(closing)return;
            if(documents.Count==0){await ShowMessageAsync("旧资料","暂无旧版文档。历史文件可在数据目录的 history 文件夹查看。");return;}
            var list=new ListView{Height=140,SelectionMode=ListViewSelectionMode.Single};
            var preview=new TextBox{IsReadOnly=true,AcceptsReturn=true,TextWrapping=TextWrapping.Wrap,Height=300};
            ScrollViewer.SetVerticalScrollBarVisibility(preview,ScrollBarVisibility.Auto);
            list.ItemsSource=documents.Select((d,i)=>new HistoryRow(i,HistoryScopeTitle(d.Scope)+" · "+HistoryTitle(d.Name))).ToArray();
            list.DisplayMemberPath=nameof(HistoryRow.Title);
            list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is HistoryRow row)preview.Text=documents[row.Index].Text;};
            list.SelectedIndex=0;
            var panel=new StackPanel{Spacing=12,Width=560};
            panel.Children.Add(new TextBlock{Text="仅供查看，不参与会话学习。",Style=(Style)Application.Current.Resources["Caption"]});
            panel.Children.Add(list);panel.Children.Add(preview);
            await ShowMessageAsync("旧资料",panel);
        }
        catch(OperationCanceledException){}
        catch(Exception){if(!closing)await ShowMessageAsync("无法查看旧资料","历史资料保留，请检查数据目录权限与文件格式。");}
        finally{if(!closing)HistoryButton.IsEnabled=true;}
    }
    private sealed record HistoryRow(int Index,string Title);
    private string HistoryScopeTitle(string scope)
    {
        if(scope=="global")return "全局";
        var project=projects.FirstOrDefault(p=>"project-"+Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(p.Id)))==scope);
        return project?.Name??("项目 "+scope.Replace("project-","")[..Math.Min(8,scope.Replace("project-","").Length)]);
    }
    private static string HistoryTitle(string name)=>name.Replace("Documents / ","").Replace("Candidates / ","候选 · ").Replace("ExternalEdits / ","外部修改 · ").Replace("style.md","风格").Replace("memory.md","记忆").Replace("overview.md","概况").Replace("summary.md","总结");
    private async void CheckRelease(object sender,RoutedEventArgs e)=>await CheckReleaseAsync();
    private async Task CheckReleaseAsync()
    {
        if(!CheckUpdatesButton.IsEnabled)return;CheckUpdatesButton.IsEnabled=false;ReleaseStatusText.Text="正在检查";
        try
        {
            var result=smokeReleaseCheck is not null&&Option("--smoke-report") is not null?await smokeReleaseCheck():await new GitHubReleaseService().CheckAsync(typeof(MainWindow).Assembly.GetName().Version?.ToString()??string.Empty,backgroundTasks.Token);
            if(closing)return;ReleaseStatusText.Text=result.Message;releasePage=result.ReleasePage;
        }
        catch(OperationCanceledException){if(!closing)ReleaseStatusText.Text="检查已停止，可重试。";}
        catch(Exception){if(!closing)ReleaseStatusText.Text="暂时无法检查，请打开下载页面查看。";}
        finally{if(!closing)CheckUpdatesButton.IsEnabled=true;}
    }
    private void OpenReleasePage(object sender,RoutedEventArgs e)=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(releasePage.AbsoluteUri){UseShellExecute=true});
}
