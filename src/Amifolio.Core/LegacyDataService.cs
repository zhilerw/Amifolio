using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Amifolio.Core;

public sealed record LegacyMigrationResult(string Status,string? HistoryDirectory,int DirectoriesMoved);
public sealed record LegacyDocument(string Scope,string Name,string Text,string Archive);

/// <summary>Retire application-owned legacy data by same-volume rename, never delete originals.</summary>
public sealed class LegacyDataService(string dataRoot)
{
    private readonly string root=ArchiveSafety.LocalPath(dataRoot);
    private static readonly string[] Names=["objects","context-state","preference-state","recovery","context"];
    internal Action<string>? AfterMove{get;init;}
    private sealed record Journal(int Version,string Archive,string[] Names);
    public Task<LegacyMigrationResult> MigrateAsync(CancellationToken ct=default)=>Task.Run(async()=>
    {
        ct.ThrowIfCancellationRequested();ArchiveSafety.NoReparse(root);Directory.CreateDirectory(root);
        await using var lease=await StorageLease.ExclusiveAsync(root,ct).ConfigureAwait(false);
        var pending=Path.Combine(root,"legacy-migration.json");var marker=Path.Combine(root,"storage-layout.json");
        ArchiveSafety.NoReparse(pending);ArchiveSafety.NoReparse(marker);
        if(File.Exists(marker)&&!File.Exists(pending))
        {
            if(new FileInfo(marker).Length>4096)throw new IOException("数据目录版本无效，未整理。");
            using var doc=JsonDocument.Parse(await File.ReadAllBytesAsync(marker,ct).ConfigureAwait(false));
            if(doc.RootElement.GetProperty("version").GetInt32()!=2)throw new IOException("数据目录版本不受支持，未整理。");
            if(doc.RootElement.TryGetProperty("history",out var saved))
            {
                if(saved.ValueKind!=JsonValueKind.String||saved.GetString() is not{} name||!Regex.IsMatch(name,"^legacy-[a-f0-9]{32}$"))throw new IOException("历史目录标记无效，未整理。");
                var kept=Path.Combine(root,"history",name);ArchiveSafety.NoReparse(kept);ArchiveSafety.NoReparse(Path.Combine(kept,"manifest.json"));
                if(!Directory.Exists(kept)||!File.Exists(Path.Combine(kept,"manifest.json")))throw new IOException("已保留的历史目录缺失，请检查备份。");
            }
            if(Names.Where(n=>n!="context").Any(n=>Directory.Exists(Path.Combine(root,n))))throw new IOException("旧版进程重新创建了资料；历史已保留，请退出旧版后检查。");
            return new LegacyMigrationResult("already",null,0);
        }
        Journal plan;
        if(File.Exists(pending))
        {
            if(new FileInfo(pending).Length>4096)throw new IOException("迁移日志无效。");
            plan=JsonSerializer.Deserialize<Journal>(await File.ReadAllBytesAsync(pending,ct).ConfigureAwait(false))??throw new IOException("迁移日志无效。");
            if(plan.Version!=1||plan.Archive is null||!Regex.IsMatch(plan.Archive,"^legacy-[a-f0-9]{32}$")||plan.Names is null||plan.Names.Any(n=>!Names.Contains(n,StringComparer.Ordinal))||plan.Names.Distinct().Count()!=plan.Names.Length)throw new IOException("迁移日志越界，未整理。");
        }
        else
        {
            var existing=Names.Where(n=>Directory.Exists(Path.Combine(root,n))).ToArray();
            // Refuse links anywhere before moving even the first folder.
            foreach(var name in existing)ValidateTree(Path.Combine(root,name),ct);
            plan=new(1,"legacy-"+Guid.NewGuid().ToString("N"),existing);
            if(existing.Length==0){StorageFiles.Write(marker,JsonSerializer.SerializeToUtf8Bytes(new{version=2}));return new("none",null,0);}
            StorageFiles.Write(pending,JsonSerializer.SerializeToUtf8Bytes(plan));
        }
        var archive=Path.Combine(root,"history",plan.Archive);ArchiveSafety.NoReparse(archive);Directory.CreateDirectory(archive);
        // A crashed migration may not overwrite material recreated by an older process.
        foreach(var name in plan.Names)
            if(Directory.Exists(Path.Combine(root,name))&&Directory.Exists(Path.Combine(archive,name)))throw new IOException("旧版进程重新创建了资料；历史已保留，请退出旧版后检查。 ");
        var db=Path.Combine(root,"portfolio.db");var snapshot=Path.Combine(archive,"portfolio.db");
        ArchiveSafety.NoReparse(db);ArchiveSafety.NoReparse(snapshot);
        if(File.Exists(db)&&!File.Exists(snapshot))
        {
            var temporary=snapshot+".partial";ArchiveSafety.NoReparse(temporary);
            // Only this exact journal-owned incomplete snapshot is disposable.
            if(File.Exists(temporary))File.Delete(temporary);
            using(var from=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=db,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
            using(var to=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=temporary,Pooling=false}.ToString()))
            {from.Open();to.Open();from.BackupDatabase(to);}
            File.Move(temporary,snapshot,false);
        }
        var moved=0;
        foreach(var name in plan.Names)
        {
            ct.ThrowIfCancellationRequested();var source=Path.Combine(root,name);var target=Path.Combine(archive,name);
            if(!Directory.Exists(source))
            {if(!Directory.Exists(target))throw new IOException("待迁移资料缺失，历史未覆盖。");ValidateTree(target,ct);continue;}
            ValidateTree(source,ct);ArchiveSafety.NoReparse(target);
            Directory.Move(source,target);moved++;AfterMove?.Invoke(name);
        }
        ct.ThrowIfCancellationRequested();
        if(File.Exists(db))
        {
            using var current=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=db,Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());current.Open();
            using var tx=current.BeginTransaction();using var command=current.CreateCommand();command.Transaction=tx;
            command.CommandText="""
                UPDATE work_items SET state='cancelled',owner=NULL,error_code='legacy_retired'
                WHERE (artifact_id IN(SELECT id FROM artifacts WHERE object_name NOT LIKE 'ref:%') OR kind='generate') AND state IN('queued','running');
                DELETE FROM artifact_origins WHERE artifact_id IN(SELECT id FROM artifacts WHERE object_name NOT LIKE 'ref:%');
                DELETE FROM artifacts WHERE object_name NOT LIKE 'ref:%';
                """;
            command.ExecuteNonQuery();ct.ThrowIfCancellationRequested();tx.Commit();
        }
        StorageFiles.Write(Path.Combine(archive,"manifest.json"),JsonSerializer.SerializeToUtf8Bytes(new{version=1,created=DateTimeOffset.UtcNow,directories=plan.Names,indexSnapshot=File.Exists(snapshot)}));
        StorageFiles.Write(marker,JsonSerializer.SerializeToUtf8Bytes(new{version=2,history=plan.Archive}));File.Delete(pending);
        return new LegacyMigrationResult("migrated",archive,moved);
    },ct);

    public Task<IReadOnlyList<LegacyDocument>> ReadHistoryAsync(CancellationToken ct=default)=>Task.Run<IReadOnlyList<LegacyDocument>>(()=>
    {
        var result=new List<LegacyDocument>();long inputBytes=0;var history=Path.Combine(root,"history");ArchiveSafety.NoReparse(history);
        if(!Directory.Exists(history))return result;
        foreach(var archive in Directory.EnumerateDirectories(history).Take(32))
        {
            ct.ThrowIfCancellationRequested();ArchiveSafety.NoReparse(archive);
            if(!Regex.IsMatch(Path.GetFileName(archive),"^legacy-[a-f0-9]{32}$"))continue;
            var states=Path.Combine(archive,"context-state");ArchiveSafety.NoReparse(states);
            if(Directory.Exists(states))foreach(var scope in Directory.EnumerateDirectories(states).Take(512))
            {
                var path=Path.Combine(scope,"state.json");using var doc=Read(path);if(doc is null)continue;
                foreach(var key in new[]{"Documents","Candidates","ExternalEdits"})
                    if(doc.RootElement.TryGetProperty(key,out var entries)&&entries.ValueKind==JsonValueKind.Object)
                        foreach(var item in entries.EnumerateObject())
                        {
                            if(item.Value.ValueKind!=JsonValueKind.String)continue;
                            var value=item.Value.GetString()!;result.Add(new(Path.GetFileName(scope),key+" / "+item.Name,value[..Math.Min(value.Length,32000)],Path.GetFileName(archive)));
                            if(result.Count>=512)return result;
                        }
            }
            using var prefs=Read(Path.Combine(archive,"preference-state","state.json"));
            if(prefs is not null)
                foreach(var property in prefs.RootElement.EnumerateObject())
                    if(property.Name.Equals("Entries",StringComparison.OrdinalIgnoreCase)&&property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                        foreach(var row in property.Value.ValueKind==JsonValueKind.Array?property.Value.EnumerateArray().ToArray():property.Value.EnumerateObject().Select(p=>p.Value).ToArray())
                        {
                            if(row.ValueKind!=JsonValueKind.Object)continue;
                            foreach(var field in row.EnumerateObject())
                                if(field.Name.Equals("Text",StringComparison.OrdinalIgnoreCase)&&field.Value.ValueKind==JsonValueKind.String)
                                {var text=field.Value.GetString()!;result.Add(new("global","旧通用偏好",text[..Math.Min(text.Length,32000)],Path.GetFileName(archive)));if(result.Count>=512)return result;}
                        }
        }
        return result;
        JsonDocument? Read(string path)
        {
            ct.ThrowIfCancellationRequested();ArchiveSafety.NoReparse(path);
            if(!File.Exists(path))return null;if(new FileInfo(path).Length>2*1024*1024)throw new IOException("历史文档超过查看上限。");
            inputBytes+=new FileInfo(path).Length;if(inputBytes>8*1024*1024)throw new IOException("历史资料超过单次查看上限，请在数据目录查看。");
            return JsonDocument.Parse(File.ReadAllBytes(path),new JsonDocumentOptions{MaxDepth=32});
        }
    },ct);

    private static void ValidateTree(string path,CancellationToken ct)
    {
        var stack=new Stack<string>();stack.Push(path);var count=0;
        while(stack.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();ArchiveSafety.NoReparse(directory);
            foreach(var entry in Directory.EnumerateFileSystemEntries(directory))
            {if(++count>20000)throw new IOException("旧资料数量超过单次整理上限，未删除。");ArchiveSafety.NoReparse(entry);if(Directory.Exists(entry))stack.Push(entry);}
        }
    }
}
