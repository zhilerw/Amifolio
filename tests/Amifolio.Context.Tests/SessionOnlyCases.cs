using System.Net;
using System.Text.Json;
using Amifolio.Context;
using Amifolio.Core;

internal static class SessionOnlyCases
{
    internal static (string, Func<Task>)[] All =>
    [
        ("session only marker retires legacy without granting any permissions", MarkerOnly),
        ("session only marker preserves independently granted scoped permissions", ExistingPermissions),
        ("session only storage layout v2 independently retires legacy", LayoutRetirement),
        ("session only layout v1 and missing markers do not imply retirement", OldLayout),
        ("session only malformed oversized and unsupported layout fails closed", InvalidLayout),
        ("session only cancelled marker creation writes nothing", CanceledMarker),
        ("session only Generate rejects both retirement markers before state or HTTP", GenerateRetired),
        ("session only Retry rejects both retirement markers before recovery or HTTP", RetryRetired),
        ("session only failed generation cannot retry after retirement", FailedRetryRetired),
        ("session only automation cannot recreate legacy state or send HTTP", AutomationRetired),
        ("session only legacy reconciliation cannot recreate retired state", ReconcileRetired),
        ("session only startup resume preserves retired generation logs without legacy scope revival", StartupResumeRetired),
        ("session only new read uses Share even with legacy Enabled false", ShareWithoutEnabled),
        ("session only legacy Enabled true cannot grant learning Share", EnabledWithoutShare),
        ("session only fresh learning read creates no legacy state", ReadWithoutLegacyState),
        ("session only old SetEnabled false revokes learning and fences stale publish", LegacyRevocation),
        ("session only migrated compatibility pointer targets authorized learning only", CompatibilityPointer),
        ("session only unmigrated compatibility preserves original legacy entry", PreserveLegacyEntry),
        ("session only fresh startup marker does not enable background HTTP", BackgroundWithoutConsent)
    ];

    private static readonly ModelOptions Options = new("https://session-only-test.invalid/v1", "synthetic-model", true);
    private static async Task MarkerOnly()
    {
        using var f = new Fixture(); await f.Initialize();
        Check(!f.Service.LegacyRetired && !f.Service.Learning.HasState, "fresh fixture already retired/learning initialized");
        await f.Service.EnableSessionLearningOnlyAsync();
        Check(f.Service.LegacyRetired && !f.Service.Learning.HasState, "marker must retire without initializing learning database");
        var path = Path.Combine(f.Data, "session-learning.marker"); var bytes = await File.ReadAllBytesAsync(path);
        await f.Service.EnableSessionLearningOnlyAsync();
        var repeated = await File.ReadAllBytesAsync(path);
        Check(bytes.SequenceEqual(repeated), "idempotent marker changed");
        var p = await f.Service.Learning.PolicyAsync(f.Project);
        Check(!p.Capture && !p.Learn && !p.AutoActivate && !p.Share && !p.Remote && !p.OriginalPaths && !p.ContributeGlobal && p.Revision == 0,
            "marker upgraded learning permissions");
        Check(await new TaskStore(f.Data).RemoteAllowedAsync() is null, "marker granted model permission");
        f.NoLegacyState(); f.NoHttp();
    }
    private static async Task ExistingPermissions()
    {
        using var f = new Fixture(); await f.Initialize();
        var local = await f.Service.Learning.SetPolicyAsync(f.Project, new() { Capture = true, Learn = true, Share = true, BatchSize = 2 });
        var global = await f.Service.Learning.SetPolicyAsync(null, new() { Share = true });
        await new TaskStore(f.Data).SetRemoteAllowedAsync(false);
        await f.Service.EnableSessionLearningOnlyAsync(); await f.Service.EnableSessionLearningOnlyAsync();
        Check(await f.Service.Learning.PolicyAsync(f.Project) == local && await f.Service.Learning.PolicyAsync(null) == global,
            "marker changed explicit policy or revision");
        Check(await new TaskStore(f.Data).RemoteAllowedAsync() == false, "marker upgraded remote permission");
        f.NoLegacyState(); f.NoHttp();
    }
    private static async Task LayoutRetirement()
    {
        using var f = new Fixture(); await f.Initialize(); await f.Retire(layout: true);
        Check(f.Service.LegacyRetired && new ContextService(f.Store, f.Http).LegacyRetired, "v2 not retired across service instances");
        Check(!File.Exists(Path.Combine(f.Data, "session-learning.marker")) && !f.Service.Learning.HasState, "v2 detection wrote marker or learning state");
        f.NoLegacyState(); f.NoHttp();
    }
    private static async Task OldLayout()
    {
        using var f = new Fixture(); await f.Initialize();
        Check(!f.Service.LegacyRetired, "missing marker retires legacy");
        await File.WriteAllTextAsync(Path.Combine(f.Data, "storage-layout.json"), "{\"version\":1}");
        Check(!f.Service.LegacyRetired, "v1 treated as v2"); f.NoLegacyState(); f.NoHttp();
    }
    private static async Task InvalidLayout()
    {
        using var f = new Fixture(); await f.Initialize();
        foreach (var value in new[] { "{", "null", "{}", "{\"version\":\"2\"}", "{\"version\":3}", "{\"version\":1,\"version\":2}", new string(' ', 4097) })
        {
            await File.WriteAllTextAsync(Path.Combine(f.Data, "storage-layout.json"), value);
            try { _ = f.Service.LegacyRetired; throw new Exception("invalid layout silently reopened legacy"); }
            catch (Exception e) when(e is IOException or InvalidDataException) { }
        }
        f.NoLegacyState(); f.NoHttp();
    }
    private static async Task CanceledMarker()
    {
        using var f = new Fixture(); await f.Initialize(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await f.Service.EnableSessionLearningOnlyAsync(cancel.Token); throw new Exception("cancellation ignored"); }
        catch (OperationCanceledException) { }
        Check(!f.Service.LegacyRetired && !f.Service.Learning.HasState, "cancellation wrote marker or permissions"); f.NoLegacyState();
    }
    private static async Task GenerateRetired()
    {
        foreach (var layout in new[] { false, true })
        {
            using var f = new Fixture(); await f.Initialize(); await f.Retire(layout);
            await Retired(() => f.Service.GenerateAsync(null, Options, "synthetic-key"));
            f.NoLegacyState(); f.NoHttp();
        }
    }
    private static async Task RetryRetired()
    {
        foreach (var layout in new[] { false, true })
        {
            using var f = new Fixture(); await f.Initialize(); await f.Retire(layout);
            await Retired(() => f.Service.RetryGenerationAsync("nonexistent-task", Options, "synthetic-key"));
            Check((await new TaskStore(f.Data).ListAsync()).Count == 0, "retired retry created task"); f.NoLegacyState(); f.NoHttp();
        }
    }
    private static async Task FailedRetryRetired()
    {
        foreach (var layout in new[] { false, true })
        {
            using var f = new Fixture(); await f.Initialize(); var tasks = new TaskStore(f.Data); await tasks.SetRemoteAllowedAsync(true);
            string id;
            using (var lease = await tasks.CreateGenerationLeaseAsync(null, new string('a', 64), 0))
            { id = lease.Item.Id; await tasks.FailAsync(lease, "provider_http"); }
            var before = await tasks.GetAsync(id); await f.Retire(layout);
            await Retired(() => f.Service.RetryGenerationAsync(id, Options, "synthetic-key"));
            Check(await tasks.GetAsync(id) == before, "retired retry changed durable task"); f.NoLegacyState(); f.NoHttp();
        }
    }
    private static async Task AutomationRetired()
    {
        foreach (var layout in new[] { false, true })
        {
            using var f = new Fixture(); await f.Initialize(); await f.Retire(layout);
            await f.Service.SetAutomationAsync(null, new(true, true, true));
            Check((await f.Service.ListAutomaticScopesAsync()).Count == 0, "retired scope scheduled");
            var status = await f.Service.ReadAutomationAsync();
            Check(status.Status == "disabled" && !status.Options.RemoteAllowed && !status.Options.LearningEnabled &&
                !status.Options.SummaryEnabled && status.RequestsToday == 0 && status.LastAttempt is null, "retired automation appears authorized");
            Check(!await f.Service.RunAutomaticAsync(null, Options, "synthetic-key"), "retired automatic execution succeeded");
            Check(await new TaskStore(f.Data).RemoteAllowedAsync() is null && !f.Service.Learning.HasState, "automation granted new permissions");
            f.NoLegacyState(); f.NoHttp();
        }
    }
    private static async Task ReconcileRetired()
    {
        using var f = new Fixture(); await f.Initialize(); var tasks = new TaskStore(f.Data); await tasks.SetRemoteAllowedAsync(true);
        using (var lease = await tasks.CreateGenerationLeaseAsync(null, new string('b', 64), 0)) await tasks.FailAsync(lease, "provider_http");
        await f.Retire(layout: true); await f.Service.ReconcileGenerationTasksAsync(); f.NoLegacyState(); f.NoHttp();
    }
    private static async Task StartupResumeRetired()
    {
        using var f = new Fixture(); await f.Initialize(); var tasks = new TaskStore(f.Data); await tasks.SetRemoteAllowedAsync(true);
        var queued = await tasks.EnqueueGenerationAsync(null, new string('c', 64), 0); string failed;
        using (var lease = await tasks.CreateGenerationLeaseAsync(null, new string('d', 64), 0))
        { failed = lease.Item.Id; await tasks.FailAsync(lease, "provider_http"); }
        var old = Path.Combine(f.Data, "context-state", "global"); Directory.CreateDirectory(old);
        await File.WriteAllTextAsync(Path.Combine(old, "state.json"), "UNREADABLE_LEGACY_SCOPE_SENTINEL");
        var migration = new LegacyDataService(f.Data); await migration.MigrateAsync();
        var retiredQueued = await tasks.GetAsync(queued); var retiredFailed = await tasks.GetAsync(failed);
        Check(retiredQueued is { Kind: "generate", State: "cancelled", ErrorCode: "legacy_retired", Attempts: 0 } &&
            retiredFailed is { Kind: "generate", State: "failed", Attempts: 0 }, "migration did not retain/cancel legacy generation logs");
        await f.Store.ResumePendingAsync(); await f.Service.ReconcileGenerationTasksAsync();
        await new ContextService(f.Store, f.Http).ReconcileGenerationTasksAsync();
        Check(await tasks.GetAsync(queued) == retiredQueued && await tasks.GetAsync(failed) == retiredFailed &&
            (await tasks.ListAsync()).Count == 2, "startup retried, changed or removed legacy logs");
        Check((await migration.MigrateAsync()).Status == "already", "startup recreated retired storage and conflicted with migration");
        foreach (var name in new[] { "objects", "context-state", "preference-state", "recovery" })
            Check(!Directory.Exists(Path.Combine(f.Data, name)), "startup recreated retired directory: " + name);
        f.NoHttp();
    }
    private static async Task ShareWithoutEnabled()
    {
        using var f = new Fixture(); await f.Initialize(); await f.Service.SetEnabledAsync(f.Project, false);
        Check(!(await f.Service.ReadAsync(f.Project)).Enabled, "legacy not disabled");
        await f.Service.Learning.SetPolicyAsync(f.Project, new() { Share = true }); await f.Retire(layout: true);
        var read = await f.Service.ReadLearningAsync(f.Project, "overview", "", f.Home);
        Check(read.ContainsKey("overview.md") && File.Exists(f.Service.Learning.ProjectionPath(f.Project)), "legacy Enabled gated independent Share"); f.NoHttp();
    }
    private static async Task EnabledWithoutShare()
    {
        using var f = new Fixture(); await f.Initialize(); await f.Service.SetEnabledAsync(f.Project, true);
        Check((await f.Service.ReadAsync(f.Project)).Enabled, "legacy not enabled"); await f.Retire(layout: false);
        var read = await f.Service.ReadLearningAsync(f.Project, "overview", "", f.Home);
        Check(read.Count == 0 && !(await f.Service.Learning.PolicyAsync(f.Project)).Share && !File.Exists(f.Service.Learning.ProjectionPath(f.Project)),
            "legacy Enabled granted Share"); f.NoHttp();
    }
    private static async Task ReadWithoutLegacyState()
    {
        foreach (var layout in new[] { false, true })
        {
            using var f = new Fixture(); await f.Initialize(); await f.Retire(layout);
            Check((await f.Service.ReadLearningAsync(f.Project, "overview", "", f.Home)).Count == 0, "new read shared without consent");
            f.NoLegacyState(); await f.Service.Learning.SetPolicyAsync(f.Project, new() { Share = true });
            Check((await f.Service.ReadLearningAsync(f.Project, "overview", "", f.Home)).ContainsKey("overview.md"), "authorized new read failed");
            f.NoLegacyState(); f.NoHttp();
        }
    }
    private static async Task LegacyRevocation()
    {
        using var f = new Fixture(); await f.Initialize(); await f.Retire(layout: true);
        var before = await f.Service.Learning.SetPolicyAsync(f.Project, new() { Capture = true, Learn = true, Share = true });
        await f.Service.ReadLearningAsync(f.Project, "overview", "", f.Home);
        await f.Service.SetEnabledAsync(f.Project, false);
        var after = await f.Service.Learning.PolicyAsync(f.Project);
        Check(!after.Share && after.Capture == before.Capture && after.Learn == before.Learn && after.Revision > before.Revision, "legacy false did not revoke just sharing");
        Check(await f.Service.Learning.PublishProjectionAsync(f.Project, true) is null && !File.Exists(f.Service.Learning.ProjectionPath(f.Project)), "stale caller revived projection");
        Check((await f.Service.ReadLearningAsync(f.Project, "overview", "", f.Home)).Count == 0, "new read ignored revocation"); f.NoHttp();
    }
    private static async Task CompatibilityPointer()
    {
        using var f = new Fixture(); await f.Initialize(); await f.Retire(layout: true);
        await f.Service.Learning.SetPolicyAsync(f.Project, new() { Share = true });
        await f.Service.ReadLearningAsync(f.Project, "overview", "", f.Home);
        var canonical = f.Service.Learning.ProjectionPath(f.Project);
        var path = Path.Combine(f.Data, "context", Path.GetFileName(Path.GetDirectoryName(canonical)!), "START.md");
        var text = await File.ReadAllTextAsync(path);
        var relative = Path.GetRelativePath(Path.GetDirectoryName(path)!, canonical).Replace('\\', '/');
        Check(text.Contains("](" + relative + ")", StringComparison.Ordinal) && text.Contains("当前用户要求优先", StringComparison.Ordinal), "missing safe compatibility pointer");
        Check(!text.Contains("generations/", StringComparison.Ordinal) && !text.Contains("synthetic-key", StringComparison.Ordinal), "compatibility embeds stale content or credentials");
        f.NoLegacyState(); f.NoHttp();
    }
    private static async Task PreserveLegacyEntry()
    {
        using var f = new Fixture(); await f.Initialize(); await f.Retire(layout: false);
        var canonical = f.Service.Learning.ProjectionPath(f.Project);
        var path = Path.Combine(f.Data, "context", Path.GetFileName(Path.GetDirectoryName(canonical)!), "START.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path, "PREMIGRATION_MANUAL_ENTRY");
        await f.Service.Learning.SetPolicyAsync(f.Project, new() { Share = true }); await f.Service.ReadLearningAsync(f.Project, "overview", "", f.Home);
        Check(await File.ReadAllTextAsync(path) == "PREMIGRATION_MANUAL_ENTRY", "unmigrated entry overwritten"); f.NoLegacyState(); f.NoHttp();
    }
    private static async Task BackgroundWithoutConsent()
    {
        using var f = new Fixture(); await f.Initialize(); await f.Retire(layout: false);
        Check(!await f.Service.RunLearningBackgroundAsync(f.Project, f.Home, Options, "synthetic-key", true), "marker authorized background work");
        var p = await f.Service.Learning.PolicyAsync(f.Project);
        Check(!p.Capture && !p.Learn && !p.Remote && !p.Share, "background upgraded permissions"); f.NoHttp();
    }

    private static async Task Retired(Func<Task> run)
    {
        try { await run(); } catch (InvalidOperationException e) when (e.Message.Contains("旧版", StringComparison.Ordinal)) { return; }
        throw new Exception("legacy entry was not explicitly retired");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Amifolio-session-only-" + Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(root, "data");
        internal string Home => Path.Combine(root, "codex");
        internal string Project = "";
        internal readonly PortfolioStore Store;
        internal readonly ContextService Service;
        internal readonly HttpClient Http;
        private readonly NoNetwork handler = new();
        internal Fixture() { Store = new(Data); Http = new(handler); Service = new(Store, Http); }
        internal async Task Initialize()
        {
            Directory.CreateDirectory(Home); var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            await Store.InitializeAsync(); Store.EnableCodexReferenceMode(Home);
            await File.WriteAllTextAsync(Path.Combine(Home, ".codex-global-state.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            { ["local-projects"] = new Dictionary<string, object> { ["synthetic"] = new { id = "synthetic", name = "synthetic", rootPaths = new[] { source } } } }));
            await Store.RefreshCodexProjectsAsync(); Project = Store.CodexProjects.Single().Id;
        }
        internal async Task Retire(bool layout)
        {
            if (layout) await File.WriteAllTextAsync(Path.Combine(Data, "storage-layout.json"), "{\"version\":2}");
            else await Service.EnableSessionLearningOnlyAsync();
        }
        internal void NoLegacyState()
        {
            foreach (var name in new[] { "context-state", "preference-state" }) Check(!Directory.Exists(Path.Combine(Data, name)), "legacy state created: " + name);
        }
        internal void NoHttp() => Check(handler.Calls == 0, "retired/session-only path sent HTTP");
        public void Dispose() { Http.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            throw new HttpRequestException("Synthetic test blocks all network", null, HttpStatusCode.ServiceUnavailable);
        }
    }
}
