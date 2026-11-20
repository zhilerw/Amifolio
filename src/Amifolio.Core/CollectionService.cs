using System.Security.Cryptography;

namespace Amifolio.Core;

/// <summary>Finite, explicitly selected scans. Never watches a directory or invokes a model.</summary>
public sealed class CollectionService(PortfolioStore store)
{
    public const int MaxEntries=5000;
    private static readonly HashSet<string> Excluded=new(StringComparer.OrdinalIgnoreCase){".git",".hg",".svn",".codex",".agents",".claude",".ssh",".aws",".azure","node_modules","bin","obj","venv",".venv","__pycache__",".cache","cache","temp","tmp","secrets","keys","inputs","input","references","reference","知识库","真题","__amifolio__"};
    public static bool IsExcludedName(string name)=>Excluded.Contains(name)||RepositorySnapshot.IsSecret(name)||name.Equals("AGENTS.md",StringComparison.OrdinalIgnoreCase)||name.Equals("config.toml",StringComparison.OrdinalIgnoreCase)||name.Equals("auth.json",StringComparison.OrdinalIgnoreCase)||name.StartsWith("context-",StringComparison.OrdinalIgnoreCase)||name.StartsWith("~$",StringComparison.Ordinal)||new[]{".tmp",".partial",".bak",".lock",".swp"}.Contains(Path.GetExtension(name),StringComparer.OrdinalIgnoreCase);
    private static void ValidateScanRoot(string root)
    {
        var forbidden=new[]{"input","inputs","reference","references","知识库","真题",".agents",".claude",".ssh",".aws",".azure","secrets","keys","context-state",".superpowers"};
        if(IsExcludedName(Path.GetFileName(root))||root.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).Any(part=>forbidden.Contains(part,StringComparer.OrdinalIgnoreCase)))throw new IOException("不能扫描来源资料、凭据、配置或保留目录。");
        var codex=Environment.GetEnvironmentVariable("CODEX_HOME");if(string.IsNullOrWhiteSpace(codex))codex=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");
        if(ArchiveSafety.Within(root,Path.GetFullPath(codex))&&!ArchiveSafety.Within(root,Path.Combine(Path.GetFullPath(codex),"worktrees")))throw new IOException("不能扫描 Codex 的用户配置与会话目录。");
    }

    public async Task<CollectionPreview> PreviewAsync(string root,string? projectId=null,string source="用户",CancellationToken ct=default)
    {
        root=ArchiveSafety.LocalPath(root);ArchiveSafety.NoReparse(root);
        ValidateScanRoot(root);
        if(ArchiveSafety.ManagedPath(root,store.DataRoot))throw new IOException("不能扫描作品库或恢复资料。");
        if(projectId is not null&&!(await store.ProjectsAsync(ct)).Any(p=>p.Id==projectId))throw new IOException("项目不存在。");
        if(source is not ("用户" or "Codex"))throw new IOException("不支持的扫描来源。");
        using var bounded=CancellationTokenSource.CreateLinkedTokenSource(ct);bounded.CancelAfter(TimeSpan.FromSeconds(20));var token=bounded.Token;
        return await Task.Run(async()=>
        {
            var result=new List<CollectionEntry>();var count=0;long bytes=0;var truncated=false;var queue=new Stack<(string,int)>();queue.Push((root,0));
            async Task Add(string path,int depth)
            {
                token.ThrowIfCancellationRequested();if(++count>MaxEntries){truncated=true;return;}
                var relative=File.Exists(root)?Path.GetFileName(path):Path.GetRelativePath(root,path);var info=new FileInfo(path);
                var attributes=File.GetAttributes(path);
                if(ArchiveSafety.ManagedPath(path,store.DataRoot)||IsExcludedName(Path.GetFileName(path))||(attributes&(FileAttributes.ReparsePoint|FileAttributes.Device))!=0){result.Add(new(path,relative,0,"","","excluded","排除的目录、配置、临时文件或链接"));return;}
                if((attributes&FileAttributes.Directory)!=0){if(depth>=32){truncated=true;result.Add(new(path,relative,0,"","","excluded","目录深度上限"));}else queue.Push((path,depth+1));return;}
                try
                {
                    await using var input=ArchiveSafety.OpenRead(path);if(input.Length>64L*1024*1024||bytes+input.Length>256L*1024*1024){truncated=true;result.Add(new(path,relative,input.Length,"","","excluded","扫描大小上限"));return;}
                    bytes+=input.Length;var hash=Convert.ToHexStringLower(await SHA256.HashDataAsync(input,token));result.Add(new(path,relative,input.Length,hash,TextExtraction.ClassifyName(path),"ready"));
                }
                catch(Exception e)when(e is IOException or UnauthorizedAccessException){result.Add(new(path,relative,0,"","","failed","文件不可读或正在写入"));}
            }
            if(File.Exists(root))await Add(root,0);
            else if(Directory.Exists(root))while(queue.Count>0&&!truncated)
            {
                var (directory,depth)=queue.Pop();ArchiveSafety.NoReparse(directory);
                try{foreach(var path in Directory.EnumerateFileSystemEntries(directory)){if(truncated)break;try{await Add(path,depth);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){result.Add(new(path,Path.GetRelativePath(root,path),0,"","","failed","目录或文件不可访问"));}}}
                catch(Exception e)when(e is IOException or UnauthorizedAccessException){result.Add(new(directory,Path.GetRelativePath(root,directory),0,"","","failed","目录不可访问"));}
            }
            else throw new FileNotFoundException("扫描目标不存在。",root);
            return new CollectionPreview(root,projectId,source,result.OrderBy(x=>x.RelativePath,StringComparer.OrdinalIgnoreCase).ToArray(),truncated);
        },token).ConfigureAwait(false);
    }
    public Task<IReadOnlyList<CollectionGrant>> GrantsAsync(CancellationToken ct=default)=>store.CollectionGrantsAsync(ct);
    public async Task GrantAsync(string projectId,string root,bool enabled,CancellationToken ct=default)
    {
        var project=(await store.ProjectsAsync(ct)).SingleOrDefault(p=>p.Id==projectId)??throw new IOException("项目不存在。");
        root=ArchiveSafety.LocalPath(root);if(!enabled){await store.SetCollectionGrantAsync(projectId,root,false,ct);return;}ArchiveSafety.NoReparse(root);
        if(string.IsNullOrWhiteSpace(project.RootPath)||root.Equals(project.RootPath,ArchiveSafety.PathComparison)||!ArchiveSafety.Within(root,project.RootPath)||ArchiveSafety.ManagedPath(root,store.DataRoot)||Path.GetRelativePath(project.RootPath,root).Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).Any(IsExcludedName))throw new IOException("请选择项目内独立的输出子目录，不能授权整个项目、来源或配置目录。");
        if(enabled&&!Directory.Exists(root))throw new DirectoryNotFoundException("输出目录不存在。");
        await store.SetCollectionGrantAsync(projectId,root,enabled,ct);
    }
    public Task<IReadOnlyList<CollectionReceipt>> ListReceiptsAsync(CancellationToken ct=default)=>store.CollectionReceiptsAsync(ct);
    public Task<IReadOnlyList<CollectionReceipt>> CollectAsync(CollectionPreview preview,IReadOnlyCollection<string>? selectedPaths=null,CancellationToken ct=default)=>CollectCoreAsync(preview,selectedPaths,null,ct);
    private async Task<IReadOnlyList<CollectionReceipt>> CollectCoreAsync(CollectionPreview preview,IReadOnlyCollection<string>? selectedPaths,CollectionGrant? grant,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();await store.InitializeAsync(ct);
        await using var session=await StorageLease.SessionAsync(store.DataRoot,ct);
        var lockPath=Path.Combine(store.DataRoot,".collection.lock");ArchiveSafety.NoReparse(lockPath);
        await using var active=new FileStream(lockPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        await store.RecoverCollectionsAsync(ct);
        // Preview objects are not an authority: validate all paths and names again, then bind the exact hashes.
        if(preview.Entries.Count>MaxEntries+32)throw new IOException("扫描清单过大。");
        var root=ArchiveSafety.LocalPath(preview.Root);ArchiveSafety.NoReparse(root);
        ValidateScanRoot(root);
        if(preview.Truncated)
        {
            var sentinel=new CollectionEntry(root,"扫描范围",0,"","","incomplete","扫描达到上限，清单不完整。请缩小输出目录范围后补扫。");
            preview=preview with{Entries=[..preview.Entries.Where(e=>!e.Path.Equals(root,ArchiveSafety.PathComparison)),sentinel]};
        }
        if(ArchiveSafety.ManagedPath(root,store.DataRoot))throw new IOException("不能收纳作品库。");
        foreach(var entry in preview.Entries)
        {
            if(!ArchiveSafety.Within(ArchiveSafety.LocalPath(entry.Path),root))throw new IOException("扫描条目越过选定范围。");
            if(entry.State=="ready"&&Path.GetRelativePath(root,entry.Path).Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).Any(IsExcludedName))throw new IOException("已排除的扫描路径。");
        }
        var selected=selectedPaths is null?null:new HashSet<string>(selectedPaths.Select(Path.GetFullPath),OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
        var batch=await store.StartCollectionAsync(preview,selected,grant,ct);
        try
        {
            foreach(var entry in preview.Entries.Where(e=>e.State=="ready"&&(selected is null||selected.Contains(e.Path))))
            {
                ct.ThrowIfCancellationRequested();
                try{await store.ImportCollectedAsync(entry,preview.ProjectId,preview.Source,new(batch,entry.Path,entry.Hash,grant?.ProjectId,grant?.Revision??0),ct);}
                catch(OperationCanceledException){throw;}
                catch(Exception e)when(e is IOException or UnauthorizedAccessException or InvalidOperationException)
                {await store.MarkCollectionAsync(batch,entry.Path,e is CollectionChangedException?"changed":"failed",e is CollectionChangedException?"文件已改变，请重新扫描":"收纳失败、文件占用或授权已撤销",CancellationToken.None);}
            }
        }
        catch(OperationCanceledException){await store.CancelCollectionAsync(batch,CancellationToken.None);throw;}
        return await store.CollectionReceiptsAsync(ct,batch);
    }
    public async Task<IReadOnlyList<CollectionReceipt>> ReconcileAsync(string workingDirectory,CancellationToken ct=default)
    {
        workingDirectory=ArchiveSafety.LocalPath(workingDirectory);ArchiveSafety.NoReparse(workingDirectory);
        var projects=await store.ProjectsAsync(ct);var grants=await GrantsAsync(ct);var result=new List<CollectionReceipt>();
        // Nested projects must not accidentally trigger the enclosing project's collector.
        var project=projects.Where(p=>p.RootPath is not null&&ArchiveSafety.Within(workingDirectory,p.RootPath)).OrderByDescending(p=>p.RootPath!.Length).FirstOrDefault();
        if(project is null)return result;
        var grant=grants.SingleOrDefault(g=>g.ProjectId==project.Id&&g.Enabled);if(grant is null)return result;
        CollectionPreview preview;
        try{preview=await PreviewAsync(grant.Root,project.Id,"Codex",ct);}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException)
        {preview=new(grant.Root,project.Id,"Codex",[new(grant.Root,".",0,"","","failed","扫描未完成，目录缺失或不可访问")],false);}
        var seen=await store.CollectionSeenAsync(project.Id,ct);
        var changed=preview.Entries.Where(e=>e.State!="ready"||!seen.Contains(e.Path+"\n"+e.Hash)).ToArray();
        if(changed.Length==0&&!preview.Truncated)return result;
        result.AddRange(await CollectCoreAsync(preview with{Entries=changed},null,grant,ct));return result;
    }
}
internal sealed class CollectionChangedException():IOException("文件在预览后改变。");
