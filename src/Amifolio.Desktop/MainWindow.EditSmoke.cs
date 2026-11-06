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
        var original = contextEdit.OriginalText;
        var normalization = new
        {
            rawStringsEqual = original == ContextEditor.Text,
            normalizedEqual = DocumentEditState.Normalize(original) == DocumentEditState.Normalize(ContextEditor.Text),
            sourceLf = original.Count(c => c == '\n'),
            controlCr = ContextEditor.Text.Count(c => c == '\r')
        };
        Check(ContextEditor.IsReadOnly && !contextEdit.IsEditing && !ContextHasChanges, "context loads read-only and clean");
        Check(await RequestNavigationAsync("library") && smokeDialogs.Count == 0, "opening context then leaving has no confirmation");
        await RequestNavigationAsync("context");
        ToggleContextEdit(ContextEditButton, new RoutedEventArgs());
        Check(contextEdit.IsEditing && !ContextEditor.IsReadOnly && (string)ContextEditButton.Content == "保存" && ContextCancelButton.Visibility == Visibility.Visible, "context modify enables editing and save/cancel");
        ContextEditor.Text += "\n未保存内容";
        Check(ContextHasChanges, "context recognizes actual edits");
        CancelContextEdit(ContextCancelButton, new RoutedEventArgs());
        Check(ContextEditor.IsReadOnly && !ContextHasChanges && DocumentEditState.Normalize(ContextEditor.Text) == DocumentEditState.Normalize(original), "context cancel restores text and read-only state");
        ToggleContextEdit(ContextEditButton, new RoutedEventArgs());
        await SaveContextAsync();
        Check(!contextEdit.IsEditing && !ContextHasChanges, "unchanged context save exits editing without writing");
        ToggleContextEdit(ContextEditButton, new RoutedEventArgs());
        ContextEditor.Text += "\n已保存内容";
        await SaveContextAsync();
        var contextAfterSave = await context.ReadAsync(null);
        Check(!contextEdit.IsEditing && ContextEditor.IsReadOnly && contextAfterSave.Documents["overview.md"].Contains("已保存内容", StringComparison.Ordinal), "context save persists and exits editing");
        ToggleContextEdit(ContextEditButton, new RoutedEventArgs());
        ContextEditor.Text += "\n待放弃";
        smokeDialogAnswers.Enqueue(false);
        Check(!await RequestNavigationAsync("library") && currentPage == "context" && ContextHasChanges, "canceling navigation preserves unsaved edits");
        smokeDialogAnswers.Enqueue(true);
        Check(await RequestNavigationAsync("library") && !contextEdit.IsEditing, "confirmed discard exits editing and navigates");
        await RequestNavigationAsync("context");
        ContextDocument.SelectedItem = "START.md";
        UpdateContextEditor();
        Check(ContextEditor.IsReadOnly && !ContextEditButton.IsEnabled && !contextEdit.Begin(), "generated entry remains read-only");
        await RequestNavigationAsync("library");

        var target = Path.Combine(dataRoot, "smoke-AGENTS.md");
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
        await context.SetEnabledAsync(null, true);
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
