using Amifolio.Core;
using Microsoft.UI.Xaml;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    // Hidden compatibility handlers no longer have a test-only path into retired UI.
    private static Task PauseContextRaceAsync(string point)=>Task.CompletedTask;

    private async Task<object> RunContextRaceRegressionAsync()
    {
        var checks=new List<string>();
        void Check(bool value,string name){if(!value)throw new InvalidOperationException("Learning draft and scope UI: "+name);checks.Add(name);}
        var project=SourceStore.CodexProjects.Single();
        await OpenLearningPageAsync(false,project.Id,true);BeginLearningSettingsSmoke();LearningPage.Share.IsChecked=true;
        var revision=(await context.Learning.PolicyAsync(project.Id)).Revision;
        await RefreshLearningPageAsync(true);
        Check(editingLearningSettings&&LearningSettingsDirty&&LearningPage.Share.IsChecked==true,"background-refresh-preserves-permission-draft");
        smokeDialogAnswers.Enqueue(false);
        Check(!await RequestNavigationAsync("settings")&&LearningSettingsDirty&&currentPage=="learning","declined-navigation-keeps-draft-and-scope");
        smokeDialogAnswers.Enqueue(true);
        Check(await RequestNavigationAsync("settings")&&!editingLearningSettings,"accepted-navigation-clears-unsaved-permissions");
        Check((await context.Learning.PolicyAsync(project.Id)).Revision==revision,"discard-does-not-write-consent");
        await OpenLearningPageAsync(false,project.Id,true);BeginLearningSettingsSmoke();LearningPage.Share.IsChecked=true;
        var durable=await context.Learning.PolicyAsync(project.Id);
        var concurrent=await context.Learning.SetPolicyAsync(project.Id,durable with{AutoActivate=!durable.AutoActivate},durable.Revision);
        smokeDialogAnswers.Enqueue(true);var rejected=false;
        try{await SaveLearningSettingsAsync();}catch(InvalidOperationException){rejected=true;}
        Check(rejected&&editingLearningSettings&&LearningSettingsDirty,"stale-consent-save-rejected-with-draft-retained");
        Check((await context.Learning.PolicyAsync(project.Id))==concurrent,"stale-form-cannot-overwrite-new-consent");
        CancelLearningSettings();await RefreshLearningPageAsync();
        Check(!LearningSettingsDirty&&LearningPage.Activate.IsChecked==concurrent.AutoActivate,"refresh-reconciles-authoritative-consent");
        await RequestNavigationAsync("settings");await OpenLearningPageAsync(false);
        Check(learningReturnPage=="settings"&&LearningPage.Back.Visibility==Visibility.Visible,"settings-learning-retains-return");
        await RequestNavigationAsync("tasks");await OpenLearningPageAsync(true);
        Check(learningReturnPage=="tasks"&&LearningPage.Kind.SelectedIndex==1&&LearningPage.Back.Visibility==Visibility.Visible,"logs-open-conversation-view-with-return");
        await RequestNavigationAsync("context");
        Check(currentPage=="learning"&&learningReturnPage=="context"&&LearningPage.Back.Visibility==Visibility.Collapsed,"main-navigation-resets-secondary-return");
        await RequestNavigationAsync("library");await ShowBrowserAsync(null);await OpenLibraryContextAsync();
        Check(LearningScopeId is null&&loadedLearningScope is null,"all-projects-browser-opens-global-learning");
        await RequestNavigationAsync("library");await ShowBrowserAsync(project);await OpenLibraryContextAsync();
        Check(LearningScopeId==project.Id&&loadedLearningScope==project.Id,"project-browser-overrides-previous-global-learning-scope");
        Check(smokeDialogAnswers.Count==0&&smokeMessages.Count==0,"draft-and-scope-confirmations-observed");
        return new{checks,syntheticOnly=true,physicalInput=false};
    }
}
