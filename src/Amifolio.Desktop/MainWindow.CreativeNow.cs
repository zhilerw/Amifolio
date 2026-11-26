using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private CancellationTokenSource? creativeNowLoad;
    private bool CanUseCreativeNow => currentPage == "creative" && !creativeUpdatingScope && !creativeBusy &&
        SelectedScope(CreativeScope)?.Id == loadedCreativeScope?.Id;

    private void SetCreativeNowActionsEnabled(bool enabled)
    {
        CreativeNow.IsHitTestVisible = enabled;
        foreach (var button in CreativeNowRows.Children.OfType<Grid>().SelectMany(row => row.Children.OfType<HyperlinkButton>()))
            button.IsEnabled = enabled;
        if (CreativeExample.Children.FirstOrDefault() is StackPanel body && body.Children.LastOrDefault() is StackPanel actions)
            foreach (var button in actions.Children.OfType<HyperlinkButton>()) button.IsEnabled = enabled;
    }

    private void CreativePageScrollSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (CreativePreviewFrame is not null)
            CreativePreviewFrame.Height = Math.Clamp(e.NewSize.Height - 245, 300, 560);
    }

    private void ResetCreativeNow()
    {
        creativeNowLoad?.Cancel();
        CreativeNowHeading.Text = loadedCreativeScope?.Id is null ? "全局偏好" : "这个项目现在";
        CreativeNowRows.Children.Clear();
        CreativeExample.Children.Clear();
    }

    private void RefreshCreativeNow()
    {
        if (closing || currentPage != "creative" || !creativeLoaded) return;
        ResetCreativeNow();
        var request = CancellationTokenSource.CreateLinkedTokenSource(backgroundTasks.Token);
        creativeNowLoad = request;
        var scope = loadedCreativeScope;
        var saved = loadedCreative;
        _ = LoadCreativeNowAsync(request, scope, saved, creativeGlobalDefaults);
    }

    private async Task LoadCreativeNowAsync(CancellationTokenSource request, ScopeChoice? scope,
        CreativePreference? saved, CreativeStyle? inherited)
    {
        var ct = request.Token;
        var projectId = scope?.Id;
        bool IsCurrent() => !ct.IsCancellationRequested && !closing && currentPage == "creative" &&
            ReferenceEquals(creativeNowLoad, request) && loadedCreativeScope?.Id == projectId;
        try
        {
            if (projectId is not null && !SourceStore.CodexProjects.Any(project => project.Id == projectId))
            {
                AddCreativeNowRow("项目目录不可用", "可以切换到其他项目，或刷新已发现的项目。");
                if (saved?.Style is { ReferenceId: not null } stale)
                    ShowCreativeExample(scope!, saved, stale, null, null, null);
                return;
            }

            var learningTask = context.Learning.ReadPreferenceHighlightsAsync(projectId, ct);
            var worksTask = projectId is null ? Task.FromResult<IReadOnlyList<CodexWorkCard>>([]) :
                SourceStore.ListCodexWorkCardsAsync(projectId, "", 1, ct);
            await Task.WhenAll(learningTask, worksTask);
            if (!IsCurrent()) return;

            var learning = await learningTask;
            var latest = (await worksTask).FirstOrDefault();
            var creative = saved is { State: "active" } ? saved.Style : null;
            var fields = CreativeHighlights(creative, inherited, projectId is null).Take(4).ToArray();
            AddCreativeNowRow("已生效偏好", fields.Length == 0 && learning.Active.Count == 0 ? "还没有启用的偏好。" :
                string.Join("\n", fields.Concat(learning.Active.Select(entry =>
                    $"{ShortCreativeText(entry.Text)} · {(learning.Shared ? "允许共享" : "仅本地")}"))),
                learning.Active.FirstOrDefault() is { } active ? "查看依据" : null,
                learning.Active.FirstOrDefault() is { } item ? () => OpenLearningPageAsync(false, projectId, true, "creative", item.Id) : null);

            if (projectId is not null)
            {
                if (latest is not null)
                    AddCreativeNowRow("最近作品", $"{latest.Work.Title} · {latest.SessionTitle ?? latest.Work.SourceLabel}",
                        "查看作品", () => OpenCreativeWorkAsync(projectId, latest.Work.Id));
                else
                    AddCreativeNowRow("最近作品", "这个项目还没有同步作品。");
            }

            if (learning.Candidate is { } candidate)
                AddCreativeNowRow("待确认", ShortCreativeText(candidate.Text), "查看建议",
                    () => OpenLearningPageAsync(false, projectId, true, "creative", candidate.Id));
            else AddCreativeNowRow("待确认", "目前没有待确认的建议。");

            if (latest is null)
            {
                var configured = await IsCreativePluginConfiguredAsync(ct);
                if (!IsCurrent()) return;
                if (!configured)
                    AddCreativeNowRow("下一步", "检查 Codex 插件接入，再同步你的作品。", "检查插件",
                        () => RequestNavigationAsync("connections"));
                else if (projectId is null)
                    AddCreativeNowRow("下一步", "选择一个项目查看它的近期作品和偏好。", "查看作品",
                        () => RequestNavigationAsync("works"));
                else
                {
                    var sync = await SourceStore.WorkSyncAsync(projectId, ct);
                    if (!IsCurrent()) return;
                    var next = sync switch
                    {
                        { Enabled: false } => "在作品页开启本项目同步，之后可以从上传件或交付物中选择范例。",
                        { State: "unsupported" } => "最近一轮未能核对作品来源。可在作品页查看同步状态。",
                        _ => "同步已开启。可在 Codex 完成一轮上传或文件交付，再回作品页选择范例。"
                    };
                    AddCreativeNowRow("下一步", next, "打开作品", () => OpenCreativeWorkAsync(projectId, null));
                }
            }

            if (projectId is not null && saved?.Style is { ReferenceId: { } referenceId } example)
            {
                var card = example.ReferenceWorkId is { } workId
                    ? latest?.Work.Id == workId ? latest : await SourceStore.GetCodexWorkCardAsync(projectId, workId, ct)
                    : latest?.Work.ReferenceId == referenceId ? latest :
                        await SourceStore.GetCodexWorkCardForReferenceAsync(projectId, referenceId, ct);
                if (card?.Work.ReferenceId != referenceId) card = null;
                var earlier = card is null ? await SourceStore.ReadCreativeExampleMetadataAsync(projectId, referenceId, ct) : null;
                var image = card?.HasImage == true ? await ReadWorksHomeImageAsync(card, 300, ct) :
                    earlier is { Kind: "图片" } ? await ReadBoundedCardImageAsync(
                        () => SourceStore.OpenCreativeExampleImageAsync(projectId, referenceId, earlier.CreatedAt, ct), 300, ct) : null;
                if (IsCurrent()) ShowCreativeExample(scope!, saved, example, card, earlier, image);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            if (!IsCurrent()) return;
            if (saved?.Style is { ReferenceId: not null } example && scope?.Id is not null && CreativeExample.Children.Count == 0)
                ShowCreativeExample(scope, saved, example, null, null, null);
            AddCreativeNowRow("部分内容暂不可用", "重新打开偏好页可再次读取。");
        }
        finally
        {
            if (ReferenceEquals(creativeNowLoad, request)) creativeNowLoad = null;
            request.Dispose();
        }
    }

    private static IEnumerable<string> CreativeHighlights(CreativeStyle? local, CreativeStyle? global, bool globalScope)
    {
        (string Label, string? Local, string? Global)[] fields =
        [
            ("字体", local?.Font, global?.Font), ("主色", local?.Color, global?.Color),
            ("风格", local?.Style, global?.Style), ("文字语气", local?.Voice, global?.Voice),
            ("背景色", local?.Background, global?.Background),
            ("间距", Pixels(local?.Spacing), Pixels(global?.Spacing)),
            ("圆角", Pixels(local?.Radius), Pixels(global?.Radius)),
            ("作品范例", local?.ReferenceName, null),
            ("样文", local?.SampleText, global?.SampleText)
        ];
        foreach (var (label, authored, fallback) in fields)
        {
            if (!string.IsNullOrEmpty(authored)) yield return $"{label} {ShortCreativeText(authored)} · {(globalScope ? "全局默认" : "本项目")}";
            else if (!string.IsNullOrEmpty(fallback)) yield return $"{label} {ShortCreativeText(fallback)} · 继承全局";
        }
        static string? Pixels(int? value) => value is { } px ? $"{px} px" : null;
    }

    private void AddCreativeNowRow(string title, string value, string? actionLabel = null, Func<Task>? action = null)
    {
        var row = new Grid { ColumnSpacing = 12, Padding = new Thickness(12, 7, 12, 7) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Children.Add(CreativeNowText(value));
        row.Children.Add(text);
        if (actionLabel is not null && action is not null)
        {
            var button = new HyperlinkButton { Content = actionLabel, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(button, 1);
            button.Click += async (_, _) => { if (CanUseCreativeNow) await action(); };
            row.Children.Add(button);
        }
        CreativeNowRows.Children.Add(row);
    }

    private void ShowCreativeExample(ScopeChoice scope, CreativePreference saved, CreativeStyle style,
        CodexWorkCard? card, ArtifactRecord? earlier, BitmapImage? image)
    {
        var work = card?.Work;
        var availability = work?.Availability ?? earlier?.ExtractionStatus;
        var missing = availability is null or "仅来源会话" or "引用不可用" or "源文件缺失" or "源文件不可读";
        var changed = availability?.StartsWith("源文件已变化", StringComparison.Ordinal) == true;
        var title = style.ReferenceName ?? "已保存的作品";
        var excerpt = card?.Excerpt ?? earlier?.Preview ?? "";
        var body = new StackPanel { Spacing = 6, Padding = new Thickness(12), Background = (Brush)Application.Current.Resources["Paper"] };
        body.Children.Add(new TextBlock { Text = "作品范例", FontSize = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        body.Children.Add(CreativeNowText($"{title} · {scope.Name}{(saved.State == "disabled" ? " · 提供已暂停" : "")}"));
        body.Children.Add(CreativeNowText($"沿用 {style.ReferencePart}"));
        if (style.ReferenceWorkId is not null && card?.SessionTitle is { } sessionTitle)
            body.Children.Add(CreativeNowText($"来源 · {sessionTitle}"));
        if (image is not null)
        {
            var preview = new Image { Source = image, MaxHeight = 106, MaxWidth = 250, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
            AutomationProperties.SetName(preview, title + "缩略图");
            body.Children.Add(preview);
        }
        else if (!missing && !changed && excerpt.Length > 0)
            body.Children.Add(CreativeNowText(ShortCreativeText(excerpt, 260)));
        else if (missing || changed)
            body.Children.Add(CreativeNowText(missing ? "范例原件不可用。仍可核对来源或移除关联。" : "原件已有变化。可在作品页预览当前文件。"));
        else body.Children.Add(CreativeNowText("这份作品暂无可显示的片段。"));

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var source = new HyperlinkButton { Content = "核对来源" };
        source.Click += async (_, _) =>
        {
            if (!CanUseCreativeNow || loadedCreativeScope?.Id != scope.Id) return;
            await RunAsync("查看范例来源", async _ =>
            {
                var origin = work is null ? "保存时登记的引用 " + style.ReferenceId :
                    $"{card!.SessionTitle ?? work.SourceLabel}\n{(style.ReferenceWorkId is null ? "与文件关联的会话" : "来源会话")} {work.SessionId}\n回合 {work.TurnId}";
                StatusText.Text = "就绪";
                await ShowMessageAsync("范例来源", $"{title}\n项目 {scope.Name}\n{origin}");
            });
        };
        var remove = new HyperlinkButton { Content = "移除关联" };
        remove.Click += async (_, _) => await UnlinkCreativeExampleAsync(scope.Id!, saved.Revision);
        actions.Children.Add(source);
        actions.Children.Add(remove);
        body.Children.Add(actions);
        CreativeExample.Children.Add(body);
    }

    private async Task UnlinkCreativeExampleAsync(string projectId, long revision)
    {
        if (!CanUseCreativeNow || loadedCreativeScope?.Id != projectId || loadedCreative?.Revision != revision) return;
        await RunCreativeActionAsync(async () =>
        {
            await FetchVisibleCreativeDraftAsync();
            if (CreativeHasDraft) throw new InvalidOperationException("请先保存样张修改，再移除范例关联。");
            if (!await ConfirmAsync("移除这份范例的关联？", "作品和已有的其他偏好会保留。", "移除关联")) return;
            if (closing || creativeUpdatingScope || SelectedScope(CreativeScope)?.Id != projectId) return;
            await CreativePreferences.RemoveCreativeReferenceAsync(projectId, revision, backgroundTasks.Token);
            await LoadSelectedCreativeAsync();
        });
    }

    private static TextBlock CreativeNowText(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Caption"]
    };

    private static string ShortCreativeText(string text, int limit = 90)
    {
        text = text.Trim().ReplaceLineEndings(" ");
        if (text.Length <= limit) return text;
        var end = char.IsHighSurrogate(text[limit]) ? limit : char.IsLowSurrogate(text[limit]) ? limit - 1 : limit;
        return text[..end] + "…";
    }
}
