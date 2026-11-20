using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amifolio.Core;

namespace Amifolio.Context;

public sealed partial class ContextService
{
    // This ledger belongs to the local library, never the exported context. Backups/restores
    // must include preference-state/state.json and revoke participation on restore.
    private string PreferenceStatePath => Path.Combine(Path.GetFullPath(store.DataRoot), "preference-state", "state.json");
    private sealed record PreferenceCatalog(IReadOnlyList<ProjectRecord> Projects, IReadOnlyList<ArtifactRecord> Artifacts);
    private sealed class PreferenceParticipation
    {
        public bool Enabled { get; set; }
        public long Revision { get; set; }
    }
    private sealed class PreferenceState
    {
        public int SchemaVersion { get; set; } = 1;
        public long Revision { get; set; }
        public Dictionary<string, PreferenceParticipation> Participation { get; set; } = new(StringComparer.Ordinal);
        public List<PreferenceEntry> Entries { get; set; } = [];
        public HashSet<string> DeletedProposals { get; set; } = new(StringComparer.Ordinal);
        public bool NeedsPublication { get; set; }
    }

    public async Task<PreferenceSnapshot> ReadPreferencesAsync(CancellationToken ct = default)
    {
        var (snapshot, revision, pending) = await ReadPreferencesCoreAsync(ct).ConfigureAwait(false);
        if (pending) await PublishPreferencesAsync(revision).ConfigureAwait(false);
        return snapshot;
    }

    public async Task<bool> GetPreferenceParticipationAsync(string projectId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var catalog = await PreferenceCatalogAsync(ct).ConfigureAwait(false);
        bool enabled, pending; long revision;
        await using (var guard = await LockPreferencesAsync(ct).ConfigureAwait(false))
        {
            catalog = await PreferenceMetadataAsync(ct).ConfigureAwait(false);
            var state = await LoadPreferencesAsync(ct).ConfigureAwait(false); RefreshPreferences(state, catalog);
            enabled = catalog.Projects.Any(p => p.Id == projectId) && state.Participation.TryGetValue(projectId, out var permission) && permission.Enabled;
            await SavePreferencesAsync(state, ct).ConfigureAwait(false); revision = state.Revision; pending = state.NeedsPublication;
        }
        if (pending) await PublishPreferencesAsync(revision).ConfigureAwait(false);
        return enabled;
    }

    public async Task SetPreferenceParticipationAsync(string projectId, bool enabled, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        var catalog = await PreferenceCatalogAsync(ct).ConfigureAwait(false);
        if (enabled && !catalog.Projects.Any(p => p.Id == projectId)) throw new InvalidOperationException("项目不存在或已撤销。");
        long revision;
        await using (var guard = await LockPreferencesAsync(ct).ConfigureAwait(false))
        {
            catalog = await PreferenceMetadataAsync(ct).ConfigureAwait(false);
            if (enabled && !catalog.Projects.Any(p => p.Id == projectId)) throw new InvalidOperationException("项目不存在或已撤销。");
            var state = await LoadPreferencesAsync(ct).ConfigureAwait(false);
            RefreshPreferences(state, catalog);
            if (!state.Participation.TryGetValue(projectId, out var permission)) state.Participation[projectId] = permission = new();
            if (permission.Enabled != enabled)
            {
                permission.Enabled = enabled; permission.Revision++;
                if (!enabled) InvalidatePreferences(state, p => p.ProjectId == projectId);
                PreferenceChanged(state);
            }
            await SavePreferencesAsync(state, ct).ConfigureAwait(false); revision = state.Revision;
        }
        await PublishPreferencesAsync(revision).ConfigureAwait(false);
    }

    /// <summary>Local edited proposal only. Participation does not grant remote, learning or project sharing permission.</summary>
    public Task<PreferenceEntry> ProposePreferenceAsync(string projectId, string artifactId, string text, CancellationToken ct = default) =>
        ProposePreferenceCoreAsync(projectId, artifactId, text, null, ct);

    /// <summary>Use the current generated project style/memory as a draft; edited text still requires separate confirmation.</summary>
    public Task<PreferenceEntry> ProposeFromCandidateAsync(string projectId, string candidateName, string artifactId, string editedText, CancellationToken ct = default)
    {
        if (candidateName is not ("candidate-style.md" or "candidate-memory.md")) throw new ArgumentException("请选择当前风格或记忆候选。", nameof(candidateName));
        return ProposePreferenceCoreAsync(projectId, artifactId, editedText, candidateName, ct);
    }

    private async Task<PreferenceEntry> ProposePreferenceCoreAsync(string projectId, string artifactId, string text, string? candidateName, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId); ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        // A session spans proof collection so restore cannot swap the library between checks.
        await using var session = await StorageLease.SessionAsync(store.DataRoot, ct).ConfigureAwait(false);
        var catalog = await PreferenceCatalogAsync(ct).ConfigureAwait(false);
        var normalized = ValidatePreferenceText(text, catalog);
        long participationRevision;
        IReadOnlyList<PreferenceSource> sources;
        await using (var guard = await LockPreferencesAsync(ct).ConfigureAwait(false))
        {
            catalog = await PreferenceMetadataAsync(ct).ConfigureAwait(false);
            normalized = ValidatePreferenceText(normalized, catalog);
            var state = await LoadPreferencesAsync(ct).ConfigureAwait(false);
            participationRevision = RequireParticipation(state, projectId);
            sources = PreferenceSources(catalog, projectId);
            if (!sources.Any(s => s.Id == artifactId) || catalog.Artifacts.Any(a => a.Id == artifactId && PreferenceModelSource(a.Source)))
                throw new InvalidOperationException("主来源必须是当前可用的已解析片段（最多24项），不能以模型作品推断用户偏好。");
        }
        var candidateHash = candidateName is null ? null : await PreferenceCandidateHashAsync(projectId, candidateName, ct).ConfigureAwait(false);
        await VerifyPreferenceSourcesAsync(projectId, sources, ct).ConfigureAwait(false);
        await VerifyPreferenceSourcesAsync(projectId, sources, ct).ConfigureAwait(false);
        if (candidateName is not null && candidateHash != await PreferenceCandidateHashAsync(projectId, candidateName, ct).ConfigureAwait(false))
            throw new InvalidOperationException("生成候选已变化，请重新编辑。");
        catalog = await PreferenceCatalogAsync(ct).ConfigureAwait(false);
        normalized = ValidatePreferenceText(normalized, catalog);
        PreferenceEntry result; long revision;
        await using (var guard = await LockPreferencesAsync(ct).ConfigureAwait(false))
        {
            catalog = await PreferenceMetadataAsync(ct).ConfigureAwait(false);
            normalized = ValidatePreferenceText(normalized, catalog);
            var state = await LoadPreferencesAsync(ct).ConfigureAwait(false);
            RefreshPreferences(state, catalog);
            if (RequireParticipation(state, projectId) != participationRevision || !sources.SequenceEqual(PreferenceSources(catalog, projectId)))
                throw new InvalidOperationException("来源或参与许可已变化，请重新提议。");
            var proposalKey = SafeFiles.Hash(JsonSerializer.Serialize(new { projectId, artifactId, text = normalized, sources, candidateName, candidateHash }, Json));
            if (state.DeletedProposals.Contains(proposalKey)) throw new InvalidOperationException("此候选已删除，请明确编辑为新候选。");
            var previous = state.Entries.FirstOrDefault(p => p.ProposalKey == proposalKey);
            if (previous is not null) result = previous; // Rejected/invalidated proposals never resurrect on retry.
            else
            {
                if (state.Entries.Count >= 2000) throw new InvalidOperationException("偏好条目达到上限，请先清理。");
                var primary = sources.Single(s => s.Id == artifactId);
                result = new(Guid.NewGuid().ToString("N"), normalized, projectId, artifactId, primary.Hash, primary.Version,
                    DateTimeOffset.UtcNow, null, "candidate", true)
                {
                    Sources = sources.ToArray(), CandidateName = candidateName, CandidateHash = candidateHash,
                    ProposalKey = proposalKey, ParticipationRevision = participationRevision
                };
                state.Entries.Add(result); PreferenceChanged(state);
            }
            await SavePreferencesAsync(state, ct).ConfigureAwait(false); revision = state.Revision;
        }
        await PublishPreferencesAsync(revision).ConfigureAwait(false);
        return result;
    }

    public async Task ConfirmPreferenceAsync(string id, string text, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        await using var session = await StorageLease.SessionAsync(store.DataRoot, ct).ConfigureAwait(false);
        var catalog = await PreferenceCatalogAsync(ct).ConfigureAwait(false);
        var normalized = ValidatePreferenceText(text, catalog);
        PreferenceEntry before;
        await using (var guard = await LockPreferencesAsync(ct).ConfigureAwait(false))
        {
            catalog = await PreferenceMetadataAsync(ct).ConfigureAwait(false);
            var state = await LoadPreferencesAsync(ct).ConfigureAwait(false); before = FindPreference(state, id);
            RequireConfirmable(before);
            if (RequireParticipation(state, before.ProjectId) != before.ParticipationRevision) throw new InvalidOperationException("参与许可已撤销。");
        }
        await VerifyPreferenceSourcesAsync(before.ProjectId, before.Sources, ct).ConfigureAwait(false);
        await VerifyPreferenceSourcesAsync(before.ProjectId, before.Sources, ct).ConfigureAwait(false);
        catalog = await PreferenceCatalogAsync(ct).ConfigureAwait(false);
        normalized = ValidatePreferenceText(normalized, catalog);
        long revision;
        await using (var guard = await LockPreferencesAsync(ct).ConfigureAwait(false))
        {
            catalog = await PreferenceMetadataAsync(ct).ConfigureAwait(false);
            normalized = ValidatePreferenceText(normalized, catalog);
            var state = await LoadPreferencesAsync(ct).ConfigureAwait(false);
            RefreshPreferences(state, catalog);
            var current = FindPreference(state, id); RequireConfirmable(current);
            if (JsonSerializer.Serialize(current, Json) != JsonSerializer.Serialize(before, Json) || RequireParticipation(state, current.ProjectId) != current.ParticipationRevision || !PreferenceEvidenceValid(current, catalog))
                throw new InvalidOperationException("候选或来源已变化，请重新复核。");
            if (current.Status != "confirmed" || current.Text != normalized)
            {
                state.Entries[state.Entries.IndexOf(current)] = current with { Text = normalized, Status = "confirmed", ConfirmedAt = DateTimeOffset.UtcNow };
                PreferenceChanged(state);
            }
            await SavePreferencesAsync(state, ct).ConfigureAwait(false); revision = state.Revision;
        }
        await PublishPreferencesAsync(revision).ConfigureAwait(false);
    }

    public Task RejectPreferenceAsync(string id, CancellationToken ct = default) => MutatePreferenceAsync(id, "reject", false, ct);
    public Task DeletePreferenceAsync(string id, CancellationToken ct = default) => MutatePreferenceAsync(id, "delete", false, ct);
    public Task SetPreferenceEnabledAsync(string id, bool enabled, CancellationToken ct = default) => MutatePreferenceAsync(id, "enable", enabled, ct);

    private async Task MutatePreferenceAsync(string id, string action, bool enabled, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var catalog = await PreferenceCatalogAsync(ct).ConfigureAwait(false); long revision;
        await using (var guard = await LockPreferencesAsync(ct).ConfigureAwait(false))
        {
            catalog = await PreferenceMetadataAsync(ct).ConfigureAwait(false);
            var state = await LoadPreferencesAsync(ct).ConfigureAwait(false); RefreshPreferences(state, catalog);
            var entry = state.Entries.FirstOrDefault(p => p.Id == id);
            if (entry is null)
            {
                if (action == "delete") return;
                throw new KeyNotFoundException("偏好条目不存在。");
            }
            if (action == "delete") { state.DeletedProposals.Add(entry.ProposalKey); state.Entries.Remove(entry); }
            else if (action == "reject") state.Entries[state.Entries.IndexOf(entry)] = entry with { Status = "rejected", Enabled = false };
            else
            {
                if (enabled)
                {
                    RequireConfirmable(entry);
                    if (RequireParticipation(state, entry.ProjectId) != entry.ParticipationRevision || !PreferenceEvidenceValid(entry, catalog))
                        throw new InvalidOperationException("来源或参与许可已撤销。");
                }
                state.Entries[state.Entries.IndexOf(entry)] = entry with { Enabled = enabled };
            }
            PreferenceChanged(state); await SavePreferencesAsync(state, ct).ConfigureAwait(false); revision = state.Revision;
        }
        // Withdrawal is durable before publication; cancellation after commit cannot leave an old export.
        await PublishPreferencesAsync(revision).ConfigureAwait(false);
    }

    /// <summary>
    /// Call OUTSIDE ContextService's scope/root leases. This performs metadata refresh before
    /// taking root-shared -> preference lock, never strong GetAsync and never PublishAsync.
    /// Suitable for previews/tests only. Actual global publication must use the under-scope
    /// hook below, so an earlier snapshot cannot republish after a concurrent withdrawal.
    /// </summary>
    internal async Task<string> BuildSharedPreferencesAsync(CancellationToken ct)
    {
        await PreferenceCatalogAsync(ct).ConfigureAwait(false);
        return await BuildSharedPreferencesUnderScopeLockAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Publication must use THIS hook while holding its global scope/root-shared lease, after
    /// LockScopeAsync preflight has initialized the store and refreshed source states. It takes
    /// only root-shared and the preference lock; no initialization/discovery, source refresh,
    /// strong GetAsync or publication occurs. Global -> preference is the only nested scope
    /// order: every preference writer releases its lease before requesting global publication.
    /// </summary>
    internal async Task<string> BuildSharedPreferencesUnderScopeLockAsync(CancellationToken ct)
    {
        await using var guard = await LockPreferencesAsync(ct).ConfigureAwait(false);
        var catalog = await PreferenceMetadataAsync(ct).ConfigureAwait(false);
        var state = await LoadPreferencesAsync(ct).ConfigureAwait(false);
        RefreshPreferences(state, catalog);
        await SavePreferencesAsync(state, ct).ConfigureAwait(false);
        return string.Concat(state.Entries.Where(p => p.Status == "confirmed" && p.Enabled)
            .Select(p => p.Text).Distinct(StringComparer.Ordinal).Select(text => "- " + text + "\n"));
    }

    private async Task<(PreferenceSnapshot Snapshot, long Revision, bool Pending)> ReadPreferencesCoreAsync(CancellationToken ct)
    {
        var catalog = await PreferenceCatalogAsync(ct).ConfigureAwait(false);
        await using var guard = await LockPreferencesAsync(ct).ConfigureAwait(false);
        catalog = await PreferenceMetadataAsync(ct).ConfigureAwait(false);
        var state = await LoadPreferencesAsync(ct).ConfigureAwait(false);
        RefreshPreferences(state, catalog);
        await SavePreferencesAsync(state, ct).ConfigureAwait(false);
        // Defensive copies prevent a caller changing persisted provenance through a returned list.
        return (new(Array.AsReadOnly(state.Entries.Select(p => p with { Sources = Array.AsReadOnly(p.Sources.ToArray()) }).ToArray())), state.Revision, state.NeedsPublication);
    }

    private async Task<PreferenceCatalog> PreferenceCatalogAsync(CancellationToken ct)
    {
        await store.InitializeAsync(ct).ConfigureAwait(false);
        if (store is PortfolioStore { ReadOnlySources: true } references) await references.RefreshSourceStatesAsync(ct).ConfigureAwait(false);
        return await PreferenceMetadataAsync(ct).ConfigureAwait(false);
    }

    // Safe under a root-shared lease only after PreferenceCatalogAsync warmed initialization
    // and discovery. List is metadata-only and never attempts root-exclusive reconciliation.
    private async Task<PreferenceCatalog> PreferenceMetadataAsync(CancellationToken ct) =>
        new(await store.ProjectsAsync(ct).ConfigureAwait(false), await store.ListAsync(cancellationToken: ct).ConfigureAwait(false));

    private async Task<IAsyncDisposable> LockPreferencesAsync(CancellationToken ct)
    {
        // Initialize/discovery/source invalidation happen before any root lease. Never upgrade.
        var session = await StorageLease.SessionAsync(store.DataRoot, ct).ConfigureAwait(false);
        FileStream? root = null;
        try
        {
            root = await StorageLease.SharedAsync(store.DataRoot, ct).ConfigureAwait(false);
            return new ScopeLease(session, root, await SafeFiles.LockAsync(PreferenceStatePath + ".lock", ct).ConfigureAwait(false));
        }
        catch { if (root is not null) await root.DisposeAsync(); await session.DisposeAsync(); throw; }
    }

    private async Task<PreferenceState> LoadPreferencesAsync(CancellationToken ct)
    {
        SafeFiles.CheckPath(PreferenceStatePath);
        if (!File.Exists(PreferenceStatePath)) return new();
        var state = JsonSerializer.Deserialize<PreferenceState>(await SafeFiles.ReadBoundedAsync(PreferenceStatePath, 4 * 1024 * 1024, ct).ConfigureAwait(false), Json)
            ?? throw new InvalidDataException("偏好台账无效。");
        if (state.SchemaVersion != 1 || state.Entries is null || state.Participation is null || state.DeletedProposals is null || state.Entries.Count > 2000 ||
            state.Participation.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Value is null) ||
            state.Entries.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || p.Text is null || p.ProjectId is null || p.SourceId is null || p.SourceHash is null ||
                p.Sources is null || p.Sources.Count is < 1 or > 24 || p.Sources.Any(s => s is null || s.Id is null || s.Hash is null || s.Checksum is null) ||
                p.Status is not ("candidate" or "confirmed" or "rejected" or "invalidated")) ||
            state.Entries.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != state.Entries.Count)
            throw new InvalidDataException("偏好台账结构无效。");
        return state;
    }
    private Task SavePreferencesAsync(PreferenceState state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (state.Revision == 0 && state.Participation.Count == 0 && state.Entries.Count == 0 && state.DeletedProposals.Count == 0 && !state.NeedsPublication && !File.Exists(PreferenceStatePath))
            return Task.CompletedTask; // Mere discovery/reading must not opt the library into preference revocation work.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, Json);
        if (bytes.Length > 4 * 1024 * 1024) throw new InvalidOperationException("偏好台账达到大小上限，请先清理。");
        return SafeFiles.AtomicWriteAsync(PreferenceStatePath, bytes, ct);
    }
    private static void PreferenceChanged(PreferenceState state) { state.Revision++; state.NeedsPublication = true; }
    private static PreferenceEntry FindPreference(PreferenceState state, string id) => state.Entries.FirstOrDefault(p => p.Id == id) ?? throw new KeyNotFoundException("偏好条目不存在。");
    private static long RequireParticipation(PreferenceState state, string projectId) =>
        state.Participation.TryGetValue(projectId, out var permission) && permission.Enabled ? permission.Revision : throw new InvalidOperationException("请先允许此项目参与通用偏好。");
    private static void RequireConfirmable(PreferenceEntry entry)
    { if (entry.Status is not ("candidate" or "confirmed")) throw new InvalidOperationException("此条目已拒绝或失效，请重新提议并复核。"); }

    private static bool ParsedPreferenceSource(ArtifactRecord a) =>
        a.Hash.Length == 64 && a.Hash.All(Uri.IsHexDigit) && !string.IsNullOrWhiteSpace(a.Preview) &&
        a.ExtractionStatus is "ready" or "已提取" or "已提取（截断）" && a.Source.Trim() != "应用" && !SecretName(a.OriginalName);
    private static bool PreferenceModelSource(string source) => Regex.IsMatch(source.Trim(), "^(应用|agent|assistant|model|ai|模型|助手)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static PreferenceSource PreferenceProof(ArtifactRecord a) => new(a.Id, a.Hash, a.Version,
        SafeFiles.Hash(JsonSerializer.Serialize(new { a.ProjectId, a.Hash, a.Version, a.Title, a.OriginalName, a.ObjectPath, a.Source, a.ExtractionStatus, Preview = a.Preview[..Math.Min(a.Preview.Length, 2048)] }, Json)));
    private static IReadOnlyList<PreferenceSource> PreferenceSources(PreferenceCatalog catalog, string projectId) => catalog.Artifacts
        .Where(a => a.ProjectId == projectId && ParsedPreferenceSource(a))
        .OrderBy(a => a.Id, StringComparer.Ordinal).Take(24).Select(PreferenceProof).ToArray();
    private static bool PreferenceEvidenceValid(PreferenceEntry entry, PreferenceCatalog catalog) =>
        catalog.Projects.Any(p => p.Id == entry.ProjectId) && entry.Sources.Count is > 0 and <= 24 &&
        entry.Sources.Any(s => s.Id == entry.SourceId && s.Hash == entry.SourceHash && s.Version == entry.SourceVersion) &&
        catalog.Artifacts.Any(a => a.Id == entry.SourceId && !PreferenceModelSource(a.Source)) &&
        entry.Sources.All(s => catalog.Artifacts.Any(a => a.Id == s.Id && a.ProjectId == entry.ProjectId && ParsedPreferenceSource(a) && PreferenceProof(a) == s));

    private async Task VerifyPreferenceSourcesAsync(string projectId, IReadOnlyList<PreferenceSource> sources, CancellationToken ct)
    {
        foreach (var proof in sources)
        {
            // Explicit actions only: reference GetAsync checks the source SHA and may acquire
            // root-exclusive to revoke stale records. No preference/context lock is held here.
            var current = await store.GetAsync(proof.Id, ct).ConfigureAwait(false);
            if (current is null || current.ProjectId != projectId || !ParsedPreferenceSource(current) || PreferenceProof(current) != proof)
                throw new InvalidOperationException("来源已变化、缺失或不可用，请重新解析并提议。");
        }
    }

    private async Task<string> PreferenceCandidateHashAsync(string projectId, string candidateName, CancellationToken ct)
    {
        var scope = Scope(projectId);
        await using var guard = await LockScopeAsync(scope.Lock, ct).ConfigureAwait(false);
        var state = await LoadAsync(scope, ct).ConfigureAwait(false);
        var input = await InputAsync(projectId, ct).ConfigureAwait(false);
        if (state.CandidateFingerprint != input.Fingerprint || !state.Candidates.TryGetValue(candidateName, out var text))
            throw new InvalidOperationException("生成候选不存在或来源已变化，请重新生成。");
        return SafeFiles.Hash(text);
    }

    private static void InvalidatePreferences(PreferenceState state, Func<PreferenceEntry, bool> predicate)
    {
        for (var i = 0; i < state.Entries.Count; i++)
        {
            var entry = state.Entries[i];
            if (entry.Status is "candidate" or "confirmed" && predicate(entry))
            { state.Entries[i] = entry with { Status = "invalidated", Enabled = false }; PreferenceChanged(state); }
        }
    }
    private static void RefreshPreferences(PreferenceState state, PreferenceCatalog catalog)
    {
        foreach (var (projectId, permission) in state.Participation)
            if (permission.Enabled && !catalog.Projects.Any(p => p.Id == projectId))
            { permission.Enabled = false; permission.Revision++; PreferenceChanged(state); }
        InvalidatePreferences(state, entry => !state.Participation.TryGetValue(entry.ProjectId, out var permission) || !permission.Enabled ||
            permission.Revision != entry.ParticipationRevision || !PreferenceEvidenceValid(entry, catalog) || !PreferenceTextValid(entry.Text, catalog));
    }

    // A deliberately conservative syntax/identifier filter, not a semantic privacy guarantee.
    // The UI must explicitly ask a human to review edited general wording before confirmation.
    private static string ValidatePreferenceText(string text, PreferenceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!PreferenceTextValid(text, catalog)) throw new ArgumentException("请只填写500字以内的通用写作、格式、工作流程或交互偏好；不要包含项目事实、名称、路径、标识、凭据、原文摘录或指令标记。", nameof(text));
        // Compatibility normalization only inspects full-width markers. Preserve the
        // human's punctuation and wording in the stored/shared text.
        return text.Trim().Normalize(NormalizationForm.FormC);
    }
    private static bool PreferenceTextValid(string? text, PreferenceCatalog catalog)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 500 || text.Any(c => char.IsControl(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.Surrogate or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator)) return false;
        text = text.Trim().Normalize(NormalizationForm.FormKC);
        if (text.Length > 500 || Regex.IsMatch(text, @"[\\/\[\]`<>#{}]|\b\w+:|@|\b[\w.-]+\.(?:md|txt|json|pdf|docx|xlsx|cs|com|net|org)\b|\b[a-f0-9]{16,}\b|\b\w+_\w+\b|\b[A-Za-z]+[-_]\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        if (Redact(text) != text || Regex.IsMatch(text, @"(?i)api[ _-]?key|password|credential|authorization|bearer|secret|token|密钥|密码|凭据|令牌|忽略.{0,12}(指令|规则|之前|以上)|指令|system\s*prompt|developer|ignore\s+(previous|all|prior)|越权|项目事实|本项目|该项目|此项目|这个项目|\b(?:this|our)\s+(?:project|repository)\b|客户|客戶|账户|帳戶|订单|合同编号|\bid\s*[:=]?\s*\d+")) return false;
        if (!Regex.IsMatch(text, @"偏好|喜欢|希望|请|优先|回答时|写作时|沟通时|(?i)\b(prefer|please|always|use|avoid)\b") ||
            !Regex.IsMatch(text, @"回答|回复|解释|推理|讲解|写作|格式|排版|标点|段落|步骤|流程|确认|沟通|交互|图表|可视化|表格|简洁|详细|语言|中文|英文|字体|标题|引用|(?i)\b(response|answer|writing|format|workflow|interaction|explanation|paragraph|heading|concise|stepwise|steps|tone|language|bullets|table)\b")) return false;
        bool ContainsIdentifier(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            value = value.Normalize(NormalizationForm.FormKC);
            if (value.Length >= 3) return text.Contains(value, StringComparison.OrdinalIgnoreCase);
            return Regex.IsMatch(text, @"(?<![\p{L}\p{N}])" + Regex.Escape(value) + @"(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        if (catalog.Projects.Any(p => ContainsIdentifier(p.Id) || ContainsIdentifier(p.Name) || ContainsIdentifier(p.RootPath))) return false;
        foreach (var a in catalog.Artifacts)
        {
            if (ContainsIdentifier(a.Id) || ContainsIdentifier(a.Hash) || ContainsIdentifier(a.Title) || ContainsIdentifier(a.OriginalName) || ContainsIdentifier(a.ObjectPath) || ContainsIdentifier(Path.GetFileNameWithoutExtension(a.OriginalName))) return false;
            // Reject long literal source excerpts. Short explicit general preferences may be
            // reproduced, but multi-sentence/private original content cannot pass unchanged.
            var preview = a.Preview.Normalize(NormalizationForm.FormKC);
            for (var i = 0; i + 24 <= text.Length; i++)
                if (preview.Contains(text.Substring(i, 24), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private async Task PublishPreferencesAsync(long revision)
    {
        // Never called from the build hook or with a preference/root lease held. Complete
        // committed withdrawal even if the original caller cancelled immediately afterwards.
        try { await PublishAsync(null, CancellationToken.None).ConfigureAwait(false); }
        catch
        {
            // Reconciliation may reject a damaged/externally edited entry. Withdrawal wins:
            // retain editable state/manual documents but remove every old exported generation.
            var scope = Scope(null);
            await using var guard = await LockScopeAsync(scope.Lock, CancellationToken.None).ConfigureAwait(false);
            var state = await LoadAsync(scope, CancellationToken.None).ConfigureAwait(false);
            state.NeedsPurge = true;
            await SaveStateAsync(scope, state, CancellationToken.None).ConfigureAwait(false);
            await PurgeAsync(scope, state, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        await using var preferenceGuard = await LockPreferencesAsync(CancellationToken.None).ConfigureAwait(false);
        var preferences = await LoadPreferencesAsync(CancellationToken.None).ConfigureAwait(false);
        if (preferences.Revision == revision)
        { preferences.NeedsPublication = false; await SavePreferencesAsync(preferences, CancellationToken.None).ConfigureAwait(false); }
    }
}
