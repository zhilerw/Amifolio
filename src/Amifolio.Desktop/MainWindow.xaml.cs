using System.Diagnostics;
using System.Text.Json;
using Amifolio.Core;
using Amifolio.Context;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.ApplicationModel.DataTransfer;

namespace Amifolio.Desktop;

public sealed partial class MainWindow : Window
{
    private readonly IPortfolioStore store;
    private readonly ContextService context;
    private readonly AgentsFileService agents = new();
    private readonly string dataRoot;
    private readonly Dictionary<string, FrameworkElement> pages;
    private readonly Dictionary<string, Button> navigation;
    private IReadOnlyList<ProjectRecord> projects = [];
    private bool ready;
    private bool updating;
    private bool loadingPage;
    private bool closing;
    private CancellationTokenSource? operation;
    private CancellationTokenSource? searchDelay;
    private string currentPage = "library";
    private string? selectedArtifactId;
    private readonly int? cornerPreferenceResult;
    private readonly DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Stopwatch operationAge = new();
    private MotionController? motion;
    private readonly Queue<bool> smokeDialogAnswers = new();
    private readonly List<SmokeDialogObservation> smokeDialogs = [];

    public MainWindow()
    {
        InitializeComponent();
        dataRoot = Option("--data-root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amifolio");
        store = new PortfolioStore(dataRoot);
        context = new ContextService(store);
        pages = new() { ["library"] = LibraryPage, ["projects"] = ProjectsPage, ["context"] = ContextPage, ["help"] = HelpPage, ["agents"] = AgentsPage, ["connections"] = ConnectionsPage, ["settings"] = SettingsPage };
        navigation = new() { ["library"] = LibraryNav, ["projects"] = ProjectsNav, ["context"] = ContextNav, ["help"] = HelpNav, ["agents"] = AgentsNav, ["connections"] = ConnectionsNav, ["settings"] = SettingsNav };
        motion = new MotionController(Root);
        Activated += (_, e) => motion.SetWindowActive(e.WindowActivationState != WindowActivationState.Deactivated);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleDragArea);
        cornerPreferenceResult = WindowAppearance.ApplySmallCorners(this);
        AppWindow.Resize(new SizeInt32(1320, 880));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = true;
        }
        AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Amifolio.ico"));
        noticeTimer.Tick += (_, _) =>
        {
            noticeTimer.Stop();
            if (operation is not null) UpdateOperationNotice();
            else motion.SetVisible(OperationNotice, false, MotionKind.Notice);
        };
        StatusText.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateOperationNotice());
        StoragePath.Text = dataRoot;
        BridgeCommand.Text = JsonSerializer.Serialize(new { mcp_servers = new { amifolio = new { command = BridgeExecutable(), args = new[] { "--data-root", dataRoot, "mcp" } } } }, new JsonSerializerOptions { WriteIndented = true });
        SelectPage("library");
        Closed += (_, _) => { closing = true; noticeTimer.Stop(); motion.Dispose(); operation?.Cancel(); searchDelay?.Cancel(); ModelKey.Password = ""; };
    }

    private static string? Option(string key)
    {
        var args = Environment.GetCommandLineArgs();
        var index = Array.IndexOf(args, key);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string BridgeExecutable()
    {
        var sibling = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "bridge", "Amifolio.Bridge.exe"));
        return sibling;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (ready) return;
        var scale = Root.XamlRoot.RasterizationScale;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        AppWindow.Resize(new SizeInt32(Math.Min((int)(1320 * scale), display.WorkArea.Width - 32), Math.Min((int)(880 * scale), display.WorkArea.Height - 48)));
        await RunAsync("正在打开本机作品库", async token =>
        {
            await Task.Run(() => store.InitializeAsync(token), token);
            await RefreshProjectsAsync();
            updating = true;
            SourceFilter.SelectedIndex = 0;
            KindFilter.ItemsSource = new[] { "全部类型" };
            KindFilter.SelectedIndex = 0;
            updating = false;
            ready = true;
            await RefreshLibraryAsync();
            StatusText.Text = "就绪";
        });
        if (ready && Option("--smoke-report") is { } report)
            await RunNativeSmokeAsync(report);
    }

    private async void Navigate(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string page } || !ready) return;
        await RequestNavigationAsync(page);
    }

    private async Task<bool> RequestNavigationAsync(string page)
    {
        if (operation is not null) return false;
        if (page == currentPage) return true;
        if (currentPage == "context" && !await CanLeaveContextAsync()) return false;
        if (currentPage == "agents" && !await CanLeaveAgentsAsync()) return false;
        await RunAsync("正在切换工作空间", async _ => await NavigateAsync(page));
        return currentPage == page;
    }

    private async Task NavigateAsync(string page)
    {
        SelectPage(page);
        loadingPage = true;
        try
        {
            switch (page)
            {
                case "library": await RefreshLibraryAsync(); break;
                case "projects": await RefreshProjectsAsync(); break;
                case "context": await LoadContextAsync(); break;
                case "agents": await LoadAgentsForScopeAsync(); break;
            }
        }
        finally { loadingPage = false; }
        if (!closing && currentPage == page) motion?.Enter(pages[page], MotionKind.Page);
        StatusText.Text = "就绪";
    }

    private void SelectPage(string page)
    {
        var changed = currentPage != page;
        currentPage = page;
        foreach (var pair in pages)
        {
            if (pair.Key != page) motion?.Stop(pair.Value);
            pair.Value.Visibility = pair.Key == page ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var pair in navigation)
        {
            var selected = pair.Key == page;
            pair.Value.Background = new SolidColorBrush(selected ? ColorHelper.FromArgb(255, 221, 239, 232) : Colors.Transparent);
            pair.Value.BorderBrush = selected ? (Brush)Application.Current.Resources["Accent"] : new SolidColorBrush(Colors.Transparent);
            pair.Value.Foreground = (Brush)Application.Current.Resources[selected ? "Accent" : "Ink"];
            if (selected && changed && ready) motion?.Enter(pair.Value, MotionKind.Selection);
        }
    }

    private async Task RunAsync(string label, Func<CancellationToken, Task> action)
    {
        if (operation is not null) { StatusText.Text = "请先完成或取消当前任务。"; return; }
        using var taskCancellation = new CancellationTokenSource();
        operation = taskCancellation;
        operationAge.Restart();
        BusyIndicator.Visibility = CancelButton.Visibility = Visibility.Visible;
        ImportButton.IsEnabled = false;
        SetScopeControlsEnabled(false);
        StatusText.Text = label + "…";
        UpdateOperationNotice(); // The same task label may be reused after its previous notice expired.
        try { await action(taskCancellation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "任务已取消；此前已完成的入库仍保留。"; }
        catch (Exception ex)
        {
            if (closing) return;
            StatusText.Text = "操作未完成：" + ex.Message;
            if (Option("--smoke-report") is { } report)
                await File.WriteAllTextAsync(report + ".error.txt", ex.ToString());
            else await ShowMessageAsync("操作未完成", ex.Message);
        }
        finally
        {
            operation = null;
            operationAge.Stop();
            if (!closing)
            {
                BusyIndicator.Visibility = CancelButton.Visibility = Visibility.Collapsed;
                ImportButton.IsEnabled = true;
                SetScopeControlsEnabled(true);
                UpdateOperationNotice();
            }
        }
    }

    private void SetScopeControlsEnabled(bool enabled)
    {
        foreach (var control in new Control[] { ContextScope, ContextDocument, ContextEnabled, OriginalAccess, AgentsScope, AgentsPath, ProjectFilter, KindFilter, SourceFilter, EditProject, ContextEditor, AgentsEditor })
            control.IsEnabled = enabled;
        UpdateEditingControls();
    }

    private void UpdateOperationNotice()
    {
        if (closing) return;
        noticeTimer.Stop();
        var show = !string.IsNullOrWhiteSpace(StatusText.Text) && StatusText.Text != "就绪";
        var elapsed = operationAge.ElapsedMilliseconds;
        if (show && operation is not null && elapsed < 180 && OperationNotice.Visibility != Visibility.Visible)
        {
            noticeTimer.Interval = TimeSpan.FromMilliseconds(181 - elapsed);
            noticeTimer.Start();
            return;
        }
        motion?.SetVisible(OperationNotice, show, MotionKind.Notice);
        if (show && operation is null) { noticeTimer.Interval = TimeSpan.FromSeconds(4); noticeTimer.Start(); }
    }

    private void CancelOperation(object sender, RoutedEventArgs e) => operation?.Cancel();

    private async Task<bool> ConfirmAsync(string title, object content, string primary = "确认")
    {
        var dialog = new ContentDialog { Title = title, Content = content, PrimaryButtonText = primary, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close, XamlRoot = Root.XamlRoot, RequestedTheme = ElementTheme.Light, Style = (Style)Application.Current.Resources["AmifolioDialogStyle"] };
        ApplyDialogTheme(dialog);
        if (Option("--smoke-report") is not { } report) return await dialog.ShowAsync() == ContentDialogResult.Primary;
        if (!smokeDialogAnswers.TryDequeue(out var answer)) throw new InvalidOperationException("Unexpected confirmation in native regression: " + title);
        var showing = dialog.ShowAsync();
        try
        {
            await Task.Delay(200);
            await RenderSnapshotAsync(report + $".dialog-{smokeDialogs.Count}.png", dialog);
            smokeDialogs.Add(new(title, answer, (dialog.Background as SolidColorBrush)?.Color.ToString()));
        }
        finally { dialog.Hide(); }
        await showing;
        return answer;
    }

    private async Task ShowMessageAsync(string title, string content)
    {
        if (Option("--smoke-report") is not null) throw new InvalidOperationException(title + ": " + content);
        var dialog = new ContentDialog { Title = title, Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 }, CloseButtonText = "关闭", XamlRoot = Root.XamlRoot, RequestedTheme = ElementTheme.Light, Style = (Style)Application.Current.Resources["AmifolioDialogStyle"] };
        ApplyDialogTheme(dialog);
        await dialog.ShowAsync();
    }

    private static void ApplyDialogTheme(ContentDialog dialog)
    {
        // The native template draws its top surface separately from Background.
        // Keep these resources local so the popup cannot resolve the stock white overlay.
        foreach (var key in new[] { "ContentDialogBackground", "ContentDialogTopOverlay" })
            dialog.Resources[key] = Application.Current.Resources["Paper"];
        dialog.Resources["ContentDialogBorderBrush"] = Application.Current.Resources["Line"];
        dialog.Resources["ContentDialogSeparatorBorderBrush"] = Application.Current.Resources["Line"];
        dialog.Resources["ContentDialogForeground"] = Application.Current.Resources["Ink"];
    }

    private static ScopeChoice? SelectedScope(ComboBox box) => box.SelectedItem as ScopeChoice;
    private string? ContextProjectId => SelectedScope(ContextScope)?.Id;
    private static string FormatBytes(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024:F1} MB" : bytes >= 1024 ? $"{bytes / 1024d:F0} KB" : $"{bytes} B";
    private static string SourceCategory(string source) => source == "用户" ? "用户" : source == "应用" ? "应用" : "Agent";
    private static void OpenFolder(string path) => Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
    private void OpenStorage(object sender, RoutedEventArgs e) => OpenFolder(dataRoot);
    private void CopyConnection(object sender, RoutedEventArgs e)
    {
        var bridge = BridgeExecutable().Replace("\\", "\\\\").Replace("\"", "\\\"");
        var root = dataRoot.Replace("\\", "\\\\").Replace("\"", "\\\"");
        var payload = new DataPackage();
        payload.SetText($"[mcp_servers.amifolio]\ncommand = \"{bridge}\"\nargs = [\"--data-root\", \"{root}\", \"mcp\"]\n");
        Clipboard.SetContent(payload);
        StatusText.Text = "已复制 MCP 配置";
    }

    private sealed record ScopeChoice(string? Id, string Name);
    private sealed record SmokeDialogObservation(string Title, bool Answer, string? Background);
    private sealed record ArtifactItem(ArtifactRecord Record, string Title, string Subtitle, string Badge, string Size);
}
