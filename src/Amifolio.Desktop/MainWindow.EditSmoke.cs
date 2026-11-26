using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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
        Check(agentsEdit.IsEditing && !AgentsEditor.IsReadOnly && AgentsTargetMatchesPath && AgentsCancelButton.Visibility == Visibility.Visible, "AGENTS modify enables editing for the loaded target");
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

        // Keep the original 22 checks for Test-Native.ps1 and report the added scenarios separately.
        var agentsRecoveryChecks = new List<string>();
        void CheckRecovery(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Native AGENTS recovery: " + name);
            agentsRecoveryChecks.Add(name);
        }
        var otherPath = Path.Combine(dataRoot, "other", "AGENTS.md");
        AgentsPath.Text = otherPath;
        UpdateAgentsControls();
        CheckRecovery(AgentsTargetPath.Text == target && !AgentsEditButton.IsEnabled && !UndoAgentsButton.IsEnabled,
            "typing a custom path keeps the actual loaded target visible and blocks writes");
        AgentsPath.Text = target;
        UpdateAgentsControls();
        ToggleAgentsEdit(AgentsEditButton, new RoutedEventArgs());
        AgentsEditor.Text += "\r\n草稿规则。\r\n";
        UpdateAgentsStatus();
        var draft = AgentsEditor.Text;
        CheckRecovery(AgentsStatus.Text == "有未保存的修改", "editing reports unsaved changes");
        var external = managed + "\r\n外部规则。\r\n";
        await File.WriteAllTextAsync(target, external);
        smokeDialogAnswers.Enqueue(true);
        await SaveAgentsAsync();
        CheckRecovery(agentsConflict && AgentsConflictButton.Visibility == Visibility.Visible &&
            !AgentsEditButton.IsEnabled && AgentsEditor.Text == draft && await File.ReadAllTextAsync(target) == external,
            "optimistic save conflict preserves both disk changes and the draft");
        smokeDialogAnswers.Enqueue(false);
        await ReviewAgentsConflictAsync();
        CheckRecovery(agentsConflict && AgentsEditor.Text == draft, "canceling conflict review preserves the draft and conflict");
        smokeDialogAnswers.Enqueue(true);
        await ReviewAgentsConflictAsync();
        CheckRecovery(!agentsConflict && agentsEdit.IsEditing && AgentsHasChanges && AgentsEditor.Text == draft,
            "confirmed conflict review uses the newer disk baseline and keeps the draft");
        AgentsEditor.Text += "\r\n外部规则。\r\n";
        smokeDialogAnswers.Enqueue(true);
        await SaveAgentsAsync();
        var merged = await File.ReadAllTextAsync(target);
        CheckRecovery(merged.Contains("外部规则。", StringComparison.Ordinal) && merged.Contains("草稿规则。", StringComparison.Ordinal)
            && UndoAgentsButton.IsEnabled, "reviewed edits can be saved and undone");
        smokeDialogAnswers.Enqueue(true);
        await UndoAgentsAsync();
        CheckRecovery(await File.ReadAllTextAsync(target) == external && !UndoAgentsButton.IsEnabled,
            "undo restores the reviewed disk baseline");

        Check(smokeDialogs.All(x => x.Background == "#FFFAF9F5"), "all native confirmation dialogs use the shared paper background");
        return new { checks, normalization, agentsRecoveryChecks };
    }
}
