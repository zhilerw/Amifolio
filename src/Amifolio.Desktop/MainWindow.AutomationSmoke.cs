using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    // The automation scenario now verifies current session-learning consent and model controls.
    private async Task<object> RunAutomationRegressionAsync()
    {
        var checks=new List<string>();
        void Check(bool value,string name){if(!value)throw new InvalidOperationException("Learning permission UI: "+name);checks.Add(name);}
        async Task Idle(){using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));while(operation is not null||learningUiBusy)await Task.Delay(20,timeout.Token);}
        var project=await SetupCatalogSmokeAsync();
        var source=Path.Combine(project.RootPath,"notes.md");var original=await File.ReadAllBytesAsync(source);var stamp=File.GetLastWriteTimeUtc(source);
        await NavigateAsync("context");
        Check(context.LegacyRetired&&currentPage=="learning"&&!pages.ContainsKey("context"),"legacy-retired-and-main-route-canonical");
        Check(ContextPage.Visibility==Visibility.Collapsed&&LearningPage.Back.Visibility==Visibility.Collapsed,"retired-page-hidden-without-test-route");
        Check(ContextNav.Style==(Style)Application.Current.Resources["SelectedNavButton"],"main-learning-highlights-context");
        await OpenLearningPageAsync(false,project.Id,true);
        Check(!LearningPage.Capture.IsEnabled&&!editingLearningSettings,"consent-opens-read-only");
        BeginLearningSettingsSmoke();
        Check(LearningPage.Capture.IsEnabled&&!LearningPage.Remote.IsEnabled,"chat-consent-edit-needs-no-remote-service");
        LearningPage.Capture.IsChecked=true;LearningPage.Learn.IsChecked=true;LearningPage.Batch.Value=1;
        smokeDialogAnswers.Enqueue(false);await LearningUiAsync(SaveLearningSettingsAsync);
        Check(!(await context.Learning.PolicyAsync(project.Id)).Capture&&LearningSettingsDirty,"declined-consent-keeps-draft-and-no-write");
        smokeDialogAnswers.Enqueue(true);await LearningUiAsync(SaveLearningSettingsAsync);
        var policy=await context.Learning.PolicyAsync(project.Id);
        Check(policy.Capture&&policy.Learn&&!policy.Share&&!policy.Remote,"capture-learning-sharing-remote-independent");
        await RequestNavigationAsync("help");
        ModelEndpoint.Text="https://synthetic.invalid/v1";ModelName.Text="synthetic-learning-model";ModelKey.Password="synthetic-learning-key";RemoteAllowed.IsOn=true;
        SaveModelConfiguration(this,new RoutedEventArgs());await Idle();
        Check((await modelConfiguration.LoadAsync()).RemoteAllowed&&await new TaskStore(dataRoot).RemoteAllowedAsync()==true,"model-save-persists-explicit-remote-permission");
        Check(!(await context.Learning.PolicyAsync(project.Id)).Remote,"model-config-does-not-authorize-project-send");
        await OpenLearningPageAsync(false,project.Id,true);BeginLearningSettingsSmoke();LearningPage.Mode.SelectedIndex=1;
        Check(LearningPage.Remote.IsEnabled&&(string)LearningPage.Process.Content=="立即整合","background-mode-exposes-project-send-consent");
        LearningPage.Remote.IsChecked=true;smokeDialogAnswers.Enqueue(true);await LearningUiAsync(SaveLearningSettingsAsync);
        policy=await context.Learning.PolicyAsync(project.Id);
        Check(policy.Remote&&policy.Endpoint=="https://synthetic.invalid/v1"&&!policy.Share,"project-send-binds-saved-endpoint-without-sharing");
        BeginLearningSettingsSmoke();LearningPage.Share.IsChecked=true;smokeDialogAnswers.Enqueue(true);await LearningUiAsync(SaveLearningSettingsAsync);
        Check((await context.Learning.PolicyAsync(project.Id)).Share&&File.Exists(context.Learning.ProjectionPath(project.Id)),"share-publishes-canonical-learning-entry");
        await RequestNavigationAsync("help");RemoteAllowed.IsOn=false;await remotePermissionUpdate;
        Check(await new TaskStore(dataRoot).RemoteAllowedAsync()==false,"model-permission-can-be-withdrawn");
        Check((await context.Learning.PolicyAsync(project.Id)).Share,"remote-withdrawal-does-not-change-share");
        ClearModelConfiguration(this,new RoutedEventArgs());await Idle();
        Check((await modelConfiguration.LoadAsync()).ApiKey==""&&!RemoteAllowed.IsOn,"clear-model-removes-key-and-send-permission");
        await OpenLearningPageAsync(false,project.Id,true);BeginLearningSettingsSmoke();LearningPage.Share.IsChecked=false;LearningPage.Remote.IsChecked=false;smokeDialogAnswers.Enqueue(true);await LearningUiAsync(SaveLearningSettingsAsync);
        Check(!File.Exists(context.Learning.ProjectionPath(project.Id))&&!(await context.Learning.PolicyAsync(project.Id)).Share,"share-off-withdraws-learning-projection");
        Check((await File.ReadAllBytesAsync(source)).SequenceEqual(original)&&File.GetLastWriteTimeUtc(source)==stamp,"consent-ui-preserves-original-bytes-and-time");
        Check(smokeDialogAnswers.Count==0&&smokeMessages.Count==0,"all-current-ui-confirmations-observed");
        return new{checks,providerRequests=0,syntheticOnly=true};
    }
}
