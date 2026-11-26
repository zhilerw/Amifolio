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
    private bool stylePreviewReady;
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
        UpdateCreativeControls();
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
        CloseStylePreview();
        CreativePreviewNotice.Visibility = Visibility.Visible;
        CreativePreviewNotice.Text = "风格预览无法打开。请确认已安装 Microsoft Edge WebView2 Runtime，再重新打开 Amifolio。";
        UpdateCreativeControls();
    }

    private void CloseStylePreview()
    {
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
                stylePreviewReady = true;
                CreativePreviewNotice.Visibility = Visibility.Collapsed;
                SendCreativePreview();
                UpdateCreativeControls();
                return;
            }

            if (currentPage == "creative" && !creativeBusy && TryReceiveCreativeDraft(message))
                UpdateCreativeControls();
        }
        catch (JsonException) { }
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
            ReferencePart = saved.ReferencePart
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
        if (!stylePreviewReady || !creativeLoaded || stylePreview?.CoreWebView2 is not { } core) return;
        var value = creativeDraft ?? loadedCreative?.Style ?? EmptyCreativeStyle;
        core.PostWebMessageAsJson(JsonSerializer.Serialize(new
        {
            type = "state",
            epoch = ++creativeEpoch,
            style = WithoutReference(value),
            defaults = WithoutReference(creativeGlobalDefaults),
            fonts = installedPreviewFonts
        }, PreviewJson));
    }

    private static CreativeStyle? WithoutReference(CreativeStyle? style) =>
        style is null ? null : style with { ReferenceId = null, ReferenceName = null, ReferencePart = null };

    private bool CreativeHasDraft => creativeDraft is { } draft && draft != (loadedCreative?.Style ?? EmptyCreativeStyle);

    private void UpdateCreativeControls(string? feedback = null)
    {
        CreativeScope.IsEnabled = !creativeBusy;
        CreativeSave.IsEnabled = !creativeBusy && creativeLoaded && stylePreviewReady && CreativeHasDraft;
        CreativeUndo.IsEnabled = !creativeBusy && creativeLoaded && creativeCanUndo && !CreativeHasDraft;
        CreativeToggle.IsEnabled = !creativeBusy && creativeLoaded && loadedCreative is not null && !CreativeHasDraft;
        CreativeToggle.Content = loadedCreative?.State == "active" ? "暂停" : "启用";

        var status = feedback ?? (creativeBusy ? "正在读取偏好…" :
            !creativeLoaded ? "偏好尚未读取。" :
            CreativeHasDraft ? "预览已更新 · 尚未保存" :
            loadedCreative?.State == "active" ? "已保存 · 会提供给 Codex" :
            loadedCreative is not null ? "已暂停 · 新会话暂不接收这份偏好" :
            "点选示例元素开始编辑。");
        if (loadedCreative?.Style is { ReferenceName: { Length: > 0 } name, ReferencePart: { Length: > 0 } part })
            status += $"\n范例 · {name} · 沿用 {part}";
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
        await RunCreativeActionAsync(LoadSelectedCreativeAsync);
    }

    private async Task LoadSelectedCreativeAsync()
    {
        var scope = loadedCreativeScope?.Id;
        var learning = CreativePreferences;
        var state = await learning.CreativeSettingsAsync(scope, backgroundTasks.Token);
        var global = scope is null ? null : await learning.CreativeAsync(null, backgroundTasks.Token);
        var canUndo = state.Preference is not null &&
            await learning.HasCreativeHistoryAsync(scope, backgroundTasks.Token);
        if (closing) return;
        (loadedCreative, creativeRevision) = state;
        creativeGlobalDefaults = global?.State == "active" ? global.Style : null;
        creativeCanUndo = canUndo;
        creativeDraft = null;
        creativeLoaded = true;
        SendCreativePreview();
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
            loadedCreative = await CreativePreferences.SaveCreativeAsync(loadedCreativeScope?.Id, draft,
                loadedCreative?.State != "disabled", creativeRevision, backgroundTasks.Token);
            creativeCanUndo |= loadedCreative.Revision != creativeRevision;
            creativeRevision = loadedCreative.Revision;
            creativeDraft = null;
            SendCreativePreview();
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
        if (!CreativeUndo.IsEnabled || loadedCreative is not { } saved) return;
        await RunCreativeActionAsync(async () =>
        {
            await CreativePreferences.UndoEntryAsync(saved.Id, creativeRevision, backgroundTasks.Token);
            await LoadSelectedCreativeAsync();
        });
    }

    private async void ReloadCreativePage(object sender, RoutedEventArgs e)
    {
        if (await CanLeaveCreativeAsync()) await LoadCreativePageAsync();
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
