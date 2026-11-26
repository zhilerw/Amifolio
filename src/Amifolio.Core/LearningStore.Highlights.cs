namespace Amifolio.Core;

public sealed record PreferenceHighlights(IReadOnlyList<LearningEntry> Active, LearningEntry? Candidate, bool Shared);

public sealed partial class LearningStore
{
    public Task<PreferenceHighlights> ReadPreferenceHighlightsAsync(string? projectId, CancellationToken ct = default)
    {
        var scope = Key(projectId);
        if (!HasState) return Task.FromResult(new PreferenceHighlights([], null, false));
        return UseAsync(db =>
        {
            var active = Read("active").Where(entry => entry.Manual || HasEvidence(entry)).Take(2).ToArray();
            var candidate = Read("candidate").FirstOrDefault(entry => entry.EvidenceIds.Count > 0 && HasEvidence(entry));
            return new PreferenceHighlights(active, candidate, Policy(db, scope).Share);

            List<LearningEntry> Read(string state) => Rows<LearningEntry>(db, "profiles", """
                scope=$scope AND json_extract(payload,'$.state')=$state
                AND json_extract(payload,'$.kind') IN ('style','memory')
                AND json_extract(payload,'$.dimension')<>'project_fact'
                ORDER BY updated DESC, id LIMIT 24
                """, ("$scope", scope), ("$state", state));

            bool HasEvidence(LearningEntry entry) => EntryEvidence(db, entry).Count == entry.EvidenceIds.Count;
        }, ct);
    }
}
