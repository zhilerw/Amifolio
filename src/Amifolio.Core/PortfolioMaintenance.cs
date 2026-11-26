using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Amifolio.Core;

/// <summary>Explicit maintenance operations. Credentials and active published projections are never backed up.</summary>
public sealed class PortfolioMaintenance(string dataRoot)
{
    private readonly string root = ArchiveSafety.LocalPath(dataRoot);
    private const long MaxTotal = 32L * 1024 * 1024 * 1024;
    private const int MaxEntries = 100_000;
    private sealed record Entry(string Path, long Bytes, string Sha256);
    private sealed record Manifest(int FormatVersion, IReadOnlyList<Entry> Files);
    private sealed record Swap(string Staging, string Previous, bool HadPrevious);

    public async Task BackupAsync(string destination, CancellationToken ct = default)
    {
        destination = ArchiveSafety.LocalPath(destination); ArchiveSafety.NoReparse(destination);
        if (ArchiveSafety.Within(destination, root)) throw new IOException("备份须保存在作品库之外。");
        await new PortfolioStore(root).InitializeAsync(ct).ConfigureAwait(false);
        await using var lease = await StorageLease.ExclusiveAsync(root, ct).ConfigureAwait(false);
        var staging = root + ".backup-" + Guid.NewGuid().ToString("N");
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        ArchiveSafety.NoReparse(staging); Directory.CreateDirectory(staging); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        try
        {
            var pendingMigration = Path.Combine(root, "legacy-migration.json"); ArchiveSafety.NoReparse(pendingMigration);
            if (File.Exists(pendingMigration)) throw new IOException("旧资料迁移尚未完成，请完成迁移后再备份。");
            using (var db = Open(root))
            using (var snapshot = Open(staging, create: true)) db.BackupDatabase(snapshot);
            var paths = new List<(string Relative, string Full)> { ("portfolio.db", Path.Combine(staging, "portfolio.db")) };
            CaptureHistory(staging, paths, ct);
            ValidateLayoutMarkers(root);
            foreach (var name in new[] { "storage-layout.json", "session-learning.marker" })
                if (File.Exists(Path.Combine(root, name))) paths.Add((name, Path.Combine(root, name)));
            if (LearningMaintenance.Capture(root, staging, ct))
            {
                paths.Add(("learning/state.sqlite", Path.Combine(staging, "learning", "state.sqlite")));
                paths.Add(("learning/initialized.marker", Path.Combine(staging, "learning", "initialized.marker")));
            }
            using (var db = Open(staging))
            using (var command = db.CreateCommand())
            {
                command.CommandText = "SELECT object_name,deleted FROM artifacts";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var name = reader.GetString(0); if(name.StartsWith("ref:",StringComparison.Ordinal))continue; ValidateObjectName(name);
                    var path = Path.Combine(root, "objects", name);
                    if (File.Exists(path)) paths.Add(("objects/" + name, path));
                    else if (reader.GetInt32(1) == 0) throw new IOException("作品副本缺失，备份已停止。");
                }
            }
            var states = Path.Combine(root, "context-state"); ArchiveSafety.NoReparse(states);
            if (Directory.Exists(states)) foreach (var directory in Directory.GetDirectories(states))
            {
                ArchiveSafety.NoReparse(directory); ValidateScope(Path.GetFileName(directory));
                var file = Path.Combine(directory, "state.json");
                if (File.Exists(file))
                {
                    var relative="context-state/"+Path.GetFileName(directory)+"/state.json";
                    var captured=Path.Combine(staging,relative.Replace('/',Path.DirectorySeparatorChar));
                    StorageFiles.Write(captured,RevocationJournal.CaptureState(root,Path.GetFileName(directory),file));
                    paths.Add((relative,captured));
                }
            }
            var budget = Path.Combine(root, "context-state", "automation-budget.json");
            ArchiveSafety.NoReparse(budget);
            if(File.Exists(budget))
            {
                var captured = Path.Combine(staging,"context-state","automation-budget.json");
                StorageFiles.Write(captured,AutomaticBudgetRecovery.Capture(budget));
                paths.Add(("context-state/automation-budget.json",captured));
            }
            var preferences = Path.Combine(root, "preference-state", "state.json");
            ArchiveSafety.NoReparse(preferences);
            if (File.Exists(preferences))
            {
                var captured = Path.Combine(staging, "preference-state", "state.json");
                StorageFiles.Write(captured, RevocationJournal.CapturePreferences(preferences, restore: false));
                paths.Add(("preference-state/state.json", captured));
            }
            var settings = Path.Combine(root, "settings", "ai.json");
            if (File.Exists(settings))
            {
                var safe = SanitizeSettings(settings);
                var sanitized = Path.Combine(staging, "ai.json"); StorageFiles.Write(sanitized, safe);
                paths.Add(("settings/ai.json", sanitized));
            }
            var entries = new List<Entry>(); long total = 0;
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var (relative, full) in paths.DistinctBy(x => x.Relative, StringComparer.OrdinalIgnoreCase))
                    {
                        ct.ThrowIfCancellationRequested();
                        if (entries.Count >= MaxEntries) throw new IOException("作品库文件数超过此备份格式上限。");
                        using var input = ArchiveSafety.OpenRead(full);
                        total += input.Length; if (total > MaxTotal) throw new IOException("作品库超过此备份格式的 32 GiB 上限。");
                        using var output = zip.CreateEntry(relative, CompressionLevel.Fastest).Open();
                        var hash = await CopyHashAsync(input, output, input.Length, ct).ConfigureAwait(false);
                        entries.Add(new Entry(relative, input.Length, hash));
                    }
                    using var manifest = zip.CreateEntry("manifest.json").Open();
                    JsonSerializer.Serialize(manifest, new Manifest(1, entries));
                }
                file.Flush(true);
            }
            ct.ThrowIfCancellationRequested(); ArchiveSafety.NoReparse(destination); File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); StorageFiles.DeleteTree(staging); }
    }

    public async Task RestoreAsync(string archive, CancellationToken ct = default)
    {
        archive = ArchiveSafety.LocalPath(archive);
        if (ArchiveSafety.Within(archive, root)) throw new IOException("请先将备份移到作品库之外。");
        var staging = root + ".restore-" + Guid.NewGuid().ToString("N");
        ArchiveSafety.NoReparse(staging); Directory.CreateDirectory(staging); var swapped = false;
        try
        {
            await ExtractVerifiedAsync(archive, staging, ct).ConfigureAwait(false);
            ValidateLayoutMarkers(staging);
            ValidateDatabase(staging, ct);
            ValidateHistory(staging, ct);
            LearningMaintenance.PrepareRestore(staging, ct);
            using(var db=Open(staging))
            {
                using var command=db.CreateCommand();command.CommandText="INSERT INTO store_meta(key,value) VALUES('remote_allowed','0') ON CONFLICT(key) DO UPDATE SET value='0'";command.ExecuteNonQuery();
                command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='collection_grants'";
                if(Convert.ToInt32(command.ExecuteScalar())!=0){command.CommandText="UPDATE collection_grants SET enabled=0,revision=revision+1";command.ExecuteNonQuery();}
                command.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='codex_work_sync'";
                if(Convert.ToInt32(command.ExecuteScalar())!=0){command.CommandText="UPDATE codex_work_sync SET enabled=0,state='off'";command.ExecuteNonQuery();}
                command.CommandText="UPDATE artifacts SET hash='',preview='',extraction_status='未解析',version=version+1 WHERE object_name LIKE 'ref:%'";command.ExecuteNonQuery();
                command.CommandText="UPDATE work_items SET state='cancelled',owner=NULL,error_code='sources_changed' WHERE artifact_id IN(SELECT id FROM artifacts WHERE object_name LIKE 'ref:%') AND state IN('queued','running')";command.ExecuteNonQuery();
            }
            var states = Path.Combine(staging, "context-state");
            if (Directory.Exists(states)) foreach (var directory in Directory.GetDirectories(states))
                RevocationJournal.Withdraw(staging, Path.GetFileName(directory), restore: true);
            var preferences = Path.Combine(staging, "preference-state", "state.json");
            if (File.Exists(preferences)) StorageFiles.Write(preferences, RevocationJournal.CapturePreferences(preferences, restore: true));
            var settings = Path.Combine(staging, "settings", "ai.json");
            if (File.Exists(settings)) StorageFiles.Write(settings, SanitizeSettings(settings));
            await using var session = await StorageLease.RestoreSessionAsync(root, ct).ConfigureAwait(false);
            await using var lease = await StorageLease.ExclusiveAsync(root, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested(); ArchiveSafety.NoReparse(root);
            AutomaticBudgetRecovery.Merge(Path.Combine(staging,"context-state","automation-budget.json"),Path.Combine(root,"context-state","automation-budget.json"));
            LearningMaintenance.Merge(staging, root, ct);
            ct.ThrowIfCancellationRequested();
            var previous = root + ".before-restore-" + Guid.NewGuid().ToString("N");
            var swap = new Swap(staging, previous, Directory.Exists(root));
            StorageFiles.Write(root + ".restore.json", JsonSerializer.SerializeToUtf8Bytes(swap));
            // Cancellation is observed before, never halfway through, the directory exchange.
            if (swap.HadPrevious) Directory.Move(root, previous);
            Directory.Move(staging, root); swapped = true;
            File.Delete(root + ".restore.json");
        }
        catch
        {
            // If an exchange failed, the next access also knows how to roll it back.
            if (File.Exists(root + ".restore.json"))
            {
                await using var recovery = await StorageLease.ExclusiveAsync(root, CancellationToken.None).ConfigureAwait(false);
            }
            throw;
        }
        finally { if (!swapped && !File.Exists(root + ".restore.json")) StorageFiles.DeleteTree(staging); }
    }

    public async Task<int> CollectAsync(TimeSpan retention, CancellationToken ct = default)
    {
        if (retention < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
        await new PortfolioStore(root).InitializeAsync(ct).ConfigureAwait(false);
        await using var lease = await StorageLease.ExclusiveAsync(root, ct).ConfigureAwait(false);
        using var db = Open(root); using var command = db.CreateCommand();
        command.CommandText = "SELECT object_name FROM artifacts a WHERE deleted=1 AND deleted_at IS NOT NULL AND deleted_at <= $cutoff AND NOT EXISTS(SELECT 1 FROM artifacts b WHERE b.object_name=a.object_name AND b.deleted=0)";
        command.Parameters.AddWithValue("$cutoff", DateTimeOffset.UtcNow.Subtract(retention).ToString("O"));
        var names = new List<string>(); using (var reader = command.ExecuteReader()) while (reader.Read()) names.Add(reader.GetString(0));
        var count = 0;
        foreach (var name in names)
        {
            ct.ThrowIfCancellationRequested(); if(name.StartsWith("ref:",StringComparison.Ordinal))continue; ValidateObjectName(name); var path = Path.Combine(root, "objects", name); ArchiveSafety.NoReparse(path);
            if (!File.Exists(path)) continue;
            File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); count++;
        }
        // Unindexed bytes from interrupted imports have at least a one-day grace period.
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        command.Parameters.Clear(); command.CommandText = "SELECT object_name FROM artifacts";
        using (var reader = command.ExecuteReader()) while (reader.Read()) referenced.Add(reader.GetString(0));
        var orphanCutoff = DateTime.UtcNow.Subtract(retention < TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : retention);
        foreach (var path in Directory.GetFiles(Path.Combine(root, "objects")))
        {
            ct.ThrowIfCancellationRequested(); ArchiveSafety.NoReparse(path);
            var name = Path.GetFileName(path); if (referenced.Contains(name) || File.GetLastWriteTimeUtc(path) > orphanCutoff) continue;
            try { ValidateObjectName(name); } catch (IOException) { continue; }
            try
            {
                using (var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None)) { }
                File.SetAttributes(path, FileAttributes.Normal); File.Delete(path); count++;
            }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { }
        }
        return count;
    }

    internal static void RecoverInterruptedRestore(string root)
    {
        var journal = root + ".restore.json"; ArchiveSafety.NoReparse(journal); if (!File.Exists(journal)) return;
        if (new FileInfo(journal).Length > 16_384) throw new IOException("恢复记录损坏，请保留作品库并检查恢复点。");
        Swap swap;
        try { swap = JsonSerializer.Deserialize<Swap>(File.ReadAllBytes(journal)) ?? throw new JsonException(); }
        catch (JsonException e) { throw new IOException("恢复记录损坏，请保留作品库并检查恢复点。", e); }
        ValidateSibling(root, swap.Staging, ".restore-"); ValidateSibling(root, swap.Previous, ".before-restore-");
        ArchiveSafety.NoReparse(root);
        if (!Directory.Exists(root))
        {
            if (swap.HadPrevious && Directory.Exists(swap.Previous)) Directory.Move(swap.Previous, root);
            else if (!swap.HadPrevious && Directory.Exists(swap.Staging)) Directory.Move(swap.Staging, root);
            else throw new IOException("恢复交换未完成，请保留恢复点并手动检查。");
        }
        File.Delete(journal);
        // Previous libraries are intentionally retained; cleanup is an explicit user decision.
        if (Directory.Exists(swap.Staging)) StorageFiles.DeleteTree(swap.Staging);
    }

    private static async Task ExtractVerifiedAsync(string archive, string staging, CancellationToken ct)
    {
        using var input = ArchiveSafety.OpenRead(archive); using var zip = new ZipArchive(input, ZipArchiveMode.Read);
        if (zip.Entries.Count > MaxEntries + 1) throw new IOException("备份文件数超过上限。");
        var unique = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in zip.Entries)
        {
            ValidateRelative(entry.FullName);
            var kind = (entry.ExternalAttributes >> 16) & 0xF000;
            if (kind is not (0 or 0x8000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || !unique.TryAdd(entry.FullName, entry)) throw new IOException("备份包含链接、特殊文件或重名路径。");
            if (entry.Length < 0 || entry.Length > 1024L * 1024 * 1024 || (total += entry.Length) > MaxTotal) throw new IOException("备份展开大小超过上限。");
        }
        if (!unique.TryGetValue("manifest.json", out var manifestEntry) || manifestEntry.Length > 32 * 1024 * 1024) throw new IOException("缺少有效备份清单。");
        Manifest manifest;
        try { using var stream = manifestEntry.Open(); manifest = JsonSerializer.Deserialize<Manifest>(stream) ?? throw new JsonException(); }
        catch (JsonException e) { throw new IOException("备份清单无法读取。", e); }
        if (manifest.FormatVersion != 1 || manifest.Files is null || manifest.Files.Count != unique.Count - 1) throw new IOException("不支持的备份格式或清单不完整。");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in manifest.Files)
        {
            ct.ThrowIfCancellationRequested(); ValidateRelative(item.Path);
            if (item.Path == "manifest.json" || !seen.Add(item.Path) || !unique.TryGetValue(item.Path, out var entry) || entry.Length != item.Bytes) throw new IOException("备份清单与文件不符。");
            var target = Path.Combine(staging, item.Path.Replace('/', Path.DirectorySeparatorChar));
            ArchiveSafety.NoReparse(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var source = entry.Open(); using var destination = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var hash = await CopyHashAsync(source, destination, item.Bytes, ct).ConfigureAwait(false); destination.Flush(true);
            if (!hash.Equals(item.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("备份校验失败，原库未替换。");
        }
        if (!seen.Contains("portfolio.db")) throw new IOException("备份缺少作品数据库。");
    }

    private static void ValidateDatabase(string directory, CancellationToken ct)
    {
        using var db = Open(directory); StoreSchema.Ensure(db, directory, existingLibrary: true);
        using var command = db.CreateCommand(); command.CommandText = "SELECT object_name,hash,bytes FROM artifacts WHERE deleted=0";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested(); var name = reader.GetString(0); if(name.StartsWith("ref:",StringComparison.Ordinal))continue; ValidateObjectName(name);
            using var file = ArchiveSafety.OpenRead(Path.Combine(directory, "objects", name));
            if (file.Length != reader.GetInt64(2) || !Convert.ToHexString(SHA256.HashData(file)).Equals(reader.GetString(1), StringComparison.OrdinalIgnoreCase)) throw new IOException("备份作品内容与索引不符。");
        }
    }

    private static byte[] SanitizeSettings(string path)
    {
        ArchiveSafety.NoReparse(path); if (new FileInfo(path).Length > 65_536) throw new IOException("AI 配置文件超过上限。");
        try
        {
            var json = JsonNode.Parse(File.ReadAllBytes(path))?.AsObject() ?? throw new JsonException();
            // Allowlist instead of retaining arbitrary future credential fields.
            return JsonSerializer.SerializeToUtf8Bytes(new { FormatVersion = 1, Endpoint = json["Endpoint"]?.GetValue<string>() ?? "", Model = json["Model"]?.GetValue<string>() ?? "", RemoteAllowed = false });
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { throw new IOException("AI 配置损坏，备份已停止。", e); }
    }

    private static async Task<string> CopyHashAsync(Stream input, Stream output, long expected, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65_536]; long bytes = 0;
        while (true)
        {
            var n = await input.ReadAsync(buffer, ct).ConfigureAwait(false); if (n == 0) break;
            bytes += n; if (bytes > expected) throw new IOException("备份文件长度发生变化。");
            hash.AppendData(buffer, 0, n); await output.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
        }
        if (bytes != expected) throw new IOException("备份文件不完整。");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
    private static void ValidateRelative(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.Contains(':') || path.Split('/').Any(x => x is "" or "." or ".." || x.EndsWith('.') || x.EndsWith(' '))) throw new IOException("备份路径无效。");
        var parts = path.Split('/');
        if (parts.Length > ArchiveSafety.MaxDepth) throw new IOException("备份目录深度超过上限。");
        foreach (var part in parts)
            try { ArchiveSafety.Name(part); } catch (ArgumentException e) { throw new IOException("备份路径无效。", e); }
        if (path is "portfolio.db" or "manifest.json" or "settings/ai.json" or "preference-state/state.json" or "context-state/automation-budget.json" or "learning/state.sqlite" or "learning/initialized.marker" or "storage-layout.json" or "session-learning.marker") return;
        if (parts.Length >= 3 && parts[0] == "history")
        {
            ValidateLegacyName(parts[1]);
            if (HistoryCredential(parts.Skip(2)) || (parts.Length == 3 && HistoryIndexSidecar(parts[2]))) throw new IOException("历史备份包含凭据或非独立数据库旁文件。");
            return;
        }
        if (parts.Length == 2 && parts[0] == "objects") { ValidateObjectName(parts[1]); return; }
        if (parts.Length == 3 && parts[0] == "context-state" && parts[2] == "state.json") { ValidateScope(parts[1]); return; }
        throw new IOException("备份包含未受支持的文件。");
    }

    // LegacyDataService has already moved the old objects and snapshotted their index.
    // The reference-only current index cannot enumerate this private historical data.
    private void CaptureHistory(string staging, List<(string Relative, string Full)> paths, CancellationToken ct)
    {
        var history = Path.Combine(root, "history"); ArchiveSafety.NoReparse(history);
        if (!Directory.Exists(history)) return;
        var scanned = 0;
        foreach (var archive in Directory.EnumerateFileSystemEntries(history))
        {
            ct.ThrowIfCancellationRequested(); ArchiveSafety.NoReparse(archive);
            if (++scanned > MaxEntries) throw new IOException("历史归档数量超过备份上限。");
            ValidateLegacyName(Path.GetFileName(archive));
            if (!Directory.Exists(archive)) throw new IOException("历史归档必须是普通目录。");
            var snapshot = Path.Combine(archive, "portfolio.db"); ArchiveSafety.NoReparse(snapshot);
            var stack = new Stack<(string Path, int Depth)>(); stack.Push((archive, 2));
            while (stack.TryPop(out var directory))
            {
                ct.ThrowIfCancellationRequested(); ArchiveSafety.NoReparse(directory.Path);
                foreach (var full in Directory.EnumerateFileSystemEntries(directory.Path))
                {
                    ct.ThrowIfCancellationRequested(); ArchiveSafety.NoReparse(full);
                    if (++scanned > MaxEntries || directory.Depth >= ArchiveSafety.MaxDepth) throw new IOException("历史文件数量或目录深度超过备份上限。");
                    var relative = Path.GetRelativePath(root, full).Replace('\\', '/');
                    if (HistoryCredential(relative.Split('/').Skip(2))) continue;
                    if (Directory.Exists(full)) { stack.Push((full, directory.Depth + 1)); continue; }
                    if (directory.Path == archive && HistoryIndexSidecar(Path.GetFileName(full)))
                    {
                        if (!File.Exists(snapshot)) throw new IOException("历史数据库缺失，不能仅备份旁文件。");
                        continue; // SQLite's backup below includes committed WAL content.
                    }
                    ValidateRelative(relative);
                    if (full.Equals(snapshot, ArchiveSafety.PathComparison))
                    {
                        var captured = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(captured)!); ArchiveSafety.NoReparse(captured);
                        using var source = OpenHistoryDatabase(full, SqliteOpenMode.ReadOnly);
                        using var target = OpenHistoryDatabase(captured, SqliteOpenMode.ReadWriteCreate);
                        source.BackupDatabase(target);
                        using (var standalone = target.CreateCommand()) { standalone.CommandText = "PRAGMA journal_mode=DELETE"; standalone.ExecuteNonQuery(); }
                        ValidateHistoricalIndex(target, archive, verifyContents: false, ct);
                        paths.Add((relative, captured));
                    }
                    else paths.Add((relative, full));
                    if (paths.Count > MaxEntries) throw new IOException("作品库文件数超过此备份格式上限。");
                }
            }
        }
    }

    private static void ValidateHistory(string directory, CancellationToken ct)
    {
        var history = Path.Combine(directory, "history"); ArchiveSafety.NoReparse(history);
        if (!Directory.Exists(history)) return;
        foreach (var archive in Directory.EnumerateDirectories(history))
        {
            ct.ThrowIfCancellationRequested(); ValidateLegacyName(Path.GetFileName(archive)); ArchiveSafety.NoReparse(archive);
            var path = Path.Combine(archive, "portfolio.db"); ArchiveSafety.NoReparse(path);
            if (!File.Exists(path)) continue; // Some pre-index/private-document archives have no index.
            // Historical databases remain inert, unmodified history. Do not migrate their
            // schema, enqueue work, grant consent, or merge their artifacts into current DB.
            using var db = OpenHistoryDatabase(path, SqliteOpenMode.ReadOnly);
            ValidateHistoricalIndex(db, archive, verifyContents: true, ct);
        }
    }

    private static void ValidateHistoricalIndex(SqliteConnection db, string archive, bool verifyContents, CancellationToken ct)
    {
        using var command = db.CreateCommand(); command.CommandText = "PRAGMA quick_check";
        if (command.ExecuteScalar() as string != "ok") throw new IOException("历史数据库检查未通过。");
        command.CommandText = "PRAGMA user_version"; var version = Convert.ToInt32(command.ExecuteScalar());
        if (version < 0 || version > StoreSchema.Version) throw new IOException("历史数据库版本不受支持。");
        command.CommandText = "SELECT object_name,hash,bytes,deleted FROM artifacts";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested(); var name = reader.GetString(0);
            if (name.StartsWith("ref:", StringComparison.Ordinal)) continue; // Never resolve/read an original source.
            ValidateObjectName(name); var full = Path.Combine(archive, "objects", name); ArchiveSafety.NoReparse(full);
            var excluded = HistoryCredential(["objects", name]);
            if (reader.GetInt64(3) != 0 && (!File.Exists(full) || !verifyContents && excluded)) continue;
            // Never acknowledge a backup whose active historical index refers to bytes
            // that are missing or must be excluded as credentials.
            if (!File.Exists(full) || !verifyContents && excluded) throw new IOException("历史作品副本缺失或属于凭据排除项，备份已停止。");
            if (!verifyContents) continue;
            using var file = ArchiveSafety.OpenRead(full);
            if (file.Length != reader.GetInt64(2) || !Convert.ToHexString(SHA256.HashData(file)).Equals(reader.GetString(1), StringComparison.OrdinalIgnoreCase)) throw new IOException("历史作品内容与归档索引不符。");
        }
    }

    private static bool HistoryCredential(IEnumerable<string> components) => components.Any(part =>
        RepositorySnapshot.IsSecret(part) || part.Equals("settings", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("keys", StringComparison.OrdinalIgnoreCase) || part.Equals(".keys", StringComparison.OrdinalIgnoreCase) ||
        part.Equals("secrets", StringComparison.OrdinalIgnoreCase) || part.Equals(".secrets", StringComparison.OrdinalIgnoreCase) ||
        part.Equals(".credentials", StringComparison.OrdinalIgnoreCase) || part.Equals("credential", StringComparison.OrdinalIgnoreCase) ||
        part.StartsWith("credential.", StringComparison.OrdinalIgnoreCase));

    private static void ValidateLegacyName(string name)
    {
        if (name.Length != 39 || !Regex.IsMatch(name, "^legacy-[a-f0-9]{32}$", RegexOptions.CultureInvariant)) throw new IOException("历史归档名称无效。");
    }
    private static bool HistoryIndexSidecar(string name) => new[] { "portfolio.db-wal", "portfolio.db-shm", "portfolio.db-journal" }.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static void ValidateLayoutMarkers(string directory)
    {
        var layout = Path.Combine(directory, "storage-layout.json"); var session = Path.Combine(directory, "session-learning.marker");
        ArchiveSafety.NoReparse(layout); ArchiveSafety.NoReparse(session);
        if (File.Exists(layout))
        {
            try
            {
                using var document = JsonDocument.Parse(ReadMarker(layout)); var value = document.RootElement;
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 2) throw new IOException("数据布局标记无效。");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                    if (property.Name is not ("version" or "history") || !names.Add(property.Name)) throw new IOException("数据布局标记字段无效。");
                if (value.TryGetProperty("history", out var archived))
                {
                    if (archived.ValueKind != JsonValueKind.String) throw new IOException("数据布局历史指针无效。");
                    var name = archived.GetString()!; ValidateLegacyName(name);
                    var history = Path.Combine(directory, "history", name); ArchiveSafety.NoReparse(history);
                    if (!Directory.Exists(history)) throw new IOException("数据布局指向的历史归档缺失。");
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { throw new IOException("数据布局标记损坏。", e); }
        }
        if (File.Exists(session) && !ReadMarker(session).AsSpan().SequenceEqual("session-learning-v1"u8)) throw new IOException("会话学习标记版本无效。");
    }
    private static byte[] ReadMarker(string path)
    {
        using var stream = ArchiveSafety.OpenRead(path); if (stream.Length > 4096) throw new IOException("数据布局标记超过上限。");
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
    private static SqliteConnection OpenHistoryDatabase(string path, SqliteOpenMode mode)
    {
        ArchiveSafety.NoReparse(path);
        foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) ArchiveSafety.NoReparse(path + suffix);
        // Nested private snapshots can exceed MAX_PATH even though .NET has created
        // their parent directory. Apply the native prefix only after path/link checks.
        var nativePath = OperatingSystem.IsWindows() ? @"\\?\" + path : path;
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = nativePath, Mode = mode, Pooling = false }.ToString());
        try
        {
            db.Open(); ArchiveSafety.NoReparse(path);
            foreach (var suffix in new[] { "-wal", "-shm", "-journal" }) ArchiveSafety.NoReparse(path + suffix);
            return db;
        }
        catch { db.Dispose(); throw; }
    }
    private static void ValidateObjectName(string name)
    {
        if (name.Length < 32 || name.Length > 56 || !Guid.TryParseExact(name[..32], "N", out _) || (name.Length > 32 && name[32] != '.')) throw new IOException("作品副本路径无效。");
        try { ArchiveSafety.Name(name); } catch (ArgumentException e) { throw new IOException("作品副本路径无效。", e); }
    }
    private static void ValidateScope(string name)
    {
        if (name != "global" && !Regex.IsMatch(name, "^project-[a-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new IOException("上下文范围无效。");
    }
    private static void ValidateSibling(string root, string path, string suffix)
    {
        if (path is null || !path.StartsWith(root + suffix, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(path[(root.Length + suffix.Length)..], "N", out _) || ArchiveSafety.LocalPath(path) != path) throw new IOException("恢复记录路径无效。");
        ArchiveSafety.NoReparse(path);
    }
    private static SqliteConnection Open(string directory, bool create = false)
    {
        var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(directory, "portfolio.db"), Mode = create ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        db.Open(); return db;
    }
}
