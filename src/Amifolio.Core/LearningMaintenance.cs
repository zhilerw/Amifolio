using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Amifolio.Core;

/// <summary>
/// Learning recovery only; callers hold PortfolioMaintenance's exclusive root lease for capture and merge.
/// Only state.sqlite and a format marker are admitted. Published shared generations, WAL/SHM files,
/// credentials and arbitrary learning-directory files are never enumerated or copied.
/// Restored provenance is historical data, never renewed consent or proof for automatic work.
/// </summary>
internal static partial class LearningMaintenance
{
    private static readonly string[] ConsentFields = ["capture", "learn", "autoActivate", "share", "remote", "originalPaths", "contributeGlobal"];
    private static readonly (string Table, string Columns)[] Tables =
    [
        ("policies", "id,payload"), ("receipts", "seq,id,scope,fingerprint,payload,eligible,processed,job_id"),
        ("evidence", "id,scope,valid,payload"), ("profiles", "id,scope,updated,payload"),
        ("jobs", "id,scope,state,created,payload,result"), ("suppressions", "id,payload"), ("history", "seq,entry_id,payload")
    ];

    internal static bool Capture(string root, string staging, CancellationToken ct)
    {
        var source = ExistingDatabase(root);
        if (source is null) return false;
        ct.ThrowIfCancellationRequested();
        var target = DatabasePath(staging); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        using (var current = Open(source, SqliteOpenMode.ReadOnly))
        using (var snapshot = Open(target, SqliteOpenMode.ReadWriteCreate))
        {
            Validate(current); current.BackupDatabase(snapshot); Validate(snapshot);
            // A standalone snapshot never relies on an omitted WAL sidecar.
            Exec(snapshot, null, "PRAGMA journal_mode=DELETE");
        }
        ct.ThrowIfCancellationRequested(); _ = ExistingDatabase(staging); WriteMarker(staging); return true;
    }

    internal static void PrepareRestore(string staging, CancellationToken ct)
    {
        var path = ExistingDatabase(staging);
        if (path is null) return; // Legacy backups retain default-off behavior on first LearningStore use.
        using (var db = Open(path, SqliteOpenMode.ReadWrite))
        {
            Validate(db);
            LearningSchema.Ensure(db,path,ct);
            using var tx = db.BeginTransaction(deferred: false);
            foreach (var (id, payload) in Payloads(db, tx, "policies"))
            {
                ct.ThrowIfCancellationRequested(); var policy = Object(payload);
                foreach (var field in ConsentFields) policy[field] = false;
                policy["revision"] = NextRevision(policy); policy["grantedAt"] = null;
                UpdatePayload(db, tx, "policies", id, policy);
            }
            foreach (var (id, payload) in Payloads(db, tx, "jobs"))
            {
                ct.ThrowIfCancellationRequested(); var job = Object(payload);
                job["state"] = "cancelled"; job["token"] = ""; job["error"] = "restored";
                Exec(db, tx, "UPDATE jobs SET state='cancelled',result=NULL,payload=$payload WHERE id=$id", ("$payload", job.ToJsonString()), ("$id", id));
            }
            var deletedReceipts = new List<DeletedReceipt>();
            foreach (var (id, payload) in Payloads(db, tx, "receipts"))
            {
                ct.ThrowIfCancellationRequested(); var receipt = Object(payload);
                if (State(receipt) == "deleted")
                {
                    deletedReceipts.Add(new(id, Value<string>(receipt, "projectId"), receipt,Convert.ToString(LearningSchema.Scalar(db,tx,"SELECT fingerprint FROM receipts WHERE id=$id",("$id",id)))!));
                    continue;
                }
                receipt["state"] = "pending_evidence";
                // Keep the permanent fingerprint. Pending evidence requires explicit
                // re-proof even for the same hash; restoration never enqueues work.
                UpdatePayload(db,tx,"receipts",id,receipt);
                Exec(db, tx, "UPDATE receipts SET eligible=0,processed=0,job_id=NULL WHERE id=$id", ("$id", id));
            }
            Exec(db, tx, "UPDATE evidence SET valid=0");
            foreach (var (id, payload) in Payloads(db, tx, "profiles"))
            {
                ct.ThrowIfCancellationRequested(); var profile = Object(payload); var state = State(profile);
                if (state == "deleted")
                {
                    ClearDeleted(profile); UpdatePayload(db, tx, "profiles", id, profile);
                    Exec(db, tx, "DELETE FROM history WHERE entry_id=$id", ("$id", id));
                }
                else if ((!Manual(profile) || Value<string>(profile, "kind") == "creative") && state == "active")
                {
                    profile["state"] = "disabled"; profile["reason"] = "restored"; profile["revision"] = NextRevision(profile);
                    UpdatePayload(db, tx, "profiles", id, profile);
                }
                // Manual entries are left byte-for-byte intact, including unknown future manual lock fields.
            }
            RestoreDeletedReceipts(db, tx, deletedReceipts, ct);
            ct.ThrowIfCancellationRequested(); tx.Commit();
        }
        WriteMarker(staging);
    }

    internal static void Merge(string staging, string current, CancellationToken ct)
    {
        var currentPath = ExistingDatabase(current);
        if (currentPath is null) return;
        using var live = Open(currentPath, SqliteOpenMode.ReadOnly); Validate(live);
        using var read = live.BeginTransaction(deferred: true);
        // Suppression payloads are opaque. Their IDs, not model text or condition wording, are the fence.
        var suppressions = Payloads(live, read, "suppressions");
        var deleted = new List<(string Id, string Scope, long Updated, JsonObject Payload)>();
        using (var command = Command(live, read, "SELECT id,scope,updated,payload FROM profiles"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested(); var profile = Object(reader.GetString(3));
                if (State(profile) != "deleted") continue;
                ClearDeleted(profile); deleted.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2), profile));
            }
        var deletedReceipts = new List<DeletedReceipt>();
        using (var command = Command(live, read, "SELECT id,scope,payload,fingerprint FROM receipts"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested(); var receipt = Object(reader.GetString(2));
                if (State(receipt) == "deleted") deletedReceipts.Add(new(reader.GetString(0), reader.GetString(1), receipt,reader.GetString(3)));
            }
        if (!suppressions.Any() && deleted.Count == 0 && deletedReceipts.Count == 0&&LearningSchema.Number(live,read,"SELECT COUNT(*) FROM receipts")==0&&LearningSchema.Number(live,read,"SELECT COUNT(*) FROM profiles")==0&&LearningSchema.Number(live,read,"SELECT COUNT(*) FROM history")==0) return;
        var stagedPath = ExistingDatabase(staging);
        if (stagedPath is null)
        {
            // A legacy backup still inherits live deletion fences, but no current grants, jobs or content.
            stagedPath = DatabasePath(staging); Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
            using var empty = Open(stagedPath, SqliteOpenMode.ReadWriteCreate); CreateEmpty(empty);
        }
        using (var db = Open(stagedPath, SqliteOpenMode.ReadWrite))
        {
            Validate(db);LearningSchema.Ensure(db,stagedPath,ct); using var tx = db.BeginTransaction(deferred: false);
            MergeCapacity(live,read,db,tx,ct);
            foreach (var (id, payload) in suppressions)
            {
                ct.ThrowIfCancellationRequested();
                Exec(db, tx, "INSERT INTO suppressions(id,payload) VALUES($id,$payload) ON CONFLICT(id) DO UPDATE SET payload=$payload", ("$id", id), ("$payload", payload));
            }
            foreach (var item in deleted)
            {
                ct.ThrowIfCancellationRequested();
                Exec(db,tx,"DELETE FROM profile_cold WHERE id=$id",("$id",item.Id));
                Exec(db, tx, "INSERT INTO profiles(id,scope,updated,payload) VALUES($id,$scope,$updated,$payload) ON CONFLICT(id) DO UPDATE SET scope=$scope,updated=$updated,payload=$payload",
                    ("$id", item.Id), ("$scope", item.Scope), ("$updated", item.Updated), ("$payload", item.Payload.ToJsonString()));
                // Deleted text must not reappear through old revision history either.
                Exec(db, tx, "DELETE FROM history WHERE entry_id=$id", ("$id", item.Id));
            }
            RestoreDeletedReceipts(db, tx, deletedReceipts, ct);
            // Retain archived deletion fences too, including newly merged live bodies.
            var allDeleted=Payloads(db,tx,"receipts").Select(row=>(row.Id,Payload:Object(row.Payload))).Where(row=>State(row.Payload)=="deleted")
                .Select(row=>new DeletedReceipt(row.Id,Value<string>(row.Payload,"projectId"),row.Payload,Convert.ToString(LearningSchema.Scalar(db,tx,"SELECT fingerprint FROM receipts WHERE id=$id",("$id",row.Id)))!)).ToArray();
            RestoreDeletedReceipts(db,tx,allDeleted,ct);
            ct.ThrowIfCancellationRequested(); tx.Commit();
        }
        WriteMarker(staging);
    }

    private sealed record DeletedReceipt(string Id, string Scope, JsonObject Payload,string Fingerprint="deleted");
    internal static void PurgeReceipt(SqliteConnection db,SqliteTransaction tx,string id,string scope,string payload,CancellationToken ct)
    {
        var fingerprint=Convert.ToString(LearningSchema.Scalar(db,tx,"SELECT fingerprint FROM receipts WHERE id=$id",("$id",id)))!;
        RestoreDeletedReceipts(db,tx,[new(id,scope,Object(payload),fingerprint)],ct);
    }
    private static (string Project, string Session, string Turn) ReceiptIdentity(JsonObject value) =>
        (Value<string>(value, "projectId"), Value<string>(value, "sessionId"), Value<string>(value, "turnId"));
    private static string[] EvidenceIds(JsonObject value)
    {
        if (value["evidenceIds"] is null) return [];
        if (value["evidenceIds"] is not JsonArray array) throw new IOException("学习证据标识无效。");
        return array.Select(item => item is JsonValue v && v.TryGetValue<string>(out var id) ? id : throw new IOException("学习证据标识无效。")).ToArray();
    }
    private static void ClearReceipt(JsonObject receipt)
    {
        receipt["state"] = "deleted"; receipt["summary"] = ""; receipt["outputs"] = new JsonArray(); receipt["evidenceIds"] = new JsonArray();
    }
    private static void RestoreDeletedReceipts(SqliteConnection db, SqliteTransaction tx, IReadOnlyList<DeletedReceipt> deleted, CancellationToken ct)
    {
        if (deleted.Count == 0) return;
        var identities = deleted.Select(item => ReceiptIdentity(item.Payload)).ToHashSet();
        var scopes = deleted.Select(item => item.Scope).ToHashSet(StringComparer.Ordinal);
        var evidenceIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in deleted)
        {
            ct.ThrowIfCancellationRequested(); var receipt = (JsonObject)item.Payload.DeepClone();
            if (ReceiptIdentity(receipt).Project != item.Scope) throw new IOException("学习回执作用域不匹配。");
            evidenceIds.UnionWith(EvidenceIds(receipt));
            using (var command = Command(db, tx, "SELECT payload FROM "+LearningColdStorage.Source("receipts")+" WHERE id=$id", ("$id", item.Id)))
                if (command.ExecuteScalar() is string prior)
                {
                    var archived = Object(prior);
                    if (ReceiptIdentity(archived) != ReceiptIdentity(receipt)) throw new IOException("学习回执身份不匹配。");
                    evidenceIds.UnionWith(EvidenceIds(archived));
                    receipt["version"] = Math.Max(Value<long>(receipt, "version"), Value<long>(archived, "version"));
                }
            ClearReceipt(receipt);
            // The stable ID is the fence. Never import a live sequence over an unrelated archived row.
            var preferred=Value<long>(receipt,"sequence");
            if(LearningSchema.Scalar(db,tx,"SELECT id FROM receipts WHERE id=$id",("$id",item.Id)) is not null)
                Exec(db,tx,"UPDATE receipts SET scope=$scope,fingerprint=$hash,payload=$payload,eligible=0,processed=1,job_id=NULL WHERE id=$id",("$id",item.Id),("$scope",item.Scope),("$hash",item.Fingerprint),("$payload",receipt.ToJsonString()));
            else if(LearningSchema.Scalar(db,tx,"SELECT id FROM receipts WHERE seq=$seq",("$seq",preferred)) is null)
                Exec(db,tx,"INSERT INTO receipts(seq,id,scope,fingerprint,payload,eligible,processed) VALUES($seq,$id,$scope,$hash,$payload,0,1)",("$seq",preferred),("$id",item.Id),("$scope",item.Scope),("$hash",item.Fingerprint),("$payload",receipt.ToJsonString()));
            else Exec(db,tx,"INSERT INTO receipts(id,scope,fingerprint,payload,eligible,processed) VALUES($id,$scope,$hash,$payload,0,1)",("$id",item.Id),("$scope",item.Scope),("$hash",item.Fingerprint),("$payload",receipt.ToJsonString()));
            Exec(db,tx,"DELETE FROM receipt_cold WHERE id=$id",("$id",item.Id));
            using (var command = Command(db, tx, "SELECT seq FROM receipts WHERE id=$id", ("$id", item.Id))) receipt["sequence"] = Convert.ToInt64(command.ExecuteScalar());
            UpdatePayload(db, tx, "receipts", item.Id, receipt);
        }
        bool Matches(JsonObject value) => value["projectId"] is not null && value["sessionId"] is not null && value["turnId"] is not null && identities.Contains(ReceiptIdentity(value));
        var evidence = Payloads(db, tx, "evidence").Select(row => (row.Id, Payload: Object(row.Payload)));
        foreach (var row in evidence)
            if (row.Payload["evidence"] is JsonObject source && Matches(source)) evidenceIds.Add(row.Id);
        var jobs = Payloads(db, tx, "jobs").Select(row => (row.Id, Payload: Object(row.Payload)));
        foreach (var row in jobs)
        {
            ct.ThrowIfCancellationRequested();
            if (row.Payload["summaries"] is JsonArray summaries)
                foreach (var summary in summaries.OfType<JsonObject>().Where(Matches)) evidenceIds.UnionWith(EvidenceIds(summary));
            if (row.Payload["evidence"] is JsonArray quotes)
                foreach (var quote in quotes.OfType<JsonObject>())
                    if (quote["evidence"] is JsonObject source && Matches(source)) evidenceIds.Add(Value<string>(source, "id"));
        }
        foreach (var id in evidenceIds) { ct.ThrowIfCancellationRequested(); Exec(db, tx, "DELETE FROM evidence WHERE id=$id", ("$id", id)); }
        foreach (var row in jobs)
        {
            ct.ThrowIfCancellationRequested(); var changed = false;
            if (row.Payload["summaries"] is JsonArray summaries)
                foreach (var summary in summaries.OfType<JsonObject>().Where(Matches)) { ClearReceipt(summary); changed = true; }
            if (row.Payload["evidence"] is JsonArray quotes)
                for (var i = quotes.Count - 1; i >= 0; i--)
                    if (quotes[i] is JsonObject quote && quote["evidence"] is JsonObject source && evidenceIds.Contains(Value<string>(source, "id"))) { quotes.RemoveAt(i); changed = true; }
            if (changed) UpdatePayload(db, tx, "jobs", row.Id, row.Payload);
        }
        var affected = new HashSet<string>(StringComparer.Ordinal);
        bool DerivedFromDeletion(JsonObject value) => EvidenceIds(value).Any(evidenceIds.Contains) ||
            value["kind"]?.GetValue<string>() == "summary" && value["scope"] is JsonValue scope && scope.TryGetValue<string>(out var key) && scopes.Contains(key);
        foreach (var (id, payload) in Payloads(db, tx, "profiles"))
        {
            ct.ThrowIfCancellationRequested(); var profile = Object(payload);
            if (Manual(profile) || State(profile) == "deleted" || !DerivedFromDeletion(profile)) continue;
            profile["state"] = "disabled"; profile["reason"] = "record_deleted"; profile["revision"] = NextRevision(profile);
            profile["text"] = ""; profile["value"] = ""; profile["condition"] = ""; profile["evidenceIds"] = new JsonArray();
            UpdatePayload(db, tx, "profiles", id, profile); affected.Add(id);
        }
        var history = new List<long>();
        using (var command = Command(db, tx, "SELECT seq,entry_id,payload FROM history"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                ct.ThrowIfCancellationRequested(); var entry = Object(reader.GetString(2));
                if (entry["manual"] is JsonValue manual && manual.TryGetValue<bool>(out var isManual) && isManual) continue;
                if (affected.Contains(reader.GetString(1)) || DerivedFromDeletion(entry)) history.Add(reader.GetInt64(0));
            }
        foreach (var seq in history) Exec(db, tx, "DELETE FROM history WHERE seq=$seq", ("$seq", seq));
    }
    private static string DatabasePath(string root) => Path.Combine(root, "learning", "state.sqlite");
    private static string? ExistingDatabase(string root)
    {
        var path = DatabasePath(root); var marker = Path.Combine(root, "learning", "initialized.marker");
        foreach (var file in new[] { path, marker, path + "-wal", path + "-shm", path + "-journal" }) ArchiveSafety.NoReparse(file);
        if (Directory.Exists(path) || Directory.Exists(marker)) throw new IOException("学习恢复路径不是普通文件。");
        if (File.Exists(marker))
        {
            using var stream = ArchiveSafety.OpenRead(marker);
            if (stream.Length > 16) throw new IOException("学习初始化标记无效。");
            using var reader = new StreamReader(stream); if (reader.ReadToEnd().Trim() != "1") throw new IOException("学习初始化标记无效。");
            if (!File.Exists(path)) throw new IOException("学习数据库缺失，未创建空库。");
        }
        if (!File.Exists(path))
        {
            if (new[] { path + "-wal", path + "-shm", path + "-journal" }.Any(File.Exists)) throw new IOException("学习数据库缺失但仍有恢复日志，未视为空库。");
            return null;
        }
        if (new FileInfo(path).Length > 1024L * 1024 * 1024) throw new IOException("学习数据库超过备份格式上限。");
        return path;
    }
    private static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        ArchiveSafety.NoReparse(path);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 5 }.ToString());
        try { db.Open(); Exec(db, null, "PRAGMA trusted_schema=OFF"); return db; } catch { db.Dispose(); throw; }
    }
    private static void Validate(SqliteConnection db)
    {
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA user_version";
        var version=Convert.ToInt32(command.ExecuteScalar());
        if (version is not(1 or LearningSchema.Version)) throw new IOException("学习数据库版本不受支持。");
        command.CommandText = "PRAGMA quick_check";
        if (command.ExecuteScalar() as string != "ok") throw new IOException("学习数据库损坏，恢复已停止。");
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger'";
        if (Convert.ToInt32(command.ExecuteScalar()) != 0) throw new IOException("学习数据库包含不受支持的触发器。");
        foreach (var (table, columns) in Tables)
        {
            command.Parameters.Clear(); command.CommandText = "SELECT type FROM sqlite_master WHERE name=$table"; command.Parameters.AddWithValue("$table", table);
            if (command.ExecuteScalar() as string != "table") throw new IOException("学习数据库结构不完整。");
            command.Parameters.Clear(); command.CommandText = "SELECT " + columns + " FROM " + table + " LIMIT 0"; using var reader = command.ExecuteReader();
        }
        if(version==LearningSchema.Version)
        {
            foreach(var table in new[]{"receipt_cold","profile_cold"}){command.CommandText="SELECT id,payload FROM "+table+" LIMIT 0";using var reader=command.ExecuteReader();}
            command.CommandText="SELECT scope,sequence FROM learning_watermarks LIMIT 0";using(var reader=command.ExecuteReader()){}
            command.CommandText="SELECT id,sequence FROM receipt_sequence_origins LIMIT 0";using(var reader=command.ExecuteReader()){}
            LearningColdStorage.Validate(db);
        }
    }
    private static JsonObject Object(string payload)
    {
        try
        {
            if (payload.Length > 4 * 1024 * 1024) throw new IOException("学习恢复记录超过大小上限。");
            var value = JsonNode.Parse(payload, new JsonNodeOptions { PropertyNameCaseInsensitive = true }, new JsonDocumentOptions { MaxDepth = 32 }) as JsonObject ?? throw new JsonException();
            _ = value.Count; return value;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException) { throw new IOException("学习记录损坏，恢复已停止。", e); }
    }
    private static string State(JsonObject value) => Value<string>(value, "state");
    private static bool Manual(JsonObject value) => Value<bool>(value, "manual");
    private static T Value<T>(JsonObject value, string field)
    {
        try { return value[field] is JsonValue item && item.TryGetValue<T>(out var result) ? result : throw new IOException("学习恢复字段缺失或无效。"); }
        catch (InvalidOperationException e) { throw new IOException("学习恢复字段无效。", e); }
    }
    private static long NextRevision(JsonObject value)
    {
        var revision = Value<long>(value, "revision");
        if (revision < 0 || revision == long.MaxValue) throw new IOException("学习修订号无效。");
        return revision + 1;
    }
    private static void ClearDeleted(JsonObject value)
    {
        value["state"] = "deleted"; value["text"] = ""; value["value"] = ""; value["condition"] = ""; value["evidenceIds"] = new JsonArray();
    }
    private static IEnumerable<(string Id, string Payload)> Payloads(SqliteConnection db, SqliteTransaction tx, string table)
    {
        var source=LearningSchema.Number(db,tx,"PRAGMA user_version")==LearningSchema.Version?LearningColdStorage.Source(table):table;
        string? after=null;
        while(true)
        {
            var page=new List<(string Id,string Payload)>();
            using(var command=Command(db,tx,"SELECT id,payload FROM "+source+" WHERE ($after IS NULL OR id>$after) ORDER BY id LIMIT 256",("$after",(object?)after??DBNull.Value)))
            using(var reader=command.ExecuteReader())while(reader.Read())page.Add((reader.GetString(0),reader.GetString(1)));
            if(page.Count==0)yield break;foreach(var row in page)yield return row;after=page[^1].Id;
        }
    }
    private static void UpdatePayload(SqliteConnection db, SqliteTransaction tx, string table, string id, JsonObject payload)
    {
        if(table is "receipts" or "profiles")LearningColdStorage.Write(db,tx,table,id,payload.ToJsonString());
        else Exec(db, tx, "UPDATE " + table + " SET payload=$payload WHERE id=$id", ("$payload", payload.ToJsonString()), ("$id", id));
    }
    private static SqliteCommand Command(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Key, object Value)[] values)
    {
        var command = db.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
        foreach (var (key, value) in values) command.Parameters.AddWithValue(key, value); return command;
    }
    private static void Exec(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Key, object Value)[] values)
    { using var command = Command(db, tx, sql, values); command.ExecuteNonQuery(); }
    private static void WriteMarker(string root) => StorageFiles.Write(Path.Combine(root, "learning", "initialized.marker"), "1"u8.ToArray());
    private static void CreateEmpty(SqliteConnection db) => Exec(db, null, """
        CREATE TABLE policies(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
        CREATE TABLE receipts(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT UNIQUE NOT NULL,scope TEXT NOT NULL,fingerprint TEXT NOT NULL,payload TEXT NOT NULL,eligible INTEGER NOT NULL,processed INTEGER NOT NULL,job_id TEXT);
        CREATE INDEX ix_learning_pending ON receipts(scope,processed,eligible,seq);
        CREATE TABLE evidence(id TEXT PRIMARY KEY,scope TEXT NOT NULL,valid INTEGER NOT NULL,payload TEXT NOT NULL);
        CREATE TABLE profiles(id TEXT PRIMARY KEY,scope TEXT NOT NULL,updated INTEGER NOT NULL,payload TEXT NOT NULL);
        CREATE TABLE jobs(id TEXT PRIMARY KEY,scope TEXT NOT NULL,state TEXT NOT NULL,created INTEGER NOT NULL,payload TEXT NOT NULL,result TEXT);
        CREATE TABLE suppressions(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
        CREATE TABLE history(seq INTEGER PRIMARY KEY AUTOINCREMENT,entry_id TEXT NOT NULL,payload TEXT NOT NULL);
        CREATE TABLE receipt_cold(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
        CREATE TABLE profile_cold(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
        CREATE TABLE learning_watermarks(scope TEXT PRIMARY KEY,sequence INTEGER NOT NULL);
        CREATE TABLE receipt_sequence_origins(id TEXT NOT NULL,sequence INTEGER NOT NULL,PRIMARY KEY(id,sequence));
        PRAGMA user_version=2;
        """);
}
