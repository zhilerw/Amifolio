using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amifolio.Core;

internal static class BrowserIndexCases
{
    private const int MaximumBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal static (string, Func<Fixture, Task>)[] All =>
    [
        ("browser index reopens metadata and last location without catalog authorization", async f =>
        {
            var snapshot = Snapshot(f);
            var store = Store(f);
            Check(await store.LoadAsync() is null, "missing cache must be disposable");
            await store.SaveAsync(snapshot);
            var reopened = await Store(f).LoadAsync();
            Check(reopened is not null && reopened.Version == 1 && reopened.SavedAt == snapshot.SavedAt, "snapshot did not reopen");
            Check(reopened!.Projects.Single() == snapshot.Projects.Single(), "project metadata lost");
            Check(reopened.Directories.Single().Entries.Single() == snapshot.Directories.Single().Entries.Single(), "entry metadata lost");
            Check(reopened.Directories.Single().Truncated && reopened.LastProjectId == "project-1" && reopened.LastDirectory == f.Root, "location/truncation lost");
            await store.SaveAsync(snapshot with { Projects = [snapshot.Projects[0] with { Name = "Renamed" }], LastProjectId = null, LastDirectory = null });
            reopened = await Store(f).LoadAsync();
            Check(reopened?.Projects.Single().Name == "Renamed" && reopened.LastProjectId is null && reopened.LastDirectory is null, "valid replacement lost updates");
            var catalog = new CodexCatalog(Home(f), CacheRoot(f));
            await Throws<IOException>(() => catalog.BrowseAsync(reopened!.Projects.Single()));
        }),
        ("browser index corrupt JSON unknown fields version and home are discarded", async f =>
        {
            var original = Snapshot(f);
            foreach (var text in new[] { "{broken", "null", "[]", "{}", Raw(original with { Version = 2 }),
                Raw(original with { CodexHome = Path.Combine(f.Root, "other-home") }),
                Raw(original).TrimEnd('}') + ",\"preview\":\"private\"}" })
            {
                WriteRaw(f, text);
                Check(await Store(f).LoadAsync() is null, "bad shape/version/home must be discarded");
            }
            await Store(f).SaveAsync(original);
            Check(await new BrowserIndexStore(CacheRoot(f), Path.Combine(f.Root, "other-home")).LoadAsync() is null, "store home mismatch accepted");
        }),
        ("browser index oversized read and write preserve prior snapshot", async f =>
        {
            var original = Snapshot(f); Check(await Store(f).SaveAsync(original), "valid save was not acknowledged");
            var previous = await File.ReadAllBytesAsync(CachePath(f));
            Check(!await Store(f).SaveAsync(original with { Projects = [original.Projects[0] with { Name = new string('x', MaximumBytes) }] }), "oversized save must report failure");
            Check(previous.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(CachePath(f))), "oversized save replaced old snapshot");
            WriteRaw(f, Raw(original with { Projects = [original.Projects[0] with { Name = new string('x', MaximumBytes) }] }));
            Check(await Store(f).LoadAsync() is null, "oversized cache accepted");
        }),
        ("browser index accepts exactly four MiB of serialized metadata", async f =>
        {
            var original = Snapshot(f); var emptyName = original with { Projects = [original.Projects[0] with { Name = "" }] };
            var padding = MaximumBytes - System.Text.Encoding.UTF8.GetByteCount(Raw(emptyName));
            var exact = original with { Projects = [original.Projects[0] with { Name = new string('x', padding) }] };
            await Store(f).SaveAsync(exact);
            Check(new FileInfo(CachePath(f)).Length == MaximumBytes, "exact byte limit not saved");
            Check((await Store(f).LoadAsync())?.Projects[0].Name.Length == padding, "exact byte limit not reopened");
        }),
        ("browser index accepts exact count limits and rejects aggregate overflow", async f =>
        {
            var original = Snapshot(f);
            var projects = Enumerable.Range(0, 2048).Select(i => new CodexProject("p" + i, "P", Path.Combine(f.Root, "p" + i), 0)).ToArray();
            var directories = Enumerable.Range(0, 16).Select(i => new BrowserDirectorySnapshot("p0", Path.Combine(projects[0].RootPath, "d" + i), [], false)).ToArray();
            directories[0] = directories[0] with { Entries = Entries(directories[0].Path, 2500) };
            directories[1] = directories[1] with { Entries = Entries(directories[1].Path, 2500) };
            var limit = original with { Projects = projects, Directories = directories, LastProjectId = "p0", LastDirectory = projects[0].RootPath };
            await Store(f).SaveAsync(limit);
            var loaded = await Store(f).LoadAsync();
            Check(loaded?.Projects.Count == 2048 && loaded.Directories.Count == 16 && loaded.Directories.Sum(d => d.Entries.Count) == 5000, "inclusive limits rejected");
            var previous = await File.ReadAllBytesAsync(CachePath(f));
            var moreEntries = directories.ToArray(); moreEntries[2] = moreEntries[2] with { Entries = Entries(moreEntries[2].Path, 1) };
            foreach (var invalid in new[] { limit with { Projects = projects.Append(new("overflow", "P", Path.Combine(f.Root, "overflow"), 0)).ToArray() },
                limit with { Directories = directories.Append(new("p0", Path.Combine(projects[0].RootPath, "extra"), [], false)).ToArray() },
                limit with { Directories = moreEntries } })
            {
                await Store(f).SaveAsync(invalid);
                Check(previous.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(CachePath(f))), "overflow save replaced old snapshot");
                WriteRaw(f, Raw(invalid)); Check(await Store(f).LoadAsync() is null, "overflow read accepted");
                await File.WriteAllBytesAsync(CachePath(f), previous);
            }
        }),
        ("browser index rejects project escapes entry escapes and invalid last locations", async f =>
        {
            var original = Snapshot(f); var directory = original.Directories[0]; var entry = directory.Entries[0];
            await Store(f).SaveAsync(original); var previous = await File.ReadAllBytesAsync(CachePath(f));
            var sibling = f.Root + "-sibling";
            foreach (var invalid in new[] {
                original with { Directories = [directory with { Path = sibling }] },
                original with { Directories = [directory with { Path = Path.Combine(f.Root, "..", "outside") }] },
                original with { Directories = [directory with { Path = "relative" }] },
                original with { Directories = [directory with { ProjectId = "unregistered" }] },
                original with { Directories = [directory with { Entries = [entry with { Path = Path.Combine(sibling, "metadata.txt") }] }] },
                original with { Directories = [directory with { Entries = [entry with { Path = Path.Combine(f.Root, "nested", "metadata.txt") }] }] },
                original with { LastProjectId = "unregistered" }, original with { LastDirectory = sibling },
                original with { Projects = [original.Projects[0] with { RootPath = "relative" }] },
                original with { Projects = [original.Projects[0], original.Projects[0]] } })
            {
                await Store(f).SaveAsync(invalid);
                Check(previous.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(CachePath(f))), "unsafe save replaced prior snapshot");
                WriteRaw(f, Raw(invalid)); Check(await Store(f).LoadAsync() is null, "unsafe cache was not discarded as a whole");
                await File.WriteAllBytesAsync(CachePath(f), previous);
            }
        }),
        ("browser index null collections and invalid metadata are discarded", async f =>
        {
            var original = Snapshot(f);
            foreach (var property in new[] { "projects", "directories" })
            {
                var node = JsonNode.Parse(Raw(original))!; node[property] = null;
                WriteRaw(f, node.ToJsonString()); Check(await Store(f).LoadAsync() is null, "null collection accepted");
            }
            foreach (var mutation in new Action<JsonNode>[] {
                n => n["projects"]![0] = null,
                n => n["directories"]![0] = null,
                n => n["directories"]![0]!["entries"] = null,
                n => n["directories"]![0]!["entries"]![0] = null,
                n => n["directories"]![0]!["entries"]![0]!["bytes"] = -1,
                n => n["directories"]![0]!["entries"]![0]!["name"] = "misleading.txt" })
            {
                var node = JsonNode.Parse(Raw(original))!; mutation(node);
                WriteRaw(f, node.ToJsonString()); Check(await Store(f).LoadAsync() is null, "malformed metadata accepted");
            }
        }),
        ("browser index stores only metadata without reading source or writing project and home", async f =>
        {
            var projectRoot = Directory.CreateDirectory(Path.Combine(f.Root, "project-source")).FullName;
            var source = f.File("project-source/metadata.txt", "SOURCE_PREVIEW_MUST_NOT_BE_CACHED");
            f.File("synthetic-home/config.toml", "HOME_SECRET_MUST_NOT_BE_CACHED");
            var original = Snapshot(f); var project = original.Projects[0] with { RootPath = projectRoot };
            var directory = original.Directories[0] with { Path = projectRoot, Entries = [original.Directories[0].Entries[0] with { Path = source }] };
            var metadata = original with { Projects = [project], Directories = [directory], LastDirectory = projectRoot };
            var sourceTime = File.GetLastWriteTimeUtc(source); var homeTime = File.GetLastWriteTimeUtc(Path.Combine(Home(f), "config.toml"));
            File.SetAttributes(source, FileAttributes.ReadOnly);
            try
            {
                // FileShare.None makes a content-reading implementation fail; metadata does not need a source handle.
                using (var denied = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    await Store(f).SaveAsync(metadata); Check(await Store(f).LoadAsync() is not null, "cache tried to open source body");
                }
                var text = await File.ReadAllTextAsync(CachePath(f));
                Check(!text.Contains("SOURCE_PREVIEW") && !text.Contains("HOME_SECRET") && !text.Contains("preview", StringComparison.OrdinalIgnoreCase)
                    && !text.Contains("permission", StringComparison.OrdinalIgnoreCase) && !text.Contains("allowedProjects"), "cache serialized content or authorization");
                Check(File.ReadAllText(source) == "SOURCE_PREVIEW_MUST_NOT_BE_CACHED" && File.GetLastWriteTimeUtc(source) == sourceTime && File.GetAttributes(source).HasFlag(FileAttributes.ReadOnly), "source changed");
                Check(Directory.EnumerateFileSystemEntries(projectRoot).Count() == 1 && Directory.EnumerateFileSystemEntries(Home(f)).Count() == 1
                    && File.GetLastWriteTimeUtc(Path.Combine(Home(f), "config.toml")) == homeTime, "source or home writes occurred");
                Check(Directory.EnumerateFiles(CacheRoot(f)).Select(Path.GetFileName).SequenceEqual(["browser-index.json"]), "temporary files leaked");
            }
            finally { File.SetAttributes(source, FileAttributes.Normal); }
        }),
        ("browser index cancellation propagates and retains old snapshot", async f =>
        {
            var original = Snapshot(f); await Store(f).SaveAsync(original); var previous = await File.ReadAllBytesAsync(CachePath(f));
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Throws<OperationCanceledException>(() => Store(f).SaveAsync(original with { LastDirectory = null }, cancelled.Token));
            await Throws<OperationCanceledException>(() => Store(f).LoadAsync(cancelled.Token));
            Check(previous.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(CachePath(f))), "cancelled save replaced old snapshot");
            Check(Directory.EnumerateFiles(CacheRoot(f)).Count() == 1, "cancelled save left staging files");
        }),
        ("browser index inaccessible cache is optional and preserves existing bytes", async f =>
        {
            var original = Snapshot(f); await Store(f).SaveAsync(original); var previous = await File.ReadAllBytesAsync(CachePath(f));
            using (var locked = new FileStream(CachePath(f), FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Check(await Store(f).LoadAsync() is null, "locked cache broke optional load");
                await Store(f).SaveAsync(original with { LastDirectory = null });
            }
            Check(previous.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(CachePath(f))), "failed replace damaged old cache");
            Check(Directory.EnumerateFiles(CacheRoot(f)).Count() == 1, "failed replace leaked staging");
        }),
        ("browser index cancellation after save begins retains prior snapshot", async f =>
        {
            var original = Snapshot(f); await Store(f).SaveAsync(original); var previous = await File.ReadAllBytesAsync(CachePath(f));
            using var cancellation = new CancellationTokenSource();
            var entries = new CancelOnReadList<CatalogEntry>(original.Directories[0].Entries, cancellation);
            var updated = original with { Directories = [original.Directories[0] with { Entries = entries }] };
            await Throws<OperationCanceledException>(() => Store(f).SaveAsync(updated, cancellation.Token));
            Check(cancellation.IsCancellationRequested, "save never inspected the supplied metadata");
            Check(previous.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(CachePath(f))), "in-flight cancellation replaced snapshot");
            Check(Directory.EnumerateFiles(CacheRoot(f)).Count() == 1, "in-flight cancellation leaked staging");
        }),
        ("browser index linked storage ancestor and linked cache target are refused", async f =>
        {
            var original = Snapshot(f); var outside = Directory.CreateDirectory(Path.Combine(f.Root, "outside-cache")).FullName;
            var target = Path.Combine(outside, "browser-index.json"); await File.WriteAllTextAsync(target, Raw(original));
            var before = await File.ReadAllBytesAsync(target);
            var link = Path.Combine(f.Root, "cache-link"); await Junction(link, outside);
            try
            {
                var linked = new BrowserIndexStore(Path.Combine(link, "nested"), Home(f));
                Check(await linked.LoadAsync() is null, "linked storage load accepted");
                await linked.SaveAsync(original); Check(!Directory.Exists(Path.Combine(outside, "nested")), "write followed storage ancestor link");
            }
            finally { Directory.Delete(link); }
            Directory.CreateDirectory(CacheRoot(f)); await Junction(CachePath(f), outside);
            try
            {
                Check(await Store(f).LoadAsync() is null, "linked cache accepted"); await Store(f).SaveAsync(original);
                Check(File.GetAttributes(CachePath(f)).HasFlag(FileAttributes.ReparsePoint) && before.AsEnumerable().SequenceEqual(await File.ReadAllBytesAsync(target)), "save replaced/followed linked target");
            }
            finally { Directory.Delete(CachePath(f)); }
        }),
        ("browser index linked project directory is discarded", async f =>
        {
            var original = Snapshot(f); var target = Directory.CreateDirectory(Path.Combine(f.Root, "target")).FullName;
            var link = Path.Combine(f.Root, "project-link"); await Junction(link, target);
            try
            {
                WriteRaw(f, Raw(original with { Directories = [original.Directories[0] with { Path = link, Entries = [] }], LastDirectory = link }));
                Check(await Store(f).LoadAsync() is null, "linked browse directory accepted");
            }
            finally { Directory.Delete(link); }
        })
    ];

    private static string Home(Fixture f) => Path.Combine(f.Root, "synthetic-home");
    private static string CacheRoot(Fixture f) => Path.Combine(f.Root, "browser-cache");
    private static string CachePath(Fixture f) => Path.Combine(CacheRoot(f), "browser-index.json");
    private static BrowserIndexStore Store(Fixture f) => new(CacheRoot(f), Home(f));
    private static string Raw(BrowserIndexSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json);
    private static void WriteRaw(Fixture f, string text) { Directory.CreateDirectory(CacheRoot(f)); File.WriteAllText(CachePath(f), text); }
    private static BrowserIndexSnapshot Snapshot(Fixture f) => new(1, Home(f), new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero),
        [new("project-1", "Project", f.Root, 3)], [new("project-1", f.Root, Entries(f.Root, 1), true)], "project-1", f.Root);
    private static CatalogEntry[] Entries(string directory, int count) => Enumerable.Range(0, count)
        .Select(i => new CatalogEntry(Path.Combine(directory, i == 0 ? "metadata.txt" : $"metadata{i}.txt"), i == 0 ? "metadata.txt" : $"metadata{i}.txt", false, "text", 42,
            new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero), "ready")).ToArray();
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
    private static async Task Junction(string link, string target)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(link, target); return; }
        var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
        start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{link.Replace("'", "''")}' -Target '{target.Replace("'", "''")}' -ErrorAction Stop | Out-Null");
        using var process = Process.Start(start)!; await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new Exception("Cannot create browser cache junction fixture: " + await process.StandardError.ReadToEndAsync());
    }
    private sealed class CancelOnReadList<T>(IReadOnlyList<T> items, CancellationTokenSource cancellation) : IReadOnlyList<T>
    {
        public int Count => items.Count;
        public T this[int index] { get { cancellation.Cancel(); return items[index]; } }
        public IEnumerator<T> GetEnumerator()
        {
            for (var i = 0; i < Count; i++) yield return this[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
