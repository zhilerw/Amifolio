using System.Security.Cryptography;
using System.Text;

namespace Amifolio.Core;

public sealed record CodexWork(string Id, string ProjectId, string SessionId, string TurnId, string Kind,
    string Title, string? ReferenceId, DateTimeOffset RecordedAt, string Availability)
{
    public string SourceLabel => Kind == "upload" ? "你上传" : "Codex 交付";
}

public sealed record CodexWorkSync(bool Enabled, string State);

public partial class PortfolioStore
{
    private const string WorkSchema = """
        CREATE TABLE IF NOT EXISTS codex_work_sync(project TEXT PRIMARY KEY, enabled INTEGER NOT NULL, state TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS codex_works(id TEXT PRIMARY KEY, project TEXT NOT NULL, session TEXT NOT NULL,
            turn TEXT NOT NULL, kind TEXT NOT NULL, title TEXT NOT NULL, reference_id TEXT, recorded INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_codex_works_project ON codex_works(project,recorded);
        """;

    public Task<CodexWorkSync> WorkSyncAsync(string projectId, CancellationToken ct = default) =>
        DbAsync(db =>
        {
            if (!HasWorks(db)) return new CodexWorkSync(false, "off");
            using var c = Command(db, "SELECT enabled,state FROM codex_work_sync WHERE project=$project", ("$project", projectId));
            using var row = c.ExecuteReader();
            return row.Read() ? new(row.GetBoolean(0), row.GetString(1)) : new(false, "off");
        }, ct);

    public Task SetWorkSyncAsync(string projectId, bool enabled, CancellationToken ct = default)
    {
        if (!ReadOnlySources || !CodexProjects.Any(p => p.Id == projectId))
            throw new InvalidOperationException("请选择已发现的 Codex 项目。");
        return WriteAsync(db =>
        {
            using var tx = db.BeginTransaction(deferred: false);
            LearningSchema.Exec(db, tx, WorkSchema);
            LearningSchema.Exec(db, tx, """
                INSERT INTO codex_work_sync(project,enabled,state) VALUES($project,$on,$state)
                ON CONFLICT(project) DO UPDATE SET enabled=$on,state=$state
                """, ("$project",projectId), ("$on",enabled), ("$state",enabled ? "waiting" : "off"));
            tx.Commit();
            return true;
        }, ct);
    }

    public async Task<int> RecordCodexWorksAsync(CodexProject project, string sessionId, string turnId,
        IReadOnlyList<CodexWorkSource>? works, CancellationToken ct = default)
    {
        if (!ReadOnlySources || !Guid.TryParseExact(sessionId, "D", out _) || !CodexProjects.Any(p => p.Id == project.Id) ||
            !(await WorkSyncAsync(project.Id, ct).ConfigureAwait(false)).Enabled) return 0;
        var observed = new List<(string Id, string Kind, string Title, ReferenceMetadata? Reference)>();
        foreach (var work in (works ?? []).Take(24))
        {
            ct.ThrowIfCancellationRequested();
            var title = Path.GetFileName(work.Path);
            if (work.Kind is not ("upload" or "delivery") || RepositorySnapshot.IsSecret(title) ||
                MemoryPolicy.ContainsSensitiveText(title)) continue;
            ReferenceMetadata? reference = null;
            // Only vetted in-project items can become file references. Outside uploads are names + conversation evidence.
            if (ArchiveSafety.Within(work.Path, project.RootPath))
            {
                try
                {
                    reference = await InspectReferenceAsync(project, work.Path, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
            var pathKey = OperatingSystem.IsWindows() ? work.Path.ToUpperInvariant() : work.Path;
            var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
                project.Id + "\n" + sessionId + "\n" + turnId + "\n" + work.Kind + "\n" + pathKey)));
            observed.Add((key, work.Kind, title, reference));
        }

        return await WriteAsync(db =>
        {
            using var tx = db.BeginTransaction(deferred: false);
            if (Convert.ToInt32(LearningSchema.Scalar(db, tx,
                "SELECT enabled FROM codex_work_sync WHERE project=$project", ("$project", project.Id))) != 1) return 0;
            foreach (var item in observed)
            {
                if (item.Reference is not null) InsertReference(db, tx, project.Id, item.Reference);
                LearningSchema.Exec(db, tx, """
                    INSERT INTO codex_works(id,project,session,turn,kind,title,reference_id,recorded)
                    VALUES($id,$project,$session,$turn,$kind,$title,$reference,$recorded)
                    ON CONFLICT(id) DO UPDATE SET reference_id=COALESCE(excluded.reference_id,codex_works.reference_id)
                    """, ("$id", item.Id), ("$project", project.Id), ("$session", sessionId), ("$turn", turnId),
                    ("$kind", item.Kind), ("$title", item.Title), ("$reference", item.Reference?.Id),
                    ("$recorded", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
            }
            LearningSchema.Exec(db, tx, "UPDATE codex_work_sync SET state=$state WHERE project=$project",
                ("$state",works is null ? "unsupported" : "ready"), ("$project",project.Id));
            tx.Commit();
            return observed.Count;
        }, ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<CodexWork>> ListCodexWorksAsync(string projectId, string search = "", CancellationToken ct = default) =>
        DbAsync<IReadOnlyList<CodexWork>>(db =>
        {
            if (!HasWorks(db)) return [];
            using var c = Command(db, """
                SELECT a.*,w.id,w.project,w.session,w.turn,w.kind,w.title,w.reference_id,w.recorded
                FROM codex_works w LEFT JOIN artifacts a ON a.id=w.reference_id AND a.deleted=0
                WHERE w.project=$project AND instr(lower(w.title),lower($search))>0
                ORDER BY w.recorded DESC,w.title,w.id LIMIT 120
                """, ("$project",projectId), ("$search",search.Trim()));
            using var rows = c.ExecuteReader();
            var result = new List<CodexWork>();
            var references = new Dictionary<string, ArtifactRecord>(StringComparer.Ordinal);
            var workOffset = rows.FieldCount - 8;
            while (rows.Read())
            {
                var reference = rows.IsDBNull(workOffset + 6) ? null : rows.GetString(workOffset + 6);
                if (!rows.IsDBNull(0))
                {
                    var item = ReadArtifact(rows);
                    if (item.IsReference && item.ProjectId == projectId) references.TryAdd(item.Id, item);
                }
                result.Add(new(rows.GetString(workOffset), rows.GetString(workOffset + 1), rows.GetString(workOffset + 2),
                    rows.GetString(workOffset + 3), rows.GetString(workOffset + 4), rows.GetString(workOffset + 5),
                    reference, DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(workOffset + 7)),
                    reference is null ? "仅来源会话" : "引用不可用"));
            }
            var live = LiveReferences(references.Values.ToArray(), new ReferenceListReads(), ct).ToDictionary(a => a.Id);
            return result.Select(work => work.ReferenceId is { } id && live.TryGetValue(id, out var item)
                ? work with { Availability = item.ExtractionStatus } : work).ToArray();
        }, ct);

    private static bool HasWorks(Microsoft.Data.Sqlite.SqliteConnection db) =>
        LearningSchema.Scalar(db, null, "SELECT 1 FROM sqlite_master WHERE type='table' AND name='codex_work_sync'") is not null;
}
