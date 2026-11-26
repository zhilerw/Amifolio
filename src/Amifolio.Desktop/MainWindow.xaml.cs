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
    private readonly ModelConfigurationStore modelConfiguration;
    private FileStream? storeSession;
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
    private readonly CancellationTokenSource backgroundTasks=new();
    private string currentPage = "creative";
    private string? selectedArtifactId;
    private readonly int? cornerPreferenceResult;
    private SizeInt32 startupWindowSize;
    private int startupResizeRequests;
    private readonly DispatcherTimer noticeTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    private readonly Stopwatch operationAge = new();
    private MotionController? motion;
    private readonly Queue<bool> smokeDialogAnswers = new();
    private readonly List<SmokeDialogObservation> smokeDialogs = [];

    public MainWindow()
    {
        StartupTiming.Mark("main.ctor.begin");
        using(StartupTiming.Measure("main.profile-validation"))ValidatePerformanceInvocation();
        using(StartupTiming.Measure("main.initialize-component"))InitializeComponent();
        StartupTiming.Mark("main.services.begin");
        dataRoot = Option("--data-root") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amifolio");
        store = new PortfolioStore(dataRoot);
        context = new ContextService(store);
        modelConfiguration = new ModelConfigurationStore(dataRoot);
        pages = new() { ["works"] = WorksHomePage, ["creative"] = CreativePage, ["native-memory"] = NativeMemoryPage, ["library"] = LibraryPage, ["projects"] = ProjectsPage, ["tasks"]=TasksPage, ["learning"]=LearningPage, ["help"] = HelpPage, ["agents"] = AgentsPage, ["connections"] = ConnectionsPage, ["settings"] = SettingsPage };
        navigation = new() { ["works"] = LibraryNav, ["creative"] = ContextNav, ["agents"] = AgentsNav, ["settings"] = SettingsNav };
        StartupTiming.Mark("main.services.end");
        using(StartupTiming.Measure("main.integration"))InitializeIntegration();
        ((PortfolioStore)store).EnableCodexReferenceMode(integration.CodexHome);
        // Start bounded local I/O while the UI thread builds chrome and performs its first layout.
        // No discovery or configuration reads are moved ahead of the existing isolation check.
        startupPreparation=PrepareStartupAsync(backgroundTasks.Token);
        StartupTiming.Mark("main.window-chrome.begin");
        using(StartupTiming.Measure("main.motion-controller"))motion = new MotionController(Root);
        Activated += (_, e) => motion.SetWindowActive(e.WindowActivationState != WindowActivationState.Deactivated);
        using(StartupTiming.Measure("main.titlebar"))
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(TitleDragArea);
        }
        using(StartupTiming.Measure("main.window-corners"))cornerPreferenceResult = WindowAppearance.ApplySmallCorners(this);
        // Size once on Loaded, when the XamlRoot monitor scale and work area are available.
        StartupTiming.Mark("main.window-size.awaiting-monitor-scale");
        using(StartupTiming.Measure("main.presenter"))
        {
            if (AppWindow.Presenter is OverlappedPresenter presenter)presenter.IsMaximizable = true;
        }
        using(StartupTiming.Measure("main.titlebar-colors"))
        {
            AppWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
            AppWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        }
        using(StartupTiming.Measure("main.window-icon"))AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Amifolio.ico"));
        StartupTiming.Mark("main.window-chrome.end");
        noticeTimer.Tick += (_, _) =>
        {
            noticeTimer.Stop();
            if (operation is not null) UpdateOperationNotice();
            else motion.SetVisible(OperationNotice, false, MotionKind.Notice);
        };
        StatusText.RegisterPropertyChangedCallback(TextBlock.TextProperty, (_, _) => UpdateOperationNotice());
        StoragePath.Text = dataRoot;
        using(StartupTiming.Measure("main.browser"))InitializeBrowser();
        using(StartupTiming.Measure("main.learning"))InitializeLearningPage();
        InitializeWorksHome();
        Root.SizeChanged += (_, e) => SetNavigationDensity(e.NewSize.Width < 520);
        SelectPage("creative");
        StartupTiming.Mark("main.ctor.end");
        Closed += (_, _) => { closing = true; CloseStylePreview(); DisposeBrowser(); StopAutomaticProcessing(); noticeTimer.Stop(); motion.Dispose(); operation?.Cancel(); backgroundTasks.Cancel(); searchDelay?.Cancel(); ModelKey.Password = ""; storeSession?.Dispose(); };
    }

    private bool compactNavigation;

    private void SetNavigationDensity(bool compact)
    {
        if (compact == compactNavigation) return;
        compactNavigation = compact;
        NavigationColumn.Width = new GridLength(compact ? 62 : 110);
        LibraryNavLabel.Visibility = ContextNavLabel.Visibility = AgentsNavLabel.Visibility =
            compact ? Visibility.Collapsed : Visibility.Visible;
        foreach (var button in new[] { LibraryNav, ContextNav, AgentsNav })
        {
            if (compact)
            {
                button.Padding = new Thickness(5, 11, 5, 11);
                button.HorizontalContentAlignment = HorizontalAlignment.Center;
            }
            else
            {
                button.ClearValue(Control.PaddingProperty);
                button.ClearValue(Control.HorizontalContentAlignmentProperty);
            }
        }
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
        // Loaded can recur while the first invocation is awaiting storage. ready is still false then.
        if (startupStarted || closing) return;
        startupStarted=true;
        StartupTiming.Mark("main.loaded.begin");
        StartupTiming.Mark("startup.window-sizing.begin");
        var scale = Root.XamlRoot.RasterizationScale;
        var display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        startupWindowSize=new SizeInt32(Math.Min((int)(1320 * scale), display.WorkArea.Width - 32), Math.Min((int)(880 * scale), display.WorkArea.Height - 48));
        var currentSize=AppWindow.Size;
        if(currentSize.Width!=startupWindowSize.Width||currentSize.Height!=startupWindowSize.Height)
        {
            using(StartupTiming.Measure("startup.window-size-apply"))AppWindow.Resize(startupWindowSize);
            startupResizeRequests++;
        }
        StartupTiming.Mark("startup.window-sizing.end");
        StartupTiming.Mark("startup.operation.begin");
        await RunAsync("正在打开风格样张", async token =>
        {
            using(StartupTiming.Measure("startup.preparation-wait"))await startupPreparation.WaitAsync(token);
            using(StartupTiming.Measure("startup.model"))await LoadModelConfigurationAsync(token);
            using(StartupTiming.Measure("startup.scopes"))await RefreshProjectsAsync();
            StartupTiming.Mark("startup.filters.begin");
            updating = true;
            KindFilter.ItemsSource = new[] { "全部类型", "文档", "表格", "演示", "PDF", "代码", "笔记", "文本", "图片", "其他" };
            KindFilter.SelectedIndex = 0;
            updating = false;
            StartupTiming.Mark("startup.filters.end");
            using(StartupTiming.Measure("startup.library"))await RefreshLibraryAsync();
            using (StartupTiming.Measure("startup.creative")) await LoadCreativePageAsync();
            ready = true;
            StatusText.Text = "就绪";
        });
        StartupTiming.Mark("startup.operation.end");
        if(closing)return;
        if(ready)
        {
            try{await CaptureStartupReadinessAsync();}
            catch(OperationCanceledException)when(closing){return;}
        }
        StartupTiming.Mark("main.loaded.initialization.end");
        if (ready && Option("--profile-report") is { } profileReport)
        {
            await RunPerformanceProbeAsync(profileReport);
            return;
        }
        if (ready) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { if (!closing) _=RetireLegacyInBackgroundAsync(); });
        if (ready && Option("--smoke-report") is { } report)
            await RunNativeSmokeAsync(report);
        else if(ready) { _=RefreshCatalogInBackgroundAsync(); _=RefreshEnvironmentAsync(); _=ResumeStartupTasksAsync(); StartLearningProcessing(); }
    }

    private async void Navigate(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string page } || !ready) return;
        await RequestNavigationAsync(page);
    }

    private async Task<bool> RequestNavigationAsync(string page)
    {
        var mainLearning=page=="context";
        page=page=="context"?"learning":page;
        if (page == currentPage) { if(mainLearning){learningReturnPage="context";SelectPage(page);} return true; }
        if(currentPage=="learning"&&!await CanLeaveLearningAsync())return false;
        if (currentPage == "context" && !await CanLeaveContextAsync()) return false;
        if (currentPage == "agents" && !await CanLeaveAgentsAsync()) return false;
        if (currentPage == "creative" && !await CanLeaveCreativeAsync()) return false;
        if(mainLearning)learningReturnPage="context";
        if(operation is not null)
        {
            var previous=currentPage;
            try{await NavigateAsync(page);}
            catch(Exception ex){SelectPage(previous);StatusText.Text="无法打开页面："+ex.Message;return false;}
        }
        else await RunAsync("正在切换工作空间", async _ => await NavigateAsync(page));
        return currentPage == page;
    }

    private async Task NavigateAsync(string page)
    {
        if(page=="context"){learningReturnPage="context";page="learning";}
        SelectPage(page);
        loadingPage = true;
        try
        {
            switch (page)
            {
                case "works": await RefreshWorksHomeAsync(); break;
                case "creative": await LoadCreativePageAsync(); break;
                case "native-memory": await LoadNativeMemoryPageAsync(); break;
                case "library": await RefreshLibraryAsync(); break;
                case "projects": await RefreshProjectsAsync(); break;
                case "tasks": await LoadTasksAsync(); break;
                case "learning": if(LearningPage.Scope.Items.Count==0){updating=true;try{SetScopes(LearningPage.Scope,"全局偏好");}finally{updating=false;}} await RefreshLearningPageAsync(true);break;
                case "agents": await LoadAgentsForScopeAsync(); break;
                case "connections": await LoadCollectionGrantStatusAsync(); _=RefreshEnvironmentAsync(); break;
            }
        }
        finally { loadingPage = false; }
        if (!closing && currentPage == page) motion?.Enter(pages[page], MotionKind.Page);
        StatusText.Text = "就绪";
    }

    private void SelectPage(string page)
    {
        if(page=="context"){learningReturnPage="context";page="learning";}
        LearningPage.Back.Visibility=learningReturnPage=="context"?Visibility.Collapsed:Visibility.Visible;
        currentPage = page;
        foreach (var pair in pages)
        {
            if (pair.Key != page) motion?.Stop(pair.Value);
            pair.Value.Visibility = pair.Key == page ? Visibility.Visible : Visibility.Collapsed;
        }
        foreach (var pair in navigation)
        {
            var parent = page == "learning" ? learningReturnPage : page;
            parent = parent switch
            {
                "context" or "native-memory" => "creative",
                "library" or "projects" => "works",
                "tasks" or "connections" or "help" => "settings",
                _ => parent
            };
            var selected = pair.Key == parent;
            pair.Value.Style = (Style)Application.Current.Resources[selected ? "SelectedNavButton" : "NavButton"];
        }
    }

    private async Task RunAsync(string label, Func<CancellationToken, Task> action)
    {
        if (operation is not null) { StatusText.Text = "当前操作完成后可继续。"; return; }
        using var taskCancellation = new CancellationTokenSource();
        operation = taskCancellation;
        operationAge.Restart();
        BusyIndicator.Visibility = Visibility.Visible;
        ScanButton.IsEnabled = ParseSelectedButton.IsEnabled = false;
        SetScopeControlsEnabled(false);
        StatusText.Text = label + "…";
        UpdateOperationNotice(); // The same task label may be reused after its previous notice expired.
        try { await action(taskCancellation.Token); }
        catch (OperationCanceledException) { StatusText.Text = "任务已取消，原文件未更改。"; }
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
                BusyIndicator.Visibility = Visibility.Collapsed;
                ScanButton.IsEnabled = true;
                ParseSelectedButton.IsEnabled = !ShowingBrowserCache&&!parsingFiles;
                SetScopeControlsEnabled(true);
                UpdateOperationNotice();
            }
        }
    }

    private void SetScopeControlsEnabled(bool enabled)
    {
        foreach (var control in new Control[] { ContextScope, ContextDocument, ContextEnabled, OriginalAccess, AgentsScope, AgentsPath, KindFilter, ContextEditor, AgentsEditor })
            control.IsEnabled = enabled;
        UpdateEditingControls();
        UpdateTaskSelection();
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

    private async Task ShowMessageAsync(string title, object content)
    {
        if (Option("--smoke-report") is not null && (!smokeMessages.TryDequeue(out var expected)||expected!=title)) throw new InvalidOperationException(title + ": " + content);
        var dialog = new ContentDialog { Title = title, Content = content is string text?new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 }:content, CloseButtonText = "关闭", XamlRoot = Root.XamlRoot, RequestedTheme = ElementTheme.Light, Style = (Style)Application.Current.Resources["AmifolioDialogStyle"] };
        ApplyDialogTheme(dialog);
        if(Option("--smoke-report") is not null){var showing=dialog.ShowAsync();await Task.Delay(180);dialog.Hide();await showing;}
        else await dialog.ShowAsync();
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
    private sealed record ArtifactItem(ArtifactRecord Record, string Title, string Subtitle, string Badge, string Size,bool IsDirectory=false,string? Path=null,CodexProject? Project=null)
    {
        public string Icon => IsDirectory ? "\uE8B7" : Record.Kind switch { "PDF"=>"\uEA90", "图片"=>"\uEB9F", "代码"=>"\uE943", "表格"=>"\uE9F9", _=>"\uE8A5" };
    }
}
