using Amifolio.Context;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private sealed record NativeMemoryChoice(string Label, NativeMemorySource Source);

    private bool bindingNativeMemory;
    private long nativeMemoryRequest;
    private NativeMemorySnapshot? nativeMemorySnapshot;
    private string? nativeMemoryProjectPath;

    private async void BrowseNativeMemory(object sender, RoutedEventArgs e)
    {
        if (await RequestNavigationAsync("native-memory"))
            NativeMemoryScope.Focus(FocusState.Programmatic);
    }

    private async Task LoadNativeMemoryPageAsync()
    {
        bindingNativeMemory = true;
        try
        {
            SetScopes(NativeMemoryScope, "全部记忆");
            if (loadedCreativeScope?.Id is { } projectId)
                NativeMemoryScope.SelectedItem = NativeMemoryScope.Items.OfType<ScopeChoice>()
                    .FirstOrDefault(item => item.Id == projectId) ?? NativeMemoryScope.Items[0];
        }
        finally { bindingNativeMemory = false; }

        await ShowNativeMemorySnapshotAsync(backgroundTasks.Token);
    }

    private async void NativeMemoryScopeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ready || bindingNativeMemory || currentPage != "native-memory" || operation is not null) return;
        await RunAsync("读取原生记忆", ShowNativeMemorySnapshotAsync);
    }

    private async void RefreshNativeMemory(object sender, RoutedEventArgs e) =>
        await RunAsync("刷新原生记忆", ShowNativeMemorySnapshotAsync);

    private async Task ShowNativeMemorySnapshotAsync(CancellationToken cancellationToken)
    {
        var request = ++nativeMemoryRequest;
        var scope = SelectedScope(NativeMemoryScope);
        var project = projects.FirstOrDefault(item => item.Id == scope?.Id);
        NativeMemoryScope.IsEnabled = NativeMemoryFiles.IsEnabled = NativeMemoryRefresh.IsEnabled = false;
        NativeMemoryStatus.Text = "正在读取原生记忆…";
        var selectedPath = (NativeMemoryFiles.SelectedItem as NativeMemoryChoice)?.Source.Path;
        bindingNativeMemory = true;
        try
        {
            NativeMemoryFiles.ItemsSource = null;
            NativeMemoryText.Text = NativeMemoryFileInfo.Text = "";
            if (scope?.Id is not null && string.IsNullOrWhiteSpace(project?.RootPath))
            {
                NativeMemoryStatus.Text = "这个项目的目录目前不可用。可以选择其他范围。";
                return;
            }

            var snapshot = await new NativeMemoryService(integration.CodexHome)
                .ReadAsync(project?.RootPath, cancellationToken);
            if (closing || request != nativeMemoryRequest || currentPage != "native-memory") return;

            var stores = new[] { snapshot.V1, snapshot.V2 };
            var visibleSources = VisibleMemorySources(snapshot, project is not null).ToArray();
            var choices = visibleSources.Select(item => new NativeMemoryChoice(
                $"{VersionLabel(item.Store.Version)} · {SourceLabel(item.Source.Kind)} · {Path.GetFileName(item.Source.Path)}",
                item.Source)).ToArray();
            var previous = nativeMemoryProjectPath == project?.RootPath ? nativeMemorySnapshot : null;
            nativeMemorySnapshot = snapshot;
            nativeMemoryProjectPath = project?.RootPath;

            var status = string.Join(" · ", stores.Select(item =>
                $"{VersionLabel(item.Version)} {StoreLabel(item.Status)}"));
            if (project is not null)
                status += choices.Length == 0 ? "\n已读取的来源中暂无目录匹配的摘要。" : "\n仅显示记录目录与当前项目一致的来源摘要。";
            else if (choices.Length == 0)
                status += "\nCodex 生成记忆后，可以在这里查看文件。";
            if (snapshot.SelectedVersion is { } version)
                status += $"\n生效版本 {VersionLabel(version)}。{snapshot.SelectionEvidence}";
            else
                status += "\n当前可查看本机文件。宿主会话使用的版本与整理进度需由 Codex 确认。";
            if (stores.Any(item => item.IsLimited))
                status += "\n仅列出读取上限内的来源。";
            if (previous is not null)
            {
                var oldByPath = VisibleMemorySources(previous, project is not null)
                    .ToLookup(item => item.Source.Path, StringComparer.OrdinalIgnoreCase);
                var newByPath = visibleSources.ToLookup(item => item.Source.Path, StringComparer.OrdinalIgnoreCase);
                var changes = visibleSources.Count(item =>
                    !oldByPath[item.Source.Path].Any(old => old.Source == item.Source));
                changes += oldByPath.Count(group => !newByPath.Contains(group.Key));
                if (changes > 0) status += $"\n本次刷新观察到 {changes} 项文件变化。";
            }
            NativeMemoryStatus.Text = status;

            NativeMemoryFiles.ItemsSource = choices;
            NativeMemoryFiles.SelectedItem = choices.FirstOrDefault(item => item.Source.Path == selectedPath) ??
                choices.FirstOrDefault(item => item.Source.Status == NativeMemoryReadStatus.Available) ??
                choices.FirstOrDefault();
            DisplayNativeMemoryFile();
            StatusText.Text = "就绪";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!closing && request == nativeMemoryRequest) NativeMemoryStatus.Text = "读取已取消，可再次刷新。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            if (!closing && request == nativeMemoryRequest) NativeMemoryStatus.Text = "暂时无法读取 Codex 记忆，可再次刷新。";
        }
        finally
        {
            if (!closing && request == nativeMemoryRequest)
            {
                bindingNativeMemory = false;
                NativeMemoryScope.IsEnabled = NativeMemoryRefresh.IsEnabled = true;
                NativeMemoryFiles.IsEnabled = NativeMemoryFiles.Items.Count > 0;
            }
        }
    }

    private static IEnumerable<(NativeMemoryStoreSnapshot Store, NativeMemorySource Source)> VisibleMemorySources(
        NativeMemorySnapshot snapshot, bool projectOnly)
    {
        return new[] { snapshot.V1, snapshot.V2 }
            .SelectMany(store => store.Sources.Select(source => (Store: store, Source: source)))
            .Where(item => !projectOnly || item.Source.ProjectMatch == NativeMemoryProjectMatch.Matched)
            .Where(item => item.Source.Status != NativeMemoryReadStatus.Missing ||
                item.Source.Kind == NativeMemorySourceKind.RolloutSummary);
    }

    private void NativeMemoryFileChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!bindingNativeMemory) DisplayNativeMemoryFile();
    }

    private void DisplayNativeMemoryFile()
    {
        var source = (NativeMemoryFiles.SelectedItem as NativeMemoryChoice)?.Source;
        NativeMemoryText.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
        if (source is null)
        {
            NativeMemoryText.Text = NativeMemoryFileInfo.Text = "";
            return;
        }

        var info = source.Path;
        if (source.LastWriteTimeUtc is { } time)
            info += $"\n文件最近修改 {time.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        if (source.Kind == NativeMemorySourceKind.PendingNote)
            info += "\n这是提交给 Codex 的备注，是否整理进摘要由宿主决定。";
        if (source.IsTruncated)
            info += "\n仅显示文件开头。";
        NativeMemoryFileInfo.Text = info;
        NativeMemoryText.Text = source.Status switch
        {
            NativeMemoryReadStatus.Available => source.Content ?? "",
            NativeMemoryReadStatus.Missing => "此来源文件已找不到。",
            NativeMemoryReadStatus.Untrusted => "此来源路径未通过核对。",
            _ => "目前无法读取此来源。"
        };
    }

    private static string VersionLabel(NativeMemoryVersion value) => value == NativeMemoryVersion.V1 ? "v1" : "v2";

    private static string StoreLabel(NativeMemoryReadStatus status) => status switch
    {
        NativeMemoryReadStatus.Available => "有本机记录",
        NativeMemoryReadStatus.Missing => "暂无记录",
        NativeMemoryReadStatus.Untrusted => "目录未通过核对",
        _ => "当前不可读"
    };

    private static string SourceLabel(NativeMemorySourceKind kind) => kind switch
    {
        NativeMemorySourceKind.Overview => "记忆摘要",
        NativeMemorySourceKind.Memory => "记忆正文",
        NativeMemorySourceKind.RolloutSummary => "来源摘要",
        _ => "更正备注"
    };
}
