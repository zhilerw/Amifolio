using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private LearningStore CreativePreferences => new(SourceStore.DataRoot);

    private async void UseSelectedAsExample(object sender, RoutedEventArgs e)
    {
        if (ShowingBrowserCache || operation is not null) return;
        var item = ArtifactList.SelectedItems.OfType<ArtifactItem>().FirstOrDefault(x => x.Record.Id == selectedArtifactId);
        if (item is not { IsDirectory: false, Path: { }, Project: { } project }) return;
        await RunAsync("打开范例设置", async ct =>
        {
            var reference = await SourceStore.ReferenceFileAsync(item.Path, project.Id, ct);
            await EditCreativeAsync(project.Id, reference.Artifact, ct);
        });
    }

    private async void ShowCodexWorks(object sender, RoutedEventArgs e) =>
        await RequestNavigationAsync("works");

    private async Task EditCreativeAsync(string projectId, ArtifactRecord? favorite, CancellationToken ct, string? sourceWorkId = null)
    {
        var learning = CreativePreferences;
        var scope = new ComboBox { Header = "应用范围", ItemsSource = new[] { "当前项目", "全局" }, SelectedIndex = 0 };
        var font = new TextBox { Header = "字体", MaxLength = 100 };
        var color = new TextBox { Header = "主色", PlaceholderText = "#24766D", MaxLength = 7 };
        var style = new TextBox { Header = "风格", MaxLength = 240 };
        var voice = new TextBox { Header = "文字语气", MaxLength = 240 };
        var part = new TextBox { Header = "沿用作品的哪部分", MaxLength = 180 };
        var referenceLabel = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var provide = new CheckBox { Content = "在符合范围的创作任务中提供给 Codex" };
        var remove = new CheckBox { Content = "保存时删除这条偏好" };
        var body = new StackPanel { Spacing = 8, MinWidth = 340 };
        foreach (var item in new UIElement[] { scope, referenceLabel, font, color, style, voice, part, provide, remove })
            body.Children.Add(item);

        (string? Scope, CreativePreference? Preference, long Revision)? loaded = null;
        long request = 0;
        var dialog = new ContentDialog
        {
            Title = favorite is null ? "创作偏好" : "用作范例",
            Content = new ScrollViewer { Content = body }, PrimaryButtonText = "保存",
            SecondaryButtonText = "撤销上次修订", CloseButtonText = "取消", XamlRoot = Root.XamlRoot
        };

        async Task LoadAsync()
        {
            var tag = ++request;
            var selected = scope.SelectedIndex == 1 ? null : projectId;
            loaded = null;
            dialog.IsPrimaryButtonEnabled = dialog.IsSecondaryButtonEnabled = false;
            referenceLabel.Text = "正在读取偏好…";
            var state = await learning.CreativeSettingsAsync(selected, ct);
            var canUndo = state.Preference is not null && await learning.HasCreativeHistoryAsync(selected, ct);
            if (tag != request) return;
            loaded = (selected, state.Preference, state.Revision);
            var current = state.Preference;
            var carried = current?.Style;
            font.Text = carried?.Font ?? "";
            color.Text = carried?.Color ?? "";
            style.Text = carried?.Style ?? "";
            voice.Text = carried?.Voice ?? "";
            part.Text = favorite is not null && favorite.Id != carried?.ReferenceId ? "" : carried?.ReferencePart ?? "";
            provide.IsChecked = current?.State != "disabled";
            remove.IsEnabled = current is not null;
            remove.IsChecked = false;
            // Global defaults can be edited here but never expose a project-only work title.
            var name = selected is null ? null : favorite?.Title ?? carried?.ReferenceName;
            referenceLabel.Text = name is null ? "" : "范例 · " + name;
            part.Visibility = name is null ? Visibility.Collapsed : Visibility.Visible;
            dialog.IsSecondaryButtonEnabled = canUndo;
            dialog.IsPrimaryButtonEnabled = true;
        }

        scope.SelectionChanged += async (_, _) =>
        {
            var tag = request + 1;
            try { await LoadAsync(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or OperationCanceledException or Microsoft.Data.Sqlite.SqliteException)
            { if (tag == request) referenceLabel.Text = "无法载入该范围的偏好。"; }
        };
        await LoadAsync();
        var result = await dialog.ShowAsync();
        ++request;
        var target = scope.SelectedIndex == 1 ? null : projectId;
        if (result == ContentDialogResult.None || loaded is not { } state || state.Scope != target) return;
        var current = state.Preference;
        var expected = state.Revision;
        var carried = current?.Style;
        if (result == ContentDialogResult.Secondary && current is not null)
        {
            await learning.UndoEntryAsync(current.Id, expected, ct);
            StatusText.Text = "已撤销上次修订。";
        }
        else if (result == ContentDialogResult.Primary)
        {
            if (remove.IsChecked == true && current is not null)
            {
                await learning.SetEntryStateAsync(current.Id, "deleted", expected, ct);
                StatusText.Text = "已删除偏好。旧会话可能仍保留先前收到的内容。";
                return;
            }

            var workId = target is null ? null : favorite?.Id ?? carried?.ReferenceId;
            var workName = target is null ? null : favorite?.Title ?? carried?.ReferenceName;
            var draft = (carried ?? new CreativeStyle("", "", "", "")) with
            {
                Font = font.Text,
                Color = color.Text,
                Style = style.Text,
                Voice = voice.Text,
                ReferenceId = workId,
                ReferenceName = workName,
                ReferencePart = target is null ? null : part.Text,
                ReferenceWorkId = target is null ? null : favorite is null ? carried?.ReferenceWorkId : sourceWorkId
            };
            await learning.SaveCreativeAsync(target, draft, provide.IsChecked == true, expected, ct);
            StatusText.Text = "已保存创作偏好。";
        }
    }
}
