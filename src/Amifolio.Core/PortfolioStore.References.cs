using System.Security.Cryptography;
using System.Text;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Amifolio.Core;

public sealed partial class PortfolioStore
{
    public bool ReadOnlySources { get; private set; }
    private CodexCatalog? codexCatalog;
    private string? referenceCatalogHome;
    private IReadOnlyList<CodexProject>? codexProjects;
    public IReadOnlyList<CodexProject> CodexProjects => codexProjects ?? [];
    public IReadOnlyList<string> DiscoveryWarnings { get; private set; } = [];
    public CodexCatalog Catalog => codexCatalog ?? throw new InvalidOperationException("尚未启用Codex只读模式。");
    // Let the desktop open its disposable display cache without triggering discovery
    // from incidental database reads. Only DiscoverAsync can authorize source access.
    public void DeferCodexDiscovery() { if(ReadOnlySources && codexProjects is null) codexProjects=[]; }
    public async Task RefreshSourceStatesAsync(CancellationToken ct=default)
    {
        if(!ReadOnlySources)return;
        if(codexProjects is null)await RefreshCodexProjectsAsync(ct).ConfigureAwait(false);
        else await RefreshReferenceStatesAsync(ct).ConfigureAwait(false);
    }

    public void EnableCodexReferenceMode(string codexHome)
    {
        ReadOnlySources = true;
        codexCatalog = new CodexCatalog(codexHome, DataRoot);
        referenceCatalogHome = ArchiveSafety.LocalPath(codexHome);
    }
    private void RefuseManualWrite()
    {
        if (ReadOnlySources) throw new NotSupportedException("只读Codex项目不支持上传、复制、手动创建或修改作品。请在Codex中管理原项目。");
    }
    public async Task RefreshCodexProjectsAsync(CancellationToken ct = default)
    {
        var result = await Catalog.DiscoverAsync(ct).ConfigureAwait(false);
        codexProjects = result.Items;
        DiscoveryWarnings = result.Warnings.Concat(result.Truncated ? ["项目发现达到上限，当前结果不完整。"] : Array.Empty<string>()).ToArray();
        await WriteAsync(db =>
        {
            using var tx = db.BeginTransaction();
            foreach (var project in codexProjects)
            {
                using var c = Command(db,"INSERT INTO projects(id,name,root_path,created_at) VALUES($id,$name,$root,$date) ON CONFLICT(id) DO UPDATE SET name=$name,root_path=$root",("$id",project.Id),("$name",project.Name),("$root",project.RootPath),("$date",FormatDate(DateTimeOffset.UtcNow)));
                c.Transaction=tx; c.ExecuteNonQuery();
            }
            tx.Commit(); return true;
        },ct).ConfigureAwait(false);
        await RefreshReferenceStatesAsync(ct).ConfigureAwait(false);
    }
    private async Task<IReadOnlyList<ProjectRecord>> ReferenceProjectsAsync(CancellationToken ct)
    {
        if(codexProjects is null) await RefreshCodexProjectsAsync(ct).ConfigureAwait(false);
        return CodexProjects.Select(p=>new ProjectRecord(p.Id,p.Name,p.RootPath,DateTimeOffset.UnixEpoch)).ToArray();
    }
    public async Task<ImportResult> ReferenceFileAsync(string path,string? projectId=null,CancellationToken ct=default)
    {
        if(!ReadOnlySources) throw new InvalidOperationException("请先启用Codex只读模式。");
        if(codexProjects is null) await RefreshCodexProjectsAsync(ct).ConfigureAwait(false);
        path=ArchiveSafety.LocalPath(path);
        var project=CodexProjects.Where(p=>projectId is null||p.Id==projectId).Where(p=>ArchiveSafety.Within(path,p.RootPath)).OrderByDescending(p=>p.RootPath.Length).FirstOrDefault()??throw new IOException("文件不属于已发现的Codex项目。");
        var reference=await InspectReferenceAsync(project,path,ct).ConfigureAwait(false);
        return await WriteAsync(db=>
        {
            var existing=Find(db,"id=$id",("$id",reference.Id));
            if(existing is not null)return new ImportResult(existing,true,[]);
            InsertReference(db,null,project.Id,reference);
            return new ImportResult(Find(db,"id=$id",("$id",reference.Id))!,false,[]);
        },ct).ConfigureAwait(false);
    }

    private sealed record ReferenceMetadata(string Id,string Path,string Name,long Bytes,DateTimeOffset Modified);

    private async Task<ReferenceMetadata> InspectReferenceAsync(CodexProject project,string path,CancellationToken ct)
    {
        path=await Catalog.ValidateFileAsync(project,path,ct).ConfigureAwait(false);
        var info=new FileInfo(path);
        var bytes=info.Length;
        if(bytes>ArchiveSafety.MaxFileBytes)throw new IOException("文件超过256 MiB解析上限。");
        var id="ref-"+Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(project.Id+"\n"+path.ToUpperInvariant())));
        return new(id,path,info.Name,bytes,info.LastWriteTimeUtc);
    }

    private static void InsertReference(Microsoft.Data.Sqlite.SqliteConnection db,Microsoft.Data.Sqlite.SqliteTransaction? tx,
        string projectId,ReferenceMetadata item) =>
        LearningSchema.Exec(db,tx,"""
            INSERT OR IGNORE INTO artifacts(id,title,original_name,kind,source,project_id,tags,object_name,hash,bytes,created_at,version,extraction_status,preview,logical_key)
            VALUES($id,$name,$name,$kind,'Codex',$project,'',$path,'',$bytes,$date,1,'未解析','',$id)
            """,("$id",item.Id),("$name",item.Name),("$kind",TextExtraction.ClassifyName(item.Name)),("$project",projectId),
            ("$path","ref:"+item.Path),("$bytes",item.Bytes),("$date",FormatDate(item.Modified)));
    public async Task ParseReferenceAsync(string id,CancellationToken ct=default)
    {
        var current=await GetAsync(id,ct).ConfigureAwait(false)??throw new IOException("文件引用不存在。");
        if(!current.IsReference)throw new NotSupportedException("只解析当前Codex项目原文件。");
        await ValidateReferenceScopeAsync(current,ct).ConfigureAwait(false);
        await using var input=ArchiveSafety.OpenRead(current.ObjectPath);
        if(input.Length>ArchiveSafety.MaxFileBytes)throw new IOException("文件超过256 MiB解析上限。");
        var hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(input,ct).ConfigureAwait(false));
        var info=new FileInfo(current.ObjectPath);
        await WriteAsync(db=>
        {
            using var tx=db.BeginTransaction();
            using var c=Command(db,"UPDATE artifacts SET hash=$hash,bytes=$bytes,created_at=$date,preview='',extraction_status='待提取' WHERE id=$id",("$hash",hash),("$bytes",input.Length),("$date",FormatDate(new DateTimeOffset(info.LastWriteTimeUtc))),("$id",id));c.Transaction=tx;c.ExecuteNonQuery();
            TaskStore.CancelSource(db,tx,id);TaskStore.Insert(db,tx,"extract",id,current.ProjectId,hash,current.Version);tx.Commit();return true;
        },ct).ConfigureAwait(false);
        await ProcessTaskAsync((await new TaskStore(this).PendingExtractionAsync(id,ct).ConfigureAwait(false)).First().Id,ct).ConfigureAwait(false);
    }
    private async Task ValidateReferenceScopeAsync(ArtifactRecord record,CancellationToken ct)
    {
        if(codexProjects is null)await RefreshCodexProjectsAsync(ct).ConfigureAwait(false);
        var project=CodexProjects.FirstOrDefault(p=>p.Id==record.ProjectId)??throw new IOException("来源项目不在当前Codex项目中。");
        await Catalog.ValidateFileAsync(project,record.ObjectPath,ct).ConfigureAwait(false);
    }
    private static ArtifactRecord LiveReference(ArtifactRecord item,bool verifyHash=false)
    {
        if(!item.IsReference)return item;
        try
        {
            ArchiveSafety.NoReparse(item.ObjectPath);
            if(!File.Exists(item.ObjectPath))return item with{Preview="",ExtractionStatus="源文件缺失"};
            var info=new FileInfo(item.ObjectPath);
            if(info.Length!=item.Bytes||info.LastWriteTimeUtc!=item.CreatedAt.UtcDateTime)return item with{Preview="",ExtractionStatus="源文件已变化，请重新解析"};
            if(verifyHash&&item.Hash.Length==64&&item.Preview.Length>0)
            {
                using var input=ArchiveSafety.OpenRead(item.ObjectPath);
                if(!Convert.ToHexString(SHA256.HashData(input)).Equals(item.Hash,StringComparison.OrdinalIgnoreCase))return item with{Preview="",ExtractionStatus="源文件已变化，请重新解析"};
            }
            return item;
        }
        catch(Exception e)when(e is FileNotFoundException or DirectoryNotFoundException){return item with{Preview="",ExtractionStatus="源文件缺失"};}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){return item with{Preview="",ExtractionStatus="源文件不可读"};}
    }

    private sealed class ReferenceListReads
    { internal int IndividualFiles; internal int Directories; internal int PinnedDirectories; }

    private IReadOnlyList<ArtifactRecord> LiveReferences(IReadOnlyList<ArtifactRecord> items,ReferenceListReads reads,CancellationToken ct)
    {
        var allowed=CodexProjects.ToDictionary(p=>p.Id,StringComparer.Ordinal);
        var result=items.Where(a=>a.IsReference&&a.ProjectId is not null&&allowed.ContainsKey(a.ProjectId)).ToArray();
        var scoped=new List<(ArtifactRecord item,int index)>();
        for(var i=0;i<result.Length;i++)
        {
            ct.ThrowIfCancellationRequested();
            if(ReferenceMetadataInScope(allowed[result[i].ProjectId!],result[i].ObjectPath))scoped.Add((result[i],i));
            else result[i]=result[i] with{Preview="",ExtractionStatus="源文件不可读"};
        }
        // Only metadata is read. No content, parser, writer lease or invalidation is entered by ListAsync.
        // Pins and file metadata live for this invocation only, never across queries.
        using var pins=new ReferenceDirectoryPins();
        foreach(var group in scoped.GroupBy(p=>Path.GetDirectoryName(p.item.ObjectPath)!,StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            var groupCount=group.Count();
            Dictionary<string,ReferenceFileMetadata>? metadata=null;
            if(OperatingSystem.IsWindows()&&groupCount>=8&&pins.TryPinCore(group.Key,ct))
            {
                StorageEvents.Log.Checkpoint("list-reference-directory-pinned");
                ct.ThrowIfCancellationRequested();
                try
                {
                    var wanted=group.Select(p=>Path.GetFileName(p.item.ObjectPath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    var entries=new FileSystemEnumerable<ReferenceFileMetadata>(group.Key,
                        (ref FileSystemEntry e)=>new(e.FileName.ToString(),e.Attributes,e.Length,e.LastWriteTimeUtc.UtcDateTime),
                        new EnumerationOptions{RecurseSubdirectories=false,AttributesToSkip=0,IgnoreInaccessible=false,ReturnSpecialDirectories=false});
                    metadata=new(StringComparer.OrdinalIgnoreCase);
                    var visited=0;
                    foreach(var entry in entries)
                    {
                        ct.ThrowIfCancellationRequested();
                        // A directory can contain far more unindexed files than references. Fall back
                        // rather than silently cap results or scan an arbitrarily large directory.
                        if(++visited>Math.Max(1024,groupCount*4)){metadata=null;break;}
                        if(wanted.Contains(entry.Name))metadata[entry.Name]=entry;
                    }
                    reads.Directories++;
                }
                catch(Exception e)when(e is IOException or UnauthorizedAccessException){metadata=null;}
            }
            foreach(var (item,index) in group)
            {
                ct.ThrowIfCancellationRequested();
                if(metadata is null){reads.IndividualFiles++;result[index]=LiveReference(item);continue;}
                if(!metadata.TryGetValue(Path.GetFileName(item.ObjectPath),out var file))
                    result[index]=item with{Preview="",ExtractionStatus="源文件缺失"};
                else if(file.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    result[index]=item with{Preview="",ExtractionStatus="源文件不可读"};
                else if(file.Attributes.HasFlag(FileAttributes.Directory))
                    result[index]=item with{Preview="",ExtractionStatus="源文件缺失"};
                else if(file.Bytes!=item.Bytes||file.ModifiedUtc!=item.CreatedAt.UtcDateTime)
                    result[index]=item with{Preview="",ExtractionStatus="源文件已变化，请重新解析"};
            }
        }
        reads.PinnedDirectories=pins.Count;
        return result;
    }

    private sealed record ReferenceFileMetadata(string Name,FileAttributes Attributes,long Bytes,DateTime ModifiedUtc);

    private const int ReferencePathDepthLimit=64;
    private static bool ReferenceDepthAllowed(string path)
    {
        var depth=0;
        foreach(var c in path)if((c=='/'||c=='\\')&&++depth>ReferencePathDepthLimit)return false;
        return true;
    }
    // The catalog remains the authorization source. Cache rows cannot authorize a different
    // root, protected application storage, or excluded relative components. This lexical guard
    // runs before BOTH directory enumeration and the per-file fallback (including missing paths).
    // Keep exclusion parity with CodexCatalog; regression cases use its public validation as oracle.
    private bool ReferenceMetadataInScope(CodexProject project,string path)
    {
        if(!ReferenceDepthAllowed(path)||!ReferenceDepthAllowed(project.RootPath)||
            !ArchiveSafety.Within(path,project.RootPath)||ArchiveSafety.ManagedPath(path,DataRoot))return false;
        var parent=Path.GetDirectoryName(path);
        if(parent is null||!ArchiveSafety.Within(parent,project.RootPath))return false;
        if(referenceCatalogHome is not null&&ArchiveSafety.Within(path,referenceCatalogHome)&&
            !ArchiveSafety.Within(path,Path.Combine(referenceCatalogHome,"worktrees")))return false;
        foreach(var part in Path.GetRelativePath(project.RootPath,path).Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar))
            if(ReferenceExcludedNames.Contains(part)||RepositorySnapshot.IsSecret(part)||
                part.StartsWith("auth.",StringComparison.OrdinalIgnoreCase)||part.StartsWith("config.",StringComparison.OrdinalIgnoreCase)||
                part.StartsWith("appsettings.",StringComparison.OrdinalIgnoreCase)||part.StartsWith("context-",StringComparison.OrdinalIgnoreCase)||
                part.StartsWith("~$",StringComparison.Ordinal)||ReferenceExcludedExtensions.Contains(Path.GetExtension(part)))return false;
        return true;
    }
    private static readonly HashSet<string> ReferenceExcludedNames=new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",".hg",".svn",".codex",".agents",".claude",".ssh",".aws",".azure",".gnupg",".kube",".docker",".config",
        "secrets",".secrets","credentials",".credentials","keys",".keys","config","node_modules","bin","obj","venv",".venv",
        "__pycache__",".cache","cache","temp","tmp","__amifolio__","context-state",".superpowers",
        "auth.json","config.json","config.toml","config.yaml","config.yml","settings.json","AGENTS.md","CLAUDE.md","GEMINI.md",
        ".codex-global-state.json",".gitconfig",".git-credentials","appsettings.json","launchSettings.json"
    };
    private static readonly HashSet<string> ReferenceExcludedExtensions=new(StringComparer.OrdinalIgnoreCase)
        {".ini",".config",".toml",".tmp",".partial",".bak",".lock",".swp"};

    /// <summary>
    /// Windows-only query-scoped ancestor handles deny directory writes/deletes while the names
    /// are enumerated. OPEN_REPARSE_POINT inspects the directory itself, never its target. Each
    /// parent is pinned first, preventing a previously checked ancestor being swapped or retargeted.
    /// Share/access failures fall back to the existing per-file path checks; file contents remain writable.
    /// </summary>
    private sealed class ReferenceDirectoryPins:IDisposable
    {
        private readonly Dictionary<string,SafeFileHandle> handles=new(StringComparer.OrdinalIgnoreCase);
        internal int Count=>handles.Count;
        internal bool TryPin(string path)=>TryPinCore(path,CancellationToken.None);
        internal bool TryPinCore(string path,CancellationToken ct)
        {
            if(!ReferenceDepthAllowed(path))return false;
            var pending=new List<string>();
            for(string? current=path;current is not null&&!handles.ContainsKey(current);current=Path.GetDirectoryName(current))
            {
                ct.ThrowIfCancellationRequested();
                if(pending.Count>=ReferencePathDepthLimit||handles.Count+pending.Count>=1024)return false;
                pending.Add(current);
            }
            for(var i=pending.Count-1;i>=0;i--)
            {
            ct.ThrowIfCancellationRequested();
            // Include FILE_LIST_DIRECTORY: an attributes-only handle does not participate
            // in Windows data-sharing checks and therefore cannot protect against rename.
            var handle=OpenReferenceDirectory(pending[i],0x81,FileShare.Read,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
            if(handle.IsInvalid){handle.Dispose();return false;}
            if(!ReferenceDirectoryInfo(handle,9,out var info,8)||
                (info.Attributes&(FileAttributes.ReparsePoint|FileAttributes.Device))!=0||!info.Attributes.HasFlag(FileAttributes.Directory))
            {handle.Dispose();return false;}
            handles.Add(pending[i],handle);
            }
            return true;
        }
        public void Dispose(){foreach(var handle in handles.Values.Reverse())handle.Dispose();}
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ReferenceDirectoryAttributes{internal FileAttributes Attributes;internal uint ReparseTag;}
    [DllImport("kernel32.dll",EntryPoint="CreateFileW",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafeFileHandle OpenReferenceDirectory(string path,uint access,FileShare share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",EntryPoint="GetFileInformationByHandleEx",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReferenceDirectoryInfo(SafeFileHandle handle,int infoClass,out ReferenceDirectoryAttributes info,uint bytes);
    private async Task RefreshReferenceStatesAsync(CancellationToken ct)
    {
        var changed=await DbAsync(db=>
        {
            using var c=Command(db,"SELECT * FROM artifacts WHERE object_name LIKE 'ref:%' AND deleted=0");using var r=c.ExecuteReader();var changes=new List<ArtifactRecord>();
            while(r.Read()){ct.ThrowIfCancellationRequested();var item=ReadArtifact(r);var live=LiveReference(item);if(live.ExtractionStatus!=item.ExtractionStatus)changes.Add(live);}
            return changes;
        },ct).ConfigureAwait(false);
        foreach(var item in changed)await InvalidateReferenceAsync(item,ct).ConfigureAwait(false);
    }
    private Task<bool> InvalidateReferenceAsync(ArtifactRecord item,CancellationToken ct)=>WriteAsync(db=>
    {
        using var tx=db.BeginTransaction();
        using var c=Command(db,"UPDATE artifacts SET preview='',hash='',extraction_status=$status,version=version+1 WHERE id=$id AND extraction_status<>$status",("$status",item.ExtractionStatus),("$id",item.Id));c.Transaction=tx;
        if(c.ExecuteNonQuery()!=0){TaskStore.CancelSource(db,tx,item.Id);RevocationJournal.Enqueue(db,tx,DataRoot,item.ProjectId);}
        tx.Commit();return true;
    },ct);
}
