using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amifolio.Core;
using Microsoft.Data.Sqlite;

internal static class LearningRecoveryCases
{
    internal static (string, Func<Fixture, Task>)[] All =>
    [
        ("learning recovery v1 upgrade snapshots safely near the Windows journal path limit", async f =>
        {
            // The database fits MAX_PATH, but the old temporary snapshot's journal does not.
            var padding = 249 - DbPath(f.Root).Length - 1;
            Check(padding > 0, "fixture base leaves no room for the path regression");
            var root = Path.Combine(f.Root, new string('r', padding));
            using (var seed = Seed(root)) { }
            Check((await new LearningStore(root).PolicyAsync("p")).Capture, "v1 upgrade lost consent");
            using (var current = Open(root)) Check(Scalar(current, "PRAGMA user_version") == "2", "v1 upgrade did not complete");
            var copy = Path.Combine(f.Root, "v1-recovery.sqlite");
            File.Copy(Path.Combine(root, "learning", "schema-v1-recovery.sqlite"), copy);
            using var recovery = OpenFile(copy);
            Check(Scalar(recovery, "PRAGMA user_version") == "1" && Scalar(recovery, "PRAGMA quick_check") == "ok", "standalone v1 recovery point was not preserved");
        }),
        ("learning recovery backup snapshots committed WAL and excludes projections and credentials", async f =>
        {
            await f.Store.InitializeAsync();
            using var live = Seed(f.DataRoot); Exec(live, "PRAGMA journal_mode=WAL;PRAGMA wal_autocheckpoint=0");
            Exec(live, "INSERT INTO history(entry_id,payload) VALUES('wal-only','{\"text\":\"committed WAL row\"}')");
            var learning = Path.Combine(f.DataRoot, "learning");
            var shared = Path.Combine(learning, "shared", "p", "generations", new string('a', 32)); Directory.CreateDirectory(shared);
            await File.WriteAllTextAsync(Path.Combine(shared, "profile.md"), "PRIVATE PROJECTION");
            await File.WriteAllTextAsync(Path.Combine(learning, "shared", "p", "START.md"), "PRIVATE POINTER");
            await File.WriteAllTextAsync(Path.Combine(learning, "credential.bin"), "SYNTHETIC CREDENTIAL");
            var zipPath = Path.Combine(f.Root, "learning-wal.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zipPath);
            using var zip = ZipFile.OpenRead(zipPath);
            Check(zip.GetEntry("learning/state.sqlite") is not null && zip.GetEntry("learning/initialized.marker") is not null, "learning database/marker omitted");
            Check(zip.Entries.Where(e => e.FullName.StartsWith("learning/", StringComparison.Ordinal)).Count() == 2, "learning sidecars/projections/credentials leaked");
            var copy = Path.Combine(f.Root, "snapshot.sqlite"); zip.GetEntry("learning/state.sqlite")!.ExtractToFile(copy);
            using var snapshot = OpenFile(copy);
            Check(Scalar(snapshot, "PRAGMA quick_check") == "ok" && Scalar(snapshot, "SELECT COUNT(*) FROM history WHERE entry_id='wal-only'") == "1", "WAL snapshot was incoherent");
            Check(Scalar(live, "SELECT json_extract(payload,'$.capture') FROM policies WHERE id='p'") == "1", "backup mutated live consent");
        }),
        ("learning recovery restore disables every consent and advances each revision", async f =>
        {
            await f.Store.InitializeAsync(); using (var seed = Seed(f.DataRoot)) { }
            var zip = Path.Combine(f.Root, "consent.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            var restored = Path.Combine(f.Root, "restored-consent"); await new PortfolioMaintenance(restored).RestoreAsync(zip);
            using var db = Open(restored);
            foreach (var id in new[] { "global", "p" })
            {
                var policy = Payload(db, "policies", id);
                foreach (var key in new[] { "capture", "learn", "autoActivate", "share", "remote", "originalPaths", "contributeGlobal" }) Check(!policy[key]!.GetValue<bool>(), "consent restored: " + key);
                Check(policy["revision"]!.GetValue<long>() == 8 && policy["grantedAt"] is null, "restore did not fence old policy proof");
                Check(policy["batchSize"]!.GetValue<int>() == 3 && policy["mode"]!.GetValue<string>() == "chat", "non-consent options lost");
            }
            Check(!Directory.Exists(Path.Combine(restored, "learning", "shared")), "restore published a learning projection");
        }),
        ("learning recovery cancels all jobs and requires fresh proof for every receipt", async f =>
        {
            await f.Store.InitializeAsync(); using (var seed = Seed(f.DataRoot)) { }
            var zip = Path.Combine(f.Root, "leases.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            var restored = Path.Combine(f.Root, "restored-leases"); await new PortfolioMaintenance(restored).RestoreAsync(zip);
            using var db = Open(restored);
            Check(Scalar(db, "SELECT COUNT(*) FROM jobs WHERE state<>'cancelled' OR result IS NOT NULL") == "0", "restored job or cached result can replay");
            foreach (var id in new[] { "running", "completed", "failed", "cancelled" })
            {
                var job = Payload(db, "jobs", id);
                Check(job["state"]!.GetValue<string>() == "cancelled" && job["token"]!.GetValue<string>() == "" && job["error"]!.GetValue<string>() == "restored", "old job lease survived");
            }
            Check(Scalar(db, "SELECT COUNT(*) FROM evidence WHERE valid<>0") == "0", "restored evidence remained valid");
            Check(Scalar(db, "SELECT COUNT(*) FROM receipts WHERE eligible<>0 OR processed<>0 OR job_id IS NOT NULL") == "0", "restored receipts are replayable");
            Check(Scalar(db, "SELECT json_extract(payload,'$.state') FROM receipts WHERE id='receipt'") == "pending_evidence", "receipt did not wait for proof");
            Check(Scalar(db, "SELECT fingerprint FROM receipts WHERE id='receipt'") == "old-fingerprint", "restore lost the permanent source fingerprint");
        }),
        ("learning recovery preserves manual fields history and archived suppression controls", async f =>
        {
            await f.Store.InitializeAsync(); string manual; string history;
            using (var seed = Seed(f.DataRoot)) { manual = Scalar(seed, "SELECT payload FROM profiles WHERE id='manual'"); history = Scalar(seed, "SELECT payload FROM history WHERE entry_id='manual'"); }
            var zip = Path.Combine(f.Root, "manual.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            var restored = Path.Combine(f.Root, "restored-manual"); await new PortfolioMaintenance(restored).RestoreAsync(zip);
            using var db = Open(restored);
            Check(Scalar(db, "SELECT payload FROM profiles WHERE id='manual'") == manual, "manual content/lock was rewritten");
            Check(Scalar(db, "SELECT payload FROM history WHERE entry_id='manual'") == history, "history changed");
            Check(Payload(db, "profiles", "automatic")["state"]!.GetValue<string>() == "disabled", "derived active entry revived");
            Check(Payload(db, "profiles", "summary")["state"]!.GetValue<string>() == "disabled", "derived summary revived");
            Check(Payload(db, "profiles", "candidate")["state"]!.GetValue<string>() == "candidate", "candidate promoted/lost");
            Check(Payload(db, "profiles", "deleted")["state"]!.GetValue<string>() == "deleted" && Scalar(db, "SELECT COUNT(*) FROM suppressions WHERE id='archived-suppression'") == "1", "archived rejection/tombstone lost");
        }),
        ("learning recovery old backup cannot revive current deletions including manual entries", async f =>
        {
            await f.Store.InitializeAsync(); using (var seed = Seed(f.DataRoot)) { }
            var zip = Path.Combine(f.Root, "older.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            using (var current = Open(f.DataRoot))
            {
                Exec(current, "INSERT INTO suppressions VALUES('current-suppression',$payload)", ("$payload", "{\"scope\":\"p\",\"dimension\":\"verbosity\"}"));
                var deleted = Entry("manual", "deleted", true); Put(current, "profiles", "manual", deleted);
                Put(current, "profiles", "deleted-after-backup", Entry("deleted-after-backup", "deleted", false));
            }
            await new PortfolioMaintenance(f.DataRoot).RestoreAsync(zip);
            // Repeating the same old restore must keep the union monotonic.
            await new PortfolioMaintenance(f.DataRoot).RestoreAsync(zip);
            using var db = Open(f.DataRoot);
            foreach (var id in new[] { "manual", "deleted-after-backup", "deleted" })
            {
                var item = Payload(db, "profiles", id);
                Check(item["state"]!.GetValue<string>() == "deleted" && item["text"]!.GetValue<string>() == "" && item["value"]!.GetValue<string>() == "", "current deletion resurrected: " + id);
                Check(Scalar(db, "SELECT COUNT(*) FROM history WHERE entry_id=$id", ("$id", id)) == "0", "deleted profile text revived through history");
            }
            Check(Scalar(db, "SELECT COUNT(*) FROM suppressions WHERE id IN ('archived-suppression','current-suppression')") == "2", "suppression union was reduced");
        }),
        ("learning recovery archived receipt tombstones stay deleted and reject capture", async f =>
        {
            await f.Store.InitializeAsync(); var id = ReceiptId("turn");
            using (var db = Seed(f.DataRoot))
            {
                SeedReceipt(db, id, 1, "turn", "deleted"); SeedReceiptDependencies(db);
            }
            var zip = Path.Combine(f.Root, "deleted-receipt.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            var restored = Path.Combine(f.Root, "restored-receipt"); await new PortfolioMaintenance(restored).RestoreAsync(zip);
            using (var db = Open(restored))
            {
                CheckDeletedReceipt(db, id); CheckReceiptDependenciesCleared(db);
            }
            await RejectCapture(restored, "turn");
        }),
        ("learning recovery merges receipt deletions by stable identity despite sequence collisions", async f =>
        {
            await f.Store.InitializeAsync(); var id = ReceiptId("turn"); var later = ReceiptId("later"); var unrelated = ReceiptId("unrelated");
            using (var db = Seed(f.DataRoot))
            {
                SeedReceipt(db, id, 1, "turn", "verified"); SeedReceipt(db, unrelated, 2, "unrelated", "verified", []);
                SeedReceiptDependencies(db);
            }
            var zip = Path.Combine(f.Root, "before-receipt-deletion.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            using (var db = Open(f.DataRoot))
            {
                Exec(db, "UPDATE receipts SET seq=41,payload=json_set(payload,'$.sequence',41) WHERE id=$id", ("$id", id));
                SeedReceipt(db, later, 1, "later", "verified", []);
            }
            var learning = new LearningStore(f.DataRoot); await learning.DeleteSummaryAsync(41); await learning.DeleteSummaryAsync(1);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                await new PortfolioMaintenance(f.DataRoot).RestoreAsync(zip);
                using var db = Open(f.DataRoot); CheckDeletedReceipt(db, id); CheckDeletedReceipt(db, later);
                Check(Scalar(db, "SELECT seq FROM receipts WHERE id=$id", ("$id", id)) == "1", "stable identity lost its archived sequence");
                Check(Scalar(db, "SELECT seq FROM receipts WHERE id=$id", ("$id", unrelated)) == "2" && Payload(db, "receipts", unrelated)["state"]!.GetValue<string>() == "pending_evidence", "sequence collision replaced an unrelated receipt");
                Check(Scalar(db, "SELECT COUNT(*) FROM receipts") == "3", "repeat restore lost or duplicated receipt tombstones");
                CheckReceiptDependenciesCleared(db);
            }
            await RejectCapture(f.DataRoot, "turn"); await RejectCapture(f.DataRoot, "later");
        }),
        ("learning recovery legacy backup inherits live deleted receipts without content or grants", async f =>
        {
            await f.Store.InitializeAsync(); var zip = Path.Combine(f.Root, "legacy-receipts.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            var id = ReceiptId("turn"); using (var db = Seed(f.DataRoot)) SeedReceipt(db, id, 1, "turn", "verified");
            await new LearningStore(f.DataRoot).DeleteSummaryAsync(1);
            await new PortfolioMaintenance(f.DataRoot).RestoreAsync(zip);
            using (var db = Open(f.DataRoot))
            {
                CheckDeletedReceipt(db, id);
                Check(Scalar(db, "SELECT COUNT(*) FROM policies") == "0" && Scalar(db, "SELECT COUNT(*) FROM evidence WHERE valid<>0") == "0" && Scalar(db, "SELECT COUNT(*) FROM jobs WHERE state<>'cancelled' OR result IS NOT NULL") == "0", "receipt merge imported live proof or consent");
            }
            await RejectCapture(f.DataRoot, "turn");
        }),
        ("learning recovery unreadable live receipt ledger stops root exchange", async f =>
        {
            await f.Store.InitializeAsync(); using (var seed = Seed(f.DataRoot)) { }
            var zip = Path.Combine(f.Root, "receipt-ledger.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            using (var db = Open(f.DataRoot)) Exec(db, "UPDATE receipts SET payload='broken-json'");
            await Reject(() => new PortfolioMaintenance(f.DataRoot).RestoreAsync(zip));
            using var current = Open(f.DataRoot); Check(Scalar(current, "SELECT payload FROM receipts") == "broken-json", "restore replaced unreadable receipt deletion ledger");
        }),
        ("learning recovery legacy backup has safe defaults and retains live tombstone union", async f =>
        {
            await f.Store.InitializeAsync(); var legacy = Path.Combine(f.Root, "legacy.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(legacy);
            var fresh = Path.Combine(f.Root, "legacy-fresh"); await new PortfolioMaintenance(fresh).RestoreAsync(legacy);
            Check(!File.Exists(DbPath(fresh)) && !File.Exists(Path.Combine(fresh, "learning", "initialized.marker")), "legacy restore invented authorized learning state");
            using (var seed = Seed(f.DataRoot)) { }
            await new PortfolioMaintenance(f.DataRoot).RestoreAsync(legacy);
            using var db = Open(f.DataRoot);
            Check(Scalar(db, "PRAGMA user_version") == "2" && Scalar(db, "SELECT COUNT(*) FROM policies") == "0", "legacy merge copied live consents");
            Check(Scalar(db, "SELECT COUNT(*) FROM profiles WHERE json_extract(payload,'$.state')='active' AND json_extract(payload,'$.manual')=0") == "0", "legacy merge revived automatic profiles");
            Check(Scalar(db,"SELECT json_extract(payload,'$.manual') FROM profiles WHERE id='manual'")=="1","legacy merge discarded manual authoritative content");
            Check(Scalar(db, "SELECT COUNT(*) FROM suppressions WHERE id='archived-suppression'") == "1" && Payload(db, "profiles", "deleted")["state"]!.GetValue<string>() == "deleted", "legacy restore forgot current deletion");
            Check(File.ReadAllText(Path.Combine(f.DataRoot, "learning", "initialized.marker")) == "1", "merged learning state lacks marker");
        }),
        ("learning recovery missing initialized database fails backup without recreating an empty store", async f =>
        {
            await f.Store.InitializeAsync(); var learning = Directory.CreateDirectory(Path.Combine(f.DataRoot, "learning")).FullName;
            await File.WriteAllTextAsync(Path.Combine(learning, "initialized.marker"), "1");
            var zip = Path.Combine(f.Root, "missing.zip"); await Reject(() => new PortfolioMaintenance(f.DataRoot).BackupAsync(zip));
            Check(!File.Exists(zip) && !File.Exists(DbPath(f.DataRoot)), "missing learning store silently recreated");
        }),
        ("learning recovery orphan SQLite sidecars cannot be mistaken for a legacy empty store", async f =>
        {
            await f.Store.InitializeAsync(); var learning = Directory.CreateDirectory(Path.Combine(f.DataRoot, "learning")).FullName;
            await File.WriteAllTextAsync(Path.Combine(learning, "state.sqlite-wal"), "synthetic orphan WAL");
            var zip = Path.Combine(f.Root, "orphan-wal.zip"); await Reject(() => new PortfolioMaintenance(f.DataRoot).BackupAsync(zip));
            Check(!File.Exists(zip) && !File.Exists(DbPath(f.DataRoot)), "orphan WAL caused an empty backup/reset");
        }),
        ("learning recovery corrupt or unsupported learning state cannot replace current root", async f =>
        {
            await f.Store.InitializeAsync(); using (var seed = Seed(f.DataRoot)) { }
            var valid = Path.Combine(f.Root, "valid.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(valid);
            foreach (var mode in new[] { "corrupt", "future", "payload", "marker-only" })
            {
                var bad = Path.Combine(f.Root, mode + ".zip"); var entries = ReadZip(valid);
                if (mode == "corrupt") entries["learning/state.sqlite"] = "not sqlite"u8.ToArray();
                if (mode is "future" or "payload")
                {
                    var edit = Path.Combine(f.Root, mode + ".sqlite"); await File.WriteAllBytesAsync(edit, entries["learning/state.sqlite"]);
                    using (var db = OpenFile(edit)) Exec(db, mode == "future" ? "PRAGMA user_version=2" : "UPDATE policies SET payload='not-json'");
                    entries["learning/state.sqlite"] = await File.ReadAllBytesAsync(edit);
                }
                if (mode == "marker-only") entries.Remove("learning/state.sqlite");
                WriteZip(bad, entries); await Reject(() => new PortfolioMaintenance(f.DataRoot).RestoreAsync(bad));
                using var current = Open(f.DataRoot); Check(Scalar(current, "SELECT json_extract(payload,'$.capture') FROM policies WHERE id='p'") == "1", "failed restore changed current consent/root");
            }
        }),
        ("learning recovery restore allowlist rejects learning projections and credential files", async f =>
        {
            await f.Store.InitializeAsync(); var valid = Path.Combine(f.Root, "allowlist.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(valid);
            foreach (var name in new[] { "learning/shared/p/START.md", "learning/credential.bin", "learning/state.sqlite-wal" })
            {
                var entries = ReadZip(valid); entries[name] = "synthetic"u8.ToArray(); var bad = Path.Combine(f.Root, Guid.NewGuid().ToString("N") + ".zip"); WriteZip(bad, entries);
                await Reject(() => new PortfolioMaintenance(f.DataRoot).RestoreAsync(bad));
            }
        }),
        ("learning recovery refuses unreadable live deletion ledger before root exchange", async f =>
        {
            await f.Store.InitializeAsync(); using (var seed = Seed(f.DataRoot)) { }
            var valid = Path.Combine(f.Root, "live-ledger.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(valid);
            using (var current = Open(f.DataRoot)) Exec(current, "UPDATE profiles SET payload='broken-json' WHERE id='deleted'");
            await Reject(() => new PortfolioMaintenance(f.DataRoot).RestoreAsync(valid));
            using var db = Open(f.DataRoot); Check(Scalar(db, "SELECT payload FROM profiles WHERE id='deleted'") == "broken-json", "restore replaced unreadable deletion ledger");
        }),
        ("learning recovery preserves existing automatic budget merge", async f =>
        {
            await f.Store.InitializeAsync(); using (var seed = Seed(f.DataRoot)) { }
            var budget = Path.Combine(f.DataRoot, "context-state", "automation-budget.json"); Directory.CreateDirectory(Path.GetDirectoryName(budget)!);
            await File.WriteAllTextAsync(budget, "{\"Day\":\"2026-11-14\",\"Requests\":2,\"LastAttempt\":\"2026-11-14T01:00:00Z\"}");
            var zip = Path.Combine(f.Root, "budget-learning.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            await File.WriteAllTextAsync(budget, "{\"Day\":\"2026-11-14\",\"Requests\":7,\"LastAttempt\":\"2026-11-14T02:00:00Z\"}");
            await new PortfolioMaintenance(f.DataRoot).RestoreAsync(zip);
            var state = JsonNode.Parse(await File.ReadAllTextAsync(budget))!;
            Check(state["Requests"]!.GetValue<int>() == 7 && state["LastAttempt"]!.GetValue<string>() == "2026-11-14T02:00:00+00:00", "learning restore changed the budget merge");
        })
    ];

    private static string ReceiptId(string turn) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("p\nsession\n" + turn)));
    private static void SeedReceipt(SqliteConnection db, string id, long sequence, string turn, string state, string[]? evidence = null)
    {
        var summary = new LearningSummary(sequence, 2, "p", "session", turn, "must erase deleted receipt body", "completed", state,
            DateTimeOffset.Parse("2026-11-14T00:00:00Z"), evidence ?? ["proof"], [new("synthetic.md", "deleted output purpose")]);
        Exec(db, "DELETE FROM receipts WHERE seq=$seq", ("$seq", sequence));
        Exec(db, "INSERT INTO receipts VALUES($seq,$id,'p','synthetic-fingerprint',$payload,1,0,NULL)", ("$seq", sequence), ("$id", id),
            ("$payload", JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
    }
    private static void SeedReceiptDependencies(SqliteConnection db)
    {
        var receipt = Payload(db, "receipts", ReceiptId("turn")); var job = Payload(db, "jobs", "completed");
        job["summaries"] = new JsonArray(receipt.DeepClone()); job["evidence"] = new JsonArray(Payload(db, "evidence", "proof").DeepClone());
        Exec(db, "UPDATE jobs SET payload=$payload WHERE id='completed'", ("$payload", job.ToJsonString()));
        Exec(db, "INSERT INTO history(entry_id,payload) VALUES('automatic',$payload)", ("$payload", Entry("automatic", "active", false)));
    }
    private static void CheckDeletedReceipt(SqliteConnection db, string id)
    {
        Check(Scalar(db, "SELECT COUNT(*) FROM receipts WHERE id=$id", ("$id", id)) == "1", "receipt deletion fence was not restored");
        var receipt = Payload(db, "receipts", id);
        Check(receipt["state"]!.GetValue<string>() == "deleted", "receipt tombstone became pending evidence");
        Check(receipt["summary"]!.GetValue<string>() == "" && receipt["outputs"]!.AsArray().Count == 0 && receipt["evidenceIds"]!.AsArray().Count == 0, "deleted receipt retained body, outputs or evidence IDs");
        Check(Scalar(db, "SELECT eligible||':'||processed||':'||(job_id IS NULL) FROM receipts WHERE id=$id", ("$id", id)) == "0:1:1", "deleted receipt became processable");
        Check(Scalar(db, "SELECT seq FROM receipts WHERE id=$id", ("$id", id)) == receipt["sequence"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture), "payload sequence differs from allocated sequence");
    }
    private static void CheckReceiptDependenciesCleared(SqliteConnection db)
    {
        Check(Scalar(db, "SELECT COUNT(*) FROM evidence WHERE id='proof'") == "0", "deleted source evidence was restored");
        foreach (var id in new[] { "automatic", "summary", "candidate" })
        {
            var entry = Payload(db, "profiles", id);
            Check(entry["state"]!.GetValue<string>() != "active" && entry["text"]!.GetValue<string>() == "" && entry["evidenceIds"]!.AsArray().Count == 0, "deleted source body survived in derived profile: " + id);
        }
        Check(Scalar(db, "SELECT COUNT(*) FROM history WHERE entry_id='automatic'") == "0", "deleted source body survived in undo history");
        Check(Payload(db, "profiles", "manual")["text"]!.GetValue<string>() == "保留合成人工内容" && Scalar(db, "SELECT COUNT(*) FROM history WHERE entry_id='manual'") == "1", "independent manual content/history was removed");
        var job = Payload(db, "jobs", "completed"); var summary = job["summaries"]![0]!;
        Check(summary["summary"]!.GetValue<string>() == "" && summary["outputs"]!.AsArray().Count == 0 && summary["evidenceIds"]!.AsArray().Count == 0 && job["evidence"]!.AsArray().Count == 0, "deleted receipt body survived in cached job input");
    }
    private static async Task RejectCapture(string root, string turn)
    {
        var learning = new LearningStore(root); await learning.SetPolicyAsync("p", new() { Capture = true });
        try { await learning.CaptureAsync(new("p", "session", turn, "attempt to restore deleted turn", "completed", [], []), null); }
        catch (InvalidOperationException e) when (e.Message.Contains("删除", StringComparison.Ordinal)) { return; }
        throw new Exception("capture recreated a deleted receipt after restore");
    }
    private static string DbPath(string root) => Path.Combine(root, "learning", "state.sqlite");
    private static SqliteConnection Open(string root) => OpenFile(DbPath(root));
    private static SqliteConnection OpenFile(string file)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()); db.Open(); return db;
    }
    private static SqliteConnection Seed(string root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DbPath(root))!); File.WriteAllText(Path.Combine(root, "learning", "initialized.marker"), "1");
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath(root), Pooling = false }.ToString()); db.Open();
        Exec(db, """
            CREATE TABLE policies(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
            CREATE TABLE receipts(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT UNIQUE NOT NULL,scope TEXT NOT NULL,fingerprint TEXT NOT NULL,payload TEXT NOT NULL,eligible INTEGER NOT NULL,processed INTEGER NOT NULL,job_id TEXT);
            CREATE TABLE evidence(id TEXT PRIMARY KEY,scope TEXT NOT NULL,valid INTEGER NOT NULL,payload TEXT NOT NULL);
            CREATE TABLE profiles(id TEXT PRIMARY KEY,scope TEXT NOT NULL,updated INTEGER NOT NULL,payload TEXT NOT NULL);
            CREATE TABLE jobs(id TEXT PRIMARY KEY,scope TEXT NOT NULL,state TEXT NOT NULL,created INTEGER NOT NULL,payload TEXT NOT NULL,result TEXT);
            CREATE TABLE suppressions(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
            CREATE TABLE history(seq INTEGER PRIMARY KEY AUTOINCREMENT,entry_id TEXT NOT NULL,payload TEXT NOT NULL);
            PRAGMA user_version=1;
            """);
        const string policy = """{"capture":true,"learn":true,"autoActivate":true,"share":true,"remote":true,"originalPaths":true,"contributeGlobal":true,"revision":7,"grantedAt":"2026-11-14T00:00:00Z","mode":"chat","batchSize":3,"endpoint":"https://example.invalid/v1"}""";
        foreach (var id in new[] { "global", "p" }) Exec(db, "INSERT INTO policies VALUES($id,$payload)", ("$id", id), ("$payload", policy));
        var summary = """{"sequence":1,"version":2,"projectId":"p","sessionId":"session","turnId":"turn","summary":"合成摘要","outcome":"completed","state":"verified","recordedAt":"2026-11-14T00:00:00Z","evidenceIds":["proof"],"outputs":[]}""";
        Exec(db, "INSERT INTO receipts VALUES(1,'receipt','p','old-fingerprint',$payload,1,1,'completed')", ("$payload", summary));
        Exec(db, "INSERT INTO evidence VALUES('proof','p',1,$payload)", ("$payload", "{\"evidence\":{\"id\":\"proof\",\"text\":\"以后请简洁回答\",\"verified\":true}}"));
        foreach (var state in new[] { "running", "completed", "failed", "cancelled" })
        {
            var job = JsonSerializer.Serialize(new { id = state, scope = "p", mode = "chat", token = "old-token", policyRevision = 7, globalRevision = 7, state, leaseUntil = "2099-01-01T00:00:00Z", fingerprint = "old-job-fingerprint", summaries = Array.Empty<object>(), evidence = Array.Empty<object>(), error = "" });
            Exec(db, "INSERT INTO jobs VALUES($id,'p',$id,1,$payload,'cached-result')", ("$id", state), ("$payload", job));
        }
        foreach (var (id, state, manual) in new[] { ("manual", "active", true), ("automatic", "active", false), ("summary", "active", false), ("candidate", "candidate", false), ("deleted", "deleted", false) }) Put(db, "profiles", id, Entry(id, state, manual));
        Exec(db, "INSERT INTO suppressions VALUES('archived-suppression','{\"scope\":\"p\",\"dimension\":\"tone\"}')");
        Exec(db, "INSERT INTO history(entry_id,payload) VALUES('manual','{\"text\":\"manual revision\",\"locked\":true}')");
        return db;
    }
    private static string Entry(string id, string state, bool manual) => JsonSerializer.Serialize(new { id, scope = "p", kind = id == "summary" ? "summary" : "style", dimension = "verbosity", value = state == "deleted" ? "" : "brief", text = state == "deleted" ? "" : "保留合成人工内容", condition = "", state, reason = "synthetic", manual, revision = 4, updatedAt = "2026-11-14T00:00:00Z", evidenceIds = state == "deleted" ? Array.Empty<string>() : new[] { "proof" }, locked = manual });
    private static void Put(SqliteConnection db, string table, string id, string payload) => Exec(db, "INSERT INTO " + table + "(id,scope,updated,payload) VALUES($id,'p',1,$payload) ON CONFLICT(id) DO UPDATE SET payload=$payload", ("$id", id), ("$payload", payload));
    private static JsonObject Payload(SqliteConnection db, string table, string id) => JsonNode.Parse(Scalar(db, "SELECT payload FROM " + table + " WHERE id=$id", ("$id", id)))!.AsObject();
    private static void Exec(SqliteConnection db, string sql, params (string Key, object Value)[] args) { using var command = Command(db, sql, args); command.ExecuteNonQuery(); }
    private static string Scalar(SqliteConnection db, string sql, params (string Key, object Value)[] args) { using var command = Command(db, sql, args); return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? ""; }
    private static SqliteCommand Command(SqliteConnection db, string sql, (string Key, object Value)[] args) { var command = db.CreateCommand(); command.CommandText = sql; foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value); return command; }
    private static Dictionary<string, byte[]> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path); var entries = new Dictionary<string, byte[]>();
        foreach (var entry in zip.Entries.Where(e => e.FullName != "manifest.json")) { using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output); entries.Add(entry.FullName, output.ToArray()); }
        return entries;
    }
    private static void WriteZip(string path, Dictionary<string, byte[]> entries)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries) { using var output = zip.CreateEntry(name).Open(); output.Write(bytes); }
        using var manifest = zip.CreateEntry("manifest.json").Open();
        JsonSerializer.Serialize(manifest, new { FormatVersion = 1, Files = entries.Select(e => new { Path = e.Key, Bytes = e.Value.LongLength, Sha256 = Convert.ToHexString(SHA256.HashData(e.Value)) }).ToArray() });
    }
    private static async Task Reject(Func<Task> run)
    {
        try { await run(); } catch (Exception e) when (e is IOException or SqliteException) { return; }
        throw new Exception("unsafe recovery succeeded");
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
}
