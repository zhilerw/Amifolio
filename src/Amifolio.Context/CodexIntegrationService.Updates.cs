using System.Text;
using System.Text.RegularExpressions;

namespace Amifolio.Context;

/// <summary>Explicit setup preview/confirmed save only. No discovery, downloads, model calls, or original-project writes.</summary>
public sealed partial class CodexIntegrationService
{
    private const string UpdateBegin = "# Amifolio MCP begin";
    private const string UpdateEnd = "# Amifolio MCP end";

    public Task<AgentsFileSnapshot> ReadSkillAsync(CancellationToken ct = default) => files.ReadAsync(SetupFile.Skill, ct);

    /// <summary>Pure preview: replace one recognized owned block, or append when there is no Amifolio definition.</summary>
    public string PreviewMcpUpdate(AgentsFileSnapshot snapshot, string snippet)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        UpdateTarget(snapshot, ConfigPath);
        var canonical = UpdateSnippet(snippet, requireCanonical: true);
        var block = UpdateBlock(snapshot.Content);
        var nl = block?.Newline ?? (snapshot.Content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n");
        var replacement = UpdateBegin + nl + canonical.Replace("\n", nl, StringComparison.Ordinal) + UpdateEnd;
        return block is not null
            ? snapshot.Content[..block.Start] + replacement + snapshot.Content[block.End..]
            : snapshot.Content + (snapshot.Content.Length == 0 ? "" : snapshot.Content.EndsWith('\n') ? nl : nl + nl) + replacement + nl;
    }

    /// <summary>Call only after the user confirms the complete preview/diff. Revalidates ownership, outside text and CAS.</summary>
    public async Task<string?> SaveMcpUpdateAsync(AgentsFileSnapshot snapshot, string proposed, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(proposed);
        UpdateTarget(snapshot, ConfigPath);
        var block = UpdateBlock(proposed) ?? throw UpdateConflict();
        var snippet = proposed[block.BodyStart..block.BodyEnd].Replace("\r\n", "\n", StringComparison.Ordinal);
        if (PreviewMcpUpdate(snapshot, snippet) != proposed) throw UpdateConflict();
        // Hash-only CAS cannot validate a caller-forged Content with an otherwise genuine Hash.
        await UpdateCurrent(SetupFile.Config, snapshot, ct).ConfigureAwait(false);
        return await files.SaveAsync(SetupFile.Config, snapshot, proposed, ct).ConfigureAwait(false);
    }

    /// <summary>Explicit user-approved replacement of the standard amifolio skill only. Returns the adjacent byte-exact backup.</summary>
    public async Task<string?> ReplaceSkillAsync(AgentsFileSnapshot snapshot, string trustedSkill, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(snapshot);
        UpdateTarget(snapshot, Path.Combine(SkillDirectory, "SKILL.md"));
        UpdateSkill(trustedSkill);
        if (!snapshot.Exists) throw new IOException("Skill 尚未安装，请使用首次安装流程。");
        UpdateSkill(snapshot.Content);
        await UpdateCurrent(SetupFile.Skill, snapshot, ct).ConfigureAwait(false);
        SafeFiles.CheckPath(SkillDirectory);
        await using var guard = await SafeFiles.LockAsync(SkillDirectory + ".install.lock", ct).ConfigureAwait(false);
        var legacy = Path.Combine(CodexHome, "skills", "amifolio");
        if (Directory.Exists(legacy) && !legacy.Equals(SkillDirectory, StringComparison.OrdinalIgnoreCase)) throw UpdateConflict();
        foreach (var path in Directory.EnumerateFileSystemEntries(SkillDirectory))
        {
            ct.ThrowIfCancellationRequested();
            if (!UpdatePathsEqual(path, snapshot.Path) && !UpdatePathsEqual(path, snapshot.Path + ".amifolio.lock"))
                throw new IOException("Skill 目录包含额外文件或备份，请手动比较；未覆盖或删除这些文件。");
        }
        // SetupFiles retains encoding/BOM, CAS and backup location. Its .bak remains visible
        // to diagnostics as an extra file; this helper neither hides nor deletes that backup.
        return await files.SaveAsync(SetupFile.Skill, snapshot, trustedSkill, ct).ConfigureAwait(false);
    }

    private async Task UpdateCurrent(SetupFile target, AgentsFileSnapshot snapshot, CancellationToken ct)
    {
        var current = await files.ReadAsync(target, ct).ConfigureAwait(false);
        if (current.Exists != snapshot.Exists || current.Hash != snapshot.Hash || current.Content != snapshot.Content)
            throw new IOException("接入文件已变化或快照不一致，请重新读取并比较。");
    }
    private static bool UpdatePathsEqual(string left, string right) => string.Equals(left, right,
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    private static void UpdateTarget(AgentsFileSnapshot snapshot, string expected)
    {
        if (!UpdatePathsEqual(snapshot.Path, expected)) throw new IOException("接入目标路径已改变，未写入。");
        if (!snapshot.Exists && snapshot.Content.Length != 0) throw UpdateConflict();
    }
    private static IOException UpdateConflict() => new("MCP 或 Skill 内容不属于可识别的 Amifolio 托管格式，请手动比较；未覆盖。");

    private static void UpdateSkill(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateSkill(text);
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines[0] != "---") throw UpdateConflict();
        var end = Array.FindIndex(lines, 1, line => line == "---");
        if (end < 2) throw UpdateConflict();
        var names = lines[1..end].Where(line => Regex.IsMatch(line, @"^\s*name\s*:", RegexOptions.CultureInvariant)).ToArray();
        if (names.Length != 1 || names[0] != "name: amifolio") throw UpdateConflict();
    }

    private sealed record UpdateManaged(int Start, int End, int BodyStart, int BodyEnd, string Newline);
    private sealed record UpdateRow(int Start, string Text);

    private static UpdateManaged? UpdateBlock(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Encoding.UTF8.GetByteCount(text) > 4 * 1024 * 1024) throw UpdateConflict();
        var mentions = Regex.Matches(text, "Amifolio MCP", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var begins = Regex.Matches(text, @"(?m)^# Amifolio MCP begin\r?$", RegexOptions.CultureInvariant);
        var ends = Regex.Matches(text, @"(?m)^# Amifolio MCP end\r?$", RegexOptions.CultureInvariant);
        UpdateManaged? block = null;
        if (mentions.Count != 0)
        {
            if (mentions.Count != 2 || begins.Count != 1 || ends.Count != 1 || begins[0].Index >= ends[0].Index) throw UpdateConflict();
            var begin = begins[0]; var end = ends[0];
            var bodyStart = begin.Index + begin.Length;
            if (bodyStart >= text.Length || text[bodyStart] != '\n') throw UpdateConflict();
            bodyStart++;
            var finish = end.Index + end.Length - (end.Value.EndsWith('\r') ? 1 : 0);
            var nl = begin.Value.EndsWith('\r') ? "\r\n" : "\n";
            block = new(begin.Index, finish, bodyStart, end.Index, nl);
            UpdateSnippet(text[bodyStart..end.Index], requireCanonical: false);
        }
        var rows = UpdateRows(text, block);
        string[] section = [];
        var tables = 0;
        foreach (var row in rows)
        {
            if (row.Text.StartsWith('['))
            {
                var array = row.Text.StartsWith("[[", StringComparison.Ordinal);
                var width = array ? 2 : 1;
                if (!row.Text.EndsWith(array ? "]]" : "]", StringComparison.Ordinal)) throw UpdateConflict();
                section = DiagnosticTomlKey(row.Text[width..^width]);
                if (HasDiagnosticMcpKey(section))
                {
                    if (array || section.Length != 2 || section[0] != "mcp_servers" || section[1] != "amifolio" || ++tables != 1
                        || block is null || row.Start < block.BodyStart || row.Start >= block.BodyEnd) throw UpdateConflict();
                }
                continue;
            }
            var assignment = DiagnosticAssignment(row.Text);
            if (assignment < 0) throw UpdateConflict();
            var key = DiagnosticTomlKey(row.Text[..assignment]);
            var full = section.Concat(key).ToArray();
            // Inline/root or scoped MCP maps cannot be safely merged without a full TOML editor.
            if (full[^1].Equals("mcp_servers", StringComparison.OrdinalIgnoreCase)) throw UpdateConflict();
            if (HasDiagnosticMcpKey(full) && (block is null || row.Start < block.BodyStart || row.Start >= block.BodyEnd)) throw UpdateConflict();
        }
        if (tables != (block is null ? 0 : 1)) throw UpdateConflict();
        return block;
    }

    private static string UpdateSnippet(string snippet, bool requireCanonical)
    {
        ArgumentNullException.ThrowIfNull(snippet);
        if (snippet.Length > 32768) throw UpdateConflict();
        var normalized = snippet.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (lines.Length != 4 || lines[3] != "" || lines[0] != "[mcp_servers.amifolio]") throw UpdateConflict();
        string Read(string row, string expected)
        {
            var split = DiagnosticAssignment(row);
            if (split < 0 || row[..split].Trim() != expected) throw UpdateConflict();
            return row[(split + 1)..].Trim();
        }
        try
        {
            var command = DiagnosticTomlString(Read(lines[1], "command"));
            var args = DiagnosticTomlArray(Read(lines[2], "args"));
            if (args.Length != 3 || args[0] != "--data-root" || args[2] != "mcp"
                || !Path.GetFileName(command).Equals("Amifolio.Bridge.exe", StringComparison.OrdinalIgnoreCase)) throw UpdateConflict();
            UpdateLocal(command); UpdateLocal(args[1]);
            var canonical = "[mcp_servers.amifolio]\ncommand = " + Toml(command) + "\nargs = [\"--data-root\", " + Toml(args[1]) + ", \"mcp\"]\n";
            if (requireCanonical && normalized != canonical) throw UpdateConflict();
            return canonical;
        }
        catch (Exception error) when (error is ArgumentException or System.Text.Json.JsonException or InvalidDataException) { throw UpdateConflict(); }
    }
    private static void UpdateLocal(string path)
    {
        if (path.Length is 0 or > 4096 || path.Any(char.IsControl) || !Path.IsPathFullyQualified(path)
            || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || path.Split(['\\', '/']).Any(part => part is "." or "..")
            || OperatingSystem.IsWindows() && path.IndexOf(':', 2) >= 0) throw UpdateConflict();
        _ = Path.GetFullPath(path); // Lexical only: do not read the command, data root or any original.
    }

    // Small bounded lexical scanner, not a general TOML rewriter. It groups multiline arrays,
    // inline maps and strings so example headers/markers never become top-level ownership.
    private static List<UpdateRow> UpdateRows(string text, UpdateManaged? block)
    {
        var rows = new List<UpdateRow>();
        var current = new StringBuilder();
        var start = -1; var square = 0; var brace = 0; var markers = 0;
        char quote = '\0'; var multi = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (start < 0 && !char.IsWhiteSpace(c) && c != '#') start = i;
            if (quote != '\0')
            {
                current.Append(c);
                if (c == '\\' && quote == '"')
                {
                    if (++i >= text.Length) throw UpdateConflict();
                    current.Append(text[i]); continue;
                }
                if (!multi && c is '\r' or '\n') throw UpdateConflict();
                if (c != quote) continue;
                if (!multi) { quote = '\0'; continue; }
                var count = 1;
                while (i + count < text.Length && text[i + count] == quote) count++;
                if (count >= 3)
                {
                    if (count > 5) throw UpdateConflict();
                    current.Append(quote, count - 1); i += count - 1; quote = '\0'; multi = false;
                }
                continue;
            }
            if (c == '#')
            {
                var end = text.IndexOf('\n', i); if (end < 0) end = text.Length;
                if (block is not null && (i == block.Start || i == block.BodyEnd))
                {
                    if (square != 0 || brace != 0 || current.ToString().Trim().Length != 0) throw UpdateConflict();
                    markers++;
                }
                i = end - 1; continue;
            }
            if (c is '\'' or '"')
            {
                quote = c;
                multi = i + 2 < text.Length && text[i + 1] == c && text[i + 2] == c;
                current.Append(c);
                if (multi) { current.Append(c, 2); i += 2; }
                continue;
            }
            if (c == '[') square++;
            else if (c == ']') { if (--square < 0) throw UpdateConflict(); }
            else if (c == '{') brace++;
            else if (c == '}') { if (--brace < 0) throw UpdateConflict(); }
            if (c == '\n' && square == 0 && brace == 0)
            {
                Add();
                continue;
            }
            current.Append(c);
            if (current.Length > 262144) throw UpdateConflict();
        }
        if (quote != '\0' || square != 0 || brace != 0 || markers != (block is null ? 0 : 2)) throw UpdateConflict();
        Add();
        return rows;
        void Add()
        {
            var row = current.ToString().Trim();
            if (row.Length > 0)
            {
                if (rows.Count >= 65536) throw UpdateConflict();
                rows.Add(new(start, row));
            }
            current.Clear(); start = -1;
        }
    }
}
