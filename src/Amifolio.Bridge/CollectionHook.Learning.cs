using System.Text.Json;
using System.Text.RegularExpressions;
using Amifolio.Context;
using Amifolio.Core;

namespace Amifolio.Bridge;

internal static partial class CollectionHook
{
    private sealed record HookLearningStatus(string state, string reason);
    private const string WaitingSummary = "回合结束，概述等待Agent补充";
    private const string ReportPrefix = "Agent 自述（未核验完成事实）：";
    private static readonly Regex HookPreference = new(
        @"^(?:(?:以后|今后|长期|始终|总是)\s*)?(?:请)?(?:" +
        @"(?:回答|回复)(?:请)?(?:尽量)?(?:简洁|简短|精简|简明|详细|详尽)(?:一些|一点)?|" +
        @"(?:简洁|简短|精简|简明|详细|详尽)(?:地)?(?:回答|回复)|" +
        @"(?:使用|用)?(?:中文|英文|英语)(?:回答|回复)?|" +
        @"(?:使用|采用|用)?(?:正式|轻松|随意|口语化)(?:的)?(?:语气|风格)|" +
        @"语气(?:请)?(?:正式|轻松|随意|口语化)|" +
        @"(?:使用|采用|用)?(?:宋体(?:字体)?|松石绿|松石绿色|绿松石色|米白|米白色))$|" +
        @"^(?:(?:from now on|always|long term)\s+)?(?:please\s+)?(?:" +
        @"(?:use|i prefer)\s+(?:brief|concise|short|detailed|thorough)\s+(?:answers|responses|replies)|" +
        @"(?:keep|make)\s+(?:the\s+)?(?:answers|responses|replies)\s+(?:brief|concise|short|detailed|thorough)|" +
        @"(?:use|answer in|reply in|i prefer)\s+(?:chinese|english)|" +
        @"(?:use|i prefer)\s+(?:a\s+|an\s+)?(?:formal|casual|informal)\s+tone|" +
        @"(?:use|i prefer)\s+(?:song|simsun)(?:\s+font)?|(?:use|i prefer)\s+(?:turquoise|beige))$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static async Task<HookLearningStatus> LearningFallbackAsync(PortfolioStore store, CodexProject project,
        string cwd, JsonElement hook, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(15));
        var token = bounded.Token;
        try
        {
            var context = new ContextService(store);
            var learning = context.Learning;
            var policy = await learning.PolicyAsync(project.Id, token).ConfigureAwait(false);
            // This gate precedes reader construction, recent-turn discovery and all dialogue reads.
            if (!policy.Capture || policy.GrantedAt is null) return new("disabled", "capture_not_authorized");
            if (hook.TryGetProperty("stop_hook_active", out var active) && active.ValueKind != JsonValueKind.False)
                return new("continuation_ignored", "stop_continuation");
            if (!TryHookString(hook, "session_id", 128, out var session) || !Guid.TryParseExact(session, "D", out _))
                return new("pending_evidence", "missing_or_invalid_session_id");
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!Path.TrimEndingDirectorySeparator(cwd).Equals(Path.TrimEndingDirectorySeparator(project.RootPath), comparison))
                return new("pending_evidence", "exact_project_cwd_required");

            string? turn = null;
            if (hook.TryGetProperty("turn_id", out var supplied) && supplied.ValueKind != JsonValueKind.Null)
            {
                if (!TryHookString(hook, "turn_id", 128, out turn) || !HookTurnId(turn)) return new("pending_evidence", "invalid_turn_id");
                // A tombstone is also a receipt. Never replace an explicit Agent report or backfill a deletion.
                if (await learning.HasReceiptAsync(project.Id, session!, turn!, token).ConfigureAwait(false)) return new("already_recorded", "receipt_exists");
            }
            var home = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            var reader = new CodexTurnReader(home);
            var now = DateTimeOffset.UtcNow;
            var since = now.AddMinutes(-10);
            if (policy.GrantedAt.Value > since) since = policy.GrantedAt.Value;
            if (turn is null)
            {
                var headers = await reader.RecentTurnsAsync(project.RootPath, since, token).ConfigureAwait(false);
                var candidates = headers.Where(t => t.SessionId == session && t.RootPath.Equals(project.RootPath, comparison)
                    && t.ObservedAt >= since && t.ObservedAt <= now.AddMinutes(2)).OrderByDescending(t => t.ObservedAt).ToArray();
                if (candidates.Length == 0) return new("pending_evidence", "no_verified_turn_in_bounded_window");
                if (candidates.Count(t => t.ObservedAt == candidates[0].ObservedAt) != 1) return new("pending_evidence", "ambiguous_latest_turn");
                turn = candidates[0].TurnId;
                if (await learning.HasReceiptAsync(project.Id, session!, turn, token).ConfigureAwait(false)) return new("already_recorded", "receipt_exists");
            }
            var source = await reader.ReadTurnAsync(session!, turn, project.RootPath, token).ConfigureAwait(false);
            if (source is null || source.SessionId != session || source.TurnId != turn || !source.RootPath.Equals(project.RootPath, comparison)
                || source.ObservedAt < since || source.ObservedAt > now.AddMinutes(2))
                return new("pending_evidence", "source_missing_unsupported_or_stale");

            // Recheck immediately before capture; Context/Store also enforce current consent and source.
            var current = await learning.PolicyAsync(project.Id, token).ConfigureAwait(false);
            if (!current.Capture || current.Revision != policy.Revision || current.GrantedAt != policy.GrantedAt)
                return new("disabled", "capture_policy_changed");
            if (await learning.HasReceiptAsync(project.Id, session!, turn, token).ConfigureAwait(false)) return new("already_recorded", "receipt_exists");
            var capture = new LearningCapture(project.Id, session!, turn, HookSummary(hook), "partial", HookQuotes(source.UserText), []);
            var receipt = await context.CaptureLearningAsync(capture, home, token).ConfigureAwait(false);
            return receipt.State == "verified" ? new("recorded", "partial_agent_report") : new("pending_evidence", "source_changed_before_commit");
        }
        catch (OperationCanceledException) { return new("pending_evidence", "bounded_capture_cancelled"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or JsonException)
        { return new("pending_evidence", "capture_unavailable"); }
    }

    private static bool TryHookString(JsonElement hook, string name, int maximum, out string? value)
    {
        value = null;
        if (!hook.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String) return false;
        var text = field.GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maximum || text.Any(char.IsControl)) return false;
        value = text; return true;
    }
    private static bool HookTurnId(string? id) => id is { Length: > 0 and <= 128 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string HookSummary(JsonElement hook)
    {
        if (!hook.TryGetProperty("last_assistant_message", out var value) || value.ValueKind != JsonValueKind.String) return WaitingSummary;
        var text = value.GetString();
        // Reject the whole unsafe/oversized report rather than truncating a secret-bearing tail.
        if (string.IsNullOrWhiteSpace(text) || text.Length > 1200 || text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')
            || MemoryPolicy.ContainsSensitiveText(text)) return WaitingSummary;
        return ReportPrefix + text;
    }
    private static IReadOnlyList<string> HookQuotes(string userText)
    {
        var quotes = new List<string>();
        foreach (var raw in userText.Split(['。', '.', '!', '?', '！', '？', ';', '；', '\r', '\n']))
        {
            var clause = raw.Trim();
            // Exact source substrings only, with a tiny preference grammar. No fact extraction,
            // paraphrases, model echoes, quoted instructions or full-history forwarding.
            if (clause.Length is 0 or > 160 || MemoryPolicy.ContainsSensitiveText(clause) || !HookPreference.IsMatch(clause)
                || quotes.Contains(clause, StringComparer.Ordinal)) continue;
            quotes.Add(clause);
            if (quotes.Count == 4) break;
        }
        return quotes;
    }
}
