using System.Text.Json;
using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private const string SmokeFont = "Segoe UI";
    private const string SmokeColor = "#3D719C";
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
        Directory.CreateDirectory(studio);
        Directory.CreateDirectory(other);
        Directory.CreateDirectory(integration.CodexHome);
        if (!reopen)
        {
            await File.WriteAllTextAsync(cover, "# 交付物\n这是 M3 的隔离测试作品。");
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

        Check(currentPage == "works", "works is the default page");
        if (!reopen)
        {
            await SourceStore.SetWorkSyncAsync(project.Id, true);
            await SourceStore.RecordCodexWorksAsync(project, Guid.NewGuid().ToString("D"), "smoke-turn",
                [new CodexWorkSource("delivery", cover)]);
            await CreativePreferences.SaveCreativeAsync(null, new("", "#6B5A80", "", ""), true, 0);
            await CreativePreferences.SaveCreativeAsync(second.Id, new(SmokeMissingFont, "#A24639", "", ""), true, 0);
            await RefreshWorksHomeAsync();
            Check(worksHomeList.Items.Count == 1, "recent synced work appears on initial page");
            worksHomeList.SelectedIndex = 0;
            Check(expandedWork is not null, "selected work expands inline");
            Root.UpdateLayout();
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
            Check(await ReadCssAsync(".action-trigger", "backgroundColor") == "rgb(107, 90, 128)",
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
                    update('#color-hex', '#3D719C');
                    update('#radius', '22');
                    document.querySelector('.body-trigger').click();
                    update('#sample-text', "给今天的风留一点空白。\n桌面与 Codex 共用这段样文。");
                    document.querySelector('[data-editor="background-editor"]').click();
                    update('#background-hex', '#FFF2E5');
                    document.querySelector('.work-card').click();
                    update('#spacing', '31');
                    document.querySelector('.work-card').click();
                })()
                """);
            await WaitAsync(() => CreativeSave.IsEnabled && creativeDraft?.SampleText == SmokeSample);
            Check(CreativeSave.IsEnabled && creativeDraft is { Font: SmokeFont, Color: SmokeColor, Background: "#FFF2E5", Radius: 22, Spacing: 31, SampleText: SmokeSample },
                "element edits arrive as a native draft");
            Check((await ReadCssAsync(".title-trigger", "fontFamily")).Contains(SmokeFont, StringComparison.OrdinalIgnoreCase) &&
                await ReadCssAsync(".action-trigger", "backgroundColor") == "rgb(61, 113, 156)",
                "font and color update before saving");
            SaveCreativePage(CreativeSave, new RoutedEventArgs());
            await WaitAsync(() => !creativeBusy && loadedCreative?.Style.SampleText == SmokeSample);
            Check(loadedCreative is { State: "active", Style.Color: SmokeColor } && !CreativeHasDraft, "native save commits active scoped preference");
        }
        else
        {
            Check(loadedCreative is { State: "active", Style.Font: SmokeFont, Style.Color: SmokeColor, Style.SampleText: SmokeSample, Style.Spacing: 31 },
                "fresh process reads the same preference");
            Check(await ReadCssAsync(".action-trigger", "backgroundColor") == "rgb(61, 113, 156)", "fresh process previews persisted color");
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
