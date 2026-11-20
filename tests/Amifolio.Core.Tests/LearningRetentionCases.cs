using Amifolio.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;

internal static class LearningRetentionCases
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-15T12:00:00Z");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static (string, Func<Fixture, Task>)[] All =>
    [
        ("learning retention empty state is idempotent and grants no permissions", async f =>
        {
            var s = await Store(f); var before = Dump(f.DataRoot);
            Check(await s.CompactAsync() == new LearningRetentionResult(0, 0), "empty GC changed counts");
            Check(await s.CompactAsync() == new LearningRetentionResult(0, 0), "repeat GC changed counts");
            Check(Dump(f.DataRoot) == before && !(await s.PolicyAsync("p")).Capture, "GC changed empty permissions/state");
        }),
        ("learning retention old completed jobs retain at least newest fifty", async f =>
        {
            var s = await Store(f); using (var db = Open(f.DataRoot)) Jobs(db, 60, At.AddDays(-40));
            Check(await s.CompactAsync() == new LearningRetentionResult(10, 0), "age cleanup count");
            using var check = Open(f.DataRoot);
            Check(Count(check, "jobs") == 50 && Exists(check, "jobs", "job-59") && !Exists(check, "jobs", "job-9"), "newest fifty lost");
        }),
        ("learning retention thirty day grace is strict and recent jobs survive", async f =>
        {
            var s = await Store(f); using (var db = Open(f.DataRoot)) Jobs(db, 51, At.AddDays(-30));
            Check(await s.CompactAsync() == new LearningRetentionResult(0, 0), "exact boundary was pruned");
            s = new LearningStore(f.DataRoot, new Clock(At.AddMilliseconds(1)));
            Check(await s.CompactAsync() == new LearningRetentionResult(1, 0), "older than thirty days was retained beyond minimum");
        }),
        ("learning retention terminal cap and each job deletion batch are bounded", async f =>
        {
            var s = await Store(f); using (var db = Open(f.DataRoot)) Jobs(db, 2205, At.AddHours(-1));
            Check((await s.CompactAsync()).JobsRemoved == 1000, "first deletion exceeded/missed batch");
            Check((await s.CompactAsync()).JobsRemoved == 205, "cap did not converge");
            Check((await s.CompactAsync()).JobsRemoved == 0, "latest thousand were pruned");
            using var db2 = Open(f.DataRoot);
            Check(Count(db2, "jobs") == 1000 && Exists(db2, "jobs", "job-2204") && !Exists(db2, "jobs", "job-1204"), "cap kept wrong jobs");
        }),
        ("learning retention running retryable and pending job inputs are protected", async f =>
        {
            var s = await Store(f);
            using (var db = Open(f.DataRoot))
            {
                Jobs(db, 1001, At.AddHours(-1));
                foreach (var state in new[] { "running", "failed", "interrupted", "cancelled" })
                {
                    var seq = 100 + Array.IndexOf(new[] { "running", "failed", "interrupted", "cancelled" }, state);
                    Receipt(db, seq, -1, state, [state + "-proof"]);
                    Job(db, state, state, At.AddDays(-90), [Summary(seq, [state + "-proof"])], [Evidence(state + "-proof")]);
                    PutEvidence(db, state + "-proof");
                }
                Receipt(db, 200, 0, "pending", []); Job(db, "pending", "completed", At.AddDays(-90), [Summary(200, [])]);
                Receipt(db, 201, -1, null, []); Job(db, "detached", "failed", At.AddDays(-90), [Summary(201, [])]);
                Job(db, "unknown", "queued", At.AddDays(-90));
            }
            await s.CompactAsync(); using var check = Open(f.DataRoot);
            foreach (var id in new[] { "running", "failed", "interrupted", "cancelled", "pending", "detached", "unknown" })
                Check(Exists(check, "jobs", id), "protected input lost: " + id);
            Check(Count(check, "evidence") == 4 && Count(check, "receipts") == 6, "retry evidence or receipts lost");
        }),
        ("learning retention superseded terminal jobs can expire beyond the cap", async f =>
        {
            var s = await Store(f); using (var db = Open(f.DataRoot))
            {
                Jobs(db, 1000, At.AddHours(-1));
                foreach (var state in new[] { "failed", "interrupted", "cancelled", "superseded" }) Job(db, state, state, At.AddDays(-90));
            }
            Check((await s.CompactAsync()).JobsRemoved == 4, "unreferenced superseded terminals never reclaimed");
        }),
        ("learning retention all receipt profile history and retained job roots protect evidence", async f =>
        {
            var s = await Store(f); var ids = new[] { "deleted-receipt", "no-change", "pending-source", "failed-source", "global-profile", "disabled-profile", "history", "job-proof", "job-summary" };
            using (var db = Open(f.DataRoot))
            {
                foreach (var id in ids) PutEvidence(db, id, valid: id == "history" ? 0 : 1);
                Receipt(db, 1, 1, null, [ids[0]], "deleted"); Receipt(db, 2, 1, null, [ids[1]], "no_change");
                Receipt(db, 3, 0, null, [ids[2]], "pending_evidence"); Receipt(db, 4, -1, null, [ids[3]], "failed");
                Entry(db, "global", [ids[4]], scope: "global"); Entry(db, "disabled", [ids[5]], state: "disabled");
                Exec(db, "INSERT INTO history(entry_id,payload) VALUES('undo',$p)", ("$p", Encode(Profile("undo", [ids[6]]))));
                Job(db, "retained", "completed", At.AddHours(-1), [Summary(999, [ids[8]])], [Evidence(ids[7])]);
                PutEvidence(db, "invalid-orphan", 0); PutEvidence(db, "valid-old-revision");
            }
            Check(await s.CompactAsync() == new LearningRetentionResult(0, 2), "reachability cleanup counts");
            using var check = Open(f.DataRoot);
            foreach (var id in ids) Check(Exists(check, "evidence", id), "reachable evidence removed: " + id);
        }),
        ("learning retention valid and invalid unreferenced evidence batches converge", async f =>
        {
            var s = await Store(f); using (var db = Open(f.DataRoot)) EvidenceRows(db, 2300);
            Check(await s.CompactAsync() == new LearningRetentionResult(0, 1000), "first evidence batch");
            Check((await s.CompactAsync()).EvidenceRemoved == 1000, "second evidence batch");
            Check((await s.CompactAsync()).EvidenceRemoved == 300, "last evidence batch");
            Check((await s.CompactAsync()).EvidenceRemoved == 0, "evidence GC not idempotent");
        }),
        ("learning retention evidence from removed jobs is reclaimed in the same transaction", async f =>
        {
            var s = await Store(f); using (var db = Open(f.DataRoot))
            {
                Jobs(db, 50, At.AddHours(-1)); PutEvidence(db, "discarded"); PutEvidence(db, "retained");
                Job(db, "expired", "completed", At.AddDays(-40), [Summary(999, ["discarded"])], [Evidence("discarded")]);
                Job(db, "running", "running", At.AddDays(-90), evidence: [Evidence("retained")]);
            }
            Check(await s.CompactAsync() == new LearningRetentionResult(1, 1), "deleted job still rooted garbage");
            using var check = Open(f.DataRoot); Check(Exists(check, "evidence", "retained"), "running proof removed");
        }),
        ("learning retention history keeps actual undo backing proof alive", async f =>
        {
            var s = await Store(f); using (var db = Open(f.DataRoot)) { Entry(db, "undo", ["undo-proof"]); PutEvidence(db, "undo-proof"); }
            var edited = await s.EditEntryAsync("undo", "人工修订", 1);
            Check(await s.CompactAsync() == new LearningRetentionResult(0, 0), "history proof collected");
            var undone = await s.UndoEntryAsync("undo", edited.Revision);
            Check(undone.State == "active" && !undone.Manual && undone.EvidenceIds.SequenceEqual(new[] { "undo-proof" }), "undo lost original automatic proof");
        }),
        ("learning retention receipt tombstone and suppression never resurrect user deletions", async f =>
        {
            var s = new LearningStore(f.DataRoot); await LearningCases.Enable(s);
            var receipt = await s.CaptureAsync(LearningCases.Request(), LearningCases.Proof());
            var job = (await s.ClaimAsync("p", "chat", true))!;
            var proposal = new MemoryProposal("style", "verbosity", "brief", "偏好简洁回答", "project", "", job.Evidence.Select(e => e.Evidence.Id).ToArray());
            await s.CompleteAsync(job.Id, job.Token, [proposal], proposal.EvidenceIds.ToHashSet());
            var entry = (await s.SnapshotAsync("p")).Entries.Single(e => e.Kind == "style");
            await s.SetEntryStateAsync(entry.Id, "deleted", entry.Revision); await s.DeleteSummaryAsync(receipt.Sequence);
            using (var db = Open(f.DataRoot)) Jobs(db, 60, At.AddDays(-90));
            var before = ProtectedDump(f.DataRoot); await s.CompactAsync();
            Check(ProtectedDump(f.DataRoot) == before, "GC changed deletion identities, suppression, seq or watermark");
            await LearningCases.Throws(() => s.CaptureAsync(LearningCases.Request(), LearningCases.Proof()));
            await s.CaptureAsync(LearningCases.Request() with { TurnId = "turn2" }, LearningCases.Proof() with { TurnId = "turn2" });
            var next = (await s.ClaimAsync("p", "chat", true))!;
            await s.CompleteAsync(next.Id, next.Token, [proposal with { EvidenceIds = next.Evidence.Select(e => e.Evidence.Id).ToArray() }], next.Evidence.Select(e => e.Evidence.Id).ToHashSet());
            Check(!(await s.SnapshotAsync("p")).Entries.Any(e => e.Dimension == "verbosity" && e.State == "active"), "GC resurrected suppressed preference");
            using var check = Open(f.DataRoot); Check(Number(check, "SELECT MAX(seq) FROM receipts") == 2, "receipt sequence reused");
        }),
        ("learning retention preserves every protected table and projection byte", async f =>
        {
            var s = await Store(f); using (var db = Open(f.DataRoot))
            {
                Jobs(db, 60, At.AddDays(-40));
                Receipt(db, 10, 1, null, ["proof"], "no_change"); Entry(db, "entry", ["proof"]); PutEvidence(db, "proof");
                Exec(db, "INSERT INTO suppressions VALUES('keep','{\"scope\":\"p\",\"dimension\":\"tone\"}')");
                Exec(db, "INSERT INTO history(entry_id,payload) VALUES('entry',$p)", ("$p", Encode(Profile("entry", ["proof"]))));
            }
            var projection = s.ProjectionPath("p"); Directory.CreateDirectory(Path.GetDirectoryName(projection)!); System.IO.File.WriteAllText(projection, "unchanged synthetic projection");
            var before = ProtectedDump(f.DataRoot); var bytes = System.IO.File.ReadAllBytes(projection);
            Check((await s.CompactAsync()).JobsRemoved == 10, "cleanup did not execute");
            Check(ProtectedDump(f.DataRoot) == before && System.IO.File.ReadAllBytes(projection).SequenceEqual(bytes), "protected state/projection changed");
        }),
        ("learning retention malformed reference sources fail closed without partial changes", async f =>
        {
            foreach (var source in new[] { "receipts", "profiles", "history", "jobs" })
            foreach (var payload in new[] { "{", "null", "{}", "{\"evidenceIds\":null}", "{\"evidenceIds\":[7]}", "{\"evidenceIds\":[],\"EvidenceIds\":[\"orphan\"]}" })
            {
                var root = Path.Combine(f.DataRoot, source + Guid.NewGuid().ToString("N")); var s = new LearningStore(root, new Clock(At)); await s.PolicyAsync("p");
                using (var db = Open(root))
                {
                    Jobs(db, 60, At.AddDays(-40)); PutEvidence(db, "orphan");
                    if (source == "receipts") Receipt(db, 1, 1, null, []);
                    if (source == "profiles") Entry(db, "bad", []);
                    if (source == "history") Exec(db, "INSERT INTO history(entry_id,payload) VALUES('bad','{}')");
                    Exec(db, "UPDATE " + source + " SET payload=$p WHERE rowid=(SELECT MIN(rowid) FROM " + source + ")", ("$p", payload));
                }
                var before = Dump(root); await Reject(() => s.CompactAsync()); Check(Dump(root) == before, "corruption allowed partial GC: " + source);
            }
            // Well-formed job envelopes must not hide malformed nested reference roots.
            foreach (var mutation in new[]
            {
                "json_set(payload,'$.summaries',NULL)",
                "json_set(payload,'$.summaries',json('[{\"sequence\":1,\"version\":1,\"evidenceIds\":null}]'))",
                "json_set(payload,'$.summaries',json('[{\"sequence\":1,\"version\":1,\"evidenceIds\":[],\"EvidenceIds\":[\"orphan\"]}]'))",
                "json_set(payload,'$.evidence',json('[{\"evidence\":{\"id\":null}}]'))",
                "json_set(payload,'$.state','running')",
            })
            {
                var root = Path.Combine(f.DataRoot, "nested-" + Guid.NewGuid().ToString("N"));
                var s = new LearningStore(root, new Clock(At)); await s.PolicyAsync("p");
                using (var db = Open(root))
                {
                    Jobs(db, 60, At.AddDays(-40)); PutEvidence(db, "orphan");
                    Exec(db, "UPDATE jobs SET payload=" + mutation + " WHERE id='job-0'");
                }
                var before = Dump(root); await Reject(() => s.CompactAsync()); Check(Dump(root) == before, "nested corruption allowed GC");
            }
        }),
        ("learning retention late database failure rolls back already deleted jobs", async f =>
        {
            var s = await Store(f); using (var db = Open(f.DataRoot))
            {
                Jobs(db, 60, At.AddDays(-40)); PutEvidence(db, "orphan");
                Exec(db, "CREATE TRIGGER retention_fail BEFORE DELETE ON evidence BEGIN SELECT RAISE(ABORT,'synthetic late failure'); END");
            }
            var before = Dump(f.DataRoot); await Reject(() => s.CompactAsync()); Check(Dump(f.DataRoot) == before, "job deletion survived evidence failure");
        }),
        ("learning retention cancellation before and during writes rolls back the batch", async f =>
        {
            var s = await Store(f); using (var db = Open(f.DataRoot))
            {
                Jobs(db, 2200, At.AddDays(-40));
                // A large rollback journal gives the observer a durable signal during writes.
                Exec(db, "UPDATE jobs SET result=$p", ("$p", new string('x', 8192)));
                Exec(db, "CREATE TRIGGER retention_slow AFTER DELETE ON jobs BEGIN SELECT sum(x) FROM (WITH RECURSIVE n(x) AS(SELECT 1 UNION ALL SELECT x+1 FROM n WHERE x<20000) SELECT x FROM n); END");
            }
            var before = Dump(f.DataRoot); using (var cancelled = new CancellationTokenSource())
            { cancelled.Cancel(); await Cancelled(() => s.CompactAsync(cancelled.Token)); }
            using var cts = new CancellationTokenSource(); var task = s.CompactAsync(cts.Token);
            var journal = Path.Combine(f.DataRoot, "learning", "state.sqlite-journal"); var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!task.IsCompleted && DateTime.UtcNow < deadline && (!System.IO.File.Exists(journal) || new FileInfo(journal).Length < 8192)) await Task.Delay(1);
            Check(!task.IsCompleted && System.IO.File.Exists(journal) && new FileInfo(journal).Length >= 8192, "did not observe in-flight deletion journal");
            cts.Cancel(); await Cancelled(() => task); Check(Dump(f.DataRoot) == before, "cancelled batch committed deletes");
        }),
        ("learning retention repeated synthetic churn reuses freed database pages", async f =>
        {
            var s = await Store(f); long warmed = 0;
            for (var cycle = 0; cycle < 5; cycle++)
            {
                using (var db = Open(f.DataRoot)) { Jobs(db, 1200, At.AddDays(-40), "cycle-" + cycle + "-"); EvidenceRows(db, 1200, "cycle-" + cycle + "-"); }
                var first = await s.CompactAsync(); var second = await s.CompactAsync();
                Check(first.JobsRemoved <= 1000 && first.EvidenceRemoved == 1000 && second.EvidenceRemoved == 200, "churn deletion bound");
                using var check = Open(f.DataRoot); var pages = Number(check, "PRAGMA page_count");
                Check(Count(check, "jobs") == 50 && Count(check, "evidence") == 0 && Number(check, "PRAGMA freelist_count") > 0, "garbage/free pages did not converge");
                if (cycle == 1) warmed = pages;
                if (cycle > 1) Check(pages <= warmed + 32, "database grew instead of reusing free pages");
            }
        }),
    ];

    private static async Task<LearningStore> Store(Fixture f) { var s = new LearningStore(f.DataRoot, new Clock(At)); await s.PolicyAsync("p"); return s; }
    private sealed class Clock(DateTimeOffset value) : TimeProvider { public override DateTimeOffset GetUtcNow() => value; }
    private static SqliteConnection Open(string root)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "learning", "state.sqlite"), Pooling = false, Mode = SqliteOpenMode.ReadWrite }.ToString()); db.Open(); return db;
    }
    private static string Encode<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static LearningEvidence Evidence(string id) => new(new(id, "以后请简洁回答", "session", "turn", "p", true, At), "synthetic-user-hash", "synthetic-source", true);
    private static LearningSummary Summary(long seq, string[] ids, string state = "verified") => new(seq, 1, "p", "session", "turn-" + seq, "合成摘要", "completed", state, At, ids, []);
    private static LearningEntry Profile(string id, string[] ids, string scope = "p", string state = "active") => new(id, scope, "style", "verbosity", "brief", "偏好简洁回答", "", state, "synthetic", false, 1, At, ids);
    private static void PutEvidence(SqliteConnection db, string id, int valid = 1) => Exec(db, "INSERT INTO evidence VALUES($id,'p',$v,$p)", ("$id", id), ("$v", valid), ("$p", Encode(Evidence(id))));
    private static void Receipt(SqliteConnection db, long seq, int processed, string? job, string[] ids, string state = "verified") =>
        Exec(db, "INSERT INTO receipts(seq,id,scope,fingerprint,payload,eligible,processed,job_id) VALUES($seq,$id,'p','stable-input-fingerprint',$p,1,$processed,$job)", ("$seq", seq), ("$id", "receipt-" + seq), ("$p", Encode(Summary(seq, ids, state))), ("$processed", processed), ("$job", job));
    private static void Entry(SqliteConnection db, string id, string[] ids, string scope = "p", string state = "active") =>
        Exec(db, "INSERT INTO profiles VALUES($id,$scope,1,$p)", ("$id", id), ("$scope", scope), ("$p", Encode(Profile(id, ids, scope, state))));
    private static void Job(SqliteConnection db, string id, string state, DateTimeOffset created, LearningSummary[]? summaries = null, LearningEvidence[]? evidence = null)
    {
        var job = new LearningJob(id, "p", "chat", "synthetic-token", 0, 0, state, At.AddMinutes(5), "synthetic-fingerprint", summaries ?? [], evidence ?? []);
        Exec(db, "INSERT INTO jobs VALUES($id,'p',$s,$created,$p,NULL)", ("$id", id), ("$s", state), ("$created", created.ToUnixTimeMilliseconds()), ("$p", Encode(job)));
    }
    private static void Jobs(SqliteConnection db, int count, DateTimeOffset created, string prefix = "job-")
    {
        Exec(db, "BEGIN IMMEDIATE"); for (var i = 0; i < count; i++) Job(db, prefix + i, "completed", created.AddMilliseconds(i)); Exec(db, "COMMIT");
    }
    private static void EvidenceRows(SqliteConnection db, int count, string prefix = "orphan-")
    {
        Exec(db, "BEGIN IMMEDIATE"); for (var i = 0; i < count; i++) PutEvidence(db, prefix + i, i % 2); Exec(db, "COMMIT");
    }
    private static bool Exists(SqliteConnection db, string table, string id) => Number(db, "SELECT COUNT(*) FROM " + table + " WHERE id=$id", ("$id", id)) == 1;
    private static long Count(SqliteConnection db, string table) => Number(db, "SELECT COUNT(*) FROM " + table);
    private static long Number(SqliteConnection db, string sql, params (string, object?)[] args) { using var c = Command(db, sql, args); return Convert.ToInt64(c.ExecuteScalar()); }
    private static void Exec(SqliteConnection db, string sql, params (string, object?)[] args) { using var c = Command(db, sql, args); c.ExecuteNonQuery(); }
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string, object?)[] args)
    { var c = db.CreateCommand(); c.CommandText = sql; foreach (var (key, value) in args) c.Parameters.AddWithValue(key, value ?? DBNull.Value); return c; }
    private static string ProtectedDump(string root) => Dump(root, ["policies", "receipts", "profiles", "suppressions", "history", "sqlite_sequence"]);
    private static string Dump(string root, string[]? tables = null)
    {
        using var db = Open(root); var rows = new List<object?>();
        foreach (var table in tables ?? ["policies", "receipts", "profiles", "suppressions", "history", "sqlite_sequence", "jobs", "evidence"])
        {
            rows.Add(table); using var c = Command(db, "SELECT * FROM " + table + " ORDER BY rowid"); using var r = c.ExecuteReader();
            while (r.Read()) { var values = new object[r.FieldCount]; r.GetValues(values); rows.Add(values); }
        }
        return Encode(rows);
    }
    private static async Task Reject(Func<Task> run) { try { await run(); } catch (Exception e) when (e is IOException or JsonException or SqliteException) { return; } throw new Exception("corrupt GC unexpectedly succeeded"); }
    private static async Task Cancelled(Func<Task> run) { try { await run(); } catch (OperationCanceledException) { return; } throw new Exception("cancelled GC unexpectedly succeeded"); }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
