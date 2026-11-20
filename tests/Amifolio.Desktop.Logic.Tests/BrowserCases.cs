using Amifolio.Desktop;

internal static class BrowserCases
{
    public static async Task RunAsync()
    {
        var failures = new List<string>();
        var checks = 0;
        void Check(bool value, string name) { checks++; if (!value) failures.Add(name); }
        var history = new BrowserHistory<string>("projects", 4, StringComparer.OrdinalIgnoreCase);
        history.Visit("A"); history.Visit("A/one");
        Check(await history.MoveAsync(-1, _ => Task.FromResult(true)) && history.Current == "A", "back visits previous directory");
        Check(await history.MoveAsync(1, _ => Task.FromResult(true)) && history.Current == "A/one", "forward visits future directory");
        await history.MoveAsync(-1, _ => Task.FromResult(true)); history.Visit("A/two");
        Check(!history.CanGoForward && history.Current == "A/two", "branch discards future");
        history.Visit("a/TWO"); Check(history.Count == 3, "case-insensitive repeat creates no duplicate");
        history.Visit("A/three"); history.Visit("A/four");
        Check(history.Count == 4, "history retains bounded entries");
        await history.MoveAsync(-1, x => Task.FromResult(x != "A/three"));
        Check(history.Current == "A/two", "back skips and removes newly unavailable directory");
        Check(await history.MoveAsync(1, _ => Task.FromResult(true)) && history.Current == "A/four", "forward survives removal of invalid neighbor");
        history.ReplaceCurrent("projects");
        Check(history.Current == "projects" && !history.CanGoForward, "refresh fallback replaces current without stale forward branch");
        var denied = new BrowserHistory<string>("projects"); denied.Visit("missing"); denied.Visit("removed-project"); denied.Visit("valid");
        Check(await denied.MoveAsync(-1, x => Task.FromResult(x == "projects")) && denied.Current == "projects" && denied.Count == 2, "history skips missing excluded or removed project entries");
        var noDestination = new BrowserHistory<string>("invalid"); noDestination.Visit("valid");
        Check(!await noDestination.MoveAsync(-1, _ => Task.FromResult(false)) && noDestination.Current == "valid" && !noDestination.CanGoBack, "all invalid destinations leave current untouched");
        var future = new BrowserHistory<string>("projects"); future.Visit("missing"); future.Visit("valid");
        await future.MoveAsync(-1, _ => Task.FromResult(true)); await future.MoveAsync(-1, _ => Task.FromResult(true));
        Check(await future.MoveAsync(1, x => Task.FromResult(x != "missing")) && future.Current == "valid" && future.Count == 2, "forward prunes an invalid entry without losing valid future");
        var bounded = new BrowserHistory<string>("projects", 3); bounded.Visit("one"); bounded.Visit("two"); bounded.Visit("three");
        while (await bounded.MoveAsync(-1, _ => Task.FromResult(true))) { }
        Check(bounded.Current == "one" && bounded.Count == 3, "bounded history evicts oldest location and stops at retained boundary");

        var items = new[] {
            new Entry("large", false, "z.md", "text", 20, 100),
            new Entry("folder", true, "b", "folder", 1, 0),
            new Entry("first", false, "a.md", "text", 10, 5),
            new Entry("equal", false, "A.md", "text", 10, 5),
            new Entry("image", false, "c.png", "image", 30, 50) };
        string Order(BrowserSortField field, bool descending) => string.Join(",", BrowserSort.Apply(items, x => new(x.Directory, x.Name, x.Type, DateTimeOffset.UnixEpoch.AddSeconds(x.Modified), x.Bytes), field, descending).Select(x => x.Id));
        Check(Order(BrowserSortField.Name, false) == "folder,first,equal,image,large", "name ascending puts folders first and keeps equal keys stable");
        Check(Order(BrowserSortField.Name, true) == "folder,large,image,first,equal", "descending never sends folders to the end");
        Check(Order(BrowserSortField.Type, false) == "folder,image,large,first,equal", "type sorting is stable for equal types");
        Check(Order(BrowserSortField.Modified, true) == "folder,image,large,first,equal", "modified sorting uses source timestamp");
        Check(Order(BrowserSortField.Size, false) == "folder,first,equal,image,large", "size ascending uses bytes");
        Check(Order(BrowserSortField.Size, true) == "folder,large,image,first,equal", "size descending preserves equal selection ordering");
        foreach (var (key, alt, control, expected) in new[] { (37,true,false,BrowserCommand.Back),(39,true,false,BrowserCommand.Forward),(38,true,false,BrowserCommand.Up),(8,false,false,BrowserCommand.Back),(13,false,false,BrowserCommand.Open),(116,false,false,BrowserCommand.Refresh),(65,false,true,BrowserCommand.SelectAll),(67,false,true,BrowserCommand.CopyPaths) })
        {
            Check(BrowserInput.Key(key, alt, control, false, false) == expected, "browser key " + key);
            Check(BrowserInput.Key(key, alt, control, false, true) == BrowserCommand.None, "editing retains key " + key);
        }
        Check(BrowserInput.Key(37, false, false, false, false) == BrowserCommand.None, "plain arrow stays with grid selection");
        Check(BrowserInput.Key(65, false, true, true, false) == BrowserCommand.None, "extra modifiers do not hijack alternate commands");
        Check(BrowserInput.Key(37, true, true, false, false) == BrowserCommand.None, "mixed modifiers stay unhandled");
        Check(BrowserInput.Key(13, false, true, false, false) == BrowserCommand.None, "control-enter does not open a directory");
        Check(BrowserInput.Key(13, true, false, false, false) == BrowserCommand.None, "alt-enter does not open a directory");
        Check(BrowserInput.SideButton(true, false) == BrowserCommand.Back && BrowserInput.SideButton(false, true) == BrowserCommand.Forward, "actual side-button press routes in both directions");
        Check(BrowserInput.SideButton(false, false) == BrowserCommand.None, "ordinary mouse press has no history action");
        if (failures.Count > 0) throw new Exception($"FAIL {failures.Count}/{checks} browser assertions: " + string.Join("; ", failures));
        Console.WriteLine($"PASS {checks} browser history/sort/input assertions");
    }
    private sealed record Entry(string Id, bool Directory, string Name, string Type, int Modified, long Bytes);
}
