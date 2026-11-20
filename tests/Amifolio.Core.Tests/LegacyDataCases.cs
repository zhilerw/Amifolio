using Amifolio.Core;
using System.Text.Json;

internal static class LegacyDataCases
{
    internal static (string,Func<Fixture,Task>)[] All=>
    [
        ("legacy retirement preserves current state and moves old data to readable history",async f=>
        {
            await f.Store.InitializeAsync();
            var old=f.File("data/context-state/global/state.json","{\"Documents\":{\"style.md\":\"retained manual style\"}}");
            f.File("data/objects/old.txt","old original copy");f.File("data/preference-state/state.json","{\"Entries\":[{\"Text\":\"old preference\"}]}");
            var key=f.File("data/settings/ai.json","opaque encrypted setting");var cache=f.File("data/browser-index.json","last project");
            var result=await new LegacyDataService(f.DataRoot).MigrateAsync();
            Check(result.Status=="migrated"&&result.HistoryDirectory is not null,"migration not recorded");
            Check(!File.Exists(old)&&await File.ReadAllTextAsync(Path.Combine(result.HistoryDirectory!,"objects/old.txt"))=="old original copy","old bytes lost");
            Check(await File.ReadAllTextAsync(key)=="opaque encrypted setting"&&await File.ReadAllTextAsync(cache)=="last project","current setting/cache changed");
            Check(File.Exists(Path.Combine(result.HistoryDirectory!,"portfolio.db")),"coherent old index snapshot missing");
            var docs=await new LegacyDataService(f.DataRoot).ReadHistoryAsync();Check(docs.Any(d=>d.Text=="retained manual style")&&docs.Any(d=>d.Text=="old preference"),"history not readable");
        }),
        ("legacy retirement interrupted rename resumes without overwriting history",async f=>
        {
            await f.Store.InitializeAsync();f.File("data/objects/a","A");f.File("data/context-state/global/state.json","{}");
            var service=new LegacyDataService(f.DataRoot){AfterMove=_=>throw new IOException("synthetic interruption")};
            await Throws(()=>service.MigrateAsync());
            var result=await new LegacyDataService(f.DataRoot).MigrateAsync();
            Check(File.Exists(Path.Combine(result.HistoryDirectory!,"objects/a"))&&!File.Exists(Path.Combine(f.DataRoot,"legacy-migration.json")),"recovery lost archive or journal");
        }),
        ("legacy retirement refuses recreated sources and unknown journals",async f=>
        {
            await f.Store.InitializeAsync();f.File("data/objects/a","A");
            await Throws(()=>new LegacyDataService(f.DataRoot){AfterMove=_=>throw new IOException("stop")}.MigrateAsync());
            f.File("data/objects/b","B");await Throws(()=>new LegacyDataService(f.DataRoot).MigrateAsync());
            Check(await File.ReadAllTextAsync(Path.Combine(f.DataRoot,"objects/b"))=="B","newer old-writer output destroyed");
        }),
        ("legacy retirement canceled request never moves user state",async f=>
        {
            var path=f.File("data/objects/a","A");using var ct=new CancellationTokenSource();ct.Cancel();
            try{await new LegacyDataService(f.DataRoot).MigrateAsync(ct.Token);throw new Exception("not cancelled");}catch(OperationCanceledException){}
            Check(File.Exists(path),"cancelled migration moved files");
        }),
        ("legacy retirement preserves unknown files and learning deletion ledger",async f=>
        {
            await f.Store.InitializeAsync();var learning=f.File("data/learning/state.sqlite","owned state untouched");f.File("data/custom-notes.txt","user file");
            await new LegacyDataService(f.DataRoot).MigrateAsync();
            Check(await File.ReadAllTextAsync(learning)=="owned state untouched"&&File.Exists(Path.Combine(f.DataRoot,"custom-notes.txt")),"unknown or learning data changed");
        }),
        ("legacy retirement unknown marker and traversal journal refuse before moves",async f=>
        {
            await f.Store.InitializeAsync();var objectFile=f.File("data/objects/keep","keep");
            var marker=f.File("data/storage-layout.json","{\"version\":99}");await Throws(()=>new LegacyDataService(f.DataRoot).MigrateAsync());
            File.Delete(marker);f.File("data/legacy-migration.json","{\"Version\":1,\"Archive\":\"../escape\",\"Names\":[\"objects\"]}");
            await Throws(()=>new LegacyDataService(f.DataRoot).MigrateAsync());Check(File.Exists(objectFile),"bad journal moved original data");
        }),
        ("legacy retirement completed marker detects an old writer without overwriting history",async f=>
        {
            await f.Store.InitializeAsync();f.File("data/objects/first","first");var service=new LegacyDataService(f.DataRoot);var result=await service.MigrateAsync();
            f.File("data/objects/recreated","second");await Throws(()=>service.MigrateAsync());
            Check(File.ReadAllText(Path.Combine(result.HistoryDirectory!,"objects/first"))=="first"&&File.ReadAllText(Path.Combine(f.DataRoot,"objects/recreated"))=="second","conflict overwrote data");
        }),
        ("legacy retirement missing completed history is not reported as successful",async f=>
        {
            await f.Store.InitializeAsync();var service=new LegacyDataService(f.DataRoot);var moved=await service.MigrateAsync();
            Directory.Move(moved.HistoryDirectory!,moved.HistoryDirectory!+"-moved");await Throws(()=>service.MigrateAsync());
        }),
        ("legacy upgrade refuses aliased report backup and opaque rollback outputs",async f=>
        {
            var backup=Path.Combine(f.Root,"recovery.zip");
            await Throws(()=>{Stage9Upgrade.ValidateOutputs(f.DataRoot,backup,backup);return Task.CompletedTask;});
            await Throws(()=>{Stage9Upgrade.ValidateOutputs(f.DataRoot,backup,backup+".local-key");return Task.CompletedTask;});
            Check(!File.Exists(backup)&&!File.Exists(backup+".local-key"),"output validation wrote a file");
        }),
        ("legacy retirement reads dictionary preference history and repeated startup creates no objects",async f=>
        {
            await f.Store.InitializeAsync();f.File("data/preference-state/state.json","{\"Entries\":{\"one\":{\"Text\":\"retained dictionary preference\"}}}");
            var service=new LegacyDataService(f.DataRoot);await service.MigrateAsync();await new PortfolioStore(f.DataRoot).InitializeAsync();
            Check(!Directory.Exists(Path.Combine(f.DataRoot,"objects")),"old object folder recreated");
            Check((await service.ReadHistoryAsync()).Any(x=>x.Text=="retained dictionary preference"),"dictionary data inaccessible");
        })
    ];
    private static void Check(bool value,string message){if(!value)throw new Exception(message);}
    private static async Task Throws(Func<Task> run){try{await run();}catch(IOException){return;}throw new Exception("unsafe migration accepted");}
}
