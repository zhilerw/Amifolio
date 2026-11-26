using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Amifolio.Core;

/// <summary>Verified means the caller independently verified USER authorship and the original quote.
/// This API has no role field: a model assertion, assistant echo, silence, or usage statistic is not verification.</summary>
public sealed record MemoryEvidence(string Id, string Text, string SessionId, string TurnId, string ProjectId, bool Verified, DateTimeOffset ObservedAt);
public sealed record MemoryProposal(string Kind, string Dimension, string Value, string Text, string Scope, string Condition, IReadOnlyList<string> EvidenceIds);
public sealed record MemoryDecision(string State, string Reason, string Dimension, string NormalizedValue);

/// <summary>
/// Pure, bounded, deterministic admission policy; no persistence, clock, I/O, permissions, or model training.
/// The parent must check learning consent, verified USER provenance, target project, current task overrides,
/// tombstones and manual locks (by scope + dimension, never model wording) before applying a decision.
/// Reasons contain codes only; blocked decisions never return proposal text or values.
/// </summary>
/// <remarks>
/// Deliberately incomplete lexical heuristics, NOT a semantic verifier or comprehensive secret detector.
/// Only whole positive preference clauses in the templates below can activate. Quoted speech, unsupported
/// conditions, mixed prose and unrecognized synonyms stay candidates. Normalization is NFKC/case/spacing/
/// punctuation similarity, not semantic equivalence. Encoded secrets, arbitrary project names, paraphrases,
/// deceptive verified provenance and adversarial natural language cannot be exhaustively recognized here.
/// Candidate text must not be treated as executable instructions or shared without the parent's consent gates.
/// </remarks>
public static class MemoryPolicy
{
    private const int MaxText = 8192;
    private const int MaxEvidence = 128;
    private static readonly string[] Dimensions = ["verbosity", "tone", "format", "language", "typography", "color", "workflow", "project_fact", "task_status", "other"];
    private const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    // NonBacktracking is linear in bounded input; no wall-clock-dependent regex deadline affects admission.
    private static Regex Rx(string pattern) => new(pattern, Options, Regex.InfiniteMatchTimeout);

    private static readonly Regex Sensitive = Rx(
        @"(?:\bsk-[a-z0-9_-]{8,}|\b(?:gh[pousr]_|github_pat_|xox[baprs]-)[a-z0-9_-]{8,}|\bAKIA[A-Z0-9]{16}\b|" +
        @"\beyJ[a-z0-9_-]{8,}\.[a-z0-9_-]+\.[a-z0-9_-]+|-----\s*BEGIN\s+(?:[A-Z]+\s+)*PRIVATE\s+KEY|" +
        @"\b(?:authorization|proxy-authorization)\s*:|\bbearer\s+\S+|" +
        @"\b(?:[a-z][a-z0-9]*[_-])*(?:api[_ -]?key|access[_ -]?token|refresh[_ -]?token|client[_ -]?secret|password|passwd|credential|secret)\s*(?:[:=：]|\bis\b)|" +
        @"(?:密码|口令|密钥|令牌|凭据)\s*(?:[:=：]|是)|" +
        @"[a-z]:[\\/]|\\\\[^\s\\]+\\|\b(?:file|https?|ssh)://|(?:^|\s)[.~]{1,2}[\\/]|" +
        @"(?:^|[\s'""(])/(?:[a-z0-9_.-]+/|home\b|users\b|tmp\b|etc\b|var\b)|" +
        @"(?:[\p{L}\p{N}_.-]+[\\/])+[\p{L}\p{N}_.-]+\.[a-z0-9]{1,12}\b|" +
        @"(?:^|[\s\\/])(?:\.env(?:\.[a-z0-9_.-]+)?|(?:credentials?|secrets?|tokens?)\.(?:json|txt|key|pem)|id_rsa|id_ed25519)(?:$|[\s\\/])|" +
        @"(?:ignore|disregard|override|forget)\s+(?:(?:all|the|any|previous|prior|system|developer|safety)\s+)*(?:instructions?|rules?|prompts?)|" +
        @"(?:disable|bypass|skip|turn off)\s+(?:(?:all|the|any)\s+)*(?:security|safety|permission|authorization|approval|sandbox)|" +
        @"(?:grant|allow|enable|change|expand)\s+(?:(?:all|the|full|any|remote)\s+)*(?:permissions?|access|network|uploads?)|" +
        @"\b(?:run|execute|invoke)\s+(?:shell|powershell|bash|cmd|commands?|scripts?)\b|(?:never|do not|don't)\s+(?:ask|request)\s+(?:for\s+)?(?:approval|permission|confirmation)|" +
        @"(?:忽略|无视|覆盖|忘记).{0,12}(?:指令|规则|提示词)|(?:跳过|禁用|关闭|绕过|扩大|授予|更改).{0,12}(?:安全|权限|授权|审批|上传)|" +
        @"(?:无需|不必|不用).{0,8}(?:确认|授权|审批|询问)|" +
        @"<\s*/?\s*(?:system|developer|assistant|tool)\b|\[\s*INST\s*\]|<\|(?:im_start|system|assistant))");
    private static readonly Regex OpaqueToken = Rx(@"\b[a-z0-9_+/=-]{40,}\b");
    private static readonly Regex ProjectPayload = Rx(@"(?:项目名称|项目名|项目路径|项目编号|project\s*(?:name|id|path))\s*[:=：]|\bproject\s*:\s*\S+|\b(?:in|for)\s+project\s+\S+");
    private static readonly Regex Echo = Rx(@"(?:^|\s)(?:assistant|ai|model|system|user)\s*(?:said|suggested|says|:)|(?:助手|模型|用户|系统)(?:说|建议|表示)\s*[:：]|['\""“”‘’`<>]");
    private static readonly Regex Negation = Rx(@"\b(?:not|never|avoid|stop|without|don't|do not|no longer)\b|不要|不再|不喜欢|禁止|别|不能|并非|不是");
    private static readonly Regex FactQualifier = Rx(@"\b(?:if|when|unless|except|only|for this task|this time|for now|temporarily)\b|本次|这次|暂时|只有|仅当|除非|如果|当.+时");
    private static readonly Regex FactRequest = Rx(@"^(?:长期记住|以后记住|今后记住|remember long term|from now on remember)\s*[:：]?\s*(?<quote>.+)$");
    private static readonly Regex Clauses = Rx(@"[。.!?！？;；\r\n]+");

    private sealed record Lexeme(string Dimension, string Value, string[] Aliases, Regex Source);
    private static Lexeme Lex(string dimension, string value, string[] aliases, string source) => new(dimension, value, aliases, Rx("^(?:" + source + ")$"));
    private static readonly Lexeme[] Lexicon =
    [
        Lex("verbosity", "brief", ["brief", "concise", "short", "简洁", "简短", "精简", "简明"],
            @"(?:请)?(?:回答(?:请)?|回复(?:请)?)?(?:尽量)?(?:简洁|简短|精简|简明)(?:地)?(?:回答|回复|一些|一点)?|(?:please )?(?:use |i prefer )(?:brief|concise|short) (?:answers|responses|replies)|(?:please )?(?:keep |make )(?:the )?(?:answers|responses|replies) (?:brief|concise|short)"),
        Lex("verbosity", "detailed", ["detailed", "thorough", "详细", "详尽"],
            @"(?:请)?(?:回答(?:请)?|回复(?:请)?)?(?:尽量)?(?:详细|详尽)(?:地)?(?:回答|回复|一些|一点)?|(?:please )?(?:use |i prefer )(?:detailed|thorough) (?:answers|responses|replies)|(?:please )?(?:keep |make )(?:the )?(?:answers|responses|replies) (?:detailed|thorough)"),
        Lex("tone", "formal", ["formal", "正式"], @"(?:请)?(?:使用|采用|用)?正式(?:的)?(?:语气|风格)|语气(?:请)?正式|(?:please )?(?:use |i prefer )(?:a )?formal tone"),
        Lex("tone", "casual", ["casual", "informal", "轻松", "随意", "口语化"], @"(?:请)?(?:使用|采用|用)?(?:轻松|随意|口语化)(?:的)?(?:语气|风格)|语气(?:请)?(?:轻松|随意|口语化)|(?:please )?(?:use |i prefer )(?:a |an )?(?:casual|informal) tone"),
        Lex("typography", "song", ["song", "simsun", "宋体"], @"(?:请)?(?:使用|采用|用)宋体(?:字体)?|(?:please )?(?:use |i prefer )(?:song|simsun)(?: font)?"),
        Lex("color", "turquoise", ["turquoise", "松石绿", "松石绿色", "绿松石色"], @"(?:请)?(?:使用|采用|用)(?:松石绿|松石绿色|绿松石色)|(?:please )?(?:use |i prefer )turquoise"),
        Lex("color", "beige", ["beige", "米白", "米白色"], @"(?:请)?(?:使用|采用|用)(?:米白|米白色)|(?:please )?(?:use |i prefer )beige"),
        Lex("language", "chinese", ["chinese", "zh", "中文"], @"(?:请)?(?:使用|用)中文(?:回答|回复)?|(?:please )?(?:use |answer in |reply in |i prefer )chinese"),
        Lex("language", "english", ["english", "en", "英文", "英语"], @"(?:请)?(?:使用|用)(?:英文|英语)(?:回答|回复)?|(?:please )?(?:use |answer in |reply in |i prefer )english")
    ];
    private sealed record Wrapper(string Pattern, string Category, string Value);
    // Conditions are intentionally a tiny exact lexicon. No model-created condition is inferred from context.
    private static readonly Wrapper[] Wrappers =
    [
        new(@"from now on|always|long term|以后|今后|长期|始终|总是", "ongoing", "yes"),
        new(@"in all projects|for all projects|across all projects|globally|所有项目|全部项目|全局|跨项目", "scope", "global"),
        new(@"in this project|for this project|在本项目中|本项目|这个项目", "scope", "project"),
        new(@"for this task|this time|for now|仅本次|本次|这次|暂时", "temporary", "yes"),
        new(@"when explaining math|讲解数学时", "condition", "when explaining math"),
        new(@"when writing code|写代码时", "condition", "when writing code"),
        new(@"when writing documents|写文档时", "condition", "when writing documents")
    ];
    private static string WrapperPattern(Wrapper w) => string.Join("|", w.Pattern.Split('|').Select(p => p.All(c => c < 128) ? @"\b" + p + @"\b" : p));
    private static readonly (Wrapper Wrapper, Regex Start, Regex End)[] WrapperRules = Wrappers
        .Select(w => (w, Rx("^(?:" + WrapperPattern(w) + @")\s*"), Rx(@"\s*(?:" + WrapperPattern(w) + ")$"))).ToArray();
    private sealed record Support(MemoryEvidence Evidence, string Value, string Condition, string SourceScope, bool Ongoing, bool Temporary);

    public static MemoryDecision Evaluate(MemoryProposal proposal, IReadOnlyList<MemoryEvidence> evidence, IReadOnlySet<string> globallyParticipatingProjects)
    {
        var dimension = proposal?.Dimension?.Trim().ToLowerInvariant() ?? "other";
        if (!Dimensions.Contains(dimension, StringComparer.Ordinal)) dimension = "other";
        MemoryDecision Block(string reason) => new("blocked", reason, dimension, "");
        var value = "";
        MemoryDecision Candidate(string reason) => new("candidate", reason, dimension, value);
        MemoryDecision Active(string reason) => new("active", reason, dimension, value);

        if (proposal is null || evidence is null || globallyParticipatingProjects is null || proposal.EvidenceIds is null)
            return Block("invalid_input");
        if (evidence.Count > MaxEvidence || proposal.EvidenceIds.Count > MaxEvidence) return Block("input_limit");
        var fields = new[] { proposal.Kind, proposal.Dimension, proposal.Value, proposal.Text, proposal.Scope, proposal.Condition };
        if (fields.Any(s => s is null || ContainsSensitiveText(s))) return Block("sensitive_or_invalid_proposal");
        var scope = proposal.Scope.Trim().ToLowerInvariant();
        var kind = proposal.Kind.Trim().ToLowerInvariant();
        if (scope is not ("project" or "global" or "task")) return Candidate("unknown_scope");
        if (kind is not ("style" or "memory" or "fact" or "summary")) return Candidate("unknown_kind");

        // Duplicate identifiers are ambiguous even if the duplicated rows happen to agree.
        var rows = new Dictionary<string, MemoryEvidence>(StringComparer.Ordinal);
        foreach (var e in evidence)
        {
            if (e is null || !ValidId(e.Id) || !rows.TryAdd(e.Id, e)) return Block("invalid_or_duplicate_evidence_id");
            if (ContainsSensitiveText(e.Text)) return Block("sensitive_source");
        }
        var selected = new List<MemoryEvidence>();
        foreach (var id in proposal.EvidenceIds.Distinct(StringComparer.Ordinal))
        {
            if (!ValidId(id) || !rows.TryGetValue(id, out var e)) return Candidate("missing_evidence");
            selected.Add(e);
        }
        foreach (var e in selected)
        {
            if (ContainsSensitiveText(e.Text)) return Block("sensitive_source");
            if (scope == "global" && (!ValidId(e.ProjectId) || !globallyParticipatingProjects.Contains(e.ProjectId)))
                return Block("nonparticipating_project");
        }
        if (scope == "global" && (dimension == "project_fact" || kind == "fact")) return Block("project_fact_global_forbidden");
        if (scope == "global" && (fields.Any(s => ProjectPayload.IsMatch(s)) || selected.Any(e => ProjectPayload.IsMatch(e.Text))))
            return Block("project_identifying_payload");
        if (scope == "task") return Candidate("task_only");
        if (kind == "summary" || dimension is "task_status" or "other") return Candidate("non_preference_or_unknown_dimension");
        if (selected.Count == 0) return Candidate("no_verified_user_evidence");
        if (scope == "project" && (selected.Any(e => !ValidId(e.ProjectId)) || selected.Select(e => e.ProjectId).Distinct(StringComparer.Ordinal).Count() != 1))
            return Candidate("mixed_or_unknown_project");

        var condition = CanonicalCondition(proposal.Condition);
        if (condition is null) return Candidate("unsupported_condition");
        if (dimension == "project_fact")
        {
            if (kind is not ("memory" or "fact")) return Candidate("fact_kind_required");
            value = Normalize(proposal.Value);
            if (value.Length == 0 || proposal.Text.Trim() != proposal.Value.Trim()) return Candidate("exact_fact_quote_required");
            // Facts are never generalized by counting repeated mentions. Only an explicit quoted memory request qualifies.
            if (condition.Length != 0) return Candidate("unsupported_fact_condition");
            if (FactQualifier.IsMatch(proposal.Text) || Echo.IsMatch(proposal.Text)) return Candidate("unsupported_fact_semantics");
            foreach (var e in evidence.Where(VerifiedUser).Where(e => e.ProjectId == selected[0].ProjectId))
            {
                var prior = FactRequest.Match(e.Text.Trim());
                // No semantic fact comparer exists. Different explicit fact quotes require review, even if unrelated.
                if (prior.Success && prior.Groups["quote"].Value.Trim() != proposal.Text.Trim()) return Candidate("ambiguous_or_conflicting_fact_quotes");
            }
            foreach (var e in selected.Where(VerifiedUser))
            {
                var m = FactRequest.Match(e.Text.Trim());
                if (m.Success && m.Groups["quote"].Value.Trim() == proposal.Text.Trim()) return Active("explicit_project_fact_quote");
            }
            return Candidate("explicit_fact_quote_required");
        }
        if (kind == "fact") return Candidate("fact_dimension_required");
        var lexeme = Lexicon.FirstOrDefault(l => l.Dimension == dimension && l.Aliases.Contains(Normalize(proposal.Value), StringComparer.Ordinal));
        if (lexeme is null) return Candidate("unsupported_dimension_or_value");
        value = lexeme.Value;

        // Examine all provided applicable verified rows for counterevidence, not only model-picked citations.
        var projects = selected.Select(e => e.ProjectId).ToHashSet(StringComparer.Ordinal);
        foreach (var e in evidence.Where(VerifiedUser))
        {
            if (scope == "project" ? !projects.Contains(e.ProjectId) : !globallyParticipatingProjects.Contains(e.ProjectId)) continue;
            if (ContainsSensitiveText(e.Text)) return Block("sensitive_applicable_source");
            var parsed = Parse(e, dimension);
            if (parsed.Any(s => !s.Temporary && s.Value != value && (s.Condition == condition || s.Condition.Length == 0 || condition.Length == 0)))
                return Candidate("contradictory_evidence");
            if (Negation.IsMatch(e.Text) && lexeme.Aliases.Any(a => HasAlias(e.Text, a))) return Candidate("negated_or_ambiguous_evidence");
        }
        var support = selected.Where(VerifiedUser).SelectMany(e => Parse(e, dimension))
            .Where(s => s.Value == value && !s.Temporary && s.Condition == condition &&
                (scope == "global" ? s.SourceScope != "project" : s.SourceScope != "global")).ToArray();
        if (support.Length == 0) return Candidate("source_value_scope_or_condition_not_supported");
        if (scope == "global")
        {
            if (support.Any(s => s.SourceScope == "global")) return Active("explicit_global_user_preference");
            if (HasIndependentTriple(support, requireTwoProjects: true)) return Active("independent_cross_project_user_evidence");
            return Candidate("insufficient_independent_global_evidence");
        }
        if (support.Any(s => s.Ongoing)) return Active("explicit_ongoing_user_preference");
        return HasIndependentTriple(support, requireTwoProjects: false) ? Active("three_independent_user_turns_and_texts") : Candidate("insufficient_independent_user_evidence");
    }

    public static IReadOnlyList<MemoryProposal> ExplicitProjectPreferences(MemoryEvidence source)
    {
        if (!VerifiedUser(source) || ContainsSensitiveText(source.Text)) return [];
        var result = new List<MemoryProposal>();
        foreach (var dimension in Lexicon.Select(item => item.Dimension).Distinct())
        {
            var supports = Parse(source, dimension).Where(s => s.Ongoing && !s.Temporary && s.SourceScope != "global")
                .Select(s => (s.Value, s.Condition)).Distinct().ToArray();
            if (supports.Length != 1) continue;
            var (value, condition) = supports[0];
            var proposal = new MemoryProposal("style", dimension, value, source.Text.Trim(), "project", condition, [source.Id]);
            if (Evaluate(proposal, [source], new HashSet<string>()).State == "active") result.Add(proposal);
        }
        return result;
    }

    /// <summary>Heuristic rejection filter, bounded at 8192 UTF-16 code units; oversize/malformed text fails closed.
    /// Use it on raw text before storage/projection, not as a guarantee that every secret or injection is recognized.</summary>
    public static bool ContainsSensitiveText(string text)
    {
        if (!TryPrepare(text, out var prepared)) return true;
        try
        {
            if (Sensitive.IsMatch(prepared)) return true;
            // Long opaque mixed letter/digit payloads are not useful preference evidence.
            return OpaqueToken.Matches(prepared).Any(m => m.Value.Any(char.IsDigit) && m.Value.Any(char.IsLetter));
        }
        catch (RegexMatchTimeoutException) { return true; }
    }

    /// <summary>Safe durable lexical similarity key, never a semantic or identity proof.
    /// Returns empty for sensitive, oversized or malformed input instead of retaining/truncating it.</summary>
    public static string Normalize(string text)
    {
        if (ContainsSensitiveText(text) || !TryPrepare(text, out var prepared)) return "";
        var result = new StringBuilder(prepared.Length);
        var space = false;
        foreach (var rune in prepared.EnumerateRunes())
        {
            if (Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark)
            {
                if (space && result.Length != 0) result.Append(' ');
                result.Append(Rune.ToLowerInvariant(rune).ToString()); space = false;
            }
            else space = result.Length != 0;
        }
        return result.ToString();
    }

    private static bool TryPrepare(string? text, out string prepared)
    {
        prepared = "";
        if (text is null || text.Length > MaxText) return false;
        try
        {
            var normalized = text.Normalize(NormalizationForm.FormKC);
            if (normalized.Length > MaxText) return false;
            var b = new StringBuilder(normalized.Length);
            foreach (var rune in normalized.EnumerateRunes())
            {
                if (rune.Value is >= 0x202a and <= 0x202e or >= 0x2066 and <= 0x2069) return false;
                var category = Rune.GetUnicodeCategory(rune);
                if (category == UnicodeCategory.Control && !Rune.IsWhiteSpace(rune)) return false;
                if (category != UnicodeCategory.Format) b.Append(rune.ToString());
            }
            prepared = b.ToString(); return true;
        }
        catch (ArgumentException) { return false; }
    }

    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 256;
    private static bool VerifiedUser(MemoryEvidence e) => e.Verified && ValidId(e.SessionId) && ValidId(e.TurnId) && ValidId(e.ProjectId);
    private static string? CanonicalCondition(string text)
    {
        var normalized = Normalize(text);
        if (text.Trim().Length == 0) return "";
        foreach (var w in Wrappers.Where(w => w.Category == "condition"))
            if (w.Pattern.Split('|').Any(a => Normalize(a) == normalized)) return w.Value;
        return null;
    }
    private static bool HasAlias(string text, string alias)
    {
        var n = Normalize(text);
        return alias.Any(c => c > 127) ? n.Contains(alias, StringComparison.Ordinal) : (" " + n + " ").Contains(" " + alias + " ", StringComparison.Ordinal);
    }

    private static Support[] Parse(MemoryEvidence evidence, string dimension)
    {
        if (Echo.IsMatch(evidence.Text) || Negation.IsMatch(evidence.Text)) return [];
        var parsed = new List<Support>();
        foreach (var raw in Clauses.Split(evidence.Text).Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            var clause = Normalize(raw);
            var ongoing = false; var temporary = false; var scope = ""; var condition = "";
            // Each removal shortens bounded text; no unbounded recursion or free-form semantic interpretation.
            var changed = true;
            while (changed && clause.Length > 0)
            {
                changed = false;
                foreach (var (w, start, end) in WrapperRules)
                {
                    var match = start.Match(clause);
                    var atStart = match.Success;
                    if (!match.Success) match = end.Match(clause);
                    if (!match.Success) continue;
                    if (w.Category == "scope") { if (scope.Length != 0 && scope != w.Value) return []; scope = w.Value; }
                    if (w.Category == "condition") { if (condition.Length != 0 && condition != w.Value) return []; condition = w.Value; }
                    if (w.Category == "ongoing") ongoing = true;
                    if (w.Category == "temporary") temporary = true;
                    clause = (atStart ? clause[match.Length..] : clause[..match.Index]).Trim();
                    changed = true; break;
                }
            }
            var lex = Lexicon.FirstOrDefault(l => l.Dimension == dimension && l.Source.IsMatch(clause));
            if (lex is null) return []; // Unrecognized residual text may contain a qualifier. Never silently discard it.
            parsed.Add(new(evidence, lex.Value, condition, scope, ongoing, temporary));
        }
        return parsed.ToArray();
    }

    private static bool HasIndependentTriple(Support[] support, bool requireTwoProjects)
    {
        var sources = support.Select(s => (s.Evidence.ProjectId, Turn: (s.Evidence.SessionId, s.Evidence.TurnId), Text: Normalize(s.Evidence.Text))).Distinct().ToArray();
        // A joint matching is required: three distinct turns AND texts separately is insufficient (a star graph).
        for (var i = 0; i < sources.Length; i++)
            for (var j = i + 1; j < sources.Length; j++)
            {
                if (sources[i].Turn == sources[j].Turn || sources[i].Text == sources[j].Text) continue;
                for (var k = j + 1; k < sources.Length; k++)
                {
                    if (sources[k].Turn == sources[i].Turn || sources[k].Turn == sources[j].Turn || sources[k].Text == sources[i].Text || sources[k].Text == sources[j].Text) continue;
                    if (!requireTwoProjects || sources[i].ProjectId != sources[j].ProjectId || sources[i].ProjectId != sources[k].ProjectId) return true;
                }
            }
        return false;
    }
}
