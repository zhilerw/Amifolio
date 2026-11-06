using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Amifolio.Core;

/// <summary>Local durable archive. A committed row always refers to an already flushed, independently owned object.</summary>
public sealed class PortfolioStore : IPortfolioStore
{
    public string DataRoot { get; }
    private readonly string objects;
    private readonly string connectionString;
    private readonly SemaphoreSlim initializeGate = new(1, 1);
    // Bound expensive work within one store; independent bridge/UI processes coordinate commits through SQLite.
    private readonly SemaphoreSlim copyGate = new(2, 2);
    private bool initialized;

    public PortfolioStore(string dataRoot)
    {
        DataRoot = ArchiveSafety.LocalPath(dataRoot);
        ArchiveSafety.NoReparse(DataRoot);
        objects = Path.Combine(DataRoot, "objects");
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(DataRoot, "portfolio.db"), Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 15
        }.ToString();
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await initializeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (initialized) return;
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested(); CheckStorage(); Directory.CreateDirectory(objects); CheckStorage();
                using var db = Open();
                using var cmd = Command(db, """
                    PRAGMA journal_mode=WAL;
                    CREATE TABLE IF NOT EXISTS projects(id TEXT PRIMARY KEY, name TEXT NOT NULL, root_path TEXT, created_at TEXT NOT NULL);
                    CREATE TABLE IF NOT EXISTS series(logical_key TEXT PRIMARY KEY, last_version INTEGER NOT NULL);
                    CREATE TABLE IF NOT EXISTS artifacts(
                        id TEXT PRIMARY KEY, title TEXT NOT NULL, original_name TEXT NOT NULL, kind TEXT NOT NULL,
                        source TEXT NOT NULL, project_id TEXT REFERENCES projects(id), tags TEXT NOT NULL,
                        object_name TEXT NOT NULL UNIQUE, hash TEXT NOT NULL, bytes INTEGER NOT NULL,
                        created_at TEXT NOT NULL, version INTEGER NOT NULL, extraction_status TEXT NOT NULL,
                        preview TEXT NOT NULL, logical_key TEXT NOT NULL, deleted INTEGER NOT NULL DEFAULT 0);
                    CREATE INDEX IF NOT EXISTS ix_artifacts_identity ON artifacts(logical_key,hash,deleted);
                    CREATE INDEX IF NOT EXISTS ix_artifacts_project ON artifacts(project_id,deleted);
                    """);
                cmd.ExecuteNonQuery(); cancellationToken.ThrowIfCancellationRequested();
            }, cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally { initializeGate.Release(); }
    }

    public Task<IReadOnlyList<ArtifactRecord>> ListAsync(string? search = null, string? projectId = null, string? kind = null, CancellationToken cancellationToken = default) => DbAsync<IReadOnlyList<ArtifactRecord>>(db =>
    {
        using var cmd = Command(db, """
            SELECT id,title,original_name,kind,source,project_id,tags,object_name,hash,bytes,
                created_at,version,extraction_status,substr(preview,1,2048) AS list_preview
            FROM artifacts WHERE deleted=0 AND ($project IS NULL OR project_id=$project)
            AND ($kind IS NULL OR kind=$kind) AND ($search IS NULL OR
            instr(lower(title || char(10) || original_name || char(10) || tags || char(10) || source || char(10) || artifacts.preview),lower($search))>0)
            ORDER BY created_at DESC,id;
            """, ("$project", projectId), ("$kind", kind), ("$search", search));
        using var reader = cmd.ExecuteReader(); var result = new List<ArtifactRecord>();
        while (reader.Read()) { cancellationToken.ThrowIfCancellationRequested(); result.Add(ReadArtifact(reader)); }
        return result;
    }, cancellationToken);

    public Task<ArtifactRecord?> GetAsync(string id, CancellationToken cancellationToken = default) => DbAsync(db => Find(db, "id=$id AND deleted=0", ("$id", id)), cancellationToken);

    public Task<IReadOnlyList<ProjectRecord>> ProjectsAsync(CancellationToken cancellationToken = default) => DbAsync<IReadOnlyList<ProjectRecord>>(db =>
    {
        using var cmd = Command(db, "SELECT * FROM projects ORDER BY created_at,id"); using var r = cmd.ExecuteReader(); var result = new List<ProjectRecord>();
        while (r.Read()) { cancellationToken.ThrowIfCancellationRequested(); result.Add(new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), ParseDate(r.GetString(3)))); }
        return result;
    }, cancellationToken);

    public Task<ProjectRecord> CreateProjectAsync(string name, string? rootPath = null, CancellationToken cancellationToken = default)
    {
        Metadata(name, 512, nameof(name), required: true);
        var root = rootPath is null ? null : ArchiveSafety.LocalPath(rootPath);
        if (root is not null) ArchiveSafety.NoReparse(root);
        var project = new ProjectRecord(Guid.NewGuid().ToString("N"), name.Trim(), root, DateTimeOffset.UtcNow);
        return DbAsync(db =>
        {
            using var cmd = Command(db, "INSERT INTO projects VALUES($id,$name,$root,$date)", ("$id", project.Id), ("$name", project.Name), ("$root", project.RootPath), ("$date", FormatDate(project.CreatedAt)));
            cancellationToken.ThrowIfCancellationRequested(); cmd.ExecuteNonQuery(); return project;
        }, cancellationToken);
    }

    public async Task<ImportResult> ImportFileAsync(string path, string? projectId = null, string source = "用户", string tags = "", CancellationToken cancellationToken = default)
    {
        var full = SourcePath(path); var name = Path.GetFileName(full); ArchiveSafety.Name(name); ValidateMetadata(source, tags);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await copyGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            temporary = NewTemporary();
            await using (var input = ArchiveSafety.OpenRead(full))
            {
                if (input.Length > ArchiveSafety.MaxFileBytes) throw new IOException("单个文件上限为 256 MiB。");
                await using var output = CreateTemporary(temporary);
                await ArchiveSafety.CopyAsync(input, output, ArchiveSafety.MaxFileBytes, cancellationToken).ConfigureAwait(false);
                await FlushAsync(output, cancellationToken).ConfigureAwait(false);
            }
            var extraction = await TextExtraction.ExtractAsync(temporary, name, cancellationToken).ConfigureAwait(false);
            return await PublishAsync(temporary, name, projectId, source.Trim(), tags, extraction, [], cancellationToken).ConfigureAwait(false);
        }
        finally { DeleteTemporary(temporary); copyGate.Release(); }
    }

    public async Task<ImportResult> SaveGeneratedAsync(string name, string content, string? projectId = null, CancellationToken cancellationToken = default)
    {
        ArchiveSafety.Name(name); ArgumentNullException.ThrowIfNull(content);
        if (Encoding.UTF8.GetByteCount(content) > ArchiveSafety.MaxFileBytes) throw new IOException("生成内容超过 256 MiB 上限。");
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await copyGate.WaitAsync(cancellationToken).ConfigureAwait(false); string? temporary = null;
        try
        {
            temporary = NewTemporary();
            await using (var output = CreateTemporary(temporary))
            {
                // StreamWriter avoids allocating a second, whole-content UTF-8 byte array.
                await using (var writer = new StreamWriter(output, new UTF8Encoding(false), 64 * 1024, leaveOpen: true))
                { await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false); await writer.FlushAsync(cancellationToken).ConfigureAwait(false); }
                await FlushAsync(output, cancellationToken).ConfigureAwait(false);
            }
            var extraction = await TextExtraction.ExtractAsync(temporary, name, cancellationToken).ConfigureAwait(false);
            return await PublishAsync(temporary, name, projectId, "应用", "", extraction, [], cancellationToken).ConfigureAwait(false);
        }
        finally { DeleteTemporary(temporary); copyGate.Release(); }
    }

    public async Task<ImportResult> ImportRepositoryAsync(string path, string? projectId = null, string source = "用户", CancellationToken cancellationToken = default)
    {
        var full = SourcePath(path); ValidateMetadata(source, ""); ArchiveSafety.NoReparse(full);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        var name = new DirectoryInfo(full).Name + ".zip"; ArchiveSafety.Name(name);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await copyGate.WaitAsync(cancellationToken).ConfigureAwait(false); string? temporary = null;
        try
        {
            temporary = NewTemporary();
            var snapshot = await RepositorySnapshot.CreateAsync(full, DataRoot, temporary, cancellationToken).ConfigureAwait(false);
            return await PublishAsync(temporary, name, projectId, source.Trim(), "", new("代码库", "已生成快照清单", snapshot.Preview), snapshot.Warnings, cancellationToken).ConfigureAwait(false);
        }
        finally { DeleteTemporary(temporary); copyGate.Release(); }
    }

    public Task UpdateArtifactAsync(string id, string title, string tags, string? projectId, CancellationToken cancellationToken = default)
    {
        Metadata(title, 1024, nameof(title), required: true); Metadata(tags, 4096, nameof(tags));
        return DbAsync(db =>
        {
            using var tx = db.BeginTransaction(deferred: false); ProjectExists(db, projectId, tx);
            var current = Find(db, "id=$id AND deleted=0", tx, ("$id", id)) ?? throw new KeyNotFoundException("作品不存在。");
            var key = LogicalKey(current.OriginalName, projectId, current.Source);
            // Project metadata changes also move deduplication scope. Reserve the historical version in
            // the destination lineage, but never renumber an immutable historical record.
            using var reserve = Command(db, "INSERT INTO series VALUES($key,$version) ON CONFLICT(logical_key) DO UPDATE SET last_version=MAX(last_version,excluded.last_version)", ("$key", key), ("$version", current.Version)); reserve.Transaction = tx;
            reserve.ExecuteNonQuery();
            using var cmd = Command(db, "UPDATE artifacts SET title=$title,tags=$tags,project_id=$project,logical_key=$key WHERE id=$id AND deleted=0", ("$title", title.Trim()), ("$tags", tags), ("$project", projectId), ("$key", key), ("$id", id)); cmd.Transaction = tx;
            cancellationToken.ThrowIfCancellationRequested();
            if (cmd.ExecuteNonQuery() != 1) throw new KeyNotFoundException("作品不存在。");
            tx.Commit(); return true;
        }, cancellationToken);
    }

    public Task RemoveArtifactAsync(string id, CancellationToken cancellationToken = default) => DbAsync(db =>
    {
        // Persistent tombstone: no source deletion, no automatic revival, and version counters survive removal.
        // Owned object reclamation is deliberately deferred, avoiding races with readers and shared consumers.
        using var cmd = Command(db, "UPDATE artifacts SET deleted=1,preview='',tags='' WHERE id=$id", ("$id", id));
        cancellationToken.ThrowIfCancellationRequested(); cmd.ExecuteNonQuery(); return true;
    }, cancellationToken);

    public async Task<string> GetOriginalPathAsync(string id, CancellationToken cancellationToken = default)
    {
        var artifact = await GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("作品不存在或已删除。");
        ValidateObject(artifact); cancellationToken.ThrowIfCancellationRequested(); return artifact.ObjectPath;
    }

    private async Task<ImportResult> PublishAsync(string temporary, string name, string? projectId, string source, string tags, Extraction extraction, IReadOnlyList<string> warnings, CancellationToken token)
    {
        string hash; long bytes;
        await using (var stream = ArchiveSafety.OpenRead(temporary))
        { bytes = stream.Length; hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant(); }
        var key = LogicalKey(name, projectId, source);
        return await DbAsync(db =>
        {
            using var tx = db.BeginTransaction(deferred: false);
            ProjectExists(db, projectId, tx);
            var existing = Find(db, "logical_key=$key AND hash=$hash AND deleted=0", tx, ("$key", key), ("$hash", hash));
            if (existing is not null) { ValidateObject(existing); return new ImportResult(existing, true, warnings); }
            using var versionCommand = Command(db, "INSERT INTO series VALUES($key,1) ON CONFLICT(logical_key) DO UPDATE SET last_version=last_version+1 RETURNING last_version", ("$key", key)); versionCommand.Transaction = tx;
            var version = Convert.ToInt32(versionCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
            var id = Guid.NewGuid().ToString("N"); var extension = Path.GetExtension(name);
            if (extension.Length > 24) extension = ".bin";
            var objectName = id + extension; var objectPath = Path.Combine(objects, objectName);
            var artifact = new ArtifactRecord(id, name, name, extraction.Kind, source, projectId, tags, objectPath, hash, bytes, DateTimeOffset.UtcNow, version, extraction.Status, extraction.Preview);
            token.ThrowIfCancellationRequested(); CheckStorage();
            File.Move(temporary, objectPath, overwrite: false);
            // Flush happened before rename. An interrupted DB commit can leave an unreferenced immutable object, never an acknowledged partial file.
            File.SetAttributes(objectPath, FileAttributes.ReadOnly);
            using var insert = Command(db, """
                INSERT INTO artifacts(id,title,original_name,kind,source,project_id,tags,object_name,hash,bytes,created_at,version,extraction_status,preview,logical_key)
                VALUES($id,$title,$name,$kind,$source,$project,$tags,$object,$hash,$bytes,$date,$version,$status,$preview,$key)
                """, ("$id", id), ("$title", artifact.Title), ("$name", name), ("$kind", extraction.Kind), ("$source", source), ("$project", projectId), ("$tags", tags), ("$object", objectName), ("$hash", hash), ("$bytes", bytes), ("$date", FormatDate(artifact.CreatedAt)), ("$version", version), ("$status", extraction.Status), ("$preview", extraction.Preview), ("$key", key));
            insert.Transaction = tx; insert.ExecuteNonQuery(); token.ThrowIfCancellationRequested(); tx.Commit();
            return new ImportResult(artifact, false, warnings);
        }, token).ConfigureAwait(false);
    }

    private async Task<T> DbAsync<T>(Func<SqliteConnection, T> action, CancellationToken token)
    {
        await InitializeAsync(token).ConfigureAwait(false);
        // Microsoft.Data.Sqlite operations are synchronous; move locking/IO off the UI thread.
        return await Task.Run(() => { token.ThrowIfCancellationRequested(); CheckStorage(); using var db = Open(); return action(db); }, token).ConfigureAwait(false);
    }
    private SqliteConnection Open()
    {
        var db = new SqliteConnection(connectionString);
        try
        {
            db.Open(); using var cmd = db.CreateCommand();
            cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000; PRAGMA synchronous=FULL;"; cmd.ExecuteNonQuery(); return db;
        }
        catch { db.Dispose(); throw; }
    }
    private void CheckStorage()
    {
        ArchiveSafety.NoReparse(DataRoot); ArchiveSafety.NoReparse(objects);
        ArchiveSafety.NoReparse(Path.Combine(DataRoot, "portfolio.db"));
        ArchiveSafety.NoReparse(Path.Combine(DataRoot, "portfolio.db-wal"));
        ArchiveSafety.NoReparse(Path.Combine(DataRoot, "portfolio.db-shm"));
    }
    private string SourcePath(string path)
    {
        var full = ArchiveSafety.LocalPath(path);
        if (ArchiveSafety.Within(full, DataRoot)) throw new IOException("不能重新导入作品仓库内部文件或目录，避免归档循环。");
        return full;
    }
    private string NewTemporary() { CheckStorage(); return Path.Combine(objects, Guid.NewGuid().ToString("N") + ".partial"); }
    private static FileStream CreateTemporary(string path) => new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    private static async Task FlushAsync(FileStream stream, CancellationToken token)
    { await stream.FlushAsync(token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); stream.Flush(flushToDisk: true); }
    private static void DeleteTemporary(string? path) { if (path is not null && File.Exists(path)) File.Delete(path); }
    private static void Metadata(string value, int max, string parameter, bool required = false)
    { if (value is null || value.Length > max || value.Contains('\0') || (required && string.IsNullOrWhiteSpace(value))) throw new ArgumentException("元数据为空或超过长度上限。", parameter); }
    private static void ValidateMetadata(string source, string tags) { Metadata(source, 128, nameof(source), true); Metadata(tags, 4096, nameof(tags)); }
    private static string FormatDate(DateTimeOffset date) => date.ToString("O", CultureInfo.InvariantCulture);
    private static string LogicalKey(string name, string? projectId, string source) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { name.ToUpperInvariant(), projectId, source }))));
    private static DateTimeOffset ParseDate(string date) => DateTimeOffset.Parse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static SqliteCommand Command(SqliteConnection db, string sql, params (string Name, object? Value)[] parameters)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in parameters) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    private static void ProjectExists(SqliteConnection db, string? id, SqliteTransaction tx)
    {
        if (id is null) return;
        using var cmd = Command(db, "SELECT 1 FROM projects WHERE id=$id", ("$id", id)); cmd.Transaction = tx;
        if (cmd.ExecuteScalar() is null) throw new ArgumentException("项目不存在。", nameof(id));
    }
    private ArtifactRecord? Find(SqliteConnection db, string predicate, params (string Name, object? Value)[] parameters) => Find(db, predicate, null, parameters);
    private ArtifactRecord? Find(SqliteConnection db, string predicate, SqliteTransaction? tx, params (string Name, object? Value)[] parameters)
    { using var cmd = Command(db, "SELECT * FROM artifacts WHERE " + predicate + " LIMIT 1", parameters); cmd.Transaction = tx; using var r = cmd.ExecuteReader(); return r.Read() ? ReadArtifact(r) : null; }
    private ArtifactRecord ReadArtifact(SqliteDataReader r)
    {
        var name = r.GetString(7); ArchiveSafety.Name(name);
        return new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), Path.Combine(objects, name), r.GetString(8), r.GetInt64(9), ParseDate(r.GetString(10)), r.GetInt32(11), r.GetString(12), r.GetString(13));
    }
    private void ValidateObject(ArtifactRecord artifact)
    {
        if (!ArchiveSafety.Within(artifact.ObjectPath, objects)) throw new IOException("无效的归档路径。");
        using var stream = ArchiveSafety.OpenRead(artifact.ObjectPath);
        if (stream.Length != artifact.Bytes) throw new IOException("归档文件大小已发生外部变化。");
    }
}
