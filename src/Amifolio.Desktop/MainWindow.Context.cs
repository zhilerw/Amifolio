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
    private string agentsFileStatus = "尚未读取文件";
    private bool agentsConflict;
    private bool agentsConfirmingLeave;
    private long contextUiRevision;
    private long contextReadRequest;

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
        if (operation is not null || agentsConfirmingLeave) return false;
        agentsConfirmingLeave = true;
        try
        {
            if (!await ConfirmDiscardAgentsAsync()) return false;
        }
        finally { agentsConfirmingLeave = false; }
        CancelAgentsEditing();
        return true;
    }

    private async Task<bool> ConfirmDiscardAgentsAsync() =>
        !AgentsHasChanges || await ConfirmAsync("有未保存的修改", "继续操作将丢弃当前草稿。", "丢弃草稿");

    private async Task LoadContextAsync(bool background = false)
    {
        var project = ContextProjectId;
        var document = ContextDocument.SelectedItem as string;
        var page = currentPage;
        var revision = contextUiRevision;
        var editing = contextEdit.IsEditing;
        var request = ++contextReadRequest;
        var snapshot = await context.ReadAsync(project, backgroundTasks.Token);
        await PauseContextRaceAsync("context-read");
        // A background read owns no foreground operation gate. Validate after every await
        // before touching either the displayed snapshot or the user's edit session.
        if (closing || request != contextReadRequest || project != ContextProjectId ||
            document != (ContextDocument.SelectedItem as string) || page != currentPage ||
            revision != contextUiRevision || editing != contextEdit.IsEditing ||
            background && (contextEdit.IsEditing || operation is not null)) return;
        contextSnapshot = snapshot;
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
        await RefreshAutomationSummaryAsync();
    }

    private void UpdateContextEditor()
    {
        contextUiRevision++;
        loadedContextProjectId = ContextProjectId;
        loadedContextDocument = ContextDocument.SelectedItem as string;
        ContextPath.Text = loadedContextDocument is null?"":loadedContextDocument+" · "+contextSnapshot?.Directory;
        var text = "";
        if (contextSnapshot is not null && loadedContextDocument is { } document)
        {
            if (contextSnapshot.Documents.TryGetValue(document, out var formal)) text = formal;
            else if (contextSnapshot.CandidateDocuments.TryGetValue(document, out var candidate)) text = candidate;
        }
        ContextEditor.Text = text;
        if (ready && !loadingPage && currentPage == "context") motion?.Enter(ContextEditor, MotionKind.Detail);
        var isCandidate = loadedContextDocument?.StartsWith("candidate-", StringComparison.Ordinal) == true;
        contextEdit.Load(text, loadedContextDocument is not null and not "START.md" and not "sources.md" and not "preferences.md" && !isCandidate);
        UpdateEditingControls();
        AdoptCandidateButton.Visibility = isCandidate ? Visibility.Visible : Visibility.Collapsed;
        UpdateContextDocumentEnabledControl();
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
        contextUiRevision++;
        UpdateEditingControls();
        ContextEditor.Focus(FocusState.Programmatic);
    }

    private void CancelContextEdit(object sender, RoutedEventArgs e)
    {
        if (operation is null) CancelContextEditing();
    }

    private void CancelContextEditing()
    {
        contextUiRevision++;
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
        var derived = contextSnapshot?.DerivedDocuments.Contains(document) == true;
        await RunAsync("正在保存并发布上下文", async token =>
        {
            if (derived)
            {
                if (!await ConfirmAsync("保存为人工文档？", "此文档来自已采用的候选。保存后，保留的全部内容将作为人工文档；来源变化或撤销时将不再自动撤回。请确认已核对内容，并愿意自行维护。", "保存为人工文档")) return;
                await context.SaveDocumentAsManualAsync(project, document, content, token);
            }
            else await context.SaveDocumentAsync(project, document, content, token);
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
            if (document is "START.md" or "sources.md" or "preferences.md") throw new InvalidOperationException("入口与来源由系统维护；通用偏好请在对应入口管理，或关闭整个作用域。");
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
            await remotePermissionUpdate;
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
        // Use the RAW snapshot value, not TextBox.Text (which can normalize newlines),
        // and never resolve the candidate again by filename after confirmation.
        if (loadedContextProjectId != project || loadedContextDocument != candidate ||
            contextSnapshot is null || !contextSnapshot.CandidateDocuments.TryGetValue(candidate, out var reviewedContent)) return;
        await RunAsync("采用候选内容", async token =>
        {
            if (!await ConfirmAsync("采用到正式文档？", $"将以此候选更新 {formal}。请确认已检查内容与来源；采用后仍可手动修改或删除。", "采用候选")) return;
            await PauseContextRaceAsync("adopt-confirmed");
            try { await context.AdoptReviewedCandidateAsync(project, candidate, reviewedContent, null, token); }
            catch (InvalidOperationException)
            {
                await LoadContextAsync();
                StatusText.Text = "候选或来源已变化，请重新核对后采用。";
                return;
            }
            await context.PublishAsync(project, token);
            var adopted = await context.ReadAsync(project, token);
            if (adopted.Documents.TryGetValue(formal, out var content)) await store.SaveGeneratedAsync($"context-{formal}", content, project, token);
            await LoadContextAsync();
            ContextDocument.SelectedItem = formal;
            UpdateContextEditor();
            StatusText.Text = "候选已采用";
        });
    }

    private async Task LoadAgentsForScopeAsync()
    {
        loadedAgentsScopeId = SelectedScope(AgentsScope)?.Id;
        var root = loadedAgentsScopeId is null
            ? integration.CodexHome
            : projects.FirstOrDefault(p => p.Id == loadedAgentsScopeId)?.RootPath;
        AgentsPath.Text = string.IsNullOrWhiteSpace(root) ? "" : Path.Combine(root, "AGENTS.md");
        SetAgentsSnapshot(null, "尚未读取文件");
        if (string.IsNullOrWhiteSpace(root))
        {
            agentsUndo.Clear();
            agentsFileStatus = "项目尚无可用目录。可在「更多操作」中指定文件。";
            UpdateAgentsControls();
            return;
        }
        await ReadAgentsAsync();
    }

    private async void AgentsScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || updating || operation is not null) return;
        if (!await CanLeaveAgentsAsync())
        {
            updating = true;
            try
            {
                AgentsScope.SelectedItem = AgentsScope.Items.OfType<ScopeChoice>().FirstOrDefault(x => x.Id == loadedAgentsScopeId);
            }
            finally { updating = false; }
            return;
        }
        await RunAsync("读取 AGENTS.md", async _ => await LoadAgentsForScopeAsync());
    }

    private async Task ReadAgentsAsync(CancellationToken token = default)
    {
        var path = AgentsPath.Text.Trim();
        if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("请输入 AGENTS.md 的绝对路径。");
        var snapshot = await agents.ReadAsync(path, token);
        loadedAgentsScopeId = SelectedScope(AgentsScope)?.Id;
        SetAgentsSnapshot(snapshot, snapshot.Exists ? "已读取" : "文件尚不存在，首次保存时创建。");
        if (ready && !loadingPage && currentPage == "agents") motion?.Enter(AgentsEditor, MotionKind.Detail);
    }

    private void SetAgentsSnapshot(AgentsFileSnapshot? snapshot, string status)
    {
        agentsSnapshot = snapshot;
        agentsFileStatus = status;
        agentsConflict = false;
        if (snapshot is not null)
        {
            agentsUndo.FileLoaded(snapshot.Path, snapshot.Hash, snapshot.Exists);
            AgentsPath.Text = snapshot.Path;
        }
        var content = snapshot?.Content ?? "";
        agentsEdit.Load(content, snapshot is not null);
        AgentsEditor.Text = content;
        UpdateAgentsControls();
    }

    private async void ReadAgents(object sender, RoutedEventArgs e)
    {
        CloseAgentsActions();
        await RunAsync("读取文件", async token =>
        {
            if (!await ConfirmDiscardAgentsAsync()) return;
            await ReadAgentsAsync(token);
        });
        if (!closing) AgentsActionsButton.Focus(FocusState.Programmatic);
    }

    private async Task PrepareAgentsAsync()
    {
        if (!agentsEdit.IsEditing || operation is not null || agentsConflict || !AgentsTargetMatchesPath) return;
        var snapshot = agentsSnapshot!;
        await RunAsync("准备作品集入口", async token =>
        {
            var project = loadedAgentsScopeId;
            var policy = await context.Learning.PolicyAsync(project, token);
            await context.Learning.PublishProjectionAsync(project, true, token);
            // Build against the draft, retaining the disk snapshot for optimistic writes.
            var editingSnapshot = snapshot with { Content = agentsEdit.TextForSave(AgentsEditor.Text) };
            AgentsEditor.Text = agents.PreviewManagedBlock(editingSnapshot, context.LearningEntryDirectory(project), policy.Share);
        });
    }

    private void CloseAgentsActions() => AgentsActionsButton.Flyout.Hide();

    private async void ToggleAgentsEdit(object sender, RoutedEventArgs e)
    {
        if (operation is not null || !AgentsTargetMatchesPath || agentsConflict) return;
        if (agentsEdit.IsEditing)
        {
            await SaveAgentsAsync();
            if (!closing)
            {
                if (agentsConflict) AgentsConflictButton.Focus(FocusState.Programmatic);
                else if (agentsEdit.IsEditing) AgentsEditor.Focus(FocusState.Programmatic);
                else AgentsEditButton.Focus(FocusState.Programmatic);
            }
            return;
        }
        if (!agentsEdit.Begin()) return;
        UpdateAgentsControls();
        AgentsEditor.Focus(FocusState.Programmatic);
    }

    private async void CancelAgentsEdit(object sender, RoutedEventArgs e)
    {
        if (operation is not null) return;
        if (agentsConflict)
        {
            await RunAsync("读取磁盘版本", async token =>
            {
                if (await ConfirmDiscardAgentsAsync()) await ReadAgentsAsync(token);
            });
        }
        else CancelAgentsEditing();
        if (!closing && !agentsEdit.IsEditing) AgentsEditButton.Focus(FocusState.Programmatic);
    }

    private void CancelAgentsEditing()
    {
        if (agentsConflict)
        {
            SetAgentsSnapshot(null, "磁盘文件已改变。请重新读取后继续编辑。");
            return;
        }
        AgentsEditor.Text = agentsEdit.Cancel();
        UpdateAgentsControls();
    }

    private async Task SaveAgentsAsync()
    {
        if (agentsSnapshot is not { } snapshot || !agentsEdit.IsEditing || agentsConflict || !AgentsTargetMatchesPath) return;
        if (!AgentsHasChanges) { CancelAgentsEditing(); return; }
        var proposed = agentsEdit.TextForSave(AgentsEditor.Text);
        await RunAsync("保存 AGENTS.md", async token =>
        {
            var preview = AgentsDiffPreview(snapshot.Path, MakeDiff(snapshot.Content, proposed));
            if (await ConfirmAsync("保存 AGENTS.md？", preview, snapshot.Exists ? "备份并保存" : "创建文件"))
                await WriteAgentsAsync(snapshot, proposed, restoring: false, token);
        });
    }

    private async void ReviewAgentsConflict(object sender, RoutedEventArgs e) => await ReviewAgentsConflictAsync();

    private async Task ReviewAgentsConflictAsync()
    {
        if (!agentsConflict || !agentsEdit.IsEditing || agentsSnapshot is not { } snapshot || !AgentsTargetMatchesPath) return;
        await RunAsync("对照磁盘版本", async token =>
        {
            var latest = await agents.ReadAsync(snapshot.Path, token);
            var draft = agentsEdit.TextForSave(AgentsEditor.Text);
            var detail = latest.Exists ? "以下是从磁盘版本改成当前草稿的差异。" : "磁盘文件已被删除。保存草稿将重新创建文件。";
            var preview = AgentsDiffPreview(latest.Path, MakeDiff(latest.Content, draft),
                detail + "可保留草稿继续编辑。之后保存时仍需确认差异。");
            if (!await ConfirmAsync("对照磁盘版本", preview, "保留草稿并继续")) return;
            SetAgentsSnapshot(latest, latest.Exists ? "已读取" : "文件尚不存在，首次保存时创建。");
            agentsEdit.Begin();
            AgentsEditor.Text = draft;
            UpdateAgentsControls();
        });
        if (!closing && agentsEdit.IsEditing) AgentsEditor.Focus(FocusState.Programmatic);
    }

    private static TextBox AgentsDiffPreview(string path, string diff, string? detail = null) => new()
    {
        Text = detail is null ? path + "\n\n" + diff : path + "\n\n" + detail + "\n\n" + diff,
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["AppFontFamily"],
        FontSize = 13,
        Height = 300
    };

    private async void UndoAgents(object sender, RoutedEventArgs e)
    {
        CloseAgentsActions();
        await UndoAgentsAsync();
        if (!closing) AgentsActionsButton.Focus(FocusState.Programmatic);
    }

    private async Task UndoAgentsAsync()
    {
        if (agentsUndo.BackupPath is not { } backup || agentsSnapshot is not { } snapshot ||
            agentsEdit.IsEditing || agentsConflict || !AgentsTargetMatchesPath) return;
        await RunAsync("撤销上次保存", async token =>
        {
            var current = await agents.ReadAsync(snapshot.Path, token);
            if (!agentsUndo.CanRestore(current.Path, current.Hash, current.Exists))
            {
                ShowAgentsUndoConflict(current);
                return;
            }
            if (!await ConfirmAsync("恢复上次备份？", "将恢复上次保存前的内容，同时备份当前内容。\n\n" + current.Path, "恢复备份")) return;
            var previous = await File.ReadAllTextAsync(backup, token);
            await WriteAgentsAsync(current, previous, restoring: true, token);
        });
    }

    private void ShowAgentsUndoConflict(AgentsFileSnapshot latest)
    {
        SetAgentsSnapshot(latest, "磁盘文件已改变，已读取当前版本。上次保存无法撤销。");
        StatusText.Text = "磁盘文件已改变，撤销已停止。";
    }

    private async Task WriteAgentsAsync(AgentsFileSnapshot expected, string text, bool restoring, CancellationToken token)
    {
        string? backup;
        try { backup = await agents.SaveAsync(expected, text, token); }
        catch (IOException)
        {
            var latest = await agents.ReadAsync(expected.Path, token);
            if (latest.Exists == expected.Exists && latest.Hash == expected.Hash) throw;
            if (restoring) ShowAgentsUndoConflict(latest);
            else
            {
                agentsUndo.FileLoaded(latest.Path, latest.Hash, latest.Exists);
                agentsConflict = true;
                UpdateAgentsControls();
                StatusText.Text = "磁盘文件已改变，草稿仍在编辑器中。";
            }
            return;
        }

        agentsUndo.Clear();
        var status = restoring ? "已恢复备份" : backup is null ? "已创建文件" : "已保存，原内容已备份";
        AgentsFileSnapshot persisted;
        try
        {
            persisted = await agents.ReadAsync(expected.Path, token);
        }
        catch
        {
            SetAgentsSnapshot(null, "已写入文件。请重新读取后继续编辑。");
            throw;
        }
        if (!TrackAgentsWrite(persisted, text, backup, restoring))
            status = "写入完成，随后文件又有变化。已显示磁盘版本。";
        SetAgentsSnapshot(persisted, status);
        StatusText.Text = status;
    }

    private bool TrackAgentsWrite(AgentsFileSnapshot persisted, string text, string? backup, bool restoring = false)
    {
        if (!persisted.Exists || persisted.Content != text) return false;
        if (!restoring) agentsUndo.Record(persisted.Path, backup, persisted.Hash);
        return true;
    }

    private bool AgentsTargetMatchesPath =>
        agentsSnapshot is { } snapshot && loadedAgentsScopeId == SelectedScope(AgentsScope)?.Id &&
        string.Equals(snapshot.Path, AgentsPath.Text.Trim(), StringComparison.OrdinalIgnoreCase);

    private void AgentsPathChanged(object sender, TextChangedEventArgs e)
    {
        if (ready) UpdateAgentsControls();
    }

    private void AgentsEditorChanged(object sender, TextChangedEventArgs e)
    {
        if (ready) UpdateAgentsStatus();
    }

    private void UpdateAgentsStatus()
    {
        var target = agentsSnapshot?.Path ?? AgentsPath.Text.Trim();
        AgentsTargetPath.Text = target.Length == 0 ? "尚未选择文件" : target;
        AgentsStatus.Text = agentsConflict
            ? "磁盘文件已改变。草稿已保留，可先对照差异。"
            : agentsSnapshot is not null && !AgentsTargetMatchesPath
                ? "目标已更改，请在「更多操作」中读取指定路径。"
                : AgentsHasChanges ? "有未保存的修改"
                : agentsEdit.IsEditing ? "正在编辑" : agentsFileStatus;
    }

    private void UpdateAgentsControls()
    {
        var idle = operation is null;
        var editing = agentsEdit.IsEditing;
        var targetReady = AgentsTargetMatchesPath;
        AgentsEditor.IsReadOnly = !editing;
        AgentsPath.IsReadOnly = editing;
        AgentsEditButton.Content = editing ? "保存" : "编辑";
        AgentsEditButton.IsEnabled = idle && targetReady && !agentsConflict;
        if (motion is { } effects) effects.SetVisible(AgentsCancelButton, editing, MotionKind.Edit);
        else AgentsCancelButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        AgentsCancelButton.IsEnabled = idle;
        AgentsCancelButton.Content = agentsConflict ? "放弃草稿" : "取消";
        AgentsConflictButton.Visibility = editing && agentsConflict ? Visibility.Visible : Visibility.Collapsed;
        AgentsConflictButton.IsEnabled = idle;
        AgentsActionsButton.IsEnabled = idle;
        UndoAgentsButton.IsEnabled = idle && !editing && targetReady && !agentsConflict && agentsUndo.HasBackup;
        UpdateAgentsStatus();
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
        UpdateAgentsControls();
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
