using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amifolio.Core;

internal static class CatalogCases
{
    // The main runner registers All. These cases never inspect the user's Codex home.
    internal static (string, Func<Fixture, Task>)[] All =>
    [
        ("catalog session reader never requests or consumes dialogue bytes", async _ =>
        {
            const string header = "{\"type\":\"session_meta\",\"payload\":{\"cwd\":\"synthetic\"}}";
            using var input = new PrefixBoundaryStream(header + "\nDIALOGUE_MUST_NOT_BE_READ", Encoding.UTF8.GetByteCount(header) + 1);
            var budget = new CodexCatalog.Budget(CancellationToken.None, CancellationToken.None);
            var result = await CodexCatalog.ReadFirstLineAsync(input, CodexCatalog.MaxSessionHeaderBytes, budget);
            Check(Encoding.UTF8.GetString(result!) == header);
            Check(input.Position == Encoding.UTF8.GetByteCount(header) + 1 && budget.ReadBytes == input.Position);
            Check(input.MaximumRequested == 1 && !budget.Truncated);
        }),
        ("catalog session file reader bypasses FileStream buffered reads", async _ =>
        {
            using var f = new CatalogFixture(); const string header = "{\"type\":\"session_meta\"}";
            var path = f.File("prefix.jsonl", header + "\n" + new string('d', 8192));
            using var input = new NoBufferedReadFileStream(path);
            var budget = new CodexCatalog.Budget(CancellationToken.None, CancellationToken.None);
            var result = await CodexCatalog.ReadFirstLineAsync(input, CodexCatalog.MaxSessionHeaderBytes, budget);
            Check(Encoding.UTF8.GetString(result!) == header && input.Position == Encoding.UTF8.GetByteCount(header) + 1);
            Check(budget.ReadBytes == input.Position && !budget.Truncated);
        }),
        ("catalog session reader exact header limit and total byte budget are enforced", async _ =>
        {
            var header = new string('h', CodexCatalog.MaxSessionHeaderBytes);
            using var exact = new PrefixBoundaryStream(header + "\nPRIVATE", header.Length + 1);
            var exactBudget = new CodexCatalog.Budget(CancellationToken.None, CancellationToken.None);
            Check((await CodexCatalog.ReadFirstLineAsync(exact, CodexCatalog.MaxSessionHeaderBytes, exactBudget))!.Length == header.Length);
            Check(exact.Position == header.Length + 1 && !exactBudget.Truncated);
            using var oversized = new PrefixBoundaryStream(header + "h\nPRIVATE", header.Length + 1);
            var oversizedBudget = new CodexCatalog.Budget(CancellationToken.None, CancellationToken.None);
            Check(await CodexCatalog.ReadFirstLineAsync(oversized, CodexCatalog.MaxSessionHeaderBytes, oversizedBudget) is null);
            Check(oversizedBudget.Truncated && oversizedBudget.ReadBytes == header.Length + 1);
            using var limited = new PrefixBoundaryStream("header\nPRIVATE", 3);
            var totalBudget = new CodexCatalog.Budget(CancellationToken.None, CancellationToken.None) { ReadBytes = CodexCatalog.MaxReadBytes - 3 };
            Check(await CodexCatalog.ReadFirstLineAsync(limited, CodexCatalog.MaxSessionHeaderBytes, totalBudget) is null);
            Check(totalBudget.Truncated && totalBudget.ReadBytes == CodexCatalog.MaxReadBytes && limited.Position == 3);
        }),
        ("catalog session reader cancels between exact byte requests", async _ =>
        {
            using var cancellation = new CancellationTokenSource();
            using var input = new PrefixBoundaryStream(new string('x', 4096) + "\nPRIVATE", 8, reads => { if (reads == 8) cancellation.Cancel(); });
            var budget = new CodexCatalog.Budget(cancellation.Token, cancellation.Token);
            await Cancelled(() => CodexCatalog.ReadFirstLineAsync(input, CodexCatalog.MaxSessionHeaderBytes, budget));
            Check(input.Position == 8 && budget.ReadBytes == 8 && input.MaximumRequested == 1);
        }),
        ("catalog current projects have stable case-insensitive root identities", async _ =>
        {
            using var f = new CatalogFixture();
            var root = f.Directory("projects/Alpha");
            f.Current(("host-id", "Alpha", [root, root.ToUpperInvariant(), root + Path.DirectorySeparatorChar]));
            var catalog = f.Catalog();
            var result = await catalog.DiscoverAsync();
            Check(result.Items.Count == 1 && result.Items[0].Name == "Alpha" && !result.Truncated);
            var id = result.Items[0].Id;
            f.Current(("changed-host-id", "Renamed", [root]));
            Check((await catalog.DiscoverAsync()).Items.Single().Id == id);
        }),
        ("catalog legacy roots and labels preserve nested same-name directories", async _ =>
        {
            using var f = new CatalogFixture();
            var a = f.Directory("projects/Alpha"); var b = f.Directory("projects/Alpha/nested/Alpha");
            f.State(new Dictionary<string, object>
            {
                ["electron-saved-workspace-roots"] = new[] { a, b },
                ["electron-workspace-root-labels"] = new Dictionary<string, string> { [a] = "outer", [b] = "inner" }
            });
            var result = await f.Catalog().DiscoverAsync();
            Check(result.Items.Count == 2 && result.Items.Select(x => x.Id).Distinct().Count() == 2);
            Check(result.Items.Single(x => x.RootPath == a).Name == "outer");
            Check(result.Items.Single(x => x.RootPath == b).Name == "inner");
        }),
        ("catalog session fallback reads long first-line metadata only", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha");
            f.Session("2026/11/13/a.jsonl", root, new string('x', 80 * 1024), "{malformed-dialogue\n");
            f.Session("2026/11/13/b.jsonl", root, "", "{\"type\":\"session_meta\",\"payload\":{\"cwd\":\"DO-NOT-DISCOVER\"}}\n");
            var result = await f.Catalog().DiscoverAsync();
            Check(result.Items.Single().RootPath == root && result.Items.Single().SessionCount == 2);
            Check(result.Warnings.Any(x => x.Contains("回退", StringComparison.Ordinal) && x.Contains("官方", StringComparison.Ordinal)));
            Check(!result.Warnings.Any(x => x.Contains("malformed-dialogue", StringComparison.Ordinal)));
        }),
        ("catalog registry counts nested cwd without creating extra historical projects", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha"); var nested = f.Directory("projects/Alpha/nested");
            var unrelated = f.Directory("historical-private-location");
            f.Current(("x", "named", [root]));
            f.Session("a.jsonl", root); f.Session("b.jsonl", nested); f.Session("c.jsonl", unrelated);
            f.Session("projectless.jsonl", "relative-private-location");
            var result = await f.Catalog().DiscoverAsync();
            Check(result.Items.Count == 1 && result.Items[0].SessionCount == 2 && result.Items[0].Name == "named");
            Check(result.Warnings.Count == 1 && result.Warnings[0].Contains("2", StringComparison.Ordinal));
            Check(result.Warnings.Count > 0 && result.Warnings.All(x => !x.Contains(unrelated, StringComparison.Ordinal) && !x.Contains("relative-private-location", StringComparison.Ordinal)));
        }),
        ("catalog unrelated session warnings are aggregated without exposing cwd paths", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha"); var unrelated = f.Directory("private-unrelated");
            f.Current(("a", "Alpha", [root]));
            for (var i = 0; i < 160; i++) f.Session($"old-{i:D3}.jsonl", unrelated);
            var result = await f.Catalog().DiscoverAsync();
            Check(result.Items.Count == 1 && result.Items[0].SessionCount == 0);
            Check(result.Warnings.Count == 1 && result.Warnings[0].Contains("160", StringComparison.Ordinal));
            Check(result.Warnings.All(x => !x.Contains(unrelated, StringComparison.Ordinal)));
        }),
        ("catalog registered nested projects get closest-root session counts", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha"); var nested = f.Directory("projects/Alpha/nested");
            var deeper = f.Directory("projects/Alpha/nested/deeper"); var sibling = f.Directory("projects/Alpha-other");
            f.Current(("outer", "outer", [root]), ("inner", "inner", [nested]));
            f.Session("a.jsonl", root); f.Session("b.jsonl", nested); f.Session("c.jsonl", deeper); f.Session("d.jsonl", sibling);
            var result = await f.Catalog().DiscoverAsync();
            Check(result.Items.Count == 2 && result.Items.Single(p => p.RootPath == root).SessionCount == 1);
            Check(result.Items.Single(p => p.RootPath == nested).SessionCount == 2);
        }),
        ("catalog legacy registry is authoritative over unrelated historical cwd", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha"); var nested = f.Directory("projects/Alpha/nested");
            var unrelated = f.Directory("private-unrelated");
            f.State(new Dictionary<string, object> { ["electron-saved-workspace-roots"] = new[] { root } });
            f.Session("a.jsonl", nested); f.Session("b.jsonl", unrelated);
            var result = await f.Catalog().DiscoverAsync();
            Check(result.Items.Count == 1 && result.Items[0].RootPath == root && result.Items[0].SessionCount == 1);
            Check(result.Warnings.Count > 0 && result.Warnings.All(x => !x.Contains(unrelated, StringComparison.Ordinal)));
        }),
        ("catalog current registry supersedes stale legacy roots and empty registry stays empty", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha"); var old = f.Directory("historical");
            var current = new Dictionary<string, object> { ["a"] = new { name = "current", rootPaths = new[] { root } } };
            f.State(new Dictionary<string, object> { ["local-projects"] = current, ["electron-saved-workspace-roots"] = new[] { old } });
            f.Session("a.jsonl", old);
            var catalog = f.Catalog(); var result = await catalog.DiscoverAsync();
            Check(result.Items.Count == 1 && result.Items[0].RootPath == root && result.Items[0].SessionCount == 0);
            f.State(new Dictionary<string, object> { ["local-projects"] = new { }, ["electron-saved-workspace-roots"] = new[] { old } });
            var empty = await catalog.DiscoverAsync();
            Check(empty.Items.Count == 0 && empty.Warnings.Count > 0 && empty.Warnings.All(x => !x.Contains("回退", StringComparison.Ordinal)));
            f.State(new Dictionary<string, object> { ["electron-saved-workspace-roots"] = Array.Empty<string>() });
            Check((await catalog.DiscoverAsync()).Items.Count == 0);
        }),
        ("catalog missing invalid unsafe roots warn independently", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha");
            var secret = f.Directory("projects/.ssh/nested");
            var worktree = f.Directory("codex/worktrees/sample");
            f.Current(("x", "roots", [root, Path.Combine(f.Root, "missing"), "relative/path", @"\\server\share", secret,
                Path.GetPathRoot(root)!, f.Home, f.Data, worktree]));
            var result = await f.Catalog().DiscoverAsync();
            Check(result.Items.Count == 2 && result.Items.Any(p => p.RootPath == worktree));
            Check(result.Warnings.Count > 0 && result.Items.Any(p => p.RootPath == root));
        }),
        ("catalog unknown malformed and recognized empty metadata differ", async _ =>
        {
            using var f = new CatalogFixture();
            f.State(new { futureProjects = new[] { "unknown" } });
            Check((await f.Catalog().DiscoverAsync()).Warnings.Count > 0);
            f.RawState("{broken");
            Check((await f.Catalog().DiscoverAsync()).Warnings.Count > 0);
            f.State(new Dictionary<string, object> { ["local-projects"] = new { } });
            var empty = await f.Catalog().DiscoverAsync();
            Check(empty.Items.Count == 0 && !empty.Truncated && empty.Warnings.Count == 0);
            f.State(new Dictionary<string, object> { ["local-projects"] = new[] { "wrong-shape" } });
            Check((await f.Catalog().DiscoverAsync()).Warnings.Count > 0);
        }),
        ("catalog metadata size and invalid session header bounds keep useful roots", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha");
            f.RawState(new string(' ', CodexCatalog.MaxMetadataBytes + 1));
            f.Session("valid.jsonl", root);
            var first = await f.Catalog().DiscoverAsync();
            Check(first.Truncated && first.Items.Count == 1 && first.Warnings.Count > 0);
            f.Current(("a", "Alpha", [root]));
            f.File("codex/sessions/over.jsonl", new string('x', CodexCatalog.MaxSessionHeaderBytes + 1));
            f.File("codex/sessions/invalid.jsonl", "{broken\n{\"type\":\"session_meta\"}\n");
            var result = await f.Catalog().DiscoverAsync();
            Check(result.Truncated && result.Items.Single().SessionCount == 1 && result.Warnings.Count >= 2);
        }),
        ("catalog independent session locks and malformed registry rows do not permit session projects", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha");
            f.State(new Dictionary<string, object> { ["local-projects"] = new Dictionary<string, object>
            {
                ["bad"] = new { rootPaths = "wrong" }, ["good"] = new { name = "Alpha", rootPaths = new[] { root } }
            } });
            f.Session("good.jsonl", root); var locked = f.Session("locked.jsonl", root);
            using var held = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
            var result = await f.Catalog().DiscoverAsync();
            Check(result.Items.Single().SessionCount == 1 && result.Warnings.Count >= 2);
            f.State(new Dictionary<string, object> { ["local-projects"] = new[] { "unknown-registry-shape" } });
            var unknown = await f.Catalog().DiscoverAsync();
            Check(unknown.Items.Count == 0 && unknown.Warnings.Count > 0 && unknown.Warnings.All(x => !x.Contains("回退", StringComparison.Ordinal)));
        }),
        ("catalog only last discovered root and identity permit browsing and validation", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha"); var other = f.Directory("projects/Beta");
            var file = f.File("projects/Alpha/readme.txt", "allowed"); var outside = f.File("projects/Beta/readme.txt", "outside");
            f.Current(("a", "Alpha", [root])); var catalog = f.Catalog();
            var project = (await catalog.DiscoverAsync()).Items.Single();
            await Reject(() => f.Catalog().BrowseAsync(project));
            await Reject(() => f.Catalog().ValidateFileAsync(project, file));
            await Reject(() => catalog.BrowseAsync(project with { Id = "forged" }));
            await Reject(() => catalog.BrowseAsync(project with { RootPath = other }));
            await Reject(() => catalog.ValidateFileAsync(project with { RootPath = other }, outside));
            var knownElsewhere = f.Catalog(); f.Current(("b", "Beta", [other]));
            var forged = (await knownElsewhere.DiscoverAsync()).Items.Single();
            await Reject(() => catalog.BrowseAsync(forged));
            await Reject(() => catalog.ValidateFileAsync(forged, outside));
            Check((await catalog.BrowseAsync(project)).Items.Count == 1);
            f.Current(); await catalog.DiscoverAsync();
            await Reject(() => catalog.BrowseAsync(project));
            await Reject(() => catalog.ValidateFileAsync(project, file));
        }),
        ("catalog browse preserves immediate hierarchy and normal source directories", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha");
            f.File("projects/Alpha/z.txt", "z"); f.File("projects/Alpha/a/deeper/same.md", "one");
            f.File("projects/Alpha/b/same.md", "two"); f.Directory("projects/Alpha/src"); f.Directory("projects/Alpha/references");
            f.Directory("projects/Alpha/知识库"); f.Current(("x", "Alpha", [root]));
            var catalog = f.Catalog(); var p = (await catalog.DiscoverAsync()).Items.Single();
            var listing = await catalog.BrowseAsync(p);
            Check(listing.Items.Count == 6 && listing.Items.Take(5).All(x => x.IsDirectory));
            Check(listing.Items.All(x => Path.GetDirectoryName(x.Path) == root && x.State == "ready"));
            var nested = await catalog.BrowseAsync(p, Path.Combine(root, "a"));
            Check(nested.Items.Single().Name == "deeper");
            Check((await catalog.BrowseAsync(p, Path.Combine(root, "a/deeper"))).Items.Single().Name == "same.md");
            Check(await catalog.ValidateFileAsync(p, Path.Combine(root, "a/deeper/same.md")) == Path.GetFullPath(Path.Combine(root, "a/deeper/same.md")));
        }),
        ("catalog excluded entries stay visible and cannot be selected as browse roots", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha");
            f.File("projects/Alpha/.env", "secret"); f.File("projects/Alpha/config.toml", "secret");
            f.File("projects/Alpha/AGENTS.md", "instruction"); f.File("projects/Alpha/.ssh/nested/key.txt", "secret");
            f.File("projects/Alpha/node_modules/lib/index.js", "cache");
            f.Current(("x", "Alpha", [root])); var catalog = f.Catalog(); var p = (await catalog.DiscoverAsync()).Items.Single();
            var result = await catalog.BrowseAsync(p);
            Check(result.Items.Count == 5 && result.Items.All(e => e.State == "excluded"));
            await Reject(() => catalog.BrowseAsync(p, Path.Combine(root, ".ssh/nested")));
            await Reject(() => catalog.BrowseAsync(p, Path.Combine(root, "node_modules/lib")));
            await Reject(() => catalog.ValidateFileAsync(p, Path.Combine(root, ".env")));
            await Reject(() => catalog.ValidateFileAsync(p, Path.Combine(root, ".ssh/nested/key.txt")));
            f.Current(("bypass", "secret", [Path.Combine(root, ".ssh/nested")]));
            Check((await catalog.DiscoverAsync()).Items.Count == 0);
        }),
        ("catalog secret handles stay unopened and regular locked files fail validation", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha");
            var secret = f.File("projects/Alpha/.env", "secret"); var normal = f.File("projects/Alpha/readme.txt", "normal");
            f.Current(("x", "Alpha", [root])); var catalog = f.Catalog(); var p = (await catalog.DiscoverAsync()).Items.Single();
            using var a = new FileStream(secret, FileMode.Open, FileAccess.Read, FileShare.None);
            using var b = new FileStream(normal, FileMode.Open, FileAccess.Read, FileShare.None);
            Check((await catalog.BrowseAsync(p)).Items.Single(x => x.Path == secret).State == "excluded");
            await Reject(() => catalog.ValidateFileAsync(p, secret));
            await Reject(() => catalog.ValidateFileAsync(p, normal));
        }),
        ("catalog traversal sibling prefix own data and Windows roots are blocked", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha");
            var outside = f.File("projects/Alpha-other/a.txt", "outside");
            var data = f.Directory("projects/Alpha/app-data"); f.File("projects/Alpha/app-data/private.txt", "private");
            f.Current(("x", "Alpha", [root])); var catalog = f.Catalog(data); var p = (await catalog.DiscoverAsync()).Items.Single();
            await Reject(() => catalog.BrowseAsync(p, Path.Combine(root, "..")));
            await Reject(() => catalog.ValidateFileAsync(p, outside));
            await Reject(() => catalog.ValidateFileAsync(p, @"\\server\share\a.txt"));
            await Reject(() => catalog.ValidateFileAsync(p, root + ":stream"));
            await Reject(() => catalog.BrowseAsync(p, data));
            Check((await catalog.BrowseAsync(p)).Items.Single().State == "excluded");
            if (OperatingSystem.IsWindows())
            {
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                f.Current(("w", "Windows", [windows]));
                Check((await f.Catalog().DiscoverAsync()).Items.Count == 0);
            }
        }),
        ("catalog missing directory and missing file fail explicitly", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha");
            f.Current(("x", "Alpha", [root])); var catalog = f.Catalog(); var p = (await catalog.DiscoverAsync()).Items.Single();
            await Reject(() => catalog.BrowseAsync(p, Path.Combine(root, "missing")));
            await Reject(() => catalog.ValidateFileAsync(p, Path.Combine(root, "missing.txt")));
            await Reject(() => catalog.ValidateFileAsync(p, root));
            System.IO.Directory.Delete(root);
            await Reject(() => catalog.BrowseAsync(p));
        }),
        ("catalog symlink roots children and session directories never escape", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha"); var outside = f.Directory("outside");
            f.File("outside/private.txt", "outside");
            await Junction(Path.Combine(root, "linked"), outside);
            await Junction(Path.Combine(f.Home, "sessions"), outside);
            f.Current(("x", "Alpha", [root, Path.Combine(root, "linked")]));
            var catalog = f.Catalog(); var found = await catalog.DiscoverAsync();
            Check(found.Items.Count == 1 && found.Warnings.Count > 0);
            var p = found.Items.Single();
            Check((await catalog.BrowseAsync(p)).Items.Single().State == "excluded");
            await Reject(() => catalog.BrowseAsync(p, Path.Combine(root, "linked")));
            await Reject(() => catalog.ValidateFileAsync(p, Path.Combine(root, "linked/private.txt")));
        }),
        ("catalog scan and validation leave source tree bytes attributes times unchanged", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha");
            var file = f.File("projects/Alpha/report.md", "original"); f.File("projects/Alpha/.env", "secret");
            f.Current(("x", "Alpha", [root])); f.Session("a.jsonl", root);
            var before = Snapshot(f.Root);
            var catalog = f.Catalog(); var p = (await catalog.DiscoverAsync()).Items.Single();
            await catalog.BrowseAsync(p); await catalog.ValidateFileAsync(p, file);
            Check(before.SequenceEqual(Snapshot(f.Root)));
        }),
        ("catalog cancelled calls never return success", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha"); f.Current(("x", "Alpha", [root]));
            var file = f.File("projects/Alpha/readme.txt", "still allowed after cancelled discovery");
            var catalog = f.Catalog(); var p = (await catalog.DiscoverAsync()).Items.Single();
            using var cts = new CancellationTokenSource(); cts.Cancel();
            await Cancelled(() => catalog.DiscoverAsync(cts.Token));
            await Cancelled(() => catalog.BrowseAsync(p, ct: cts.Token));
            await Cancelled(() => catalog.ValidateFileAsync(p, Path.Combine(root, "missing.txt"), cts.Token));
            Check((await catalog.BrowseAsync(p)).Items.Count == 1);
            Check(await catalog.ValidateFileAsync(p, file) == file);
        }),
        ("catalog immediate directory count limit reports truncation", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha");
            for (var i = 0; i <= CodexCatalog.MaxEntries; i++) f.Directory($"projects/Alpha/d{i:D5}");
            f.Current(("x", "Alpha", [root])); var catalog = f.Catalog(); var p = (await catalog.DiscoverAsync()).Items.Single();
            var result = await catalog.BrowseAsync(p);
            Check(result.Truncated && result.Items.Count == CodexCatalog.MaxEntries && result.Warnings.Count > 0);
        }),
        ("catalog finite session count and directory depth return explicit partial results", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha");
            f.Current(("x", "Alpha", [root]));
            for (var i = 0; i <= CodexCatalog.MaxSessionFiles; i++) f.Session($"a{i:D5}.jsonl", root);
            var result = await f.Catalog().DiscoverAsync();
            Check(result.Truncated && result.Items.Single().SessionCount <= CodexCatalog.MaxSessionFiles && result.Warnings.Count > 0);
            using var nested = new CatalogFixture(); var nestedRoot = nested.Directory("projects/Alpha");
            nested.Current(("x", "Alpha", [nestedRoot]));
            nested.Session("0/1/2/3/4/5/6/7/8/9/deep.jsonl", nestedRoot);
            var deep = await nested.Catalog().DiscoverAsync();
            Check(deep.Truncated && deep.Items.Single().SessionCount == 0 && deep.Warnings.Count > 0);
        }),
        ("catalog oversized files remain visible but cannot be submitted", async _ =>
        {
            using var f = new CatalogFixture(); var root = f.Directory("projects/Alpha");
            var path = f.File("projects/Alpha/large.txt", "");
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None)) file.SetLength(CodexCatalog.MaxFileBytes + 1);
            f.Current(("x", "Alpha", [root])); var catalog = f.Catalog(); var p = (await catalog.DiscoverAsync()).Items.Single();
            var entry = (await catalog.BrowseAsync(p)).Items.Single();
            Check(entry.State == "too_large" && entry.Bytes == CodexCatalog.MaxFileBytes + 1);
            await Reject(() => catalog.ValidateFileAsync(p, path));
        }),
        ("catalog invalid home and no recognizable sources are explicit", async _ =>
        {
            using var f = new CatalogFixture();
            Check((await f.Catalog().DiscoverAsync()).Warnings.Count > 0);
            var missing = new CodexCatalog(Path.Combine(f.Root, "missing"), f.Data);
            var result = await missing.DiscoverAsync();
            Check(result.Items.Count == 0 && result.Warnings.Count > 0);
            var other = f.Directory("other-home"); var link = Path.Combine(f.Root, "home-link");
            await Junction(link, other);
            Check((await new CodexCatalog(link, f.Data).DiscoverAsync()).Warnings.Count > 0);
        })
    ];

    private static void Check(bool value) { if (!value) throw new Exception("catalog invariant failed"); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (IOException) { return; }
        throw new Exception("expected rejected catalog path");
    }
    private static async Task Cancelled(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { return; }
        throw new Exception("expected catalog cancellation");
    }
    private static string[] Snapshot(string root) => System.IO.Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
        .Order(StringComparer.Ordinal).Select(p =>
        {
            var attributes = System.IO.File.GetAttributes(p);
            var hash = attributes.HasFlag(FileAttributes.Directory) ? "directory" : Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(p)));
            return $"{p}|{attributes}|{System.IO.File.GetLastWriteTimeUtc(p):O}|{hash}";
        }).ToArray();

    private static async Task Junction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { System.IO.Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
        using var process = Process.Start(start)!; await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception("Cannot create catalog junction fixture: " + await process.StandardError.ReadToEndAsync());
    }

    // Unlike malformed-body fixtures, this fails on the READ itself, before any parsing.
    private sealed class PrefixBoundaryStream(string text, int allowedBytes, Action<int>? afterRead = null)
        : MemoryStream(Encoding.UTF8.GetBytes(text), writable: false)
    {
        internal int MaximumRequested { get; private set; }
        private int reads;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MaximumRequested = Math.Max(MaximumRequested, buffer.Length);
            if (buffer.Length != 1 || Position + buffer.Length > allowedBytes)
                throw new Exception("catalog reader requested bytes beyond its permitted prefix");
            var count = base.Read(buffer.Span);
            afterRead?.Invoke(++reads);
            return ValueTask.FromResult(count);
        }
    }

    private sealed class NoBufferedReadFileStream(string path)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536, FileOptions.Asynchronous)
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new Exception("buffered Read would prefetch dialogue");
        public override int Read(Span<byte> buffer) => throw new Exception("buffered Read would prefetch dialogue");
        public override int ReadByte() => throw new Exception("buffered ReadByte would prefetch dialogue");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => throw new Exception("buffered ReadAsync would prefetch dialogue");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw new Exception("buffered ReadAsync would prefetch dialogue");
    }

    private sealed class CatalogFixture : IDisposable
    {
        private static readonly string TestRoot = Path.Combine(Path.GetTempPath(), "AmifolioCatalogTests");
        internal string Root { get; } = Path.Combine(TestRoot, Guid.NewGuid().ToString("N"));
        internal string Home => Path.Combine(Root, "codex");
        internal string Data => Path.Combine(Root, "app-data");
        internal CatalogFixture() { System.IO.Directory.CreateDirectory(Home); System.IO.Directory.CreateDirectory(Data); }
        internal CodexCatalog Catalog(string? data = null) => new(Home, data ?? Data);
        internal string Directory(string relative) { var path = Path.GetFullPath(Path.Combine(Root, relative)); System.IO.Directory.CreateDirectory(path); return path; }
        internal string File(string relative, string text)
        {
            var path = Path.GetFullPath(Path.Combine(Root, relative)); System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, text, new UTF8Encoding(false)); return path;
        }
        internal void RawState(string text) => File("codex/.codex-global-state.json", text);
        internal void State(object value) => RawState(JsonSerializer.Serialize(value));
        internal void Current(params (string Id, string Name, string[] Roots)[] projects) => State(new Dictionary<string, object>
        {
            ["local-projects"] = projects.ToDictionary(p => p.Id, p => new { id = p.Id, name = p.Name, rootPaths = p.Roots })
        });
        internal string Session(string relative, string cwd, string instructions = "", string body = "") => File("codex/sessions/" + relative,
            JsonSerializer.Serialize(new { type = "session_meta", payload = new { cwd, base_instructions = instructions } }) + "\n" + body);
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(TestRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Unsafe catalog test cleanup");
            Clean(Root);
        }
        private static void Clean(string directory)
        {
            foreach (var entry in System.IO.Directory.EnumerateFileSystemEntries(directory))
            {
                var attrs = System.IO.File.GetAttributes(entry);
                if (attrs.HasFlag(FileAttributes.ReparsePoint))
                {
                    if (attrs.HasFlag(FileAttributes.Directory)) System.IO.Directory.Delete(entry); else System.IO.File.Delete(entry);
                }
                else if (attrs.HasFlag(FileAttributes.Directory)) Clean(entry);
                else System.IO.File.Delete(entry);
            }
            System.IO.Directory.Delete(directory);
        }
    }
}
