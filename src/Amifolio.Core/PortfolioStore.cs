using System.Globalization;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Amifolio.Core;

/// <summary>Local durable archive. A committed row always refers to an already flushed, independently owned object.</summary>
public sealed partial class PortfolioStore : IPortfolioStore
{
    public string DataRoot { get; }
    private readonly string objects;
    private readonly string connectionString;
    private readonly SemaphoreSlim initializeGate = new(1, 1);
    // Bound expensive work within one store; independent bridge/UI processes coordinate commits through SQLite.
    private readonly SemaphoreSlim copyGate = new(2, 2);
    private bool initialized;
    private readonly SemaphoreSlim queryMetadataGate=new(1,1);
    private QueryMetadataSet? queryMetadata;
    internal long QueryMetadataCacheBudget=64L*1024*1024;
    private sealed record QueryMetadataSet(string Stamp,ArtifactRecord[] Rows,Dictionary<string,int> Ordinals,Dictionary<int,string> RawPaths,long Bytes);
    // Internal opt-in diagnostics: no paths/content and no public behavior switch.
    internal Action<ListPerformanceSample>? ListPerformanceObserver { get; set; }
    internal sealed record ListPerformanceSample(double MetadataMs, double FileSystemMs, int MetadataRows,
        int ReturnedRows, int IndividualFileReads, int EnumeratedDirectories, int PinnedDirectories)
    {
        public int ValidatedVolumes { get; init; }
        public double SqlExecuteMs { get; init; }
        public double MaterializationMs { get; init; }
        public double PathNormalizationMs { get; init; }
        public bool MetadataCacheHit { get; init; }
        public long MetadataCacheBytes { get; init; }
    }

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
            await using var maintenance = await StorageLease.ExclusiveAsync(DataRoot, cancellationToken).ConfigureAwait(false);
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested(); CheckStorage();
                if (!File.Exists(Path.Combine(DataRoot, "portfolio.db")) &&
                    ((Directory.Exists(objects) && Directory.EnumerateFiles(objects).Any()) || Directory.Exists(Path.Combine(DataRoot, "context-state"))))
                    throw new IOException("作品数据库缺失，请从完整备份恢复；未创建空库。");
                Directory.CreateDirectory(DataRoot);
                if(!ReadOnlySources&&!File.Exists(Path.Combine(DataRoot,"storage-layout.json"))&&!File.Exists(Path.Combine(DataRoot,"session-learning.marker")))Directory.CreateDirectory(objects); CheckStorage();
                var existingLibrary=File.Exists(Path.Combine(DataRoot,"portfolio.db"));
                using var db = Open();
                StoreSchema.Ensure(db, DataRoot, existingLibrary); cancellationToken.ThrowIfCancellationRequested();
            }, cancellationToken).ConfigureAwait(false);
            initialized = true;
        }
        finally { initializeGate.Release(); }
    }

    public async Task<IReadOnlyList<ArtifactRecord>> ListAsync(string? search = null, string? projectId = null, string? kind = null, CancellationToken cancellationToken = default)
    {
        if(ReadOnlySources&&codexProjects is null)await RefreshCodexProjectsAsync(cancellationToken).ConfigureAwait(false);
        var observer=ListPerformanceObserver;
        var started=observer is null?0:Stopwatch.GetTimestamp();
        var referencePaths=new ListReferencePaths{MeasureTimings=observer is not null};
        double sqlExecuteMs=0,materializationMs=0;
        var cacheHit=false;long cacheBytes=0;
        IReadOnlyList<ArtifactRecord> result;
        await queryMetadataGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
        result=await DbAsync<IReadOnlyList<ArtifactRecord>>(db =>
    {
        // The revision read, candidate search and any refill share a single WAL read
        // snapshot, including writers which do not participate in StorageLease.
        using var transaction=db.BeginTransaction(deferred:true);
        using var revision=Command(db,"""
            SELECT a.value||':'||b.value FROM store_meta a CROSS JOIN store_meta b
            WHERE a.key='query_database_id' AND b.key='query_revision'
            """);revision.Transaction=transaction;
        var stamp=revision.ExecuteScalar() as string;
        var budget=Math.Clamp(QueryMetadataCacheBudget,0,64L*1024*1024);
        if(queryMetadata is{} prior&&(prior.Stamp!=stamp||prior.Bytes>budget))queryMetadata=null;
        StorageEvents.Log.Checkpoint("query-metadata-snapshot");
        if(queryMetadata is{} cached)
        {
            cacheHit=true;cacheBytes=cached.Bytes;
            var cacheClock=observer is null?0:Stopwatch.GetTimestamp();
            var indices=ReadCachedQueryIndices(db,transaction,cached,search,projectId,kind,cancellationToken);
            if(observer is not null)sqlExecuteMs=Stopwatch.GetElapsedTime(cacheClock).TotalMilliseconds;
            cacheClock=observer is null?0:Stopwatch.GetTimestamp();
            var selected=new ArtifactRecord[indices.Length];
            for(var i=0;i<indices.Length;i++)
            {
                cancellationToken.ThrowIfCancellationRequested();var ordinal=indices[i];var item=cached.Rows[ordinal];
                if(item.IsReference)
                {
                    var raw=cached.RawPaths.GetValueOrDefault(ordinal)??item.ObjectPath;
                    var current=referencePaths.Normalize(raw);
                    if(!current.Equals(item.ObjectPath,StringComparison.Ordinal))item=item with{ObjectPath=current};
                }
                selected[i]=item;
            }
            if(observer is not null)materializationMs=Stopwatch.GetElapsedTime(cacheClock).TotalMilliseconds;
            return selected;
        }
        var match=QueryMatch(search);
        // CROSS JOIN is intentional: candidate production must precede primary-key
        // artifact lookups. An IN subquery let ORDER BY select a full artifact scan.
        var from=match is null?"artifacts":"artifact_query_fts CROSS JOIN artifact_query_docs d CROSS JOIN artifacts";
        var candidates=match is null?"":" AND artifact_query_fts MATCH $match AND d.docid=artifact_query_fts.rowid AND artifacts.id=d.artifact_id";
        using var cmd = Command(db, """
            SELECT id,title,original_name,kind,source,project_id,tags,object_name,hash,bytes,
                created_at,version,extraction_status,substr(preview,1,2048) AS list_preview
            FROM
            """+" "+from+" WHERE deleted=0 AND ($project IS NULL OR project_id=$project) "+"""
            AND ($kind IS NULL OR kind=$kind) AND ($search IS NULL OR
            instr(lower(title || char(10) || original_name || char(10) || tags || char(10) || source || char(10) || artifacts.preview),lower($search))>0)
            """+candidates+" ORDER BY created_at DESC,id;", ("$project", projectId), ("$kind", kind), ("$search", search));
        if(match is not null)cmd.Parameters.AddWithValue("$match",match);
        cmd.Transaction=transaction;
        // A cold selective search or directory browse must never materialize the whole
        // library just to fill a cache. Only an already-full list can populate this slot.
        var retain=stamp is not null&&budget>0&&string.IsNullOrEmpty(search)&&projectId is null&&kind is null;
        var rawPaths=new Dictionary<int,string>();long estimated=0;
        var clock=observer is null?0:Stopwatch.GetTimestamp();
        using var reader = cmd.ExecuteReader(); var result = new List<ArtifactRecord>();
        if(observer is not null)sqlExecuteMs=Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        clock=observer is null?0:Stopwatch.GetTimestamp();
        while (reader.Read())
        {
            cancellationToken.ThrowIfCancellationRequested();var item=ReadArtifactCore(reader,referencePaths);result.Add(item);
            if(retain)
            {
                estimated+=QueryMetadataBytes(item);
                if(item.IsReference)
                {
                    var raw=reader.GetString(7)[4..];
                    if(!raw.Equals(item.ObjectPath,StringComparison.Ordinal)){rawPaths[result.Count-1]=raw;estimated+=48L+raw.Length*2L;}
                }
                if(estimated>budget){retain=false;rawPaths.Clear();}
            }
        }
        if(retain)
        {
            var rows=result.ToArray();var ordinals=new Dictionary<string,int>(rows.Length,StringComparer.Ordinal);
            for(var i=0;i<rows.Length;i++)ordinals.Add(rows[i].Id,i);
            queryMetadata=new(stamp!,rows,ordinals,rawPaths,estimated);cacheBytes=estimated;
        }
        if(observer is not null)materializationMs=Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
        return result;
        }, cancellationToken).ConfigureAwait(false);
        }
        finally{queryMetadataGate.Release();}
        // A list may run under ContextService's shared publication lease. It must never
        // upgrade that lease to a writer; reconciliation runs before the caller takes it.
        var metadataEnd=observer is null?0:Stopwatch.GetTimestamp();
        var reads=new ReferenceListReads();
        var live=ReadOnlySources?LiveReferences(result,reads,cancellationToken):result;
        observer?.Invoke(new(Stopwatch.GetElapsedTime(started,metadataEnd).TotalMilliseconds,
            Stopwatch.GetElapsedTime(metadataEnd).TotalMilliseconds,result.Count,live.Count,reads.IndividualFiles,reads.Directories,reads.PinnedDirectories)
            {ValidatedVolumes=referencePaths.ValidatedVolumes,SqlExecuteMs=sqlExecuteMs,MaterializationMs=materializationMs,PathNormalizationMs=referencePaths.PathMilliseconds,
                MetadataCacheHit=cacheHit,MetadataCacheBytes=cacheBytes});
        return live;
    }

    private static long QueryMetadataBytes(ArtifactRecord item)=>1104L+2L*(
        item.Id.Length+item.Title.Length+item.OriginalName.Length+item.Kind.Length+item.Source.Length+(item.ProjectId?.Length??0)+
        item.Tags.Length+item.ObjectPath.Length+item.Hash.Length+item.ExtractionStatus.Length+item.Preview.Length);

    private static int[] ReadCachedQueryIndices(SqliteConnection db,SqliteTransaction transaction,QueryMetadataSet cached,
        string? search,string? projectId,string? kind,CancellationToken ct)
    {
        IEnumerable<int> candidates;
        if(string.IsNullOrEmpty(search))candidates=Enumerable.Range(0,cached.Rows.Length);
        else
        {
            var literal=search.IndexOf('\0')<0&&search.EnumerateRunes().Take(3).Count()==3;
            // The literal index is the exact historical normalized full content, with
            // case-sensitive Unicode trigrams: an escaped phrase is substring matching,
            // including punctuation/newlines and the entire preview. Short/NUL queries
            // retain the byte candidate index AND original SQLite instr verification.
            var table=literal?"artifact_query_literal":"artifact_query_fts";
            var match=literal?"\""+AsciiLower(search).Replace("\"","\"\"",StringComparison.Ordinal)+"\"":QueryMatch(search)!;
            var from=table+" CROSS JOIN artifact_query_docs d"+(literal?"":" CROSS JOIN artifacts");
            using var command=Command(db,"SELECT d.artifact_id FROM "+from+" WHERE "+table+" MATCH $match AND d.docid="+table+".rowid"+
                (literal?"":" AND artifacts.id=d.artifact_id AND artifacts.deleted=0 AND instr("+StoreSchema.QueryText()+",lower($search))>0"));
            command.Transaction=transaction;command.Parameters.AddWithValue("$match",match);
            if(!literal)command.Parameters.AddWithValue("$search",search);
            var indices=new HashSet<int>();using var reader=command.ExecuteReader();
            while(reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                if(!cached.Ordinals.TryGetValue(reader.GetString(0),out var index))throw new IOException("查询索引与同一快照的元数据不一致。");
                indices.Add(index);
            }
            candidates=indices.Order(); // Preserve SQLite's original created_at/id ordering.
        }
        var result=new List<int>();
        foreach(var index in candidates)
        {
            ct.ThrowIfCancellationRequested();var item=cached.Rows[index];
            if((projectId is null||item.ProjectId==projectId)&&(kind is null||item.Kind==kind))result.Add(index);
        }
        return result.ToArray();
    }

    private static string AsciiLower(string value)
    {
        var chars=value.ToCharArray();for(var i=0;i<chars.Length;i++)if(chars[i] is >= 'A' and <= 'Z')chars[i]=(char)(chars[i]+32);
        return new string(chars);
    }

    /// <summary>
    /// Complete immediate-file references for one discovered project directory. No paging or
    /// result limit; missing/changed references remain visible. Full search remains ListAsync.
    /// </summary>
    public async Task<IReadOnlyList<ArtifactRecord>> ListDirectoryAsync(string projectId,string directory,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(!ReadOnlySources)throw new NotSupportedException("目录查询仅用于只读Codex项目引用。");
        if(codexProjects is null)await RefreshCodexProjectsAsync(ct).ConfigureAwait(false);
        var observer=ListPerformanceObserver;
        var started=observer is null?0:Stopwatch.GetTimestamp();
        var project=CodexProjects.FirstOrDefault(p=>p.Id==projectId)??throw new IOException("项目不在当前只读发现范围内。");
        directory=ArchiveSafety.LocalPath(directory);
        if(!ReferenceMetadataInScope(project,Path.Combine(directory,"__query_scope__.md")))throw new IOException("目录越过项目范围或位于排除路径中。");
        ArchiveSafety.NoReparse(project.RootPath);ArchiveSafety.NoReparse(directory);
        var attributes=File.GetAttributes(directory);
        if(!attributes.HasFlag(FileAttributes.Directory)||(attributes&(FileAttributes.ReparsePoint|FileAttributes.Device))!=0)
            throw new IOException("查询目录必须是项目内可用的普通目录。");
        var parentKey="ref:"+Path.TrimEndingDirectorySeparator(directory).Replace('/','\\')+"\\";
        var paths=new ListReferencePaths{MeasureTimings=observer is not null};
        double sqlExecuteMs=0,materializationMs=0;
        var result=await DbAsync<IReadOnlyList<ArtifactRecord>>(db=>
        {
            // NOCASE is exactly ASCII case-folding. For cased non-ASCII paths, use a pure
            // lexical .NET comparison over the small directory metadata table, never preview.
            var unicodeCase=directory.EnumerateRunes().Any(r=>r.Value>127&&Rune.ToUpperInvariant(r)!=Rune.ToLowerInvariant(r));
            db.CreateFunction<string,bool>("amifolio_query_directory",raw=>
            {
                try{return raw.StartsWith("ref:",StringComparison.Ordinal)&&
                    Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw[4..].Replace('\\',Path.DirectorySeparatorChar)))
                        .Equals(directory,ArchiveSafety.PathComparison);}
                catch(Exception e)when(e is ArgumentException or IOException or NotSupportedException){return false;}
            });
            var scope=unicodeCase?"amifolio_query_directory(reference_directory)":"reference_directory=$parent COLLATE NOCASE OR instr(reference_directory,'\\.\\')>0 OR instr(reference_directory,'\\..\\')>0";
            using var command=Command(db,"""
                SELECT id,title,original_name,kind,source,project_id,tags,object_name,hash,bytes,
                    created_at,version,extraction_status,substr(preview,1,2048)
                FROM artifact_query_docs CROSS JOIN artifacts WHERE artifacts.id=artifact_query_docs.artifact_id
                AND deleted=0 AND project_id=$project AND (
                """+scope+") ORDER BY created_at DESC,id;",("$project",projectId));
            if(!unicodeCase)command.Parameters.AddWithValue("$parent",parentKey);
            var rows=new List<ArtifactRecord>();var clock=observer is null?0:Stopwatch.GetTimestamp();using var reader=command.ExecuteReader();
            if(observer is not null)sqlExecuteMs=Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
            clock=observer is null?0:Stopwatch.GetTimestamp();
            while(reader.Read())
            {
                ct.ThrowIfCancellationRequested();var item=ReadArtifactCore(reader,paths);
                if(item.IsReference&&string.Equals(Path.GetDirectoryName(item.ObjectPath),directory,ArchiveSafety.PathComparison))rows.Add(item);
            }
            if(observer is not null)materializationMs=Stopwatch.GetElapsedTime(clock).TotalMilliseconds;
            return rows;
        },ct).ConfigureAwait(false);
        var metadataEnd=observer is null?0:Stopwatch.GetTimestamp();var reads=new ReferenceListReads();
        var live=LiveReferences(result,reads,ct);
        observer?.Invoke(new(Stopwatch.GetElapsedTime(started,metadataEnd).TotalMilliseconds,Stopwatch.GetElapsedTime(metadataEnd).TotalMilliseconds,
            result.Count,live.Count,reads.IndividualFiles,reads.Directories,reads.PinnedDirectories)
            {ValidatedVolumes=paths.ValidatedVolumes,SqlExecuteMs=sqlExecuteMs,MaterializationMs=materializationMs,PathNormalizationMs=paths.PathMilliseconds});
        return live;
    }

    // UTF-8 hex lets native trigram FTS produce candidates for short Chinese/emoji too.
    // Recheck the original full-text predicate after this candidate filter: nibble-boundary
    // false positives and FTS tokenizer behavior can never become returned matches.
    private static string? QueryMatch(string? search)
    {
        if(string.IsNullOrEmpty(search))return null;
        var hex=Convert.ToHexString(Encoding.UTF8.GetBytes(AsciiLower(search)));
        if(hex.Length==2)return "("+string.Join(" OR ","0123456789ABCDEFZ".Select(c=>"\""+c+hex+"\""))+")";
        return "\""+hex+"\"";
    }

    public async Task<ArtifactRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if(ReadOnlySources&&codexProjects is null)await RefreshCodexProjectsAsync(cancellationToken).ConfigureAwait(false);
        var item=await DbAsync(db=>Find(db,"id=$id AND deleted=0",("$id",id)),cancellationToken).ConfigureAwait(false);
        if(ReadOnlySources&&item is not null&&(!item.IsReference&&item.Source!="应用"||item.IsReference&&!CodexProjects.Any(p=>p.Id==item.ProjectId)))return null;
        if(item is null||!item.IsReference)return item;
        var live=LiveReference(item,true);
        if(live.ExtractionStatus!=item.ExtractionStatus)await InvalidateReferenceAsync(live,cancellationToken).ConfigureAwait(false);
        return live;
    }

    public Task<IReadOnlyList<ProjectRecord>> ProjectsAsync(CancellationToken cancellationToken = default) => ReadOnlySources?ReferenceProjectsAsync(cancellationToken):DbAsync<IReadOnlyList<ProjectRecord>>(db =>
    {
        using var cmd = Command(db, "SELECT * FROM projects ORDER BY created_at,id"); using var r = cmd.ExecuteReader(); var result = new List<ProjectRecord>();
        while (r.Read()) { cancellationToken.ThrowIfCancellationRequested(); result.Add(new(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2), ParseDate(r.GetString(3)))); }
        return result;
    }, cancellationToken);

    public Task<ProjectRecord> CreateProjectAsync(string name, string? rootPath = null, CancellationToken cancellationToken = default)
    {
        RefuseManualWrite();
        Metadata(name, 512, nameof(name), required: true);
        var root = rootPath is null ? null : ArchiveSafety.LocalPath(rootPath);
        if (root is not null) ArchiveSafety.NoReparse(root);
        var project = new ProjectRecord(Guid.NewGuid().ToString("N"), name.Trim(), root, DateTimeOffset.UtcNow);
        return WriteAsync(db =>
        {
            using var cmd = Command(db, "INSERT INTO projects VALUES($id,$name,$root,$date)", ("$id", project.Id), ("$name", project.Name), ("$root", project.RootPath), ("$date", FormatDate(project.CreatedAt)));
            cancellationToken.ThrowIfCancellationRequested(); cmd.ExecuteNonQuery(); return project;
        }, cancellationToken);
    }

    public async Task<ImportResult> ImportFileAsync(string path, string? projectId = null, string source = "用户", string tags = "", CancellationToken cancellationToken = default)
    {
        RefuseManualWrite();
        var full = SourcePath(path); var name = Path.GetFileName(full); ArchiveSafety.Name(name); ValidateMetadata(source, tags);
        await using var session = await StorageLease.SessionAsync(DataRoot, cancellationToken).ConfigureAwait(false);
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
                StorageEvents.Log.Checkpoint("import-copy");
                await ArchiveSafety.CopyAsync(input, output, ArchiveSafety.MaxFileBytes, cancellationToken).ConfigureAwait(false);
                await FlushAsync(output, cancellationToken).ConfigureAwait(false);
            }
            var archived = await PublishAsync(temporary, name, projectId, source.Trim(), tags, new(TextExtraction.ClassifyName(name),"待提取",""), [], cancellationToken,queueExtraction:true,originPath:full).ConfigureAwait(false);
            return await AnalyzeArchivedAsync(archived,cancellationToken).ConfigureAwait(false);
        }
        finally { DeleteTemporary(temporary); copyGate.Release(); }
    }

    public async Task<ImportResult> SaveGeneratedAsync(string name, string content, string? projectId = null, CancellationToken cancellationToken = default)
    {
        ArchiveSafety.Name(name); ArgumentNullException.ThrowIfNull(content);
        if (Encoding.UTF8.GetByteCount(content) > ArchiveSafety.MaxFileBytes) throw new IOException("生成内容超过 256 MiB 上限。");
        await using var session = await StorageLease.SessionAsync(DataRoot, cancellationToken).ConfigureAwait(false);
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
            var archived = await PublishAsync(temporary, name, projectId, "应用", "", new(TextExtraction.ClassifyName(name),"待提取",""), [], cancellationToken,queueExtraction:true).ConfigureAwait(false);
            return await AnalyzeArchivedAsync(archived,cancellationToken).ConfigureAwait(false);
        }
        finally { DeleteTemporary(temporary); copyGate.Release(); }
    }

    public async Task<ImportResult> ImportRepositoryAsync(string path, string? projectId = null, string source = "用户", CancellationToken cancellationToken = default)
    {
        RefuseManualWrite();
        var full = SourcePath(path); ValidateMetadata(source, ""); ArchiveSafety.NoReparse(full);
        if (!Directory.Exists(full)) throw new DirectoryNotFoundException(full);
        var name = new DirectoryInfo(full).Name + ".zip"; ArchiveSafety.Name(name);
        await using var session = await StorageLease.SessionAsync(DataRoot, cancellationToken).ConfigureAwait(false);
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
        =>UpdateArtifactDetailsAsync(id,title,tags,projectId,null,cancellationToken);

    public Task UpdateArtifactDetailsAsync(string id,string title,string tags,string? projectId,string? kind,CancellationToken cancellationToken=default)
    {
        RefuseManualWrite();
        Metadata(title, 1024, nameof(title), required: true); Metadata(tags, 4096, nameof(tags));
        if(kind is not null)Metadata(kind,64,nameof(kind),required:true);
        return WriteAsync(db =>
        {
            using var tx = db.BeginTransaction(deferred: false); ProjectExists(db, projectId, tx);
            var current = Find(db, "id=$id AND deleted=0", tx, ("$id", id)) ?? throw new KeyNotFoundException("作品不存在。");
            var key = LogicalKey(current.OriginalName, projectId, current.Source);
            if(projectId==current.ProjectId)
            {
                using var lineage=Command(db,"SELECT logical_key FROM artifacts WHERE id=$id",("$id",id));lineage.Transaction=tx;key=(string)lineage.ExecuteScalar()!;
            }
            else
            {
                using var origin=Command(db,"SELECT MIN(path) FROM artifact_origins WHERE artifact_id=$id AND EXISTS(SELECT 1 FROM collection_items WHERE artifact_id=$id AND state='archived')",("$id",id));origin.Transaction=tx;
                if(origin.ExecuteScalar() is string collectedPath)key=LogicalKey(collectedPath,projectId,current.Source);
            }
            // Project metadata changes also move deduplication scope. Reserve the historical version in
            // the destination lineage, but never renumber an immutable historical record.
            using var reserve = Command(db, "INSERT INTO series VALUES($key,$version) ON CONFLICT(logical_key) DO UPDATE SET last_version=MAX(last_version,excluded.last_version)", ("$key", key), ("$version", current.Version)); reserve.Transaction = tx;
            reserve.ExecuteNonQuery();
            using var cmd = Command(db, "UPDATE artifacts SET title=$title,tags=$tags,project_id=$project,logical_key=$key,kind=COALESCE($kind,kind) WHERE id=$id AND deleted=0", ("$title", title.Trim()), ("$tags", tags), ("$project", projectId), ("$key", key), ("$id", id),("$kind",kind?.Trim())); cmd.Transaction = tx;
            cancellationToken.ThrowIfCancellationRequested();
            if (cmd.ExecuteNonQuery() != 1) throw new KeyNotFoundException("作品不存在。");
            if (!string.Equals(current.Source.Trim(), "应用", StringComparison.Ordinal))
            {
                RevocationJournal.Enqueue(db, tx, DataRoot, current.ProjectId);
                if (projectId != current.ProjectId) RevocationJournal.Enqueue(db, tx, DataRoot, projectId);
            }
            if(projectId!=current.ProjectId)
            {
                TaskStore.CancelSource(db,tx,id);
                if(current.ExtractionStatus=="待提取")TaskStore.Insert(db,tx,"extract",id,projectId,current.Hash,current.Version);
            }
            tx.Commit(); return true;
        }, cancellationToken);
    }

    public Task RemoveArtifactAsync(string id, CancellationToken cancellationToken = default)
    {
        RefuseManualWrite();
        return WriteAsync(db =>
    {
        using var tx = db.BeginTransaction(deferred: false);
        var current = Find(db, "id=$id AND deleted=0", tx, ("$id", id));
        if (current is null) return true;
        using var cmd = Command(db, "UPDATE artifacts SET deleted=1,preview='',tags='',deleted_at=$date WHERE id=$id", ("$id", id), ("$date", FormatDate(DateTimeOffset.UtcNow)));
        cmd.Transaction = tx;
        cancellationToken.ThrowIfCancellationRequested(); cmd.ExecuteNonQuery();
        TaskStore.CancelSource(db,tx,id);
        if (!string.Equals(current.Source.Trim(), "应用", StringComparison.Ordinal)) RevocationJournal.Enqueue(db, tx, DataRoot, current.ProjectId);
        tx.Commit(); StorageEvents.Log.Checkpoint("revocation-committed"); return true;
        }, cancellationToken);
    }

    public async Task<string> GetOriginalPathAsync(string id, CancellationToken cancellationToken = default)
    {
        var artifact = await GetAsync(id, cancellationToken).ConfigureAwait(false) ?? throw new KeyNotFoundException("作品不存在或已删除。");
        if(artifact.IsReference)
        {
            await ValidateReferenceScopeAsync(artifact,cancellationToken).ConfigureAwait(false);
            if(artifact.ExtractionStatus.Contains("缺失",StringComparison.Ordinal)||artifact.ExtractionStatus.Contains("不可读",StringComparison.Ordinal))throw new IOException("原文件缺失或不可读。");
            return artifact.ObjectPath;
        }
        ValidateObject(artifact); cancellationToken.ThrowIfCancellationRequested(); return artifact.ObjectPath;
    }

    private async Task<ImportResult> PublishAsync(string temporary, string name, string? projectId, string source, string tags, Extraction extraction, IReadOnlyList<string> warnings, CancellationToken token,bool queueExtraction=false,CollectionCommit? collection=null,string? originPath=null)
    {
        string hash; long bytes;
        await using (var stream = ArchiveSafety.OpenRead(temporary))
        { bytes = stream.Length; hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)).ToLowerInvariant(); }
        var key = LogicalKey(collection is null?name:collection.Path, projectId, source);
        return await WriteAsync(db =>
        {
            using var tx = db.BeginTransaction(deferred: false);
            ProjectExists(db, projectId, tx);
            originPath??=collection?.Path;
            if(originPath is not null)
            {
                using var lineage=Command(db,"SELECT a.logical_key FROM artifact_origins o JOIN artifacts a ON a.id=o.artifact_id WHERE o.path=$p AND a.project_id IS $project AND a.source=$s ORDER BY a.version DESC LIMIT 1",("$p",originPath.ToUpperInvariant()),("$project",projectId),("$s",source));lineage.Transaction=tx;
                if(lineage.ExecuteScalar() is string prior)key=prior;
            }
            if(collection?.GrantProject is not null)CheckGrant(db,tx,collection.GrantProject,collection.GrantRevision);
            var existing = Find(db, "logical_key=$key AND hash=$hash AND deleted=0", tx, ("$key", key), ("$hash", hash));
            if (existing is not null) { ValidateObject(existing); CompleteCollection(db,tx,collection,existing);RecordOrigin(db,tx,existing.Id,originPath);tx.Commit();return new ImportResult(existing, true, warnings); }
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
            StorageEvents.Log.Checkpoint("object-published");
            using var insert = Command(db, """
                INSERT INTO artifacts(id,title,original_name,kind,source,project_id,tags,object_name,hash,bytes,created_at,version,extraction_status,preview,logical_key)
                VALUES($id,$title,$name,$kind,$source,$project,$tags,$object,$hash,$bytes,$date,$version,$status,$preview,$key)
                """, ("$id", id), ("$title", artifact.Title), ("$name", name), ("$kind", extraction.Kind), ("$source", source), ("$project", projectId), ("$tags", tags), ("$object", objectName), ("$hash", hash), ("$bytes", bytes), ("$date", FormatDate(artifact.CreatedAt)), ("$version", version), ("$status", extraction.Status), ("$preview", extraction.Preview), ("$key", key));
            insert.Transaction = tx; insert.ExecuteNonQuery();
            if(queueExtraction)TaskStore.Insert(db,tx,"extract",id,projectId,hash,version);
            CompleteCollection(db,tx,collection,artifact);
            RecordOrigin(db,tx,id,originPath);
            token.ThrowIfCancellationRequested(); tx.Commit();
            StorageEvents.Log.Checkpoint("db-committed");
            return new ImportResult(artifact, false, warnings);
        }, token).ConfigureAwait(false);
    }

    internal Task<T> WriteAsync<T>(Func<SqliteConnection, T> action, CancellationToken token) => DbAsync(action, token, true);
    internal async Task<T> DbAsync<T>(Func<SqliteConnection, T> action, CancellationToken token, bool write = false)
    {
        await using var session = await StorageLease.SessionAsync(DataRoot, token).ConfigureAwait(false);
        await InitializeAsync(token).ConfigureAwait(false);
        await using var lease = await (write ? StorageLease.ExclusiveAsync(DataRoot, token) : StorageLease.SharedAsync(DataRoot, token)).ConfigureAwait(false);
        // Microsoft.Data.Sqlite operations are synchronous; move locking/IO off the UI thread.
        return await Task.Run(() => { token.ThrowIfCancellationRequested(); CheckStorage(); using var db = Open(); var result = action(db); if (write) RevocationJournal.Replay(DataRoot); return result; }, token).ConfigureAwait(false);
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
        if (ArchiveSafety.ManagedPath(full, DataRoot)) throw new IOException("不能重新导入作品仓库或恢复资料，避免归档循环。");
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
    private ArtifactRecord ReadArtifact(SqliteDataReader r)=>ReadArtifactCore(r,null);
    private ArtifactRecord ReadArtifactCore(SqliteDataReader r,ListReferencePaths? referencePaths)
    {
        var name = r.GetString(7);var reference=name.StartsWith("ref:",StringComparison.Ordinal);var path=reference?(referencePaths is null?ArchiveSafety.LocalPath(name[4..]):referencePaths.Normalize(name[4..])):Path.Combine(objects,name);if(!reference)ArchiveSafety.Name(name);
        return new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetString(6), path, r.GetString(8), r.GetInt64(9), ParseDate(r.GetString(10)), r.GetInt32(11), r.GetString(12), r.GetString(13),reference);
    }

    // Local-volume classification is invariant within this metadata read. Normalize and reject
    // UNC/device/ADS paths on EVERY row, but do not call GetDriveType for every file on that volume.
    // This object belongs to one ListAsync invocation; no source/volume validation survives it.
    private sealed class ListReferencePaths
    {
        private readonly HashSet<string> roots=new(StringComparer.OrdinalIgnoreCase);
        internal bool MeasureTimings;
        private long pathTicks;
        internal double PathMilliseconds=>pathTicks*1000.0/Stopwatch.Frequency;
        internal int ValidatedVolumes=>roots.Count;
        internal string Normalize(string path)
        {
            var clock=MeasureTimings?Stopwatch.GetTimestamp():0;
            try
            {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            var full=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if(OperatingSystem.IsWindows())
            {
                if(full.StartsWith(@"\\",StringComparison.Ordinal)||full[2..].Contains(':'))
                    throw new IOException("仅支持本机普通路径，不支持网络、设备路径或备用数据流。");
                var root=Path.GetPathRoot(full)!;
                if(roots.Add(root))ArchiveSafety.LocalPath(full);
            }
            return full;
            }
            finally{if(MeasureTimings)pathTicks+=Stopwatch.GetTimestamp()-clock;}
        }
    }
    private void ValidateObject(ArtifactRecord artifact)
    {
        if (!ArchiveSafety.Within(artifact.ObjectPath, objects)) throw new IOException("无效的归档路径。");
        using var stream = ArchiveSafety.OpenRead(artifact.ObjectPath);
        if (stream.Length != artifact.Bytes) throw new IOException("归档文件大小已发生外部变化。");
    }
}
