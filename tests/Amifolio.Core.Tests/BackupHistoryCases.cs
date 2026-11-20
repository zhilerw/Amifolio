using Amifolio.Core;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal static class BackupHistoryCases
{
    internal static (string, Func<Fixture, Task>)[] All =>
    [
        ("backup history real artifact migration backup restore roundtrip keeps historical bytes and references", async f =>
        {
            var original = f.File("old-report.md", "真实旧 artifact 合成内容");
            var old = (await f.Store.ImportFileAsync(original)).Artifact;
            var reference = (await f.Store.ImportFileAsync(f.File("reference.md", "cached reference"))).Artifact;
            using (var db = Open(f.DataRoot)) Exec(db, "UPDATE artifacts SET object_name='ref:synthetic-reference' WHERE id=$id", ("$id", reference.Id));
            f.File("data/context-state/global/state.json", "{\"Documents\":{\"style.md\":\"历史人工文档\"}}");
            f.File("data/preference-state/state.json", "{\"Entries\":[{\"Text\":\"历史偏好\"}]}");
            f.File("data/session-learning.marker", "session-learning-v1");
            var migrated = await new LegacyDataService(f.DataRoot).MigrateAsync(); var archive = Path.GetFileName(migrated.HistoryDirectory!);
            using (var current = Open(f.DataRoot)) Check(Number(current, "SELECT COUNT(*) FROM artifacts") == 1 && Text(current, "SELECT object_name FROM artifacts") == "ref:synthetic-reference", "migration did not leave reference-only current DB");
            var layout = File.ReadAllBytes(Path.Combine(f.DataRoot, "storage-layout.json"));
            var zip = Path.Combine(f.Root, "roundtrip.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            var target = Path.Combine(f.Root, "roundtrip-restored"); await new PortfolioMaintenance(target).RestoreAsync(zip);
            var historical = Path.Combine(target, "history", archive); var objectPath = Path.Combine(historical, "objects", Path.GetFileName(old.ObjectPath));
            Check(File.ReadAllText(objectPath) == File.ReadAllText(original), "migrated original bytes omitted from backup");
            using (var snapshot = Open(historical)) Check(Text(snapshot, "SELECT hash FROM artifacts WHERE id=$id", ("$id", old.Id)) == old.Hash, "historical artifact metadata/hash lost");
            using (var current = Open(target)) Check(Number(current, "SELECT COUNT(*) FROM artifacts") == 1 && Text(current, "SELECT hash FROM artifacts") == "", "historical artifacts resurrected or reference hash was not invalidated");
            Check(File.ReadAllBytes(Path.Combine(target, "storage-layout.json")).SequenceEqual(layout) && File.ReadAllText(Path.Combine(target, "session-learning.marker")) == "session-learning-v1", "layout/session mode markers lost");
            Check(!Directory.Exists(Path.Combine(target, "objects")) && !Directory.Exists(Path.Combine(target, "context")), "restore recreated active legacy layout");
            var documents = await new LegacyDataService(target).ReadHistoryAsync(); Check(documents.Any(d => d.Text == "历史人工文档") && documents.Any(d => d.Text == "历史偏好"), "restored history not readable");
            Check((await new LegacyDataService(target).MigrateAsync()).Status == "already", "restored marker allowed repeat migration");
            var next = Path.Combine(f.Root, "second-generation.zip"); await new PortfolioMaintenance(target).BackupAsync(next);
            var twice = Path.Combine(f.Root, "twice-restored"); await new PortfolioMaintenance(twice).RestoreAsync(next);
            Check(File.ReadAllText(Path.Combine(twice, "history", archive, "objects", Path.GetFileName(old.ObjectPath))) == File.ReadAllText(original), "second backup lost historical objects");
        }),
        ("backup history all legacy archive subtrees and multiple archives survive byte for byte", async f =>
        {
            await f.Store.InitializeAsync();
            var expected = new Dictionary<string, byte[]>();
            foreach (var archive in new[] { Legacy(), Legacy() })
            foreach (var tail in new[] { "objects/old.txt", "objects/nested/unindexed.bin", "context-state/global/state.json", "preference-state/state.json", "recovery/migration.db", "context/global/generations/old/style.md", "manifest.json", "private-notes/备注.txt" })
            {
                var relative = "history/" + archive + "/" + tail; var text = "private historical bytes: " + relative;
                f.File("data/" + relative, text); expected.Add(relative, Encoding.UTF8.GetBytes(text));
            }
            var zip = Path.Combine(f.Root, "all-history.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            var target = Path.Combine(f.Root, "all-history-restored"); await new PortfolioMaintenance(target).RestoreAsync(zip);
            foreach (var (relative, bytes) in expected) Check(File.ReadAllBytes(Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar))).SequenceEqual(bytes), "history omitted/rewritten: " + relative);
        }),
        ("backup history historical portfolio database is snapshotted with committed WAL", async f =>
        {
            var old = (await f.Store.ImportFileAsync(f.File("wal-old.md", "WAL source"))).Artifact;
            var migrated = await new LegacyDataService(f.DataRoot).MigrateAsync();
            using var live = Open(migrated.HistoryDirectory!); Exec(live, "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; UPDATE artifacts SET tags='committed-history-WAL'");
            var zip = Path.Combine(f.Root, "history-wal.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            using (var read = ZipFile.OpenRead(zip)) Check(!read.Entries.Any(e => e.FullName.EndsWith("portfolio.db-wal", StringComparison.Ordinal) || e.FullName.EndsWith("portfolio.db-shm", StringComparison.Ordinal)), "history snapshot depends on raw SQLite sidecars");
            var target = Path.Combine(f.Root, "history-wal-restored"); await new PortfolioMaintenance(target).RestoreAsync(zip);
            using var restored = Open(Path.Combine(target, "history", Path.GetFileName(migrated.HistoryDirectory!)));
            Check(Text(restored, "SELECT tags FROM artifacts WHERE id=$id", ("$id", old.Id)) == "committed-history-WAL" && Text(restored, "PRAGMA quick_check") == "ok", "historical committed WAL row lost");
        }),
        ("backup history markers work without a history directory or active objects", async f =>
        {
            await f.Store.InitializeAsync(); f.File("data/storage-layout.json", "{\"version\":2}"); f.File("data/session-learning.marker", "session-learning-v1");
            var zip = Path.Combine(f.Root, "markers.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            var target = Path.Combine(f.Root, "markers-restored"); await new PortfolioMaintenance(target).RestoreAsync(zip);
            await new PortfolioStore(target).InitializeAsync();
            Check(File.ReadAllText(Path.Combine(target, "storage-layout.json")) == "{\"version\":2}" && File.Exists(Path.Combine(target, "session-learning.marker")), "empty-layout marker lost");
            Check(!Directory.Exists(Path.Combine(target, "objects")), "marker was ignored on reopened store");
        }),
        ("backup history credentials excluded and restore still withdraws current consent", async f =>
        {
            await f.Store.InitializeAsync(); var archive = Legacy();
            foreach (var tail in new[] { "settings/ai.json", "private/credentials.json", "private/.env", "private/key.pem", "credentials/token.txt", "learning/credential.bin" }) f.File("data/history/" + archive + "/" + tail, "SYNTHETIC_HISTORICAL_CREDENTIAL");
            f.File("data/history/" + archive + "/context/global/START.md", "historical private projection");
            f.File("data/settings/ai.json", "{\"Endpoint\":\"https://example.invalid/v1\",\"Model\":\"synthetic\",\"RemoteAllowed\":true,\"ProtectedApiKey\":\"SYNTHETIC_ACTIVE_CREDENTIAL\"}");
            var learning = new LearningStore(f.DataRoot); await learning.SetPolicyAsync("p", new() { Capture = true, Learn = true, AutoActivate = true, Share = true, Remote = true, OriginalPaths = true, ContributeGlobal = true, Endpoint = "https://example.invalid/v1" });
            var zip = Path.Combine(f.Root, "credentials.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            using (var read = ZipFile.OpenRead(zip))
            foreach (var entry in read.Entries.Where(e => !e.FullName.EndsWith("sqlite", StringComparison.Ordinal) && !e.FullName.EndsWith(".db", StringComparison.Ordinal)))
            { using var text = new StreamReader(entry.Open()); Check(!(await text.ReadToEndAsync()).Contains("SYNTHETIC_", StringComparison.Ordinal), "credential leaked: " + entry.FullName); }
            var target = Path.Combine(f.Root, "credentials-restored"); await new PortfolioMaintenance(target).RestoreAsync(zip);
            var policy = await new LearningStore(target).PolicyAsync("p");
            Check(!policy.Capture && !policy.Learn && !policy.AutoActivate && !policy.Share && !policy.Remote && !policy.OriginalPaths && !policy.ContributeGlobal, "restore granted learning consent");
            Check(!Directory.Exists(Path.Combine(target, "context")) && !Directory.Exists(Path.Combine(target, "learning", "shared")), "private history was republished");
            using var settings = JsonDocument.Parse(File.ReadAllText(Path.Combine(target, "settings", "ai.json"))); Check(!settings.RootElement.GetProperty("RemoteAllowed").GetBoolean() && !settings.RootElement.TryGetProperty("ProtectedApiKey", out _), "settings credentials/consent restored");
        }),
        ("backup history refuses junctions at history archive and nested levels", async f =>
        {
            await f.Store.InitializeAsync(); var outside = Directory.CreateDirectory(Path.Combine(f.Root, "outside")).FullName; File.WriteAllText(Path.Combine(outside, "keep.txt"), "untouched outside");
            foreach (var relative in new[] { "history", "history/" + Legacy(), "history/" + Legacy() + "/objects/linked" })
            {
                var link = Path.Combine(f.DataRoot, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(link)!);
                await Junction(link, outside);
                try { var zip = Path.Combine(f.Root, Guid.NewGuid().ToString("N") + ".zip"); await Reject(() => new PortfolioMaintenance(f.DataRoot).BackupAsync(zip)); Check(!File.Exists(zip), "linked history produced archive"); }
                finally { Directory.Delete(link); }
            }
            Check(File.ReadAllText(Path.Combine(outside, "keep.txt")) == "untouched outside", "history link followed or changed outside");
        }),
        ("backup history rejects archive traversal credential and link entries before root replacement", async f =>
        {
            await f.Store.InitializeAsync(); var valid = Path.Combine(f.Root, "safe.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(valid);
            var archive = Legacy();
            foreach (var path in new[] { "history/legacy-not-guid/notes.txt", "history/" + archive + "/../escape", "history/" + archive + "/objects/a:stream", "history/" + archive + "/objects/a. ", "history/" + archive + "/credentials/key.txt", "history/" + archive + "/settings/ai.json", "history/" + archive + "/private/credentials.json", "history/" + archive + "/PORTFOLIO.DB-WAL" })
            {
                var entries = ReadZip(valid); entries[path] = "bad"u8.ToArray(); var bad = Path.Combine(f.Root, Guid.NewGuid().ToString("N") + ".zip"); WriteZip(bad, entries);
                await Reject(() => new PortfolioMaintenance(f.DataRoot).RestoreAsync(bad));
            }
            foreach (var attributes in new[] { unchecked((int)0xA1FF0000), (int)FileAttributes.ReparsePoint })
            {
                var entries = ReadZip(valid); var path = "history/" + archive + "/objects/link"; entries[path] = "outside"u8.ToArray();
                var bad = Path.Combine(f.Root, Guid.NewGuid().ToString("N") + ".zip"); WriteZip(bad, entries, path, attributes); await Reject(() => new PortfolioMaintenance(f.DataRoot).RestoreAsync(bad));
            }
            Check(File.Exists(Path.Combine(f.DataRoot, "portfolio.db")) && !Directory.Exists(Path.Combine(f.DataRoot, "history")), "unsafe zip replaced current root");
        }),
        ("backup history corrupt historical artifacts and index paths fail without replacing live root", async f =>
        {
            var old = (await f.Store.ImportFileAsync(f.File("old.md", "historical body"))).Artifact;
            var migrated = await new LegacyDataService(f.DataRoot).MigrateAsync(); var archive = Path.GetFileName(migrated.HistoryDirectory!);
            var valid = Path.Combine(f.Root, "history-valid.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(valid);
            foreach (var mode in new[] { "bytes", "missing", "index-path", "database" })
            {
                var entries = ReadZip(valid); var objectPath = "history/" + archive + "/objects/" + Path.GetFileName(old.ObjectPath); var dbPath = "history/" + archive + "/portfolio.db";
                if (mode == "bytes") entries[objectPath] = "different content"u8.ToArray();
                if (mode == "missing") entries.Remove(objectPath);
                if (mode == "database") entries[dbPath] = "not sqlite"u8.ToArray();
                if (mode == "index-path")
                {
                    var local = Path.Combine(f.Root, "malicious.sqlite"); File.WriteAllBytes(local, entries[dbPath]);
                    using (var db = OpenFile(local)) Exec(db, "UPDATE artifacts SET object_name='../outside.md'"); entries[dbPath] = File.ReadAllBytes(local);
                }
                var bad = Path.Combine(f.Root, mode + ".zip"); WriteZip(bad, entries); await Reject(() => new PortfolioMaintenance(f.DataRoot).RestoreAsync(bad));
                Check(File.ReadAllText(Path.Combine(migrated.HistoryDirectory!, "objects", Path.GetFileName(old.ObjectPath))) == "historical body", "failed restore replaced original history");
            }
            var indexed = Path.Combine(migrated.HistoryDirectory!, "objects", Path.GetFileName(old.ObjectPath));
            var held = Path.Combine(f.Root, "held-original.md"); File.Move(indexed, held);
            try { await Reject(() => new PortfolioMaintenance(f.DataRoot).BackupAsync(Path.Combine(f.Root, "missing-history.zip"))); }
            finally { File.Move(held, indexed); }
            var excludedName = Path.GetFileNameWithoutExtension(indexed) + ".pem"; var excludedPath = Path.Combine(Path.GetDirectoryName(indexed)!, excludedName); File.Move(indexed, excludedPath);
            using (var db = Open(migrated.HistoryDirectory!)) Exec(db, "UPDATE artifacts SET object_name=$name WHERE id=$id", ("$name", excludedName), ("$id", old.Id));
            try { await Reject(() => new PortfolioMaintenance(f.DataRoot).BackupAsync(Path.Combine(f.Root, "excluded-history.zip"))); }
            finally
            {
                using (var db = Open(migrated.HistoryDirectory!)) Exec(db, "UPDATE artifacts SET object_name=$name WHERE id=$id", ("$name", Path.GetFileName(indexed)), ("$id", old.Id));
                File.Move(excludedPath, indexed);
            }
        }),
        ("backup history malformed markers and incomplete migration fail closed", async f =>
        {
            await f.Store.InitializeAsync(); var valid = Path.Combine(f.Root, "no-marker.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(valid);
            foreach (var (path, text) in new[] { ("storage-layout.json", "{\"version\":3}"), ("storage-layout.json", "{\"version\":2,\"history\":\"../outside\"}"), ("storage-layout.json", "{\"version\":2,\"history\":\"" + Legacy() + "\"}"), ("storage-layout.json", "{\"version\":2,\"ProtectedApiKey\":\"secret\"}"), ("session-learning.marker", "future-or-secret") })
            {
                var marker = f.File("data/" + path, text); var zip = Path.Combine(f.Root, Guid.NewGuid().ToString("N") + ".zip");
                try { await Reject(() => new PortfolioMaintenance(f.DataRoot).BackupAsync(zip)); } finally { File.Delete(marker); }
                var entries = ReadZip(valid); entries[path] = Encoding.UTF8.GetBytes(text); WriteZip(zip, entries); await Reject(() => new PortfolioMaintenance(f.DataRoot).RestoreAsync(zip));
            }
            var pending = f.File("data/legacy-migration.json", "{}");
            try { await Reject(() => new PortfolioMaintenance(f.DataRoot).BackupAsync(Path.Combine(f.Root, "pending.zip"))); }
            finally { File.Delete(pending); }
        }),
    ];

    private static string Legacy() => "legacy-" + Guid.NewGuid().ToString("N");
    private static SqliteConnection Open(string root) => OpenFile(Path.Combine(root, "portfolio.db"));
    private static SqliteConnection OpenFile(string path) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()); db.Open(); return db; }
    private static void Exec(SqliteConnection db, string sql, params (string, object)[] args) { using var c = Command(db, sql, args); c.ExecuteNonQuery(); }
    private static long Number(SqliteConnection db, string sql, params (string, object)[] args) { using var c = Command(db, sql, args); return Convert.ToInt64(c.ExecuteScalar()); }
    private static string Text(SqliteConnection db, string sql, params (string, object)[] args) { using var c = Command(db, sql, args); return Convert.ToString(c.ExecuteScalar()) ?? ""; }
    private static SqliteCommand Command(SqliteConnection db, string sql, (string, object)[] args) { var c = db.CreateCommand(); c.CommandText = sql; foreach (var (key, value) in args) c.Parameters.AddWithValue(key, value); return c; }
    private static Dictionary<string, byte[]> ReadZip(string path)
    {
        using var zip = ZipFile.OpenRead(path); var entries = new Dictionary<string, byte[]>();
        foreach (var entry in zip.Entries.Where(e => e.FullName != "manifest.json")) { using var input = entry.Open(); using var output = new MemoryStream(); input.CopyTo(output); entries.Add(entry.FullName, output.ToArray()); } return entries;
    }
    private static void WriteZip(string path, Dictionary<string, byte[]> entries, string? special = null, int attributes = 0)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries) { var entry = zip.CreateEntry(name); if (name == special) entry.ExternalAttributes = attributes; using var output = entry.Open(); output.Write(bytes); }
        using var manifest = zip.CreateEntry("manifest.json").Open(); JsonSerializer.Serialize(manifest, new { FormatVersion = 1, Files = entries.Select(e => new { Path = e.Key, Bytes = e.Value.LongLength, Sha256 = Convert.ToHexString(SHA256.HashData(e.Value)) }).ToArray() });
    }
    private static async Task Junction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command"); start.ArgumentList.Add("New-Item -ItemType Junction -Path '" + link.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' -ErrorAction Stop | Out-Null");
        using var p = Process.Start(start)!; await p.WaitForExitAsync(); if (p.ExitCode != 0) throw new Exception(await p.StandardError.ReadToEndAsync());
    }
    private static async Task Reject(Func<Task> run) { try { await run(); } catch (Exception e) when (e is IOException or SqliteException) { return; } throw new Exception("unsafe backup/restore unexpectedly succeeded"); }
    private static void Check(bool pass, string message) { if (!pass) throw new Exception(message); }
}
