using System.Text.Json;
using Amifolio.Core;

internal static class WorkCardCases
{
    internal static (string, Func<Fixture, Task>)[] All =>
    [
        ("work cards explicitly preview a scoped document", DocumentPreview)
    ];

    private static async Task<(PortfolioStore Store, CodexProject Project, string Home)> Open(Fixture fixture)
    {
        var root = Directory.CreateDirectory(Path.Combine(fixture.ReferenceRoot, "project")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(fixture.ReferenceRoot, "other")).FullName;
        var registry = fixture.File("codex/.codex-global-state.json",
            JsonSerializer.Serialize(new Dictionary<string, string[]> { ["electron-saved-workspace-roots"] = [root, other] }));
        var home = Path.GetDirectoryName(registry)!;
        var store = new PortfolioStore(fixture.DataRoot);
        store.EnableCodexReferenceMode(home);
        await store.RefreshCodexProjectsAsync();
        var project = store.CodexProjects.Single(p => p.RootPath == root);
        await store.SetWorkSyncAsync(project.Id, true);
        return (store, project, home);
    }

    private static async Task DocumentPreview(Fixture fixture)
    {
        var (store, project, _) = await Open(fixture);
        var path = Path.Combine(project.RootPath, "document.md");
        const string original = "# 交付文档\n隔离的文档预览正文";
        await File.WriteAllTextAsync(path, original);
        await store.RecordCodexWorksAsync(project, Guid.NewGuid().ToString("D"), "turn-1", [new("delivery", path)]);
        var card = (await store.ListCodexWorkCardsAsync(project.Id, "", 1, default)).Single();
        Check(card.Excerpt.Length == 0, "listing should not parse an original");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        var preview = await store.PreviewCodexWorkReferenceAsync(card.Work, deadline.Token);
        Check(preview?.Preview.Contains("隔离的文档预览正文", StringComparison.Ordinal) == true &&
            await File.ReadAllTextAsync(path) == original, "explicit preview must extract without modifying the source");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
