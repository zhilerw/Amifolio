using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Amifolio.Context;
using Amifolio.Core;

if(args.Length==3&&args[0]=="--live-provider")return await LiveProviderCheck.Run(args[1],args[2]);
if(args.Length==3&&args[0]=="--stage6-live-provider")return await Stage6LiveProviderCheck.Run(args[1],args[2]);
if(args.Length==3&&args[0]=="--stage8-host")return await Stage8HostCheck.Run(args[1],args[2]);
if(args.Length==3&&args[0]=="--stage8-live-provider")return await Stage8LiveProviderCheck.Run(args[1],args[2]);
if (args.Length == 5 && args[0] == "--stage2-crash") return await StorageFaultCases.Worker(args);
var tests = new (string Name, Func<Task> Run)[]
{
    ("honest disabled defaults", Initial),
    ("strict global/project isolation and no generated feedback", Isolation),
    ("immutable generation and atomic pointer links", Publication),
    ("manual edits survive refresh and model candidates", Manual),
    ("direct file edits and deletions are protected", ExternalDocuments),
    ("deletion survives restart and removes historic exports", Deletion),
    ("disable withdraws all shared documents", Disable),
    ("document names and project IDs cannot traverse", Traversal),
    ("remote requires consent and secure endpoint", Consent),
    ("bounded scoped HTTP payload and candidates only", Remote),
    ("optional evidence candidates remain read-only and adopt explicitly", OptionalCandidates),
    ("candidate deletion survives a later generation", CandidateDeletion),
    ("removed sources purge stale exports and candidates", SourceWithdrawal),
    ("original paths are separately opt-in and scope filtered", OriginalPaths),
    ("revoking original access purges historical paths", RevokePaths),
    ("inflight results cannot revive deletion or disabled scope", StaleGeneration),
    ("removed source invalidates in-flight result", RemovedSource),
    ("provider errors and cancellation never persist credentials", ProviderFailure),
    ("no evidence cannot generate invented memory", NoEvidence),
    ("credential headers and quoted secrets are filtered", CredentialFormats),
    ("credential filename variants never enter previews exports or provider payloads", CredentialFiles),
    ("real loopback HTTP summary-only provider", LoopbackProvider),
    ("AGENTS preserves user text and updates idempotently", AgentsManaged),
    ("AGENTS ordinary Amifolio and portfolio prose is preserved", AgentsProse),
    ("AGENTS rejects malformed and duplicate markers", AgentsMarkers),
    ("AGENTS stale content conflicts without overwrite", AgentsConflict),
    ("AGENTS exact backup and UTF16 BOM preservation", AgentsEncoding),
    ("AGENTS missing-file race and cancellation", AgentsCreate),
};
tests = tests.Concat(ConfigurationCases.All).ToArray();
tests = tests.Concat(StorageFaultCases.All).ToArray();
tests = tests.Concat(TaskCases.All).ToArray();
tests = tests.Concat(IntegrationCases.All).ToArray();
tests = tests.Concat(AgentsBoundaryCases.All).ToArray();
tests = tests.Concat(ReferenceContextCases.All).ToArray();
tests = tests.Concat(AutomationCases.All).ToArray();
tests = tests.Concat(PreferenceCases.All).Concat(Stage6IntegrationCases.All).ToArray();
tests = tests.Concat(EnvironmentCases.All).ToArray();
tests = tests.Concat(LearningContextCases.All).ToArray();
tests = tests.Concat(SetupUpdateCases.All).ToArray();
tests = tests.Concat(ReleaseCases.All).Concat(SessionOnlyCases.All).Concat(LearningBackgroundBoundaryCases.All).ToArray();
var failed = 0;
var selected = args.Length == 2 && args[0] == "--filter" ? tests.Where(t => t.Name.Contains(args[1], StringComparison.OrdinalIgnoreCase)).ToArray() : tests;
foreach (var test in selected)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL {test.Name}: {e.GetType().Name}: {e.Message}"); }
}
Console.WriteLine($"{selected.Length - failed}/{selected.Length} passed");
return failed == 0 ? 0 : 1;

static async Task Initial()
{
    using var f = new Fixture();
    var s = await f.Service.ReadAsync();
    Check(!s.Enabled, "sharing must be opt-in");
    Check(s.Documents.Count == 6 && s.Documents.ContainsKey("START.md"), "six editable defaults");
    Check(s.Documents["memory.md"].Contains("尚无") && s.Documents["style.md"].Contains("尚无"), "no invented preferences");
    Check(!File.Exists(Path.Combine(s.Directory, "START.md")), "disabled defaults must not export");
}
static async Task Isolation()
{
    using var f = new Fixture();
    f.Store.Add("g", null, "用户", "GLOBAL_ONLY");
    f.Store.Add("a", "a", "用户", "PROJECT_A_SECRET");
    f.Store.Add("b", "b", "用户", "PROJECT_B_SECRET");
    f.Store.Add("self", null, "应用", "GENERATED_LOOP");
    foreach (var scope in new string?[] { null, "a", "b" })
    {
        await f.Service.SetEnabledAsync(scope, true);
        await f.Service.PublishAsync(scope);
        var s = await f.Service.ReadAsync(scope);
        var all = string.Join("\n", s.Documents.Values) + await ExportText(s.Directory);
        Has(all, scope is null ? "GLOBAL_ONLY" : scope == "a" ? "PROJECT_A_SECRET" : "PROJECT_B_SECRET");
        Lacks(all, scope is null ? "PROJECT_A_SECRET" : "GLOBAL_ONLY");
        Lacks(all, scope == "b" ? "PROJECT_A_SECRET" : "PROJECT_B_SECRET");
        Lacks(all, "GENERATED_LOOP");
    }
}
static async Task Publication()
{
    using var f = new Fixture();
    await f.Service.SetEnabledAsync(null, true);
    var s = await f.Service.ReadAsync();
    var first = Directory.GetDirectories(Path.Combine(s.Directory, "generations")).Single();
    var bytes = await File.ReadAllBytesAsync(Path.Combine(first, "overview.md"));
    f.Store.Add("next", null, "用户", "NEW_ITEM");
    await f.Service.PublishAsync();
    var retained = await File.ReadAllBytesAsync(Path.Combine(first, "overview.md"));
    Check(bytes.SequenceEqual(retained), "old generation mutated");
    var start = await File.ReadAllTextAsync(Path.Combine(s.Directory, "START.md"));
    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(start, @"generations/[a-f0-9]+/[a-z]+\.md"))
        Check(File.Exists(Path.Combine(s.Directory, m.Value)), "START points at missing document");
    Has(start, "overview.md"); Has(start, "sources.md");
}
static async Task Manual()
{
    using var f = new Fixture();
    f.Store.Add("manual-source", null, "用户", "Evidence");
    await f.Service.SaveDocumentAsync(null, "memory.md", "CONFIRMED_MANUAL");
    await f.Service.SetEnabledAsync(null, true);
    await f.Service.PublishAsync();
    await f.Service.GenerateAsync(null, new("https://provider.invalid/v1", "test-model", true), "session-key");
    var s = await f.Service.ReadAsync();
    Check(s.Documents["memory.md"] == "CONFIRMED_MANUAL", "model overwrote manual memory");
    Check(s.CandidateDocuments.ContainsKey("candidate-summary.md"), "candidate missing");
    Lacks(await ExportText(s.Directory), "MODEL_CANDIDATE");
}
static async Task ExternalDocuments()
{
    using var f = new Fixture();
    await f.Service.SetEnabledAsync(null, true);
    var s = await f.Service.ReadAsync();
    var gen = Directory.GetDirectories(Path.Combine(s.Directory, "generations")).Single();
    await File.WriteAllTextAsync(Path.Combine(gen, "style.md"), "EXTERNAL_CONFIRMED");
    File.Delete(Path.Combine(gen, "summary.md"));
    await f.Service.PublishAsync();
    s = await new ContextService(f.Store).ReadAsync();
    Check(s.Documents["style.md"] == "EXTERNAL_CONFIRMED", "external edit lost");
    Check(!s.Documents.ContainsKey("summary.md"), "external deletion revived");
}
static async Task Deletion()
{
    using var f = new Fixture();
    f.Store.Add("deletion-source", null, "用户", "Evidence");
    await f.Service.SaveDocumentAsync(null, "memory.md", "MUST_BE_ERASED");
    await f.Service.SetEnabledAsync(null, true);
    await f.Service.PublishAsync();
    await f.Service.DeleteDocumentAsync(null, "memory.md");
    var other = new ContextService(f.Store, f.Client);
    await other.PublishAsync();
    await other.GenerateAsync(null, new("https://provider.invalid/v1", "test-model", true), "session-key");
    var s = await other.ReadAsync();
    Check(!s.Documents.ContainsKey("memory.md") && !s.CandidateDocuments.ContainsKey("candidate-memory.md"), "tombstone lost");
    Lacks(await ExportText(s.Directory), "MUST_BE_ERASED");
    await other.DeleteDocumentAsync(null, "START.md");
    await other.PublishAsync();
    Check(!File.Exists(Path.Combine(s.Directory, "START.md")), "START deletion revived");
}
static async Task Disable()
{
    using var f = new Fixture();
    await f.Service.SaveDocumentAsync("p", "summary.md", "PRIVATE_SHARED");
    await f.Service.SetEnabledAsync("p", true);
    var s = await f.Service.ReadAsync("p");
    await f.Service.SetEnabledAsync("p", false);
    await new ContextService(f.Store).PublishAsync("p");
    Lacks(await ExportText(s.Directory), "PRIVATE_SHARED");
    Check(!File.Exists(Path.Combine(s.Directory, "START.md")), "disabled START remains");
    await Throws<InvalidOperationException>(() => f.Service.GenerateAsync("p", new("https://provider.invalid/v1", "x", false), "key"));
}
static async Task Traversal()
{
    using var f = new Fixture();
    await Throws<ArgumentException>(() => f.Service.SaveDocumentAsync(null, "../escape.md", "bad"));
    await Throws<ArgumentException>(() => f.Service.DeleteDocumentAsync(null, "MEMORY.md"));
    var global = await f.Service.ReadAsync();
    var project = await f.Service.ReadAsync("../../private");
    Check(project.Directory.StartsWith(f.Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "project escaped data root");
    Check(global.Directory != project.Directory, "scope alias");
}
static async Task Consent()
{
    using var f = new Fixture();
    f.Store.Add("consent-source", null, "用户", "Evidence");
    await f.Service.SetEnabledAsync(null, true);
    await Throws<InvalidOperationException>(() => f.Service.GenerateAsync(null, new("https://provider.invalid/v1", "x", false), "key"));
    foreach (var endpoint in new[] { "http://provider.invalid/v1", "file:///C:/secret", "https://user:pass@provider.invalid", "https://provider.invalid/v1?key=x" })
        await Throws<ArgumentException>(() => f.Service.GenerateAsync(null, new(endpoint, "x", true), "key"));
    Check(f.Handler.Calls == 0, "invalid request reached network");
    await f.Service.GenerateAsync(null, new("http://127.0.0.1:12345/v1", "x", true), "key");
    Check(f.Handler.Calls == 1, "loopback development endpoint rejected");
}
static async Task Remote()
{
    using var f = new Fixture();
    f.Store.Add("g", null, "用户", "GLOBAL_PUBLIC", "VISIBLE_SNIPPET api_key=VERY_SECRET_VALUE " + new string('x', 150000));
    f.Store.Add("p", "p", "用户", "PROJECT_SECRET", "PRIVATE_RAW");
    f.Store.Add("app", null, "应用", "RECURSIVE", "DERIVED_RAW");
    f.Store.Add("env", null, "用户", ".env", "ENV_SECRET");
    await f.Service.SetEnabledAsync(null, true);
    await f.Service.GenerateAsync(null, new("https://provider.invalid/v1/", "test-model", true), "SESSION_KEY_NEVER_PERSIST");
    Check(f.Handler.Url == "https://provider.invalid/v1/chat/completions", "wrong provider route");
    Check(f.Handler.Authorization == "Bearer SESSION_KEY_NEVER_PERSIST", "missing session authentication");
    var payload = f.Handler.Payload!;
    Has(payload, "VISIBLE_SNIPPET"); Lacks(payload, "PRIVATE_RAW"); Lacks(payload, "PROJECT_SECRET");
    Lacks(payload, "DERIVED_RAW"); Lacks(payload, "ENV_SECRET"); Lacks(payload, "VERY_SECRET_VALUE");
    Check(Encoding.UTF8.GetByteCount(payload) < 70000, "unbounded remote payload");
    Lacks(await ExportText(f.Root), "SESSION_KEY_NEVER_PERSIST");
    var s = await f.Service.ReadAsync();
    Has(s.CandidateDocuments["candidate-summary.md"], "MODEL_CANDIDATE");
    Lacks(s.Documents["summary.md"], "MODEL_CANDIDATE");
}
static async Task StaleGeneration()
{
    foreach (var disable in new[] { false, true })
    {
        using var f = new Fixture();
        f.Store.Add("stale-source", null, "用户", "Evidence");
        await f.Service.SetEnabledAsync(null, true);
        f.Handler.Block = true;
        var pending = f.Service.GenerateAsync(null, new("https://provider.invalid/v1", "x", true), "key");
        await f.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (disable) await new ContextService(f.Store).SetEnabledAsync(null, false);
        else await new ContextService(f.Store).DeleteDocumentAsync(null, "summary.md");
        f.Handler.Release.TrySetResult();
        await Throws<InvalidOperationException>(() => pending);
        var s = await f.Service.ReadAsync();
        Check(s.CandidateDocuments.Count == 0, "stale request wrote results");
    }
}
static async Task OptionalCandidates()
{
    using var f = new Fixture();
    f.Handler.ModelContent = "{\"summary.md\":\"SUMMARY_EVIDENCE\",\"memory.md\":\"MEMORY_EVIDENCE\",\"style.md\":\"STYLE_EVIDENCE\",\"overview.md\":\"OVERVIEW_EVIDENCE\"}";
    f.Store.Add("evidence-id", null, "用户", "user-confirmed", "Explicit project constraint");
    await f.Service.SaveDocumentAsync(null, "memory.md", "CONFIRMED_MEMORY");
    await f.Service.SetEnabledAsync(null, true);
    await f.Service.GenerateAsync(null, new("https://provider.invalid/v1", "x", true), "key");
    var s = await f.Service.ReadAsync();
    Check(s.CandidateDocuments.Count == 4, "optional model documents discarded");
    foreach (var name in new[] { "summary", "memory", "style", "overview" })
    {
        var candidate = s.CandidateDocuments[$"candidate-{name}.md"];
        Has(candidate, "evidence-id"); Has(candidate, "待确认");
        await Throws<ArgumentException>(() => f.Service.SaveDocumentAsync(null, $"candidate-{name}.md", "bypass"));
    }
    Check(s.Documents["memory.md"] == "CONFIRMED_MEMORY", "candidate adopted automatically");
    await f.Service.SaveDocumentAsync(null, "style.md", s.CandidateDocuments["candidate-style.md"]);
    Has((await f.Service.ReadAsync()).Documents["style.md"], "STYLE_EVIDENCE");
}
static async Task CandidateDeletion()
{
    using var f = new Fixture();
    f.Store.Add("candidate-source", null, "用户", "Evidence");
    f.Handler.ModelContent = "{\"summary.md\":\"SUM\",\"style.md\":\"STYLE\"}";
    await f.Service.SetEnabledAsync(null, true);
    await f.Service.GenerateAsync(null, new("https://provider.invalid/v1", "x", true), "key");
    await f.Service.DeleteDocumentAsync(null, "candidate-style.md");
    await new ContextService(f.Store, f.Client).GenerateAsync(null, new("https://provider.invalid/v1", "x", true), "key");
    var s = await f.Service.ReadAsync();
    Check(!s.CandidateDocuments.ContainsKey("candidate-style.md"), "deleted candidate revived");
    Check(s.CandidateDocuments.ContainsKey("candidate-summary.md"), "unrelated generation blocked");
    await f.Service.DeleteDocumentAsync(null, "style.md");
    await f.Service.GenerateAsync(null, new("https://provider.invalid/v1", "x", true), "key");
    Check(!(await f.Service.ReadAsync()).Documents.ContainsKey("style.md"), "formal tombstone lost");
}
static async Task SourceWithdrawal()
{
    using var f = new Fixture();
    f.Store.Add("private-source", null, "用户", "WITHDRAWN_SOURCE");
    await f.Service.SetEnabledAsync(null, true);
    await f.Service.GenerateAsync(null, new("https://provider.invalid/v1", "x", true), "key");
    f.Store.Items.Clear();
    var s = await f.Service.ReadAsync();
    Check(s.CandidateDocuments.Count == 0, "source removal kept candidates");
    Lacks(await ExportText(s.Directory), "WITHDRAWN_SOURCE");
}
static async Task OriginalPaths()
{
    using var f = new Fixture();
    f.Store.Add("global-object", null, "用户", "Global");
    f.Store.Add("project-object", "p", "用户", "Private");
    f.Store.Add("app-object", null, "应用", "Generated");
    await f.Service.SetEnabledAsync(null, true);
    var global = await f.Service.ReadAsync();
    Check(!global.OriginalAccessAllowed, "raw access not opt-in");
    Lacks(await ExportText(global.Directory), Path.Combine(f.Root, "objects"));
    await f.Service.SetOriginalAccessAsync(null, true);
    global = await f.Service.ReadAsync();
    Check(global.OriginalAccessAllowed, "raw access state missing");
    var published = await ExportText(global.Directory);
    Has(published, Path.Combine(f.Root, "objects", "global-object"));
    Lacks(published, Path.Combine(f.Root, "objects", "project-object"));
    Lacks(published, Path.Combine(f.Root, "objects", "app-object"));
    await f.Service.SetOriginalAccessAsync("p", true);
    await f.Service.SetEnabledAsync("p", true);
    var project = await f.Service.ReadAsync("p");
    published = await ExportText(project.Directory);
    Has(published, Path.Combine(f.Root, "objects", "project-object"));
    Lacks(published, Path.Combine(f.Root, "objects", "global-object"));
    Check((await f.Service.PreviewGenerationAsync()).Contains("global-object"), "source missing from preview");
    Lacks(await f.Service.PreviewGenerationAsync(), Path.Combine(f.Root, "objects"));
}
static async Task RevokePaths()
{
    using var f = new Fixture();
    f.Store.Add("raw-object", null, "用户", "Raw");
    await f.Service.SetOriginalAccessAsync(null, true);
    await f.Service.SetEnabledAsync(null, true);
    await f.Service.PublishAsync();
    await f.Service.SetOriginalAccessAsync(null, false);
    var service = new ContextService(f.Store);
    await service.PublishAsync();
    var s = await service.ReadAsync();
    Check(!s.OriginalAccessAllowed, "revocation not persisted");
    Lacks(await ExportText(s.Directory), Path.Combine(f.Root, "objects", "raw-object"));
}
static async Task RemovedSource()
{
    using var f = new Fixture();
    f.Store.Add("source", null, "用户", "SOURCE");
    await f.Service.SetEnabledAsync(null, true);
    f.Handler.Block = true;
    var pending = f.Service.GenerateAsync(null, new("https://provider.invalid/v1", "x", true), "key");
    await f.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    f.Store.Items.Clear();
    f.Handler.Release.TrySetResult();
    await Throws<InvalidOperationException>(() => pending);
    Check((await f.Service.ReadAsync()).CandidateDocuments.Count == 0, "deleted source accepted");
}
static async Task ProviderFailure()
{
    using var f = new Fixture();
    f.Store.Add("failure-source", null, "用户", "Evidence");
    await f.Service.SetEnabledAsync(null, true);
    f.Handler.Status = HttpStatusCode.Unauthorized;
    await Throws<HttpRequestException>(() => f.Service.GenerateAsync(null, new("https://provider.invalid/v1", "x", true), "session-key"));
    Check((await f.Service.ReadAsync()).CandidateDocuments.Count == 0, "failed request wrote candidates");
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    await Throws<OperationCanceledException>(() => f.Service.SaveDocumentAsync(null, "memory.md", "cancelled", cancel.Token));
    Lacks(string.Join("", (await f.Service.ReadAsync()).Documents.Values), "cancelled");
}
static async Task NoEvidence()
{
    using var f = new Fixture();
    f.Store.Add("app", null, "应用", "Not independent evidence");
    await f.Service.SetEnabledAsync(null, true);
    await Throws<InvalidOperationException>(() => f.Service.GenerateAsync(null, new("https://provider.invalid/v1", "x", true), "key"));
    Check(f.Handler.Calls == 0 && (await f.Service.ReadAsync()).CandidateDocuments.Count == 0, "empty evidence reached model");
}
static async Task CredentialFormats()
{
    using var f = new Fixture();
    f.Store.Add("secrets", null, "用户", "Evidence", "Authorization: Bearer HEADER_SECRET\n{\"api_key\":\"QUOTED_SECRET\",\"refresh_token\":\"REFRESH_SECRET\"}\nhttps://user:URL_PASSWORD@example.invalid/path\npublic data");
    await f.Service.SetEnabledAsync(null, true);
    Lacks(await f.Service.PreviewGenerationAsync(), "REFRESH_SECRET");
    await f.Service.GenerateAsync(null, new("https://provider.invalid/v1", "x", true), "key");
    var payload = f.Handler.Payload!;
    Lacks(payload, "HEADER_SECRET"); Lacks(payload, "QUOTED_SECRET"); Lacks(payload, "URL_PASSWORD"); Lacks(payload, "REFRESH_SECRET"); Has(payload, "public data");
}
static async Task CredentialFiles()
{
    using var f = new Fixture();
    f.Store.Add("public", null, "用户", "Public report.md", "PUBLIC_EVIDENCE");
    string[] names = ["credentials.production.json", "CREDENTIALS.dev.JSON", "secrets.local.json", "secret.json", ".env.production", ".envrc", ".npmrc", ".pypirc", ".netrc", "id_dsa", "id_ecdsa", "auth.p12", "auth.p8", "auth.keystore", "auth.jks", "passwords.kdbx"];
    foreach (var (name, index) in names.Select((name, index) => (name, index)))
        f.Store.Add("credential-" + index, null, "用户", name, "{\"refresh_token\":\"REFRESH_SENTINEL\"}");
    await f.Service.SetEnabledAsync(null, true);
    var preview = await f.Service.PreviewGenerationAsync();
    Has(preview, "PUBLIC_EVIDENCE");
    Lacks(preview, "REFRESH_SENTINEL");
    foreach (var name in names) Lacks(preview, name);
    await f.Service.GenerateAsync(null, new("https://provider.invalid/v1", "x", true), "key");
    Lacks(f.Handler.Payload!, "REFRESH_SENTINEL");
    foreach (var name in names) Lacks(f.Handler.Payload!, name);
    var exports = await ExportText((await f.Service.ReadAsync()).Directory);
    foreach (var name in names) Lacks(exports, name);
    Has(exports, "Public report.md");
}
static async Task LoopbackProvider()
{
    using var f = new Fixture();
    f.Store.Add("http-evidence", null, "用户", "HTTP source");
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    var port = ((IPEndPoint)listener.LocalEndpoint).Port;
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var serve = Task.Run(async () =>
    {
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        await using var stream = client.GetStream();
        var header = new List<byte>();
        var b = new byte[1];
        while (header.Count < 16000)
        {
            if (await stream.ReadAsync(b, timeout.Token) == 0) throw new Exception("incomplete HTTP request");
            header.Add(b[0]);
            if (header.Count >= 4 && Encoding.ASCII.GetString(header.TakeLast(4).ToArray()) == "\r\n\r\n") break;
        }
        var headers = Encoding.ASCII.GetString(header.ToArray());
        var length = int.Parse(System.Text.RegularExpressions.Regex.Match(headers, @"(?i)Content-Length: (\d+)").Groups[1].Value);
        var body = new byte[length]; await stream.ReadExactlyAsync(body, timeout.Token);
        Has(headers, "POST /v1/chat/completions HTTP/1.1"); Has(headers, "Authorization: Bearer loopback-session");
        Has(Encoding.UTF8.GetString(body), "http-evidence");
        var responseBody = JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = "{\"summary.md\":\"LOOPBACK_HTTP_SUMMARY\"}" } } } });
        var bytes = Encoding.UTF8.GetBytes(responseBody);
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), timeout.Token);
        await stream.WriteAsync(bytes, timeout.Token);
    }, timeout.Token);
    var service = new ContextService(f.Store);
    await service.SetEnabledAsync(null, true);
    await service.GenerateAsync(null, new($"http://127.0.0.1:{port}/v1", "model", true), "loopback-session", timeout.Token);
    await serve;
    var s = await service.ReadAsync();
    Check(s.CandidateDocuments.Count == 1, "summary-only compatible response rejected");
    Has(s.CandidateDocuments["candidate-summary.md"], "LOOPBACK_HTTP_SUMMARY");
}
static async Task AgentsManaged()
{
    using var f = new Fixture();
    var service = new AgentsFileService();
    var path = Path.Combine(f.Root, "AGENTS.md");
    const string original = "# 我的规则\r\n先思考。\r\n";
    await File.WriteAllTextAsync(path, original);
    var s = await service.ReadAsync(path);
    var context = Path.Combine(f.Root, "中文 with space");
    var preview = service.PreviewManagedBlock(s, context, true);
    Check(preview.StartsWith(original, StringComparison.Ordinal), "outside text changed");
    Has(preview, Path.Combine(context, "START.md")); Has(preview, "folio:begin scope=");
    var backup = await service.SaveAsync(s, preview);
    Check(backup is not null && await File.ReadAllTextAsync(backup) == original, "backup missing");
    s = await service.ReadAsync(path);
    Check(service.PreviewManagedBlock(s, context, true) == preview, "repeated integration duplicates block");
    var removed = service.PreviewManagedBlock(s, context, false);
    Check(removed.StartsWith(original, StringComparison.Ordinal), "disconnect lost user text");
    Lacks(removed, "folio:");
    await Throws<ArgumentException>(() => Task.FromResult(service.PreviewManagedBlock(s, context + "\nINJECT", true)));
}
static async Task AgentsMarkers()
{
    using var f = new Fixture();
    var service = new AgentsFileService();
    var path = Path.Combine(f.Root, "AGENTS.md");
    foreach (var content in new[] { "<!-- folio:begin scope=x -->\nmissing end", "<!-- folio:end -->", "<!-- folio:begin scope=x -->\n<!-- folio:end -->\n<!-- folio:begin scope=y -->\n<!-- folio:end -->", "<!-- folio:begin corrupted -->\n<!-- folio:end -->", "inline <!-- folio:begin scope=x -->\n<!-- folio:end -->" })
    {
        await File.WriteAllTextAsync(path, content);
        var s = await service.ReadAsync(path);
        await Throws<InvalidDataException>(() => Task.FromResult(service.PreviewManagedBlock(s, f.Root, true)));
        await Throws<InvalidDataException>(() => service.SaveAsync(s, content));
        Check(await File.ReadAllTextAsync(path) == content, "malformed content changed");
    }
}
static async Task AgentsProse()
{
    using var f = new Fixture();
    var path = Path.Combine(f.Root, "AGENTS.md");
    const string prose = "# Amifolio: user rules\nportfolio: ordinary prose\n<!-- unrelated: keep -->\n";
    await File.WriteAllTextAsync(path, prose);
    var service = new AgentsFileService();
    var snapshot = await service.ReadAsync(path);
    var preview = service.PreviewManagedBlock(snapshot, f.Root, true);
    Check(preview.StartsWith(prose, StringComparison.Ordinal), "ordinary text changed");
    await service.SaveAsync(snapshot, preview);
    snapshot = await service.ReadAsync(path);
    Check(service.PreviewManagedBlock(snapshot, f.Root, true) == preview, "prose broke idempotence");
    Check(service.PreviewManagedBlock(snapshot, f.Root, false).StartsWith(prose, StringComparison.Ordinal), "prose deleted on disconnect");
}
static async Task AgentsConflict()
{
    using var f = new Fixture();
    var path = Path.Combine(f.Root, "AGENTS.md");
    await File.WriteAllTextAsync(path, "original");
    var service = new AgentsFileService();
    var s = await service.ReadAsync(path);
    await File.WriteAllTextAsync(path, "EXTERNAL_EDIT");
    await Throws<IOException>(() => service.SaveAsync(s, "lost update"));
    Check(await File.ReadAllTextAsync(path) == "EXTERNAL_EDIT", "external edit overwritten");
    Check(Directory.GetFiles(f.Root, "*.bak").Length == 0, "conflict created backup");
}
static async Task AgentsEncoding()
{
    using var f = new Fixture();
    var path = Path.Combine(f.Root, "AGENTS.md");
    await File.WriteAllTextAsync(path, "中文\r\n规则\r\n", Encoding.Unicode);
    var original = await File.ReadAllBytesAsync(path);
    var service = new AgentsFileService();
    var s = await service.ReadAsync(path);
    var backup = await service.SaveAsync(s, service.PreviewManagedBlock(s, f.Root, true));
    var current = await File.ReadAllBytesAsync(path);
    Check(current[0] == 0xff && current[1] == 0xfe, "UTF16 BOM lost");
    var backupBytes = backup is null ? [] : await File.ReadAllBytesAsync(backup);
    Check(backup is not null && original.SequenceEqual(backupBytes), "backup bytes changed");
    Has(Encoding.Unicode.GetString(current), "\r\n");
}
static async Task AgentsCreate()
{
    using var f = new Fixture();
    var path = Path.Combine(f.Root, "AGENTS.md");
    var service = new AgentsFileService();
    var s = await service.ReadAsync(path);
    Check(!s.Exists, "missing file shown existing");
    await File.WriteAllTextAsync(path, "created elsewhere");
    await Throws<IOException>(() => service.SaveAsync(s, "overwrite"));
    File.Delete(path);
    var backup = await service.SaveAsync(await service.ReadAsync(path), "new content\n");
    Check(backup is null && await File.ReadAllTextAsync(path) == "new content\n", "new file contract wrong");
    s = await service.ReadAsync(path);
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    await Throws<OperationCanceledException>(() => service.SaveAsync(s, "cancelled", cancel.Token));
    Check(await File.ReadAllTextAsync(path) == "new content\n", "cancelled save changed file");
}
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Has(string text, string part) => Check(text.Contains(part, StringComparison.Ordinal), "missing " + part);
static void Lacks(string text, string part) => Check(!text.Contains(part, StringComparison.Ordinal), "leaked " + part);
static async Task Throws<T>(Func<Task> run) where T : Exception
{
    try { await run(); } catch (T) { return; }
    throw new Exception("expected " + typeof(T).Name);
}
static async Task<string> ExportText(string dir)
{
    if (!Directory.Exists(dir)) return "";
    var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories).Where(p => p.EndsWith(".md") || p.EndsWith(".json"));
    return string.Join("\n", await Task.WhenAll(files.Select(p => File.ReadAllTextAsync(p))));
}
sealed class Fixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "amifolio-context-test-" + Guid.NewGuid().ToString("N"));
    public FakeStore Store { get; }
    public ProviderHandler Handler { get; } = new();
    public HttpClient Client { get; }
    public ContextService Service { get; }
    public Fixture() { Directory.CreateDirectory(Root); Store = new(Root); Client = new(Handler); Service = new(Store, Client); }
    public void Dispose() { Client.Dispose(); Directory.Delete(Root, true); }
}
sealed class ProviderHandler : HttpMessageHandler
{
    public string? Payload, Url, Authorization;
    public int Calls;
    public bool Block;
    public HttpStatusCode Status = HttpStatusCode.OK;
    public string ModelContent = "{\"summary.md\":\"MODEL_CANDIDATE\",\"memory.md\":\"MODEL_GUESS\"}";
    public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++; Url = request.RequestUri!.ToString(); Authorization = request.Headers.Authorization?.ToString();
        Payload = await request.Content!.ReadAsStringAsync(cancellationToken); Started.TrySetResult();
        if (Block) await Release.Task.WaitAsync(cancellationToken);
        return new(Status) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = ModelContent } } } })) };
    }
}
sealed class FakeStore(string dataRoot) : IPortfolioStore
{
    public string DataRoot => dataRoot;
    public List<ArtifactRecord> Items { get; } = [];
    public void Add(string id, string? project, string source, string title, string? preview = null) { preview ??= title; Items.Add(new(id, title, title, "text", source, project, "", Path.Combine(DataRoot, "objects", id), id, preview.Length, DateTimeOffset.UtcNow, 1, "ready", preview)); }
    public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    // Deliberately broad: a storage query with projectId=null can mean 'all'. The service must filter again.
    public Task<IReadOnlyList<ArtifactRecord>> ListAsync(string? search = null, string? projectId = null, string? kind = null, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ArtifactRecord>>(Items.ToArray());
    public Task<ArtifactRecord?> GetAsync(string id, CancellationToken cancellationToken = default) => Task.FromResult(Items.FirstOrDefault(x => x.Id == id));
    public Task<IReadOnlyList<ProjectRecord>> ProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProjectRecord>>([]);
    public Task<ProjectRecord> CreateProjectAsync(string name, string? rootPath = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ImportResult> ImportFileAsync(string path, string? projectId = null, string source = "用户", string tags = "", CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task<ImportResult> ImportRepositoryAsync(string path, string? projectId = null, string source = "用户", CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task UpdateArtifactAsync(string id, string title, string tags, string? projectId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public Task RemoveArtifactAsync(string id, CancellationToken cancellationToken = default) { Items.RemoveAll(x => x.Id == id); return Task.CompletedTask; }
    public Task<string> GetOriginalPathAsync(string id, CancellationToken cancellationToken = default) => throw new NotSupportedException("Context must never read originals implicitly");
    public Task<ImportResult> SaveGeneratedAsync(string name, string content, string? projectId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
}
