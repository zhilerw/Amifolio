using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amifolio.Context;
using Amifolio.Core;

/// <summary>
/// Opt-in installed-host acceptance, never part of the ordinary test suite. The model is a
/// deterministic loopback Responses/SSE fixture, NOT a reasoning model or paid live service.
/// No auth, formal configuration, formal rollout, or formal application data is opened.
/// The parent owns registration of --stage8-host and builds/packages before invoking this.
/// </summary>
public static class Stage8HostCheck
{
    private const string Quote = "以后请简洁回答";
    private const string Style = "偏好简洁回答"; // Application's canonical renderer, not the proposal's prose.
    private const int MaxBody = 4 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    private static readonly string[] ExpectedTools =
    [
        "artifact_submit", "artifact_search", "artifact_status", "projects_list", "context_read",
        "project_resolve", "learning_status", "learning_capture", "learning_claim", "learning_complete", "learning_context"
    ];

    public static async Task<int> Run(string packageRoot, string resultsRoot)
    {
        var checks = new List<string>();
        var failures = new List<string>();
        var children = new List<ChildReport>();
        var phase = "preflight";
        var root = Path.Combine(Path.GetTempPath(), "Amifolio-stage8-host-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "host");
        var user = Path.Combine(root, "user");
        var project = Path.Combine(root, "project");
        var data = Path.Combine(root, "data");
        var agentsMarker = "STAGE8_AGENTS_" + Guid.NewGuid().ToString("N");
        var skillMarker = "STAGE8_SKILL_" + Guid.NewGuid().ToString("N");
        string? reportPath = null;
        Dictionary<string, FileStamp>? before = null;
        LocalProvider? provider = null;
        var sourceSupported = false;
        var noOriginalChanges = false;
        var clean = false;
        var receiptCount = 0;
        long watermark = 0;
        var active = 0;
        string? diagnosticType = null;
        string? diagnosticCode = null;
        object? fixtureMetadataDiagnostics = null;
        void Check(bool condition, string code)
        {
            if (!condition) throw new HostFailure(code);
            checks.Add(code);
        }
        try
        {
            Check(OperatingSystem.IsWindows(), "windows-npm-host-required");
            packageRoot = Path.GetFullPath(packageRoot);
            resultsRoot = Path.GetFullPath(resultsRoot);
            Check(!Within(resultsRoot, packageRoot) && !Within(packageRoot, resultsRoot), "results-separate-from-package");
            Check(!Segments(root).Contains(".codex", StringComparer.OrdinalIgnoreCase), "fixture-outside-dot-codex");
            NoLinks(packageRoot); NoLinks(resultsRoot); NoLinks(root);
            Directory.CreateDirectory(resultsRoot);
            reportPath = Path.Combine(resultsRoot, "report.json");
            Check(!File.Exists(reportPath), "fresh-report-required");
            var bridge = Path.Combine(packageRoot, "bridge", "Amifolio.Bridge.exe");
            var bundledSkill = Path.Combine(packageRoot, "codex", "skills", "amifolio", "SKILL.md");
            NoLinks(bridge); NoLinks(bundledSkill);
            Check(File.Exists(bridge) && File.Exists(bundledSkill), "packaged-bridge-and-skill-present");
            foreach (var path in new[] { root, home, user, project, data, Path.Combine(root, "tmp"),
                Path.Combine(user, "AppData", "Roaming"), Path.Combine(user, "AppData", "Local") }) Directory.CreateDirectory(path);

            // Only the installed npm program is discovered. No npm/auth configuration is read.
            var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var discovery = await Child(powershell,
                ["-NoProfile", "-NonInteractive", "-Command", "(Get-Command codex -CommandType ExternalScript -ErrorAction Stop).Source"],
                "discover-npm-wrapper", root, home, user, project);
            children.Add(discovery.Report);
            Check(discovery.Report.ExitCode == 0 && !discovery.Report.TimedOut && !discovery.Overflow, "codex-npm-wrapper-discovered");
            var wrapper = discovery.Output.Trim();
            Check(Path.IsPathFullyQualified(wrapper) && Path.GetFileName(wrapper).Equals("codex.ps1", StringComparison.OrdinalIgnoreCase)
                && File.Exists(wrapper), "actual-get-command-npm-entry");
            var version = await Child(powershell, WrapperArgs(wrapper, "--version"), "cli-version", root, home, user, project);
            children.Add(version.Report);
            Check(version.Report.ExitCode == 0 && !version.Report.TimedOut && !version.Overflow
                && version.Output.Trim() == "codex-cli 0.163.0", "installed-codex-cli-0.163.0");

            phase = "seed-isolated-fixture";
            Check(new FileInfo(bundledSkill).Length <= 256 * 1024, "bounded-bundled-skill");
            var skill = await File.ReadAllTextAsync(bundledSkill, Utf8);
            Check(skill.StartsWith("---", StringComparison.Ordinal) && skill.Contains("name: amifolio", StringComparison.Ordinal), "bundled-amifolio-skill");
            var skillPath = Path.Combine(project, ".agents", "skills", "amifolio", "SKILL.md");
            Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
            await File.WriteAllTextAsync(skillPath, skill + "\n\nSynthetic host-loading marker: " + skillMarker + "\n", Utf8);
            await File.WriteAllTextAsync(Path.Combine(project, "AGENTS.md"),
                "# Synthetic acceptance workspace\nSynthetic host-loading marker: " + agentsMarker +
                "\nUse the installed amifolio skill for explicitly authorized learning. Project originals stay read-only.\n", Utf8);
            await File.WriteAllTextAsync(Path.Combine(project, "fixture.txt"), "Synthetic source; must remain unchanged.\n", Utf8);
            before = Snapshot(project); // AGENTS and skill are seeded BEFORE the original-file baseline.
            await File.WriteAllTextAsync(Path.Combine(home, ".codex-global-state.json"), JsonSerializer.Serialize(
                new Dictionary<string, object> { ["local-projects"] = new Dictionary<string, object>
                { ["stage8-synthetic"] = new { name = "Stage8 synthetic host", rootPaths = new[] { project } } } }), Utf8);
            var store = new PortfolioStore(data);
            store.EnableCodexReferenceMode(home);
            await store.InitializeAsync();
            await store.RefreshCodexProjectsAsync();
            var projectId = store.CodexProjects.Single().Id;
            var context = new ContextService(store);
            await context.Learning.SetPolicyAsync(projectId, new()
            { Capture = true, Learn = true, AutoActivate = true, Share = true, BatchSize = 1 });
            await context.SetEnabledAsync(projectId, true);

            await using var server = new LocalProvider(project, projectId, agentsMarker, skillMarker);
            provider = server;
            var integration = new CodexIntegrationService(home, user);
            var config = ProviderConfig(server.Port, project);
            await integration.InstallStopHookAsync(bridge,data);
            // Codex's stdio transport may filter inherited variables. Bind the bridge explicitly;
            // an isolated CODEX_HOME on the Codex parent alone is not isolation evidence.
            var mcpEnvironment = new Dictionary<string, string>
            {
                ["CODEX_HOME"] = home, ["USERPROFILE"] = user, ["HOME"] = user,
                ["APPDATA"] = Path.Combine(user, "AppData", "Roaming"),
                ["LOCALAPPDATA"] = Path.Combine(user, "AppData", "Local"),
                ["TEMP"] = Path.GetTempPath(), ["TMP"] = Path.GetTempPath()
            };
            var mcpSnippet = integration.McpSnippet(bridge, data) + "required = true\nstartup_timeout_sec = 20\ntool_timeout_sec = 25\n"
                + "env = { " + string.Join(", ", mcpEnvironment.Select(pair => pair.Key + " = " + J(pair.Value))) + " }\n";
            await File.WriteAllTextAsync(integration.ConfigPath, config, Utf8);
            await integration.AppendMcpAsync(await integration.ReadConfigAsync(), mcpSnippet);
            var installedConfig = await File.ReadAllTextAsync(integration.ConfigPath, Utf8);
            var mcpReadback = await Child(powershell, WrapperArgs(wrapper, "mcp", "list", "--json"),
                "isolated-mcp-config-readback", root, home, user, project);
            children.Add(mcpReadback.Report);
            Check(mcpReadback.Report.ExitCode == 0 && !mcpReadback.Overflow, "real-cli-mcp-config-readback");
            using (var readback = JsonDocument.Parse(mcpReadback.Output))
            {
                Check(readback.RootElement.ValueKind == JsonValueKind.Array && readback.RootElement.EnumerateArray()
                    .Count(x => S(x, "name") == "amifolio" && B(x, "enabled")) == 1, "real-cli-recognizes-enabled-isolated-amifolio");
                var configured = readback.RootElement.EnumerateArray().Single(x => S(x, "name") == "amifolio");
                var transport = configured.GetProperty("transport");
                var explicitEnvironment = transport.GetProperty("env");
                Check(mcpEnvironment.All(pair => S(explicitEnvironment, pair.Key) == pair.Value), "real-cli-mcp-explicit-isolated-environment-readback");
            }
            var handshake = J(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new
                { protocolVersion = "2025-06-18", capabilities = new { }, clientInfo = new { name = "stage8-stdio-probe", version = "1" } } }) + "\n"
                + J(new { jsonrpc = "2.0", method = "notifications/initialized", @params = new { } }) + "\n"
                + J(new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } }) + "\n";
            var bridgeProbe = await Child(bridge, ["--data-root", data, "mcp"], "isolated-bridge-stdio-probe", root, home, user, project, handshake, true);
            children.Add(bridgeProbe.Report);
            Check(bridgeProbe.Report.ExitCode == 0 && !bridgeProbe.Report.TimedOut && !bridgeProbe.Overflow, "packaged-bridge-open-stdin-handshake-exit-zero");
            var probeTools = 0;
            foreach (var line in bridgeProbe.Output.Split('\n').Where(line => line.Length > 0))
            {
                using var row = JsonDocument.Parse(line);
                if (N(row.RootElement, "id") == 2 && row.RootElement.TryGetProperty("result", out var result)
                    && result.TryGetProperty("tools", out var tools)) probeTools = tools.GetArrayLength();
            }
            Check(probeTools == ExpectedTools.Length, "packaged-bridge-stdio-exposes-eleven-tools");

            async Task RunHost(string purpose, Scenario scenario, string prompt)
            {
                phase = purpose;
                server.Begin(scenario);
                var run = await Child(powershell, WrapperArgs(wrapper, "exec", "--sandbox", "read-only",
                    "--skip-git-repo-check", "--json", "--cd", project, prompt), purpose, root, home, user, project);
                children.Add(run.Report);
                Check(!run.Report.TimedOut, purpose + "-within-120s");
                Check(!run.Overflow, purpose + "-bounded-output");
                server.ThrowIfFaulted();
                Check(run.Report.ExitCode == 0, purpose + "-exit-zero");
                Check(server.Current.Finished, purpose + "-final-sse-consumed");
                Check(run.Report.EventTypes.Contains("turn.completed"), purpose + "-host-turn-completed");
            }

            phase="app-server-structured-skill";server.Begin(Scenario.Skill);
            var skillReport=await ReadSkillThroughAppServer(powershell,wrapper,root,home,user,project,skillPath);children.Add(skillReport);
            server.ThrowIfFaulted();Check(skillReport.ExitCode==0&&server.Current.Finished&&server.Current.SkillSeen,"app-server-loaded-structured-skill-body");
            Check(server.Current.AgentsSeen,"app-server-loaded-agents-marker");
            Check(skillReport.DiagnosticCodes.Contains("one-enabled-untrusted-stop-listed"),"real-host-lists-stop-hook-as-needing-trust");
            await RunHost("installed-host-learning", Scenario.Learn,
                Quote);
            var learningExchange = server.Current;
            Check(learningExchange.AgentsSeen, "host-loaded-agents-marker");
            // Full skill loading was verified through the first-party structured input, separately from the CLI text turn.
            Check(learningExchange.CatalogExact, "actual-eleven-mcp-tools-advertised");
            Check(learningExchange.ProjectResolved, "actual-mcp-project-routing");
            var snapshot = await context.Learning.SnapshotAsync(projectId);
            sourceSupported = learningExchange.Captured && snapshot.Summaries.Count == 1 && snapshot.Summaries[0].State == "verified";
            receiptCount = snapshot.Summaries.Count;
            watermark = snapshot.Watermark;
            active = snapshot.Entries.Count(e => e.State == "active");
            if (sourceSupported)
            {
                var summary = snapshot.Summaries.Single();
                var source = await new CodexTurnReader(home).ReadTurnAsync(summary.SessionId, summary.TurnId, project);
                Check(source is not null && source.UserText.Contains(Quote, StringComparison.Ordinal), "real-cli-user-event-reverified");
                var evidence = await context.Learning.EvidenceAsync(summary.EvidenceIds);
                Check(evidence.Count == 1 && evidence[0].Evidence.Verified && evidence[0].Evidence.Text == Quote
                    && evidence[0].Evidence.SessionId == summary.SessionId && evidence[0].Evidence.TurnId == summary.TurnId,
                    "receipt-cites-exact-real-user-clause");
                Check(learningExchange.Completed && snapshot.Jobs.Count == 1 && snapshot.Jobs[0].State == "completed",
                    "actual-mcp-claim-and-complete");
                var styles = snapshot.Entries.Where(e => e.Kind == "style").ToArray();
                Check(styles.Length == 1 && styles[0] is { State: "active", Dimension: "verbosity", Value: "brief", Text: Style }
                    && styles[0].EvidenceIds.SequenceEqual(summary.EvidenceIds), "canonical-brief-style-active");
                Check(snapshot.Entries.Count == 2 && snapshot.Entries.Single(e => e.Kind == "summary") is
                    { State: "active", Reason: "agent_report_only" }, "agent-summary-distinct-from-user-style-evidence");
                Check(watermark == summary.Sequence && watermark > 0 && snapshot.Pending == 0, "durable-watermark-and-drained-batch");
                Check(learningExchange.SharedStyle, "active-style-returned-through-host-mcp");
                Check(File.Exists(context.Learning.ProjectionPath(projectId)),"scoped-learning-projection-created");
            }
            else
            {
                failures.Add("capture-source-unsupported-or-unverified");
                Check(snapshot.Entries.Count == 0 && snapshot.Watermark == 0 && snapshot.Pending == 0
                    && !learningExchange.Completed, "unsupported-source-fails-closed-no-autoprofile");
            }

            phase = "withdraw-sharing";
            var policy = await context.Learning.PolicyAsync(projectId);
            await context.Learning.SetPolicyAsync(projectId, policy with { Share = false }, policy.Revision);
            await RunHost("fresh-host-share-off", Scenario.ShareOff,
                "$amifolio 请只核对当前项目的风格共享是否已关闭，不要记录新偏好，也不要修改文件。");
            Check(server.Current.ShareOff, "sharing-off-empty-through-fresh-host-mcp");
            var withdrawn = await context.Learning.SnapshotAsync(projectId);
            Check(withdrawn.Watermark == watermark && withdrawn.Entries.Count(e => e.State == "active") == active,
                "sharing-withdrawal-retains-private-canonical-state");
            Check(!File.Exists(context.Learning.ProjectionPath(projectId)), "sharing-withdrawal-removes-projection");

            phase = "remove-mcp-and-restart";
            // Only our isolated generated file is edited; no user's config is opened or patched.
            await File.WriteAllTextAsync(integration.ConfigPath, config, Utf8);
            await RunHost("fresh-host-mcp-removed", Scenario.Removed, "这是合成验收任务，请简洁回复已完成。");
            Check(server.Current.NoMcp, "fresh-host-catalog-has-no-mcp-tools");
            await integration.AppendMcpAsync(await integration.ReadConfigAsync(), mcpSnippet);
            Check(await File.ReadAllTextAsync(integration.ConfigPath, Utf8) == installedConfig,
                "helper-reinstall-restores-only-isolated-config");
            Check(server.Requests <= 16, "provider-total-request-budget-at-most-16");
            Check(!server.AuthorizationSeen, "no-provider-authorization-required-or-sent");
        }
        catch (Exception error)
        {
            diagnosticType = error.GetType().Name;
            diagnosticCode = error is HostFailure host ? host.Code : error is OperationCanceledException ? "bounded-timeout" : "harness-exception";
            failures.Add(diagnosticCode);
        }
        finally
        {
            try
            {
                var statePath = Path.Combine(home, ".codex-global-state.json");
                var registryPresent = false; var registryContainsSeed = false;
                if (File.Exists(statePath) && new FileInfo(statePath).Length <= MaxBody)
                {
                    using var state = JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
                    registryPresent = state.RootElement.TryGetProperty("local-projects", out var registry);
                    registryContainsSeed = registryPresent && registry.ValueKind == JsonValueKind.Object && registry.EnumerateObject()
                        .Any(row => row.Value.TryGetProperty("rootPaths", out var roots) && roots.ValueKind == JsonValueKind.Array
                            && roots.EnumerateArray().Any(path => path.ValueKind == JsonValueKind.String && path.GetString() == project));
                }
                var cwdShapes = new List<object>();
                var recordShapes = new List<object?>();
                var sessions = Path.Combine(home, "sessions");
                if (Directory.Exists(sessions))
                    foreach (var rollout in Directory.EnumerateFiles(sessions, "rollout-*.jsonl", SearchOption.AllDirectories).Take(8))
                    {
                        NoLinks(rollout);
                        await using var input = new FileStream(rollout, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        var header = new byte[128 * 1024]; var length = await input.ReadAsync(header);
                        var end = Array.IndexOf(header, (byte)'\n', 0, length);
                        if (end < 0) continue;
                        using var row = JsonDocument.Parse(header.AsMemory(0, end));
                        var cwd = row.RootElement.TryGetProperty("payload", out var payload) ? S(payload, "cwd") : null;
                        cwdShapes.Add(new { shape = cwd?.StartsWith(@"\\?\", StringComparison.Ordinal) == true ? "extended" : cwd?.StartsWith(@"\\", StringComparison.Ordinal) == true ? "unc" : "ordinary",
                            equalsFixtureRoot = string.Equals(cwd, project, StringComparison.OrdinalIgnoreCase) });
                        if (new FileInfo(rollout).Length <= 1024 * 1024)
                        {
                            foreach (var record in (await File.ReadAllLinesAsync(rollout)).Take(120))
                            {
                                try { using var parsed=JsonDocument.Parse(record); recordShapes.Add(RecordShape(parsed.RootElement)); } catch(JsonException) { }
                            }
                        }
                    }
                fixtureMetadataDiagnostics = new { registryPresent, registryContainsSeed, projectDirectoryExists = Directory.Exists(project), cwdShapes, recordShapes };
            }
            catch (Exception error) { fixtureMetadataDiagnostics = new { diagnosticType = error.GetType().Name }; }
            // Failures still check the fixture. There is no formal source baseline or formal IO.
            try
            {
                if (before is not null)
                {
                    var after = Snapshot(project);
                    noOriginalChanges = before.Count == after.Count && before.All(pair => after.TryGetValue(pair.Key, out var stamp) && stamp == pair.Value);
                    if (noOriginalChanges) checks.Add("fixture-original-bytes-timestamps-attributes-and-file-set-unchanged");
                    else failures.Add("fixture-originals-changed");
                }
            }
            catch (Exception error) { failures.Add("fixture-snapshot-failed"); diagnosticType ??= error.GetType().Name; }
            try
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Amifolio-stage8-host-"));
                var resolved = Path.GetFullPath(root);
                if (!resolved.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || Segments(resolved).Contains(".codex", StringComparer.OrdinalIgnoreCase))
                    throw new HostFailure("cleanup-path-rejected");
                NoLinks(resolved);
                if (Directory.Exists(resolved))
                {
                    // The host may create read-only Git pack/cache files in its own isolated home.
                    // Check every directory before descent; clear attributes only inside this run.
                    var directories = new Stack<string>(); directories.Push(resolved);
                    while (directories.TryPop(out var directory))
                        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                        {
                            NoLinks(entry);
                            if (!Within(entry, resolved)) throw new HostFailure("cleanup-path-rejected");
                            var attributes = File.GetAttributes(entry);
                            if ((attributes & FileAttributes.Directory) != 0) directories.Push(entry);
                            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(entry, attributes & ~FileAttributes.ReadOnly);
                        }
                    Directory.Delete(resolved, true);
                }
                clean = true;
            }
            catch (Exception error) { failures.Add("isolated-cleanup-failed"); diagnosticType ??= error.GetType().Name; }
            if (reportPath is not null)
            {
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(new
                {
                    schemaVersion = 1, status = failures.Count == 0 ? "passed" : "failed", phase,
                    host = "installed-npm-codex-cli-and-app-server", requiredVersion = "0.163.0",
                    model = "deterministic-local-responses-sse-fixture", realModelReasoningVerified = false, paidServiceUsed = false,
                    sourceSupported, receiptCount, watermark, active, noOriginalChanges, isolatedFixtureCleaned = clean,
                    providerRequests = provider?.Requests ?? 0, maxProviderRequests = 16, childTimeoutSeconds = 120,
                    catalogDiagnostics = provider?.CatalogDiagnostics,
                    wireDiagnostics = provider?.WireDiagnostics, resultDiagnostics = provider?.ResultDiagnostics,
                    fixtureMetadataDiagnostics,
                    formalConfigurationRead = (bool?)null, formalConfigurationWritten = false, formalAuthRead = (bool?)null, formalDataRead = (bool?)null,
                    formalReadAudit = "No filesystem access audit is installed; earlier parent-only environment isolation was insufficient evidence. Explicit MCP environment is now required and checked.",
                    rolloutInjection = false, rawProviderRequestsPersisted = false,
                    limitations = new[] { "Deterministic function selection verifies host transport and receipts, not model reasoning or paid-service behavior.",
                        "Only known top-level user schemas with exact identity are accepted; generic item_completed, missing IDs and non-text sources fail closed.",
                        "Reinstall checks the isolated helper/config roundtrip; a second installed-host learning cycle is not claimed." },
                    checks, failures = failures.Distinct().ToArray(), diagnosticType, diagnosticCode, children
                }, Pretty), Utf8);
            }
        }
        Console.WriteLine(failures.Count == 0 ? "PASS stage8 installed Codex host; deterministic local provider; sanitized report.json."
            : "FAIL stage8 installed Codex host; see sanitized report.json codes and isolation audit limitations.");
        return failures.Count == 0 ? 0 : 1;
    }

    // Checked against https://developers.openai.com/codex/config-reference/ : model_provider,
    // model_providers.<id>.name/base_url/requires_openai_auth/wire_api/supports_websockets.
    // No env_key, bearer token, auth.json, copied config, or external model URL is needed.
    private static string ProviderConfig(int port, string project) =>
        "model = \"gpt-5.1-codex\"\nmodel_provider = \"stage8_local\"\napproval_policy = \"never\"\nsandbox_mode = \"read-only\"\n" +
        "web_search = \"disabled\"\ncheck_for_update_on_startup = false\ncli_auth_credentials_store = \"file\"\n" +
        "[model_providers.stage8_local]\nname = \"Stage8 deterministic loopback fixture\"\nbase_url = \"http://127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture) +
        "/v1\"\nrequires_openai_auth = false\nsupports_websockets = false\nwire_api = \"responses\"\n" +
        "request_max_retries = 0\nstream_max_retries = 0\nstream_idle_timeout_ms = 15000\n" +
        "[analytics]\nenabled = false\n[feedback]\nenabled = false\n[projects." + J(project) + "]\ntrust_level = \"trusted\"\n";

    private static string[] WrapperArgs(string wrapper, params string[] args) =>
        new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", wrapper }.Concat(args).ToArray();

    private sealed record ChildReport(string Purpose, int ExitCode, bool TimedOut, long StdoutBytes, long StderrBytes, string[] EventTypes, string[] DiagnosticCodes);
    private sealed record ChildResult(ChildReport Report, string Output, bool Overflow);
    private sealed record Drain(string Text, long Bytes, bool Overflow);
    private static async Task<ChildResult> Child(string executable, string[] args, string purpose, string root, string home, string user, string project, string? input = null, bool holdInputUntilResponse = false)
    {
        var start = ChildStart(executable,args,root,home,user,project);
        using var process = new Process { StartInfo = start };
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        if (!process.Start()) throw new HostFailure("child-start-failed");
        string? firstLine = null;
        var timedOut = false;
        if (input is not null)
        {
            var firstFrameEnd = holdInputUntilResponse ? input.IndexOf('\n') + 1 : input.Length;
            await process.StandardInput.WriteAsync(input.AsMemory(0, firstFrameEnd), deadline.Token);
            await process.StandardInput.FlushAsync(deadline.Token);
            if (holdInputUntilResponse)
            {
                using var firstResponseDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                try { firstLine = await process.StandardOutput.ReadLineAsync(firstResponseDeadline.Token); if (firstLine is null) timedOut = true; }
                catch (OperationCanceledException) { timedOut = true; process.Kill(entireProcessTree: true); }
            }
            if (!timedOut && firstFrameEnd < input.Length)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(firstFrameEnd), deadline.Token);
                await process.StandardInput.FlushAsync(deadline.Token);
            }
        }
        process.StandardInput.Close();
        var output = DrainAsync(process.StandardOutput, true);
        var error = DrainAsync(process.StandardError, true);
        try { await Task.WhenAll(process.WaitForExitAsync(deadline.Token), output, error).WaitAsync(deadline.Token); }
        catch (OperationCanceledException) { timedOut = true; }
        finally
        {
            if (!process.HasExited)
            {
                // Only this exact started process and its descendants; never kill by image/name.
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        var stdout = await output.WaitAsync(TimeSpan.FromSeconds(5));
        if (firstLine is not null) stdout = stdout with { Text = firstLine + "\n" + stdout.Text, Bytes = stdout.Bytes + Utf8.GetByteCount(firstLine) + 1 };
        var stderr = await error.WaitAsync(TimeSpan.FromSeconds(5));
        var types = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in stdout.Text.Split('\n'))
        {
            if (line.Length > 256 * 1024) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var type = S(doc.RootElement, "type");
                if (type is "thread.started" or "turn.started" or "turn.completed" or "turn.failed" or "item.started" or "item.completed" or "error") types.Add(type);
            }
            catch (JsonException) { }
        }
        var diagnosticCodes = new[] { "mcp", "startup", "disabled", "untrusted", "required", "stdio", "configuration", "denied", "failed", "plugin",
            "timeout", "timed out", "initialize", "handshake", "capabilities", "protocol", "spawn", "not found", "eof", "closed", "permission", "parse", "utf", "unexpected", "missing", "dll", "dotnet", "home" }
            .Where(word => stderr.Text.Contains(word, StringComparison.OrdinalIgnoreCase)).Select(word => "stderr-mentions-" + word).ToArray();
        // Startup errors only, with every local path/URL and synthetic user clause removed.
        // Never emit provider requests, host instructions, stdout user events, or arbitrary logs.
        if (purpose == "installed-host-learning" && process.ExitCode != 0 && stdout.Bytes == 0)
        {
            var safeDiagnostic = System.Text.RegularExpressions.Regex.Replace(stderr.Text, @"(?i)[a-z]:[\\/][^\r\n]+|https?://[^\s]+", "[local-target]");
            safeDiagnostic = safeDiagnostic.Replace(Quote, "[synthetic-clause]", StringComparison.Ordinal);
            Console.WriteLine("Host startup diagnostic: " + safeDiagnostic[..Math.Min(safeDiagnostic.Length, 1500)]);
        }
        return new(new(purpose, process.ExitCode, timedOut, stdout.Bytes, stderr.Bytes, types.Order().ToArray(), diagnosticCodes), stdout.Text, stdout.Overflow || stderr.Overflow);
    }
    private static ProcessStartInfo ChildStart(string executable,IReadOnlyList<string> args,string root,string home,string user,string project)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = project, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        // Clear first: no inherited CODEX_* IPC/thread identity, auth, proxy, telemetry,
        // NODE_OPTIONS or agent environment reaches even the discovery/version child.
        var safe = new[] { "PATH", "PATHEXT", "SystemRoot", "WINDIR", "SystemDrive", "ComSpec", "OS", "ProgramFiles",
            "ProgramFiles(x86)", "ProgramW6432", "PROCESSOR_ARCHITECTURE", "NUMBER_OF_PROCESSORS", "DOTNET_ROOT", "DOTNET_ROOT_X64" }
            .Where(start.Environment.ContainsKey).ToDictionary(key => key, key => start.Environment[key]);
        start.Environment.Clear();
        foreach (var (key, value) in safe) start.Environment[key] = value;
        start.Environment["CODEX_HOME"] = home;
        start.Environment["USERPROFILE"] = user;
        start.Environment["HOME"] = user;
        start.Environment["APPDATA"] = Path.Combine(user, "AppData", "Roaming");
        start.Environment["LOCALAPPDATA"] = Path.Combine(user, "AppData", "Local");
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(user, "config");
        start.Environment["XDG_DATA_HOME"] = Path.Combine(user, "data");
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(user, "cache");
        // Preserve the OS temporary boundary: CodexCatalog exempts that exact ancestor.
        // Changing it would make this synthetic project under the real Temp look like a forbidden cache.
        start.Environment["TEMP"] = Path.GetTempPath();
        start.Environment["TMP"] = Path.GetTempPath();
        start.Environment["NO_PROXY"] = "127.0.0.1,localhost";
        start.Environment["POWERSHELL_TELEMETRY_OPTOUT"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return start;
    }
    // Use the installed app-server's structured Skill input. No shell approval is requested or bypassed.
    private static async Task<ChildReport> ReadSkillThroughAppServer(string executable,string wrapper,string root,string home,string user,string project,string skill)
    {
        var launcher=Path.Combine(Path.GetDirectoryName(wrapper)!,"node_modules","@openai","codex","bin","codex.js");
        var node=Path.Combine(Path.GetDirectoryName(wrapper)!,"node.exe");
        if(!File.Exists(node))node=(Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator).Select(p=>Path.Combine(p,"node.exe")).FirstOrDefault(File.Exists)??throw new HostFailure("app-skill-node-missing");
        if(!File.Exists(launcher))throw new HostFailure("app-skill-installed-launcher-missing");
        // The npm PowerShell shim does not forward a persistent stdin stream. Use its actual
        // installed Node launcher, preserving the identical Codex executable and all restrictions.
        using var process=new Process{StartInfo=ChildStart(node,[launcher,"app-server","--stdio"],root,home,user,project)};
        if(!process.Start())throw new HostFailure("app-skill-start-failed");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var errors=DrainAsync(process.StandardError,false);long bytes=0;var events=new HashSet<string>(StringComparer.Ordinal);var sequence=0;var turnCompleted=false;var stage="start";
        async Task Send(object value){await process.StandardInput.WriteLineAsync(J(value).AsMemory(),deadline.Token);await process.StandardInput.FlushAsync(deadline.Token);}
        async Task<JsonElement> Next()
        {
            var line=await process.StandardOutput.ReadLineAsync(deadline.Token)??throw new HostFailure("app-skill-eof");
            bytes+=Utf8.GetByteCount(line);if(bytes>MaxBody||line.Length>256*1024)throw new HostFailure("app-skill-output-limit");
            using var doc=JsonDocument.Parse(line);var row=doc.RootElement.Clone();var method=S(row,"method");if(method is not null)events.Add(method);
            if(row.TryGetProperty("id",out _)&&method is not null)throw new HostFailure("app-skill-unexpected-approval-request");
            if(method=="turn/completed")
            {
                var status=S(row.GetProperty("params").GetProperty("turn"),"status");
                if(status!="completed")throw new HostFailure("app-skill-turn-"+status);turnCompleted=true;
            }
            return row;
        }
        async Task<JsonElement> Call(string method,object parameters)
        {
            stage=method.Replace('/','-');var id=++sequence;await Send(new{id,method,@params=parameters});
            while(true){var row=await Next();if(row.TryGetProperty("id",out var responseId)&&responseId.ValueKind==JsonValueKind.Number&&responseId.GetInt32()==id)
            {if(row.TryGetProperty("error",out var error))throw new HostFailure("app-skill-rpc-"+method.Replace('/','-')+"-"+N(error,"code"));return row.GetProperty("result");}}
        }
        try
        {
            await Call("initialize",new{clientInfo=new{name="amifolio_stage8_probe",title="Amifolio synthetic acceptance",version="1"},capabilities=new{experimentalApi=true}});
            await Send(new{method="initialized",@params=new{}});
            var listed=await Call("hooks/list",new{cwds=new[]{project}});
            var configured=listed.GetProperty("data").EnumerateArray().SelectMany(e=>e.GetProperty("hooks").EnumerateArray()).Where(h=>S(h,"eventName")=="stop"&&S(h,"sourcePath")==Path.Combine(home,"hooks.json")).ToArray();
            if(configured.Length!=1||!B(configured[0],"enabled")||S(configured[0],"trustStatus")!="untrusted"||S(configured[0],"handlerType")!="command")throw new HostFailure("host-stop-hook-trust-boundary-mismatch");
            var thread=await Call("thread/start",new{cwd=project,model="gpt-5.1-codex"});var id=Required(thread.GetProperty("thread"),"id");
            await Call("turn/start",new{threadId=id,input=new object[]{new{type="text",text="请加载配套skill，完成这次合成接口验收。",text_elements=Array.Empty<object>()},new{type="skill",name="amifolio",path=skill}}});
            stage="turn-events";while(!turnCompleted)await Next();
            process.StandardInput.Close();await process.WaitForExitAsync(deadline.Token);
            var stderr=await errors.WaitAsync(TimeSpan.FromSeconds(5));
            if(events.Any(e=>e.Contains("hook",StringComparison.OrdinalIgnoreCase)&&e.Contains("started",StringComparison.OrdinalIgnoreCase)))throw new HostFailure("untrusted-hook-was-started");
            return new("app-server-structured-skill",process.ExitCode,false,bytes,stderr.Bytes,events.Order().ToArray(),["one-enabled-untrusted-stop-listed","no-hook-started-before-trust"]);
        }
        catch(OperationCanceledException){throw new HostFailure("app-skill-timeout-"+stage+"-"+string.Join("_",events.Take(6)));}
        finally
        {
            if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));}
        }
    }
    private static async Task<Drain> DrainAsync(StreamReader reader, bool keep)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        long bytes = 0;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            bytes += Encoding.UTF8.GetByteCount(buffer.AsSpan(0, count));
            if (keep && bytes <= MaxBody) text.Append(buffer, 0, count);
        }
        return new(text.ToString(), bytes, bytes > MaxBody);
    }

    private enum Scenario { Skill, Learn, ShareOff, Removed }
    private static object? RecordShape(JsonElement node,string key="",int depth=0)
    {
        if(depth>7)return "depth-limit";
        if(node.ValueKind==JsonValueKind.Object)return node.EnumerateObject().Take(24).ToDictionary(p=>p.Name,p=>RecordShape(p.Value,p.Name,depth+1));
        if(node.ValueKind==JsonValueKind.Array)return node.EnumerateArray().Take(6).Select(n=>RecordShape(n,key,depth+1)).ToArray();
        if(node.ValueKind==JsonValueKind.String)
        {
            var value=node.GetString()!;
            return key is "type" or "role" or "channel" or "phase"?value[..Math.Min(value.Length,100)]:new{length=value.Length,hasSyntheticQuote=value.Contains(Quote,StringComparison.Ordinal)};
        }
        return node.ValueKind.ToString();
    }

    private sealed class Exchange(Scenario scenario)
    {
        internal Scenario Scenario = scenario;
        internal string? PendingId, PendingTool, PendingNamespace;
        internal bool AgentsSeen, SkillSeen, CatalogExact, ProjectResolved, Captured, Completed, SharedStyle, ShareOff, NoMcp, Finished;
        internal int StatusAttempts = 0;
    }

    private sealed class LocalProvider : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly Task serve;
        private readonly string project, projectId, agentsMarker, skillMarker;
        private Exception? fault;
        internal Exchange Current { get; private set; } = new(Scenario.Learn);
        internal int Requests { get; private set; }
        internal bool AuthorizationSeen { get; private set; }
        internal List<object> CatalogDiagnostics { get; } = [];
        internal List<object> WireDiagnostics { get; } = [];
        internal List<object> ResultDiagnostics { get; } = [];
        internal int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
        internal LocalProvider(string project, string projectId, string agentsMarker, string skillMarker)
        {
            this.project = project; this.projectId = projectId; this.agentsMarker = agentsMarker; this.skillMarker = skillMarker;
            listener.Start(2);
            serve = Serve();
        }
        internal void Begin(Scenario scenario) { ThrowIfFaulted(); Current = new(scenario); }
        internal void ThrowIfFaulted() { if (fault is not null) throw new HostFailure(fault is HostFailure h ? h.Code : "local-provider-" + fault.GetType().Name); }
        private async Task Serve()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await listener.AcceptTcpClientAsync(stop.Token);
                    using var requestDeadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                    requestDeadline.CancelAfter(TimeSpan.FromSeconds(20));
                    var ct = requestDeadline.Token;
                    await using var stream = client.GetStream();
                    if (++Requests > 16) throw new HostFailure("provider-request-budget-exceeded");
                    var request = await ReadHttp(stream, ct);
                    WireDiagnostics.Add(new { bytes = request.Body.Length, encoding = request.Encoding,
                        jsonPrefix = request.Body.Length > 0 && request.Body[0] is (byte)'{' or (byte)'[' });
                    AuthorizationSeen |= request.Authorization;
                    using var doc = JsonDocument.Parse(request.Body, new JsonDocumentOptions { MaxDepth = 64 });
                    var bodyText = Utf8.GetString(request.Body);
                    Current.AgentsSeen |= bodyText.Contains(agentsMarker, StringComparison.Ordinal);
                    Current.SkillSeen |= bodyText.Contains(skillMarker, StringComparison.Ordinal);
                    var response = Select(doc.RootElement);
                    if (Current.PendingTool == "learning_status")
                        await Task.Delay(TimeSpan.FromSeconds(1.25), ct); // Bounded real-rollout flush retry, never manufacture evidence.
                    // The request/system prompt exists only in memory here. Never log it.
                    await SendSse(stream, response.Tool, response.Arguments, response.Text, ct);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception error) { fault = error; listener.Stop(); }
        }

        private (string? Tool, object? Arguments, string Text) Select(JsonElement request)
        {
            if(Current.Scenario==Scenario.Skill)return Final("Synthetic structured skill loading completed.");
            if (!request.TryGetProperty("tools", out var catalog) || catalog.ValueKind != JsonValueKind.Array)
                throw new HostFailure("responses-tool-catalog-missing");
            if (CatalogDiagnostics.Count < 3 && (Current.PendingTool is null || Current.Scenario == Scenario.Removed))
                CatalogDiagnostics.Add(new { scenario = Current.Scenario.ToString(), entries = catalog.EnumerateArray().Take(64).Select(t => new
                {
                    type = SafeCatalogName(S(t, "type")), name = SafeCatalogName(S(t, "name")),
                    fields = t.EnumerateObject().Select(p => p.Name).Where(n => n.Length <= 64).ToArray(),
                    children = t.TryGetProperty("tools", out var nested) && nested.ValueKind == JsonValueKind.Array
                        ? nested.EnumerateArray().Take(32).Select(n => new { type = SafeCatalogName(S(n, "type")), name = SafeCatalogName(S(n, "name")) }).ToArray() : null
                }).ToArray() });
            var available = new List<(string Canonical, string WireName, string? Namespace)>();
            foreach (var entry in catalog.EnumerateArray())
            {
                var ns = S(entry, "name");
                if (S(entry, "type") == "namespace" && ns is not null && entry.TryGetProperty("tools", out var nested) && nested.ValueKind == JsonValueKind.Array)
                {
                    foreach (var function in nested.EnumerateArray())
                        if (S(function, "type") == "function" && S(function, "name") is { } shortName)
                            available.Add((ns + "__" + shortName, shortName, ns));
                }
                else if (S(entry, "name") is { } flatName) available.Add((flatName, flatName, null));
            }
            var names = available.Select(tool => tool.Canonical).ToArray();
            var mcp = names.Where(n => n.StartsWith("mcp__", StringComparison.Ordinal)).ToArray();
            if (Current.Scenario == Scenario.Removed)
            {
                if (mcp.Length != 0) throw new HostFailure("removed-host-still-advertises-mcp");
                Current.NoMcp = true;
                return Final("Synthetic MCP removal verified.");
            }
            string Name(string shortName)
            {
                var found = available.Where(tool => tool.Canonical == "mcp__amifolio__" + shortName).ToArray();
                if (found.Length != 1) throw new HostFailure("required-host-mcp-tool-missing");
                Current.PendingNamespace = found[0].Namespace;
                return found[0].WireName;
            }
            Current.CatalogExact = mcp.Length == ExpectedTools.Length && ExpectedTools.All(tool => names.Contains("mcp__amifolio__" + tool, StringComparer.Ordinal));
            if (!Current.CatalogExact) throw new HostFailure("host-mcp-catalog-not-exactly-eleven");
            (string?, object?, string) Call(string tool, object args)
            {
                Current.PendingTool = tool;
                return (Name(tool), args, "");
            }
            if (Current.PendingTool is null) return Current.Scenario == Scenario.Learn ? Call("projects_list", new { }) : Call("project_resolve", new { cwd = project });
            var result = ResultFor(request, Current.PendingId!);
            switch (Current.PendingTool)
            {
                case "projects_list":
                    var projects = result.GetProperty("projects");
                    var expectedFound = projects.EnumerateArray().Count(row => S(row, "id") == projectId);
                    ResultDiagnostics.Add(new { tool = "projects_list", projectCount = projects.GetArrayLength(), expectedProjectIdMatches = expectedFound });
                    if (expectedFound != 1) throw new HostFailure("actual-mcp-project-catalog-does-not-contain-seeded-id");
                    return Call("project_resolve", new { cwd = project });
                case "project_resolve":
                    if (S(result, "projectId") != projectId) throw new HostFailure("mcp-resolved-wrong-project");
                    Current.ProjectResolved = true;
                    return Current.Scenario == Scenario.Learn
                        ? Call("learning_status", new { projectId })
                        : Call("learning_context", new { projectId, layer = "style" });
                case "learning_status":
                    if (!B(result, "captureAllowed") || !B(result, "learningAllowed") || S(result, "processor") != "chat"
                        || N(result, "batchSize") != 1) throw new HostFailure("isolated-learning-consent-mismatch");
                    var turns = result.GetProperty("recentTurns");
                    if (turns.GetArrayLength() == 0)
                    {
                        // One bounded retry allows a real writer flush. No injected rollout/IDs.
                        return Call("learning_claim", new { projectId });
                    }
                    if (turns.GetArrayLength() != 1) throw new HostFailure("ambiguous-real-host-turn");
                    var turn = turns[0];
                    return Call("learning_capture", new { projectId, sessionId = Required(turn, "sessionId"), turnId = Required(turn, "turnId"),
                        summary = "合成验收记录：请求长期简洁表达。", outcome = "completed", quotes = new[] { Quote }, outputs = Array.Empty<object>() });
                case "learning_capture":
                    Current.Captured = S(result, "state") == "verified";
                    if (!Current.Captured && S(result, "state") != "pending_evidence") throw new HostFailure("unexpected-capture-receipt");
                    return Call("learning_claim", new { projectId });
                case "learning_claim":
                    var job = result.GetProperty("job");
                    if (job.ValueKind == JsonValueKind.Null)
                    {
                        if (Current.Captured) throw new HostFailure("verified-batch-not-claimed");
                        return Final("Synthetic source unsupported; no capture/activation success claimed.");
                    }
                    if (!Current.Captured) throw new HostFailure("unsupported-source-produced-job");
                    // Bridge wrapper uses camelCase; the public job/evidence records use PascalCase.
                    var evidence = job.GetProperty("Evidence").EnumerateArray().Select(e => e.GetProperty("Evidence")).ToArray();
                    if (evidence.Length != 1 || !B(evidence[0], "Verified") || S(evidence[0], "Text") != Quote)
                        throw new HostFailure("claim-missing-verified-user-evidence");
                    return Call("learning_complete", new { jobId = Required(job, "Id"), token = Required(job, "Token"),
                        proposals = new[] { new { kind = "style", dimension = "verbosity", value = "brief", text = "偏好简洁表达", scope = "project", condition = "",
                            evidenceIds = new[] { Required(evidence[0], "Id") } } } });
                case "learning_complete":
                    ResultDiagnostics.Add(new{tool="learning_complete",state=S(result,"state"),active=N(result,"active"),candidates=N(result,"candidates"),watermark=N(result,"watermark")});
                    if (S(result, "state") != "completed" || N(result, "active") != 1 || N(result, "candidates") != 0 || N(result, "watermark") <= 0)
                        throw new HostFailure("learning-complete-receipt-mismatch");
                    Current.Completed = true;
                    return Call("learning_context", new { projectId, layer = "style" });
                case "learning_context":
                    var documents = result.GetProperty("documents");
                    if (Current.Scenario == Scenario.ShareOff)
                    {
                        if (B(result, "enabled") || documents.EnumerateObject().Any()) throw new HostFailure("share-off-leaked-context");
                        Current.ShareOff = true;
                    }
                    else
                    {
                        if (!B(result, "enabled") || S(documents, "style.md")?.Contains(Style, StringComparison.Ordinal) != true)
                            throw new HostFailure("active-style-not-shared-through-mcp");
                        Current.SharedStyle = true;
                    }
                    return Final("Synthetic host transport and receipt checks completed.");
                default: throw new HostFailure("unexpected-workflow-stage");
            }
        }
        private static string? SafeCatalogName(string? name) => name is null ? null : name.Length <= 128
            && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' or ':') ? name : "nonstandard-name";
        private (string?, object?, string) Final(string text) { Current.Finished = true; return (null, null, text); }

        private async Task SendSse(NetworkStream stream, string? tool, object? arguments, string text, CancellationToken ct)
        {
            var id = "resp_" + Guid.NewGuid().ToString("N");
            var itemId = (tool is null ? "msg_" : "fc_") + Guid.NewGuid().ToString("N");
            var callId = "call_" + Guid.NewGuid().ToString("N");
            var argText = JsonSerializer.Serialize(arguments);
            object item = tool is not null
                ? new { type = "function_call", id = itemId, call_id = callId, name = tool, @namespace = Current.PendingNamespace, arguments = argText, status = "completed" }
                : new { type = "message", id = itemId, role = "assistant", status = "completed", content = new[] { new { type = "output_text", text, annotations = Array.Empty<object>() } } };
            if (tool is not null) Current.PendingId = callId;
            object Response(string status, object[] output) => new { id, @object = "response", created_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                status, model = "gpt-5.1-codex", output, parallel_tool_calls = false,
                usage = new { input_tokens = 0, output_tokens = 0, total_tokens = 0 } };
            var events = new List<object>
            {
                new { type = "response.created", response = Response("in_progress", []) },
                new { type = "response.output_item.added", output_index = 0, item = tool is not null
                    ? (object)new { type = "function_call", id = itemId, call_id = callId, name = tool, @namespace = Current.PendingNamespace, arguments = "", status = "in_progress" }
                    : new { type = "message", id = itemId, role = "assistant", status = "in_progress", content = Array.Empty<object>() } }
            };
            if (tool is not null)
            {
                events.Add(new { type = "response.function_call_arguments.delta", item_id = itemId, output_index = 0, delta = argText });
                events.Add(new { type = "response.function_call_arguments.done", item_id = itemId, output_index = 0, arguments = argText });
            }
            else
            {
                events.Add(new { type = "response.content_part.added", item_id = itemId, output_index = 0, content_index = 0,
                    part = new { type = "output_text", text = "", annotations = Array.Empty<object>() } });
                events.Add(new { type = "response.output_text.delta", item_id = itemId, output_index = 0, content_index = 0, delta = text });
                events.Add(new { type = "response.output_text.done", item_id = itemId, output_index = 0, content_index = 0, text });
                events.Add(new { type = "response.content_part.done", item_id = itemId, output_index = 0, content_index = 0,
                    part = new { type = "output_text", text, annotations = Array.Empty<object>() } });
            }
            events.Add(new { type = "response.output_item.done", output_index = 0, item });
            events.Add(new { type = "response.completed", response = Response("completed", [item]) });
            var body = Utf8.GetBytes(string.Concat(events.Select(e => "data: " + J(e) + "\n\n")));
            var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\nContent-Length: " + body.Length.ToString(CultureInfo.InvariantCulture) + "\r\n\r\n");
            await stream.WriteAsync(header, ct);
            await stream.WriteAsync(body, ct);
            await stream.FlushAsync(ct);
        }

        private JsonElement ResultFor(JsonElement request, string callId)
        {
            if (request.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array)
                foreach (var item in input.EnumerateArray())
                    if (S(item, "type") == "function_call_output" && S(item, "call_id") == callId)
                        return Unwrap(item.GetProperty("output"), 0);
            throw new HostFailure("actual-host-function-result-missing");
        }
        private JsonElement Unwrap(JsonElement value, int depth)
        {
            if (depth > 8) throw new HostFailure("mcp-wrapper-depth-exceeded");
            if (ResultDiagnostics.Count < 32) ResultDiagnostics.Add(new { depth, kind = value.ValueKind.ToString(),
                fields = value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().Select(p => p.Name).ToArray() : null,
                stringLength = value.ValueKind == JsonValueKind.String ? value.GetString()!.Length : 0,
                jsonPrefix = value.ValueKind == JsonValueKind.String && value.GetString()!.TrimStart().StartsWith('{'),
                wrapperMarkers = value.ValueKind == JsonValueKind.String ? new[] { "Wall time:", "Output:", "Final output:", "Final response:", "Tool result:", "Error", "error", "CallToolResult", "content", "structuredContent", "unknown tool", "not found", "Unsupported", "unsupported", "-32602", "-32603", "invalid_arguments", "project_not_found" }
                    .Where(marker => value.GetString()!.Contains(marker, StringComparison.Ordinal)).ToArray() : null });
            if (value.ValueKind == JsonValueKind.String)
            {
                var outputText = value.GetString()!;
                // Codex 0.163 wraps MCP results in its bounded tool timing envelope.
                // Strip only the recognized envelope; preserve and reject the payload's errors.
                if (outputText.StartsWith("Wall time:", StringComparison.Ordinal))
                {
                    var normalized = outputText.Replace("\r\n", "\n", StringComparison.Ordinal);
                    var outputAt = normalized.IndexOf("\nOutput:\n", StringComparison.Ordinal);
                    if (outputAt < 0 || outputAt > 256) throw new HostFailure("unrecognized-host-tool-envelope");
                    outputText = normalized[(outputAt + "\nOutput:\n".Length)..];
                }
                using var doc = JsonDocument.Parse(outputText);
                return Unwrap(doc.RootElement, depth + 1);
            }
            if (value.ValueKind == JsonValueKind.Array)
            {
                if (value.GetArrayLength() != 1) throw new HostFailure("ambiguous-mcp-content");
                return Unwrap(value[0], depth + 1);
            }
            if (value.ValueKind != JsonValueKind.Object) throw new HostFailure("invalid-mcp-content");
            if (value.TryGetProperty("error", out var toolError))
            {
                var code = S(toolError, "code");
                var detail = S(toolError, "message") switch
                {
                    "Workspace directory is unavailable." => "workspace-unavailable",
                    "The workspace is not a currently discovered Codex project." => "not-discovered",
                    _ => "other"
                };
                throw new HostFailure("actual-mcp-tool-" + (code is "project_not_found" or "invalid_arguments" ? code : "error") + "-" + detail);
            }
            if (B(value, "isError")) throw new HostFailure("actual-mcp-tool-error");
            if (value.TryGetProperty("structuredContent", out var structured)) return Unwrap(structured, depth + 1);
            if (value.TryGetProperty("result", out var wrapped)) return Unwrap(wrapped, depth + 1);
            if (value.TryGetProperty("content", out var content)) return Unwrap(content, depth + 1);
            if (S(value, "type") == "text" && value.TryGetProperty("text", out var text)) return Unwrap(text, depth + 1);
            return value.Clone();
        }

        private sealed record HttpInput(byte[] Body, bool Authorization, string Encoding);
        private static async Task<HttpInput> ReadHttp(NetworkStream stream, CancellationToken ct)
        {
            // Bounded HTTP/1.1 parser. No StreamReader read-ahead can consume body bytes.
            var bytes = new List<byte>();
            var one = new byte[1];
            while (bytes.Count < 32 * 1024)
            {
                if (await stream.ReadAsync(one, ct) != 1) throw new HostFailure("http-header-truncated");
                bytes.Add(one[0]);
                var n = bytes.Count;
                if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) break;
            }
            if (bytes.Count >= 32 * 1024) throw new HostFailure("http-header-too-large");
            var lines = Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n", StringSplitOptions.None);
            if (lines[0] != "POST /v1/responses HTTP/1.1") throw new HostFailure("unexpected-local-provider-route");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1).Where(line => line.Length > 0))
            {
                var split = line.IndexOf(':');
                if (split < 1 || !headers.TryAdd(line[..split].Trim(), line[(split + 1)..].Trim())) throw new HostFailure("ambiguous-http-header");
            }
            if (headers.TryGetValue("Expect", out _)) throw new HostFailure("http-expect-unsupported");
            byte[] body;
            if (headers.TryGetValue("Transfer-Encoding", out var transfer))
            {
                if (!transfer.Equals("chunked", StringComparison.OrdinalIgnoreCase) || headers.ContainsKey("Content-Length"))
                    throw new HostFailure("unsupported-http-transfer");
                using var buffer = new MemoryStream();
                while (true)
                {
                    var size = await Line(stream, ct);
                    if (!int.TryParse(size, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var count) || count < 0 || count > MaxBody - buffer.Length)
                        throw new HostFailure("http-body-too-large-or-invalid-chunk");
                    if (count == 0)
                    {
                        if (await Line(stream, ct) != "") throw new HostFailure("http-trailers-unsupported");
                        break;
                    }
                    var chunk = new byte[count]; await stream.ReadExactlyAsync(chunk, ct); buffer.Write(chunk);
                    if (await Line(stream, ct) != "") throw new HostFailure("invalid-http-chunk-end");
                }
                body = buffer.ToArray();
            }
            else
            {
                if (!headers.TryGetValue("Content-Length", out var value) || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var length)
                    || length <= 0 || length > MaxBody) throw new HostFailure("http-body-length-invalid");
                body = new byte[length]; await stream.ReadExactlyAsync(body, ct);
            }
            var encoding = headers.GetValueOrDefault("Content-Encoding", "identity");
            return new(body, headers.ContainsKey("Authorization"), encoding is "gzip" or "zstd" or "deflate" or "identity" ? encoding : "other");
        }
        private static async Task<string> Line(NetworkStream stream, CancellationToken ct)
        {
            var bytes = new List<byte>(); var one = new byte[1];
            while (bytes.Count < 128)
            {
                if (await stream.ReadAsync(one, ct) != 1) throw new HostFailure("http-chunk-truncated");
                if (one[0] == 10)
                {
                    if (bytes.Count == 0 || bytes[^1] != 13) throw new HostFailure("http-chunk-line-invalid");
                    return Encoding.ASCII.GetString(bytes.Take(bytes.Count - 1).ToArray());
                }
                bytes.Add(one[0]);
            }
            throw new HostFailure("http-chunk-line-too-long");
        }
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop();
            await serve.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Dispose();
        }
    }

    private sealed class HostFailure(string code) : Exception(code) { internal string Code { get; } = code; }
    private sealed record FileStamp(long Length, string Sha256, long LastWriteTicks, FileAttributes Attributes);
    private static Dictionary<string, FileStamp> Snapshot(string project)
    {
        var result = new Dictionary<string, FileStamp>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(project, "*", SearchOption.AllDirectories))
        {
            NoLinks(path);
            var info = new FileInfo(path);
            if (info.Length > MaxBody || result.Count >= 32) throw new HostFailure("fixture-snapshot-budget-exceeded");
            using var stream = File.OpenRead(path);
            result.Add(Path.GetRelativePath(project, path), new(info.Length, Convert.ToHexString(SHA256.HashData(stream)), info.LastWriteTimeUtc.Ticks, info.Attributes));
        }
        return result;
    }
    private static bool Within(string path, string parent) => path.Equals(parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string[] Segments(string path) => Path.GetFullPath(path).Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
    private static void NoLinks(string path)
    {
        for (var part = Path.GetFullPath(path); part is not null; part = Path.GetDirectoryName(part))
            if ((Directory.Exists(part) || File.Exists(part)) && (File.GetAttributes(part) & FileAttributes.ReparsePoint) != 0)
                throw new HostFailure("linked-fixture-or-package-path-rejected");
    }
    private static string J(object value) => JsonSerializer.Serialize(value,
        new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    private static string? S(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static string Required(JsonElement value, string name) => S(value, name) is { Length: > 0 and <= 128 } result
        ? result : throw new HostFailure("missing-bounded-mcp-identity");
    private static bool B(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.True;
    private static long N(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object
        && value.TryGetProperty(name, out var item) && item.TryGetInt64(out var number) ? number : -1;
}
