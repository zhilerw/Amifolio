using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amifolio.Context;

/// <summary>Local evidence only. No state returned by this inspector proves host readiness.</summary>
public sealed record EnvironmentCheck(string Name, string State, string Detail);

public sealed partial class CodexIntegrationService
{
    /// <summary>
    /// Reads only the instance's config/skill and the explicitly selected AGENTS.md, with existing
    /// size/link limits. expectedSkill is the trusted bundled SKILL.md text, not a file path.
    /// Does not run commands, load assemblies, read credentials or entry-target bodies, or write files.
    /// configured = local text matches; local-pass = local files/absence checked; neither means loaded.
    /// missing/conflict/invalid/unreadable identify local issues. Host confirmation is always unverified.
    /// </summary>
    public async Task<IReadOnlyList<EnvironmentCheck>> InspectEnvironmentAsync(string bridge, string dataRoot,
        string expectedSkill, string agentsPath, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        bridge = DiagnosticLocalPath(bridge);
        dataRoot = DiagnosticLocalPath(dataRoot);
        agentsPath = DiagnosticLocalPath(agentsPath);
        ArgumentNullException.ThrowIfNull(expectedSkill);
        if (!Path.GetFileName(agentsPath).Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("检测入口必须为 AGENTS.md。", nameof(agentsPath));

        var checks = new List<EnvironmentCheck>(8);
        checks.Add(await InspectLocalCheckAsync("MCP 配置", async () =>
        {
            DiagnosticLocalPath(ConfigPath);
            var snapshot = await files.ReadAsync(SetupFile.Config, ct).ConfigureAwait(false);
            if (!snapshot.Exists) return new("MCP 配置", "missing", "当前 CodexHome 的 config.toml 不存在；未创建文件。");
            return InspectMcpText(snapshot.Content, bridge, dataRoot, ct);
        }, ct).ConfigureAwait(false));
        checks.Add(await InspectLocalCheckAsync("桥接组件", () =>
        {
            SafeFiles.CheckPath(bridge);
            var found = Path.GetFileName(bridge).Equals("Amifolio.Bridge.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(bridge);
            return Task.FromResult(new EnvironmentCheck("桥接组件", found ? "local-pass" : "missing",
                found ? "指定随包桥接文件存在；未执行、未验证签名或宿主信任。" : "指定随包 Amifolio.Bridge.exe 缺失；请检查完整便携包。"));
        }, ct).ConfigureAwait(false));
        checks.Add(await InspectLocalCheckAsync("运行库文件", () =>
        {
            var directory = Path.GetDirectoryName(bridge)!;
            foreach (var name in new[] { "Amifolio.Bridge.dll", "Amifolio.Bridge.deps.json", "Amifolio.Bridge.runtimeconfig.json", "Amifolio.Core.dll", "Amifolio.Context.dll" })
            {
                ct.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, name); SafeFiles.CheckPath(path);
                if (!File.Exists(path)) return Task.FromResult(new EnvironmentCheck("运行库文件", "missing", "随包桥接运行库文件不完整；未探测系统运行时。"));
            }
            return Task.FromResult(new EnvironmentCheck("运行库文件", "local-pass", "随包桥接运行库文件存在；仅检查文件，不证明系统运行时可用或组件可执行。"));
        }, ct).ConfigureAwait(false));
        checks.Add(await InspectLocalCheckAsync("Skill 安装", async () =>
        {
            ValidateDiagnosticSkill(expectedSkill);
            DiagnosticLocalPath(SkillDirectory);
            var snapshot = await files.ReadAsync(SetupFile.Skill, ct).ConfigureAwait(false);
            if (!snapshot.Exists) return new("Skill 安装", "missing", "标准 .agents/skills/amifolio/SKILL.md 尚未安装。");
            ValidateDiagnosticSkill(snapshot.Content);
            if (!snapshot.Content.Equals(expectedSkill, StringComparison.Ordinal))
                return new("Skill 安装", "conflict", "已安装 skill 与随包内容不同；请手动比较版本与用户改动。");
            foreach (var path in Directory.EnumerateFileSystemEntries(SkillDirectory))
            {
                ct.ThrowIfCancellationRequested();
                if (!DiagnosticPathsEqual(path, snapshot.Path) && !DiagnosticPathsEqual(path, snapshot.Path + ".amifolio.lock"))
                    return new("Skill 安装", "conflict", "目标 skill 目录包含额外文件；未读取或执行这些文件。");
            }
            return new("Skill 安装", "configured", "标准目标内 SKILL.md 与随包内容一致；宿主是否加载尚未验证。");
        }, ct).ConfigureAwait(false));
        checks.Add(await InspectLocalCheckAsync("旧版 Skill", () =>
        {
            foreach (var path in new[] { Path.Combine(CodexHome, "skills", "amifolio"), Path.Combine(UserHome, ".codex", "skills", "amifolio") }.Distinct(DiagnosticPathComparer))
            {
                ct.ThrowIfCancellationRequested(); DiagnosticLocalPath(path); SafeFiles.CheckPath(path);
                if (!DiagnosticPathsEqual(path, SkillDirectory) && (Directory.Exists(path) || File.Exists(path)))
                    return Task.FromResult(new EnvironmentCheck("旧版 Skill", "conflict", "旧 skills 位置仍有 amifolio；可能重复安装，请手动比较处理。"));
            }
            return Task.FromResult(new EnvironmentCheck("旧版 Skill", "local-pass", "已检查的旧 Codex skills 位置未发现重复项；未枚举其他 skills。"));
        }, ct).ConfigureAwait(false));
        try { checks.AddRange(await InspectAgentsAsync(agentsPath, dataRoot, ct).ConfigureAwait(false)); }
        catch (Exception e) when (DiagnosticReadFailure(e))
        {
            checks.Add(DiagnosticFailure("AGENTS 入口", e));
            checks.Add(new("入口目标", "unverified", "AGENTS 入口无法安全核对；未读取目标正文。"));
        }
        ct.ThrowIfCancellationRequested();
        checks.Add(new("宿主确认", "unverified", "尚无真实宿主连接及作用域回执；本次仅做本机只读检查，未启动宿主、执行配置命令或授予信任。"));
        return checks.AsReadOnly();
    }

    private static StringComparer DiagnosticPathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static string DiagnosticLocalPath(string path)
    {
        // Never turn an inspection into a network/device read, including via explicit caller input.
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32760 || path.Any(char.IsControl) ||
            !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            throw new ArgumentException("检测路径必须为长度受限的绝对本机路径。");
        var full = Path.GetFullPath(path);
        if (OperatingSystem.IsWindows() && (full.IndexOf(':', 2) >= 0 || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network))
            throw new ArgumentException("不支持网络、设备或替代数据流路径。");
        return full;
    }

    private static bool DiagnosticPathsEqual(string a, string b) => DiagnosticPathComparer.Equals(
        Path.TrimEndingDirectorySeparator(DiagnosticLocalPath(a)), Path.TrimEndingDirectorySeparator(DiagnosticLocalPath(b)));

    private static void ValidateDiagnosticSkill(string text)
    {
        if (text.Length > 262144) throw new InvalidDataException("Skill 超过大小上限。");
        try { ValidateSkill(text); }
        catch (IOException) { throw new InvalidDataException("Skill 格式或大小不受支持。"); }
    }

    private static bool DiagnosticReadFailure(Exception e) => e is IOException or InvalidDataException or UnauthorizedAccessException or SecurityException or ArgumentException or JsonException;

    private static EnvironmentCheck DiagnosticFailure(string name, Exception e) => new(name,
        e is InvalidDataException or ArgumentException or JsonException ? "invalid" : "unreadable",
        e is InvalidDataException or ArgumentException or JsonException
            ? "内容格式、编码或大小不受支持，无法安全确认；未更改文件。"
            : "文件无法安全读取，可能存在链接、权限或占用问题；未更改文件。");

    private static async Task<EnvironmentCheck> InspectLocalCheckAsync(string name, Func<Task<EnvironmentCheck>> inspect, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return await inspect().ConfigureAwait(false); }
        catch (Exception e) when (DiagnosticReadFailure(e)) { return DiagnosticFailure(name, e); }
    }

    private static EnvironmentCheck InspectMcpText(string text, string bridge, string dataRoot, CancellationToken ct)
    {
        static EnvironmentCheck Conflict() => new("MCP 配置", "conflict", "Amifolio MCP 与当前桥接或数据根不一致，或存在重复、禁用、内联及覆盖配置；请手动比较，未回显配置值。");
        List<string> rows;
        try { rows = TomlStatements(text); }
        catch (IOException) { throw new InvalidDataException("TOML 字符串无效。"); }
        if (rows.Count > 65536 || rows.Any(r => r.Length > 32768)) throw new InvalidDataException();
        string[] section = []; var tables = 0; string? command = null; string[]? args = null;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < rows.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var row = rows[i].Trim();
            if (row.Length == 0 || row.StartsWith('#')) continue;
            if (row.StartsWith('['))
            {
                var array = row.StartsWith("[[", StringComparison.Ordinal);
                var delimiter = array ? 2 : 1;
                if (!row.EndsWith(array ? "]]" : "]", StringComparison.Ordinal)) throw new InvalidDataException();
                section = DiagnosticTomlKey(row[delimiter..^delimiter]);
                if (HasDiagnosticMcpKey(section))
                {
                    if (array || section.Length != 2 || section[0] != "mcp_servers" || section[1] != "amifolio" || ++tables != 1) return Conflict();
                }
                continue;
            }
            var assignment = DiagnosticAssignment(row);
            if (assignment < 0) throw new InvalidDataException();
            var key = DiagnosticTomlKey(row[..assignment]);
            var full = section.Concat(key).ToArray();
            if (!HasDiagnosticMcpKey(full) && !key.Contains("mcp_servers", StringComparer.OrdinalIgnoreCase)) continue;
            if (section.Length != 2 || section[0] != "mcp_servers" || section[1] != "amifolio" || key.Length != 1 || !keys.Add(key[0])) return Conflict();
            var value = row[(assignment + 1)..].Trim();
            switch (key[0])
            {
                case "command": command = DiagnosticTomlString(value); break;
                case "args":
                    // TOML arrays may span lines; only string elements are accepted for this known schema.
                    if (value.StartsWith('[') && !value.EndsWith(']'))
                    {
                        var arrayText = new StringBuilder(value);
                        while (++i < rows.Count)
                        {
                            ct.ThrowIfCancellationRequested();
                            var next = rows[i].Trim();
                            if (arrayText.Length + next.Length + 1 > 262144) throw new InvalidDataException();
                            arrayText.Append('\n').Append(next);
                            if (next.EndsWith(']')) break;
                        }
                        value = arrayText.ToString();
                    }
                    args = DiagnosticTomlArray(value); break;
                case "enabled": if (value != "true") return Conflict(); break;
                default: return Conflict(); // env/url/unknown options prevent an honest exact-match claim.
            }
        }
        if (tables == 0) return new("MCP 配置", "missing", "当前 CodexHome 的 config.toml 未发现 Amifolio MCP 表；未读取其他宿主配置源。");
        try
        {
            if (command is null || args is null || args.Length != 3 || args[0] != "--data-root" || args[2] != "mcp" ||
                !DiagnosticPathsEqual(command, bridge) || !DiagnosticPathsEqual(args[1], dataRoot)) return Conflict();
        }
        catch (ArgumentException) { return Conflict(); }
        return new("MCP 配置", "configured", "当前 CodexHome 的 config.toml 内 Amifolio 命令与数据根一致；不证明宿主已加载或外部覆盖链有效。");
    }

    private static bool HasDiagnosticMcpKey(string[] key)
    {
        for (var i = 0; i + 1 < key.Length; i++)
            if (key[i].Equals("mcp_servers", StringComparison.OrdinalIgnoreCase) && key[i + 1].Equals("amifolio", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static int DiagnosticAssignment(string row)
    {
        char quote = '\0';
        for (var i = 0; i < row.Length; i++)
        {
            var c = row[i];
            if (quote != '\0') { if (c == '\\' && quote == '"') i++; else if (c == quote) quote = '\0'; }
            else if (c is '\'' or '"') quote = c;
            else if (c == '=') return i;
        }
        return -1;
    }

    private static string[] DiagnosticTomlKey(string text)
    {
        var parts = new List<string>(); var start = 0; char quote = '\0';
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && quote != '\0') { if (text[i] == '\\' && quote == '"') i++; else if (text[i] == quote) quote = '\0'; continue; }
            if (i < text.Length && text[i] is '\'' or '"') { quote = text[i]; continue; }
            if (i < text.Length && text[i] != '.') continue;
            var part = text[start..i].Trim();
            if (part.Length == 0) throw new InvalidDataException();
            parts.Add(part[0] is '\'' or '"' ? DiagnosticTomlString(part) :
                part.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? part : throw new InvalidDataException());
            start = i + 1;
        }
        if (quote != '\0') throw new InvalidDataException();
        return parts.ToArray();
    }

    private static string DiagnosticTomlString(string value)
    {
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'' && !value[1..^1].Contains('\'')) return value[1..^1];
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            return JsonSerializer.Deserialize<string>(value) ?? throw new InvalidDataException();
        throw new InvalidDataException();
    }

    private static string[] DiagnosticTomlArray(string value)
    {
        if (!value.StartsWith('[') || !value.EndsWith(']')) throw new InvalidDataException();
        var items = new List<string>(); var start = 1; char quote = '\0';
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (quote != '\0') { if (c == '\\' && quote == '"') i++; else if (c == quote) quote = '\0'; continue; }
            if (c is '\'' or '"') { quote = c; continue; }
            if (c != ',' && i != value.Length - 1) continue;
            var part = value[start..i].Trim();
            if (part.Length == 0) { if (c == ',') throw new InvalidDataException(); }
            else items.Add(DiagnosticTomlString(part));
            start = i + 1;
        }
        if (quote != '\0') throw new InvalidDataException();
        return items.ToArray();
    }

    private static async Task<EnvironmentCheck[]> InspectAgentsAsync(string agentsPath, string dataRoot, CancellationToken ct)
    {
        static EnvironmentCheck[] Problem(string state, string detail) =>
            [new("AGENTS 入口", state, detail), new("入口目标", state == "missing" ? "missing" : "conflict", "尚无可安全核对的本数据根入口；未读取目标正文。")];
        var service = new AgentsFileService();
        var snapshot = await service.ReadAsync(agentsPath, ct).ConfigureAwait(false);
        if (!snapshot.Exists) return Problem("missing", "所选 AGENTS.md 不存在；未创建文件。");
        var overridePath = Path.Combine(Path.GetDirectoryName(agentsPath)!, "AGENTS.override.md");
        SafeFiles.CheckPath(overridePath);
        if (File.Exists(overridePath)) return Problem("conflict", "同目录存在 AGENTS.override.md；不能把 AGENTS.md 入口视为有效宿主指令。");
        var contextRoot = Path.Combine(dataRoot, "context");
        service.PreviewManagedBlock(snapshot, Path.Combine(contextRoot, "global"), false); // Validate markers; no save or lock.
        var block = Regex.Match(snapshot.Content, @"(?ms)^<!-- folio:begin scope=[A-Za-z0-9_-]{1,128} -->\r?\n(.*?)^<!-- folio:end -->\r?$");
        if (!block.Success) return Problem("missing", "AGENTS.md 仅有用户规则，未发现 Amifolio 托管入口；不会把普通文字当已配置。");
        var entries = Regex.Matches(block.Groups[1].Value, @"(?m)^入口：`([^`\r\n]+)`。");
        if (entries.Count != 1) return Problem("conflict", "托管入口缺失或含歧义；请手动比较。");
        string entry;
        try { entry = DiagnosticLocalPath(entries[0].Groups[1].Value); }
        catch (ArgumentException) { return Problem("conflict", "入口目标不是受支持的本机路径。"); }
        var relative = Path.GetRelativePath(contextRoot, entry).Replace(Path.DirectorySeparatorChar, '/');
        var currentRelative=Path.GetRelativePath(Path.Combine(dataRoot,"learning","shared"),entry).Replace(Path.DirectorySeparatorChar,'/');
        if (!Regex.IsMatch(relative, @"^(?:global|project-[a-f0-9]{64})/START\.md$", RegexOptions.CultureInvariant)&&!Regex.IsMatch(currentRelative,@"^(?:global|project-[a-f0-9]{64})/START\.md$",RegexOptions.CultureInvariant))
            return Problem("conflict", "入口目标不在当前数据根的标准全局或项目作用域；未跟随其他路径。");
        ct.ThrowIfCancellationRequested(); SafeFiles.CheckPath(entry);
        var exists = File.Exists(entry);
        return [new("AGENTS 入口", "configured", "AGENTS.md 托管入口指向当前数据根的标准作用域；用户规则未改动，宿主指令链尚未验证。"),
            new("入口目标", exists ? "local-pass" : "missing", exists ? "START.md 目标文件存在；未读取正文，不证明共享内容或宿主已加载。" : "START.md 目标不存在；共享可能尚未开启或已撤回。")];
    }
}
