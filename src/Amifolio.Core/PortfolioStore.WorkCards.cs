namespace Amifolio.Core;

public sealed record CodexWorkCard(CodexWork Work, string Excerpt, DateTimeOffset? ImageModifiedAt)
{
    public bool HasImage => ImageModifiedAt is not null;
}

public sealed partial class PortfolioStore
{
    private const int WorkCardExcerptLimit = 480;

    public Task<IReadOnlyList<CodexWorkCard>> ListCodexWorkCardsAsync(string projectId, string search, int limit,
        CancellationToken ct) => DbAsync<IReadOnlyList<CodexWorkCard>>(db =>
    {
        var project = CodexProjects.FirstOrDefault(p => p.Id == projectId);
        if (project is null || !HasWorks(db)) return [];

        using var command = Command(db, """
            SELECT a.*, w.id, w.session, w.turn, w.kind, w.title, w.reference_id, w.recorded
            FROM codex_works w
            LEFT JOIN artifacts a ON a.id=w.reference_id AND a.project_id=w.project AND a.deleted=0
                AND a.object_name LIKE 'ref:%'
            WHERE w.project=$project AND instr(lower(w.title),lower($search))>0
            ORDER BY w.recorded DESC, w.title, w.id LIMIT $limit
            """, ("$project", projectId), ("$search", search.Trim()), ("$limit", Math.Clamp(limit, 0, 120)));
        using var rows = command.ExecuteReader();
        var cards = new List<CodexWorkCard>();
        var offset = rows.FieldCount - 7;
        while (rows.Read())
        {
            ct.ThrowIfCancellationRequested();
            var reference = rows.IsDBNull(offset + 5) ? null : rows.GetString(offset + 5);
            var artifact = ReadWorkCardReference(rows);
            var title = rows.GetString(offset + 4);
            var availability = reference is null ? "仅来源会话" : "引用不可用";
            var excerpt = "";
            DateTimeOffset? imageModifiedAt = null;
            if (artifact is { IsReference: true } && ReferenceMetadataInScope(project, artifact.ObjectPath))
            {
                // Validate freshness using the recorded file's metadata only. No folder enumeration or original text reads.
                var live = LiveReference(artifact);
                availability = live.ExtractionStatus;
                excerpt = WorkCardExcerpt(live.Preview);
                if (artifact.Bytes is > 0 and <= CodexCatalog.MaxReadBytes &&
                    IsWorkCardImage(title) && IsWorkCardImage(artifact.OriginalName) && IsWorkCardImage(artifact.ObjectPath) &&
                    WorkCardOriginalIsCurrent(live))
                    imageModifiedAt = artifact.CreatedAt;
            }

            var work = new CodexWork(rows.GetString(offset), projectId, rows.GetString(offset + 1),
                rows.GetString(offset + 2), rows.GetString(offset + 3), title, reference,
                DateTimeOffset.FromUnixTimeMilliseconds(rows.GetInt64(offset + 6)), availability);
            cards.Add(new(work, excerpt, imageModifiedAt));
        }
        return cards;
    }, ct);

    public async Task<Stream?> OpenCodexWorkImageAsync(CodexWork work, DateTimeOffset? expectedModifiedAt, CancellationToken ct)
    {
        if (!IsWorkCardImage(work.Title)) return null;
        var candidate = await FindCodexWorkReferenceAsync(work, ct).ConfigureAwait(false);
        var project = CodexProjects.FirstOrDefault(p => p.Id == work.ProjectId);
        if (candidate is not { IsReference: true, Bytes: > 0 and <= CodexCatalog.MaxReadBytes } || project is null ||
            candidate.CreatedAt != expectedModifiedAt ||
            !IsWorkCardImage(candidate.OriginalName) || !IsWorkCardImage(candidate.ObjectPath) ||
            !ReferenceMetadataInScope(project, candidate.ObjectPath))
            return null;

        try
        {
            var path = await Catalog.ValidateFileAsync(project, candidate.ObjectPath, ct).ConfigureAwait(false);
            if (!WorkCardOriginalIsCurrent(LiveReference(candidate))) return null;
            var stream = ArchiveSafety.OpenRead(path);
            try
            {
                // Check the opened handle's real path in OpenRead and compare its revision before decoding.
                if (stream.Length == candidate.Bytes &&
                    File.GetLastWriteTimeUtc(stream.SafeFileHandle) == candidate.CreatedAt.UtcDateTime)
                {
                    ct.ThrowIfCancellationRequested();
                    return stream;
                }
            }
            catch
            {
                stream.Dispose();
                throw;
            }
            stream.Dispose();
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public async Task<ArtifactRecord?> PreviewCodexWorkReferenceAsync(CodexWork work, CancellationToken ct)
    {
        var reference = await FindCodexWorkReferenceAsync(work, ct).ConfigureAwait(false);
        var project = CodexProjects.FirstOrDefault(p => p.Id == work.ProjectId);
        if (reference is not { IsReference: true } || project is null ||
            !ReferenceMetadataInScope(project, reference.ObjectPath)) return null;

        FileStream guard;
        try
        {
            var path = await Catalog.ValidateFileAsync(project, reference.ObjectPath, ct).ConfigureAwait(false);
            guard = ArchiveSafety.OpenRead(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        using (guard)
        {
            if (reference.Kind == "图片") return reference with { Preview = "" };
            // The parser verifies the full file hash. Hold a read-only handle across
            // that work so the size checked here remains valid on Windows.
            if (guard.Length > CodexCatalog.MaxReadBytes)
                return reference with
                {
                    Preview = "",
                    ExtractionStatus = $"文件超过 {CodexCatalog.MaxReadBytes / (1024 * 1024)} MiB 预览上限"
                };

            var live = await GetAsync(reference.Id, ct).ConfigureAwait(false);
            if (live is null || live.ExtractionStatus is "源文件缺失" or "源文件不可读") return null;
            if (live.ExtractionStatus is "未解析" or "已取消" or "提取失败" ||
                live.ExtractionStatus.StartsWith("源文件已变化", StringComparison.Ordinal))
            {
                await ParseReferenceAsync(reference.Id, ct).ConfigureAwait(false);
                live = await GetAsync(reference.Id, ct).ConfigureAwait(false);
            }
            return live is { ExtractionStatus: not ("源文件缺失" or "源文件不可读") } ? live : null;
        }
    }

    private Task<ArtifactRecord?> FindCodexWorkReferenceAsync(CodexWork work, CancellationToken ct)
    {
        if (work.ReferenceId is not { } reference) return Task.FromResult<ArtifactRecord?>(null);
        return DbAsync(db =>
        {
            if (!HasWorks(db)) return null;
            using var command = Command(db, """
                SELECT a.* FROM codex_works w JOIN artifacts a ON a.id=w.reference_id AND a.project_id=w.project
                WHERE w.id=$id AND w.project=$project AND w.reference_id=$reference AND w.title=$title
                    AND a.deleted=0 AND a.object_name LIKE 'ref:%'
                """, ("$id", work.Id), ("$project", work.ProjectId), ("$reference", reference), ("$title", work.Title));
            using var row = command.ExecuteReader();
            return row.Read() ? ReadWorkCardReference(row) : null;
        }, ct);
    }

    private ArtifactRecord? ReadWorkCardReference(Microsoft.Data.Sqlite.SqliteDataReader row)
    {
        if (row.IsDBNull(0)) return null;
        try { return ReadArtifact(row); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool WorkCardOriginalIsCurrent(ArtifactRecord artifact) =>
        artifact.ExtractionStatus is not ("源文件缺失" or "源文件不可读") &&
        !artifact.ExtractionStatus.StartsWith("源文件已变化", StringComparison.Ordinal);

    private static bool IsWorkCardImage(string name) =>
        Path.GetExtension(name).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp";

    private static string WorkCardExcerpt(string preview)
    {
        var text = preview.AsSpan().Trim();
        if (text.Length <= WorkCardExcerptLimit) return text.ToString();
        var length = WorkCardExcerptLimit - 1;
        if (char.IsHighSurrogate(text[length - 1])) length--;
        return text[..length].TrimEnd().ToString() + "…";
    }
}
