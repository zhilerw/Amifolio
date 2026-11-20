using Amifolio.Context;
using Microsoft.UI.Xaml;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer learningTimer=new(){Interval=TimeSpan.FromSeconds(20)};
    private CancellationTokenSource? learningRequest;
    private bool learningWorkRunning,learningTickRunning;
    private int learningProjectCursor;
    private void StartLearningProcessing()
    {
        if(Option("--smoke-report") is not null)return;
        learningTimer.Tick-=LearningProcessingTick;learningTimer.Tick+=LearningProcessingTick;learningTimer.Start();
    }
    private void StopLearningProcessing(){learningTimer.Stop();learningTimer.Tick-=LearningProcessingTick;learningRequest?.Cancel();}
    private async void LearningProcessingTick(object? sender,object e)
    {
        if(closing||!ready||!catalogVerified||learningTickRunning||learningWorkRunning)return;
        learningTickRunning=true;
        try
        {
            var projects=await context.Learning.AuthorizedProjectsAsync(backgroundTasks.Token);
            if(projects.Count==0)return;
            for(var i=0;i<Math.Min(16,projects.Count);i++)
            {
                var id=projects[(learningProjectCursor++)%projects.Count];
                if(!SourceStore.CodexProjects.Any(p=>p.Id==id))continue;
                var snapshot=await context.Learning.SnapshotAsync(id,backgroundTasks.Token);
                if(snapshot.Policy.Mode!="background"||!snapshot.Policy.Remote||!snapshot.Policy.Learn||snapshot.Pending==0)continue;
                var saved=await modelConfiguration.LoadAsync(backgroundTasks.Token);
                if(!saved.RemoteAllowed||saved.ApiKey.Length==0||saved.Model.Length==0)break;
                await RunLearningWorkAsync(id,saved,false);break;
            }
            if(currentPage=="learning"&&!editingLearning&&!LearningSettingsDirty)await RefreshLearningPageSafelyAsync();
        }
        catch(OperationCanceledException){}
        catch(Exception){if(!closing&&currentPage=="learning")LearningPage.SettingsInfo.Text="后台检查未完成，可刷新日志查看。";}
        finally{learningTickRunning=false;}
    }
    private async Task RunLearningWorkAsync(string scope,SavedModelConfiguration saved,bool force)
    {
        if(learningWorkRunning||closing)return;learningWorkRunning=true;
        using var request=CancellationTokenSource.CreateLinkedTokenSource(backgroundTasks.Token);learningRequest=request;
        try
        {
            await remotePermissionUpdate;
            await context.RunLearningBackgroundAsync(scope,integration.CodexHome,new(saved.Endpoint,saved.Model,saved.RemoteAllowed),saved.ApiKey,force,request.Token);
            if(!closing&&currentPage=="learning"&&LearningScopeId==scope&&!editingLearning&&!LearningSettingsDirty)await RefreshLearningPageSafelyAsync();
        }
        catch(OperationCanceledException){}
        catch(Exception){if(!closing&&currentPage=="learning")LearningPage.SettingsInfo.Text="后台整合未完成，请在处理日志中查看；不会自动重发。";}
        finally{if(ReferenceEquals(learningRequest,request))learningRequest=null;learningWorkRunning=false;}
    }
}
