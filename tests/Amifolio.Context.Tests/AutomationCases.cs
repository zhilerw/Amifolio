using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amifolio.Context;
using Amifolio.Core;

internal static class AutomationCases
{
    internal static (string Name, Func<Task> Run)[] All =>
    [
        ("stage6 automation exposes independent policy and adoption contracts", () => {
            foreach (var name in new[] { "ReadAutomationAsync", "SetAutomationAsync", "RunAutomaticAsync", "AdoptCandidateAsync", "SetDocumentEnabledAsync" })
                Check(typeof(ContextService).GetMethod(name) is not null, "missing contract: " + name);
            return Task.CompletedTask;
        }),
        ("stage6 automation only parsed nonempty evidence enters previews", async () => {
            using var f = new Fixture();
            f.Store.Add("valid", null, "用户", "Valid", "PARSED_EVIDENCE");
            f.Store.Add("empty", null, "用户", "EMPTY_METADATA_ONLY", "");
            f.Store.Add("unparsed", null, "用户", "UNPARSED_METADATA", "UNPARSED_BODY");
            f.Store.Items[2] = f.Store.Items[2] with { ExtractionStatus = "未解析" };
            await f.Service.SetEnabledAsync(null, true);
            var preview = await f.Service.PreviewGenerationAsync();
            Check(preview.Contains("PARSED_EVIDENCE") && !preview.Contains("EMPTY_METADATA_ONLY") && !preview.Contains("UNPARSED"), "unparsed metadata entered request");
        }),
        ("stage6 automation manual consent generates privately with sharing off", async () => {
            using var f = new Fixture(); f.Store.Add("valid", null, "用户", "Valid", "PARSED_EVIDENCE");
            await f.Service.GenerateAsync(null, new("https://example.invalid/v1", "synthetic", true), "dummy");
            var snapshot = await f.Service.ReadAsync();
            Check(!snapshot.Enabled && snapshot.CandidateDocuments.Count > 0 && !File.Exists(Path.Combine(snapshot.Directory, "START.md")), "generation requires sharing or leaked candidates");
        }),
        ("stage6 automation defaults and unknown project status are safe", async () => {
            using var f = new AutomaticFixture();
            var status = await f.Service.ReadAutomationAsync("unknown");
            Check(status.Options == new ContextAutomationOptions() && status.RequestsToday == 0 && status.DailyLimit == 8 && status.Status == "disabled", "unsafe defaults");
            Check(!await f.Service.RunAutomaticAsync(null, f.Options, "dummy") && f.Handler.Calls == 0, "default sent a request");
        }),
        ("stage6 automation requires scope saved global and task remote permissions", async () => {
            using var f = new AutomaticFixture(); await f.Ready();
            await f.Service.SetAutomationAsync(null, new(SummaryEnabled: true, RemoteAllowed: true));
            Check(!await f.Service.RunAutomaticAsync(null, f.Options, "dummy"), "unsaved AI permission accepted");
            await f.SaveConfiguration(true);
            Check(!await f.Service.RunAutomaticAsync(null, f.Options, "dummy"), "missing task permission accepted");
            await new TaskStore(f.Root).SetRemoteAllowedAsync(true);
            Check(!await f.Service.RunAutomaticAsync(null, f.Options with { Model = "other" }, "dummy"), "unsaved endpoint/model accepted");
            Check(!await f.Service.RunAutomaticAsync(null, f.Options with { RemoteAllowed = false }, "dummy"), "call permission ignored");
            await f.Service.SetAutomationAsync(null, new(SummaryEnabled: true));
            Check(!await f.Service.RunAutomaticAsync(null, f.Options, "dummy"), "scope permission ignored");
            Check(f.Handler.Calls == 0, "unauthorized network request");
        }),
        ("stage6 automation learning only works privately and does not require summary", async () => {
            using var f = new AutomaticFixture(); await f.Ready(); await f.Authorize(new(LearningEnabled: true, RemoteAllowed: true));
            f.Handler.Content = "{\"memory.md\":\"Explicit constraint [evidence]\",\"style.md\":\"Explicit preference [evidence]\",\"summary.md\":\"UNWANTED\"}";
            Check(await f.Service.RunAutomaticAsync(null, f.Options, "SYNTHETIC_AUTOMATION_KEY"), "learning-only failed");
            var snapshot = await f.Service.ReadAsync();
            Check(!snapshot.Enabled && snapshot.CandidateDocuments.Count == 2 && !snapshot.CandidateDocuments.ContainsKey("candidate-summary.md"), "policy output filter failed");
            Check(!File.Exists(Path.Combine(snapshot.Directory, "START.md")), "private candidate was published");
            Check(f.Handler.Payload.Contains("Never infer user style from agent prose") && f.Handler.Payload.Contains("do not produce summary.md"), "missing explicit preference guard");
            Check(!(await f.StateText()).Contains("SYNTHETIC_AUTOMATION_KEY"), "key persisted");
        }),
        ("stage6 automation summary only filters learning output and remembers success", async () => {
            using var f = new AutomaticFixture(); await f.Ready(); await f.Authorize();
            Check(await f.Service.RunAutomaticAsync(null, f.Options, "dummy"), "summary generation failed");
            var snapshot = await f.Service.ReadAsync();
            Check(snapshot.CandidateDocuments.Count == 2 && !snapshot.CandidateDocuments.ContainsKey("candidate-memory.md"), "learning output leaked");
            Check(!await new ContextService(f.Store, f.Client).RunAutomaticAsync(null, f.Options, "different-key") && f.Handler.Calls == 1, "same input repeated after restart or key rotation");
            Check((await f.Service.ReadAutomationAsync()).Status == "generated", "success status lost");
        }),
        ("stage6 automation failure and interrupted claims are never silently retried", async () => {
            using var f = new AutomaticFixture(); await f.Ready(); await f.Authorize(); f.Handler.Fail = true;
            Check(!await f.Service.RunAutomaticAsync(null, f.Options, "dummy"), "failed request reported success");
            var other = new ContextService(f.Store, f.Client);
            Check(!await other.RunAutomaticAsync(null, f.Options, "dummy") && f.Handler.Calls == 1, "failed input retried");
            var status = await other.ReadAutomationAsync();
            Check(status.Status == "provider_http" && status.RequestsToday == 1 && status.LastAttempt is not null, "failure status or durable budget missing");
            f.Handler.Fail = false;
            await other.GenerateAsync(null, f.Options, "dummy");
            Check(f.Handler.Calls == 2, "explicit manual retry was suppressed");
        }),
        ("stage6 automation appwide cooldown and daily eight cap survive scopes and restart", async () => {
            using var f = new AutomaticFixture(); await f.Ready(); await f.Authorize();
            Check(await f.Service.RunAutomaticAsync(null, f.Options, "dummy"), "initial request failed");
            var project = await f.Store.CreateProjectAsync("Synthetic project");
            await f.AddSource(project.Id); await f.Service.SetAutomationAsync(project.Id, new(SummaryEnabled: true, RemoteAllowed: true));
            Check(!await f.Service.RunAutomaticAsync(project.Id, f.Options, "dummy") && (await f.Service.ReadAutomationAsync(project.Id)).Status == "cooldown", "cooldown was per scope");
            for (var i = 1; i < 8; i++)
            {
                await f.ElapseCooldown();
                var source = (await f.Store.ListAsync()).First(a => a.ProjectId is null);
                await f.Store.UpdateArtifactAsync(source.Id, "Synthetic " + i, "", null);
                Check(await f.Service.RunAutomaticAsync(null, f.Options, "dummy"), "budget request failed: " + i);
            }
            await f.ElapseCooldown();
            Check(!await new ContextService(f.Store, f.Client).RunAutomaticAsync(project.Id, f.Options, "dummy"), "daily cap reset across process");
            var status = await f.Service.ReadAutomationAsync(project.Id);
            Check(status.Status == "budget" && status.RequestsToday == 8 && f.Handler.Calls == 8, "appwide daily cap incorrect");
        }),
        ("stage6 automation policy change allows new input and scanner lists only valid optins", async () => {
            using var f = new AutomaticFixture(); await f.Ready(); await f.Authorize();
            var valid = await f.Store.CreateProjectAsync("Synthetic");
            await f.Service.SetAutomationAsync(valid.Id, new(LearningEnabled: true, RemoteAllowed: true));
            await f.Service.SetAutomationAsync("missing-project", new(SummaryEnabled: true, RemoteAllowed: true));
            var scopes = await f.Service.ListAutomaticScopesAsync();
            Check(scopes.Count == 2 && scopes.Contains(null) && scopes.Contains(valid.Id), "scope scanner included unknown project or missed global");
            Check(await f.Service.RunAutomaticAsync(null, f.Options, "dummy"), "initial generation failed");
            await f.Service.SetAutomationAsync(null, new(LearningEnabled: true, RemoteAllowed: true)); await f.ElapseCooldown();
            Check(await f.Service.RunAutomaticAsync(null, f.Options, "dummy"), "new policy was suppressed");
        }),
        ("stage6 automation revocation deletion and disable fence late results", async () => {
            foreach (var mutation in new[] { "policy", "sharing", "delete", "disable", "source", "remote" })
            {
                using var f = new AutomaticFixture(); await f.Ready(); await f.Authorize();
                f.Handler.OnResponse = async () => {
                    switch (mutation)
                    {
                        case "policy": await f.Service.SetAutomationAsync(null, new()); break;
                        case "sharing": await f.Service.SetEnabledAsync(null, false); break;
                        case "delete": await f.Service.DeleteDocumentAsync(null, "summary.md"); break;
                        case "disable": await f.Service.SetDocumentEnabledAsync(null, "summary.md", false); break;
                        case "source": await f.Store.RemoveArtifactAsync((await f.Store.ListAsync()).Single().Id); break;
                        case "remote": await new TaskStore(f.Root).SetRemoteAllowedAsync(false); break;
                    }
                };
                Check(!await f.Service.RunAutomaticAsync(null, f.Options, "dummy"), "late result accepted: " + mutation);
                Check((await f.Service.ReadAsync()).CandidateDocuments.Count == 0, "candidate revived: " + mutation);
            }
        }),
        ("stage6 automation cancellation persists claim and prevents repeated input", async () => {
            using var f = new AutomaticFixture(); await f.Ready(); await f.Authorize(); f.Handler.Block = true;
            using var cts = new CancellationTokenSource();
            var pending = f.Service.RunAutomaticAsync(null, f.Options, "dummy", cts.Token);
            await f.Handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cts.Cancel();
            try { await pending; throw new Exception("cancellation ignored"); } catch (OperationCanceledException) { }
            Check(!await new ContextService(f.Store, f.Client).RunAutomaticAsync(null, f.Options, "dummy") && f.Handler.Calls == 1, "cancelled claim retried");
            Check((await f.Service.ReadAsync()).CandidateDocuments.Count == 0, "cancelled candidate saved");
        }),
        ("stage6 automation document toggle purges history without resurrecting deletion", async () => {
            using var f = new AutomaticFixture(); await f.Ready();
            await f.Service.SaveDocumentAsync(null, "summary.md", "MANUAL_DISABLED_SENTINEL"); await f.Service.SetEnabledAsync(null, true);
            var snapshot = await f.Service.ReadAsync(); var old = Directory.GetDirectories(Path.Combine(snapshot.Directory, "generations"));
            await f.Service.SetDocumentEnabledAsync(null, "summary.md", false);
            snapshot = await f.Service.ReadAsync();
            Check(snapshot.DisabledDocuments.Contains("summary.md") && snapshot.Documents["summary.md"].Contains("MANUAL_DISABLED_SENTINEL"), "toggle deleted content");
            Check(!old.Any(Directory.Exists) && !(await f.ExportedText()).Contains("MANUAL_DISABLED_SENTINEL"), "disabled export history survived");
            await f.Service.SetDocumentEnabledAsync(null, "summary.md", true);
            Check((await f.ExportedText()).Contains("MANUAL_DISABLED_SENTINEL"), "enabled manual content missing");
            await f.Service.DeleteDocumentAsync(null, "summary.md");
            try { await f.Service.SetDocumentEnabledAsync(null, "summary.md", true); throw new Exception("deleted document revived"); } catch (InvalidOperationException) { }
            try { await f.Service.SetDocumentEnabledAsync(null, "sources.md", false); throw new Exception("nonsemantic document toggle accepted"); } catch (ArgumentException) { }
        }),
        ("stage6 automation regression new evidence cannot rebind an omitted stale candidate", async () => {
            using var f = new Fixture(); f.Store.Add("old", null, "用户", "Old", "OLD_EVIDENCE");
            f.Handler.ModelContent = "{\"summary.md\":\"old summary\",\"style.md\":\"OLD_STYLE\"}";
            await f.Service.GenerateAsync(null, new("https://synthetic.invalid/v1", "synthetic", true), "dummy");
            f.Store.Items.Clear(); f.Store.Add("new", null, "用户", "New", "NEW_EVIDENCE");
            f.Handler.ModelContent = "{\"summary.md\":\"new summary\"}";
            await f.Service.GenerateAsync(null, new("https://synthetic.invalid/v1", "synthetic", true), "dummy");
            Check(!(await f.Service.ReadAsync()).CandidateDocuments.ContainsKey("candidate-style.md"), "old style rebound to a new fingerprint");
        }),
        ("stage6 automation regression policy off preserves confirmed documents and only stops future generation", async () => {
            using var f = new AutomaticFixture(); await f.Ready(); await f.Authorize();
            Check(await f.Service.RunAutomaticAsync(null, f.Options, "dummy"), "generation failed");
            await f.Service.AdoptCandidateAsync(null, "candidate-summary.md", "DERIVED_POLICY_SENTINEL");
            await f.Service.SaveDocumentAsync(null, "memory.md", "INDEPENDENT_MANUAL_MEMORY");
            await f.Service.SetEnabledAsync(null, true); await f.Service.SetAutomationAsync(null, new());
            var snapshot = await f.Service.ReadAsync();
            Check(snapshot.Documents.GetValueOrDefault("summary.md", "").Contains("DERIVED_POLICY_SENTINEL") && (await f.ExportedText()).Contains("DERIVED_POLICY_SENTINEL"), "generation controls silently removed a confirmed document");
            Check(snapshot.Documents["memory.md"] == "INDEPENDENT_MANUAL_MEMORY", "policy off removed manual content");
        }),
        ("stage6 automation reviewed adoption rejects a replacement candidate", async () => {
            using var f = new AutomaticFixture(); await f.Ready(); await f.Authorize();
            await f.Service.GenerateAsync(null, f.Options, "dummy");
            var reviewed = (await f.Service.ReadAsync()).CandidateDocuments["candidate-summary.md"];
            await f.Service.GenerateAsync(null, f.Options, "dummy");
            var method = typeof(ContextService).GetMethod("AdoptReviewedCandidateAsync") ?? throw new Exception("missing reviewed candidate adoption contract");
            try { await (Task)method.Invoke(f.Service, [null,"candidate-summary.md",reviewed,null,CancellationToken.None])!; throw new Exception("unseen replacement candidate adopted"); }
            catch(InvalidOperationException) { }
        }),
        ("stage6 automation edited adopted content requires explicit manual-detach authority", async () => {
            using var f = new AutomaticFixture(); await f.Ready(); await f.Authorize();
            await f.Service.GenerateAsync(null, f.Options, "dummy"); await f.Service.AdoptCandidateAsync(null,"candidate-summary.md");
            var derived = (await f.Service.ReadAsync()).Documents["summary.md"];
            try { await f.Service.SaveDocumentAsync(null,"summary.md",derived+"\nminor edit"); throw new Exception("ordinary save silently detached source provenance"); }
            catch(InvalidOperationException) { }
            var method = typeof(ContextService).GetMethod("SaveDocumentAsManualAsync") ?? throw new Exception("missing explicit manual save contract");
            await (Task)method.Invoke(f.Service,[null,"summary.md",derived+"\nmanual authority",CancellationToken.None])!;
            Check((await f.Service.ReadAsync()).Documents["summary.md"].Contains("manual authority"),"explicit manual save failed");
        }),
        ("stage6 automation regression abandoned durable claim is visible as interrupted", async () => {
            using var f = new AutomaticFixture(); await f.Ready(); await f.Authorize(); f.Handler.Fail = true;
            Check(!await f.Service.RunAutomaticAsync(null, f.Options, "dummy"), "failure not recorded");
            await f.SimulateAbandonedClaim();
            var restarted = new ContextService(f.Store, f.Client);
            Check((await restarted.ReadAutomationAsync()).Status == "interrupted", "abandoned claim still running");
            Check(!await restarted.RunAutomaticAsync(null, f.Options, "dummy") && f.Handler.Calls == 1, "abandoned claim resent");
        }),
        ("stage6 automation regression cold reference metadata never upgrades the scope lease", async () => {
            using var f = new AutomaticFixture(); var project = await f.ReadyReference(); await f.Authorize(projectId: project);
            Check(await f.Service.RunAutomaticAsync(project, f.Options, "dummy"), "initial generation failed");
            var cold = new ContextService(f.ColdReferenceStore(), f.Client);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            Check(!await cold.RunAutomaticAsync(project, f.Options, "dummy", timeout.Token) && f.Handler.Calls == 1, "cold duplicate sent a request");
        }),
        ("stage6 automation regression changed reference metadata withdraws adopted exports during idle tick", async () => {
            using var f = new AutomaticFixture(); var project = await f.ReadyReference(); await f.Authorize(projectId: project);
            Check(await f.Service.RunAutomaticAsync(project, f.Options, "dummy"), "initial generation failed");
            await f.Service.AdoptCandidateAsync(project, "candidate-summary.md", "IDLE_WITHDRAWAL_SENTINEL"); await f.Service.SetEnabledAsync(project, true);
            await File.WriteAllTextAsync(f.ReferenceSource, "different source metadata and content");
            Check(!await f.Service.RunAutomaticAsync(project, f.Options, "dummy"), "invalid evidence generated");
            Check(!(await f.ExportedText()).Contains("IDLE_WITHDRAWAL_SENTINEL"), "idle metadata observation left adopted publication stale");
        }),
        ("stage6 automation reference idle tick never opens originals and new policy strong checks before send", async () => {
            using var f = new AutomaticFixture(); var project = await f.ReadyReference(); await f.Authorize(projectId: project);
            Check(await f.Service.RunAutomaticAsync(project, f.Options, "dummy"), "reference generation failed");
            using (var held = new FileStream(f.ReferenceSource, FileMode.Open, FileAccess.Read, FileShare.None))
                Check(!await f.Service.RunAutomaticAsync(project, f.Options, "dummy") && (await f.Service.ReadAsync(project)).CandidateDocuments.Count > 0, "unchanged input reopened or invalidated original");
            var stamp = File.GetLastWriteTimeUtc(f.ReferenceSource);
            await File.WriteAllTextAsync(f.ReferenceSource, "changed source"); File.SetLastWriteTimeUtc(f.ReferenceSource, stamp);
            await f.Service.SetAutomationAsync(project, new(LearningEnabled: true, RemoteAllowed: true)); await f.ElapseCooldown();
            Check(!await f.Service.RunAutomaticAsync(project, f.Options, "dummy") && f.Handler.Calls == 1, "stale parsed reference reached provider");
            Check((await f.Service.ReadAsync(project)).CandidateDocuments.Count == 0, "stale reference candidates survived");
        }),
        ("stage6 automation externally edited adoption retains provenance and private recovery", async () => {
            using var f = new AutomaticFixture(); await f.Ready(); await f.Authorize();
            await f.Service.RunAutomaticAsync(null, f.Options, "dummy");
            await f.Service.AdoptCandidateAsync(null,"candidate-summary.md");
            await f.Service.SetEnabledAsync(null,true);
            var snapshot=await f.Service.ReadAsync();
            var generation=Directory.GetDirectories(Path.Combine(snapshot.Directory,"generations")).Single();
            await File.WriteAllTextAsync(Path.Combine(generation,"summary.md"),"EXTERNAL_DERIVED_SENTINEL");
            snapshot=await f.Service.ReadAsync();
            Check(snapshot.DerivedDocuments.Contains("summary.md"),"external edit detached provenance");
            await f.Store.RemoveArtifactAsync((await f.Store.ListAsync()).Single().Id);
            snapshot=await f.Service.ReadAsync();
            Check(!snapshot.Documents.Values.Any(x=>x.Contains("EXTERNAL_DERIVED_SENTINEL")) && !(await f.ExportedText()).Contains("EXTERNAL_DERIVED_SENTINEL"),"external derived edit survived withdrawal");
            var statePath=Path.Combine(f.Root,"context-state","global","state.json");
            using var state=JsonDocument.Parse(await File.ReadAllTextAsync(statePath));
            Check(state.RootElement.GetProperty("ExternalEdits").GetProperty("summary.md").GetString()=="EXTERNAL_DERIVED_SENTINEL","private recovery lost external edits");
        }),
        ("stage6 automation adopted candidates retain evidence and withdraw while manual saves survive", async () => {
            using var f = new AutomaticFixture(); await f.Ready(); await f.Authorize();
            Check(await f.Service.RunAutomaticAsync(null, f.Options, "dummy"), "generation failed");
            await f.Service.AdoptCandidateAsync(null, "candidate-overview.md", "EDITED_DERIVED_OVERVIEW");
            await f.Service.AdoptCandidateAsync(null, "candidate-summary.md", "EDITED_DERIVED_SUMMARY");
            await f.Service.SaveDocumentAsManualAsync(null, "summary.md", "INDEPENDENT_MANUAL_SUMMARY");
            await f.Service.SetEnabledAsync(null, true);
            var snapshot = await f.Service.ReadAsync();
            Check(snapshot.Documents["overview.md"].Contains("EDITED_DERIVED_OVERVIEW") && snapshot.Documents["overview.md"].Contains("来源版本指纹："), "overview refresh overwrote adoption or lost provenance");
            var sourceId = (await f.Store.ListAsync()).Single().Id;
            Check(snapshot.Documents["overview.md"].Contains("来源版本：" + sourceId + "@v1"), "edited adoption lost the explicit source version");
            var old = Directory.GetDirectories(Path.Combine(snapshot.Directory, "generations"));
            await f.Store.RemoveArtifactAsync((await f.Store.ListAsync()).Single().Id);
            snapshot = await new ContextService(f.Store).ReadAsync();
            Check(!snapshot.Documents["overview.md"].Contains("EDITED_DERIVED_OVERVIEW") && snapshot.Documents["summary.md"] == "INDEPENDENT_MANUAL_SUMMARY", "derived withdrawal or manual independence broken");
            Check(!old.Any(Directory.Exists) && !(await f.ExportedText()).Contains("EDITED_DERIVED_OVERVIEW"), "adopted historical export survived");
        })
    ];
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

    private sealed class AutomaticFixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "Amifolio-stage6-auto-" + Guid.NewGuid().ToString("N"));
        internal string Root => Path.Combine(directory, "data");
        internal PortfolioStore Store { get; }
        internal AutomaticHandler Handler { get; } = new();
        internal HttpClient Client { get; }
        internal ContextService Service { get; }
        internal ModelOptions Options => new("https://synthetic.invalid/v1", "synthetic", true);
        internal AutomaticFixture() { Directory.CreateDirectory(directory); Store = new(Root); Client = new(Handler); Service = new(Store, Client); }
        internal Task Ready() => AddSource(null);
        internal async Task AddSource(string? projectId)
        {
            var path = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".md");
            await File.WriteAllTextAsync(path, "用户明确说明：输出应简短，事实需要来源。Synthetic evidence only.");
            await Store.ImportFileAsync(path, projectId);
        }
        internal Task SaveConfiguration(bool allowed) => new ModelConfigurationStore(Root).SaveAsync(new(Options.Endpoint, Options.Model, allowed, ""));
        internal async Task Authorize(ContextAutomationOptions? options = null, string? projectId = null)
        {
            await SaveConfiguration(true); await new TaskStore(Root).SetRemoteAllowedAsync(true);
            await Service.SetAutomationAsync(projectId, options ?? new(SummaryEnabled: true, RemoteAllowed: true));
        }
        internal string ReferenceSource => Path.Combine(directory, "project", "notes.md");
        internal PortfolioStore ColdReferenceStore() { var result = new PortfolioStore(Root); result.EnableCodexReferenceMode(Path.Combine(directory, "codex")); return result; }
        internal async Task<string> ReadyReference()
        {
            var project = Directory.CreateDirectory(Path.Combine(directory, "project")).FullName;
            var home = Directory.CreateDirectory(Path.Combine(directory, "codex")).FullName;
            await File.WriteAllTextAsync(ReferenceSource, "initial source");
            await File.WriteAllTextAsync(Path.Combine(home, ".codex-global-state.json"), JsonSerializer.Serialize(new Dictionary<string, object> { ["local-projects"] = new Dictionary<string, object> { ["p"] = new { name = "Synthetic", rootPaths = new[] { project } } } }));
            Store.EnableCodexReferenceMode(home); await Store.RefreshCodexProjectsAsync();
            var id = Store.CodexProjects.Single().Id; var item = await Store.ReferenceFileAsync(ReferenceSource, id); await Store.ParseReferenceAsync(item.Artifact.Id); return id;
        }
        internal async Task SimulateAbandonedClaim()
        {
            var path = Path.Combine(Root, "context-state", "global", "state.json");
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject(); json["AutomationStatus"] = "running";
            await File.WriteAllTextAsync(path, json.ToJsonString());
        }
        internal async Task ElapseCooldown()
        {
            var path = Path.Combine(Root, "context-state", "automation-budget.json");
            var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            json["LastAttempt"] = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("O");
            await File.WriteAllTextAsync(path, json.ToJsonString());
        }
        internal async Task<string> StateText() => string.Join("\n", await Task.WhenAll(Directory.GetFiles(Path.Combine(Root, "context-state"), "*.json", SearchOption.AllDirectories).Select(p => File.ReadAllTextAsync(p))));
        internal async Task<string> ExportedText() => !Directory.Exists(Path.Combine(Root, "context")) ? "" : string.Join("\n", await Task.WhenAll(Directory.GetFiles(Path.Combine(Root, "context"), "*.md", SearchOption.AllDirectories).Select(p => File.ReadAllTextAsync(p))));
        public void Dispose()
        {
            Client.Dispose(); Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (!directory.StartsWith(Path.Combine(Path.GetTempPath(), "Amifolio-stage6-auto-"), StringComparison.OrdinalIgnoreCase)) throw new Exception("unsafe cleanup");
            foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }
    private sealed class AutomaticHandler : HttpMessageHandler
    {
        internal int Calls;
        internal string Payload = "";
        internal bool Fail, Block;
        internal Func<Task>? OnResponse;
        internal string Content = "{\"summary.md\":\"Synthetic summary [evidence]\",\"overview.md\":\"Synthetic overview [evidence]\",\"memory.md\":\"Explicit constraint [evidence]\"}";
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Payload = await request.Content!.ReadAsStringAsync(ct); Started.TrySetResult();
            if (Block) await Task.Delay(Timeout.Infinite, ct);
            if (OnResponse is not null) await OnResponse();
            return new(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = Content } } } })) };
        }
    }
}
