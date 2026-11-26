using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amifolio.Core;

public sealed record CreativeStyle(string Font, string Color, string Style, string Voice,
    string? ReferenceId = null, string? ReferenceName = null, string? ReferencePart = null)
{
    public string? Background { get; init; }
    public int? Spacing { get; init; }
    public int? Radius { get; init; }
    public string? SampleText { get; init; }

    public bool HasValues() => Spacing.HasValue || Radius.HasValue ||
        !string.IsNullOrWhiteSpace(Font) || !string.IsNullOrWhiteSpace(Color) ||
        !string.IsNullOrWhiteSpace(Style) || !string.IsNullOrWhiteSpace(Voice) ||
        !string.IsNullOrWhiteSpace(Background) || !string.IsNullOrWhiteSpace(SampleText) ||
        !string.IsNullOrWhiteSpace(ReferenceId);
}

public sealed record CreativePreference(string Id, string? ProjectId, CreativeStyle Style, string State, long Revision);

public sealed partial class LearningStore
{
    private const string CreativeKind = "creative";
    private static readonly Regex ColorPattern = new("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant);
    private static readonly Regex CreativeTask = new(
        @"设计|画风|绘制|插画|海报|封面|排版|配色|界面|页面|网页|文案|写一|写个|风格|润色|design|illustrat|poster|cover|layout|typograph|palette|landing page|website|copywriting|draft a|rewrite",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public Task<CreativePreference?> CreativeAsync(string? projectId, CancellationToken ct = default) =>
        UseAsync(db => AsCreative(ReadCreative(db, Key(projectId))), ct);

    public Task<CreativePreference> SaveCreativeAsync(string? projectId, CreativeStyle style, bool provide,
        long expectedRevision, CancellationToken ct = default)
    {
        var scope = Key(projectId);
        style = NormalizeStyle(style);
        var value = Serialize(style);
        return UseAsync(db =>
        {
            var previous = ReadCreative(db, scope);
            if ((previous?.Revision ?? 0) != expectedRevision)
                throw new InvalidOperationException("偏好已被修改，请重新查看。");

            var state = provide ? "active" : "disabled";
            var current = AsCreative(previous);
            if (current?.State == state && current.Style == style)
                return current;

            var entry = new LearningEntry(CreativeId(scope), scope, CreativeKind, "creative_profile", value,
                CreativeText(style), "creative", state, "user_edited", true, expectedRevision + 1, Now, []);
            // Creation can be undone. A deleted baseline carries no style or reference data.
            History(db, current is not null ? previous! : entry with
            {
                Value = "",
                Text = "",
                Condition = "",
                State = "deleted",
                Revision = expectedRevision
            });
            PutEntry(db, entry);
            return AsCreative(entry)!;
        }, ct);
    }

    /// <summary>Clears the authored fields for a scope and retains the previous revision for undo.</summary>
    public Task<long> RemoveCreativeAsync(string? projectId, long expectedRevision, CancellationToken ct = default) =>
        UseAsync(db =>
        {
            var previous = ReadCreative(db, Key(projectId));
            if (previous is not { State: not "deleted" } || previous.Revision != expectedRevision)
                throw new InvalidOperationException("偏好已被修改，请重新查看。");
            if (!string.IsNullOrWhiteSpace(AsCreative(previous)?.Style.ReferenceId))
                throw new InvalidOperationException("请先移除作品范例，再清除这份偏好。");

            var removed = previous with
            {
                Value = "", Text = "", Condition = "", State = "deleted", Reason = "user_cleared",
                EvidenceIds = [], Revision = previous.Revision + 1, UpdatedAt = Now
            };
            History(db, previous);
            PutEntry(db, removed);
            return removed.Revision;
        }, ct);

    public Task<CreativePreference> UndoCreativeRemovalAsync(string? projectId, long expectedRevision,
        CancellationToken ct = default) => UseAsync(db =>
        {
            var current = ReadCreative(db, Key(projectId));
            if (current is not { State: "deleted" } || current.Revision != expectedRevision)
                throw new InvalidOperationException("偏好已被修改，请重新查看。");

            var previous = One<LearningEntry>(db,
                "SELECT payload FROM history WHERE entry_id=$id ORDER BY seq DESC LIMIT 1", ("$id", current.Id));
            if (previous is not { State: not "deleted" }) throw new InvalidOperationException("没有可撤销的修订。");

            var restored = previous with { Revision = current.Revision + 1, UpdatedAt = Now, Reason = "user_undo" };
            db.Exec("DELETE FROM history WHERE seq=(SELECT MAX(seq) FROM history WHERE entry_id=$id)", ("$id", current.Id));
            PutEntry(db, restored);
            return AsCreative(restored)!;
        }, ct);

    /// <summary>
    /// Previews the saved payload for an eligible creative request in a fresh project session.
    /// A null project previews the global contribution on its own.
    /// </summary>
    public Task<string?> PreviewCreativeDeliveryAsync(string? projectId, CancellationToken ct = default)
    {
        var scope = Key(projectId);
        if (!HasState) return Task.FromResult<string?>(null);
        return UseAsync(db =>
        {
            var lines = new List<string>(2);
            if (ReadCreative(db, "global") is { State: "active" } global)
                lines.Add(CreativeDeliveryLine(global, isGlobal: true));
            if (scope != "global" && ReadCreative(db, scope) is { State: "active" } local)
                lines.Add(CreativeDeliveryLine(local, isGlobal: false));
            return FormatCreativeDelivery(lines);
        }, ct);
    }

    public Task<string?> ProvideCreativeAsync(string projectId, string sessionId, string turnId, string prompt,
        CancellationToken ct = default)
    {
        var project = Key(projectId);
        if (project == "global" || !Guid.TryParseExact(sessionId, "D", out _) ||
            !ValidDeliveryTurn(turnId) || prompt.Length > 32768 || !CreativeTask.IsMatch(prompt) || !HasState)
            return Task.FromResult<string?>(null);

        return UseAsync(db =>
        {
            var local = ReadCreative(db, project);
            var global = ReadCreative(db, "global");
            var texts = new List<string>(2);
            Consider(global, isGlobal: true);
            Consider(local, isGlobal: false);
            if (texts.Count == 0) return null;
            db.Exec("DELETE FROM creative_delivery WHERE id IN (SELECT id FROM creative_delivery ORDER BY observed DESC LIMIT -1 OFFSET 4096)");
            return FormatCreativeDelivery(texts);

            void Consider(LearningEntry? entry, bool isGlobal)
            {
                if (entry is not { State: "active" }) return;
                var id = Hash(sessionId + "\n" + project + "\n" + entry.Id + "\n" + entry.Revision);
                var claimed = db.Exec("""
                    INSERT INTO creative_delivery(id,session,project,turn,observed)
                    VALUES($id,$session,$project,$turn,$observed)
                    ON CONFLICT(id) DO UPDATE SET turn=excluded.turn,observed=excluded.observed
                    WHERE creative_delivery.confirmed=0 AND creative_delivery.turn<>excluded.turn
                    """, ("$id", id), ("$session", sessionId), ("$project", project), ("$turn", turnId),
                    ("$observed", Now.ToUnixTimeMilliseconds()));
                if (claimed == 0) return;
                texts.Add(CreativeDeliveryLine(entry, isGlobal));
            }
        }, ct);
    }

    private static string CreativeDeliveryLine(LearningEntry entry, bool isGlobal) =>
        (isGlobal ? "全局默认" : "当前项目优先") + " · " + CreativeText(AsCreative(entry)!.Style);

    private static string? FormatCreativeDelivery(List<string> lines) => lines.Count == 0 ? null :
        "以下是用户已确认的创作偏好。当前用户要求优先，项目设置优先于全局默认值。只在与当前任务相关时采用。\n" +
        string.Join("\n", lines);

    public Task ConfirmCreativeAsync(string projectId, string sessionId, string turnId, CancellationToken ct = default)
    {
        if (!HasState || !Guid.TryParseExact(sessionId, "D", out _) || !ValidDeliveryTurn(turnId))
            return Task.CompletedTask;
        return UseAsync(db => db.Exec("""
            UPDATE creative_delivery SET confirmed=1
            WHERE session=$session AND project=$project AND turn=$turn AND confirmed=0
            """, ("$session",sessionId), ("$project",Key(projectId)), ("$turn",turnId)), ct);
    }

    private static bool ValidDeliveryTurn(string id) =>
        id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string CreativeId(string scope) => "creative-" + Hash(scope);

    private static LearningEntry? ReadCreative(Db db, string scope) =>
        One<LearningEntry>(db, "SELECT payload FROM profiles WHERE id=$id", ("$id", CreativeId(scope)));

    internal static CreativePreference? AsCreative(LearningEntry? entry) =>
        entry is { Kind: CreativeKind, State: not "deleted" }
            ? new(entry.Id, entry.Scope == "global" ? null : entry.Scope,
                JsonSerializer.Deserialize<CreativeStyle>(entry.Value, Json) ?? throw new IOException("创作偏好记录无效。"),
                entry.State, entry.Revision)
            : null;

    public Task<(CreativePreference? Preference, long Revision)> CreativeSettingsAsync(string? projectId, CancellationToken ct = default) =>
        UseAsync(db =>
        {
            var entry = ReadCreative(db, Key(projectId));
            return (AsCreative(entry), entry?.Revision ?? 0);
        }, ct);

    public Task<bool> HasCreativeHistoryAsync(string? projectId, CancellationToken ct = default) =>
        UseAsync(db =>
        {
            var current = ReadCreative(db, Key(projectId));
            return current is not null &&
                db.Scalar("SELECT 1 FROM history WHERE entry_id=$id LIMIT 1", ("$id", current.Id)) is not null;
        }, ct);

    private static CreativeStyle NormalizeStyle(CreativeStyle value)
    {
        var font = Field(value.Font, 100);
        var color = Field(value.Color, 7).ToUpperInvariant();
        var style = Field(value.Style, 240);
        var voice = Field(value.Voice, 240);
        var background = Field(value.Background, 7).ToUpperInvariant();
        var sampleText = Field(value.SampleText?.ReplaceLineEndings("\n"), 500, allowLineFeed: true);
        var name = Field(value.ReferenceName, 180);
        var part = Field(value.ReferencePart, 180);
        var reference = value.ReferenceId?.Trim() ?? "";
        if (reference.Length > 0 && (reference.Length != 68 || !reference.StartsWith("ref-", StringComparison.Ordinal) ||
            !reference.Skip(4).All(char.IsAsciiHexDigitLower)))
            throw new ArgumentException("范例引用无效。");
        if (color.Length > 0 && !ColorPattern.IsMatch(color)) throw new ArgumentException("主色应为 #RRGGBB。");
        if (background.Length > 0 && !ColorPattern.IsMatch(background)) throw new ArgumentException("背景色应为 #RRGGBB。");
        if (value.Spacing is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(value.Spacing), "间距应为 0–64 px。");
        if (value.Radius is < 0 or > 48) throw new ArgumentOutOfRangeException(nameof(value.Radius), "圆角应为 0–48 px。");
        if (reference.Length > 0 && (name.Length == 0 || part.Length == 0))
            throw new ArgumentException("请填写这份作品中要沿用的部分。");
        var normalized = new CreativeStyle(font, color, style, voice, reference.Length == 0 ? null : reference,
            reference.Length == 0 ? null : name, reference.Length == 0 ? null : part)
        {
            Background = background.Length == 0 ? null : background,
            Spacing = value.Spacing,
            Radius = value.Radius,
            SampleText = sampleText.Length == 0 ? null : sampleText
        };
        if (!normalized.HasValues()) throw new ArgumentException("至少填写一项打算沿用的偏好。");
        return normalized;

        static string Field(string? text, int maximum, bool allowLineFeed = false)
        {
            text = text?.Trim() ?? "";
            if (text.Length > maximum || text.Any(c => char.IsControl(c) && (!allowLineFeed || c != '\n')))
                throw new ArgumentException($"偏好字段包含不允许的控制字符或超过 {maximum} 字符。");
            if (text.Length > 0) SafeText(text);
            return text;
        }
    }

    private static string CreativeText(CreativeStyle style)
    {
        var fields = new List<string>(9);
        Add("字体", style.Font);
        Add("主色", style.Color);
        Add("风格", style.Style);
        Add("文字语气", style.Voice);
        Add("背景色", style.Background);
        if (style.Spacing is int spacing) Add("间距", FormattableString.Invariant($"{spacing} px"));
        if (style.Radius is int radius) Add("圆角", FormattableString.Invariant($"{radius} px"));
        if (style.SampleText is { Length: > 0 } sampleText) Add("样文", "「" + sampleText + "」");
        if (style.ReferenceName is not null) Add("参考作品", style.ReferenceName + "（沿用 " + style.ReferencePart + "）");
        return string.Join("；", fields);

        void Add(string label, string? value)
        {
            if (!string.IsNullOrEmpty(value)) fields.Add(label + " " + value);
        }
    }
}
