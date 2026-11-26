using System.Collections.Concurrent;
using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.System;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private const int WorksHomeLimit = 60;
    private ComboBox worksHomeScope = null!;
    private TextBox worksHomeSearch = null!;
    private ListView worksHomeList = null!;
    private StackPanel worksHomeSyncPanel = null!;
    private ToggleSwitch worksHomeSync = null!;
    private TextBlock worksHomeSyncNote = null!;
    private TextBlock worksHomeStatus = null!;
    private HyperlinkButton worksHomeConnection = null!;
    private WorksHomeProject[] worksHomeProjects = [];
    private CancellationTokenSource? worksHomeLoad;
    private bool updatingWorksHome;
    private bool savingWorksHomeSync;
    private StackPanel? expandedWork;

    private sealed record WorksHomeProject(string? Id, string Name);

    private void InitializeWorksHome()
    {
        var heading = new TextBlock { Text = "作品", Style = (Style)Application.Current.Resources["PageHeading"] };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level1);
        var refresh = new HyperlinkButton { Content = "更新作品", HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center };
        refresh.Click += async (_, _) =>
        {
            await RefreshCatalogInBackgroundAsync();
            await RefreshWorksHomeAsync();
        };
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(heading);
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);

        worksHomeScope = new ComboBox { DisplayMemberPath = nameof(WorksHomeProject.Name), HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(worksHomeScope, "筛选项目");
        worksHomeSearch = new TextBox { PlaceholderText = "搜索作品名称" };
        AutomationProperties.SetName(worksHomeSearch, "搜索作品名称");
        var filters = new Grid { ColumnSpacing = 10, RowSpacing = 8 };
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        filters.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        filters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        filters.Children.Add(worksHomeScope);
        filters.Children.Add(worksHomeSearch);
        Grid.SetColumn(worksHomeSearch, 1);
        filters.SizeChanged += (_, e) =>
        {
            var narrow = e.NewSize.Width < 430;
            Grid.SetColumnSpan(worksHomeScope, narrow ? 2 : 1);
            Grid.SetColumnSpan(worksHomeSearch, narrow ? 2 : 1);
            Grid.SetColumn(worksHomeSearch, narrow ? 0 : 1);
            Grid.SetRow(worksHomeSearch, narrow ? 1 : 0);
        };

        worksHomeSync = new ToggleSwitch { Header = "同步此项目的作品", IsEnabled = false };
        worksHomeSyncNote = WorksHomeCaption("");
        worksHomeSyncPanel = new StackPanel { Spacing = 3, Visibility = Visibility.Collapsed };
        worksHomeSyncPanel.Children.Add(worksHomeSync);
        worksHomeSyncPanel.Children.Add(worksHomeSyncNote);

        worksHomeStatus = WorksHomeCaption("正在查找已同步的作品…");
        AutomationProperties.SetLiveSetting(worksHomeStatus, AutomationLiveSetting.Polite);
        worksHomeConnection = new HyperlinkButton { Content = "查看插件设置", Visibility = Visibility.Collapsed };
        worksHomeConnection.Click += async (_, _) => await RequestNavigationAsync("connections");
        worksHomeList = new ListView { SelectionMode = ListViewSelectionMode.Single, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(worksHomeList, "最近作品");
        ScrollViewer.SetVerticalScrollMode(worksHomeList, ScrollMode.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(worksHomeList, ScrollBarVisibility.Disabled);
        var library = new HyperlinkButton { Content = "浏览项目文件" };
        library.Click += async (_, _) => await RequestNavigationAsync("library");

        var content = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(24, 20, 24, 28) };
        foreach (var element in new UIElement[] { header, filters, worksHomeSyncPanel, worksHomeStatus,
                     worksHomeConnection, worksHomeList, library })
            content.Children.Add(element);
        var scroll = new ScrollViewer
        {
            Content = content, HorizontalContentAlignment = HorizontalAlignment.Left,
            HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        scroll.SizeChanged += (_, e) => content.Width = Math.Clamp(e.NewSize.Width - 56, 0, 920);
        WorksHomePage.Children.Add(scroll);

        worksHomeScope.SelectionChanged += async (_, _) =>
        {
            if (updatingWorksHome) return;
            worksHomeList.Items.Clear();
            await RefreshWorksHomeAsync();
        };
        worksHomeSearch.TextChanged += async (_, _) => { if (!updatingWorksHome) await LoadWorksHomeAsync(debounce: true); };
        worksHomeSync.Toggled += async (_, _) => await SaveWorksHomeSyncAsync();
        worksHomeList.SelectionChanged += (_, _) => ExpandSelectedWork();
    }

    private Task RefreshWorksHomeAsync() => LoadWorksHomeAsync(debounce: false);

    private async Task LoadWorksHomeAsync(bool debounce)
    {
        if (closing) return;
        worksHomeLoad?.Cancel();
        using var request = CancellationTokenSource.CreateLinkedTokenSource(backgroundTasks.Token);
        worksHomeLoad = request;
        var token = request.Token;
        try
        {
            if (debounce) await Task.Delay(200, token);

            var projects = SourceStore.CodexProjects.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(p => p.RootPath, StringComparer.OrdinalIgnoreCase).ToArray();
            var duplicateNames = projects.GroupBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
            WorksHomeProject[] choices = [new(null, "所有项目"), .. projects.Select(p =>
                new WorksHomeProject(p.Id, duplicateNames.Contains(p.Name) ? $"{p.Name} · {p.RootPath}" : p.Name))];
            if (!worksHomeProjects.SequenceEqual(choices))
            {
                var previous = (worksHomeScope.SelectedItem as WorksHomeProject)?.Id;
                worksHomeProjects = choices;
                updatingWorksHome = true;
                try
                {
                    worksHomeScope.ItemsSource = choices;
                    worksHomeScope.SelectedItem = choices.FirstOrDefault(p => p.Id == previous) ?? choices[0];
                }
                finally { updatingWorksHome = false; }
            }

            var projectId = (worksHomeScope.SelectedItem as WorksHomeProject)?.Id;
            var search = worksHomeSearch.Text.Trim();
            var projectNames = projects.ToDictionary(p => p.Id, p => p.Name, StringComparer.Ordinal);
            worksHomeScope.IsEnabled = worksHomeSearch.IsEnabled = projects.Length > 0;
            worksHomeSyncPanel.Visibility = projectId is null ? Visibility.Collapsed : Visibility.Visible;
            worksHomeSync.IsEnabled = false;
            worksHomeSyncNote.Text = projectId is null ? "" : "正在读取同步设置…";
            worksHomeSyncNote.Visibility = Visibility.Visible;
            worksHomeConnection.Visibility = Visibility.Collapsed;
            SetWorksHomeStatus(projects.Length == 0 ? "" : "正在查找已同步的作品…");

            var recent = new ConcurrentBag<IReadOnlyList<CodexWork>>();
            var matchingProjects = projects.Where(p => projectId is null || p.Id == projectId);
            // Reuse the project's bounded query. Limit concurrency because each query opens its own store session.
            await Parallel.ForEachAsync(matchingProjects,
                new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token }, async (project, ct) =>
                recent.Add(await SourceStore.ListCodexWorksAsync(project.Id, search, ct).ConfigureAwait(false)));
            var sync = projectId is null ? null : await SourceStore.WorkSyncAsync(projectId, token);
            token.ThrowIfCancellationRequested();

            if (sync is not null)
            {
                updatingWorksHome = true;
                try { worksHomeSync.IsOn = sync.Enabled; }
                finally { updatingWorksHome = false; }
                worksHomeSync.IsEnabled = !savingWorksHomeSync;
                worksHomeSyncNote.Text = sync.State switch
                {
                    "unsupported" => "最近一轮未能核对作品来源。已同步的作品仍可查看。",
                    "waiting" => "已开启。Codex 完成新回合后会同步作品。",
                    "off" => "开启后，同步此项目新回合中的上传件和交付物。",
                    _ => ""
                };
                worksHomeSyncNote.Visibility = string.IsNullOrEmpty(worksHomeSyncNote.Text) ? Visibility.Collapsed : Visibility.Visible;
            }

            var items = recent.SelectMany(list => list).OrderByDescending(work => work.RecordedAt)
                .ThenBy(work => work.Title, StringComparer.Ordinal).ThenBy(work => work.Id, StringComparer.Ordinal)
                .Take(WorksHomeLimit + 1).ToArray();
            var selectedId = ((worksHomeList.SelectedItem as ListViewItem)?.Tag as CodexWork)?.Id;
            worksHomeList.Items.Clear();
            foreach (var work in items.Take(WorksHomeLimit))
            {
                var row = CreateWorksHomeRow(work, projectNames[work.ProjectId]);
                worksHomeList.Items.Add(row);
                if (work.Id == selectedId) worksHomeList.SelectedItem = row;
            }
            worksHomeList.Visibility = items.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            SetWorksHomeStatus((projects.Length, items.Length) switch
            {
                (0, _) => "还没有发现项目。在 Codex 打开一个本机项目后，再更新作品。",
                (_, 0) when search.Length > 0 => "没有与此名称匹配的作品。",
                (_, 0) when projectId is null => "还没有同步作品。选一个项目开启同步，上传件和交付物会在 Codex 完成新回合后出现在这里。",
                (_, 0) => "这个项目还没有同步作品。",
                (_, > WorksHomeLimit) => $"显示最近 {WorksHomeLimit} 件。可按项目或名称缩小范围。",
                _ => ""
            });
            worksHomeConnection.Visibility = items.Length == 0 && search.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;
            worksHomeList.Items.Clear();
            worksHomeList.Visibility = Visibility.Collapsed;
            worksHomeSyncNote.Text = "无法读取同步设置。";
            SetWorksHomeStatus("暂时无法读取作品，请点击更新作品重试。");
            System.Diagnostics.Trace.TraceWarning("Works home refresh failed: {0}", ex.GetType().Name);
        }
        finally
        {
            if (ReferenceEquals(worksHomeLoad, request)) worksHomeLoad = null;
        }
    }

    private async Task SaveWorksHomeSyncAsync()
    {
        if (closing || updatingWorksHome || savingWorksHomeSync || !worksHomeSync.IsEnabled ||
            (worksHomeScope.SelectedItem as WorksHomeProject)?.Id is not { } projectId) return;

        var enabled = worksHomeSync.IsOn;
        savingWorksHomeSync = true;
        worksHomeSync.IsEnabled = false;
        var failed = false;
        try { await SourceStore.SetWorkSyncAsync(projectId, enabled, backgroundTasks.Token); }
        catch (OperationCanceledException) when (closing) { }
        catch (Exception ex)
        {
            failed = true;
            System.Diagnostics.Trace.TraceWarning("Works sync save failed: {0}", ex.GetType().Name);
        }
        finally { savingWorksHomeSync = false; }

        if (closing) return;
        await RefreshWorksHomeAsync();
        if (failed && !closing) StatusText.Text = "同步设置未保存，请重试。";
    }

    private void SetWorksHomeStatus(string message)
    {
        worksHomeStatus.Text = message;
        worksHomeStatus.Visibility = message.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    private static ListViewItem CreateWorksHomeRow(CodexWork work, string projectName)
    {
        var metadata = $"{work.SourceLabel} · {projectName} · {work.RecordedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
        var caption = WorksHomeCaption(metadata);
        caption.MaxLines = 2;
        caption.TextTrimming = TextTrimming.CharacterEllipsis;
        var title = new TextBlock { Text = work.Title, FontSize = 18, TextWrapping = TextWrapping.Wrap,
            Foreground = WorksHomeBrush("Ink"), MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(title);
        text.Children.Add(caption);
        var row = new ListViewItem { Content = text, Tag = work, Padding = new Thickness(14, 10, 14, 10),
            HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(row, $"{work.Title}，{metadata}");
        return row;
    }

    private void ExpandSelectedWork()
    {
        if (expandedWork is not null)
        {
            expandedWork.Children.RemoveAt(expandedWork.Children.Count - 1);
            expandedWork = null;
        }
        if (worksHomeList.SelectedItem is not ListViewItem { Content: StackPanel text, Tag: CodexWork work }) return;

        var details = new StackPanel { Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var session = WorksHomeCaption($"来源会话 {work.SessionId}");
        session.IsTextSelectionEnabled = true;
        ToolTipService.SetToolTip(session, $"回合 {work.TurnId}");
        details.Children.Add(session);
        var sourceNote = WorksHomeCaption("");
        details.Children.Add(sourceNote);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        details.Children.Add(actions);
        details.SizeChanged += (_, e) => actions.Orientation = e.NewSize.Width < 420 ? Orientation.Vertical : Orientation.Horizontal;
        var originalActions = new List<Button>(3);
        AddAction("打开原件", "打开原件", async (artifact, ct) =>
        {
            var path = await SourceStore.GetOriginalPathAsync(artifact.Id, ct);
            var file = await StorageFile.GetFileFromPathAsync(path);
            ct.ThrowIfCancellationRequested();
            if (!await Launcher.LaunchFileAsync(file)) StatusText.Text = "系统未能打开这份原件。";
        });
        AddAction("预览", "预览作品", PreviewWorksHomeOriginalAsync);
        AddAction("用作范例", "打开范例设置", (artifact, ct) => EditCreativeAsync(work.ProjectId, artifact, ct));
        var source = new Button { Content = "打开来源会话", IsEnabled = Guid.TryParseExact(work.SessionId, "D", out _) };
        source.Click += async (_, _) => await RunAsync("打开来源会话", async ct =>
        {
            ct.ThrowIfCancellationRequested();
            if (Guid.TryParseExact(work.SessionId, "D", out var sessionId) &&
                !await Launcher.LaunchUriAsync(new Uri($"codex://threads/{sessionId:D}")))
                StatusText.Text = "系统未能打开 Codex 来源会话。";
        });
        actions.Children.Add(source);
        if (work.ReferenceId is null || work.Availability is "引用不可用" or "源文件缺失" or "源文件不可读")
            ShowSourceUnavailable();
        else if (work.Availability.StartsWith("源文件已变化", StringComparison.Ordinal))
            sourceNote.Text = "原件有更新，将打开当前文件。";
        else sourceNote.Visibility = Visibility.Collapsed;

        text.Children.Add(details);
        expandedWork = text;

        void AddAction(string label, string operationLabel, Func<ArtifactRecord, CancellationToken, Task> action)
        {
            var button = new Button { Content = label };
            button.Click += async (_, _) => await RunAsync(operationLabel, async ct =>
            {
                try
                {
                    if (work.ReferenceId is not { } id || await SourceStore.GetAsync(id, ct) is not { IsReference: true } artifact ||
                        artifact.ProjectId != work.ProjectId || artifact.ExtractionStatus is "源文件缺失" or "源文件不可读")
                    {
                        ShowSourceUnavailable();
                        return;
                    }
                    await action(artifact, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or KeyNotFoundException)
                {
                    var current = work.ReferenceId is { } reference ? await SourceStore.GetAsync(reference, ct) : null;
                    if (current is null || current.ExtractionStatus is "源文件缺失" or "源文件不可读") ShowSourceUnavailable();
                    else throw;
                }
            });
            actions.Children.Add(button);
            originalActions.Add(button);
        }

        void ShowSourceUnavailable()
        {
            sourceNote.Text = "原件不可用，可回到来源会话查看。";
            sourceNote.Visibility = Visibility.Visible;
            foreach (var button in originalActions) button.Visibility = Visibility.Collapsed;
        }
    }

    private async Task PreviewWorksHomeOriginalAsync(ArtifactRecord artifact, CancellationToken ct)
    {
        var path = await SourceStore.GetOriginalPathAsync(artifact.Id, ct);
        var content = new StackPanel { Spacing = 12, MaxWidth = 480 };
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp") && new FileInfo(path).Length <= 32 * 1024 * 1024)
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            var bitmap = new BitmapImage { DecodePixelWidth = 960 };
            try
            {
                await bitmap.SetSourceAsync(stream);
                content.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, MaxHeight = 360 });
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                content.Children.Add(WorksHomeCaption("无法预览此图片，可打开原件查看。"));
            }
        }
        else if (!string.IsNullOrWhiteSpace(artifact.Preview))
        {
            var excerpt = artifact.Preview[..Math.Min(artifact.Preview.Length, 4000)];
            content.Children.Add(new TextBlock { Text = excerpt, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
        else content.Children.Add(WorksHomeCaption("这份作品暂无预览，可打开原件查看。"));

        var original = WorksHomeCaption(path);
        original.IsTextSelectionEnabled = true;
        content.Children.Add(original);
        ct.ThrowIfCancellationRequested();
        await ShowMessageAsync(artifact.Title, new ScrollViewer { Content = content, MaxHeight = 470 });
    }

    private static TextBlock WorksHomeCaption(string text) => new()
    {
        Text = text, Style = (Style)Application.Current.Resources["Caption"]
    };

    private static Brush WorksHomeBrush(string key) => (Brush)Application.Current.Resources[key];
}
