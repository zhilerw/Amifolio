using Microsoft.UI.Xaml;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private Task? environmentInspection;
    private int environmentVersion;
    private string? environmentRequestedScope;
    private Task RefreshEnvironmentAsync()
    {
        var scope=SelectedScope(IntegrationScope)?.Id;
        if(environmentInspection is {IsCompleted:false}&&environmentRequestedScope==scope)return environmentInspection;
        environmentRequestedScope=scope;var version=++environmentVersion;
        environmentInspection=InspectEnvironmentCoreAsync(scope,version);return environmentInspection;
    }
    private async Task InspectEnvironmentCoreAsync(string? scope,int version)
    {
        try
        {
            var skill=await BundledSkillAsync(backgroundTasks.Token);
            var path=DeployPath(scope);
            var checks=await Task.Run(()=>integration.InspectEnvironmentAsync(BridgeExecutable(),dataRoot,skill,
                path,backgroundTasks.Token),backgroundTasks.Token);
            if(closing||version!=environmentVersion)return;
            EnvironmentChecks.ItemsSource=checks;
            var incomplete=checks.Any(c=>c.State is "missing" or "conflict" or "invalid" or "error" or "unreadable");
            EnvironmentBadge.Visibility=incomplete?Visibility.Visible:Visibility.Collapsed;
            EnvironmentSummary.Text=incomplete?"本机配置待完善 · Codex 连接尚未验证":"本机检查完成 · Codex 连接尚未验证";
        }
        catch(OperationCanceledException)when(backgroundTasks.IsCancellationRequested){}
        catch(Exception)
        {
            if(closing||version!=environmentVersion)return;
            EnvironmentBadge.Visibility=Visibility.Visible;
            EnvironmentSummary.Text="检测未完成，请检查便携包组件后重试。";
        }
    }
    private async void InspectEnvironment(object sender,RoutedEventArgs e)=>await RefreshEnvironmentAsync();
}
