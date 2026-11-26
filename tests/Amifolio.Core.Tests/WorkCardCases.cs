using System.Text.Json;
using Amifolio.Core;
using Microsoft.Data.Sqlite;

internal static class WorkCardCases
{
    internal static (string, Func<Fixture, Task>)[] All =>
    [
        ("work cards use the latest trusted session title", SessionTitle),
        ("work cards fall back when session titles cannot be verified", TitleFallback),
        ("work cards resolve older exact references and keep original checks", ExactReference),
        ("work cards explicitly preview a scoped document", DocumentPreview)
    ];

    private static async Task SessionTitle(Fixture fixture)
    {
        var (store, project, home) = await Open(fixture);
        var session = Guid.NewGuid();
        var second = Guid.NewGuid();
        await Record(store, project, session, "cover.png");
        await Record(store, project, second, "other.png");
        var index = Path.Combine(home, "session_index.jsonl");
        File.WriteAllLines(index, [
            TitleRow(session, "封面设计", 2),
            TitleRow(second, "另一项任务", 1),
            TitleRow(session, "旧标题", 1),
            TitleRow(Guid.NewGuid(), "无关会话", 3)
        ]);
        var cards = await store.ListCodexWorkCardsAsync(project.Id, "", 2, default);
        Check(cards.Single(c => c.Work.SessionId == session.ToString("D")).SessionTitle == "封面设计" &&
            cards.Single(c => c.Work.SessionId == second.ToString("D")).SessionTitle == "另一项任务",
            "titles must match the recorded UUID and newest timestamp");
        File.AppendAllLines(index, [TitleRow(session, "新任务名", 3)]);
        Check((await store.ListCodexWorkCardsAsync(project.Id, "cover", 1, default)).Single().SessionTitle == "新任务名",
            "a later rename should appear on refresh");
        await store.SetWorkSyncAsync(project.Id, false);
        var paused = (await store.ListCodexWorkCardsAsync(project.Id, "cover", 1, default)).Single();
        Check(paused.SessionTitle is null && paused.Work.SessionId == session.ToString("D"),
            "pausing sync should keep existing works and UUIDs without reading fresh titles");
    }

    private static async Task TitleFallback(Fixture fixture)
    {
        var (store, project, home) = await Open(fixture);
        var session = Guid.NewGuid();
        await Record(store, project, session, "cover.png");
        var directory = Path.Combine(home, "sessions");
        Directory.CreateDirectory(directory);
        File.WriteAllLines(Path.Combine(directory, $"rollout-{session:D}.jsonl"), [
            JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = session, cwd = project.RootPath } }),
            JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = "user_message", message = "不要从正文推断标题" } })
        ]);
        await AssertFallback();
        var index = Path.Combine(home, "session_index.jsonl");
        File.WriteAllLines(index, [
            "{broken",
            JsonSerializer.Serialize(new { id = session, title = "未知字段", updated_at = DateTimeOffset.UtcNow }),
            TitleRow(Guid.NewGuid(), "其他会话", 2)
        ]);
        await AssertFallback();
        File.WriteAllLines(index, [TitleRow(session, "旧标题", 1), TitleRow(session, "  ", 2)]);
        await AssertFallback();
        File.WriteAllLines(index, [TitleRow(session, "标题甲", 2), TitleRow(session, "标题乙", 2)]);
        await AssertFallback();
        using (var oversized = File.Create(index)) oversized.SetLength(CodexCatalog.MaxMetadataBytes + 1);
        await AssertFallback();

        async Task AssertFallback()
        {
            var card = (await store.ListCodexWorkCardsAsync(project.Id, "", 1, default)).Single();
            Check(card.SessionTitle is null && card.Work.SessionId == session.ToString("D") && card.HasImage,
                "unverified title should not remove the work, original image, or its source UUID");
        }
    }

    private static async Task ExactReference(Fixture fixture)
    {
        var (store, project, home) = await Open(fixture);
        var session = Guid.NewGuid();
        var image = await Record(store, project, session, "old-cover.png");
        var old = (await store.ListCodexWorkCardsAsync(project.Id, "", 120, default)).Single();
        var other = store.CodexProjects.Single(p => p.Id != project.Id);
        using (var db = new SqliteConnection($"Data Source={Path.Combine(fixture.DataRoot, "portfolio.db")}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                DROP INDEX IF EXISTS ix_codex_works_reference;
                WITH RECURSIVE seq(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM seq WHERE n<125)
                INSERT INTO codex_works(id,project,session,turn,kind,title,recorded)
                SELECT 'new-'||n,$project,$session,'turn','delivery','recent-'||n,$recorded+n FROM seq;
                """;
            command.Parameters.AddWithValue("$project", project.Id);
            command.Parameters.AddWithValue("$session", session.ToString("D"));
            command.Parameters.AddWithValue("$recorded", old.Work.RecordedAt.ToUnixTimeMilliseconds());
            command.ExecuteNonQuery();
        }
        store = new PortfolioStore(fixture.DataRoot);
        store.EnableCodexReferenceMode(home);
        await store.RefreshCodexProjectsAsync();
        Check((await store.ListCodexWorkCardsAsync(project.Id, "", 120, default)).All(c => c.Work.Id != old.Work.Id),
            "fixture must move the old work outside the recent list");
        using (var db = new SqliteConnection($"Data Source={Path.Combine(fixture.DataRoot, "portfolio.db")}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = """
                EXPLAIN QUERY PLAN SELECT id FROM codex_works INDEXED BY ix_codex_works_reference
                WHERE project=$project AND reference_id=$ref ORDER BY recorded DESC,title,id LIMIT 1
                """;
            command.Parameters.AddWithValue("$project", project.Id);
            command.Parameters.AddWithValue("$ref", old.Work.ReferenceId!);
            using var reader = command.ExecuteReader();
            var plan = reader.Read() ? reader.GetString(3) : "";
            Check(plan.Contains("ix_codex_works_reference") && plan.Contains("reference_id=?"),
                "old databases should gain the index used by the exact reference lookup: " + plan);
        }
        var exact = await store.GetCodexWorkCardForReferenceAsync(project.Id, old.Work.ReferenceId!, default);
        if (exact is null || exact.Work.Id != old.Work.Id || !exact.HasImage)
            throw new InvalidOperationException("exact reference lookup missed the old card");
        Check(await store.GetCodexWorkCardAsync(project.Id, old.Work.Id, default) == exact &&
            await store.GetCodexWorkCardForReferenceAsync(other.Id, old.Work.ReferenceId!, default) is null &&
            await store.GetCodexWorkCardAsync(other.Id, old.Work.Id, default) is null, "exact lookup ignored scope or work identity");
        await using (var stream = await store.OpenCodexWorkImageAsync(exact.Work, exact.ImageModifiedAt, default))
            Check(stream is { Length: 8 }, "validated image source should still open");
        File.Delete(image);
        var missing = await store.GetCodexWorkCardForReferenceAsync(project.Id, old.Work.ReferenceId!, default);
        Check(missing is { HasImage: false, Work.Availability: "源文件缺失" } &&
            await store.OpenCodexWorkImageAsync(exact.Work, exact.ImageModifiedAt, default) is null &&
            await store.PreviewCodexWorkReferenceAsync(exact.Work, default) is null,
            "missing originals must keep conversation evidence and block stale preview actions");
    }

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

    private static async Task<string> Record(PortfolioStore store, CodexProject project, Guid session, string name)
    {
        var path = Path.Combine(project.RootPath, name);
        File.WriteAllBytes(path, [137, 80, 78, 71, 13, 10, 26, 10]);
        Check(await store.RecordCodexWorksAsync(project, session.ToString("D"), "turn-1", [new("delivery", path)]) == 1,
            "work fixture did not record");
        return path;
    }

    private static string TitleRow(Guid session, string title, int day) =>
        JsonSerializer.Serialize(new { id = session, thread_name = title, updated_at = new DateTimeOffset(2026, 11, day, 0, 0, 0, TimeSpan.Zero) });

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
