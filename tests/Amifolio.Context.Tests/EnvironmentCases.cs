using System.Text;
using Amifolio.Context;

internal static class EnvironmentCases
{
    internal static (string Name, Func<Task> Run)[] All =>
    [
        ("environment missing setup is read-only and host unverified", Missing),
        ("environment matching setup only reports configured and local-pass", Matching),
        ("environment different bridge and data root are conflicts without secret output", ConfigConflict),
        ("environment duplicate inline disabled and ambiguous MCP are conflicts", AmbiguousConfig),
        ("environment TOML comments quotes and multiline decoys cannot spoof MCP", TomlForms),
        ("environment missing runtime never implies executable readiness", Runtime),
        ("environment duplicate old skill and changed target content are conflicts", Skills),
        ("environment AGENTS user rules alone are not configured", UserRules),
        ("environment AGENTS wrong root malformed block override and absent target", AgentsTargets),
        ("environment inspection preserves every byte and directory entry", ReadOnly),
        ("environment cancellation and overlong inputs are bounded", Bounds),
        ("environment linked setup paths are rejected without following", LinkedPaths),
        ("environment inaccessible config is isolated from other checks", InaccessibleConfig),
        ("environment project entry and multiline argument configuration", ProjectEntry)
    ];

    private static async Task Missing()
    {
        using var f = new EnvironmentFixture();
        var before = f.Files();
        var checks = await f.Inspect();
        Check(checks.Count == 8, "expected eight independent checks");
        State(checks, "MCP 配置", "missing"); State(checks, "桥接组件", "missing");
        State(checks, "运行库文件", "missing"); State(checks, "Skill 安装", "missing");
        State(checks, "AGENTS 入口", "missing"); State(checks, "入口目标", "missing");
        Honest(checks); Check(before.SequenceEqual(f.Files()), "inspection created entries");
    }

    private static async Task Matching()
    {
        using var f = new EnvironmentFixture(); await f.Setup();
        var checks = await f.Inspect();
        State(checks, "MCP 配置", "configured"); State(checks, "桥接组件", "local-pass");
        State(checks, "运行库文件", "local-pass"); State(checks, "Skill 安装", "configured");
        State(checks, "旧版 Skill", "local-pass"); State(checks, "AGENTS 入口", "configured");
        State(checks, "入口目标", "local-pass"); Honest(checks);
    }

    private static async Task ConfigConflict()
    {
        using var f = new EnvironmentFixture(); await f.Setup();
        foreach (var snippet in new[] { f.Service.McpSnippet(f.Bridge, f.Root + "-other"),
            f.Service.McpSnippet(Path.Combine(f.Root, "other.exe"), f.Data) })
        {
            await File.WriteAllTextAsync(f.Service.ConfigPath, "api_key='CONFIG_SECRET'\n" + snippet);
            var checks = await f.Inspect(); State(checks, "MCP 配置", "conflict");
            Check(!string.Join("", checks).Contains("CONFIG_SECRET", StringComparison.Ordinal), "config value leaked"); Honest(checks);
        }
    }

    private static async Task AmbiguousConfig()
    {
        using var f = new EnvironmentFixture(); await f.Setup(); var valid = f.Service.McpSnippet(f.Bridge, f.Data);
        foreach (var text in new[] { valid + valid, "[mcp_servers]\namifolio={command='SECRET'}\n", valid + "enabled=false\n",
            "mcp_servers.amifolio.command='SECRET'\n", valid + "[mcp_servers.amifolio.env]\nAPI_KEY='SECRET'\n",
            valid + "command='SECRET'\n", valid + "url='SECRET'\n" })
        { await File.WriteAllTextAsync(f.Service.ConfigPath, text); State(await f.Inspect(), "MCP 配置", "conflict"); }
        await File.WriteAllTextAsync(f.Service.ConfigPath, "model='unclosed\n");
        State(await f.Inspect(), "MCP 配置", "invalid");
    }

    private static async Task TomlForms()
    {
        using var f = new EnvironmentFixture(); await f.Setup();
        var command = f.Bridge.Replace("\\", "/"); var data = f.Data.Replace("\\", "/");
        await File.WriteAllTextAsync(f.Service.ConfigPath,
            "model='PRIVATE_MODEL'\n# irrelevant\n[mcp_servers.\"\\u0061mifolio\"] # installed\ncommand='" + command + "'\nargs=['--data-root', '" + data + "', 'mcp'] # comment\nenabled=true\n", Encoding.Unicode);
        State(await f.Inspect(), "MCP 配置", "configured");
        await File.WriteAllTextAsync(f.Service.ConfigPath, "note='''\n" + f.Service.McpSnippet(f.Bridge, f.Data) + "'''\n");
        State(await f.Inspect(), "MCP 配置", "missing");
        await File.WriteAllTextAsync(f.Service.ConfigPath, f.Service.McpSnippet(f.Bridge, f.Data) + "[profiles.mine.mcp_servers.amifolio]\ncommand='override'\n");
        State(await f.Inspect(), "MCP 配置", "conflict");
        await File.WriteAllTextAsync(f.Service.ConfigPath, f.Service.McpSnippet(f.Bridge, f.Data) + "not a TOML statement\n");
        State(await f.Inspect(), "MCP 配置", "invalid");
    }

    private static async Task Runtime()
    {
        using var f = new EnvironmentFixture(); await f.Setup();
        File.Delete(Path.Combine(Path.GetDirectoryName(f.Bridge)!, "Amifolio.Bridge.runtimeconfig.json"));
        State(await f.Inspect(), "运行库文件", "missing");
        File.Delete(f.Bridge); Directory.CreateDirectory(f.Bridge);
        State(await f.Inspect(), "桥接组件", "missing");
    }

    private static async Task Skills()
    {
        using var f = new EnvironmentFixture(); await f.Setup();
        foreach (var old in new[] { Path.Combine(f.Service.CodexHome, "skills", "amifolio"), Path.Combine(f.Root, ".codex", "skills", "amifolio") })
        {
            Directory.CreateDirectory(old); State(await f.Inspect(), "旧版 Skill", "conflict"); Directory.Delete(old);
        }
        await File.AppendAllTextAsync(Path.Combine(f.Service.SkillDirectory, "SKILL.md"), "\nuser modification");
        State(await f.Inspect(), "Skill 安装", "conflict");
        await File.WriteAllTextAsync(Path.Combine(f.Service.SkillDirectory, "SKILL.md"), EnvironmentFixture.Skill);
        await File.WriteAllTextAsync(Path.Combine(f.Service.SkillDirectory, "untrusted.ps1"), "DO_NOT_EXECUTE");
        State(await f.Inspect(), "Skill 安装", "conflict");
    }

    private static async Task UserRules()
    {
        using var f = new EnvironmentFixture(); await f.Setup();
        await File.WriteAllTextAsync(f.Agents, "# 用户规则\nUSER_RULE_SECRET\nAmifolio: 普通文字\n");
        var checks = await f.Inspect(); State(checks, "AGENTS 入口", "missing"); Honest(checks);
        Check(!string.Join("", checks).Contains("USER_RULE_SECRET", StringComparison.Ordinal), "user rule leaked");
    }

    private static async Task AgentsTargets()
    {
        using var f = new EnvironmentFixture(); await f.Setup(); var service = new AgentsFileService();
        await File.WriteAllTextAsync(f.Agents, service.PreviewManagedBlock(new(f.Agents, "", "", false), Path.Combine(f.Root, "other-data", "context", "global"), true));
        State(await f.Inspect(), "AGENTS 入口", "conflict"); State(await f.Inspect(), "入口目标", "conflict");
        await File.WriteAllTextAsync(f.Agents, "<!-- folio:begin scope=x -->\n"); State(await f.Inspect(), "AGENTS 入口", "invalid");
        await f.AgentsSetup(); File.Delete(f.Entry); State(await f.Inspect(), "AGENTS 入口", "configured"); State(await f.Inspect(), "入口目标", "missing");
        await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(f.Agents)!, "AGENTS.override.md"), "OVERRIDE_SECRET");
        State(await f.Inspect(), "AGENTS 入口", "conflict");
    }

    private static async Task ReadOnly()
    {
        using var f = new EnvironmentFixture(); await f.Setup();
        await File.AppendAllTextAsync(f.Service.ConfigPath, "# SECRET_SENTINEL\n", Encoding.Unicode);
        await File.WriteAllTextAsync(Path.Combine(f.Root, "auth.json"), "KEY_FILE_DO_NOT_READ");
        await File.WriteAllTextAsync(f.Entry, "ORIGINAL_BODY_DO_NOT_READ");
        var entries = f.Files(); var bytes = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        await using (var keyGuard = new FileStream(Path.Combine(f.Root, "auth.json"), FileMode.Open, FileAccess.Read, FileShare.None))
        await using (var targetGuard = new FileStream(f.Entry, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            foreach (var checks in new[] { await f.Inspect(), await f.Inspect() })
            {
                State(checks, "入口目标", "local-pass"); State(checks, "MCP 配置", "configured");
                var output = string.Join("", checks);
                foreach (var secret in new[] { "SECRET_SENTINEL", "KEY_FILE_DO_NOT_READ", "ORIGINAL_BODY_DO_NOT_READ", "USER_RULE_SECRET" })
                    Check(!output.Contains(secret, StringComparison.Ordinal), "private content in result");
                Honest(checks);
            }
        }
        Check(entries.SequenceEqual(f.Files()), "inspection changed entries");
        foreach (var file in bytes)
        {
            var after = await File.ReadAllBytesAsync(file.Key);
            Check(file.Value.SequenceEqual(after), "inspection changed bytes");
        }
    }

    private static async Task Bounds()
    {
        using var f = new EnvironmentFixture(); await f.Setup(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Throws<OperationCanceledException>(() => f.Service.InspectEnvironmentAsync(f.Bridge, f.Data, EnvironmentFixture.Skill, f.Agents, cancel.Token));
        await Throws<ArgumentException>(() => f.Service.InspectEnvironmentAsync(new string('x', 40000), f.Data, EnvironmentFixture.Skill, f.Agents));
        await Throws<ArgumentException>(() => f.Service.InspectEnvironmentAsync(f.Bridge, f.Data, EnvironmentFixture.Skill, Path.Combine(f.Root, "auth.json")));
        await Throws<ArgumentException>(() => f.Service.InspectEnvironmentAsync(@"\\invalid-host\share\Amifolio.Bridge.exe", f.Data, EnvironmentFixture.Skill, f.Agents));
        State(await f.Service.InspectEnvironmentAsync(f.Bridge, f.Data, new string('x', 262145), f.Agents), "Skill 安装", "invalid");
        await File.WriteAllTextAsync(f.Service.ConfigPath, new string('x', 4 * 1024 * 1024 + 1)); State(await f.Inspect(), "MCP 配置", "invalid");
        await File.WriteAllTextAsync(f.Agents, new string('x', 4 * 1024 * 1024 + 1)); State(await f.Inspect(), "AGENTS 入口", "invalid");
        await File.WriteAllTextAsync(Path.Combine(f.Service.SkillDirectory, "SKILL.md"), new string('x', 4 * 1024 * 1024 + 1)); State(await f.Inspect(), "Skill 安装", "invalid");
    }

    private static async Task LinkedPaths()
    {
        using var f = new EnvironmentFixture(); await f.Setup();
        var actual = Path.Combine(f.Root, "actual"); Directory.Move(f.Service.SkillDirectory, actual);
        // Directory junctions on Windows do not require symbolic-link privileges.
        if (OperatingSystem.IsWindows())
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            { UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "/c", "mklink", "/J", f.Service.SkillDirectory, actual } })!;
            await process.WaitForExitAsync(); Check(process.ExitCode == 0, "could not create isolated test junction");
        }
        else Directory.CreateSymbolicLink(f.Service.SkillDirectory, actual);
        try { State(await f.Inspect(), "Skill 安装", "unreadable"); }
        finally { Directory.Delete(f.Service.SkillDirectory); }
    }

    private static async Task InaccessibleConfig()
    {
        using var f = new EnvironmentFixture(); await f.Setup();
        await using var guard = new FileStream(f.Service.ConfigPath, FileMode.Open, FileAccess.Read, FileShare.None);
        var checks = await f.Inspect(); State(checks, "MCP 配置", "unreadable");
        State(checks, "Skill 安装", "configured"); State(checks, "AGENTS 入口", "configured"); Honest(checks);
    }

    private static async Task ProjectEntry()
    {
        using var f = new EnvironmentFixture(); await f.Setup();
        var context = Path.Combine(f.Data, "context", "project-" + new string('a', 64)); Directory.CreateDirectory(context);
        await File.WriteAllTextAsync(Path.Combine(context, "START.md"), "DO_NOT_READ");
        var service = new AgentsFileService();
        await File.WriteAllTextAsync(f.Agents, service.PreviewManagedBlock(new(f.Agents, "# 用户规则\n", "", false), context, true));
        var config = f.Service.McpSnippet(f.Bridge, f.Data).Replace("args = [", "args = [\n").Replace(", ", ",\n").Replace("\"mcp\"]", "\"mcp\",\n]");
        await File.WriteAllTextAsync(f.Service.ConfigPath, config);
        var checks = await f.Inspect(); State(checks, "MCP 配置", "configured");
        State(checks, "AGENTS 入口", "configured"); State(checks, "入口目标", "local-pass"); Honest(checks);
    }

    private static void State(IReadOnlyList<EnvironmentCheck> checks, string name, string state) => Check(checks.Single(x => x.Name == name).State == state, name + " expected " + state + "; actual " + checks.Single(x => x.Name == name).State);
    private static void Honest(IReadOnlyList<EnvironmentCheck> checks) { State(checks, "宿主确认", "unverified"); Check(checks.All(c => c.State != "ready"), "file existence claimed readiness"); }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new Exception("expected " + typeof(T).Name); }

    private sealed class EnvironmentFixture : IDisposable
    {
        internal const string Skill = "---\nname: amifolio\ndescription: Synthetic test skill.\n---\nRead-only fixture.\n";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "amifolio-environment-" + Guid.NewGuid().ToString("N"));
        internal CodexIntegrationService Service { get; }
        internal string Bridge => Path.Combine(Root, "bundle", "bridge", "Amifolio.Bridge.exe");
        internal string Data => Path.Combine(Root, "中文 data");
        internal string Agents => Path.Combine(Root, "project", "AGENTS.md");
        internal string Entry => Path.Combine(Data, "context", "global", "START.md");
        internal EnvironmentFixture() { Directory.CreateDirectory(Root); Service = new(Path.Combine(Root, "custom-codex"), Root); }
        internal Task<IReadOnlyList<EnvironmentCheck>> Inspect() => Service.InspectEnvironmentAsync(Bridge, Data, Skill, Agents);
        internal string[] Files() => Directory.GetFileSystemEntries(Root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        internal async Task Setup()
        {
            Directory.CreateDirectory(Service.CodexHome); Directory.CreateDirectory(Service.SkillDirectory); Directory.CreateDirectory(Path.GetDirectoryName(Bridge)!);
            foreach (var name in new[] { "Amifolio.Bridge.exe", "Amifolio.Bridge.dll", "Amifolio.Bridge.deps.json", "Amifolio.Bridge.runtimeconfig.json", "Amifolio.Core.dll", "Amifolio.Context.dll" })
                await File.WriteAllTextAsync(Path.Combine(Path.GetDirectoryName(Bridge)!, name), "NOT_AN_EXECUTABLE");
            await File.WriteAllTextAsync(Service.ConfigPath, "model='PRIVATE_MODEL'\n" + Service.McpSnippet(Bridge, Data), Encoding.Unicode);
            await File.WriteAllTextAsync(Path.Combine(Service.SkillDirectory, "SKILL.md"), Skill);
            await AgentsSetup();
        }
        internal async Task AgentsSetup()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Agents)!); Directory.CreateDirectory(Path.GetDirectoryName(Entry)!);
            await File.WriteAllTextAsync(Entry, "ORIGINAL_BODY_DO_NOT_READ");
            var service = new AgentsFileService();
            await File.WriteAllTextAsync(Agents, service.PreviewManagedBlock(new(Agents, "# 用户规则\r\nUSER_RULE_SECRET\r\n", "", false), Path.GetDirectoryName(Entry)!, true), Encoding.Unicode);
        }
        public void Dispose()
        {
            var path = Path.GetFullPath(Root);
            if (path.StartsWith(Path.Combine(Path.GetTempPath(), "amifolio-environment-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(path, true);
        }
    }
}
