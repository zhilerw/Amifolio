using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amifolio.Core;
using Microsoft.Data.Sqlite;

/// <summary>Independent, synthetic-only regression checks; no parent Program registration here.</summary>
internal static class Stage9PerformanceLogicCases
{
    internal static (string Name, Func<Task> Test)[] All =>
    [
        ("deep cached paths cannot recurse or enumerate", DeepCachedPaths),
        ("polluted references cannot enumerate outside or excluded paths", PollutedPaths),
        ("list validates a local volume once per query while checking every path", QueryPathValidation),
        ("list batches metadata without a stale cross-query cache", BatchAndRefresh),
        ("list missing state is read-only under a shared publication lease", MissingUnderLease),
        ("detail still hashes equal-size equal-mtime replacements", DetailHash),
        ("list rejects replaced parent junction and symlink leaf", Reparse),
        ("directory pin blocks replacement but permits source-file edits", DirectoryPin),
        ("filesystem phase observes cancellation", Cancellation),
        ("list preserves filtering ordering preview-tail search and full counts", QuerySemantics),
        ("read-only initialize and existing layout do not recreate objects", InitializeLayout)
    ];

    private static async Task DeepCachedPaths()
    {
        var f=await Fixture.Create(8,8);
        var deep=Path.Combine(f.Project.RootPath,string.Join(Path.DirectorySeparatorChar,Enumerable.Repeat("x",10000)),"missing");
        var pinsType=typeof(PortfolioStore).GetNestedType("ReferenceDirectoryPins",System.Reflection.BindingFlags.NonPublic)!;
        using(var pins=(IDisposable)Activator.CreateInstance(pinsType,true)!)
            Check(!(bool)pinsType.GetMethod("TryPin",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
                .Invoke(pins,[deep])!,"deep-direct-pin-rejected-without-recursion");
        using(var db=f.Open())using(var cmd=db.CreateCommand())
        {
            cmd.CommandText="UPDATE artifacts SET object_name='ref:' || $root || '/' || original_name";
            cmd.Parameters.AddWithValue("$root",deep);cmd.ExecuteNonQuery();
        }
        PortfolioStore.ListPerformanceSample? sample=null;f.Store.ListPerformanceObserver=value=>sample=value;
        var rows=await f.Store.ListAsync();
        Check(rows.Count==8&&rows.All(r=>r.Preview==""&&r.ExtractionStatus=="源文件不可读"),"deep-public-list-fails-closed");
        Check(sample is{EnumeratedDirectories:0,IndividualFileReads:0,PinnedDirectories:0},"deep-path-has-no-filesystem-work");
    }

    private static async Task PollutedPaths()
    {
        foreach(var kind in new[]{"outside","excluded-directory","excluded-leaf"})
        {
            var f=await Fixture.Create(8,8);
            var directory=kind=="outside"?Path.Combine(f.Root,"outside"):
                Path.Combine(f.Project.RootPath,kind=="excluded-directory"?".ssh":"allowed");
            Directory.CreateDirectory(directory);
            for(var i=0;i<f.Files.Length;i++)
            {
                var path=Path.Combine(directory,kind=="excluded-leaf"?$"auth.{i}.md":Path.GetFileName(f.Files[i]));
                await File.WriteAllTextAsync(path,"original\n",new UTF8Encoding(false));
                File.SetLastWriteTimeUtc(path,File.GetLastWriteTimeUtc(f.Files[i]));
                var rejected=false;
                try{await f.Store.Catalog.ValidateFileAsync(f.Project,path);}catch(IOException){rejected=true;}
                Check(rejected,"catalog-rejects-polluted-path");
                using var db=f.Open();using var cmd=db.CreateCommand();
                cmd.CommandText="UPDATE artifacts SET object_name=$path WHERE id=$id";
                cmd.Parameters.AddWithValue("$path","ref:"+path);cmd.Parameters.AddWithValue("$id",f.Ids[i]);cmd.ExecuteNonQuery();
            }
            PortfolioStore.ListPerformanceSample? sample=null;f.Store.ListPerformanceObserver=value=>sample=value;
            var rows=await f.Store.ListAsync();
            Check(rows.Count==8&&rows.All(r=>r.Preview==""&&r.ExtractionStatus=="源文件不可读"),"polluted-preview-cleared");
            Check(sample is{EnumeratedDirectories:0,IndividualFileReads:0,PinnedDirectories:0},"catalog-rejected-path-never-enumerated-or-statted");
        }
    }

    private static async Task QueryPathValidation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var f = await Fixture.Create(16, 8);
        PortfolioStore.ListPerformanceSample? sample = null;
        f.Store.ListPerformanceObserver = value => sample = value;
        await f.Store.ListAsync();
        Check(sample is { ValidatedVolumes: 1, MetadataRows: 16 }, "one-volume-validation-per-query");
        sample = null;
        await f.Store.ListAsync();
        Check(sample is { ValidatedVolumes: 1 }, "volume-validation-not-cached-across-queries");
        using var db = f.Open(); using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE artifacts SET object_name=$path WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", f.Ids[0]);
        cmd.Parameters.AddWithValue("$path", "ref:" + f.Files[0] + ":stream");
        cmd.ExecuteNonQuery();
        try { await f.Store.ListAsync(); }
        catch (IOException) { return; }
        throw new Exception("ads-path-not-rejected-per-row");
    }

    internal static async Task<int> Run()
    {
        foreach (var (name, test) in All)
        {
            await test();
            Console.WriteLine("PASS " + name);
        }
        return 0;
    }

    private static async Task BatchAndRefresh()
    {
        var f = await Fixture.Create(120, 40);
        PortfolioStore.ListPerformanceSample? diagnostic = null;
        f.Store.ListPerformanceObserver = value => diagnostic = value;
        var rows = await f.Store.ListAsync();
        Check(rows.Count == 120 && rows.All(r => r.Preview == Fixture.Preview), "all-real-rows");
        if (OperatingSystem.IsWindows())
            Check(diagnostic is { EnumeratedDirectories: 3, IndividualFileReads: 0 } && diagnostic.PinnedDirectories >= 3,
                "metadata-bounded-by-directories-not-files");
        File.Delete(f.Files[0]);
        await File.WriteAllTextAsync(f.Files[1], "different and longer source content");
        File.SetLastWriteTimeUtc(f.Files[2], File.GetLastWriteTimeUtc(f.Files[2]).AddMinutes(1));
        var live = await f.Store.ListAsync();
        Check(live.Count == 120, "missing-row-is-not-dropped");
        Check(live.Single(r => r.ObjectPath == f.Files[0]) is { ExtractionStatus: "源文件缺失", Preview: "" }, "missing-is-fresh");
        Check(live.Single(r => r.ObjectPath == f.Files[1]).ExtractionStatus.Contains("变化"), "length-change-is-fresh");
        Check(live.Single(r => r.ObjectPath == f.Files[2]).ExtractionStatus.Contains("变化"), "mtime-change-is-fresh");
        Check(live.Where(r => r.Preview == Fixture.Preview).Count() == 117, "other-rows-intact");
    }

    private static async Task MissingUnderLease()
    {
        var f = await Fixture.Create(4, 2);
        File.Delete(f.Files[0]);
        var before = await File.ReadAllBytesAsync(Path.Combine(f.Data, "portfolio.db"));
        await using (var lease = await StorageLease.SharedAsync(f.Data))
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var rows = await f.Store.ListAsync(cancellationToken: timeout.Token);
            Check(rows.Count == 4 && rows.Single(r => r.ObjectPath == f.Files[0]).Preview == "", "shared-lease-missing");
        }
        var after = await File.ReadAllBytesAsync(Path.Combine(f.Data, "portfolio.db"));
        Check(before.SequenceEqual(after), "list-does-not-reconcile-or-write");
        await f.Store.RefreshSourceStatesAsync();
        var item = await f.Store.GetAsync(f.Ids[0]);
        Check(item is { ExtractionStatus: "源文件缺失", Preview: "", Hash: "" } && item.Version == 2, "explicit-refresh-persists-invalidation");
    }

    private static async Task DetailHash()
    {
        var f = await Fixture.Create(1, 1);
        var time = File.GetLastWriteTimeUtc(f.Files[0]);
        await File.WriteAllTextAsync(f.Files[0], "CHANGED!\n", new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(f.Files[0], time);
        Check((await f.Store.ListAsync()).Single().Preview == Fixture.Preview, "list-remains-metadata-only");
        var item = await f.Store.GetAsync(f.Ids[0]);
        Check(item!.Preview == "" && item.ExtractionStatus.Contains("变化") && item.Version == 1, "detail-hash-detects-replacement");
        Check((await f.Store.GetAsync(f.Ids[0]))!.Version == 2, "detail-invalidation-persisted");
    }

    private static async Task Reparse()
    {
        var f = await Fixture.Create(16, 8);
        var parent = Path.GetDirectoryName(f.Files[0])!;
        var saved = parent + "-saved";
        Directory.Move(parent, saved);
        await LinkDirectory(parent, saved);
        try
        {
            var rows = await f.Store.ListAsync();
            Check(rows.Single(r => r.ObjectPath == f.Files[0]) is { Preview: "", ExtractionStatus: "源文件不可读" }, "junction-never-reuses-preview");
            Check(rows.Single(r => r.ObjectPath == f.Files[8]).Preview == Fixture.Preview, "other-directory-readable");
        }
        finally { Directory.Delete(parent); Directory.Move(saved, parent); }
        if (OperatingSystem.IsWindows())
        {
            var savedFile = f.Files[0] + "-saved";
            File.Move(f.Files[0], savedFile);
            await LinkDirectory(f.Files[0], Path.GetDirectoryName(f.Files[8])!);
            try
            {
                Check((await f.Store.ListAsync()).Single(r => r.ObjectPath == f.Files[0])
                    is { Preview: "", ExtractionStatus: "源文件不可读" }, "leaf-directory-junction-rejected-before-directory-missing-check");
            }
            finally { Directory.Delete(f.Files[0]); File.Move(savedFile, f.Files[0]); }
        }
        if (!OperatingSystem.IsWindows())
        {
            var savedFile = f.Files[0] + "-saved";
            File.Move(f.Files[0], savedFile);
            File.CreateSymbolicLink(f.Files[0], savedFile);
            try { Check((await f.Store.ListAsync()).Single(r => r.ObjectPath == f.Files[0]).Preview == "", "leaf-link-rejected"); }
            finally { File.Delete(f.Files[0]); File.Move(savedFile, f.Files[0]); }
        }
    }

    private static async Task DirectoryPin()
    {
        if (!OperatingSystem.IsWindows()) return;
        var f = await Fixture.Create(8, 8);
        var observed = false;
        StorageFaults.Handler.Value = stage =>
        {
            if (stage != "list-reference-directory-pinned" || observed) return;
            observed = true;
            var parent = Path.GetDirectoryName(f.Files[0])!;
            var blocked = false;
            try { Directory.Move(parent, parent + "-replaced"); }
            catch (IOException) { blocked = true; }
            Check(blocked, "source-directory-cannot-be-replaced-during-enumeration");
            File.WriteAllText(f.Files[0], "changed while directory metadata is pinned");
        };
        try
        {
            var rows = await f.Store.ListAsync();
            var changed = rows.Single(r => r.ObjectPath == f.Files[0]);
            Check(observed && changed.Preview == "" && changed.ExtractionStatus.Contains("变化"), "source-file-edits-remain-visible");
        }
        finally { StorageFaults.Handler.Value = null; }
        // Handles are query-scoped, not a watcher or a persistent source lock.
        var directory = Path.GetDirectoryName(f.Files[0])!;
        Directory.Move(directory, directory + "-released");
        Directory.Move(directory + "-released", directory);
    }

    private static async Task Cancellation()
    {
        if (!OperatingSystem.IsWindows()) return;
        var f = await Fixture.Create(16, 8);
        using var cts = new CancellationTokenSource();
        StorageFaults.Handler.Value = stage => { if (stage == "list-reference-directory-pinned") cts.Cancel(); };
        try
        {
            try { await f.Store.ListAsync(cancellationToken: cts.Token); }
            catch (OperationCanceledException) { return; }
            throw new Exception("filesystem-cancellation-not-observed");
        }
        finally { StorageFaults.Handler.Value = null; }
    }

    private static async Task QuerySemantics()
    {
        var f = await Fixture.Create(120, 40);
        var all = await f.Store.ListAsync();
        Check(all.Count == 120 && all.Select(r => r.Id).Distinct().Count() == 120, "no-result-cap");
        Check(all.SequenceEqual(all.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id, StringComparer.Ordinal)), "stable-order");
        Check((await f.Store.ListAsync("preview-needle", f.Project.Id, "笔记")).Count == 120, "preview-search");
        Check((await f.Store.ListAsync("Codex\nSynthetic query")).Count == 120, "cross-field-newline-substring-preserved");
        Check((await f.Store.ListAsync("")).Count == 120, "empty-search-preserved");
        Check((await f.Store.ListAsync("PREVIEW-NEEDLE")).Count == 120, "sqlite-ascii-lower-preserved");
        Check((await f.Store.ListAsync("%_")).Count == 0, "search-is-literal-substring-not-like");
        Check((await f.Store.ListAsync("not-a-match")).Count == 0, "no-match");
        Check((await f.Store.ListAsync(projectId: "unknown-project")).Count == 0, "project-filter");
        Check((await f.Store.ListAsync(kind: "表格")).Count == 0, "kind-filter");
        using var db = f.Open();
        using var cmd = db.CreateCommand();
        cmd.CommandText = "UPDATE artifacts SET object_name='ref:' || $path WHERE id=$id";
        cmd.Parameters.AddWithValue("$path", @"\\untrusted\share\no-read.md");
        cmd.Parameters.AddWithValue("$id", f.Ids[0]);
        cmd.ExecuteNonQuery();
        try { await f.Store.ListAsync(); }
        catch (IOException) { return; }
        throw new Exception("network-reference-path-accepted");
    }

    private static async Task InitializeLayout()
    {
        var f = await Fixture.Create(0, 1);
        Check(!Directory.Exists(Path.Combine(f.Data, "objects")), "readonly-initialize-does-not-create-objects");
        foreach (var marker in new[] { "storage-layout.json", "session-learning.marker" })
        {
            var path = Path.Combine(f.Root, "marked-" + marker);
            Directory.CreateDirectory(path);
            await File.WriteAllTextAsync(Path.Combine(path, marker), "synthetic marker");
            await new PortfolioStore(path).InitializeAsync();
            Check(!Directory.Exists(Path.Combine(path, "objects")), "existing-marker-does-not-recreate-objects");
        }
    }

    private static async Task LinkDirectory(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", $"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' -ErrorAction Stop | Out-Null" }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        await process.WaitForExitAsync();
        Check(process.ExitCode == 0, "synthetic-junction-created");
    }

    private static void Check(bool value, string code) { if (!value) throw new Exception(code); }
    private sealed record Fixture(string Root, string Data, PortfolioStore Store, CodexProject Project, string[] Files, string[] Ids)
    {
        internal const string Preview = "Synthetic query fixture only; not parser evidence; preview-needle";
        internal SqliteConnection Open()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Data, "portfolio.db"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            db.Open(); return db;
        }
        internal static async Task<Fixture> Create(int count, int perDirectory)
        {
            var root = Path.Combine(Path.GetTempPath(), "Amifolio-stage9-logic-" + Guid.NewGuid().ToString("N"));
            var projectRoot = Path.Combine(root, "project");
            var data = Path.Combine(root, "data");
            var home = Path.Combine(root, "catalog");
            Directory.CreateDirectory(projectRoot); Directory.CreateDirectory(home);
            var files = new string[count];
            for (var i = 0; i < count; i++)
            {
                var directory = Path.Combine(projectRoot, $"batch{i / perDirectory:D3}");
                Directory.CreateDirectory(directory);
                files[i] = Path.Combine(directory, $"item{i:D5}.md");
                await File.WriteAllTextAsync(files[i], "original\n", new UTF8Encoding(false));
            }
            await File.WriteAllTextAsync(Path.Combine(home, ".codex-global-state.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            { ["local-projects"] = new Dictionary<string, object> { ["fixture"] = new { name = "Synthetic", rootPaths = new[] { projectRoot } } } }));
            var store = new PortfolioStore(data); store.EnableCodexReferenceMode(home);
            await store.RefreshCodexProjectsAsync();
            var project = store.CodexProjects.Single();
            var ids = files.Select(p => "ref-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(project.Id + "\n" + p.ToUpperInvariant())))).ToArray();
            var fixture = new Fixture(root, data, store, project, files, ids);
            using var db = fixture.Open(); using var tx = db.BeginTransaction(); using var insert = db.CreateCommand(); insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO artifacts(id,title,original_name,kind,source,project_id,tags,object_name,hash,bytes,created_at,version,extraction_status,preview,logical_key)
                VALUES($id,$name,$name,'笔记','Codex',$project,'synthetic', $path,$hash,$bytes,$date,1,'synthetic fixture',$preview,$id)
                """;
            foreach (var key in new[] { "$id", "$name", "$project", "$path", "$hash", "$bytes", "$date", "$preview" }) insert.Parameters.AddWithValue(key, "");
            for (var i = 0; i < count; i++)
            {
                insert.Parameters["$id"].Value = ids[i]; insert.Parameters["$name"].Value = Path.GetFileName(files[i]);
                insert.Parameters["$project"].Value = project.Id; insert.Parameters["$path"].Value = "ref:" + files[i];
                insert.Parameters["$hash"].Value = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(files[i])));
                insert.Parameters["$bytes"].Value = new FileInfo(files[i]).Length;
                insert.Parameters["$date"].Value = new DateTimeOffset(File.GetLastWriteTimeUtc(files[i])).ToString("O");
                insert.Parameters["$preview"].Value = Preview; insert.ExecuteNonQuery();
            }
            tx.Commit(); return fixture;
        }
    }
}
