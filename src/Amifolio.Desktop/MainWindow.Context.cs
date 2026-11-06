using Amifolio.Context;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private ContextSnapshot? contextSnapshot;
    private AgentsFileSnapshot? agentsSnapshot;
    private readonly AgentsUndoState agentsUndo = new();
    private string? loadedAgentsScopeId;
    private string? loadedContextProjectId;
    private string? loadedContextDocument;
    private readonly DocumentEditState contextEdit = new();
    private readonly DocumentEditState agentsEdit = new();

    private bool ContextHasChanges => contextEdit.HasChanges(ContextEditor.Text);
    private bool AgentsHasChanges => agentsEdit.HasChanges(AgentsEditor.Text);

    private async Task<bool> CanLeaveContextAsync()
    {
        if (ContextHasChanges && !await ConfirmAsync("有尚未保存的修改", "切换将丢弃未保存的修改。", "丢弃修改")) return false;
        CancelContextEditing();
        return true;
    }

    private async Task<bool> CanLeaveAgentsAsync()
    {
        if (AgentsHasChanges && !await ConfirmAsync("有尚未保存的修改", "切换将丢弃未保存的修改。", "丢弃修改")) return false;
        CancelAgentsEditing();
        return true;
    }

    private async Task LoadContextAsync()
    {
        contextSnapshot = await context.ReadAsync(ContextProjectId);
        updating = true;
        try
        {
            ContextEnabled.IsOn = contextSnapshot.Enabled;
            OriginalAccess.IsOn = contextSnapshot.OriginalAccessAllowed;
            ContextPath.Text = contextSnapshot.Directory;
            var previousDocument = ContextDocument.SelectedItem as string;
            var documents = new[] { "overview.md", "memory.md", "style.md", "summary.md", "sources.md", "START.md" }
                .Concat(contextSnapshot.Documents.Keys).Concat(contextSnapshot.CandidateDocuments.Keys).Distinct().ToList();
            ContextDocument.ItemsSource = documents;
            ContextDocument.SelectedItem = previousDocument is not null && documents.Contains(previousDocument) ? previousDocument : "overview.md";
            UpdateContextEditor();
        }
        finally { updating = false; }
    }

    private void UpdateContextEditor()
    {
        loadedContextProjectId = ContextProjectId;
        loadedContextDocument = ContextDocument.SelectedItem as string;
        var text = "";
        if (contextSnapshot is not null && loadedContextDocument is { } document)
        {
            if (contextSnapshot.Documents.TryGetValue(document, out var formal)) text = formal;
            else if (contextSnapshot.CandidateDocuments.TryGetValue(document, out var candidate)) text = candidate;
        }
        ContextEditor.Text = text;
        if (ready && !loadingPage && currentPage == "context") motion?.Enter(ContextEditor, MotionKind.Detail);
        var isCandidate = loadedContextDocument?.StartsWith("candidate-", StringComparison.Ordinal) == true;
        contextEdit.Load(text, loadedContextDocument is not null and not "START.md" and not "sources.md" && !isCandidate);
        UpdateEditingControls();
        AdoptCandidateButton.Visibility = isCandidate ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ContextScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || updating || operation is not null) return;
        if (!await CanLeaveContextAsync())
        {
            updating = true;
            ContextScope.SelectedItem = ContextScope.Items.OfType<ScopeChoice>().FirstOrDefault(x => x.Id == loadedContextProjectId);
            updating = false;
            return;
        }
        await RunAsync("正在读取作用域", async _ => { await LoadContextAsync(); StatusText.Text = "已切换上下文作用域"; });
    }

    private async void ContextDocumentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || updating) return;
        if (!await CanLeaveContextAsync())
        {
            updating = true;
            ContextDocument.SelectedItem = loadedContextDocument;
            updating = false;
            return;
        }
        UpdateContextEditor();
    }

    private async void ContextEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || updating) return;
        var enabled = ContextEnabled.IsOn;
        var project = ContextProjectId;
        await RunAsync("正在更新上下文许可", async token =>
        {
            await context.SetEnabledAsync(project, enabled, token);
            await context.PublishAsync(project, token);
            if (!ContextHasChanges) await LoadContextAsync();
            StatusText.Text = enabled ? "上下文已开启" : "上下文已关闭";
        });
    }

    private async void ToggleContextEdit(object sender, RoutedEventArgs e)
    {
        if (operation is not null) return;
        if (contextEdit.IsEditing) { await SaveContextAsync(); return; }
        if (!contextEdit.Begin()) return;
        UpdateEditingControls();
        ContextEditor.Focus(FocusState.Programmatic);
    }

    private void CancelContextEdit(object sender, RoutedEventArgs e)
    {
        if (operation is null) CancelContextEditing();
    }

    private void CancelContextEditing()
    {
        ContextEditor.Text = contextEdit.Cancel();
        UpdateEditingControls();
    }

    private async Task SaveContextAsync()
    {
        var document = ContextDocument.SelectedItem as string;
        if (document is null) return;
        if (!contextEdit.IsEditing) return;
        if (!ContextHasChanges) { CancelContextEditing(); return; }
        var content = contextEdit.TextForSave(ContextEditor.Text);
        var project = ContextProjectId;
        await RunAsync("正在保存并发布上下文", async token =>
        {
            await context.SaveDocumentAsync(project, document, content, token);
            await context.PublishAsync(project, token);
            await store.SaveGeneratedAsync($"context-{document}", content, project, token);
            await LoadContextAsync();
            StatusText.Text = "已保存并发布";
        });
    }

    private async void OriginalAccessChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || updating) return;
        var allowed = OriginalAccess.IsOn;
        var project = ContextProjectId;
        await RunAsync("正在更新原件定位许可", async token =>
        {
            await context.SetOriginalAccessAsync(project, allowed, token);
            await context.PublishAsync(project, token);
            if (!contextEdit.IsEditing) await LoadContextAsync();
            StatusText.Text = allowed ? "原件路径已开启" : "原件路径已关闭";
        });
    }

    private async void DeleteContext(object sender, RoutedEventArgs e)
    {
        var document = ContextDocument.SelectedItem as string;
        if (document is null) return;
        var project = ContextProjectId;
        await RunAsync("删除上下文文档", async token =>
        {
            if (document is "START.md" or "sources.md") throw new InvalidOperationException("入口与来源由系统维护；可关闭整个作用域，或删除概要、记忆等内容。");
            if (!await ConfirmAsync("删除此上下文文档？", "将删除并停用此文档，自动生成不会恢复它。已归档或导出到其他位置的副本需分别管理。", "删除")) return;
            await context.DeleteDocumentAsync(project, document, token);
            await context.PublishAsync(project, token);
            await LoadContextAsync();
            StatusText.Text = "文档已删除并更新发布入口";
        });
    }

    private async void PublishContext(object sender, RoutedEventArgs e)
    {
        if (!await CanLeaveContextAsync()) return;
        var project = ContextProjectId;
        await RunAsync("正在发布上下文", async token =>
        {
            await context.PublishAsync(project, token);
            await LoadContextAsync();
            await ArchiveContextAsync(project, token);
            StatusText.Text = "来源已更新";
        });
    }

    private async void GenerateContext(object sender, RoutedEventArgs e)
    {
        if (!RemoteAllowed.IsOn || string.IsNullOrWhiteSpace(ModelKey.Password) || string.IsNullOrWhiteSpace(ModelName.Text))
        {
            await ShowMessageAsync("需要模型配置", "请在“AI 配置”中填写模型配置，并开启“允许发送片段给模型服务”。");
            return;
        }
        if (!await CanLeaveContextAsync()) return;
        var project = ContextProjectId;
        var options = new ModelOptions(ModelEndpoint.Text.Trim(), ModelName.Text.Trim(), RemoteAllowed.IsOn);
        var key = ModelKey.Password;
        await RunAsync("正在生成候选上下文", async token =>
        {
            var payload = await context.PreviewGenerationAsync(project, token);
            var preview = new TextBox { Text = $"目标服务：{options.Endpoint}\n模型：{options.Model}\n\n" + payload, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Width = 530, Height = 330 };
            if (!await ConfirmAsync("发送这些片段并生成候选？", preview, "发送并生成")) return;
            await context.GenerateAsync(project, options, key, token);
            await context.PublishAsync(project, token);
            await LoadContextAsync();
            await ArchiveContextAsync(project, token);
            var candidate = ContextDocument.Items.OfType<string>().FirstOrDefault(x => x.Contains("candidate", StringComparison.OrdinalIgnoreCase));
            if (candidate is not null) { ContextDocument.SelectedItem = candidate; UpdateContextEditor(); }
            StatusText.Text = "候选已生成，确认后可采用";
        });
    }

    private async Task ArchiveContextAsync(string? project, CancellationToken token)
    {
        var snapshot = await context.ReadAsync(project, token);
        foreach (var document in snapshot.Documents.Concat(snapshot.CandidateDocuments))
        {
            if (document.Key is "START.md" or "sources.md" || string.IsNullOrWhiteSpace(document.Value)) continue;
            await store.SaveGeneratedAsync("context-" + document.Key, document.Value, project, token);
        }
    }

    private async void AdoptCandidate(object sender, RoutedEventArgs e)
    {
        if (ContextDocument.SelectedItem is not string candidate || !candidate.StartsWith("candidate-", StringComparison.Ordinal)) return;
        var formal = candidate[10..];
        var project = ContextProjectId;
        var content = "# 用户已采用的候选\n\n确认时间：" + DateTimeOffset.Now.ToString("O") + "\n以下保留生成时的标记与来源；本次采用优先于原始待确认状态。\n\n" + ContextEditor.Text;
        await RunAsync("采用候选内容", async token =>
        {
            if (!await ConfirmAsync("采用到正式文档？", $"将以此候选更新 {formal}。请确认已检查内容与来源；采用后仍可手动修改或删除。", "采用候选")) return;
            await context.SaveDocumentAsync(project, formal, content, token);
            await context.PublishAsync(project, token);
            await store.SaveGeneratedAsync($"context-{formal}", content, project, token);
            await LoadContextAsync();
            ContextDocument.SelectedItem = formal;
            UpdateContextEditor();
            StatusText.Text = "候选已采用";
        });
    }

    private async Task LoadAgentsForScopeAsync()
    {
        var projectId = SelectedScope(AgentsScope)?.Id;
        loadedAgentsScopeId = projectId;
        if (projectId is null)
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(codexHome)) codexHome = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            AgentsPath.Text = Path.Combine(codexHome, "AGENTS.md");
        }
        else
        {
            var project = projects.First(p => p.Id == projectId);
            if (string.IsNullOrWhiteSpace(project.RootPath))
            {
                AgentsPath.Text = "";
                AgentsEditor.Text = "";
                agentsSnapshot = null;
                agentsEdit.Load("", false);
                UpdateEditingControls();
                ClearAgentsUndo();
                AgentsStatus.Text = "项目未绑定目录，请输入 AGENTS.md 路径。";
                return;
            }
            AgentsPath.Text = Path.Combine(project.RootPath, "AGENTS.md");
        }
        await ReadAgentsAsync();
    }

    private async void AgentsScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || updating || operation is not null) return;
        if (!await CanLeaveAgentsAsync())
        {
            updating = true;
            AgentsScope.SelectedItem = AgentsScope.Items.OfType<ScopeChoice>().FirstOrDefault(x => x.Id == loadedAgentsScopeId);
            updating = false;
            return;
        }
        await RunAsync("正在读取 AGENTS.md", async _ => await LoadAgentsForScopeAsync());
    }

    private async Task ReadAgentsAsync()
    {
        if (!Path.IsPathFullyQualified(AgentsPath.Text.Trim())) throw new InvalidOperationException("请输入 AGENTS.md 的绝对路径。");
        var snapshot = await agents.ReadAsync(AgentsPath.Text.Trim());
        agentsUndo.FileLoaded(snapshot.Path);
        UndoAgentsButton.IsEnabled = agentsUndo.HasBackup;
        agentsSnapshot = snapshot;
        AgentsEditor.Text = snapshot.Content;
        if (ready && !loadingPage && currentPage == "agents") motion?.Enter(AgentsEditor, MotionKind.Detail);
        agentsEdit.Load(snapshot.Content, true);
        UpdateEditingControls();
        AgentsStatus.Text = snapshot.Exists ? "已读取" : "文件不存在，保存后创建";
    }

    private async void ReadAgents(object sender, RoutedEventArgs e)
    {
        await RunAsync("读取文件", async _ =>
        {
            if (!await CanLeaveAgentsAsync()) return;
            await ReadAgentsAsync();
        });
    }

    private async void PrepareAgents(object sender, RoutedEventArgs e)
        => await PrepareAgentsAsync();

    private async Task PrepareAgentsAsync()
    {
        if (!agentsEdit.IsEditing || operation is not null) return;
        await RunAsync("准备作品集入口", async token =>
        {
            if (agentsSnapshot is null || !string.Equals(agentsSnapshot.Path, AgentsPath.Text.Trim(), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("目标路径已变更，请取消编辑后重新读取。");
            var project = SelectedScope(AgentsScope)?.Id;
            await context.PublishAsync(project, token);
            var snapshot = await context.ReadAsync(project, token);
            // Build against current editor text, while retaining the disk snapshot for optimistic write checks.
            var editingSnapshot = agentsSnapshot! with { Content = agentsEdit.TextForSave(AgentsEditor.Text) };
            AgentsEditor.Text = agents.PreviewManagedBlock(editingSnapshot, snapshot.Directory, snapshot.Enabled);
            AgentsStatus.Text = snapshot.Enabled ? "入口已更新，尚未保存" : "上下文已关闭，对应入口已移除，尚未保存";
        });
    }

    private async void ToggleAgentsEdit(object sender, RoutedEventArgs e)
    {
        if (operation is not null) return;
        if (agentsEdit.IsEditing) { await SaveAgentsAsync(); return; }
        if (agentsSnapshot is null || !agentsEdit.Begin()) return;
        UpdateEditingControls();
        AgentsEditor.Focus(FocusState.Programmatic);
    }

    private void CancelAgentsEdit(object sender, RoutedEventArgs e)
    {
        if (operation is null) CancelAgentsEditing();
    }

    private void CancelAgentsEditing()
    {
        AgentsEditor.Text = agentsEdit.Cancel();
        UpdateEditingControls();
    }

    private async Task SaveAgentsAsync()
    {
        if (agentsSnapshot is null) { await ShowMessageAsync("尚未读取文件", "请先读取目标文件，再编辑和保存。"); return; }
        if (!agentsEdit.IsEditing) return;
        if (!AgentsHasChanges) { CancelAgentsEditing(); return; }
        await RunAsync("保存 AGENTS.md", async token =>
        {
            if (!string.Equals(agentsSnapshot.Path, AgentsPath.Text.Trim(), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("目标路径已变更，请重新读取后保存。");
            var newText = agentsEdit.TextForSave(AgentsEditor.Text);
            if (newText == agentsSnapshot.Content) { StatusText.Text = "没有需要保存的修改"; return; }
            var preview = new TextBox { Text = MakeDiff(agentsSnapshot.Content, newText), IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"), FontSize = 12, Width = 530, Height = 330 };
            if (!await ConfirmAsync("确认写入 AGENTS.md", preview, "备份并保存")) return;
            var backup = await agents.SaveAsync(agentsSnapshot, newText, token);
            var persisted = await agents.ReadAsync(agentsSnapshot.Path, token);
            if (persisted.Content == newText) agentsUndo.Record(persisted.Path, backup, persisted.Hash);
            else agentsUndo.Clear();
            await ReadAgentsAsync();
            UndoAgentsButton.IsEnabled = agentsUndo.HasBackup;
            AgentsStatus.Text = backup is null ? "已创建文件。" : "已保存并备份";
            StatusText.Text = "AGENTS.md 已保存，新会话生效情况需验证";
        });
    }

    private async void UndoAgents(object sender, RoutedEventArgs e)
    {
        if (agentsUndo.BackupPath is null || agentsSnapshot is null) return;
        if (!await CanLeaveAgentsAsync()) return;
        await RunAsync("撤销上次保存", async token =>
        {
            if (!string.Equals(agentsSnapshot.Path, AgentsPath.Text.Trim(), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("目标路径已变更，请重新读取后撤销。");
            var current = await agents.ReadAsync(agentsSnapshot.Path, token);
            if (!agentsUndo.CanRestore(current.Path, current.Hash)) throw new InvalidOperationException("目标或内容已改变，已停止撤销以保护其他文件和外部编辑。");
            if (!await ConfirmAsync("恢复上次备份？", "将恢复上次保存前的内容，同时备份当前内容。", "恢复备份")) return;
            var previous = await File.ReadAllTextAsync(agentsUndo.BackupPath, token);
            await agents.SaveAsync(current, previous, token);
            await ReadAgentsAsync();
            UndoAgentsButton.IsEnabled = false;
            agentsUndo.Clear();
            StatusText.Text = "已恢复备份内容";
        });
    }

    private void ClearAgentsUndo()
    {
        agentsUndo.Clear();
        UndoAgentsButton.IsEnabled = false;
    }

    private void UpdateEditingControls()
    {
        var idle = operation is null;
        ContextEditor.IsReadOnly = !contextEdit.IsEditing;
        ContextEditButton.Content = contextEdit.IsEditing ? "保存" : "修改";
        ContextEditButton.IsEnabled = idle && contextEdit.CanEdit;
        if (motion is { } effects) effects.SetVisible(ContextCancelButton, contextEdit.IsEditing, MotionKind.Edit);
        else ContextCancelButton.Visibility = contextEdit.IsEditing ? Visibility.Visible : Visibility.Collapsed;
        ContextCancelButton.IsEnabled = idle;
        ContextDeleteButton.IsEnabled = idle && !contextEdit.IsEditing && loadedContextDocument is not null and not "START.md" and not "sources.md";
        ContextRefreshButton.IsEnabled = ContextGenerateButton.IsEnabled = AdoptCandidateButton.IsEnabled = idle && !contextEdit.IsEditing;
        ContextEnabled.IsEnabled = OriginalAccess.IsEnabled = idle && !contextEdit.IsEditing;
        AgentsEditor.IsReadOnly = !agentsEdit.IsEditing;
        AgentsPath.IsReadOnly = agentsEdit.IsEditing;
        AgentsEditButton.Content = agentsEdit.IsEditing ? "保存" : "修改";
        AgentsEditButton.IsEnabled = idle && agentsEdit.CanEdit;
        if (motion is { } agentEffects) agentEffects.SetVisible(AgentsCancelButton, agentsEdit.IsEditing, MotionKind.Edit);
        else AgentsCancelButton.Visibility = agentsEdit.IsEditing ? Visibility.Visible : Visibility.Collapsed;
        AgentsCancelButton.IsEnabled = idle;
        PrepareAgentsButton.IsEnabled = idle && agentsEdit.IsEditing;
        UndoAgentsButton.IsEnabled = idle && !agentsEdit.IsEditing && agentsUndo.HasBackup;
    }

    private static string MakeDiff(string before, string after)
    {
        var oldLines = DocumentEditState.Normalize(before).Split('\n');
        var newLines = DocumentEditState.Normalize(after).Split('\n');
        var prefix = 0;
        while (prefix < Math.Min(oldLines.Length, newLines.Length) && oldLines[prefix] == newLines[prefix]) prefix++;
        var suffix = 0;
        while (suffix < Math.Min(oldLines.Length, newLines.Length) - prefix && oldLines[^(suffix + 1)] == newLines[^(suffix + 1)]) suffix++;
        return "--- 磁盘原内容\n+++ 编辑后内容\n" + string.Join("\n", oldLines.Take(prefix).TakeLast(2).Select(x => "  " + x)
            .Concat(oldLines.Skip(prefix).Take(oldLines.Length - prefix - suffix).Select(x => "- " + x))
            .Concat(newLines.Skip(prefix).Take(newLines.Length - prefix - suffix).Select(x => "+ " + x))
            .Concat(newLines.Skip(newLines.Length - suffix).Take(2).Select(x => "  " + x)));
    }
}
