using Amifolio.Context;
using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private sealed record PreferenceSourceChoice(string Id, string Name);
    private static string PreferenceStatusLabel(string status, bool enabled) => status switch
    {
        "candidate" => "待确认", "confirmed" => enabled ? "已确认" : "已停用",
        "rejected" => "已拒绝", "invalidated" => "来源或授权已变化", _ => "待检查"
    };

    private async void OpenPreferences(object sender, RoutedEventArgs e)
    {
        if (!await CanLeaveContextAsync()) return;
        var project = ContextProjectId;
        await RunAsync("通用偏好", async ct =>
        {
            var dialog = await CreatePreferencesDialogAsync(project, ct);
            await dialog.ShowAsync();
            if (!closing) await LoadContextAsync();
        });
    }

    private async Task<ContentDialog> CreatePreferencesDialogAsync(string? project, CancellationToken ct)
    {
        var panel = new StackPanel { Spacing = 12, Width = 580 };
        panel.Children.Add(new TextBlock { Text = "确认后仅共享通用表述。请排除项目名称、路径、正文和专有事实。", TextWrapping = TextWrapping.Wrap });
        var participation = new ToggleSwitch { Header = "允许从此项目提炼通用偏好", OnContent = "开启", OffContent = "关闭", Visibility = project is null ? Visibility.Collapsed : Visibility.Visible };
        if (project is not null) participation.IsOn = await context.GetPreferenceParticipationAsync(project, ct);
        panel.Children.Add(participation);
        if (project is null) panel.Children.Add(new TextBlock { Text = "在项目作用域授权并添加候选；此处查看全部条目。", Style = (Style)Application.Current.Resources["Caption"] });
        var list = new ListView { Height = 170, SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetName(list, "通用偏好条目");
        panel.Children.Add(list);
        var editor = new TextBox { Header = "通用表述", PlaceholderText = "例如：回答时，请先列依据，再说明待确认项。", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 100, MaxLength = 500 };
        panel.Children.Add(editor);
        var sources = new ComboBox { Header = "来源", DisplayMemberPath = "Name", HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = project is null ? Visibility.Collapsed : Visibility.Visible };
        var artifacts = project is null ? Array.Empty<ArtifactRecord>() : (await store.ListAsync(projectId: project, cancellationToken: ct)).Where(a => a.ProjectId == project && a.Hash.Length == 64 && a.Preview.Length > 0 && a.Source != "应用").Take(24).ToArray();
        sources.ItemsSource = artifacts.Select(a => new PreferenceSourceChoice(a.Id, a.Title)).ToArray();
        if (artifacts.Length > 0) sources.SelectedIndex = 0;
        panel.Children.Add(sources);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var add = new Button { Content = "添加候选", Visibility = project is null ? Visibility.Collapsed : Visibility.Visible };
        var fromModel = new Button { Content = "使用生成候选", Visibility = project is null ? Visibility.Collapsed : Visibility.Visible };
        var disable = new Button { Content = "停用" };
        var reject = new Button { Content = "拒绝" };
        actions.Children.Add(add); actions.Children.Add(fromModel); actions.Children.Add(disable); actions.Children.Add(reject);
        panel.Children.Add(actions);
        var status = new TextBlock { Style = (Style)Application.Current.Resources["Caption"], TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(status);
        var scroll = new ScrollViewer { Content = panel, MaxHeight = Math.Max(240, Math.Min(580, Root.ActualHeight - 190)), VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var dialog = new ContentDialog { Title = "通用偏好", Content = scroll, CloseButtonText = "关闭", PrimaryButtonText = "确认偏好", SecondaryButtonText = "删除", IsPrimaryButtonEnabled = false, IsSecondaryButtonEnabled = false, XamlRoot = Root.XamlRoot, RequestedTheme = ElementTheme.Light, Style = (Style)Application.Current.Resources["AmifolioDialogStyle"] };
        ApplyDialogTheme(dialog);
        PreferenceEntry? selected = null; var loading = false; var busy = false; string? candidateName = null;
        var persistedParticipation = participation.IsOn;
        var permissionVerified = true;

        void UpdateControls()
        {
            list.IsEnabled = editor.IsEnabled = sources.IsEnabled = !busy;
            participation.IsEnabled = !busy && permissionVerified;
            dialog.IsPrimaryButtonEnabled = !busy && selected?.Status is ("candidate" or "confirmed");
            dialog.IsSecondaryButtonEnabled = !busy && selected is not null;
            disable.IsEnabled = !busy && selected?.Status == "confirmed";
            disable.Content = selected?.Enabled == false ? "启用" : "停用";
            reject.IsEnabled = !busy && selected?.Status == "candidate";
            add.IsEnabled = !busy && project is not null && permissionVerified && persistedParticipation && artifacts.Length > 0;
            fromModel.IsEnabled = !busy && project is not null && permissionVerified && persistedParticipation;
        }
        void BindSelection()
        {
            selected = (list.SelectedItem as ListViewItem)?.Tag as PreferenceEntry;
            editor.Text = selected?.Text ?? "";
            candidateName = null;
            if (selected is not null)
            {
                sources.SelectedItem = sources.Items.OfType<PreferenceSourceChoice>().FirstOrDefault(x => x.Id == selected.SourceId);
                var projectName = projects.FirstOrDefault(x => x.Id == selected.ProjectId)?.Name ?? "来源项目";
                status.Text = $"{projectName} · 来源 {selected.Sources.Count} 项 · 主来源版本 {selected.SourceVersion} · {PreferenceStatusLabel(selected.Status, selected.Enabled)}";
            }
            UpdateControls();
        }
        async Task Refresh(string? selectedId = null, CancellationToken? refreshToken = null)
        {
            var token = refreshToken ?? ct;
            var allowed = project is not null && await context.GetPreferenceParticipationAsync(project, token);
            var snapshot = await context.ReadPreferencesAsync(token);
            loading = true;
            try
            {
                persistedParticipation = allowed;
                permissionVerified = true;
                participation.IsOn = allowed;
                list.Items.Clear();
                foreach (var entry in snapshot.Entries.Where(x => project is null || x.ProjectId == project))
                {
                    var row = new StackPanel { Spacing = 4, Padding = new Thickness(4) };
                    row.Children.Add(new TextBlock { Text = entry.Text, TextWrapping = TextWrapping.Wrap, MaxLines = 2 });
                    row.Children.Add(new TextBlock { Text = $"{PreferenceStatusLabel(entry.Status, entry.Enabled)} · {entry.CreatedAt.LocalDateTime:MM-dd HH:mm}", Style = (Style)Application.Current.Resources["Caption"] });
                    var item = new ListViewItem { Content = row, Tag = entry, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    list.Items.Add(item); if (entry.Id == selectedId) list.SelectedItem = item;
                }
                // SelectionChanged is suppressed while rebuilding. Bind the text and source
                // explicitly so an old editor can never be confirmed against a new entry.
                BindSelection();
            }
            finally { loading = false; }
        }
        async Task Perform(Func<Task> action)
        {
            if (busy) return;
            busy = true; UpdateControls();
            var selectedId = selected?.Id;
            string? failure = null;
            try { await PauseContextRaceAsync("preference-action"); await action(); }
            catch (OperationCanceledException) { failure = "操作已取消。"; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { failure = error is ArgumentException ? "请填写通用偏好，移除项目标识、路径或敏感内容。" : "来源或状态已变化，请刷新后重试。"; }
            finally
            {
                if (failure is not null && !closing)
                {
                    // The original token may have been cancelled after a durable write.
                    // Recover from persisted permission instead of trusting the toggled UI.
                    try { await Refresh(selectedId, backgroundTasks.Token); }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        loading = true;
                        try { participation.IsOn = persistedParticipation; }
                        finally { loading = false; }
                        permissionVerified = false;
                        failure += " 许可状态未能核对，请关闭后重新打开。";
                    }
                    status.Text = failure;
                }
                busy = false; UpdateControls();
            }
        }
        list.SelectionChanged += (_, _) =>
        {
            if (loading) return;
            if (busy)
            {
                loading = true;
                try { list.SelectedItem = list.Items.OfType<ListViewItem>().FirstOrDefault(x => (x.Tag as PreferenceEntry)?.Id == selected?.Id); }
                finally { loading = false; }
                return;
            }
            BindSelection();
        };
        participation.Toggled += async (_, _) =>
        {
            if (loading || project is null) return;
            if (busy)
            {
                loading = true;
                try { participation.IsOn = persistedParticipation; }
                finally { loading = false; }
                return;
            }
            var allowed = participation.IsOn;
            await Perform(async () =>
            {
                await context.SetPreferenceParticipationAsync(project, allowed, ct);
                await Refresh(); status.Text = allowed ? "参与已开启，候选仍需确认。" : "参与已撤销，相关派生偏好已撤回。";
            });
        };
        dialog.Closing += (_, args) => { if (busy) args.Cancel = true; };
        fromModel.Click += async (_, _) => await Perform(async () =>
        {
            var snapshot = await context.ReadAsync(project, ct);
            var picked = new[] { "candidate-style.md", "candidate-memory.md" }.FirstOrDefault(snapshot.CandidateDocuments.ContainsKey);
            if (picked is null) { status.Text = "尚无风格或记忆候选。请先在本项目生成候选；没有明确偏好证据时可不添加。"; return; }
            var raw = snapshot.CandidateDocuments[picked].Replace("\r\n", "\n");
            var body = raw.Split("\n\n", 3).Last();
            loading = true;
            try { list.SelectedItem = null; selected = null; }
            finally { loading = false; }
            candidateName = picked;
            // Preserve model evidence for review; the user supplies the final general wording.
            editor.Text = body.Length <= 500 ? body : "";
            status.Text = body.Length <= 500 ? "请核对并改为通用表述，再添加候选。" : "生成内容较长，请回到风格或记忆候选查看后，在此填写通用表述。";
        });
        add.Click += async (_, _) =>
        {
            if (project is null || sources.SelectedItem is not PreferenceSourceChoice source) return;
            var name = candidateName; var text = editor.Text; var sourceId = source.Id;
            await Perform(async () =>
            {
                var entry = name is null ? await context.ProposePreferenceAsync(project, sourceId, text, ct) : await context.ProposeFromCandidateAsync(project, name, sourceId, text, ct);
                await Refresh(entry.Id); status.Text = "候选已添加，确认后才参与共享。";
            });
        };
        disable.Click += async (_, _) =>
        {
            var entry = selected; if (entry is null) return;
            await Perform(async () => { await context.SetPreferenceEnabledAsync(entry.Id, !entry.Enabled, ct); await Refresh(entry.Id); status.Text = "偏好状态已更新。"; });
        };
        reject.Click += async (_, _) =>
        {
            var id = selected?.Id; if (id is null) return;
            await Perform(async () => { await context.RejectPreferenceAsync(id, ct); await Refresh(id); status.Text = "候选已拒绝。"; });
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; var deferral = args.GetDeferral();
            var id = selected?.Id; var text = editor.Text;
            try { if (id is not null) await Perform(async () => { await context.ConfirmPreferenceAsync(id, text, ct); await Refresh(id); status.Text = "偏好已确认；是否向 Codex 提供由全局共享开关控制。"; }); }
            finally { deferral.Complete(); }
        };
        dialog.SecondaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; var deferral = args.GetDeferral();
            var id = selected?.Id;
            try { if (id is not null) await Perform(async () => { await context.DeletePreferenceAsync(id, ct); await Refresh(); status.Text = "偏好已删除。"; }); }
            finally { deferral.Complete(); }
        };
        await Refresh();
        return dialog;
    }
}
