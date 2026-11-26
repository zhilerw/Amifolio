using System.Diagnostics;
using System.Text;
using Amifolio.Context;

internal static class NativeMemoryCases
{
    internal static (string, Func<Task>)[] All =>
    [
        ("native memory reads v1 contents path and file mtime", ReadV1),
        ("native memory reads v2 overview without a v1 fallback", ReadV2),
        ("native memory coexistence and config do not establish effective version", Coexistence),
        ("native memory missing and empty homes remain unchanged", NoData),
        ("native memory rejects an explicit invalid home without falling back", InvalidHome),
        ("native memory exposes v2 notes as inputs without asserting consolidation", Notes),
        ("native memory uses exact recorded cwd for project matching", ProjectMatching),
        ("native memory project quota counts matches after scanning foreign sources", ProjectQuota),
        ("native memory reports recognized missing sources and ignores outside pointers", SourceBoundary),
        ("native memory rejects reparse homes and source folders and hard link sources", LinkedSources),
        ("native memory bounds enumeration and previews and rejects invalid text", Limits),
        ("native memory refresh reports changed content mtime and removed sources", Refresh),
        ("native memory respects cancellation", Cancellation)
    ];

    private static async Task ReadV1()
    {
        using var f = new Fixture();
        const string body = "# 用户记忆\r\n字体与配色\r\n";
        var path = f.Write("memories/MEMORY.md", body);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 5, 12, 30, 0, DateTimeKind.Utc));
        f.Write("memories/memory_summary.md", "v1\n# Overview\n");
        var result = await f.Read();
        var memory = Source(result.V1, NativeMemorySourceKind.Memory);
        Check(result.V1.Status == NativeMemoryReadStatus.Available && result.V2.Status == NativeMemoryReadStatus.Missing,
            "Expected available v1 and missing v2.");
        Check(memory.Status == NativeMemoryReadStatus.Available && memory.Path == path && memory.Content == body &&
            memory.LastWriteTimeUtc == File.GetLastWriteTimeUtc(path) && !memory.IsTruncated,
            "Expected exact text, verified path and filesystem timestamp.");
        Check(result.SelectedVersion is null && result.SelectionEvidence is null, "Files alone do not show runtime selection.");
    }

    private static async Task ReadV2()
    {
        using var f = new Fixture();
        const string body = "v1\n## User Profile\nV2 overview using Codex's v1 format marker.\n";
        var path = f.Write("memories_v2/memory_summary.md", body, new UTF8Encoding(true));
        f.Write("memories_v2/MEMORY.md", "non-v2 artifact");
        var result = await f.Read();
        var overview = Source(result.V2, NativeMemorySourceKind.Overview);
        Check(result.V2.Version == NativeMemoryVersion.V2 && result.V1.Status == NativeMemoryReadStatus.Missing &&
            overview.Path == path && overview.Content == body, "V2 must read its own overview and preserve its text.");
        Check(result.V2.Sources.All(x => x.Kind != NativeMemorySourceKind.Memory), "V2 has no MEMORY.md handbook.");
        Check(result.SelectedVersion is null && result.V2Readiness == NativeMemoryReadiness.Unsupported,
            "File presence and the format marker must not imply selection or readiness.");
    }

    private static async Task Coexistence()
    {
        using var f = new Fixture();
        f.Write("memories/memory_summary.md", "v1\nlegacy overview");
        f.Write("memories_v2/memory_summary.md", "v1\nnew overview");
        foreach (var version in new[] { "v1", "v2" })
        {
            f.Write("config.toml", $"[memories]\nversion = \"{version}\"\ndual_write = true\n");
            var result = await f.Read();
            Check(Source(result.V1, NativeMemorySourceKind.Overview).Content == "v1\nlegacy overview" &&
                Source(result.V2, NativeMemorySourceKind.Overview).Content == "v1\nnew overview", "Coexisting stores must stay distinct.");
            Check(result.SelectedVersion is null && result.SelectionEvidence is null,
                "Local files do not prove any loaded thread's effective config.");
        }
    }

    private static async Task NoData()
    {
        using var f = new Fixture();
        var missing = await f.Read();
        Check(!Directory.Exists(f.Home) && missing.CodexHome == f.Home && missing.V1.Status == NativeMemoryReadStatus.Missing &&
            missing.V2.Status == NativeMemoryReadStatus.Missing, "Missing home should return no data without creating it.");
        Directory.CreateDirectory(Path.Combine(f.Home, "memories"));
        var empty = await f.Read();
        Check(empty.V1.Status == NativeMemoryReadStatus.Missing && empty.V1.Sources.All(x => x.Content is null &&
            x.LastWriteTimeUtc is null) && !Directory.EnumerateFileSystemEntries(Path.Combine(f.Home, "memories")).Any(),
            "An empty store should have no content or timestamps and remain empty.");
    }

    private static async Task InvalidHome()
    {
        foreach (var path in new[] { "", "relative-codex-home", @"\\untrusted.invalid\share\codex", @"\\?\C:\codex" })
        {
            var snapshot = await new NativeMemoryService(path).ReadAsync();
            Check(snapshot.CodexHome == path && snapshot.V1.Status == NativeMemoryReadStatus.Untrusted &&
                snapshot.V2.Status == NativeMemoryReadStatus.Untrusted && snapshot.V1.Sources.Count == 0 && snapshot.V2.Sources.Count == 0,
                "An explicit invalid home must not produce discoveries from another location.");
        }
    }

    private static async Task Notes()
    {
        using var f = new Fixture();
        var notePath = f.Write("memories_v2/extensions/ad_hoc/notes/2026-11-23T10-00-00-font.md", "Use a serif title.");
        f.Write("memories_v2/extensions/ad_hoc/instructions.md", "extension instructions");
        f.Write("memories_v2/extensions/other/notes/other.md", "unrelated extension");
        f.Write("memories/extensions/ad_hoc/notes/v1.md", "not part of the v2 note display");
        var result = await f.Read();
        var note = Source(result.V2, NativeMemorySourceKind.PendingNote);
        Check(result.V2.Status == NativeMemoryReadStatus.Available && note.Path == notePath && note.Content == "Use a serif title." &&
            note.LastWriteTimeUtc == File.GetLastWriteTimeUtc(notePath) && note.ProjectMatch == NativeMemoryProjectMatch.Unknown,
            "Expected the verified note input and its metadata.");
        Check(Source(result.V2, NativeMemorySourceKind.Overview).Status == NativeMemoryReadStatus.Missing &&
            result.V1.Sources.All(x => x.Kind != NativeMemorySourceKind.PendingNote), "An input is not a consolidated summary.");
    }

    private static async Task ProjectMatching()
    {
        using var f = new Fixture();
        var target = Path.Combine(f.Root, "repo");
        var similar = target + "-other";
        var header = "thread_id: 019a0000-0000-7000-8000-000000000001\nupdated_at: 2026-11-01T09:00:00Z\n" +
            "rollout_path: " + Path.Combine(f.Home, "sessions", "never-open.jsonl") + "\n";
        var matching = f.Write("memories_v2/rollout_summaries/match.md", header + $"cwd: {target}\n\nSummary\n");
        var different = f.Write("memories_v2/rollout_summaries/different.md", header + $"cwd: {similar}\n\n{target}\n");
        var unverified = f.Write("memories_v2/rollout_summaries/unverified.md", header + $"\n# Prose\ncwd: {target}\n");
        var relative = f.Write("memories_v2/rollout_summaries/relative.md", header + "cwd: repo\n\n");
        f.Write("memories_v2/memory_summary.md", $"v1\n## {target}\n");
        var result = await f.Read(target + Path.DirectorySeparatorChar);
        var sources = result.V2.Sources.ToDictionary(x => x.Path);
        Check(sources[matching].ProjectMatch == NativeMemoryProjectMatch.Matched &&
            sources[different].ProjectMatch == NativeMemoryProjectMatch.Different &&
            sources[unverified].ProjectMatch == NativeMemoryProjectMatch.Unknown &&
            sources[relative].ProjectMatch == NativeMemoryProjectMatch.Unknown &&
            Source(result.V2, NativeMemorySourceKind.Overview).ProjectMatch == NativeMemoryProjectMatch.Unknown,
            "Only the exact normalized cwd in the Codex source header is evidence.");
        Check((await f.Read()).V2.Sources.All(x => x.ProjectMatch == NativeMemoryProjectMatch.Unknown),
            "Matching must be relative to an explicitly requested project.");
    }

    private static async Task ProjectQuota()
    {
        using var f = new Fixture();
        var project = Path.Combine(f.Root, "repo");
        const string header = "thread_id: 019a0000-0000-7000-8000-000000000001\nupdated_at: 2026-11-01T09:00:00Z\nrollout_path: unused.jsonl\n";
        for (var i = 0; i < 60; i++)
            f.Write($"memories_v2/rollout_summaries/a-foreign-{i:D3}.md", header + $"cwd: {project}-other\n\nForeign\n");
        var match = f.Write("memories_v2/rollout_summaries/z-match.md", header + $"cwd: {project}\n\nMatched\n");

        var global = (await f.Read()).V2;
        Check(global.IsLimited && global.Sources.Count(x => x.Kind == NativeMemorySourceKind.RolloutSummary) == 48 &&
            global.Sources.All(x => x.Path != match), "The global source budget should remain 48.");
        var scoped = (await f.Read(project)).V2;
        Check(!scoped.IsLimited && scoped.Sources.Any(x => x.Path == match &&
            x.ProjectMatch == NativeMemoryProjectMatch.Matched && x.Content!.EndsWith("Matched\n", StringComparison.Ordinal)),
            "A project match beyond the first 48 foreign names must still be found with a complete scan.");

        for (var i = 0; i < 260; i++)
            f.Write($"memories_v2/rollout_summaries/b-foreign-{i:D3}.md", header + $"cwd: {project}-other\n\nForeign\n");
        var incomplete = (await f.Read(project)).V2;
        Check(incomplete.IsLimited, "A capped project scan must report that its results are incomplete.");
    }

    private static async Task SourceBoundary()
    {
        using var f = new Fixture();
        var secret = f.Write("secrets/private.md", "OUTSIDE_MARKER");
        f.Write("sessions/raw.jsonl", "ROLLOUT_MARKER");
        f.Write("memories_v2/raw_memories.md", "RAW_MEMORIES_MARKER");
        f.Write("memories_v2/rollout_summaries/nested/hidden.md", "NESTED_MARKER");
        f.Write("memories_v2/rollout_summaries/wrong.jsonl", "JSONL_MARKER");
        f.Write("memories_v2/memory_summary.md", "v1\n" +
            "- rollout_summaries/gone.md — observed previously; thread_id=019a0000-0000-7000-8000-000000000001\n" +
            "- rollout_summaries/../../secrets/private.md — outside\n" +
            $"- {secret} — absolute\n- rollout_summaries/nested/hidden.md — nested\n");
        f.Write("memories/MEMORY.md", "### rollout_summary_files\n" +
            "- old.md (cwd=C:\\old, rollout_path=some.jsonl, updated_at=2026-11-01T09:00:00Z)\n");
        var result = await f.Read();
        var gone = Source(result.V2, NativeMemorySourceKind.RolloutSummary);
        var old = Source(result.V1, NativeMemorySourceKind.RolloutSummary);
        Check(gone.Path == Path.Combine(f.Home, "memories_v2", "rollout_summaries", "gone.md") &&
            old.Path == Path.Combine(f.Home, "memories", "rollout_summaries", "old.md") &&
            new[] { gone, old }.All(x => x.Status == NativeMemoryReadStatus.Missing && x.Content is null && x.LastWriteTimeUtc is null),
            "Official one-segment summary pointers may report a missing source without loading any raw or arbitrary path.");
    }

    private static async Task LinkedSources()
    {
        using var f = new Fixture();
        var outside = Path.Combine(f.Root, "outside");
        var target = Path.Combine(outside, "memories", "MEMORY.md");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "unsafe content");
        Link(f.Home, outside, directory: true);
        try
        {
            var linkedHome = await f.Read();
            Check(linkedHome.V1.Status == NativeMemoryReadStatus.Untrusted && linkedHome.V2.Status == NativeMemoryReadStatus.Untrusted,
                "Both stores must reject a linked Codex home.");
        }
        finally { Directory.Delete(f.Home); }
        f.Write("memories_v2/memory_summary.md", "v1\n- rollout_summaries/MEMORY.md — source\n");
        var linkedDirectory = Path.Combine(f.Home, "memories_v2", "rollout_summaries");
        Link(linkedDirectory, Path.GetDirectoryName(target)!, directory: true);
        try
        {
            var linkedFolder = await f.Read();
            var source = Source(linkedFolder.V2, NativeMemorySourceKind.RolloutSummary);
            Check(source.Status == NativeMemoryReadStatus.Untrusted && source.Content is null && source.LastWriteTimeUtc is null,
                "A cited name in a linked folder must remain unread.");
        }
        finally { Directory.Delete(linkedDirectory); }
        if (OperatingSystem.IsWindows())
        {
            var v1 = Path.Combine(f.Home, "memories");
            Directory.CreateDirectory(v1);
            Link(Path.Combine(v1, "MEMORY.md"), target, directory: false);
            var hardLink = Source((await f.Read()).V1, NativeMemorySourceKind.Memory);
            Check(hardLink.Status == NativeMemoryReadStatus.Untrusted && hardLink.Content is null,
                "A hard link can alias text outside the approved store.");
        }
    }

    private static async Task Limits()
    {
        using var f = new Fixture();
        var body = string.Concat(Enumerable.Repeat("甲乙丙😀\n", 100_000));
        f.Write("memories_v2/memory_summary.md", body);
        var corrupt = f.Write("memories_v2/rollout_summaries/bad.md", "");
        File.WriteAllBytes(corrupt, [0xff, 0x80, 0x80]);
        var bounded = await f.Read();
        var overview = Source(bounded.V2, NativeMemorySourceKind.Overview);
        Check(overview.IsTruncated && overview.Content is { Length: > 0 } && body.StartsWith(overview.Content, StringComparison.Ordinal) &&
            Source(bounded.V2, NativeMemorySourceKind.RolloutSummary).Status == NativeMemoryReadStatus.Unreadable,
            "Preview must be a bounded valid UTF-8 prefix and malformed text must be unavailable.");
        for (var i = 0; i < 300; i++) f.Write($"memories_v2/rollout_summaries/{i:D3}.md", "small");
        var many = await f.Read();
        Check(many.V2.IsLimited && many.V2.Sources.Count(x => x.Kind == NativeMemorySourceKind.RolloutSummary) < 300,
            "Enumeration must have a visible source count limit.");
    }

    private static async Task Refresh()
    {
        using var f = new Fixture();
        const string pointer = "v1\n- rollout_summaries/changed.md — source\n";
        f.Write("memories_v2/memory_summary.md", pointer);
        var path = f.Write("memories_v2/rollout_summaries/changed.md", "original");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
        var service = new NativeMemoryService(f.Home);
        var original = Source((await service.ReadAsync()).V2, NativeMemorySourceKind.RolloutSummary);
        File.WriteAllText(path, "updated");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc));
        var updated = Source((await service.ReadAsync()).V2, NativeMemorySourceKind.RolloutSummary);
        Check(original.Content == "original" && updated.Content == "updated" && updated.Path == path &&
            updated.LastWriteTimeUtc == original.LastWriteTimeUtc?.AddDays(1) && updated.LastWriteTimeUtc == File.GetLastWriteTimeUtc(path),
            "Refreshing must report the verified source's updated bytes and mtime.");
        File.Delete(path);
        var missing = Source((await service.ReadAsync()).V2, NativeMemorySourceKind.RolloutSummary);
        Check(missing.Path == path && missing.Status == NativeMemoryReadStatus.Missing && missing.Content is null &&
            missing.LastWriteTimeUtc is null, "A stale pointer must not retain old content or timestamps.");
    }

    private static async Task Cancellation()
    {
        using var f = new Fixture();
        try { await new NativeMemoryService(f.Home).ReadAsync(cancellationToken: new CancellationToken(canceled: true)); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation.");
    }

    private static NativeMemorySource Source(NativeMemoryStoreSnapshot store, NativeMemorySourceKind kind) =>
        store.Sources.Single(x => x.Kind == kind);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Link(string path, string target, bool directory)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(path, target); return; }
        using var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "mklink", directory ? "/J" : "/H", path, target }
        }) ?? throw new InvalidOperationException("Could not create link fixture.");
        process.WaitForExit();
        Check(process.ExitCode == 0, "Could not create link fixture.");
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Directory.CreateTempSubdirectory("amifolio-native-memory-").FullName;
        public string Home => Path.Combine(Root, "codex");
        public Task<NativeMemorySnapshot> Read(string? projectPath = null) => new NativeMemoryService(Home).ReadAsync(projectPath);

        public string Write(string relativePath, string content, Encoding? encoding = null)
        {
            var path = Path.Combine(Home, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content, encoding ?? new UTF8Encoding(false));
            return path;
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
