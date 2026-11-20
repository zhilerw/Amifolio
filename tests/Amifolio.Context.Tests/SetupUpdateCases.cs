using System.Security.Cryptography;
using System.Text;
using Amifolio.Context;

internal static class SetupUpdateCases
{
    internal static (string, Func<Task>)[] All =>
    [
        ("setup update previews managed MCP without IO and preserves surrounding text", Preview),
        ("setup update saves managed MCP with byte exact UTF16 backup and idempotence", Save),
        ("setup update appends fresh MCP and leaves legacy append semantics intact", Fresh),
        ("setup update rejects quoted example duplicate inline and customized MCP", Conflicts),
        ("setup update preserves unrelated multiline TOML and other servers", Multiline),
        ("setup update rejects altered preview unsafe new snippet and forged content", Tampered),
        ("setup update protects stale retargeted and canceled MCP snapshots", McpCas),
        ("setup update reads only standard skill path without creating files", ReadSkill),
        ("setup update replaces user edited owned skill only after explicit call with backup", ReplaceSkill),
        ("setup update rejects extra skill files and leaves backup diagnostics honest", ExtraSkillFiles),
        ("setup update protects skill identity retargeting stale snapshots and cancellation", SkillCas)
    ];
    private const string OldSkill = "---\nname: amifolio\ndescription: Synthetic older bundle.\n---\nOld user-owned workflow.\n";
    private const string NewSkill = "---\nname: amifolio\ndescription: Synthetic trusted bundle.\n---\nUpdated workflow.\n";
    private static Task Preview() => InFixture(async f =>
    {
        var before = "model = 'mine'\r\n# keep before\r\n";
        var after = "\r\n[other]\r\nvalue = 42\r\n";
        var text = before + Block(f.Old, "\r\n") + after;
        var snapshot = f.Snapshot(text);
        var entries = Entries(f.Root);
        var proposed = f.Service.PreviewMcpUpdate(snapshot, f.New);
        Check(proposed == before + Block(f.New, "\r\n") + after, "preview touched outside text or newlines");
        Check(entries.SequenceEqual(Entries(f.Root)), "preview performed writes");
        await Task.CompletedTask;
    });
    private static Task Save() => InFixture(async f =>
    {
        var text = "# 保留用户文本\r\n" + Block(f.Old, "\r\n") + "\r\n[other]\nflag=true\n";
        Directory.CreateDirectory(f.Service.CodexHome);
        await File.WriteAllTextAsync(f.Service.ConfigPath, text, Encoding.Unicode);
        var bytes = await File.ReadAllBytesAsync(f.Service.ConfigPath);
        var snapshot = await f.Service.ReadConfigAsync();
        var proposed = f.Service.PreviewMcpUpdate(snapshot, f.New);
        var backup = await f.Service.SaveMcpUpdateAsync(snapshot, proposed);
        Check(backup is not null && Path.GetDirectoryName(backup) == f.Service.CodexHome, "MCP backup missing or moved");
        var backupBytes = await File.ReadAllBytesAsync(backup!);
        Check(bytes.SequenceEqual(backupBytes), "backup bytes changed");
        var saved = await File.ReadAllBytesAsync(f.Service.ConfigPath);
        Check(saved[0] == 0xff && saved[1] == 0xfe && (await f.Service.ReadConfigAsync()).Content == proposed, "encoding or content lost");
        var current = await f.Service.ReadConfigAsync();
        var files = Entries(f.Root);
        Check(await f.Service.SaveMcpUpdateAsync(current, f.Service.PreviewMcpUpdate(current, f.New)) is null, "no-op created backup");
        Check(files.SequenceEqual(Entries(f.Root)), "no-op changed file set");
    });
    private static Task Fresh() => InFixture(async f =>
    {
        var missing = await f.Service.ReadConfigAsync();
        var proposed = f.Service.PreviewMcpUpdate(missing, f.New);
        Check(proposed == Block(f.New) + "\n", "fresh preview differs from append footprint");
        Check(await f.Service.SaveMcpUpdateAsync(missing, proposed) is null, "fresh install invented backup");
        var original = await f.Service.ReadConfigAsync();
        await Reject(() => f.Service.AppendMcpAsync(original, f.Old));
        Check((await f.Service.ReadConfigAsync()).Content == proposed, "legacy append overwrote newer managed block");
    });
    private static Task Conflicts() => InFixture(async f =>
    {
        var owned = Block(f.Old);
        var examples = new[]
        {
            owned + "\n" + owned, "# Amifolio MCP begin\n" + f.Old,
            "note = '''\n" + owned + "\n'''\n", "note = \"\"\"\n" + owned + "\n\"\"\"\n",
            "# example: " + owned + "\n", "  " + owned + "\n", owned.Replace("# Amifolio MCP end", "# Amifolio MCP end # example"),
            f.Old, "[mcp_servers]\namifolio={command='custom'}\n", "mcp_servers={amifolio={command='custom'}}\n",
            owned + "\n[mcp_servers.\"\\u0061mifolio\"]\ncommand='custom'\n",
            owned + "\n[profiles.mine.mcp_servers.amifolio]\ncommand='custom'\n",
            owned.Replace("# Amifolio MCP end", "enabled = true\n# Amifolio MCP end"),
            owned.Replace("# Amifolio MCP end", "env = {VALUE='mine'}\n# Amifolio MCP end"),
            owned.Replace("Amifolio.Bridge.exe", "Other.exe"), owned.Replace("\"mcp\"]", "\"mcp\", \"extra\"]"),
            owned.Replace("[mcp_servers.amifolio]", "[[mcp_servers.amifolio]]"),
            owned.Replace("[mcp_servers.amifolio]", "[mcp_servers.\"amifolio\"]")
        };
        foreach (var text in examples)
        {
            var files = Entries(f.Root);
            await Reject(() => Task.FromResult(f.Service.PreviewMcpUpdate(f.Snapshot(text), f.New)));
            Check(files.SequenceEqual(Entries(f.Root)), "rejected preview created files");
        }
    });
    private static Task Multiline() => InFixture(async f =>
    {
        var before = "note = '''\n[mcp_servers.amifolio]\nplain example, not a definition\n'''\n" +
            "values = [\n  'a', # unrelated comment\n  'b',\n]\n[mcp_servers.other]\ncommand='mine'\nargs = ['one']\n";
        var text = before + Block(f.Old) + "\n[unrelated]\nvalue={a=1,b=2}\n";
        var proposed = f.Service.PreviewMcpUpdate(f.Snapshot(text), f.New);
        Check(proposed == text.Replace(Block(f.Old), Block(f.New), StringComparison.Ordinal), "multiline/unrelated bytes changed");
        await Task.CompletedTask;
    });
    private static Task Tampered() => InFixture(async f =>
    {
        Directory.CreateDirectory(f.Service.CodexHome);
        await File.WriteAllTextAsync(f.Service.ConfigPath, "model='keep'\n" + Block(f.Old) + "\n");
        var snapshot = await f.Service.ReadConfigAsync();
        var proposed = f.Service.PreviewMcpUpdate(snapshot, f.New);
        var original = await File.ReadAllBytesAsync(f.Service.ConfigPath);
        await Reject(() => f.Service.SaveMcpUpdateAsync(snapshot, proposed.Replace("model='keep'", "model='changed'")));
        await Reject(() => f.Service.SaveMcpUpdateAsync(snapshot with { Content = snapshot.Content.Replace("model='keep'", "model='changed'") },
            proposed.Replace("model='keep'", "model='changed'")));
        foreach (var snippet in new[] { f.New + "enabled=true\n", f.New.Replace("Amifolio.Bridge.exe", "other.exe"),
            f.New.Replace("\"mcp\"]", "\"hook-stop\"]"), "# example\n" + f.New })
            await Reject(() => Task.FromResult(f.Service.PreviewMcpUpdate(snapshot, snippet)));
        var after = await File.ReadAllBytesAsync(f.Service.ConfigPath);
        Check(original.SequenceEqual(after), "tampering changed real config");
        Check(!Directory.EnumerateFiles(f.Service.CodexHome, "*.bak").Any(), "rejected edit made backup");
    });
    private static Task McpCas() => InFixture(async f =>
    {
        Directory.CreateDirectory(f.Service.CodexHome);
        await File.WriteAllTextAsync(f.Service.ConfigPath, Block(f.Old) + "\n");
        var snapshot = await f.Service.ReadConfigAsync(); var proposed = f.Service.PreviewMcpUpdate(snapshot, f.New);
        await Reject(() => f.Service.SaveMcpUpdateAsync(snapshot with { Path = Path.Combine(f.Root, "outside.toml") }, proposed));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Canceled(() => f.Service.SaveMcpUpdateAsync(snapshot, proposed, cancel.Token));
        await File.AppendAllTextAsync(f.Service.ConfigPath, "# external edit\n");
        var current = await File.ReadAllBytesAsync(f.Service.ConfigPath);
        await Reject(() => f.Service.SaveMcpUpdateAsync(snapshot, proposed));
        var after = await File.ReadAllBytesAsync(f.Service.ConfigPath);
        Check(current.SequenceEqual(after) && !File.Exists(Path.Combine(f.Root, "outside.toml")), "CAS/target protection failed");
    });
    private static Task ReadSkill() => InFixture(async f =>
    {
        var before = Entries(f.Root); var missing = await f.Service.ReadSkillAsync();
        Check(!missing.Exists && missing.Path == Path.Combine(f.Service.SkillDirectory, "SKILL.md"), "read targeted wrong skill");
        Check(before.SequenceEqual(Entries(f.Root)), "missing read created directories");
        await f.Service.InstallSkillAsync(OldSkill);
        Check((await f.Service.ReadSkillAsync()).Content == OldSkill, "standard skill read mismatch");
    });
    private static Task ReplaceSkill() => InFixture(async f =>
    {
        await f.Service.InstallSkillAsync(OldSkill);
        var path = Path.Combine(f.Service.SkillDirectory, "SKILL.md");
        var customized = OldSkill + "\nUser's explicit customization.\n";
        await File.WriteAllTextAsync(path, customized, Encoding.Unicode);
        var snapshot = await f.Service.ReadSkillAsync(); var original = await File.ReadAllBytesAsync(path);
        // Legacy installer must keep refusing changed user text; replacement is a separate explicit API.
        await Reject(() => f.Service.InstallSkillAsync(NewSkill));
        Check((await f.Service.ReadSkillAsync()).Content == customized, "read/install silently replaced custom content");
        var backup = await f.Service.ReplaceSkillAsync(snapshot, NewSkill);
        Check(backup is not null && Path.GetDirectoryName(backup) == f.Service.SkillDirectory, "skill backup moved or missing");
        var backupBytes = await File.ReadAllBytesAsync(backup!);
        Check(original.SequenceEqual(backupBytes), "skill backup lost user bytes");
        var saved = await File.ReadAllBytesAsync(path);
        Check(saved[0] == 0xff && saved[1] == 0xfe && (await f.Service.ReadSkillAsync()).Content == NewSkill, "skill update encoding/content mismatch");
    });
    private static Task ExtraSkillFiles() => InFixture(async f =>
    {
        await f.Service.InstallSkillAsync(OldSkill);
        var extra = Path.Combine(f.Service.SkillDirectory, "user-notes.txt");
        await File.WriteAllTextAsync(extra, "Do not touch.");
        var snapshot = await f.Service.ReadSkillAsync();
        await Reject(() => f.Service.ReplaceSkillAsync(snapshot, NewSkill));
        Check(await File.ReadAllTextAsync(extra) == "Do not touch." && (await f.Service.ReadSkillAsync()).Content == OldSkill, "extra file or skill changed");
        File.Delete(extra);
        var backup = await f.Service.ReplaceSkillAsync(snapshot, NewSkill);
        Check(backup is not null && File.Exists(backup), "backup missing");
        var checks = await f.Service.InspectEnvironmentAsync(Path.Combine(f.Root, "bridge", "Amifolio.Bridge.exe"),
            Path.Combine(f.Root, "data"), NewSkill, Path.Combine(f.Root, "AGENTS.md"));
        Check(checks.Single(c => c.Name == "Skill 安装").State == "conflict", "backup hidden from existing diagnostics");
        var current = await f.Service.ReadSkillAsync();
        await Reject(() => f.Service.ReplaceSkillAsync(current, OldSkill));
        Check(File.Exists(backup!), "replacement deleted its backup");
    });
    private static Task SkillCas() => InFixture(async f =>
    {
        await Reject(() => f.Service.ReplaceSkillAsync(f.Snapshot("" ) with { Path = Path.Combine(f.Service.SkillDirectory, "SKILL.md"), Exists = false }, NewSkill));
        await f.Service.InstallSkillAsync(OldSkill); var snapshot = await f.Service.ReadSkillAsync();
        await Reject(() => f.Service.ReplaceSkillAsync(snapshot with { Path = f.Service.ConfigPath }, NewSkill));
        foreach (var text in new[] { NewSkill.Replace("name: amifolio", "name: another"), "---\nname: another\n---\nname: amifolio\n", NewSkill.Replace("description:", "name: amifolio\ndescription:") })
            await Reject(() => f.Service.ReplaceSkillAsync(snapshot, text));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Canceled(() => f.Service.ReplaceSkillAsync(snapshot, NewSkill, cancel.Token));
        await Reject(() => f.Service.ReplaceSkillAsync(snapshot with { Content = OldSkill + "forged" }, NewSkill));
        await File.AppendAllTextAsync(snapshot.Path, "external edit");
        var current = await File.ReadAllBytesAsync(snapshot.Path);
        await Reject(() => f.Service.ReplaceSkillAsync(snapshot, NewSkill));
        var after = await File.ReadAllBytesAsync(snapshot.Path);
        Check(current.SequenceEqual(after), "stale skill snapshot overwrote user edit");
    });

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "amifolio-setup-update-" + Guid.NewGuid().ToString("N"));
        internal CodexIntegrationService Service { get; }
        internal string Old => Service.McpSnippet(Path.Combine(Root, "old-package", "Amifolio.Bridge.exe"), Path.Combine(Root, "old-data"));
        internal string New => Service.McpSnippet(Path.Combine(Root, "new-package", "Amifolio.Bridge.exe"), Path.Combine(Root, "new-data"));
        internal Fixture() { Directory.CreateDirectory(Root); Service = new(Path.Combine(Root, "codex"), Path.Combine(Root, "user")); }
        internal AgentsFileSnapshot Snapshot(string text) => new(Service.ConfigPath, text, "preview-only", true);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
    private static string Block(string snippet, string nl = "\n") => "# Amifolio MCP begin" + nl + snippet.Replace("\n", nl) + "# Amifolio MCP end";
    private static string[] Entries(string root) => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
    private static async Task InFixture(Func<Fixture, Task> run) { using var fixture = new Fixture(); await run(fixture); }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (Exception error) when (error is IOException or ArgumentException) { return; }
        throw new InvalidOperationException("Expected protected setup rejection.");
    }
    private static async Task Canceled(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Cancellation did not stop setup write.");
    }
}
