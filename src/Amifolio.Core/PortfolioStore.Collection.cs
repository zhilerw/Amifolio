using Microsoft.Data.Sqlite;
using System.Security.Cryptography;
using System.Text;

namespace Amifolio.Core;

public sealed partial class PortfolioStore
{
    private static void RecordOrigin(SqliteConnection db,SqliteTransaction tx,string id,string? path)
    {if(path is null)return;using var c=Command(db,"INSERT OR IGNORE INTO artifact_origins VALUES($id,$p)",("$id",id),("$p",path.ToUpperInvariant()));c.Transaction=tx;c.ExecuteNonQuery();}
    internal Task<int> RecoverCollectionsAsync(CancellationToken ct)=>WriteAsync(db=>{using var c=Command(db,"UPDATE collection_items SET state='interrupted',error='上次收纳中断，请重新扫描核对' WHERE state='pending'");return c.ExecuteNonQuery();},ct);
    internal Task<IReadOnlyList<CollectionGrant>> CollectionGrantsAsync(CancellationToken ct)=>DbAsync<IReadOnlyList<CollectionGrant>>(db=>{using var c=Command(db,"SELECT project_id,root,enabled,revision FROM collection_grants");using var r=c.ExecuteReader();var rows=new List<CollectionGrant>();while(r.Read())rows.Add(new(r.GetString(0),r.GetString(1),r.GetBoolean(2),r.GetInt32(3)));return rows;},ct);
    internal Task<int> SetCollectionGrantAsync(string project,string root,bool enabled,CancellationToken ct)=>WriteAsync(db=>{using var c=Command(db,"INSERT INTO collection_grants VALUES($p,$r,$e,1) ON CONFLICT(project_id) DO UPDATE SET root=$r,enabled=$e,revision=revision+1",("$p",project),("$r",root),("$e",enabled));return c.ExecuteNonQuery();},ct);
    internal Task<HashSet<string>> CollectionSeenAsync(string project,CancellationToken ct)=>DbAsync(db=>{using var c=Command(db,"SELECT o.path,a.hash FROM artifact_origins o JOIN artifacts a ON a.id=o.artifact_id WHERE a.project_id=$p AND a.source='Codex'",("$p",project));using var r=c.ExecuteReader();var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);while(r.Read())seen.Add(r.GetString(0)+"\n"+r.GetString(1));return seen;},ct);
    internal Task<string> StartCollectionAsync(CollectionPreview preview,HashSet<string>? selected,CollectionGrant? grant,CancellationToken ct)=>WriteAsync(db=>
    {
        using var tx=db.BeginTransaction(false);ProjectExists(db,preview.ProjectId,tx);if(grant is not null)CheckGrant(db,tx,grant.ProjectId,grant.Revision);
        var id=Guid.NewGuid().ToString("N");using var c=Command(db,"INSERT INTO collection_batches VALUES($id,$root,$p,$s,$date)",("$id",id),("$root",preview.Root),("$p",preview.ProjectId),("$s",preview.Source),("$date",FormatDate(DateTimeOffset.UtcNow)));c.Transaction=tx;c.ExecuteNonQuery();
        foreach(var entry in preview.Entries){using var item=Command(db,"INSERT INTO collection_items VALUES($id,$path,$hash,$state,NULL,$error)",("$id",id),("$path",entry.Path),("$hash",entry.Hash),("$state",entry.State=="ready"?(selected is null||selected.Contains(entry.Path)?"pending":"unsubmitted"):entry.State),("$error",entry.Error));item.Transaction=tx;item.ExecuteNonQuery();}
        ct.ThrowIfCancellationRequested();tx.Commit();return id;
    },ct);
    internal Task<IReadOnlyList<CollectionReceipt>> CollectionReceiptsAsync(CancellationToken ct,string? batch=null)=>DbAsync<IReadOnlyList<CollectionReceipt>>(db=>
    {
        using var c=Command(db,"SELECT i.batch_id,i.path,i.hash,i.state,i.artifact_id,i.error,b.created_at FROM collection_items i JOIN collection_batches b ON i.batch_id=b.id WHERE ($id IS NULL OR b.id=$id) ORDER BY b.created_at DESC,i.path LIMIT 5000",("$id",batch));using var r=c.ExecuteReader();var rows=new List<CollectionReceipt>();while(r.Read())rows.Add(new(r.GetString(0),r.GetString(1),r.GetString(2),r.GetString(3),r.IsDBNull(4)?null:r.GetString(4),r.IsDBNull(5)?null:r.GetString(5),ParseDate(r.GetString(6))));return rows;
    },ct);
    internal Task<int> MarkCollectionAsync(string batch,string path,string state,string error,CancellationToken ct)=>WriteAsync(db=>{using var c=Command(db,"UPDATE collection_items SET state=$s,error=$e WHERE batch_id=$id AND path=$p AND state='pending'",("$s",state),("$e",error),("$id",batch),("$p",path));return c.ExecuteNonQuery();},ct);
    internal Task<int> CancelCollectionAsync(string batch,CancellationToken ct)=>WriteAsync(db=>{using var c=Command(db,"UPDATE collection_items SET state='cancelled',error='收纳已取消' WHERE batch_id=$id AND state='pending'",("$id",batch));return c.ExecuteNonQuery();},ct);
    private static void CheckGrant(SqliteConnection db,SqliteTransaction tx,string project,int revision)
    {using var c=Command(db,"SELECT COUNT(*) FROM collection_grants WHERE project_id=$p AND revision=$r AND enabled=1",("$p",project),("$r",revision));c.Transaction=tx;if(Convert.ToInt32(c.ExecuteScalar())!=1)throw new IOException("自动收纳授权已变化。");}
    private static void CompleteCollection(SqliteConnection db,SqliteTransaction tx,CollectionCommit? commit,ArtifactRecord artifact)
    {
        if(commit is null)return;
        if(commit.GrantProject is not null)CheckGrant(db,tx,commit.GrantProject,commit.GrantRevision);
        using var c=Command(db,"UPDATE collection_items SET state='archived',artifact_id=$a,error=NULL WHERE batch_id=$b AND path=$p AND hash=$h AND state='pending'",("$a",artifact.Id),("$b",commit.BatchId),("$p",commit.Path),("$h",commit.Hash));c.Transaction=tx;if(c.ExecuteNonQuery()!=1)throw new IOException("收纳回执状态已变化。");
    }
    internal async Task<ImportResult> ImportCollectedAsync(CollectionEntry entry,string? project,string source,CollectionCommit commit,CancellationToken ct)
    {
        RefuseManualWrite();
        var full=SourcePath(entry.Path);if(entry.RelativePath.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar).Any(CollectionService.IsExcludedName))throw new IOException("已排除此文件。");
        ValidateMetadata(source,"");await InitializeAsync(ct);await using var session=await StorageLease.SessionAsync(DataRoot,ct);await copyGate.WaitAsync(ct);string? temp=null;
        try
        {
            temp=NewTemporary();await using(var input=ArchiveSafety.OpenRead(full))
            {if(input.Length!=entry.Bytes||input.Length>64L*1024*1024)throw new CollectionChangedException();await using var output=CreateTemporary(temp);await ArchiveSafety.CopyAsync(input,output,64L*1024*1024,ct);await FlushAsync(output,ct);}
            await using(var copied=ArchiveSafety.OpenRead(temp)){if(Convert.ToHexStringLower(await SHA256.HashDataAsync(copied,ct))!=entry.Hash)throw new CollectionChangedException();}
            return await PublishAsync(temp,Path.GetFileName(full),project,source,"",new(TextExtraction.ClassifyName(full),"待提取",""),[],ct,true,commit);
        }
        finally{DeleteTemporary(temp);copyGate.Release();}
    }
    public Task<int> UpdateKindAsync(string id,string kind,CancellationToken ct=default)
    {Metadata(kind,64,nameof(kind),true);return WriteAsync(db=>{using var c=Command(db,"UPDATE artifacts SET kind=$k WHERE id=$id AND deleted=0",("$k",kind.Trim()),("$id",id));return c.ExecuteNonQuery();},ct);}
}
