using System.Runtime.InteropServices;
using System.Text.Json;
using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private const string StylePreviewHost = "amifolio-preview.local";
    private const string StylePreviewUrl = "https://" + StylePreviewHost + "/index.html";
    private static readonly JsonSerializerOptions PreviewJson = new(JsonSerializerDefaults.Web);
    private static readonly CreativeStyle EmptyCreativeStyle = new("", "", "", "");
    private WebView2? stylePreview;
    private Task? stylePreviewInitialization;
    private string[] installedPreviewFonts = [];
    private bool stylePreviewBridgeReady;
    private bool stylePreviewReady;
    private bool stylePreviewUnavailable;
    private bool stylePreviewStartedWithWindow;
    private bool creativeLoaded;
    private bool creativeUpdatingScope;
    private bool creativeBusy;
    private bool creativeCanUndo;
    private bool creativeLeavePending;
    private long creativeEpoch;
    private long creativeRevision;
    private ScopeChoice? loadedCreativeScope;
    private CreativePreference? loadedCreative;
    private CreativeStyle? creativeDraft;
    private CreativeStyle? creativeGlobalDefaults;

    private async Task LoadCreativePageAsync()
    {
        RefreshCreativeScopes();
        await ReadCreativeScopeAsync();
        if (closing) return;
        stylePreviewInitialization ??= CreateStylePreviewAsync();
        await stylePreviewInitialization;
        if (!closing) UpdateCreativeControls();
    }

    private void RefreshCreativeScopes()
    {
        if (creativeUpdatingScope) return;
        creativeUpdatingScope = true;
        try { SetScopes(CreativeScope, "全局默认"); }
        finally { creativeUpdatingScope = false; }
    }

    private async Task CreateStylePreviewAsync()
    {
        stylePreviewStartedWithWindow = startupStarted && !ready && currentPage == "creative";
        try
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "StylePreview");
            if (!File.Exists(Path.Combine(folder, "index.html")))
                throw new FileNotFoundException("缺少示例网页。");

            installedPreviewFonts = await Task.Run(LocalFontFamilies.Read);
            if (closing) return;

            var view = new WebView2();
            AutomationProperties.SetName(view, "可编辑的风格预览");
            stylePreview = view;
            CreativePreviewFrame.Children.Add(view);
            var environment = await CoreWebView2Environment.CreateWithOptionsAsync(
                "", Path.Combine(dataRoot, "webview-style-preview"), new CoreWebView2EnvironmentOptions());
            if (closing) return;
            await view.EnsureCoreWebView2Async(environment);
            if (closing) return;

            var core = view.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.SetVirtualHostNameToFolderMapping(StylePreviewHost, folder, CoreWebView2HostResourceAccessKind.DenyCors);
            core.NavigationStarting += (_, args) => args.Cancel = args.Uri != StylePreviewUrl;
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, args) => { args.Cancel = true; args.Handled = true; };
            core.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess && !closing) ShowStylePreviewUnavailable();
            };
            core.WebMessageReceived += HandlePreviewMessage;
            core.Navigate(StylePreviewUrl);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (!closing) ShowStylePreviewUnavailable();
            System.Diagnostics.Trace.TraceWarning("Unable to open style preview: {0}", ex.GetType().Name);
        }
    }

    private void ShowStylePreviewUnavailable()
    {
        if (stylePreviewUnavailable) return;
        stylePreviewUnavailable = true;
        if (stylePreviewStartedWithWindow) StartupTiming.Mark("startup.creative.unavailable");
        CloseStylePreview();
        CreativePreviewNotice.Visibility = Visibility.Visible;
        CreativePreviewNotice.Text = "风格预览无法打开。请确认已安装 Microsoft Edge WebView2 Runtime，再重新打开 Amifolio。";
        UpdateCreativeControls();
    }

    private void CloseStylePreview()
    {
        stylePreviewBridgeReady = false;
        stylePreviewReady = false;
        if (stylePreview is not { } view) return;
        stylePreview = null;
        if (view.CoreWebView2 is { } core) core.WebMessageReceived -= HandlePreviewMessage;
        CreativePreviewFrame.Children.Remove(view);
        view.Close();
    }

    private void HandlePreviewMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (closing || args.Source != StylePreviewUrl || args.WebMessageAsJson.Length > 8192) return;
        try
        {
            using var document = JsonDocument.Parse(args.WebMessageAsJson);
            var message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.String) return;

            if (type.GetString() == "ready")
            {
                stylePreviewBridgeReady = true;
                SendCreativePreview();
                return;
            }

            if (type.GetString() == "state-applied" && stylePreviewBridgeReady && !stylePreviewReady && creativeLoaded &&
                message.TryGetProperty("epoch", out var epoch) && epoch.ValueKind == JsonValueKind.Number &&
                epoch.TryGetInt64(out var appliedEpoch) && appliedEpoch == creativeEpoch)
            {
                stylePreviewReady = true;
                stylePreviewUnavailable = false;
                CreativePreviewNotice.Visibility = Visibility.Collapsed;
                UpdateCreativeControls();
                if (stylePreviewStartedWithWindow) StartupTiming.Mark("startup.creative.interactive");
                return;
            }

            if (currentPage == "creative" && !creativeBusy && TryReceiveCreativeDraft(message))
                UpdateCreativeControls();
        }
        catch (JsonException) { }
        catch (COMException) when (!closing) { ShowStylePreviewUnavailable(); }
    }

    private bool TryReceiveCreativeDraft(JsonElement message)
    {
        if (!stylePreviewReady || !creativeLoaded || message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("type", out var type) || !type.ValueEquals("draft") ||
            !message.TryGetProperty("epoch", out var epoch) || epoch.ValueKind != JsonValueKind.Number ||
            !epoch.TryGetInt64(out var revision) || revision != creativeEpoch ||
            !message.TryGetProperty("style", out var payload) ||
            payload.Deserialize<CreativeStyle>(PreviewJson) is not { } draft) return false;

        var saved = loadedCreative?.Style ?? EmptyCreativeStyle;
        creativeDraft = draft with
        {
            Font = draft.Font ?? "",
            Color = draft.Color?.ToUpperInvariant() ?? "",
            Style = draft.Style ?? "",
            Voice = draft.Voice ?? "",
            ReferenceId = saved.ReferenceId,
            ReferenceName = saved.ReferenceName,
            ReferencePart = saved.ReferencePart,
            ReferenceWorkId = saved.ReferenceWorkId
        };
        return true;
    }

    private async Task FetchVisibleCreativeDraftAsync()
    {
        if (!stylePreviewReady || !creativeLoaded || stylePreview?.CoreWebView2 is not { } core) return;
        var snapshot = await core.ExecuteScriptAsync("window.amifolioDraftSnapshot?.()");
        if (closing) return;
        using var document = JsonDocument.Parse(snapshot);
        if (!TryReceiveCreativeDraft(document.RootElement))
            throw new InvalidOperationException("预览还在载入，请稍后重试。");
    }

    private void SendCreativePreview()
    {
        if (!stylePreviewBridgeReady || !creativeLoaded || stylePreview?.CoreWebView2 is not { } core) return;
        var value = creativeDraft ?? loadedCreative?.Style ?? EmptyCreativeStyle;
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "state",
            epoch = ++creativeEpoch,
            acknowledge = !stylePreviewReady,
            style = WithoutReference(value),
            defaults = WithoutReference(creativeGlobalDefaults),
            scopeName = loadedCreativeScope?.Id is null ? null : loadedCreativeScope.Name,
            fonts = installedPreviewFonts
        }, PreviewJson));
    }

    private static CreativeStyle? WithoutReference(CreativeStyle? style) =>
        style is null ? null : style with { ReferenceId = null, ReferenceName = null, ReferencePart = null, ReferenceWorkId = null };

    private bool CreativeHasDraft => creativeDraft is { } draft && draft != (loadedCreative?.Style ?? EmptyCreativeStyle);

    private void UpdateCreativeControls(string? feedback = null)
    {
        CreativeScope.IsEnabled = !creativeBusy;
        CreativeDelivery.IsEnabled = !creativeBusy && creativeLoaded;
        CreativeSave.IsEnabled = !creativeBusy && creativeLoaded && stylePreviewReady && CreativeHasDraft;
        CreativeUndo.IsEnabled = !creativeBusy && creativeLoaded && creativeCanUndo && !CreativeHasDraft;
        CreativeToggle.IsEnabled = !creativeBusy && creativeLoaded && loadedCreative is not null && !CreativeHasDraft;
        CreativeToggle.Content = loadedCreative?.State == "active" ? "暂停提供" : "启用提供";

        var status = feedback ?? (creativeBusy ? "正在读取偏好…" :
            !creativeLoaded ? "偏好尚未读取。" :
            CreativeHasDraft ? "预览已更新 · 尚未保存" :
            loadedCreative?.State == "active" ? "已保存 · 会提供给 Codex" :
            loadedCreative is not null ? "已暂停 · 新会话暂不接收这份偏好" :
            "点选示例元素开始编辑。");
        if (creativeGlobalDefaults is not null && creativeLoaded && !creativeBusy)
            status += "\n未设置的字段沿用已启用的全局默认值。";
        CreativeStatus.Text = status;
    }

    private async Task ReadCreativeScopeAsync()
    {
        loadedCreativeScope = SelectedScope(CreativeScope);
        creativeLoaded = false;
        loadedCreative = null;
        creativeDraft = null;
        ResetCreativeNow();
        await RunCreativeActionAsync(LoadSelectedCreativeAsync);
    }

    private async Task LoadSelectedCreativeAsync()
    {
        var scope = loadedCreativeScope?.Id;
        var learning = CreativePreferences;
        var hasState = learning.HasState;
        var state = hasState ? await learning.CreativeSettingsAsync(scope, backgroundTasks.Token) : (Preference: (CreativePreference?)null, Revision: 0L);
        var global = scope is null || !hasState ? null : await learning.CreativeAsync(null, backgroundTasks.Token);
        var canUndo = state.Revision > 0 &&
            await learning.HasCreativeHistoryAsync(scope, backgroundTasks.Token);
        if (closing) return;
        (loadedCreative, creativeRevision) = state;
        creativeGlobalDefaults = global?.State == "active" ? global.Style : null;
        creativeCanUndo = canUndo;
        creativeDraft = null;
        creativeLoaded = true;
        SendCreativePreview();
        RefreshCreativeNow();
    }

    private async Task RunCreativeActionAsync(Func<Task> action)
    {
        if (creativeBusy) return;
        creativeBusy = true;
        if (stylePreview is { } view) view.IsHitTestVisible = false;
        UpdateCreativeControls();
        string? feedback = null;
        try { await action(); }
        catch (OperationCanceledException) when (closing) { }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            feedback = "暂时无法读写偏好。可重试，或从「更多」重新读取偏好。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or COMException or JsonException)
        {
            feedback = ex.Message;
        }
        finally
        {
            creativeBusy = false;
            if (!closing)
            {
                if (stylePreview is { } activeView) activeView.IsHitTestVisible = true;
                UpdateCreativeControls(feedback);
            }
        }
    }

    private async Task<bool> CanLeaveCreativeAsync()
    {
        if (creativeBusy || creativeLeavePending) return false;
        creativeLeavePending = true;
        try
        {
            try { await FetchVisibleCreativeDraftAsync(); }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or JsonException)
            {
                // The message handler may already hold a usable draft when the preview process closes.
            }
            if (closing || !CreativeHasDraft) return true;
            if (!await ConfirmAsync("舍弃未保存的偏好？", "预览将恢复到上次保存的样子。", "舍弃修改"))
                return false;
            creativeDraft = null;
            SendCreativePreview();
            UpdateCreativeControls();
            return true;
        }
        finally { creativeLeavePending = false; }
    }

    private async void CreativeScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || creativeUpdatingScope || currentPage != "creative") return;
        var choice = SelectedScope(CreativeScope);
        creativeUpdatingScope = true;
        CreativeScope.IsEnabled = false;
        SetCreativeNowActionsEnabled(false);
        try
        {
            if (!await CanLeaveCreativeAsync())
            {
                CreativeScope.SelectedItem = CreativeScope.Items.OfType<ScopeChoice>()
                    .FirstOrDefault(scope => scope.Id == loadedCreativeScope?.Id);
                return;
            }
            CreativeScope.SelectedItem = choice;
        }
        finally
        {
            creativeUpdatingScope = false;
            CreativeScope.IsEnabled = !creativeBusy;
            SetCreativeNowActionsEnabled(true);
        }
        await ReadCreativeScopeAsync();
    }

    private async void SaveCreativePage(object sender, RoutedEventArgs e)
    {
        if (!CreativeSave.IsEnabled) return;
        await RunCreativeActionAsync(async () =>
        {
            await FetchVisibleCreativeDraftAsync();
            if (closing || !CreativeHasDraft || creativeDraft is not { } draft) return;
            var scope = loadedCreativeScope?.Id;
            if (draft.HasValues())
            {
                loadedCreative = await CreativePreferences.SaveCreativeAsync(scope, draft,
                    loadedCreative?.State != "disabled", creativeRevision, backgroundTasks.Token);
                creativeCanUndo |= loadedCreative.Revision != creativeRevision;
                creativeRevision = loadedCreative.Revision;
            }
            else if (loadedCreative is not null)
            {
                creativeRevision = await CreativePreferences.RemoveCreativeAsync(scope, creativeRevision, backgroundTasks.Token);
                loadedCreative = null;
                creativeCanUndo = true;
            }
            creativeDraft = null;
            SendCreativePreview();
            RefreshCreativeNow();
        });
    }

    private async void ToggleCreative(object sender, RoutedEventArgs e)
    {
        if (!CreativeToggle.IsEnabled || loadedCreative is not { } saved) return;
        await RunCreativeActionAsync(async () =>
        {
            await CreativePreferences.SetEntryStateAsync(saved.Id, saved.State == "active" ? "disabled" : "active",
                creativeRevision, backgroundTasks.Token);
            await LoadSelectedCreativeAsync();
        });
    }

    private async void UndoCreativePage(object sender, RoutedEventArgs e)
    {
        if (!CreativeUndo.IsEnabled) return;
        await RunCreativeActionAsync(async () =>
        {
            if (loadedCreative is { } saved)
                await CreativePreferences.UndoEntryAsync(saved.Id, creativeRevision, backgroundTasks.Token);
            else
                await CreativePreferences.UndoCreativeRemovalAsync(loadedCreativeScope?.Id, creativeRevision, backgroundTasks.Token);
            await LoadSelectedCreativeAsync();
        });
    }

    private async void ReloadCreativePage(object sender, RoutedEventArgs e)
    {
        if (await CanLeaveCreativeAsync()) await LoadCreativePageAsync();
    }

    private void CreativeFooterSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 380;
        Grid.SetRow(CreativeDelivery, stacked ? 1 : 0);
        Grid.SetColumn(CreativeDelivery, stacked ? 0 : 1);
    }

    private async void ShowNextCreativeDelivery(object sender, RoutedEventArgs e)
    {
        if (!CreativeDelivery.IsEnabled || currentPage != "creative") return;
        var scope = loadedCreativeScope;
        await RunAsync("读取已保存的偏好", token => RunCreativeActionAsync(async () =>
        {
            var allowedScope = scope?.Id is null || SourceStore.CodexProjects.Any(project => project.Id == scope.Id);
            var text = allowedScope ? await CreativePreferences.PreviewCreativeDeliveryAsync(scope?.Id, token) : null;
            if (closing || currentPage != "creative") return;
            var guidance = scope?.Id is null
                ? "下方是全局偏好。具体项目如有启用的偏好，也会一并提供。"
                : "下方是这个项目的新会话首次提交相关创作任务时可收到的偏好。";
            var body = new StackPanel { Spacing = 10, MaxWidth = 500 };
            var caption = (Style)Application.Current.Resources["Caption"];
            body.Children.Add(new TextBlock { Text = allowedScope ? guidance : "当前项目目录不可用，插件无法匹配这个项目。", Style = caption });
            if (allowedScope)
            {
                body.Children.Add(new TextBlock { Text = "内容按已保存的设置生成。同一会话已收到的修订通常不会重复提供。", Style = caption });
                body.Children.Add(new TextBox
                {
                    Text = text ?? "当前范围没有启用的创作偏好。",
                    IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
                    MinHeight = 84, MaxHeight = 300, VerticalContentAlignment = VerticalAlignment.Top
                });
            }
            StatusText.Text = "就绪";
            await ShowMessageAsync("下次提供内容", body);
        }));
    }

    private async void OpenCreativeLearning(object sender, RoutedEventArgs e) =>
        await OpenLearningPageAsync(false, loadedCreativeScope?.Id, true, "creative");

    private async void OpenCreativeLearningLogs(object sender, RoutedEventArgs e) =>
        await ShowCreativeDeliveryLogAsync(loadedCreativeScope?.Id);


}

internal static class LocalFontFamilies
{
    // LOGFONTW begins with five LONGs and eight BYTEs, followed by its 32-WCHAR family name.
    private const int FaceOffset = 28;
    private const int FaceLength = 32;

    internal static string[] Read()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return [];
        try
        {
            var query = new byte[FaceOffset + FaceLength * sizeof(char)];
            query[23] = 1; // DEFAULT_CHARSET asks GDI to enumerate every family.
            EnumFontFamiliesExW(dc, query, (font, _, _, _) =>
            {
                var name = Marshal.PtrToStringUni(font + FaceOffset, FaceLength)?.TrimEnd('\0');
                if (!string.IsNullOrWhiteSpace(name) && !name.StartsWith('@')) names.Add(name);
                return 1;
            }, IntPtr.Zero, 0);
        }
        finally { ReleaseDC(IntPtr.Zero, dc); }
        return names.Order(StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private delegate int FontCallback(IntPtr font, IntPtr metrics, uint type, IntPtr state);

    [DllImport("gdi32.dll", ExactSpelling = true)]
    private static extern int EnumFontFamiliesExW(IntPtr dc, byte[] query, FontCallback callback, IntPtr state, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);
}
