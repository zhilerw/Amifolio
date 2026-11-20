using System.Reflection;
using Amifolio.Core;

internal static class Stage9bQueryCacheCases
{
    internal static (string Name,Func<Task> Test)[] All =>
    [
        ("metadata reuse remains bounded and never caches source state",FreshSourceState),
        ("raw writers and other stores invalidate all artifact metadata",RawWriters),
        ("restoring the same database path cannot reuse a stale metadata set",RestoredIdentity),
        ("cache revision and query results share one SQLite snapshot",ConcurrentSnapshot),
        ("over-budget metadata falls back without truncation",BudgetFallback)
    ];
    internal static async Task<int> Run(){foreach(var(name,test)in All){await test();Console.WriteLine("PASS "+name);}return 0;}
    private static bool Hit(PortfolioStore.ListPerformanceSample? sample)=>sample?.GetType().GetProperty("MetadataCacheHit")?.GetValue(sample) is true;
    private static long Bytes(PortfolioStore.ListPerformanceSample? sample)=>sample?.GetType().GetProperty("MetadataCacheBytes")?.GetValue(sample) as long? ?? 0;
    private static void Check(bool value,string code){if(!value)throw new Exception(code);}
    private static async Task FreshSourceState()
    {
        var f=await Stage9bQueryCases.Fixture.Create();PortfolioStore.ListPerformanceSample? sample=null;f.Store.ListPerformanceObserver=p=>sample=p;
        await f.Store.ListAsync();Check(!Hit(sample),"cold-metadata-set");
        var original=await File.ReadAllBytesAsync(f.Files[0]);var modified=File.GetLastWriteTimeUtc(f.Files[0]);File.Delete(f.Files[0]);
        var rows=await f.Store.ListAsync();Check(Hit(sample)&&Bytes(sample)>0&&Bytes(sample)<=64L*1024*1024,"bounded-metadata-set-missing");
        Check(rows.Count==12&&rows.Single(r=>r.Id==f.Ids[0]) is{Preview:"",ExtractionStatus:"源文件缺失"},"cache-does-not-hide-deletion");
        await File.WriteAllBytesAsync(f.Files[0],original);File.SetLastWriteTimeUtc(f.Files[0],modified);
        Check((await f.Store.ListAsync()).Single(r=>r.Id==f.Ids[0]).Preview.StartsWith("SYNTHETIC"),"live-missing-state-was-not-cached");
    }
    private static async Task RawWriters()
    {
        var f=await Stage9bQueryCases.Fixture.Create();await f.Store.ListAsync();
        f.Execute("UPDATE artifacts SET title='cross-process',extraction_status='raw-status',version=17 WHERE id=$id",f.Ids[0]);
        Check((await f.Store.ListAsync()).Single(r=>r.Id==f.Ids[0]) is{Title:"cross-process",ExtractionStatus:"raw-status",Version:17},"all-metadata-fields-invalidated");
        var writer=new PortfolioStore(f.Data);writer.EnableCodexReferenceMode(f.Home);await writer.InitializeAsync();
        var extra=Path.Combine(f.Project.RootPath,"another-store.md");await File.WriteAllTextAsync(extra,"SYNTHETIC metadata-only source");
        var inserted=await writer.ReferenceFileAsync(extra,f.Project.Id);
        Check((await f.Store.ListAsync()).Any(r=>r.Id==inserted.Artifact.Id),"another-store-insert-invalidates-cache");
        Check(f.Oracle("cross-process").SequenceEqual((await f.Store.ListAsync("cross-process")).Select(r=>r.Id)),"updated-query-stays-correct");
        f.Execute("UPDATE artifacts SET deleted=1 WHERE id=$id",f.Ids[0]);Check((await f.Store.ListAsync()).Count==12,"cached-tombstone-removed");
    }
    private static async Task RestoredIdentity()
    {
        var f=await Stage9bQueryCases.Fixture.Create();var zip=Path.Combine(f.Root,"cache-backup.zip");
        string Identity(){using var db=f.Open();using var c=db.CreateCommand();c.CommandText="SELECT value FROM store_meta WHERE key='query_database_id'";return (string)c.ExecuteScalar()!;}
        var previousIdentity=Identity();
        await new PortfolioMaintenance(f.Data).BackupAsync(zip);await f.Store.ListAsync();
        f.Execute("UPDATE artifacts SET title='newer-cache-title' WHERE id=$id",f.Ids[0]);await f.Store.ListAsync();
        await new PortfolioMaintenance(f.Data).RestoreAsync(zip);
        Check(Identity()!=previousIdentity,"restore-must-assign-new-query-database-identity");
        var rows=await f.Store.ListAsync();Check(rows.Count==12&&rows.All(r=>r.Preview=="")&&rows.All(r=>r.Title!="newer-cache-title"),"same-path-restore-stale-cache");
    }
    private static async Task ConcurrentSnapshot()
    {
        var f=await Stage9bQueryCases.Fixture.Create();await f.Store.ListAsync();var fired=false;
        StorageFaults.Handler.Value=stage=>
        {
            if(stage!="query-metadata-snapshot"||fired)return;fired=true;
            f.Execute("UPDATE artifacts SET title='writer-after-snapshot' WHERE id=$id",f.Ids[0]);
        };
        try{Check((await f.Store.ListAsync("writer-after-snapshot")).Count==0,"sqlite-snapshot-mixed-revisions");}
        finally{StorageFaults.Handler.Value=null;}
        Check(fired&&(await f.Store.ListAsync("writer-after-snapshot")).Single().Id==f.Ids[0],"snapshot-writer-not-seen-next-query");
    }
    private static async Task BudgetFallback()
    {
        var f=await Stage9bQueryCases.Fixture.Create();
        var budget=typeof(PortfolioStore).GetField("QueryMetadataCacheBudget",BindingFlags.Instance|BindingFlags.NonPublic);
        Check(budget is not null,"bounded-cache-budget-seam-missing");budget!.SetValue(f.Store,1024L);
        PortfolioStore.ListPerformanceSample? sample=null;f.Store.ListPerformanceObserver=p=>sample=p;
        Check((await f.Store.ListAsync()).Count==12&&(await f.Store.ListAsync()).Count==12&&!Hit(sample)&&Bytes(sample)==0,"over-budget-query-incomplete-or-retained");
    }
}
