using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amifolio.Core;
using Microsoft.Data.Sqlite;

// Opt-in copied-source runner only. Never register this benchmark in the normal suite.
// Reuse the original stage9 synthetic seed, including its >2048-character previews,
// rather than substituting a smaller/selective workload. Success is distinct from target attainment.
internal static class Stage9bQueryBenchmark
{
    internal static async Task<int> Run(string fixtureRoot,string reportPath)
    {
        if(Directory.Exists(fixtureRoot)||File.Exists(fixtureRoot)||File.Exists(reportPath))throw new IOException("fresh-benchmark-paths-required");
        Directory.CreateDirectory(fixtureRoot);
        var start=Stopwatch.StartNew();
        var prepare=(Task)typeof(Stage9PerformanceCheck).GetMethod("Prepare",BindingFlags.Static|BindingFlags.NonPublic)!
            .Invoke(null,[fixtureRoot,"large10000",10000])!;
        await prepare;
        var fixture=prepare.GetType().GetProperty("Result")!.GetValue(prepare)!;
        var type=fixture.GetType();
        var store=(PortfolioStore)type.GetProperty("Store")!.GetValue(fixture)!;
        var data=(string)type.GetProperty("Data")!.GetValue(fixture)!;
        var files=(List<string>)type.GetProperty("Files")!.GetValue(fixture)!;
        var registry=(string)type.GetProperty("RegistryPath")!.GetValue(fixture)!;
        var project=(CodexProject)type.GetProperty("Project")!.GetValue(fixture)!;
        var setupMs=start.Elapsed.TotalMilliseconds;
        Console.WriteLine($"10k original synthetic seed ready: {setupMs:F2}ms");
        string Manifest()
        {
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach(var path in files.Append(registry))
            {
                var attributes=File.GetAttributes(path);
                if((attributes&(FileAttributes.Directory|FileAttributes.ReparsePoint|FileAttributes.Device))!=0)throw new IOException("synthetic-file-type-changed");
                var bytes=File.ReadAllBytes(path);
                hash.AppendData(Encoding.UTF8.GetBytes($"{path}|{bytes.Length}|{File.GetLastWriteTimeUtc(path).Ticks}|{(int)attributes}|"));
                hash.AppendData(bytes);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        string Scalar(string sql)
        {
            using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(data,"portfolio.db"),Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
            db.Open();using var c=db.CreateCommand();c.CommandText=sql;return Convert.ToString(c.ExecuteScalar())!;
        }
        var before=Manifest();
        var revisionBefore=Scalar("SELECT value FROM store_meta WHERE key='query_revision'");
        PortfolioStore.ListPerformanceSample? sample=null;store.ListPerformanceObserver=p=>sample=p;
        var firstClock=Stopwatch.StartNew();var first=await store.ListAsync();firstClock.Stop();
        var firstPhase=sample;
        var expectedIds=first.Select(r=>r.Id).ToHashSet(StringComparer.Ordinal);
        if(first.Count!=10000||expectedIds.Count!=10000)throw new Exception("complete-seed-count");
        var operations=new(string Name,string? Search,string? Project,string? Kind,int Expected)[]
        {
            ("all",null,null,null,10000),
            ("title-all","Stage9Title",null,null,10000),
            ("previewtail-all","stage9-preview-tail-needle",null,null,10000),
            ("selective","Stage9Title09999",null,null,1),
            ("no-match","stage9-never-present-needle",null,null,0),
            ("project-kind",null,project.Id,"笔记",3334)
        };
        var measurements=new List<object>();var coldPass=true;var warmPass=true;
        foreach(var mode in new[]{"cold-metadata","warm-metadata"})
        {
            store.QueryMetadataCacheBudget=mode=="cold-metadata"?0:64L*1024*1024;
            // Cold bypasses retention on every query. Warm primes exactly the full 10k set once.
            if(mode=="warm-metadata")await store.ListAsync();
            foreach(var op in operations)
            {
                var times=new List<double>();var counts=new List<int>();var phases=new List<PortfolioStore.ListPerformanceSample>();
                for(var i=0;i<40;i++)
                {
                    sample=null;var sw=Stopwatch.StartNew();var rows=await store.ListAsync(op.Search,op.Project,op.Kind);sw.Stop();
                    times.Add(sw.Elapsed.TotalMilliseconds);counts.Add(rows.Count);phases.Add(sample??throw new Exception("missing-phases"));
                    if(rows.Count!=op.Expected||rows.Select(r=>r.Id).Distinct().Count()!=op.Expected)throw new Exception("count-or-duplicate");
                    if(op.Expected==10000&&!expectedIds.SetEquals(rows.Select(r=>r.Id)))throw new Exception("complete-id-set");
                    if(rows.Any(r=>!r.IsReference||!r.Preview.StartsWith("SYNTHETIC QUERY FIXTURE ONLY",StringComparison.Ordinal)))throw new Exception("source-state-or-preview");
                    if(sample!.MetadataCacheHit!=(mode=="warm-metadata"))throw new Exception("cache-mode-mismatch");
                    if(op.Expected==10000&&sample.EnumeratedDirectories!=100)throw new Exception("fresh-filesystem-directory-checks");
                    if((i+1)%10==0)Console.WriteLine($"{mode} {op.Name} {i+1}/40 {sw.Elapsed.TotalMilliseconds:F2}ms rows={rows.Count}");
                }
                var sorted=times.Order().ToArray();var p95=sorted[37];var pass=p95<=200;
                if(mode=="cold-metadata")coldPass&=pass;else warmPass&=pass;
                measurements.Add(new{mode,op.Name,expectedCount=op.Expected,samplesMs=times,returnedCounts=counts,p50Ms=sorted[19],p95Ms=p95,maxMs=sorted[^1],within200ms=pass,phases});
                Console.WriteLine($"{mode} {op.Name} P95={p95:F2}ms target={pass}");
            }
        }
        var after=Manifest();var sourceUnchanged=before==after;
        var rowsAfter=Scalar("SELECT COUNT(*) FROM artifacts WHERE deleted=0");
        var queryWritesAbsent=Scalar("SELECT value FROM store_meta WHERE key='query_revision'")==revisionBefore;
        if(!sourceUnchanged||rowsAfter!="10000"||!queryWritesAbsent)throw new Exception("read-only-invariant");
        var report=new
        {
            scope="complete original six 10k queries; 40 cold-metadata plus 40 warm-metadata samples per query",
            timing="public ListAsync and await; setup, source hashing, correctness checks and serialization excluded; sequential calls; no forced GC or discarded samples",
            coldBoundary="metadata retention disabled for every sample; same process/OS cache; SQLite connection pooling disabled; NOT cold disk or cold process",
            warmBoundary="one complete 10k metadata prime; source path/scope/volume and filesystem checks rerun every query; no result cache",
            percentile="nearest rank: P50=20th P95=38th max=40th of 40",
            setupMs,firstFullListMs=firstClock.Elapsed.TotalMilliseconds,firstFullListCount=first.Count,firstPhase,
            targetMs=200,coldSixWithin200ms=coldPass,warmSixWithin200ms=warmPass,
            sourceUnchanged,sourceBefore=before,sourceAfter=after,queryWritesAbsent,rowsAfter,
            fixtureRoot,data,registry,cacheBudgetBytes=64L*1024*1024,
            runtime=System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os=System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            logicalProcessors=Environment.ProcessorCount,
            dbPages=Scalar("PRAGMA page_count"),dbPageBytes=Scalar("PRAGMA page_size"),
            elapsedSeconds=start.Elapsed.TotalSeconds,measurements
        };
        await File.WriteAllTextAsync(reportPath,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"COMPLETED invariants=true coldTarget={coldPass} warmTarget={warmPass}");
        return 0;
    }
}
