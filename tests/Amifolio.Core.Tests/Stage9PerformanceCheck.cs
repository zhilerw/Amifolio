using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amifolio.Core;
using Microsoft.Data.Sqlite;

/// <summary>
/// Opt-in synthetic core benchmark; the parent owns Program registration and native/idle sampling.
/// Run requires a NONEXISTENT, local fixture directory outside .codex, and a fresh report path.
/// All inputs are generated here. Fixtures are retained, never cleaned recursively by this harness.
/// No import, archive, parser, model, network, credentials, or formal application data is used.
/// </summary>
internal static class Stage9PerformanceCheck
{
    private const int Repetitions = 40;
    private const double QueryTargetMs = 200;
    private const string PreviewLabel = "SYNTHETIC QUERY FIXTURE ONLY; NOT PARSER OUTPUT OR EXTRACTION EVIDENCE. ";
    private const string PreviewNeedle = "stage9-preview-tail-needle";
    private const string FixtureStatus = "Synthetic query fixture; not parser output";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    internal static async Task<int> Run(string fixtureRoot, string reportPath)
    {
        var report = new Report();
        var runTimer = Stopwatch.StartNew();
        FileStream? output = null;
        var phase = "preflight";
        try
        {
            // Reserve the report before doing any work, without overwriting an existing file.
            reportPath = LocalPath(reportPath);
            NoLinks(reportPath);
            Require(!File.Exists(reportPath) && !Directory.Exists(reportPath), "fresh-report-required");
            fixtureRoot = LocalPath(fixtureRoot);
            Require(!Within(reportPath, fixtureRoot), "report-outside-fixture-required");
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            ValidateFreshRoot(fixtureRoot);
            Directory.CreateDirectory(fixtureRoot);
            using (var marker = new FileStream(Path.Combine(fixtureRoot, "stage9-owned-fixture.json"), FileMode.CreateNew))
                JsonSerializer.Serialize(marker, new { owner = "Stage9PerformanceCheck", syntheticOnly = true, runId = Guid.NewGuid().ToString("N") });

            foreach (var (name, count) in new[] { ("empty", 0), ("small100", 100), ("large10000", 10000) })
            {
                phase = name + "/setup";
                Console.WriteLine($"stage9 {name}: setup starting; elapsed={runTimer.Elapsed.TotalSeconds:F1}s");
                var setup = Stopwatch.StartNew();
                var fixture = await Prepare(fixtureRoot, name, count);
                setup.Stop();
                var dataset = new DatasetReport { Name = name, ReferenceCount = count, SetupMs = setup.Elapsed.TotalMilliseconds };
                report.Datasets.Add(dataset); // Retain partial results on failure.
                phase = name + "/measure";
                await MeasureDataset(fixture, dataset);
                dataset.Recommendations = Recommendations(dataset);
            }
            report.Success = true;
        }
        catch (Exception error)
        {
            // Never emit exception.Message/ToString, SQL, absolute paths, or stack traces.
            report.Failure = new(phase, error is CheckFailure failure ? failure.Code : "unexpected-error", ErrorType(error));
        }
        finally
        {
            report.ElapsedSeconds = runTimer.Elapsed.TotalSeconds;
            if (output is not null)
            {
                try { await JsonSerializer.SerializeAsync(output, report, Json); await output.FlushAsync(); }
                catch { report.Success = false; Console.Error.WriteLine("stage9-report-write-failed"); }
                finally { await output.DisposeAsync(); }
            }
            else Console.Error.WriteLine("stage9-report-unavailable; no existing report was overwritten");
        }
        Console.WriteLine(report.Success ? "stage9-synthetic-core-complete" : "stage9-synthetic-core-failed");
        return report.Success ? 0 : 1;
    }

    private static async Task<Fixture> Prepare(string root, string name, int count)
    {
        Require(count is 0 or 100 or 10000, "supported-fixture-scale");
        var directory = Path.Combine(root, name);
        Require(!Directory.Exists(directory) && !File.Exists(directory), "fresh-dataset-required");
        var projectRoot = Path.Combine(directory, "project");
        var home = Path.Combine(directory, "catalog");
        var data = Path.Combine(directory, "data");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(home);
        var files = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var parent = count > 100 ? Path.Combine(projectRoot, $"batch{i / 100:D3}") : projectRoot;
            Directory.CreateDirectory(parent);
            var extension = (i % 3) switch { 0 => ".md", 1 => ".csv", _ => ".cs" };
            var text = (i % 3) switch
            {
                0 => $"# Synthetic stage9 item {i:D5}\nTiny Markdown fixture.\n",
                1 => $"id,label\n{i},synthetic-stage9\n",
                _ => $"// Synthetic stage9 file\ninternal static class Item{i:D5} {{ }}\n"
            };
            var path = Path.Combine(parent, $"item{i:D5}" + extension);
            using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Write(Utf8.GetBytes(text));
            files.Add(path);
            if ((i + 1) % 2000 == 0) Console.WriteLine($"stage9 synthetic files created: {i + 1}/{count}");
        }
        // This is the real catalog's supported registry shape, not a hand-constructed project ID.
        var registryPath = Path.Combine(home, ".codex-global-state.json");
        await File.WriteAllTextAsync(registryPath, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["local-projects"] = new Dictionary<string, object>
            { ["stage9-synthetic"] = new { id = "stage9-synthetic", name = name, rootPaths = new[] { projectRoot } } }
        }), Utf8);
        var store = new PortfolioStore(data);
        store.EnableCodexReferenceMode(home);
        await store.InitializeAsync();
        await store.RefreshCodexProjectsAsync();
        Require(store.ReadOnlySources && store.DiscoveryWarnings.Count == 0 && store.CodexProjects.Count == 1, "valid-catalog-registry");
        var project = store.CodexProjects.Single();
        Require(SamePath(project.RootPath, projectRoot), "catalog-root-matches-fixture");
        var fixture = new Fixture(directory, projectRoot, registryPath, data, store, project, files);
        Seed(fixture);
        return fixture;
    }

    private static void Seed(Fixture fixture)
    {
        using var db = Open(fixture.Data, readOnly: false);
        using var tx = db.BeginTransaction();
        using var insert = db.CreateCommand();
        insert.Transaction = tx;
        // Use the schema created by InitializeAsync, including genuine reference identity/path rules.
        // A single prepared command + ONE transaction avoids 10,000 sequential durable commits.
        insert.CommandText = """
            INSERT INTO artifacts(id,title,original_name,kind,source,project_id,tags,object_name,hash,bytes,
                created_at,version,extraction_status,preview,logical_key)
            VALUES($id,$title,$name,$kind,'Codex',$project,'synthetic-stage9-query-fixture',$path,$hash,$bytes,
                $date,1,$status,$preview,$id)
            """;
        foreach (var parameter in new[] { "$id", "$title", "$name", "$kind", "$project", "$path", "$hash", "$date", "$status", "$preview" })
            insert.Parameters.Add(parameter, SqliteType.Text);
        insert.Parameters.Add("$bytes", SqliteType.Integer);
        insert.Prepare();
        var preview = PreviewLabel + new string('x', 2200) + " " + PreviewNeedle;
        foreach (var (path, index) in fixture.Files.Select((p, i) => (p, i)))
        {
            var info = new FileInfo(path);
            var id = "ref-" + Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(fixture.Project.Id + "\n" + path.ToUpperInvariant())));
            insert.Parameters["$id"].Value = id;
            insert.Parameters["$title"].Value = $"Stage9Title{index:D5}";
            insert.Parameters["$name"].Value = info.Name;
            insert.Parameters["$kind"].Value = Path.GetExtension(path) switch { ".md" => "笔记", ".csv" => "表格", _ => "代码" };
            insert.Parameters["$project"].Value = fixture.Project.Id;
            insert.Parameters["$path"].Value = "ref:" + path;
            insert.Parameters["$hash"].Value = HashFile(path);
            insert.Parameters["$bytes"].Value = info.Length;
            insert.Parameters["$date"].Value = new DateTimeOffset(info.LastWriteTimeUtc).ToString("O", CultureInfo.InvariantCulture);
            insert.Parameters["$status"].Value = FixtureStatus;
            insert.Parameters["$preview"].Value = preview;
            Require(insert.ExecuteNonQuery() == 1, "seed-row-inserted");
        }
        tx.Commit();
    }

    private static async Task MeasureDataset(Fixture fixture, DatasetReport report)
    {
        Console.WriteLine($"stage9 {report.Name}: source baseline and database integrity");
        var before = Snapshot(fixture);
        report.SourceBefore = Summarize(before);
        report.SourceFileBytes = before.Where(p => p.Key.StartsWith("project/", StringComparison.Ordinal) && !p.Value.Directory).Sum(p => p.Value.Bytes);
        report.FileTypeCounts = fixture.Files.GroupBy(Path.GetExtension).ToDictionary(g => g.Key!, g => g.Count());
        report.DatabaseBefore = Database(fixture);
        Require(before.Values.Count(s => !s.Directory) == fixture.Files.Count + 1, "real-files-plus-registry");
        Require(before.Values.Where(s => s.Directory).All(s => s.ImmediateEntries <= 100), "at-most-100-immediate-entries");
        Require(report.DatabaseBefore.Rows == fixture.Files.Count && report.DatabaseBefore.ReferenceRows == fixture.Files.Count, "real-schema-row-count");
        Require(report.DatabaseBefore.Integrity == "ok" && report.DatabaseBefore.ForeignKeyViolations == 0, "database-integrity");
        var expectedFiles = fixture.Files.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expectedIds = fixture.Files.Select(p => "ref-" + Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(fixture.Project.Id + "\n" + p.ToUpperInvariant()))))
            .ToHashSet(StringComparer.Ordinal);

        // Immutable source/registry baseline is also checked in finally when any measured call fails.
        try
        {
            await StoreCase("list-all", () => fixture.Store.ListAsync(), fixture.Files.Count, all: true);
            await StoreCase("search-title-all", () => fixture.Store.ListAsync("Stage9Title"), fixture.Files.Count, all: true);
            await StoreCase("search-preview-tail-all", () => fixture.Store.ListAsync(PreviewNeedle), fixture.Files.Count, all: true);
            await StoreCase("search-title-selective", () => fixture.Store.ListAsync($"Stage9Title{Math.Max(0, fixture.Files.Count - 1):D5}"), fixture.Files.Count == 0 ? 0 : 1);
            await StoreCase("search-no-match", () => fixture.Store.ListAsync("stage9-never-present-needle"), 0);
            var mdCount = fixture.Files.Count(p => Path.GetExtension(p) == ".md");
            await StoreCase("list-project-and-kind", () => fixture.Store.ListAsync(projectId: fixture.Project.Id, kind: "笔记"), mdCount);

            await Measure(report, "catalog-discover", () => Observe(() => fixture.Store.Catalog.DiscoverAsync(), result =>
            {
                return new Observation(result.Items.Count, result.Truncated, result.Warnings.Count,
                    result.Items.Count == 1 && result.Items[0] == fixture.Project);
            }), 1);
            var directories = fixture.Files.Count > 100 ? 100 : 0;
            await Measure(report, "catalog-browse-root", () => Observe(() => fixture.Store.Catalog.BrowseAsync(fixture.Project),
                result => BrowseObservation(result, directories == 0 ? fixture.Files.Count : directories)),
                directories == 0 ? fixture.Files.Count : directories);
            // An aggregate traversal explicitly visits every leaf, rather than timing only one 100-file page.
            await Measure(report, "catalog-browse-all-files", async () =>
            {
                var timer = Stopwatch.StartNew();
                var root = await fixture.Store.Catalog.BrowseAsync(fixture.Project);
                var truncated = root.Truncated;
                var warnings = root.Warnings.Count;
                var valid = root.Items.All(e => e.State == "ready");
                var paths = root.Items.Where(e => !e.IsDirectory).Select(e => e.Path).ToList();
                foreach (var directory in root.Items.Where(e => e.IsDirectory))
                {
                    var children = await fixture.Store.Catalog.BrowseAsync(fixture.Project, directory.Path);
                    truncated |= children.Truncated;
                    warnings += children.Warnings.Count;
                    valid &= children.Items.Count == 100 && children.Items.All(e => !e.IsDirectory && e.State == "ready");
                    paths.AddRange(children.Items.Select(e => e.Path));
                }
                timer.Stop();
                return new Observation(paths.Count, truncated, warnings, valid && paths.Count == expectedFiles.Count && expectedFiles.SetEquals(paths))
                    { ApiMs = timer.Elapsed.TotalMilliseconds };
            }, fixture.Files.Count);
            if (fixture.Files.Count > 0)
                await Measure(report, "catalog-validate-one-rotating", async () =>
                {
                    var path = fixture.Files[fixture.ValidationIndex++ % fixture.Files.Count];
                    return await Observe(() => fixture.Store.Catalog.ValidateFileAsync(fixture.Project, path),
                        result => new Observation(1, false, 0, SamePath(path, result)));
                }, 1);
            else report.SkippedOperations.Add("catalog-validate-one-rotating: empty fixture has no file");
        }
        finally
        {
            var after = Snapshot(fixture);
            report.SourceAfter = Summarize(after);
            report.SourceUnchanged = SameSnapshot(before, after);
            report.DatabaseAfter = Database(fixture);
            report.DatabaseContentUnchanged = report.DatabaseBefore.DatabaseSha256 == report.DatabaseAfter.DatabaseSha256
                && report.DatabaseBefore.Rows == report.DatabaseAfter.Rows;
            report.NoArchiveObjects = !Directory.Exists(Path.Combine(fixture.Data, "objects"))
                || !Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Data, "objects")).Any();
            Require(report.SourceUnchanged, "source-bytes-mtime-attributes-or-layout-changed");
            Require(report.DatabaseContentUnchanged, "query-mutated-database");
            Require(report.NoArchiveObjects, "no-archive-objects-during-query");
        }

        async Task StoreCase(string name, Func<Task<IReadOnlyList<ArtifactRecord>>> action, int expected, bool all = false)
        {
            await Measure(report, name, async () =>
            {
                PortfolioStore.ListPerformanceSample? phases = null;
                var previousObserver = fixture.Store.ListPerformanceObserver;
                fixture.Store.ListPerformanceObserver = value => phases = value;
                try
                {
                var observation = await Observe(action, rows =>
                {
                var valid = rows.All(r => r.IsReference && expectedFiles.Contains(r.ObjectPath) && expectedIds.Contains(r.Id)
                    && r.ExtractionStatus == FixtureStatus && r.Preview.StartsWith(PreviewLabel, StringComparison.Ordinal)
                    && r.Preview.Length <= 2048);
                if (name == "search-title-selective")
                    valid &= rows.All(r => r.Title == $"Stage9Title{fixture.Files.Count - 1:D5}");
                if (name == "list-project-and-kind")
                    valid &= rows.All(r => r.ProjectId == fixture.Project.Id && r.Kind == "笔记" && Path.GetExtension(r.ObjectPath) == ".md");
                valid &= rows.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() == rows.Count;
                if (all) valid &= expectedIds.SetEquals(rows.Select(r => r.Id));
                return new Observation(rows.Count, false, 0, valid);
                });
                return observation with { StorePerformance = phases };
                }
                finally { fixture.Store.ListPerformanceObserver = previousObserver; }
            }, expected);
        }
    }

    private static async Task<Observation> Observe<T>(Func<Task<T>> call, Func<T, Observation> inspect)
    {
        var timer = Stopwatch.StartNew();
        var result = await call();
        timer.Stop();
        return inspect(result) with { ApiMs = timer.Elapsed.TotalMilliseconds };
    }

    private static Observation BrowseObservation(CatalogResult<CatalogEntry> result, int expected) =>
        new(result.Items.Count, result.Truncated, result.Warnings.Count,
            result.Items.Count == expected && result.Items.All(e => e.State == "ready"));

    private static async Task Measure(DatasetReport report, string name, Func<Task<Observation>> action, int expected)
    {
        var metric = new Metric { Operation = name, ExpectedCount = expected };
        var operationTimer = Stopwatch.StartNew();
        Console.WriteLine($"stage9 {report.Name}/{name}: starting 40 samples");
        report.Measurements.Add(metric);
        var (first, firstMs) = await Timed();
        metric.FirstCallMs = firstMs;
        metric.FirstCallReturnedCount = first.Count;
        ValidateObservation(first, expected);
        // One untimed warmup; the first call is reported separately and is NOT a cold-OS-cache claim.
        ValidateObservation(await action(), expected);
        for (var i = 0; i < Repetitions; i++)
        {
            var (result, elapsed) = await Timed();
            metric.SamplesMs.Add(elapsed);
            metric.ReturnedCounts.Add(result.Count);
            if (result.StorePerformance is { } phases) metric.StorePhases.Add(phases);
            ValidateObservation(result, expected);
            if ((i + 1) % 5 == 0) Console.WriteLine($"stage9 {report.Name}/{name}: {i + 1}/40; operation elapsed={operationTimer.Elapsed.TotalSeconds:F1}s");
        }
        var sorted = metric.SamplesMs.Order().ToArray();
        metric.P50Ms = Percentile(sorted, .50);
        metric.P95Ms = Percentile(sorted, .95);
        metric.MaxMs = sorted[^1];
        if (metric.StorePhases.Count == Repetitions)
        {
            metric.MetadataP50Ms = Percentile(metric.StorePhases.Select(p => p.MetadataMs).Order().ToArray(), .5);
            metric.MetadataP95Ms = Percentile(metric.StorePhases.Select(p => p.MetadataMs).Order().ToArray(), .95);
            metric.FileSystemP50Ms = Percentile(metric.StorePhases.Select(p => p.FileSystemMs).Order().ToArray(), .5);
            metric.FileSystemP95Ms = Percentile(metric.StorePhases.Select(p => p.FileSystemMs).Order().ToArray(), .95);
        }
        Console.WriteLine($"stage9 {report.Name}/{name}: P95={metric.P95Ms:F2}ms metadataP95={metric.MetadataP95Ms:F2}ms FS_P95={metric.FileSystemP95Ms:F2}ms");
        if (report.ReferenceCount == 10000 && (name.StartsWith("list-", StringComparison.Ordinal) || name.StartsWith("search-", StringComparison.Ordinal)))
            metric.Within200MsP95 = metric.P95Ms <= QueryTargetMs;

        async Task<(Observation Result, double Ms)> Timed()
        {
            // Observe captures only the API/await. The aggregate traversal has its own timer.
            var timer = Stopwatch.StartNew();
            var result = await action();
            timer.Stop();
            return (result, result.ApiMs ?? timer.Elapsed.TotalMilliseconds);
        }
    }

    private static void ValidateObservation(Observation result, int expected)
    {
        Require(result.Count == expected, "returned-count-mismatch-or-query-cap");
        Require(!result.Truncated && result.Warnings == 0, "catalog-truncation-or-warning");
        Require(result.Valid, "returned-result-invariant");
    }

    private static double Percentile(double[] sorted, double p) => sorted[(int)Math.Ceiling(sorted.Length * p) - 1];

    private static Dictionary<string, Stamp> Snapshot(Fixture fixture)
    {
        var result = new Dictionary<string, Stamp>(StringComparer.Ordinal);
        Visit(fixture.ProjectRoot);
        Add(fixture.RegistryPath);
        return result;
        void Visit(string path)
        {
            Add(path);
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            {
                if (File.GetAttributes(entry).HasFlag(FileAttributes.Directory)) Visit(entry);
                else Add(entry);
            }
        }
        void Add(string path)
        {
            NoLinks(path);
            var attributes = File.GetAttributes(path);
            var isDirectory = attributes.HasFlag(FileAttributes.Directory);
            var relative = Path.GetRelativePath(fixture.Directory, path).Replace('\\', '/');
            result.Add(relative, new(isDirectory, isDirectory ? 0 : new FileInfo(path).Length,
                File.GetLastWriteTimeUtc(path).Ticks, (int)attributes, isDirectory ? "directory" : HashFile(path),
                isDirectory ? Directory.EnumerateFileSystemEntries(path).Count() : 0));
        }
    }

    private static bool SameSnapshot(Dictionary<string, Stamp> before, Dictionary<string, Stamp> after) =>
        before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var stamp) && stamp == pair.Value);

    private static SourceSummary Summarize(Dictionary<string, Stamp> snapshot) => new(
        snapshot.Values.Count(s => !s.Directory), snapshot.Values.Count(s => s.Directory), snapshot.Values.Sum(s => s.Bytes),
        snapshot.Values.Where(s => s.Directory).Max(s => s.ImmediateEntries),
        Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(JsonSerializer.Serialize(snapshot.OrderBy(p => p.Key, StringComparer.Ordinal))))));

    private static DatabaseSummary Database(Fixture fixture)
    {
        using var db = Open(fixture.Data, readOnly: true);
        using var command = db.CreateCommand();
        long Scalar(string sql) { command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture); }
        var rows = Scalar("SELECT COUNT(*) FROM artifacts");
        var refs = Scalar("SELECT COUNT(*) FROM artifacts WHERE object_name LIKE 'ref:%' AND deleted=0");
        var pages = Scalar("PRAGMA page_count");
        var pageBytes = Scalar("PRAGMA page_size");
        var free = Scalar("PRAGMA freelist_count");
        var schema = Scalar("PRAGMA user_version");
        command.CommandText = "PRAGMA quick_check";
        var integrity = command.ExecuteScalar() as string == "ok" ? "ok" : "failed";
        command.CommandText = "PRAGMA foreign_key_check";
        var violations = 0;
        using (var reader = command.ExecuteReader()) while (reader.Read()) violations++;
        var path = Path.Combine(fixture.Data, "portfolio.db");
        return new(rows, refs, schema, pages, pageBytes, pages * pageBytes, free, FileBytes(path), FileBytes(path + "-wal"),
            FileBytes(path + "-shm"), HashFile(path), integrity, violations);
    }

    private static SqliteConnection Open(string data, bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(data, "portfolio.db"), Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=15000;";
        command.ExecuteNonQuery();
        return connection;
    }

    private static List<string> Recommendations(DatasetReport report)
    {
        var recommendations = new List<string>();
        var queries = report.Measurements.Where(m => m.Operation.StartsWith("list-", StringComparison.Ordinal) || m.Operation.StartsWith("search-", StringComparison.Ordinal)).ToArray();
        var slowest = queries.MaxBy(m => m.P95Ms)!;
        recommendations.Add($"Observed slowest store query: {slowest.Operation}; P95={slowest.P95Ms:F3} ms (warm same-process synthetic fixture). No per-stage CPU/IO attribution was measured.");
        if (queries.Any(m => m.Within200MsP95 == false))
            recommendations.Add("10,000-row query target exceeded. Profile ListAsync SQL/materialization separately from LiveReference filesystem checks. Evaluate bounded pages with explicit total/count and cancellation, preserving source freshness checks; do not impose a silent result cap.");
        var miss = queries.Single(m => m.Operation == "search-no-match");
        var broad = queries.Single(m => m.Operation == "search-preview-tail-all");
        if (broad.P95Ms > miss.P95Ms * 2)
            recommendations.Add("Broad preview search is over 2x the no-match P95. It returns more rows; inspect row materialization and per-reference path/mtime checks before attributing the difference to SQL.");
        recommendations.Add("Search uses instr(lower(concatenated fields), lower(query)): inspect SQLite query plans before proposing FTS or an indexed normalized search representation; preserve literal substring semantics and preview-tail matching.");
        recommendations.Add("Catalog full traversal is 1+leaf-directory public calls; keep it separate from immediate-directory browsing. Validate native/UI responsiveness and whole-process idle resources in the parent's sampler.");
        return recommendations;
    }

    private static void ValidateFreshRoot(string path)
    {
        Require(!path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains(".codex", StringComparer.OrdinalIgnoreCase), "fixture-outside-dot-codex");
        NoLinks(path);
        Require(!Directory.Exists(path) && !File.Exists(path), "fresh-fixture-root-required");
        Require(Directory.Exists(Path.GetDirectoryName(path)), "fixture-parent-must-exist");
    }

    private static string LocalPath(string path)
    {
        Require(!string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal), "absolute-local-path-required");
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        Require(full != Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full)!), "not-volume-root");
        if (OperatingSystem.IsWindows())
        {
            Require(full.Length > 3 && char.IsAsciiLetter(full[0]) && full[1] == ':' && full[2] == '\\'
                && !full[2..].Contains(':') && !path.Split('\\', '/').Any(p => p.EndsWith(' ') || p.EndsWith('.')), "ordinary-local-path-required");
            Require(new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Network, "no-network-drive");
        }
        return full;
    }

    private static void NoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { Require((File.GetAttributes(current) & (FileAttributes.ReparsePoint | FileAttributes.Device)) == 0, "no-links-or-device-paths"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    private static bool Within(string path, string root) => SamePath(path, root) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string HashFile(string path) { using var file = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    private static long FileBytes(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
    private static void Require(bool value, string code) { if (!value) throw new CheckFailure(code); }
    private static string ErrorType(Exception error) => error switch
    { CheckFailure => "invariant", SqliteException => "sqlite", IOException => "io", UnauthorizedAccessException => "access", OperationCanceledException => "cancelled", _ => "unexpected" };
    private sealed class CheckFailure(string code) : Exception { internal string Code { get; } = code; }
    private sealed record Observation(int Count, bool Truncated, int Warnings, bool Valid)
    { public double? ApiMs { get; init; } public PortfolioStore.ListPerformanceSample? StorePerformance { get; init; } }
    private sealed record Stamp(bool Directory, long Bytes, long LastWriteUtcTicks, int Attributes, string Sha256, int ImmediateEntries);
    private sealed record SourceSummary(int FilesIncludingRegistry, int Directories, long BytesIncludingRegistry, int MaxImmediateEntries, string ManifestSha256);
    private sealed record DatabaseSummary(long Rows, long ReferenceRows, long SchemaVersion, long PageCount, long PageBytes, long LogicalPageBytes,
        long FreePages, long DatabaseBytes, long WalBytes, long ShmBytes, string DatabaseSha256, string Integrity, int ForeignKeyViolations);
    private sealed record Diagnostic(string Phase, string Code, string Type);
    private sealed record Fixture(string Directory, string ProjectRoot, string RegistryPath, string Data, PortfolioStore Store, CodexProject Project, List<string> Files)
    { internal int ValidationIndex; }
    private sealed class Metric
    {
        public string Operation { get; set; } = "";
        public int ExpectedCount { get; set; }
        public double FirstCallMs { get; set; }
        public int FirstCallReturnedCount { get; set; }
        public List<double> SamplesMs { get; } = [];
        public List<int> ReturnedCounts { get; } = [];
        public List<PortfolioStore.ListPerformanceSample> StorePhases { get; } = [];
        public double? MetadataP50Ms { get; set; }
        public double? MetadataP95Ms { get; set; }
        public double? FileSystemP50Ms { get; set; }
        public double? FileSystemP95Ms { get; set; }
        public double P50Ms { get; set; }
        public double P95Ms { get; set; }
        public double MaxMs { get; set; }
        public bool? Within200MsP95 { get; set; }
    }
    private sealed class DatasetReport
    {
        public string Name { get; set; } = "";
        public int ReferenceCount { get; set; }
        public long SourceFileBytes { get; set; }
        public Dictionary<string, int> FileTypeCounts { get; set; } = [];
        public double SetupMs { get; set; }
        public int SeedTransactions => 1;
        public string PreviewProvenance => PreviewLabel;
        public SourceSummary? SourceBefore { get; set; }
        public SourceSummary? SourceAfter { get; set; }
        public bool SourceUnchanged { get; set; }
        public bool DatabaseContentUnchanged { get; set; }
        public bool NoArchiveObjects { get; set; }
        public DatabaseSummary? DatabaseBefore { get; set; }
        public DatabaseSummary? DatabaseAfter { get; set; }
        public List<Metric> Measurements { get; } = [];
        public List<string> SkippedOperations { get; } = [];
        public List<string> Recommendations { get; set; } = [];
    }
    private sealed class Report
    {
        public int SchemaVersion => 1;
        public string Scope => "stage9-synthetic-core-only";
        public bool Success { get; set; }
        public double ElapsedSeconds { get; set; }
        public Diagnostic? Failure { get; set; }
        public DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;
        public string OS => RuntimeInformation.OSDescription;
        public string Architecture => RuntimeInformation.ProcessArchitecture.ToString();
        public string Runtime => RuntimeInformation.FrameworkDescription;
        public int LogicalProcessors => Environment.ProcessorCount;
        public string BuildConfiguration =>
#if DEBUG
            "Debug";
#else
            "Release";
#endif
        public long StopwatchFrequency => Stopwatch.Frequency;
        public int QueryRepetitions => Repetitions;
        public string Percentiles => "Nearest rank over 40 sorted samples: P50=20th, P95=38th; max=40th. Sequential awaited calls; no forced GC or outlier removal.";
        public string CacheAssumptions => "FirstCall is first timed invocation of that operation after fixture creation, initialization, discovery, seeding and hash/integrity reads in the same process. It is NOT cold process or cold OS cache. One extra warmup precedes 40 warm repeats. OS filesystem cache is uncontrolled and likely warm; SQLite connections are opened by public store APIs with pooling disabled. No cache eviction, process restart, reboot or memory-pressure test.";
        public string TimingBoundary => "Wall-clock Stopwatch includes the actual public API and await. Result correctness checks, setup, snapshot hashing, DB integrity/statistics and report serialization are outside samples. Catalog full traversal includes all immediate browse calls, iteration and intermediate collection/checks; final set comparison is outside its timer. No CPU attribution or UI timing.";
        public string TargetPolicy => "200 ms P95 evaluated separately per 10,000-row store operation; false is retained, never relabeled passed. Success means fixture/invariants/completion, not performance acceptance. Catalog traversal, first-call timing, parser throughput, native startup, UI/idle/process-group CPU, hardware minimum and cross-machine validation are separate.";
        public bool? AllLargeStoreQueriesWithin200MsP95 => Datasets.FirstOrDefault(d => d.ReferenceCount == 10000) is { } large
            && large.Measurements.Count(m => m.Within200MsP95.HasValue) == 6
            ? large.Measurements.Where(m => m.Within200MsP95.HasValue).All(m => m.Within200MsP95 == true) : null;
        public List<DatasetReport> Datasets { get; } = [];
    }
}
