using Microsoft.UI.Xaml;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    // Runs only with an explicit developer mode and uses the supplied isolated data root.
    private async Task<object> RunEditorRegressionAsync()
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Native editor regression: " + name);
            checks.Add(name);
        }

        await NavigateAsync("context");
        var originalPolicy=await context.Learning.PolicyAsync(null);
        var normalization=new{kind="learning-settings",unchangedRevision=originalPolicy.Revision};
        Check(LearningPage.Editor.IsReadOnly&&!editingLearningSettings&&!LearningSettingsDirty,"learning loads read-only and clean");
        Check(await RequestNavigationAsync("library")&&smokeDialogs.Count==0,"opening learning then leaving has no confirmation");
        await RequestNavigationAsync("context");BeginLearningSettingsSmoke();
        Check(editingLearningSettings&&LearningPage.Share.IsEnabled&&(string)LearningPage.SaveSettings.Content=="保存"&&LearningSettingsCancel.Visibility==Visibility.Visible,"learning modify enables save and cancel");
        LearningPage.Share.IsChecked=true;Check(LearningSettingsDirty,"learning recognizes permission edits");
        CancelLearningSettings();
        Check(!editingLearningSettings&&!LearningSettingsDirty&&!LearningPage.Share.IsEnabled&&(await context.Learning.PolicyAsync(null))==originalPolicy,"learning cancel restores without writes");
        BeginLearningSettingsSmoke();await LearningUiAsync(SaveLearningSettingsAsync);
        Check(!editingLearningSettings&&!LearningSettingsDirty&&(await context.Learning.PolicyAsync(null)).Revision==originalPolicy.Revision,"unchanged learning save exits without writing");
        BeginLearningSettingsSmoke();LearningPage.Share.IsChecked=true;smokeDialogAnswers.Enqueue(true);await LearningUiAsync(SaveLearningSettingsAsync);
        Check((await context.Learning.PolicyAsync(null)).Share&&!editingLearningSettings&&!LearningPage.Share.IsEnabled,"learning save persists and returns read-only");
        BeginLearningSettingsSmoke();LearningPage.Share.IsChecked=false;smokeDialogAnswers.Enqueue(false);
        Check(!await RequestNavigationAsync("library")&&currentPage=="learning"&&LearningSettingsDirty,"cancelled navigation retains permission draft");
        smokeDialogAnswers.Enqueue(true);
        Check(await RequestNavigationAsync("library")&&!editingLearningSettings,"confirmed discard navigates");
        await RequestNavigationAsync("context");
        Check(LearningPage.Editor.IsReadOnly&&LearningPage.Share.IsChecked==true&&!LearningSettingsDirty,"return reloads durable learning settings");
        await RequestNavigationAsync("library");

        var target = Path.Combine(dataRoot, "smoke", "AGENTS.md"); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var before = "# 规则\r\n\r\n保留已有规则。\r\n";
        await File.WriteAllTextAsync(target, before);
        SelectPage("agents");
        AgentsPath.Text = target;
        await ReadAgentsAsync();
        Check(AgentsEditor.IsReadOnly && !AgentsHasChanges && !agentsEdit.IsEditing, "AGENTS loads read-only and clean");
        var confirmationsBefore = smokeDialogs.Count;
        Check(await RequestNavigationAsync("library") && smokeDialogs.Count == confirmationsBefore, "opening AGENTS then leaving has no confirmation");
        SelectPage("agents");
        AgentsPath.Text = target;
        await ReadAgentsAsync();
        ToggleAgentsEdit(AgentsEditButton, new RoutedEventArgs());
        Check(agentsEdit.IsEditing && !AgentsEditor.IsReadOnly && PrepareAgentsButton.IsEnabled && AgentsCancelButton.Visibility == Visibility.Visible, "AGENTS modify enables editing and managed-entry update");
        AgentsEditor.Text += "\n未保存内容";
        CancelAgentsEdit(AgentsCancelButton, new RoutedEventArgs());
        Check(AgentsEditor.IsReadOnly && !AgentsHasChanges && await File.ReadAllTextAsync(target) == before, "AGENTS cancel restores content without disk writes");
        ToggleAgentsEdit(AgentsEditButton, new RoutedEventArgs());
        await SaveAgentsAsync();
        Check(!agentsEdit.IsEditing && await File.ReadAllTextAsync(target) == before && !agentsUndo.HasBackup, "unchanged AGENTS save preserves exact bytes and needs no backup");
        ToggleAgentsEdit(AgentsEditButton, new RoutedEventArgs());
        AgentsEditor.Text += "\r\n已保存规则。\r\n";
        smokeDialogAnswers.Enqueue(true);
        await SaveAgentsAsync();
        var saved = await File.ReadAllTextAsync(target);
        Check(saved.Contains("已保存规则。", StringComparison.Ordinal) && !saved.Replace("\r\n", "", StringComparison.Ordinal).Contains('\r'), "AGENTS save restores valid CRLF line endings");
        Check(agentsUndo.HasBackup && File.Exists(agentsUndo.BackupPath) && !agentsEdit.IsEditing && AgentsEditor.IsReadOnly, "AGENTS save retains backup and returns to read-only");
        var policy=await context.Learning.PolicyAsync(null);
        await context.Learning.SetPolicyAsync(null,policy with{Share=true},policy.Revision);
        ToggleAgentsEdit(AgentsEditButton, new RoutedEventArgs());
        await PrepareAgentsAsync();
        Check(AgentsHasChanges && AgentsEditor.Text.Contains("folio:begin", StringComparison.Ordinal), "managed entry can be prepared only in edit mode");
        CancelAgentsEdit(AgentsCancelButton, new RoutedEventArgs());
        Check(await File.ReadAllTextAsync(target) == saved && !AgentsHasChanges, "canceling prepared entry leaves file unchanged");
        ToggleAgentsEdit(AgentsEditButton, new RoutedEventArgs());
        await PrepareAgentsAsync();
        smokeDialogAnswers.Enqueue(true);
        await SaveAgentsAsync();
        var managed = await File.ReadAllTextAsync(target);
        Check(managed.Contains("<!-- folio:begin", StringComparison.Ordinal) && managed.Contains("保留已有规则。", StringComparison.Ordinal), "prepared entry saves with valid markers and existing rules preserved");
        Check(await CanLeaveAgentsAsync() && !AgentsHasChanges && smokeDialogAnswers.Count == 0, "post-save navigation is clean");
        Check(smokeDialogs.All(x => x.Background == "#FFFCFAF5"), "all native confirmation dialogs use cream background");
        return new { checks, normalization };
    }
}
