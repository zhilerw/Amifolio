using System.Text.Json;
using Amifolio.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace Amifolio.Desktop;

public sealed partial class MainWindow
{
    private async Task<object> RunProductRegressionAsync(string reportPath)
    {
        var checks = new List<string>();
        void Check(bool ok, string name)
        {
            if (!ok) throw new InvalidOperationException("Product follow-up: " + name);
            checks.Add(name);
        }
        async Task Wait(Func<bool> condition)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!condition()) await Task.Delay(30, deadline.Token);
        }
        static string TextIn(DependencyObject panel) => string.Join("\n", SmokeVisuals(panel).OfType<TextBlock>().Select(text => text.Text));
        static void Invoke(Control control) =>
            ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(control).GetPattern(PatternInterface.Invoke)).Invoke();
        static IEnumerable<UIElement> PanelContent(Panel panel)
        {
            foreach (var child in panel.Children)
            {
                yield return child;
                if (child is Panel nested)
                    foreach (var descendant in PanelContent(nested)) yield return descendant;
            }
        }
        HyperlinkButton ActionIn(Panel panel, string label) =>
            PanelContent(panel).OfType<HyperlinkButton>().Single(button => Equals(button.Content, label));
        async Task CaptureNow(string name)
        {
            Root.UpdateLayout();
            CreativePageScroll.ChangeView(null, CreativePageScroll.ScrollableHeight, null, true);
            await RenderSnapshotAsync(reportPath + $".{name}.png", CreativeNow);
        }

        await Wait(() => stylePreviewReady && creativeNowLoad is null);
        Check(currentPage == "creative" && TextIn(CreativeNowRows).Contains("还没有启用的偏好") &&
            TextIn(CreativeNowRows).Contains("目前没有待确认"), "empty home reports actual state");
        await CaptureNow("empty-now");
        Invoke(ActionIn(CreativeNowRows, "检查插件"));
        await Wait(() => currentPage == "connections" && operation is null);
        await RefreshEnvironmentAsync();
        Check(!MigrationCompatibility.IsExpanded && Equals(PluginConnectionAction.Content, "安装插件"),
            "empty setup opens the simple install state");
        await RenderSnapshotAsync(reportPath + ".plugin-simple.png");

        Directory.CreateDirectory(integration.CodexHome);
        await File.WriteAllTextAsync(integration.ConfigPath, "[mcp_servers.amifolio]\ncommand = 'Amifolio.Bridge.exe'\n");
        await RefreshEnvironmentAsync(force: true);
        Check(PluginMigrationNotice.Visibility == Visibility.Visible && pluginLegacyEntries.Count > 0 &&
            !MigrationCompatibility.IsExpanded, "legacy MCP is reported without opening migration controls");
        MigrationCompatibility.IsExpanded = true;
        await RefreshEnvironmentAsync(force: true, showDetails: true);
        Check(MigrationCompatibility.IsExpanded && Equals(IntegrationScope.Header, "部署范围"),
            "legacy migration and scope controls still open");
        await RenderSnapshotAsync(reportPath + ".plugin-migration.png");

        await RequestNavigationAsync("settings");
        Check(!AdvancedSettings.IsExpanded, "project files are behind Advanced by default");
        AdvancedSettings.IsExpanded = true;
        Invoke(ProjectFilesButton);
        await Wait(() => currentPage == "library" && operation is null);
        Check(LibraryHeading.Text == "项目文件" && ScanButton.IsEnabled, "advanced project files retain browsing and scan");

        var fixtureRoot = Path.Combine(Path.GetDirectoryName(dataRoot)!, "product-fixture");
        var studioRoot = Directory.CreateDirectory(Path.Combine(fixtureRoot, "studio")).FullName;
        var otherRoot = Directory.CreateDirectory(Path.Combine(fixtureRoot, "other")).FullName;
        var artifactPath = Path.Combine(studioRoot, "studio-cover.png");
        await File.WriteAllBytesAsync(artifactPath, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAABAAAAAICAIAAAB/FOjAAAAAGElEQVR4nGOQLowmiLZdWQhHDKMaaKEBAIn0nAHZYGEmAAAAAElFTkSuQmCC"));
        await File.WriteAllTextAsync(Path.Combine(integration.CodexHome, ".codex-global-state.json"),
            JsonSerializer.Serialize(new Dictionary<string, object> { ["local-projects"] = new
                { studio = new { name = "海岸计划", rootPaths = new[] { studioRoot } }, other = new { name = "树影计划", rootPaths = new[] { otherRoot } } } }));
        await SourceStore.RefreshCodexProjectsAsync();
        catalogVerified = true;
        await RefreshProjectsAsync();
        var studio = SourceStore.CodexProjects.Single(project => project.RootPath == studioRoot);
        var other = SourceStore.CodexProjects.Single(project => project.RootPath == otherRoot);
        var session = Guid.NewGuid().ToString("D");
        await File.WriteAllTextAsync(Path.Combine(integration.CodexHome, "session_index.jsonl"),
            JsonSerializer.Serialize(new { id = session, thread_name = "海岸封面交付", updated_at = DateTimeOffset.UtcNow }));
        await SourceStore.SetWorkSyncAsync(studio.Id, true);
        await SourceStore.RecordCodexWorksAsync(studio, session, "product-work", [new("delivery", artifactPath)]);
        var card = (await SourceStore.ListCodexWorkCardsAsync(studio.Id, "", 1, default)).Single();
        var laterSession = Guid.NewGuid().ToString("D");
        await File.AppendAllTextAsync(Path.Combine(integration.CodexHome, "session_index.jsonl"), "\n" +
            JsonSerializer.Serialize(new { id = laterSession, thread_name = "海岸封面补发", updated_at = DateTimeOffset.UtcNow }));
        await SourceStore.RecordCodexWorksAsync(studio, laterSession, "product-work-later", [new("delivery", artifactPath)]);
        var latest = (await SourceStore.ListCodexWorkCardsAsync(studio.Id, "", 1, default)).Single();
        await CreativePreferences.SaveCreativeAsync(null, new("宋体", "", "", ""), true, 0);
        await CreativePreferences.SaveCreativeAsync(studio.Id,
            new CreativeStyle("", "#20685F", "", "", card.Work.ReferenceId, card.Work.Title, "标题字重和留白")
            { ReferenceWorkId = card.Work.Id }, true, 0);
        await CreativePreferences.SaveCreativeAsync(other.Id, new("等线", "#718250", "", ""), true, 0);
        await context.Learning.SetPolicyAsync(studio.Id, new() { Capture = true, Learn = true, BatchSize = 1 });
        var capture = await NativeLearningTurnAsync(studio, session, "review-turn", "以后请简洁回答");
        await context.CaptureLearningAsync(capture, integration.CodexHome);
        var job = await context.Learning.ClaimAsync(studio.Id, "chat") ?? throw new InvalidOperationException("Candidate fixture not claimed.");
        var evidence = job.Evidence.Select(item => item.Evidence.Id).ToArray();
        await context.CompleteLearningAsync(job.Id, job.Token,
            [new("style", "verbosity", "brief", "偏好简洁回答", "project", "", evidence)], integration.CodexHome);

        async Task Pick(string id)
        {
            CreativeScope.SelectedItem = CreativeScope.Items.OfType<ScopeChoice>().Single(scope => scope.Id == id);
            await Wait(() => !creativeBusy && loadedCreativeScope?.Id == id && creativeNowLoad is null);
        }
        await RequestNavigationAsync("creative");
        await Pick(studio.Id);
        var now = TextIn(CreativeNowRows);
        Check(now.Contains("#20685F · 本项目") && now.Contains("宋体 · 继承全局") &&
            now.Contains("海岸封面补发") && now.Contains("待确认") && now.Contains("偏好简洁回答"),
            "studio reflects saved fields, inherited font, sourced work and pending learned preference");
        Check(TextIn(CreativeExample).Contains("标题字重和留白") && TextIn(CreativeExample).Contains("海岸封面交付") &&
            SmokeVisuals(CreativeExample).OfType<Image>().Any(), "example shows real thumbnail, project and reuse description");
        await CaptureNow("scoped-now");

        Invoke(ActionIn(CreativeNowRows, "查看建议"));
        await Wait(() => currentPage == "learning" && !learningUiBusy && learningEntry?.State == "candidate");
        Check(loadedLearningScope == studio.Id && LearningPage.Evidence.Text.Contains("以后请简洁回答"),
            "candidate shortcut selects existing scoped evidence and confirmation");
        await RequestNavigationAsync("creative");
        await Pick(studio.Id);
        Root.UpdateLayout();
        Invoke(ActionIn(CreativeNowRows, "查看作品"));
        await Wait(() => currentPage == "works" && worksHomeLoad is null && worksHomeList.SelectedItem is not null);
        await RenderSnapshotAsync(reportPath + ".works-followup.png");
        var selected = (worksHomeList.SelectedItem as GridViewItem)?.Tag as WorksHomeCardEntry;
        Check((worksHomeScope.SelectedItem as WorksHomeProject)?.Id == studio.Id &&
            selected is { } chosen && chosen.Card.Work.Id == latest.Work.Id && chosen.Card.SessionTitle == "海岸封面补发" &&
            !TextIn(WorksHomePage).Contains("浏览项目文件"),
            $"recent work opens its exact card with the verified title and no legacy browser shortcut (selected={selected?.Card.Work.Id == card.Work.Id}, title={selected?.Card.SessionTitle})");

        await File.AppendAllTextAsync(integration.ConfigPath, "\n[plugins.\"amifolio@amifolio-local\"]\nenabled = true\n");
        await RequestNavigationAsync("creative");
        await Pick(other.Id);
        now = TextIn(CreativeNowRows);
        Check(now.Contains("等线 · 本项目") && now.Contains("#718250 · 本项目") &&
            !now.Contains("海岸封面补发") && !now.Contains("偏好简洁回答") && CreativeExample.Children.Count == 0 &&
            ActionIn(CreativeNowRows, "打开作品").IsEnabled,
            "second project has its own values without the first project's work, suggestion or example");

        File.Delete(artifactPath);
        await Pick(studio.Id);
        Check(TextIn(CreativeExample).Contains("范例原件不可用") &&
            !SmokeVisuals(CreativeExample).OfType<Image>().Any(), "missing example clears the thumbnail but keeps source and association");
        await CaptureNow("missing-example");
        smokeMessages.Enqueue("范例来源");
        Invoke(ActionIn(CreativeExample, "核对来源"));
        await Wait(() => operation is null && smokeMessages.Count == 0);
        smokeDialogAnswers.Enqueue(true);
        Invoke(ActionIn(CreativeExample, "移除关联"));
        await Wait(() => !creativeBusy && loadedCreative?.Style.ReferenceId is null && creativeNowLoad is null);
        var saved = await CreativePreferences.CreativeAsync(studio.Id);
        Check(saved is { Style.Color: "#20685F", Style.ReferenceId: null } && CreativeExample.Children.Count == 0 &&
            (await SourceStore.ListCodexWorkCardsAsync(studio.Id, "", 3, default)).Count == 2,
            "unlink retains the project style and work record when the original is missing");
        return new { checks, scale = Root.XamlRoot.RasterizationScale };
    }
}
