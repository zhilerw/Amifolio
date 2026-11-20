using Amifolio.Core;
using Amifolio.Context;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private sealed record TaskRow(WorkItem Item,string Title,string Detail,string State);
    private async Task ResumeStartupTasksAsync()
    {
        try
        {
            await ((PortfolioStore)store).ResumePendingAsync(backgroundTasks.Token);
            await context.ReconcileGenerationTasksAsync(backgroundTasks.Token);
            if(!closing&&currentPage=="tasks")await LoadTasksAsync();
        }
        catch(OperationCanceledException){}
        catch(Exception){if(!closing)StatusText.Text="部分任务未完成，请在“设置 → 日志”中查看。";}
    }
    private async Task LoadTasksAsync()
    {
        var tasks=new TaskStore(dataRoot);await tasks.RecoverAsync();await context.ReconcileGenerationTasksAsync();
        var artifacts=(await store.ListAsync()).ToDictionary(x=>x.Id);var selected=(TaskList.SelectedItem as TaskRow)?.Item.Id;
        TaskList.ItemsSource=(await tasks.ListAsync()).Select(x=>new TaskRow(x,x.ArtifactId is not null&&artifacts.TryGetValue(x.ArtifactId,out var a)?a.Title:x.ProjectId is null?"历史全局任务":"历史项目任务",
            $"{x.KindLabel} · {x.UpdatedAt.LocalDateTime:MM-dd HH:mm} · 已尝试 {x.Attempts} 次"+(string.IsNullOrEmpty(x.ErrorLabel)?"":" · "+x.ErrorLabel),x.StateLabel)).ToArray();
        if(selected is not null)TaskList.SelectedItem=TaskList.Items.OfType<TaskRow>().FirstOrDefault(x=>x.Item.Id==selected);
        UpdateTaskSelection();
    }
    private async void RefreshTasks(object sender,RoutedEventArgs e)
    {try{await LoadTasksAsync();}catch(Exception){StatusText.Text="无法读取日志，请稍后重试。";}}
    private void TaskSelectionChanged(object sender,SelectionChangedEventArgs e)=>UpdateTaskSelection();
    private void UpdateTaskSelection()
    {
        var item=(TaskList.SelectedItem as TaskRow)?.Item;
        RetryTaskButton.IsEnabled=operation is null&&item?.Kind!="generate"&&item?.State is "failed" or "cancelled" or "interrupted";
        CancelTaskButton.IsEnabled=item?.Kind!="generate"&&item?.State is "queued" or "running";
        TaskDetail.Text=item is null?"最近 200 项。关闭应用会停止当前处理，原件保留。":$"{item.KindLabel} · {item.StateLabel}"+(string.IsNullOrEmpty(item.ErrorLabel)?"":" · "+item.ErrorLabel)+(item.Kind=="generate"?" · 旧版生成记录仅供查阅":"");
    }
    private async void CancelTask(object sender,RoutedEventArgs e)
    {
        if(TaskList.SelectedItem is not TaskRow row||row.Item.Kind=="generate")return;
        try{await new TaskStore(dataRoot).CancelAsync(row.Item.Id);await LoadTasksAsync();StatusText.Text="任务已取消，原件保留。";}
        catch(Exception){StatusText.Text="取消未完成，请刷新任务状态。";}
    }
    private async void RetryTask(object sender,RoutedEventArgs e)
    {
        if(TaskList.SelectedItem is not TaskRow row||row.Item.Kind=="generate")return;
        await RunAsync("正在重试",async ct=>{
            await new TaskStore(dataRoot).RetryAsync(row.Item.Id,ct);
            await ((PortfolioStore)store).ProcessTaskAsync(row.Item.Id,ct);
            await LoadTasksAsync();StatusText.Text="任务状态已更新";
        });
        UpdateTaskSelection();
    }
}
