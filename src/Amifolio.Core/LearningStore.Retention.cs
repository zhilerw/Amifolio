using System.Text.Json;

namespace Amifolio.Core;

public sealed record LearningRetentionResult(int JobsRemoved, int EvidenceRemoved);

public sealed partial class LearningStore
{
    /// <summary>
    /// Reclaims redundant job logs and unreachable evidence, at most 1000 rows of each
    /// per call. Receipt identities/tombstones, consent, profiles and undo history are
    /// permanent roots. Freed SQLite pages are reused; this does not run VACUUM.
    /// </summary>
    public Task<LearningRetentionResult> CompactAsync(CancellationToken ct = default) => UseAsync(db =>
    {
        // Validate cold roots before relying on compact ledger evidence IDs.
        LearningColdStorage.Validate(db.Connection,db.Transaction,ct);
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var inputJobs = new HashSet<string>(StringComparer.Ordinal);
        var inputs = new HashSet<(long Sequence, int Version)>();
        using (var command = db.Command("SELECT seq,processed,job_id,payload FROM receipts"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                using var document = RetentionDocument(reader.GetString(3));
                var summary = document.RootElement;
                RetentionEvidenceIds(summary, reachable);
                var identity = RetentionSummaryIdentity(summary);
                if (identity.Sequence != reader.GetInt64(0)) throw RetentionCorruption();
                var processed = reader.GetInt64(1);
                if (processed is not (-1 or 0 or 1)) throw RetentionCorruption();
                if (processed != 1)
                {
                    inputs.Add(identity);
                    if (!reader.IsDBNull(2)) inputJobs.Add(reader.GetString(2));
                }
            }

        // Include every state and scope, including deleted/manual/disabled profiles,
        // no-change receipts and revisions only reachable through user undo.
        foreach (var table in new[] { "profiles", "history" })
        {
            using var command = db.Command("SELECT payload FROM " + table);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                using var document = RetentionDocument(reader.GetString(0));
                RetentionEvidenceIds(document.RootElement, reachable);
            }
        }

        var cutoff = Now.AddDays(-30).ToUnixTimeMilliseconds();
        var jobs = new List<string>(1000);
        long terminalRank = 0;
        using (var command = db.Command("SELECT id,state,created,payload FROM jobs ORDER BY created DESC,id DESC"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var id = reader.GetString(0); var state = reader.GetString(1);
                using var document = RetentionDocument(reader.GetString(3));
                var job = document.RootElement;
                if (RetentionString(RetentionProperty(job, "id")) != id || RetentionString(RetentionProperty(job, "state")) != state)
                    throw RetentionCorruption();
                var jobEvidence = new HashSet<string>(StringComparer.Ordinal);
                var hasInput = inputJobs.Contains(id);
                var summaries = RetentionArray(job, "summaries");
                foreach (var summary in summaries.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    RetentionObject(summary);
                    RetentionEvidenceIds(summary, jobEvidence);
                    hasInput |= inputs.Contains(RetentionSummaryIdentity(summary));
                }
                foreach (var source in RetentionArray(job, "evidence").EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    RetentionObject(source);
                    var evidence = RetentionProperty(source, "evidence"); RetentionObject(evidence);
                    jobEvidence.Add(RetentionString(RetentionProperty(evidence, "id")));
                }
                var terminal = state is "completed" or "failed" or "interrupted" or "cancelled" or "superseded";
                if (terminal) terminalRank++;
                // An old failure/cancellation is redundant only when its inputs no longer
                // need processing. Unknown states and all running jobs are always retained.
                var redundant = terminal && !hasInput && terminalRank > 50 &&
                    (terminalRank > 1000 || state == "completed" && reader.GetInt64(2) < cutoff);
                if (redundant && jobs.Count < 1000) jobs.Add(id);
                else reachable.UnionWith(jobEvidence);
            }

        // Finish validating all reference roots before the first mutation. Both deletion
        // phases share UseAsync's writer transaction and its final cancellation check.
        var jobsRemoved = 0;
        foreach (var id in jobs)
        {
            ct.ThrowIfCancellationRequested();
            jobsRemoved += db.Exec("DELETE FROM jobs WHERE id=$id", ("$id", id));
        }
        var evidenceIds = new List<string>(1000);
        using (var command = db.Command("SELECT id FROM evidence ORDER BY rowid"))
        using (var reader = command.ExecuteReader())
            while (evidenceIds.Count < 1000 && reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                var id = reader.GetString(0);
                // valid=1 alone is not a root: old, unreferenced source revisions are
                // reclaimable too. Invalid evidence still referenced by undo is retained.
                if (!reachable.Contains(id)) evidenceIds.Add(id);
            }
        var evidenceRemoved = 0;
        foreach (var id in evidenceIds)
        {
            ct.ThrowIfCancellationRequested();
            evidenceRemoved += db.Exec("DELETE FROM evidence WHERE id=$id", ("$id", id));
        }
        return new LearningRetentionResult(jobsRemoved, evidenceRemoved);
    }, ct);

    private static IOException RetentionCorruption() => new("学习清理遇到无法确认的引用数据，未提交任何删除。");
    private static JsonDocument RetentionDocument(string payload)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 24 }); }
        catch (JsonException e) { throw new IOException("学习清理遇到损坏的数据，未提交任何删除。", e); }
        try { RetentionObject(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    private static void RetentionObject(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) throw RetentionCorruption();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw RetentionCorruption();
    }
    private static JsonElement RetentionProperty(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property)) throw RetentionCorruption();
        return property;
    }
    private static JsonElement RetentionArray(JsonElement value, string name)
    {
        var array = RetentionProperty(value, name);
        if (array.ValueKind != JsonValueKind.Array) throw RetentionCorruption();
        return array;
    }
    private static string RetentionString(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString())) throw RetentionCorruption();
        return value.GetString()!;
    }
    private static void RetentionEvidenceIds(JsonElement value, HashSet<string> reachable)
    {
        foreach (var id in RetentionArray(value, "evidenceIds").EnumerateArray()) reachable.Add(RetentionString(id));
    }
    private static (long Sequence, int Version) RetentionSummaryIdentity(JsonElement summary)
    {
        var sequence = RetentionProperty(summary, "sequence"); var version = RetentionProperty(summary, "version");
        if (sequence.ValueKind != JsonValueKind.Number || !sequence.TryGetInt64(out var seq) || seq <= 0 ||
            version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var revision) || revision <= 0) throw RetentionCorruption();
        return (seq, revision);
    }
}
