using System.Text.Json;
using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private const string SmokeFont = "Segoe UI";
    private const string SmokeColor = "#20685F";
    private const string SmokeSample = "给今天的风留一点空白。\n桌面与 Codex 共用这段样文。";
    private const string SmokeMissingFont = "Amifolio Smoke Uninstalled Font";

    private async Task<object> RunCreativePreviewSmokeAsync(string reportPath, bool reopen)
    {
        var checks = new List<string>();
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("M3 native regression: " + name);
            checks.Add(name);
        }

        async Task WaitAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            while (!condition() && !timeout.IsCancellationRequested) await Task.Delay(35);
        }

        var fixture = Path.Combine(Path.GetDirectoryName(dataRoot)!, "m3-fixture");
        var studio = Path.Combine(fixture, "studio");
        var other = Path.Combine(fixture, "other");
        var cover = Path.Combine(studio, "cover.md");
        var image = Path.Combine(studio, "cover.png");
        Directory.CreateDirectory(studio);
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(integration.CodexHome);
        if (!reopen)
        {
            await File.WriteAllTextAsync(cover, "# 交付物\n这是风格样张的隔离测试作品。");
            await File.WriteAllBytesAsync(image, Convert.FromBase64String(
                "iVBORw0KGgoAAAANSUhEUgAAABAAAAAICAIAAAB/FOjAAAAAGElEQVR4nGOQLowmiLZdWQhHDKMaaKEBAIn0nAHZYGEmAAAAAElFTkSuQmCC"));
            await File.WriteAllTextAsync(Path.Combine(integration.CodexHome, ".codex-global-state.json"),
                JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["local-projects"] = new Dictionary<string, object>
                    {
                        ["studio"] = new { name = "示例项目", rootPaths = new[] { studio } },
                        ["other"] = new { name = "另一项目", rootPaths = new[] { other } }
                    }
                }));
        }
        await SourceStore.RefreshCodexProjectsAsync();
        catalogVerified = true;
        await RefreshProjectsAsync();
        var project = SourceStore.CodexProjects.Single(item => item.RootPath == studio);
        var second = SourceStore.CodexProjects.Single(item => item.RootPath == other);

        Check(currentPage == "creative" && CreativePage.Visibility == Visibility.Visible, "style sample is the first page");
        if (!reopen)
        {
            await WaitAsync(() => stylePreviewReady);
            if (stylePreview?.CoreWebView2 is not { } firstView || !stylePreviewReady)
                throw new InvalidOperationException("The startup artboard did not load.");
            await CaptureStylePreviewAsync(firstView, reportPath + ".first-artboard.png");
            await SourceStore.SetWorkSyncAsync(project.Id, true);
            var workSession = Guid.NewGuid().ToString("D");
            const string sessionTitle = "封面排版练习";
            await SourceStore.RecordCodexWorksAsync(project, workSession, "smoke-turn",
                [new CodexWorkSource("delivery", cover), new CodexWorkSource("upload", image)]);
            await File.WriteAllLinesAsync(Path.Combine(integration.CodexHome, "session_index.jsonl"),
                [JsonSerializer.Serialize(new { id = workSession, thread_name = sessionTitle, updated_at = DateTimeOffset.UtcNow })]);
            await CreativePreferences.SaveCreativeAsync(null, new("", "#4F7058", "", ""), true, 0);
            await CreativePreferences.SaveCreativeAsync(second.Id, new(SmokeMissingFont, "#54704D", "", ""), true, 0);
            var documentId = (await SourceStore.ListCodexWorkCardsAsync(project.Id, "cover.md", 1, backgroundTasks.Token))
                .Single().Work.Id;
            var opened = await OpenCreativeWorkAsync(project.Id, documentId);
            Check(opened && (worksHomeScope.SelectedItem as WorksHomeProject)?.Id == project.Id &&
                worksHomeList.Items.Count == 2 && worksHomeList.SelectedItem is GridViewItem { Tag: WorksHomeCardEntry selected } &&
                selected.Card.Work.Id == documentId, "creative shortcut opens scoped work cards and selects the requested work");
            var workRows = worksHomeList.Items.OfType<GridViewItem>().ToArray();
            var textRow = workRows.Single(row => ((WorksHomeCardEntry)row.Tag).Card.Work.Title == "cover.md");
            var imageRow = workRows.Single(row => ((WorksHomeCardEntry)row.Tag).Card.Work.Title == "cover.png");
            var documentName = AutomationProperties.GetName(textRow);
            var keepsSourceDetail = expandedWork?.Children.OfType<StackPanel>().LastOrDefault()?.Children.OfType<TextBlock>()
                .Any(text => text.Text.Contains(workSession, StringComparison.Ordinal)) == true;
            var imageFrame = (Border)((StackPanel)imageRow.Content).Children[0];
            await WaitAsync(() => imageFrame.Child is Image);
            Check(imageFrame.Child is Image, "scoped image work decodes a cover before selection");
            worksHomeList.SelectedItem = textRow;
            var document = ((WorksHomeCardEntry)textRow.Tag).Card;
            Check(expandedWork is not null && document is { Excerpt.Length: 0, Work.Availability: "未解析" } &&
                documentName.Contains(sessionTitle, StringComparison.Ordinal) &&
                !documentName.Contains(workSession, StringComparison.Ordinal) && keepsSourceDetail,
                "freshly synced work shows the session title on the card and keeps its UUID in details");
            Root.UpdateLayout();
            var previewButton = SmokeVisuals(expandedWork!).OfType<Button>().Single(button => Equals(button.Content, "预览"));
            smokeMessages.Enqueue(document.Work.Title);
            ((IInvokeProvider)new ButtonAutomationPeer(previewButton).GetPattern(PatternInterface.Invoke)).Invoke();
            var previewContainsText = false;
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(12)))
            {
                while (!deadline.IsCancellationRequested)
                {
                    var previewDialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot)
                        .Where(popup => popup.IsOpen && popup.Child is not null)
                        .SelectMany(popup => SmokeVisuals(popup.Child)).OfType<ContentDialog>()
                        .FirstOrDefault(dialog => Equals(dialog.Title, document.Work.Title));
                    if (previewDialog?.Content is ScrollViewer { Content: StackPanel previewPanel })
                    {
                        previewContainsText = previewPanel.Children.OfType<TextBlock>().Any(text => text.Text.Contains("隔离测试作品"));
                        break;
                    }
                    if (operation is null && smokeMessages.Count == 0) break;
                    await Task.Delay(10);
                }
            }
            await WaitAsync(() => operation is null);
            Root.UpdateLayout();
            Check(previewContainsText && operation is null && StatusText.Text == "就绪" &&
                smokeMessages.Count == 0 && worksHomeList.Items.Count == 2 &&
                worksHomeList.SelectedItem is GridViewItem { Tag: WorksHomeCardEntry updated } updatedRow &&
                updated.Card.Work.Id == document.Work.Id &&
                SmokeVisuals(updatedRow).OfType<TextBlock>().Any(text => text.Text.Contains("隔离测试作品")),
                "works Preview extracts the fresh document, displays its text and refreshes the selected cover");
            var worksScroll = WorksHomePage.Children.OfType<ScrollViewer>().Single();
            var workBody = (FrameworkElement)worksScroll.Content;
            var workOrigin = workBody.TransformToVisual(WorksHomePage).TransformPoint(new Windows.Foundation.Point());
            Check(workOrigin.X >= 0 && workOrigin.X + workBody.ActualWidth <= WorksHomePage.ActualWidth + 1,
                "works content fits the page at native display scaling");
            await RenderSnapshotAsync(reportPath + ".works.png");
        }

        await NavigateAsync("creative");
        await WaitAsync(() => stylePreviewReady);
        Check(stylePreviewReady && stylePreview?.CoreWebView2 is not null, "local preview hosted by WebView2");
        var core = stylePreview!.CoreWebView2;

        async Task PickAsync(string id)
        {
            CreativeScope.SelectedItem = CreativeScope.Items.OfType<ScopeChoice>().Single(scope => scope.Id == id);
            await WaitAsync(() => !creativeBusy && creativeLoaded && loadedCreativeScope?.Id == id);
            Check(!creativeBusy && creativeLoaded && loadedCreativeScope?.Id == id, "loaded selected project " + id);
        }

        async Task<string> ReadCssAsync(string selector, string property) =>
            JsonSerializer.Deserialize<string>(await core.ExecuteScriptAsync(
                $"getComputedStyle(document.querySelector({JsonSerializer.Serialize(selector)}))[{JsonSerializer.Serialize(property)}]")) ?? "";

        await PickAsync(project.Id);
        if (!reopen)
        {
            Check(installedPreviewFonts.Contains(SmokeFont, StringComparer.OrdinalIgnoreCase), "system font enumerated");
            Check(await ReadCssAsync(".action-trigger", "backgroundColor") == "rgb(79, 112, 88)",
                "project with no local color previews the active global default");
            await core.ExecuteScriptAsync("""
                (() => {
                    const update = (selector, value, eventName = 'input') => {
                        const field = document.querySelector(selector);
                        field.value = value;
                        field.dispatchEvent(new Event(eventName, { bubbles: true }));
                    };
                    document.querySelector('.title-trigger').click();
                    update('#font', 'Segoe UI', 'change');
                    document.querySelector('.action-trigger').click();
                    update('#color-hex', '#20685F');
                    update('#radius', '22');
                    document.querySelector('.body-trigger').click();
                    update('#sample-text', "给今天的风留一点空白。\n桌面与 Codex 共用这段样文。");
                    document.querySelector('[data-editor="background-editor"]').click();
                    update('#background-hex', '#F2F4EB');
                    document.querySelector('.work-card').click();
                    update('#spacing', '31');
                    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
                })()
                """);
            await WaitAsync(() => CreativeSave.IsEnabled && creativeDraft?.SampleText == SmokeSample);
            Check(CreativeSave.IsEnabled && creativeDraft is { Font: SmokeFont, Color: SmokeColor, Background: "#F2F4EB", Radius: 22, Spacing: 31, SampleText: SmokeSample },
                "element edits arrive as a native draft");
            Check((await ReadCssAsync(".title-trigger", "fontFamily")).Contains(SmokeFont, StringComparison.OrdinalIgnoreCase) &&
                await ReadCssAsync(".action-trigger", "backgroundColor") == "rgb(32, 104, 95)",
                "font and color update before saving");
            SaveCreativePage(CreativeSave, new RoutedEventArgs());
            await WaitAsync(() => !creativeBusy && loadedCreative?.Style.SampleText == SmokeSample);
            Check(loadedCreative is { State: "active", Style.Color: SmokeColor } && !CreativeHasDraft && CreativeDelivery.IsEnabled,
                "native save commits active scoped preference and enables exact-delivery preview");
            smokeMessages.Enqueue("下次提供内容");
            ShowNextCreativeDelivery(CreativeDelivery, new RoutedEventArgs());
            await WaitAsync(() => operation is null && !creativeBusy);
            if (smokeMessages.Count != 0) throw new InvalidOperationException("Next creative delivery dialog did not open.");
        }
        else
        {
            Check(loadedCreative is { State: "active", Style.Font: SmokeFont, Style.Color: SmokeColor, Style.SampleText: SmokeSample, Style.Spacing: 31 },
                "fresh process reads the same preference");
            Check(await ReadCssAsync(".action-trigger", "backgroundColor") == "rgb(32, 104, 95)", "fresh process previews persisted color");
            ToggleCreative(CreativeToggle, new RoutedEventArgs());
            await WaitAsync(() => !creativeBusy && loadedCreative?.State == "disabled");
            Check(loadedCreative?.State == "disabled", "pause available from project toolbar");
            UndoCreativePage(CreativeUndo, new RoutedEventArgs());
            await WaitAsync(() => !creativeBusy && loadedCreative?.State == "active");
            Check(loadedCreative is { State: "active", Style.Font: SmokeFont, Style.Color: SmokeColor }, "undo restores the active revision");
        }

        await PickAsync(second.Id);
        Check(loadedCreative?.Style.Font == SmokeMissingFont && await core.ExecuteScriptAsync("document.querySelector('#font-note').hidden") == "false",
            "missing installed font keeps the saved name and displays fallback");
        await PickAsync(project.Id);
        Check(loadedCreative?.Style.Font == SmokeFont, "returning to project restores its own values");

        var originalSize = AppWindow.Size;
        var scale = Root.XamlRoot.RasterizationScale;
        AppWindow.Resize(new SizeInt32(Math.Min(originalSize.Width, (int)(620 * scale)),
            Math.Min(originalSize.Height, (int)(690 * scale))));
        Root.UpdateLayout();
        await Task.Delay(300);
        Check(await core.ExecuteScriptAsync("document.documentElement.scrollWidth <= innerWidth + 1") == "true",
            "no horizontal scrolling in narrow preview");
        await CaptureStylePreviewAsync(core, reportPath + ".preview-narrow.png");
        AppWindow.Resize(originalSize);
        Root.UpdateLayout();
        await Task.Delay(250);
        await CaptureStylePreviewAsync(core, reportPath + ".preview.png");

        return new { checks, project = project.Id, projectRoot = studio, otherRoot = other, dataRoot, codexHome = integration.CodexHome,
            font = loadedCreative!.Style.Font, color = loadedCreative.Style.Color, revision = loadedCreative.Revision, scale = Root.XamlRoot.RasterizationScale };
    }

    private static async Task CaptureStylePreviewAsync(CoreWebView2 core, string path)
    {
        await core.ExecuteScriptAsync("window.scrollTo(0, 0)");
        using var image = new InMemoryRandomAccessStream();
        await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, image);
        using var reader = new DataReader(image.GetInputStreamAt(0));
        var content = new byte[checked((int)image.Size)];
        await reader.LoadAsync(checked((uint)content.Length));
        reader.ReadBytes(content);
        await File.WriteAllBytesAsync(path, content);
    }
}
