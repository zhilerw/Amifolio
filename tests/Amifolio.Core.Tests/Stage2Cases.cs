using System.IO.Compression;
using System.Reflection;
using System.Text.Json.Nodes;
using Amifolio.Core;
using Microsoft.Data.Sqlite;

internal static class Stage2Cases
{
    internal static readonly (string Name, Func<Fixture, Task> Run)[] All =
    [
        ("stage2 editing a generated context archive does not remove it",async f=>{
            var item=await f.Store.SaveGeneratedAsync("context-summary.md","user archived summary");
            await f.Store.UpdateArtifactAsync(item.Artifact.Id,"renamed summary","manual tag",null);
            Check((await f.Store.GetAsync(item.Artifact.Id))?.Title=="renamed summary","editing generated archive removed it");
        }),
        ("stage2 existing truncated database never resets library", async f=>{
            var artifact=await f.Store.ImportFileAsync(f.File("keep-truncated.md","keep bytes"));
            await System.IO.File.WriteAllBytesAsync(Path.Combine(f.DataRoot,"portfolio.db"),[]);
            await Refuses(()=>f.Restart().InitializeAsync());
            Check(new FileInfo(Path.Combine(f.DataRoot,"portfolio.db")).Length==0 && System.IO.File.Exists(artifact.Artifact.ObjectPath),"truncated database silently replaced");
        }),
        ("stage2 restore refuses active sessions without affecting backup", async f => {
            var zip=Path.Combine(f.Root,"active.zip");
            await using(var session=await StorageLease.SessionAsync(f.DataRoot))
            {await Call(f.DataRoot,"BackupAsync",zip,CancellationToken.None);await Refuses(()=>Call(f.DataRoot,"RestoreAsync",zip,CancellationToken.None));}
            Check(System.IO.File.Exists(zip),"running backup missing");
        }),
        ("stage2 interrupted directory exchange restores previous library", async f => {
            var item=await f.Store.ImportFileAsync(f.File("keep.md","old library"));
            var old=f.DataRoot+".before-restore-"+Guid.NewGuid().ToString("N");var staging=f.DataRoot+".restore-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(staging);
            await System.IO.File.WriteAllTextAsync(f.DataRoot+".restore.json",System.Text.Json.JsonSerializer.Serialize(new{Staging=staging,Previous=old,HadPrevious=true}));
            Directory.Move(f.DataRoot,old);
            Check(await f.Restart().GetAsync(item.Artifact.Id) is not null,"interrupted exchange lost previous library");
        }),
        ("stage2 backup cancellation leaves no acknowledged archive", async f => {
            using var cts=new CancellationTokenSource();cts.Cancel();var zip=Path.Combine(f.Root,"cancel.zip");
            try{await Call(f.DataRoot,"BackupAsync",zip,cts.Token);}catch(OperationCanceledException){}
            Check(!System.IO.File.Exists(zip),"cancelled backup acknowledged");
        }),
        ("stage2 orphan collection keeps fresh and referenced objects", async f => {
            var live=await f.Store.ImportFileAsync(f.File("README","valid extensionless"));
            var orphan=Path.Combine(f.DataRoot,"objects",Guid.NewGuid().ToString("N")+".partial");await System.IO.File.WriteAllTextAsync(orphan,"abandoned");System.IO.File.SetLastWriteTimeUtc(orphan,DateTime.UtcNow.AddDays(-2));
            var fresh=Path.Combine(f.DataRoot,"objects",Guid.NewGuid().ToString("N")+".partial");await System.IO.File.WriteAllTextAsync(fresh,"in flight");
            await Call(f.DataRoot,"CollectAsync",TimeSpan.Zero,CancellationToken.None);
            Check(!System.IO.File.Exists(orphan)&&System.IO.File.Exists(fresh)&&System.IO.File.Exists(live.Artifact.ObjectPath),"orphan collection unsafe");
            var zip=Path.Combine(f.Root,"no-extension.zip");await Call(f.DataRoot,"BackupAsync",zip,CancellationToken.None);
        }),
        ("stage2 legacy upgrade records version and recovery point", async f => {
            var saved=await f.Store.ImportFileAsync(f.File("old.md","old bytes"));
            using(var db=Open(f.DataRoot)){Sql(db,"PRAGMA user_version=0");}
            await f.Restart().InitializeAsync();
            using(var db=Open(f.DataRoot)){Check(Convert.ToInt32(Scalar(db,"PRAGMA user_version"))==StoreSchema.Version,"schema version missing");}
            Check(Directory.GetFiles(Path.Combine(f.DataRoot,"recovery"),"*.db",SearchOption.AllDirectories).Length>0,"pre-upgrade snapshot missing");
            Check((await f.Restart().GetAsync(saved.Artifact.Id)) is not null,"upgrade lost committed artifact");
        }),
        ("stage2 future database is refused without data rewrite", async f => {
            using(var db=Open(f.DataRoot)){Sql(db,"PRAGMA user_version=999");}
            await Refuses(()=>f.Restart().InitializeAsync());
            using var after=Open(f.DataRoot);Check(Convert.ToInt32(Scalar(after,"PRAGMA user_version"))==999,"future schema rewritten");
        }),
        ("stage2 deletion revokes old generations and candidates but retains manual content", async f => {
            var item=await f.Store.ImportFileAsync(f.File("source.md","private source"));
            var state=Path.Combine(f.DataRoot,"context-state","global");Directory.CreateDirectory(state);
            await System.IO.File.WriteAllTextAsync(Path.Combine(state,"state.json"),"{\"Enabled\":true,\"Revision\":3,\"Documents\":{\"memory.md\":\"manual fact\",\"summary.md\":\"old derived\"},\"Manual\":[\"memory.md\"],\"Candidates\":{\"candidate-memory.md\":\"old guess\"}}");
            var export=Path.Combine(f.DataRoot,"context","global","generations","old");Directory.CreateDirectory(export);await System.IO.File.WriteAllTextAsync(Path.Combine(export,"summary.md"),"old derived");
            var derived=await f.Store.SaveGeneratedAsync("context-summary.md","old derived");
            await f.Store.RemoveArtifactAsync(item.Artifact.Id);
            Check(!Directory.Exists(Path.Combine(f.DataRoot,"context","global")),"old published bytes remain");
            var json=JsonNode.Parse(await System.IO.File.ReadAllTextAsync(Path.Combine(state,"state.json")))!;
            Check(json["Documents"]!["memory.md"]!.GetValue<string>()=="manual fact","manual memory lost");
            Check(json["Candidates"]!.AsObject().Count==0,"stale candidate retained");
            Check(await f.Store.GetAsync(derived.Artifact.Id) is null,"application context archive remains visible");
            Check(System.IO.File.Exists(item.Artifact.ObjectPath),"removal unexpectedly reclaimed archive bytes");
        }),
        ("stage2 backup restore verifies bytes and disables sharing", async f => {
            var p=await f.Store.CreateProjectAsync("备份项目");var item=await f.Store.ImportFileAsync(f.File("backup.md","durable"),p.Id);
            var state=Path.Combine(f.DataRoot,"context-state","global");Directory.CreateDirectory(state);
            await System.IO.File.WriteAllTextAsync(Path.Combine(state,"state.json"),"{\"Enabled\":true,\"OriginalAccessAllowed\":true,\"Documents\":{\"memory.md\":\"manual\"},\"Manual\":[\"memory.md\"],\"Deleted\":[\"style.md\"]}");
            var settings=Path.Combine(f.DataRoot,"settings");Directory.CreateDirectory(settings);await System.IO.File.WriteAllTextAsync(Path.Combine(settings,"ai.json"),"{\"Endpoint\":\"https://example.com/v1\",\"Model\":\"test\",\"RemoteAllowed\":true,\"ProtectedApiKey\":\"CIPHERTEXT_NOT_FOR_BACKUP\"}");
            var zip=Path.Combine(f.Root,"backup.zip");await Call(f.DataRoot,"BackupAsync",zip,CancellationToken.None);
            using(var archive=ZipFile.OpenRead(zip)){var entry=archive.GetEntry("settings/ai.json")!;using var reader=new StreamReader(entry.Open());var text=await reader.ReadToEndAsync();Check(!text.Contains("CIPHERTEXT_NOT_FOR_BACKUP"),"backup contains credential");}
            var restored=Path.Combine(f.Root,"restored");await Call(restored,"RestoreAsync",zip,CancellationToken.None);
            var store=new PortfolioStore(restored);await store.InitializeAsync();Check((await store.GetAsync(item.Artifact.Id))?.ProjectId==p.Id,"restored metadata differs");
            Check(await System.IO.File.ReadAllTextAsync(await store.GetOriginalPathAsync(item.Artifact.Id))=="durable","restored original differs");
            var json=JsonNode.Parse(await System.IO.File.ReadAllTextAsync(Path.Combine(restored,"context-state","global","state.json")))!;
            Check(json["Enabled"]!.GetValue<bool>()==false && json["OriginalAccessAllowed"]!.GetValue<bool>()==false,"restore reenabled sharing");
            Check(json["Documents"]!["memory.md"]!.GetValue<string>()=="manual" && json["Deleted"]!.AsArray().Any(x=>x!.GetValue<string>()=="style.md"),"restore lost manual/tombstone state");
        }),
        ("stage2 tampered backup never replaces existing library", async f => {
            var item=await f.Store.ImportFileAsync(f.File("keep.md","keep me"));var zip=Path.Combine(f.Root,"bad.zip");await Call(f.DataRoot,"BackupAsync",zip,CancellationToken.None);
            using(var archive=ZipFile.Open(zip,ZipArchiveMode.Update)){var entry=archive.GetEntry("portfolio.db")!;entry.Delete();using var w=new StreamWriter(archive.CreateEntry("portfolio.db").Open());w.Write("tampered");}
            await Refuses(()=>Call(f.DataRoot,"RestoreAsync",zip,CancellationToken.None));Check((await f.Restart().GetAsync(item.Artifact.Id)) is not null,"failed restore changed library");
        }),
        ("stage2 zip traversal refused without writing outside restore root", async f => {
            var zip=f.Zip("escape.zip",("../escaped.txt","bad"),("manifest.json","{}"));var target=Path.Combine(f.Root,"target");await Refuses(()=>Call(target,"RestoreAsync",zip,CancellationToken.None));Check(!System.IO.File.Exists(Path.Combine(f.Root,"escaped.txt")),"zip escaped");
        }),
        ("stage2 reclaim removes retired bytes but preserves live archive and source", async f => {
            var source=f.File("deleted.md","retired");var a=await f.Store.ImportFileAsync(source);var live=await f.Store.ImportFileAsync(f.File("live.md","live"));await f.Store.RemoveArtifactAsync(a.Artifact.Id);
            await Call(f.DataRoot,"CollectAsync",TimeSpan.Zero,CancellationToken.None);
            Check(!System.IO.File.Exists(a.Artifact.ObjectPath),"retired object not reclaimed");Check(System.IO.File.Exists(live.Artifact.ObjectPath)&&System.IO.File.Exists(source),"collection deleted live/source file");
        }),
    ];
    private static object Maintenance(string root)=>Activator.CreateInstance(typeof(PortfolioStore).Assembly.GetType("Amifolio.Core.PortfolioMaintenance") ?? throw new Exception("maintenance API missing"),root)!;
    private static async Task Call(string root,string name,params object[] args){var o=Maintenance(root);var m=o.GetType().GetMethod(name)??throw new Exception(name+" missing");try{await (Task)m.Invoke(o,args)!;}catch(TargetInvocationException e){throw e.InnerException!;}}
    private static async Task Refuses(Func<Task> action){try{await action();}catch(Exception e) when(e is IOException or InvalidDataException or InvalidOperationException or NotSupportedException){return;}throw new Exception("operation unexpectedly succeeded");}
    private static SqliteConnection Open(string root){var db=new SqliteConnection("Data Source="+Path.Combine(root,"portfolio.db"));db.Open();return db;}
    private static void Sql(SqliteConnection db,string text){using var c=db.CreateCommand();c.CommandText=text;c.ExecuteNonQuery();}
    private static object? Scalar(SqliteConnection db,string text){using var c=db.CreateCommand();c.CommandText=text;return c.ExecuteScalar();}
    private static void Check(bool pass,string message){if(!pass)throw new Exception(message);}
}
