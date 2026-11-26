using Amifolio.Core;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

if (args.Length == 5 && args[0] == "--codex-plugin-fixture")
    return await CodexPluginCases.Run(args[1], args[2], args[3], args[4]);
if(args.Length==4&&args[0]=="--stage9-upgrade")return await Stage9Upgrade.Run(args[1],args[2],args[3]);
if(args.Length==1&&args[0]=="--stage9-performance-logic")return await Stage9PerformanceLogicCases.Run();
if(args.Length==3&&args[0]=="--stage9-performance")return await Stage9PerformanceCheck.Run(args[1],args[2]);
if (args.Length == 3 && args[0] == "--stage9-query-benchmark")
    return await Stage9bQueryBenchmark.Run(args[1], args[2]);
if(args.Length==4&&args[0]=="--claim-task")
{
    using var lease=await new TaskStore(args[1]).TryClaimAsync(args[2]);if(lease is null)return 5;
    await System.IO.File.WriteAllTextAsync(args[3],"claimed");await Task.Delay(Timeout.Infinite);return 0;
}
if(args.Length==4&&args[0]=="--parent-worker")
{
    var store=new PortfolioStore(args[1]);var artifact=(await store.ImportFileAsync(args[2])).Artifact;
    await new ExtractionProcess(["dotnet",typeof(Stage3Cases).Assembly.Location,"--worker-fixture","hang",args[3]]).RunAsync(artifact,CancellationToken.None);return 0;
}
if(args.Length is 2 or 3&&args[0]=="--worker-fixture")
{
    await Console.In.ReadToEndAsync();
    if(args.Length==3){await System.IO.File.WriteAllTextAsync(args[2]+".tmp",Environment.ProcessId.ToString());System.IO.File.Move(args[2]+".tmp",args[2]);}
    if(args[1]=="hang")await Task.Delay(Timeout.Infinite);
    if(args[1]=="overflow")await Console.Out.WriteAsync(new string('x',1024*1024+1));
    return 3;
}
if (args.Length == 5 && args[0] == "--stage2-crash") return await CrashCases.Worker(args);
if (args.Length == 3 && args[0] == "--import-worker")
{
    var type = typeof(IPortfolioStore).Assembly.GetType("Amifolio.Core.PortfolioStore")!;
    var store = (IPortfolioStore)Activator.CreateInstance(type, args[1])!;
    var result = await store.ImportFileAsync(args[2]);
    Console.WriteLine(result.Artifact.Id);
    return 0;
}

var tests = new (string Name, Func<Fixture, Task> Run)[]
{
    ("durable copy survives source mutation, deletion and restart", async f =>
    {
        var p = f.File("report.md", "原始内容 needle");
        var a = (await f.Store.ImportFileAsync(p)).Artifact;
        Check(Path.IsPathFullyQualified(a.ObjectPath) && a.ObjectPath != p, "object must be a full independent path");
        Check(await System.IO.File.ReadAllTextAsync(await f.Store.GetOriginalPathAsync(a.Id)) == "原始内容 needle", "ack before complete copy");
        System.IO.File.WriteAllText(p, "changed"); System.IO.File.Delete(p);
        var restarted = f.Restart(); await restarted.InitializeAsync();
        Check((await restarted.ListAsync("needle")).Single().Id == a.Id, "extracted search lost on restart");
        Check(await System.IO.File.ReadAllTextAsync(await restarted.GetOriginalPathAsync(a.Id)) == "原始内容 needle", "original bytes lost");
        Check(a.Hash == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("原始内容 needle"))).ToLowerInvariant(), "incorrect digest");
    }),
    ("read-only source is untouched", async f =>
    {
        var p = f.File("readonly.txt", "unchanged"); var time = System.IO.File.GetLastWriteTimeUtc(p);
        System.IO.File.SetAttributes(p, FileAttributes.ReadOnly);
        await f.Store.ImportFileAsync(p);
        Check(System.IO.File.ReadAllText(p) == "unchanged" && System.IO.File.GetLastWriteTimeUtc(p) == time, "source modified");
        Check(System.IO.File.GetAttributes(p).HasFlag(FileAttributes.ReadOnly), "source attributes modified");
    }),
    ("same bytes name project source deduplicate; changed bytes append versions", async f =>
    {
        var p = f.File("a/note.txt", "v1"); var q = f.File("b/note.txt", "v1");
        var a = await f.Store.ImportFileAsync(p);
        var duplicate = await f.Store.ImportFileAsync(q);
        Check(duplicate.AlreadyExists && duplicate.Artifact.Id == a.Artifact.Id, "duplicate changed identity");
        System.IO.File.WriteAllText(p, "v2"); var b = await f.Store.ImportFileAsync(p);
        Check(!b.AlreadyExists && b.Artifact.Version == 2 && b.Artifact.ObjectPath != a.Artifact.ObjectPath, "overwrote version");
        Check(System.IO.File.ReadAllText(a.Artifact.ObjectPath) == "v1", "old version corrupted");
        System.IO.File.WriteAllText(p, "v1"); Check((await f.Store.ImportFileAsync(p)).AlreadyExists, "old bytes should deduplicate");
    }),
    ("identity separates names projects and sources", async f =>
    {
        var p = f.File("x.txt", "same"); var q = f.File("y.txt", "same"); var project = await f.Store.CreateProjectAsync("P");
        var results = new[] { await f.Store.ImportFileAsync(p), await f.Store.ImportFileAsync(q),
            await f.Store.ImportFileAsync(p, project.Id), await f.Store.ImportFileAsync(p, source: "Codex") };
        Check(results.All(x => !x.AlreadyExists && x.Artifact.Version == 1) && results.Select(x => x.Artifact.Id).Distinct().Count() == 4, "identity merged distinct imports");
    }),
    ("projects metadata filters literal search and injection safety persist", async f =>
    {
        var project = await f.Store.CreateProjectAsync("项目 '一'", f.Root);
        var a = (await f.Store.ImportFileAsync(f.File("a.md", "body"), project.Id, "Codex", "old")).Artifact;
        await f.Store.UpdateArtifactAsync(a.Id, "O'Brien %_ 标题", "中文 标签", project.Id);
        var s = f.Restart(); await s.InitializeAsync();
        Check((await s.ProjectsAsync()).Single().RootPath == f.Root, "project not persisted");
        Check((await s.ListAsync("%_", project.Id, a.Kind)).Single().Title == "O'Brien %_ 标题", "literal search/metadata failure");
        Check((await s.ListAsync("中文")).Single().Tags == "中文 标签", "tags not searchable");
        Check((await s.ListAsync("' OR 1=1 --")).Count == 0, "search injection");
        Check((await s.ListAsync("Codex")).Count == 1, "source not searchable");
        Check((await s.ListAsync(projectId: "unknown")).Count == 0, "project filter ignored");
    }),
    ("removal never deletes source and old versions stay independent", async f =>
    {
        var p = f.File("a.txt", "one"); var a = (await f.Store.ImportFileAsync(p)).Artifact;
        System.IO.File.WriteAllText(p, "two"); var b = (await f.Store.ImportFileAsync(p)).Artifact;
        await f.Store.RemoveArtifactAsync(a.Id); await f.Store.RemoveArtifactAsync(a.Id);
        Check(await f.Store.GetAsync(a.Id) is null && (await f.Store.ListAsync()).Count == 1, "removed entry still visible");
        Check(System.IO.File.ReadAllText(p) == "two" && System.IO.File.ReadAllText(b.ObjectPath) == "two", "removal deleted other bytes");
        await Throws<KeyNotFoundException>(() => f.Store.GetOriginalPathAsync(a.Id));
        System.IO.File.WriteAllText(p, "three"); Check((await f.Store.ImportFileAsync(p)).Artifact.Version == 3, "version counter reused");
    }),
    ("generated artifacts are marked application and object-store reimport is rejected", async f =>
    {
        var a = await f.Store.SaveGeneratedAsync("summary.md", "summary");
        Check(a.Artifact.Source == "应用" && a.Artifact.OriginalName == "summary.md", "generated provenance missing");
        Check((await f.Store.SaveGeneratedAsync("summary.md", "summary")).AlreadyExists, "generated duplicate");
        Check((await f.Store.SaveGeneratedAsync("summary.md", "changed")).Artifact.Version == 2, "generated version");
        await Throws<IOException>(() => f.Store.ImportFileAsync(a.Artifact.ObjectPath));
        await Throws<ArgumentException>(() => f.Store.SaveGeneratedAsync("../escape.md", "oops"));
    }),
    ("concurrent independent stores serialize dedupe and version allocation", async f =>
    {
        var p = f.File("race.txt", "same");
        var stores = Enumerable.Range(0, 8).Select(_ => f.Restart()).ToArray();
        await Task.WhenAll(stores.Select(x => x.InitializeAsync()));
        var results = await Task.WhenAll(stores.Select(x => x.ImportFileAsync(p)));
        Check(results.Count(x => !x.AlreadyExists) == 1 && results.Select(x => x.Artifact.Id).Distinct().Count() == 1, "racing imports duplicated");
        var files = Enumerable.Range(0, 8).Select(i => f.File($"v{i}/version.txt", $"bytes{i}")).ToArray();
        var versions = await Task.WhenAll(stores.Select((s, i) => s.ImportFileAsync(files[i])));
        Check(versions.Select(x => x.Artifact.Version).Order().SequenceEqual(Enumerable.Range(1, 8)), "concurrent versions collided");
        Check((await f.Store.ListAsync()).Count == 9, "concurrent commits lost");
    }),
    ("cancelled imports do not acknowledge or leave artifacts", async f =>
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Throws<OperationCanceledException>(() => f.Store.ImportFileAsync(f.File("cancel.txt", "data"), cancellationToken: cts.Token));
        await Throws<OperationCanceledException>(() => f.Store.SaveGeneratedAsync("x.txt", "data", cancellationToken: cts.Token));
        await Throws<OperationCanceledException>(() => f.Store.ImportRepositoryAsync(f.Root, cancellationToken: cts.Token));
        Check((await f.Store.ListAsync()).Count == 0, "cancellation published artifact");
    }),
    ("mid-copy cancellation removes partial object", async f =>
    {
        var p = f.File("large.bin", ""); using (var stream = System.IO.File.OpenWrite(p)) stream.SetLength(200L * 1024 * 1024);
        using var cts = new CancellationTokenSource();
        var import = f.Store.ImportFileAsync(p, cancellationToken: cts.Token);
        var cancelledDuringCopy = false;
        while (!import.IsCompleted)
        {
            if (Directory.EnumerateFiles(f.DataRoot, "*.partial", SearchOption.AllDirectories).Any()) { cts.Cancel(); cancelledDuringCopy = true; break; }
            await Task.Delay(1);
        }
        Check(cancelledDuringCopy, "could not observe in-flight staging copy");
        await Throws<OperationCanceledException>(() => import);
        Check((await f.Store.ListAsync()).Count == 0 && !Directory.EnumerateFiles(f.DataRoot, "*.partial", SearchOption.AllDirectories).Any(), "cancelled copy leaked");
    }),
    ("oversize files are rejected before publication", async f =>
    {
        var p = f.File("oversize.bin", ""); using (var s = System.IO.File.OpenWrite(p)) s.SetLength(257L * 1024 * 1024);
        await Throws<IOException>(() => f.Store.ImportFileAsync(p)); Check((await f.Store.ListAsync()).Count == 0, "oversize archived");
    }),
    ("PDF scan legacy and binary remain archived with truthful unsupported status", async f =>
    {
        foreach (var name in new[] { "scan.pdf", "old.doc", "picture.png", "opaque.bin" })
        {
            var a = await f.Store.ImportFileAsync(f.File(name, "%PDF-fake"));
            Check(a.Artifact.ExtractionStatus.Contains(name.EndsWith(".pdf")?"损坏":"不支持") && System.IO.File.Exists(a.Artifact.ObjectPath), "fabricated extraction");
        }
    }),
    ("DOCX extracts paragraph and table locations without evaluating XML entities", async f =>
    {
        var p = f.Zip("report.docx", ("word/document.xml", "<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>Hello</w:t></w:r><w:r><w:t> world</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>表格</w:t></w:r></w:p></w:tc></w:tr></w:tbl></w:body></w:document>"));
        var a = (await f.Store.ImportFileAsync(p)).Artifact;
        Check(a.Preview.Contains("Hello world") && a.Preview.Contains("段落") && a.Preview.Contains("表格") && a.ExtractionStatus.Contains("已提取"), "DOCX text/location missing");
    }),
    ("XLSX resolves shared strings inline cells and worksheet names", async f =>
    {
        var p = f.Zip("book.xlsx",
            ("xl/workbook.xml", "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Budget' sheetId='1' r:id='rId1'/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Target='worksheets/sheet1.xml'/></Relationships>"),
            ("xl/sharedStrings.xml", "<sst xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><si><t>Revenue</t></si></sst>"),
            ("xl/worksheets/sheet1.xml", "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><sheetData><row r='1'><c r='A1' t='s'><v>0</v></c><c r='B1'><v>42</v></c><c r='C1' t='inlineStr'><is><t>内联</t></is></c></row></sheetData></worksheet>"));
        var a = (await f.Store.ImportFileAsync(p)).Artifact;
        Check(new[] { "Budget", "A1", "Revenue", "B1", "42", "C1", "内联" }.All(a.Preview.Contains), "XLSX values/locations missing");
    }),
    ("PPTX uses presentation order and slide locations", async f =>
    {
        var p = f.Zip("slides.pptx",
            ("ppt/presentation.xml", "<p:presentation xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><p:sldIdLst><p:sldId id='1' r:id='r2'/><p:sldId id='2' r:id='r1'/></p:sldIdLst></p:presentation>"),
            ("ppt/_rels/presentation.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Target='slides/slide1.xml'/><Relationship Id='r2' Target='slides/slide2.xml'/></Relationships>"),
            ("ppt/slides/slide1.xml", "<p:sld xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:p><a:r><a:t>Second</a:t></a:r></a:p></p:sld>"),
            ("ppt/slides/slide2.xml", "<p:sld xmlns:p='http://schemas.openxmlformats.org/presentationml/2006/main' xmlns:a='http://schemas.openxmlformats.org/drawingml/2006/main'><a:p><a:r><a:t>First</a:t></a:r></a:p></p:sld>"));
        var a = (await f.Store.ImportFileAsync(p)).Artifact;
        Check(a.Preview.Contains("幻灯片 1") && a.Preview.IndexOf("First", StringComparison.Ordinal) < a.Preview.IndexOf("Second", StringComparison.Ordinal), "slide order or locations lost");
    }),
    ("Office bombs malformed archives and XXE are archived but not extracted", async f =>
    {
        var outside = f.File("private.txt", "DO_NOT_DISCLOSE");
        var paths = new[] {
            f.Zip("bomb.docx", ("word/document.xml", new string('x', 9 * 1024 * 1024))),
            f.File("bad.docx", "not a ZIP"),
            f.Zip("xxe.docx", ("word/document.xml", $"<!DOCTYPE doc [<!ENTITY xxe SYSTEM '{new Uri(outside).AbsoluteUri}'>]><doc>&xxe;</doc>")),
            f.Zip("traversal.docx", ("../word/document.xml", "<doc/>")),
            f.Zip("many.docx", Enumerable.Range(0, 2050).Select(i => ($"part{i}.xml", "<x/>")).ToArray()) };
        foreach (var p in paths)
        {
            var a = (await f.Store.ImportFileAsync(p)).Artifact;
            Check(!a.ExtractionStatus.Contains("已提取") && !a.Preview.Contains("DO_NOT_DISCLOSE") && System.IO.File.Exists(a.ObjectPath), "unsafe extraction succeeded");
        }
    }),
    ("text preview is bounded and explicitly reports truncation", async f =>
    {
        var a = (await f.Store.ImportFileAsync(f.File("huge.txt", new string('a', 150_000)))).Artifact;
        Check(a.Preview.Length <= 66_000 && a.ExtractionStatus.Contains("截断"), "unbounded or silent preview truncation");
        var binary = (await f.Store.ImportFileAsync(f.File("binary.txt", "zero\0binary"))).Artifact;
        Check(!binary.ExtractionStatus.Contains("已提取"), "binary treated as text");
    }),
    ("list preview is SQL-bounded while full detail and near-end search survive", async f =>
    {
        var imported = (await f.Store.ImportFileAsync(f.File("long-preview.txt", new string('a', 60_000) + " NEAR_END_SEARCH_SENTINEL"))).Artifact;
        Check(imported.Preview.Length > 60_000, "fixture did not produce a full stored preview");
        var restarted = f.Restart();
        var listed = (await restarted.ListAsync()).Single();
        Check(listed.Preview.Length <= 2048, "ListAsync materialized the full stored preview");
        Check(listed.Preview == imported.Preview[..2048], "list preview is not the stored prefix");
        Check(!listed.Preview.Contains("NEAR_END_SEARCH_SENTINEL"), "sentinel must be outside the list prefix");
        var found = (await restarted.ListAsync("NEAR_END_SEARCH_SENTINEL")).Single();
        Check(found.Id == imported.Id && found.Preview.Length <= 2048, "search no longer uses the full stored preview");
        var detail = await restarted.GetAsync(imported.Id);
        Check(detail is not null && detail.Preview == imported.Preview && detail.Preview.Contains("NEAR_END_SEARCH_SENTINEL"), "GetAsync lost full preview");
        Check(listed with { Preview = imported.Preview } == imported, "projection changed other artifact metadata");
    }),
    ("repository snapshot excludes secrets builds links and includes manifest", async f =>
    {
        var repo = Path.Combine(f.Root, "repo"); Directory.CreateDirectory(repo);
        foreach (var name in new[] { "README.md", "src/main.cs", "package-lock.json", ".git/config", "node_modules/x.js", "bin/a", "obj/a", "venv/a", ".env", ".env.production", "keys/id", "secrets/a", "id_rsa", "cert.pem", "credentials.json" }) f.File("repo/" + name, name);
        var a = await f.Store.ImportRepositoryAsync(repo);
        using var zip = ZipFile.OpenRead(a.Artifact.ObjectPath);
        var names = zip.Entries.Select(x => x.FullName).ToArray();
        Check(names.Contains("README.md") && names.Contains("src/main.cs") && names.Contains("package-lock.json"), "source/lockfile missing");
        Check(!names.Any(n => n.StartsWith(".git/") || n.StartsWith("node_modules/") || n.StartsWith("bin/") || n.StartsWith("obj/") || n.StartsWith("venv/") || n.StartsWith("keys/") || n.StartsWith("secrets/") || n.StartsWith(".env") || n == "id_rsa" || n == "cert.pem" || n == "credentials.json"), "secret/build leak");
        var manifest = zip.GetEntry("__amifolio__/manifest.json"); Check(manifest is not null, "manifest absent");
        using var reader = new StreamReader(manifest!.Open()); var json = await reader.ReadToEndAsync();
        using var doc = JsonDocument.Parse(json);
        Check(json.Contains(".env.production") && json.Contains("package-lock.json") && a.Warnings.Count >= 10 && a.Artifact.Preview.Contains("README.md"), "manifest/report exclusions missing");
        Check((await f.Store.ImportRepositoryAsync(repo)).AlreadyExists, "unchanged repository is not deterministic");
        f.File("repo/src/main.cs", "changed"); Check((await f.Store.ImportRepositoryAsync(repo)).Artifact.Version == 2, "snapshot version missing");
    }),
    ("repository excludes contained object store and rejects store as repository", async f =>
    {
        f.File("keep.txt", "source"); await f.Store.SaveGeneratedAsync("private.md", "private");
        var recovery=f.DataRoot+".before-restore-"+Guid.NewGuid().ToString("N");Directory.CreateDirectory(recovery);File.WriteAllText(Path.Combine(recovery,"private.md"),"private recovery");
        var a = await f.Store.ImportRepositoryAsync(f.Root);
        using var zip = ZipFile.OpenRead(a.Artifact.ObjectPath);
        Check(!zip.Entries.Any(x => x.FullName.StartsWith("data/")), "repository recursively captured its object store");
        Check(!zip.Entries.Any(x => x.FullName.StartsWith("data.")), "repository captured store sidecars or recovery library");
        Check(a.Warnings.Any(x => x.Contains("data")), "object-store exclusion unreported");
        await Throws<IOException>(() => f.Store.ImportRepositoryAsync(f.DataRoot));
    }),
    ("repository manifest namespace collisions are excluded with warning", async f =>
    {
        f.File("collision/__amifolio__/manifest.json", "fake"); f.File("collision/ok.txt", "ok");
        var a = await f.Store.ImportRepositoryAsync(Path.Combine(f.Root, "collision"));
        using var zip = ZipFile.OpenRead(a.Artifact.ObjectPath);
        Check(zip.Entries.Count(x => x.FullName == "__amifolio__/manifest.json") == 1 && a.Warnings.Any(x => x.Contains("__amifolio__")), "manifest spoofing");
    }),
    ("junction source ancestors and repository escapes are rejected", async f =>
    {
        var outside = Path.Combine(f.Root, "outside"); Directory.CreateDirectory(outside); f.File("outside/private.txt", "secret sentinel");
        var repo = Path.Combine(f.Root, "links"); Directory.CreateDirectory(repo); f.File("links/ok.txt", "ok");
        var link = Path.Combine(repo, "escape"); await Junction(link, outside);
        await Throws<IOException>(() => f.Store.ImportFileAsync(Path.Combine(link, "private.txt")));
        await Throws<IOException>(() => f.Store.ImportRepositoryAsync(link));
        var a = await f.Store.ImportRepositoryAsync(repo);
        using var zip = ZipFile.OpenRead(a.Artifact.ObjectPath);
        Check(!zip.Entries.Any(x => x.FullName.Contains("private.txt")) && a.Warnings.Any(x => x.Contains("escape")), "junction followed");
        Directory.Delete(link); // remove junction only, never traverse it in cleanup
    }),
    ("invalid projects and missing objects fail honestly without publishing", async f =>
    {
        await Throws<ArgumentException>(() => f.Store.ImportFileAsync(f.File("x.txt", "x"), "unknown"));
        Check((await f.Store.ListAsync()).Count == 0, "invalid project published");
        var a = (await f.Store.ImportFileAsync(f.File("valid.txt", "valid"))).Artifact;
        System.IO.File.SetAttributes(a.ObjectPath, FileAttributes.Normal); System.IO.File.Delete(a.ObjectPath);
        await Throws<IOException>(() => f.Store.GetOriginalPathAsync(a.Id));
        await Throws<IOException>(() => f.Store.ImportFileAsync(f.File("again/valid.txt", "valid")));
    }),
    ("SQLite uses WAL and database remains valid after concurrent operations", async f =>
    {
        await f.Store.ImportFileAsync(f.File("a.txt", "a"));
        var database = Directory.EnumerateFiles(f.DataRoot, "*.db").Single();
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString()); connection.Open();
        using var cmd = connection.CreateCommand(); cmd.CommandText = "PRAGMA journal_mode"; Check((string)cmd.ExecuteScalar()! == "wal", "WAL missing");
        cmd.CommandText = "PRAGMA integrity_check"; Check((string)cmd.ExecuteScalar()! == "ok", "invalid database");
    }),
    ("moved metadata cannot cause cross-project dedupe or reuse version numbers", async f =>
    {
        var project = await f.Store.CreateProjectAsync("private");
        var p = f.File("move.txt", "same");
        var a = (await f.Store.ImportFileAsync(p)).Artifact;
        await f.Store.UpdateArtifactAsync(a.Id, "renamed", "tag", project.Id);
        var global = await f.Store.ImportFileAsync(p);
        Check(!global.AlreadyExists && global.Artifact.ProjectId is null, "global import returned moved private artifact");
        Check(global.Artifact.Version > a.Version, "global lineage reused an old version");
        var scoped = await f.Store.ImportFileAsync(p, project.Id);
        Check(scoped.AlreadyExists && scoped.Artifact.Id == a.Id, "moved artifact not deduplicated in its current scope");
        System.IO.File.WriteAllText(p, "changed");
        var newer = await f.Store.ImportFileAsync(p, project.Id);
        Check(newer.Artifact.Version > a.Version, "moving to a new lineage reused version 1");
    }),
    ("deleted sources stay excluded from lists and lookup after restart", async f =>
    {
        var p = f.File("deleted.md", "private original sentinel");
        var a = (await f.Store.ImportFileAsync(p)).Artifact;
        await f.Store.RemoveArtifactAsync(a.Id);
        var restarted = f.Restart();
        Check((await restarted.ListAsync()).Count == 0 && (await restarted.ListAsync("sentinel")).Count == 0 && await restarted.GetAsync(a.Id) is null, "deleted original returned to default sources");
        await Throws<KeyNotFoundException>(() => restarted.UpdateArtifactAsync(a.Id, "revive", "", null));
        var explicitImport = await restarted.ImportFileAsync(p);
        Check(!explicitImport.AlreadyExists && explicitImport.Artifact.Version == 2 && explicitImport.Artifact.Id != a.Id, "explicit reimport revived tombstone");
    }),
    ("separate operating-system processes share durable dedupe", async f =>
    {
        var path = f.File("process.txt", "bridge/UI shared bytes");
        var starts = Enumerable.Range(0, 4).Select(_ =>
        {
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location); start.ArgumentList.Add("--import-worker");
            start.ArgumentList.Add(f.DataRoot); start.ArgumentList.Add(path);
            return Process.Start(start)!;
        }).ToArray();
        try
        {
            var outputs = await Task.WhenAll(starts.Select(async p =>
            {
                var text = p.StandardOutput.ReadToEndAsync(); var errors = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync(); Check(p.ExitCode == 0, await errors); return (await text).Trim();
            }));
            Check(outputs.Distinct().Count() == 1 && (await f.Store.ListAsync()).Count == 1, "cross-process duplicate commit");
        }
        finally { foreach (var p in starts) p.Dispose(); }
    }),
    ("CSV and TSV have bounded text extraction", async f =>
    {
        foreach (var name in new[] { "table.csv", "table.tsv" })
        {
            var a = (await f.Store.ImportFileAsync(f.File(name, "name,value\nAmifolio,42"))).Artifact;
            Check(a.Kind == "表格" && a.ExtractionStatus.Contains("已提取") && a.Preview.Contains("Amifolio"), "plain spreadsheet text not extracted");
        }
    }),
    ("Office expansion work and XML nesting are bounded even for repeated empty parts", async f =>
    {
        var depth = new string(' ', 1) + string.Concat(Enumerable.Repeat("<n>", 150)) + string.Concat(Enumerable.Repeat("</n>", 150));
        var deep = f.Zip("deep.docx", ("word/document.xml", depth));
        var emptySheet = "<worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><!--" + new string(' ', 512 * 1024) + "--></worksheet>";
        // Avoid a compression-ratio rejection so this case exercises repeated part processing.
        var workbook = "<workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets>" + string.Concat(Enumerable.Range(0, 1000).Select(i => $"<sheet name='S{i}' r:id='r1'/>")) + "</sheets></workbook>";
        var repeated = Path.Combine(f.Root, "repeated.xlsx");
        using (var zip = ZipFile.Open(repeated, ZipArchiveMode.Create))
        {
            foreach (var (n, t) in new[] { ("xl/workbook.xml", workbook), ("xl/_rels/workbook.xml.rels", "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='r1' Target='worksheets/sheet1.xml'/></Relationships>"), ("xl/worksheets/sheet1.xml", emptySheet) })
            { using var writer = new StreamWriter(zip.CreateEntry(n, CompressionLevel.NoCompression).Open()); writer.Write(t); }
        }
        foreach (var path in new[] { deep, repeated })
        {
            var a = (await f.Store.ImportFileAsync(path)).Artifact;
            Check(a.ExtractionStatus.Contains("提取受限"), "unbounded XML processing not rejected");
        }
    }),
    ("repository aggregate bytes and directory depth are bounded before copying", async f =>
    {
        for (var i = 0; i < 3; i++) { var p = f.File($"too-large/{i}.bin", ""); using var s = System.IO.File.OpenWrite(p); s.SetLength(200L * 1024 * 1024); }
        await Throws<IOException>(() => f.Store.ImportRepositoryAsync(Path.Combine(f.Root, "too-large")));
        var deep = Path.Combine(f.Root, "deep-repo"); var current = deep;
        for (var i = 0; i < 66; i++) { current = Path.Combine(current, "d"); Directory.CreateDirectory(current); }
        await Throws<IOException>(() => f.Store.ImportRepositoryAsync(deep));
        Check((await f.Store.ListAsync()).Count == 0 && !Directory.EnumerateFiles(f.DataRoot, "*.partial", SearchOption.AllDirectories).Any(), "repository limits leaked a partial snapshot");
    }),
    ("repository entry scan limit counts excluded entries too", async f =>
    {
        var repo = Path.Combine(f.Root, "too-many"); Directory.CreateDirectory(repo);
        for (var i = 0; i < 20_001; i++) { using var stream = System.IO.File.Create(Path.Combine(repo, $".env-{i}")); }
        await Throws<IOException>(() => f.Store.ImportRepositoryAsync(repo));
        Check((await f.Store.ListAsync()).Count == 0, "entry scan limit ignored");
    }),
    ("reparse data roots and network paths are refused", async f =>
    {
        var actual = Path.Combine(f.Root, "actual"); Directory.CreateDirectory(actual);
        var link = Path.Combine(f.Root, "linked-store"); await Junction(link, actual);
        var type = typeof(IPortfolioStore).Assembly.GetType("Amifolio.Core.PortfolioStore")!;
        try
        {
            try { Activator.CreateInstance(type, link); throw new Exception("reparse store accepted"); }
            catch (TargetInvocationException ex) when (ex.InnerException is IOException) { }
        }
        finally { Directory.Delete(link); }
        if (OperatingSystem.IsWindows())
        {
            await Throws<IOException>(() => f.Store.ImportFileAsync(@"\\localhost\share\secret.txt"));
            await Throws<IOException>(() => f.Store.ImportFileAsync(f.File("ads.txt", "normal") + ":hidden"));
        }
    }),
    ("storage safety failures release bounded copy slots", async f =>
    {
        var objects = Path.Combine(f.DataRoot, "objects"); var saved = Path.Combine(f.DataRoot, "objects-saved");
        var outside = Path.Combine(f.Root, "unsafe-output"); Directory.CreateDirectory(outside);
        Directory.Move(objects, saved); await Junction(objects, outside);
        try
        {
            for (var i = 0; i < 3; i++)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await Throws<IOException>(() => f.Store.ImportFileAsync(f.File($"safety-{i}.txt", "safe"), cancellationToken: cts.Token));
            }
        }
        finally { Directory.Delete(objects); Directory.Move(saved, objects); }
        var a = await f.Store.ImportFileAsync(f.File("recovery.txt", "safe"));
        Check(!a.AlreadyExists, "safety failure permanently blocked copies");
    }),
};

tests = tests.Concat(Stage2Cases.All).ToArray();
tests = tests.Concat(Stage3Cases.All).ToArray();
tests = tests.Concat(CollectionCases.All).ToArray();
tests = tests.Concat(CrashCases.All).ToArray();
tests = tests.Concat(FormatCases.All).ToArray();
tests = tests.Concat(ReferenceCases.All).Concat(CatalogCases.All).ToArray();
tests = tests.Concat(Stage6RecoveryCases.All).ToArray();
tests = tests.Concat(BrowserIndexCases.All).ToArray();
tests = tests.Concat(LearningCases.All).ToArray();
tests = tests.Concat(CreativePreviewCases.All).ToArray();
tests = tests.Concat(MemoryPolicyCases.All).ToArray();
tests = tests.Concat(CodexTurnCases.All).ToArray();
tests = tests.Concat(WorkCardCases.All).ToArray();
tests = tests.Concat(LearningRecoveryCases.All).ToArray();
tests = tests.Concat(LegacyDataCases.All).Concat(BackupHistoryCases.All).ToArray();
tests = tests.Concat(LearningRetentionCases.All).Concat(LearningCapacityCases.All).ToArray();
tests = tests.Concat(CatalogPolicyCases.All.Select(test=>(test.Name,(Func<Fixture,Task>)(_=>test.Test())))).ToArray();
tests = tests.Concat(Stage9bQueryCases.All.Concat(Stage9bQueryCacheCases.All).Select(test=>(test.Name,(Func<Fixture,Task>)(_=>test.Test())))).ToArray();
tests = tests.Append(("parser exposes only its owned child identity",(Func<Fixture,Task>)(_=>WorkerObservationCases.Run()))).ToArray();
if (args.Length == 2 && args[0] == "--filter")
    tests = tests.Where(t => t.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase)).ToArray();
if (tests.Length == 0) throw new ArgumentException("No integration cases matched the filter.");

var failures = 0;
foreach (var test in tests)
{
    using var f = new Fixture();
    try { await f.Store.InitializeAsync(); await test.Run(f); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {test.Name}: {ex.GetType().Name}: {ex.Message}"); }
}
Console.WriteLine($"RESULT {tests.Length - failures}/{tests.Length} passed; {failures} failed");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
static async Task Junction(string link, string target)
{
    if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
    var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
    start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
    using var p = Process.Start(start)!; await p.WaitForExitAsync();
    if (p.ExitCode != 0) throw new Exception("Cannot create junction fixture: " + await p.StandardError.ReadToEndAsync());
}

sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(AppContext.BaseDirectory, "TestRuns", Guid.NewGuid().ToString("N"));
    public string DataRoot => Path.Combine(Root, "data");
    private string? referenceRoot;
    public string ReferenceRoot => referenceRoot ??= Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),"Amifolio-reference-tests-"+Guid.NewGuid().ToString("N"))).FullName;
    public IPortfolioStore Store => instance ??= Restart();
    private IPortfolioStore? instance;
    public Fixture() { Directory.CreateDirectory(Root); }
    public IPortfolioStore Restart()
    {
        var type = typeof(IPortfolioStore).Assembly.GetType("Amifolio.Core.PortfolioStore");
        if (type is null) throw new Exception("PortfolioStore public implementation is missing");
        return (IPortfolioStore)Activator.CreateInstance(type, DataRoot)!;
    }
    public string File(string relative, string text)
    {
        var path = Path.Combine(Root, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllText(path, text, new UTF8Encoding(false)); return path;
    }
    public string Zip(string name, params (string Name, string Text)[] entries)
    {
        var path = Path.Combine(Root, name); using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var entry in entries) { using var w = new StreamWriter(zip.CreateEntry(entry.Name).Open(), new UTF8Encoding(false)); w.Write(entry.Text); }
        return path;
    }
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        // Only this verified disposable tree; junctions are removed without following targets.
        if (!Root.StartsWith(Path.Combine(AppContext.BaseDirectory, "TestRuns") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup");
        Clean(Root);
        if(referenceRoot is not null){if(!referenceRoot.StartsWith(Path.Combine(Path.GetTempPath(),"Amifolio-reference-tests-"),StringComparison.OrdinalIgnoreCase))throw new Exception("Unsafe reference cleanup");Clean(referenceRoot);}
    }
    private static void Clean(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attrs = System.IO.File.GetAttributes(entry);
            if (attrs.HasFlag(FileAttributes.Directory)) { if (attrs.HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(entry); else Clean(entry); }
            else { System.IO.File.SetAttributes(entry, FileAttributes.Normal); System.IO.File.Delete(entry); }
        }
        Directory.Delete(directory);
    }
}
