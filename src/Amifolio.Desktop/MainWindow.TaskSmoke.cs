using Amifolio.Core;
using Microsoft.UI.Xaml;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private async Task<object> RunTaskRegressionAsync()
    {
        var checks=new List<string>();void Check(bool value,string name){if(!value)throw new InvalidOperationException("Task regression: "+name);checks.Add(name);}
        var project=await SetupCatalogSmokeAsync("synthetic-task.md",false);
        var source=Path.Combine(project.RootPath,"synthetic-task.md");
        var artifact=await SourceStore.ReferenceFileAsync(source,project.Id);await SourceStore.ParseReferenceAsync(artifact.Artifact.Id);
        await ShowBrowserAsync(project);await NavigateAsync("tasks");
        Check(TaskList.Items.Count==1,"import-has-task-row");var row=TaskList.Items.OfType<TaskRow>().Single();
        Check(row.Item.State=="completed","worker-completion-visible");TaskList.SelectedItem=row;
        Check(!CancelTaskButton.IsEnabled&&!RetryTaskButton.IsEnabled,"completed-actions-disabled");
        var tasks=new TaskStore(dataRoot);var id=await tasks.EnqueueExtractionAsync(artifact.Artifact.Id);await LoadTasksAsync();
        TaskList.SelectedItem=TaskList.Items.OfType<TaskRow>().Single(x=>x.Item.Id==id);Check(CancelTaskButton.IsEnabled,"queued-cancel-enabled");
        CancelTask(this,new RoutedEventArgs());
        using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)))while((await tasks.GetAsync(id))?.State!="cancelled")await Task.Delay(20,timeout.Token);
        await LoadTasksAsync();TaskList.SelectedItem=TaskList.Items.OfType<TaskRow>().Single(x=>x.Item.Id==id);
        Check(RetryTaskButton.IsEnabled&&!CancelTaskButton.IsEnabled,"cancelled-retry-enabled");RetryTask(this,new RoutedEventArgs());
        while(operation is not null)await Task.Delay(20);
        Check((await tasks.GetAsync(id))?.State=="completed","retry-completes-same-task");
        Check((await store.ListAsync()).Count==1,"retry-does-not-duplicate-original");
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var busy=RunAsync("任务测试",_=>gate.Task);
        Check(await RequestNavigationAsync("library"),"navigation-during-processing");
        Check(ArtifactList.Items.Count==1,"library-readable-during-processing");
        SearchBox.Text="no-such-synthetic-document";
        using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)))while(ArtifactList.Items.Count!=0)await Task.Delay(20,timeout.Token);
        Check(operation is not null&&ArtifactList.Items.Count==0,"search-during-processing");
        SearchBox.Text="synthetic-task";
        using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5)))while(ArtifactList.Items.Count!=1)await Task.Delay(20,timeout.Token);
        Check(ArtifactList.Items.Count==1,"search-updated-results");
        Check(await RequestNavigationAsync("context")&&currentPage=="learning"&&operation is not null,"canonical-learning-route-preserves-active-operation");
        await RequestNavigationAsync("help");RemoteAllowed.IsOn=true;await remotePermissionUpdate;
        var model=await tasks.EnqueueGenerationAsync(null,new string('a',64),0);RemoteAllowed.IsOn=false;await remotePermissionUpdate;
        Check((await tasks.GetAsync(model))?.ErrorCode=="permission_revoked","remote-toggle-revokes-queued-model");
        Check(await new TaskStore(dataRoot).RemoteAllowedAsync()==false,"remote-revocation-persists");
        gate.TrySetResult();await busy;await NavigateAsync("tasks");
        SearchBox.Text="";
        TaskList.SelectedItem=TaskList.Items.OfType<TaskRow>().Single(x=>x.Item.Id==model);
        Check(!RetryTaskButton.IsEnabled&&!CancelTaskButton.IsEnabled,"legacy-generation-history-is-read-only");
        Check((await new TaskStore(dataRoot).ListAsync()).Count==3,"task-history-persists");
        return new{checks};
    }
}
