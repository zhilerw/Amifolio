using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amifolio.Context;

public enum CodexPluginState { Unknown, NotConfigured, Enabled, Disabled }

public sealed record PluginConnectionInspection(CodexPluginState PluginState, IReadOnlyList<string> LegacyEntries,
    bool LegacyInspectionIncomplete);

public sealed partial class CodexIntegrationService
{
    private const string PluginId = "amifolio@amifolio-local";

    // The native plugin installation writes this setting in config.toml. This reports the
    // saved local switch only. A running host can have different settings or hook trust.
    public async Task<CodexPluginState> ReadPluginStateAsync(CancellationToken ct = default) =>
        (await ReadConnectionConfigAsync(ct).ConfigureAwait(false)).PluginState;

    public Task<AgentsFileSnapshot> ReadHooksAsync(CancellationToken ct = default)
    {
        DiagnosticLocalPath(HooksPath);
        return files.ReadAsync(SetupFile.Hooks, ct);
    }

    public async Task<PluginConnectionInspection> InspectPluginConnectionAsync(string? projectAgentsPath = null,
        CancellationToken ct = default)
    {
        var config = await ReadConnectionConfigAsync(ct).ConfigureAwait(false);
        var legacy = new List<string>();
        var incomplete = !config.Readable;
        if (config.LegacyMcp) legacy.Add("独立 MCP");

        async Task CheckAsync(string label, Func<Task<bool>> exists)
        {
            ct.ThrowIfCancellationRequested();
            try { if (await exists().ConfigureAwait(false)) legacy.Add(label); }
            catch (Exception ex) when (DiagnosticReadFailure(ex)) { incomplete = true; }
        }

        await CheckAsync("独立 Skills", () => InspectStandaloneSkillsAsync(ct)).ConfigureAwait(false);
        await CheckAsync("独立结束钩子", () => HasLegacyStopHookAsync(ct)).ConfigureAwait(false);
        await CheckAsync("全局 AGENTS.md 接入段落", () => HasManagedAgentsBlockAsync(Path.Combine(CodexHome, "AGENTS.md"), ct))
            .ConfigureAwait(false);
        if (projectAgentsPath is not null)
            await CheckAsync("所选项目的 AGENTS.md 接入段落", () => HasManagedAgentsBlockAsync(projectAgentsPath, ct))
                .ConfigureAwait(false);
        return new(config.PluginState, legacy.AsReadOnly(), incomplete);
    }

    private async Task<(CodexPluginState PluginState, bool LegacyMcp, bool Readable)> ReadConnectionConfigAsync(CancellationToken ct)
    {
        var state = CodexPluginState.NotConfigured;
        var legacyMcp = false;
        try
        {
            DiagnosticLocalPath(ConfigPath);
            var snapshot = await ReadConfigAsync(ct).ConfigureAwait(false);
            string[] section = [];
            var pluginTable = false;
            var pluginSwitch = false;
            var unsupportedPlugin = false;

            foreach (var statement in TomlStatements(snapshot.Content))
            {
                ct.ThrowIfCancellationRequested();
                var row = statement.Trim();
                if (row.Length == 0 || row.StartsWith('#')) continue;
                if (row.StartsWith('['))
                {
                    var array = row.StartsWith("[[", StringComparison.Ordinal);
                    var size = array ? 2 : 1;
                    if (!row.EndsWith(array ? "]]" : "]", StringComparison.Ordinal))
                        throw new InvalidDataException();
                    section = DiagnosticTomlKey(row[size..^size]);
                    legacyMcp |= HasDiagnosticMcpKey(section);
                    if (section is ["plugins", PluginId])
                    {
                        unsupportedPlugin |= array || pluginTable || pluginSwitch;
                        pluginTable = true;
                    }
                    continue;
                }

                var assignment = DiagnosticAssignment(row);
                // Unrelated multiline value continuations and payloads are not settings.
                if (assignment < 0) continue;
                var key = DiagnosticTomlKey(row[..assignment]);
                var path = section.Concat(key).ToArray();
                var value = row[(assignment + 1)..].Trim();
                legacyMcp |= HasDiagnosticMcpKey(path) ||
                    path is ["mcp_servers"] && HasInlineTomlKey(value, "amifolio");
                if (path is ["plugins", PluginId, "enabled"])
                {
                    unsupportedPlugin |= pluginSwitch;
                    pluginSwitch = true;
                    state = value switch
                    {
                        "true" => CodexPluginState.Enabled,
                        "false" => CodexPluginState.Disabled,
                        _ => CodexPluginState.Unknown
                    };
                }
                else if (path is ["plugins", PluginId] ||
                    path is ["plugins"] && HasInlineTomlKey(value, PluginId))
                {
                    unsupportedPlugin = true;
                }
            }
            if (unsupportedPlugin || pluginTable && !pluginSwitch) state = CodexPluginState.Unknown;
            return (state, legacyMcp, true);
        }
        catch (Exception ex) when (DiagnosticReadFailure(ex))
        {
            return (CodexPluginState.Unknown, legacyMcp, false);
        }
    }

    // Only match keys at the outer level of an inline table, outside quoted strings.
    private static bool HasInlineTomlKey(string value, string name)
    {
        if (!value.StartsWith('{')) return false;
        var depth = 1;
        var keyStart = 1;
        char quote = '\0';
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (quote != '\0')
            {
                if (c == '\\' && quote == '"') i++;
                else if (c == quote) quote = '\0';
                continue;
            }
            if (c is '\'' or '"') quote = c;
            else if (c is '{' or '[') depth++;
            else if (c is '}' or ']') depth--;
            else if (c == ',' && depth == 1) keyStart = i + 1;
            else if (c == '=' && depth == 1 && DiagnosticTomlKey(value[keyStart..i]).First() == name)
                return true;
        }
        return false;
    }

    private async Task<bool> InspectStandaloneSkillsAsync(CancellationToken ct)
    {
        DiagnosticLocalPath(SkillDirectory);
        var snapshot = await ReadSkillAsync(ct).ConfigureAwait(false);
        if (snapshot.Exists) return true;
        foreach (var directory in new[] { CodexHome, Path.Combine(UserHome, ".codex") }.Distinct(DiagnosticPathComparer))
        {
            var path = DiagnosticLocalPath(Path.Combine(directory, "skills", "amifolio", "SKILL.md"));
            SafeFiles.CheckPath(path);
            if (File.Exists(path)) return true;
        }
        return false;
    }

    private async Task<bool> HasLegacyStopHookAsync(CancellationToken ct)
    {
        var snapshot = await ReadHooksAsync(ct).ConfigureAwait(false);
        if (!snapshot.Exists) return false;
        using var doc = JsonDocument.Parse(snapshot.Content);
        if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        if (!doc.RootElement.TryGetProperty("hooks", out var hookGroups)) return false;
        if (hookGroups.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        if (!hookGroups.TryGetProperty("Stop", out var stops)) return false;
        if (stops.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        foreach (var group in stops.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("hooks", out var hooks) ||
                hooks.ValueKind != JsonValueKind.Array) continue;
            foreach (var hook in hooks.EnumerateArray())
                if (hook.ValueKind == JsonValueKind.Object && hook.TryGetProperty("type", out var type) &&
                    type.ValueKind == JsonValueKind.String && type.GetString() == "command" &&
                    hook.TryGetProperty("statusMessage", out var status) && status.ValueKind == JsonValueKind.String &&
                    status.GetString() is "Amifolio 收纳核对" or "Amifolio 项目扫描")
                    return true;
        }
        return false;
    }

    private static async Task<bool> HasManagedAgentsBlockAsync(string path, CancellationToken ct)
    {
        var snapshot = await new AgentsFileService().ReadAsync(DiagnosticLocalPath(path), ct).ConfigureAwait(false);
        return Regex.IsMatch(snapshot.Content, @"(?m)^<!-- folio:begin scope=[A-Za-z0-9_-]{1,128} -->\r?$");
    }
}
