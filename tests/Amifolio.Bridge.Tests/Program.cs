using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Amifolio.Context;
using Amifolio.Core;

var suite = new BridgeSuite();
return await suite.RunAsync();

internal sealed partial class BridgeSuite
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "amifolio-bridge-tests-" + Guid.NewGuid().ToString("N"));
    private string root = null!, dll = null!, data = null!, source = null!, projectA = null!, projectB = null!, id = null!, codexHome = null!, caseRoot = null!, projectRootA = null!, projectRootB = null!;
    private PortfolioStore store = null!;
    private int passed, failed, caseNumber;
    private readonly string? testFilter=Environment.GetEnvironmentVariable("AMIFOLIO_TEST_FILTER");
    private const string PrivateContents = "PRIVATE_RAW_CONTENT_SHOULD_NEVER_APPEAR_IN_SEARCH_9387";

    public async Task<int> RunAsync()
    {
        try
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Amifolio.Core", "Models.cs"))) directory = directory.Parent;
            root = directory?.FullName ?? throw new Exception("Repository root not found.");
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            dll = Path.Combine(root, "src", "Amifolio.Bridge", "bin", configuration, "net10.0", "Amifolio.Bridge.dll");
            Check(File.Exists(dll), "Build bridge before running its child-process tests.");
            Directory.CreateDirectory(temporary);

            await Test("CLI submit commits a reference without copying, parsing or source writes", async () =>
            {
                var before = await SourceSnapshot();
                var result = await Cli("submit", source, "--project", projectA, "--source", "Codex", "--tags", "final,report");
                Check(result.Exit == 0, result.Error);
                Check(result.Json.EnumerateObject().Count() == 3, "Submit receipt exposes unexpected fields.");
                id = result.Json.GetProperty("id").GetString()!;
                Check(result.Json.GetProperty("path").GetString() == source, "Receipt does not identify the actual source.");
                Check(result.Json.GetProperty("status").GetString() == "referenced", "Reference was falsely called archived.");
                var record = await store.GetAsync(id);
                Check(record is { IsReference: true, Preview.Length: 0, Hash.Length: 0 } && record.ObjectPath == source, "Receipt lacks an unparsed reference record.");
                Check((await new TaskStore(data).ListAsync()).Count == 0, "Submit scheduled parsing.");
                Check((!Directory.Exists(Path.Combine(data,"objects"))||!Directory.EnumerateFiles(Path.Combine(data, "objects")).Any()), "Submit created an archive object.");
                Check(before == await SourceSnapshot(), "Submit changed source bytes, attributes, timestamps or directory entries.");
            });
            await Test("CLI task query and cancel work but retry and drain cannot parse",async()=>{
                var tasks=new TaskStore(data);var task=await tasks.EnqueueExtractionAsync(id);var listed=await Cli("tasks");Check(listed.Exit==0&&listed.Json.GetProperty("tasks").GetArrayLength()>0,"task query failed");
                Check((await Cli("task-cancel",task)).Exit==0&&(await tasks.GetAsync(task))?.State=="cancelled","CLI cancel failed");
                foreach(var args in new[]{new[]{"task-retry",task},new[]{"process-tasks"}}){var result=await Cli(args);Check(result.Exit!=0&&result.Json.GetProperty("error").GetProperty("code").GetString()=="confirmation_required","CLI started parsing without app user action");}
                Check((await tasks.GetAsync(task))?.State=="cancelled","Refused retry mutated the task");
                Check((await store.GetAsync(id)) is { Preview.Length:0, Hash.Length:0 },"CLI parsed source content");
            });
            await Test("CLI refuses model retry without preview confirmation",async()=>{
                var tasks=new TaskStore(data);var task=await tasks.EnqueueGenerationAsync(projectA,new string('a',64),0);await tasks.CancelAsync(task);
                var result=await Cli("task-retry",task);Check(result.Exit!=0&&result.Json.GetProperty("error").GetProperty("code").GetString()=="confirmation_required","CLI replayed model request");
            });
            await Test("CLI fresh process verifies status and project metadata", async () =>
            {
                var status = await Cli("status", id);
                Check(status.Exit == 0 && status.Json.GetProperty("status").GetString() == "referenced", status.Error);
                var listing = await Cli("list", "--search", "report", "--project", projectA);
                Check(listing.Exit == 0 && listing.Json.GetProperty("artifacts").GetArrayLength() == 1, listing.Error);
                Check(!listing.Json.GetRawText().Contains(PrivateContents), "Raw search content leaked.");
                var projects = await Cli("projects");
                Check(projects.Exit == 0 && projects.Json.GetProperty("projects").GetArrayLength() == 2, projects.Error);
                Check(!projects.Json.GetRawText().Contains("rootPath"), "Project root paths leaked.");
            });
            await Test("CLI failures return one error line and nonzero exit", async () =>
            {
                string[][] bad = [[], ["unknown"], ["status"], ["submit", source, "--bogus", "x"],
                    ["projects", "--search", "x"], ["submit", source, "--source", ""], ["list", "--project", "unknown"],
                    ["submit", source, "--project", "unknown"], ["submit", Path.Combine(temporary, "missing.md")],
                    ["list", "--search", "a", "--search", "b"], ["status", "not-an-id"]];
                foreach (var args in bad)
                {
                    var result = await Cli(args);
                    Check(result.Exit != 0 && result.Json.TryGetProperty("error", out _), "CLI failure acknowledged success.");
                    Check(!result.Json.GetRawText().Contains("stackTrace", StringComparison.OrdinalIgnoreCase), "Stack trace leaked.");
                }
            });
            await Test("CLI repo snapshots and directory submissions are rejected", async () =>
            {
                var repo = Path.Combine(projectRootB, "repo");
                Directory.CreateDirectory(repo);
                await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "repository deliverable");
                var result = await Cli("repo", repo, "--project", projectB);
                Check(result.Exit != 0 && result.Json.TryGetProperty("error", out _), "Legacy repo snapshot was accepted.");
                Check((await Cli("submit",repo,"--project",projectB)).Exit!=0,"Directory submission was accepted");
                Check((!Directory.Exists(Path.Combine(data,"objects"))||!Directory.EnumerateFiles(Path.Combine(data,"objects")).Any()),"Rejected snapshot created a copy");
            });
            await Test("CLI infers a discovered project and legacy labels do not edit source metadata", async () =>
            {
                var file = Path.Combine(projectRootB, "default.txt");
                await File.WriteAllTextAsync(file, "default source");
                var result = await Cli("submit", file);
                Check(result.Exit == 0, result.Error);
                var record=(await store.GetAsync(result.Json.GetProperty("id").GetString()!))!;
                Check(record.Source == "Codex" && record.ProjectId==projectB && record.IsReference, "Reference was not associated with the discovered project.");
                var process = await RunChild(Dotnet(), [dll, "projects", "--data-root", data]);
                Check(process.Exit == 0 && ParseOne(process.Output).GetProperty("projects").GetArrayLength() == 2, process.Error);
            });

            await Test("CLI rejects undiscovered paths, mismatched projects, secrets and manual verbs", async () =>
            {
                var outside=Path.Combine(caseRoot,"outside.md");await File.WriteAllTextAsync(outside,"outside scope");
                var sibling=projectRootA+"-other";Directory.CreateDirectory(sibling);var escaped=Path.Combine(sibling,"escape.md");await File.WriteAllTextAsync(escaped,"prefix escape");
                var secret=Path.Combine(projectRootA,".env");await File.WriteAllTextAsync(secret,"TOKEN=private");
                var config=Path.Combine(projectRootA,"AGENTS.md");await File.WriteAllTextAsync(config,"private rules");
                var before=await SourceSnapshot();var count=(await store.ListAsync()).Count;
                string[][] bad=[["submit",outside],["submit",escaped],["submit",secret],["submit",config],["submit",source,"--project",projectB],
                    ["submit",Path.Combine(projectRootB,"..","..","outside.md")],["create-project","name"],["update",id],["remove",id],["parse",id]];
                foreach(var args in bad)Check((await Cli(args)).Exit!=0,"Unsupported path or mutation was accepted: "+args[0]);
                Check((await store.ListAsync()).Count==count&&before==await SourceSnapshot(),"Rejected operations mutated project files or records");
            });

            await Test("CLI status reports changed and missing references without a false archive receipt", async () =>
            {
                await File.WriteAllTextAsync(source,"different bytes and length");
                var changed=await Cli("status",id);Check(changed.Exit==0&&changed.Json.GetProperty("status").GetString()=="changed","Changed reference was called current");
                File.Delete(source);
                var missing=await Cli("status",id);Check(missing.Exit==0&&missing.Json.GetProperty("status").GetString()=="missing","Missing reference was called archived or raised an unstructured failure");
            });

            await Test("Reference mode hides legacy archive records and manually created projects", async () =>
            {
                // Synthetic old application state, not a product path: no source files are copied.
                var legacy=new PortfolioStore(data);var oldProject=await legacy.CreateProjectAsync("Legacy-only",caseRoot);
                var generated=await legacy.SaveGeneratedAsync("legacy.md","legacy application bytes",oldProject.Id);
                var listing=await Cli("list");Check(listing.Exit==0&&listing.Json.GetProperty("artifacts").GetArrayLength()==2,"Legacy archive leaked into default references");
                Check((await Cli("status",generated.Artifact.Id)).Exit!=0,"Legacy archive received a reference receipt");
                Check((await Cli("projects")).Json.GetProperty("projects").GetArrayLength()==2,"Manual project leaked into discovery");
            });

            await Test("MCP lifecycle, five tools, status, search, projects and receipts", async () =>
            {
                await using var rpc = new RpcChild(this);
                var early = await rpc.Request("tools/list", new { });
                Check(ErrorCode(early) == -32002, "Pre-initialize tools were accepted.");
                Check((await rpc.Request("ping", new { })).TryGetProperty("result", out _), "Early ping failed.");
                await rpc.Initialize();
                var tools = (await rpc.Request("tools/list", new { })).GetProperty("result").GetProperty("tools");
                var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order().ToArray();
                Check(names.SequenceEqual(new[] { "artifact_search", "artifact_status", "artifact_submit", "context_read", "learning_capture", "learning_claim", "learning_complete", "learning_context", "learning_status", "project_resolve", "projects_list" }), "Unexpected tool surface.");
                var status = Structured(await rpc.Tool("artifact_status", new { id }));
                Check(status.GetProperty("status").GetString() == "referenced", "MCP status failed.");
                var search = Structured(await rpc.Tool("artifact_search", new { search = "report", projectId = projectA, limit = 1 }));
                Check(search.GetProperty("artifacts").GetArrayLength() == 1, "Scoped search failed.");
                var metadata = search.GetProperty("artifacts")[0];
                Check(!metadata.TryGetProperty("preview", out _) && !metadata.TryGetProperty("objectPath", out _) && !metadata.TryGetProperty("hash", out _), "Raw/archive fields exposed.");
                Check(!search.GetRawText().Contains(PrivateContents), "Raw content leaked.");
                var allMetadata = Structured(await rpc.Tool("artifact_search", new { }));
                Check(allMetadata.GetProperty("artifacts").EnumerateArray().Any(item => item.GetProperty("projectId").GetString() == projectB), "Unfiltered local portfolio metadata contract changed.");
                Check(search.GetProperty("artifacts").EnumerateArray().All(item => item.GetProperty("projectId").GetString() == projectA), "Project metadata filter leaked another project.");
                Check(Structured(await rpc.Tool("projects_list", new { })).GetProperty("projects").GetArrayLength() == 2, "Projects tool failed.");
                var final = Path.Combine(projectRootA, "rpc-deliverable.md");
                await File.WriteAllTextAsync(final, "MCP user deliverable");
                var receipt = Structured(await rpc.Tool("artifact_submit", new { path = final, projectId = projectA, source = "Codex", tags = "final" }));
                Check(receipt.EnumerateObject().Count() == 3 && receipt.GetProperty("path").GetString()==final && receipt.GetProperty("status").GetString()=="referenced", "Invalid MCP reference receipt.");
                Check(Structured(await rpc.Tool("artifact_status", new { id = receipt.GetProperty("id").GetString() })).GetProperty("status").GetString() == "referenced", "Receipt status failed.");
                Check((await new TaskStore(data).ListAsync()).Count==0 && (!Directory.Exists(Path.Combine(data,"objects"))||!Directory.EnumerateFiles(Path.Combine(data,"objects")).Any()),"MCP copied or scheduled parsing");
            });

            await Test("MCP parse and validation errors recover without extra stdout", async () =>
            {
                await using var rpc = new RpcChild(this);
                foreach (var malformed in new[] { "{", "[]", "{\"jsonrpc\":\"1.0\",\"id\":1,\"method\":\"ping\"}", "{\"jsonrpc\":\"2.0\",\"id\":null,\"method\":\"ping\"}", "{\"jsonrpc\":\"2.0\",\"id\":true,\"method\":\"ping\"}", "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\",\"method\":\"ping\"}" })
                {
                    var result = await rpc.Raw(malformed);
                    Check(result.TryGetProperty("error", out _), "Malformed request accepted.");
                }
                Check(ErrorCode(await rpc.Raw(new string('x', 70000))) == -32700, "Oversized line accepted.");
                Check(ErrorCode(await rpc.Raw("{\"x\":" + new string('[', 40) + "0" + new string(']', 40) + "}")) == -32700, "Nesting not bounded.");
                await rpc.InvalidUtf8();
                Check(ErrorCode(await rpc.Read()) == -32700, "Invalid UTF-8 accepted.");
                Check((await rpc.Request("ping", new { })).TryGetProperty("result", out _), "Parse error poisoned the next request.");
                var stringId = await rpc.Raw("{\"jsonrpc\":\"2.0\",\"id\":\"client-string-id\",\"method\":\"ping\"}");
                Check(stringId.GetProperty("id").GetString() == "client-string-id", "String request ID was not echoed.");
                var initialized = await rpc.Request("initialize", new { protocolVersion = "future-version", capabilities = new { }, clientInfo = new { name = "test", version = "1" } });
                Check(initialized.GetProperty("result").GetProperty("protocolVersion").GetString() == "2025-11-25", "Version negotiation failed.");
                await rpc.Notify("notifications/initialized", new { });
                Check(ErrorCode(await rpc.Request("nonexistent", new { })) == -32601, "Unknown method not rejected.");
                Check(ErrorCode(await rpc.Request("tools/call", new { name = "open_path", arguments = new { path = source } })) == -32602, "Arbitrary path reader accepted.");
                object[] badArguments = [new { name = "artifact_submit", arguments = new { } }, new { name = "artifact_search", arguments = new { limit = 51 } },
                    new { name = "artifact_search", arguments = new { limit = 0 } }, new { name = "artifact_search", arguments = new { limit = 1.5 } },
                    new { name = "artifact_search", arguments = new { projectId = (string?)null } }, new { name = "context_read", arguments = new { layer = "sources" } },
                    new { name = "context_read", arguments = new { path = source } }, new { name = "artifact_submit", arguments = new { path = source, source = new string('a', 129) } },
                    new { name = "artifact_submit", arguments = new { path = source, source = " 应用 " } },
                    new { name = "projects_list", arguments = new { unknown = true } }, new { name = "artifact_status", arguments = new { id = 123 } }];
                foreach (var invalid in badArguments) Check(ErrorCode(await rpc.Request("tools/call", invalid)) == -32602, "Invalid tool arguments accepted.");
            });

            await Test("MCP notifications do not write files or receive responses", async () =>
            {
                var file = Path.Combine(projectRootA, "notification-must-not-submit.md");
                await File.WriteAllTextAsync(file, "not authorized as a request");
                await using var rpc = new RpcChild(this);
                await rpc.Initialize();
                await rpc.Notify("notifications/progress", new { progress = 1 });
                await rpc.Notify("tools/call", new { name = "artifact_submit", arguments = new { path = file } });
                var ping = await rpc.Request("ping", new { });
                Check(ping.GetProperty("result").EnumerateObject().Count() == 0, "A notification received a response.");
                Check(!(await store.ListAsync()).Any(a => a.OriginalName == Path.GetFileName(file)), "Notification invoked a mutation.");
            });

            await Test("MCP execution failures are tool errors and do not acknowledge storage", async () =>
            {
                await using var rpc = new RpcChild(this);
                await rpc.Initialize();
                var before = (await store.ListAsync()).Count;
                var failure = await rpc.Tool("artifact_submit", new { path = Path.Combine(temporary, "missing-output.md") });
                Check(failure.GetProperty("result").GetProperty("isError").GetBoolean(), "Missing file acknowledged.");
                Check(!Structured(failure, true).TryGetProperty("id", out _), "Failed tool fabricated an ID.");
                Check((await store.ListAsync()).Count == before, "Failed import left an indexed artifact.");
                var unknownProject = await rpc.Tool("context_read", new { projectId = "unknown" });
                Check(unknownProject.GetProperty("result").GetProperty("isError").GetBoolean(), "Unknown project used global fallback.");
                var unknownStatus = await rpc.Tool("artifact_status", new { id = "unknown" });
                Check(unknownStatus.GetProperty("result").GetProperty("isError").GetBoolean(), "Unknown status acknowledged.");
                var outside=Path.Combine(caseRoot,"outside.txt");await File.WriteAllTextAsync(outside,PrivateContents);
                var secret=Path.Combine(projectRootA,".env");await File.WriteAllTextAsync(secret,"API_KEY=private");
                foreach(var path in new[]{outside,secret,projectRootA})
                {var rejected=await rpc.Tool("artifact_submit",new{path});Check(rejected.GetProperty("result").GetProperty("isError").GetBoolean()&&!Structured(rejected,true).TryGetProperty("id",out _),"MCP accepted a forbidden path");}
                Check((await store.ListAsync()).Count==before,"Rejected MCP reference changed indexed metadata");
            });

            await Test("MCP refreshes discovery per request and rejects revoked project access", async () =>
            {
                await using var rpc=new RpcChild(this);await rpc.Initialize();
                await WriteProjects(("Project B",projectRootB));
                Check(Structured(await rpc.Tool("projects_list",new{})).GetProperty("projects").GetArrayLength()==1,"MCP retained stale discovery");
                Check(Structured(await rpc.Tool("artifact_search",new{})).GetProperty("artifacts").EnumerateArray().All(a=>a.GetProperty("projectId").GetString()==projectB),"Revoked project metadata leaked");
                foreach(var response in new[]{await rpc.Tool("artifact_submit",new{path=source}),await rpc.Tool("artifact_status",new{id}),await rpc.Tool("context_read",new{projectId=projectA})})
                    Check(response.GetProperty("result").GetProperty("isError").GetBoolean(),"Revoked project access succeeded");
            });

            await Test("Context reads enforce enabled state, exact scope and layer bounds", async () =>
            {
                var context = new ContextService(store);
                foreach (var scope in new string?[] { null, projectA, projectB })
                {
                    await context.SetEnabledAsync(scope, true);
                    var marker = scope is null ? "GLOBAL_ONLY_491" : scope == projectA ? "PROJECT_A_ONLY_492" : "PROJECT_B_ONLY_493";
                    await context.SaveDocumentAsync(scope, "overview.md", marker);
                    await context.SaveDocumentAsync(scope, "memory.md", marker + " MEMORY");
                    await context.SaveDocumentAsync(scope, "style.md", new string('中', 18000));
                    await context.PublishAsync(scope);
                }
                await using var rpc = new RpcChild(this);
                await rpc.Initialize();
                var global = Structured(await rpc.Tool("context_read", new { })).GetRawText();
                Check(global.Contains("GLOBAL_ONLY_491") && !global.Contains("PROJECT_A_ONLY_492") && !global.Contains("PROJECT_B_ONLY_493"), "Global context leaked a project.");
                var a = Structured(await rpc.Tool("context_read", new { projectId = projectA }));
                Check(a.GetRawText().Contains("PROJECT_A_ONLY_492") && !a.GetRawText().Contains("GLOBAL_ONLY_491") && !a.GetRawText().Contains("PROJECT_B_ONLY_493"), "Context scope widened.");
                Check(!a.GetProperty("documents").TryGetProperty("sources.json", out _) && !a.GetProperty("documents").TryGetProperty("memory.md", out _), "Default read revealed a non-overview layer.");
                var memory = Structured(await rpc.Tool("context_read", new { projectId = projectA, layer = "memory" }));
                Check(memory.GetProperty("documents").EnumerateObject().Count() == 1 && memory.GetRawText().Contains("MEMORY"), "Layer selection failed.");
                var style = Structured(await rpc.Tool("context_read", new { projectId = projectA, layer = "style" }));
                Check(style.GetProperty("truncated").GetBoolean() && style.GetProperty("documents").GetProperty("style.md").GetString()!.Length == 12000, "Layer length not bounded.");
                using var client = new HttpClient(new CandidateHandler());
                var modelContext = new ContextService(store, client);
                await store.ParseReferenceAsync(id); // The test explicitly authorizes parsing; bridge registration never does.
                await modelContext.GenerateAsync(projectA, new("https://provider.invalid/v1", "test-model", true), "test-session-key");
                var candidateSnapshot = await modelContext.ReadAsync(projectA);
                Check(candidateSnapshot.CandidateDocuments.Values.Any(text => text.Contains("UNCONFIRMED_CANDIDATE_729")), "Candidate fixture did not create a suggestion.");
                foreach (var layer in new[] { "overview", "memory", "style", "summary" })
                {
                    var confirmedOnly = Structured(await rpc.Tool("context_read", new { projectId = projectA, layer }));
                    Check(!confirmedOnly.GetRawText().Contains("UNCONFIRMED_CANDIDATE_729"), "Unconfirmed candidate leaked through context_read.");
                }
                await context.SetEnabledAsync(projectA, false);
                var disabled = Structured(await rpc.Tool("context_read", new { projectId = projectA, layer = "memory" }));
                Check(!disabled.GetProperty("enabled").GetBoolean() && disabled.GetProperty("documents").EnumerateObject().Count() == 0, "Disabled context was exposed.");
                var cliContext = await Cli("context", "--project", projectA);
                Check(cliContext.Exit == 0 && !cliContext.Json.GetProperty("enabled").GetBoolean(), "CLI bypassed disabled context.");
            });

            await Test("Disabled documents stay private through CLI and MCP layers", async () =>
            {
                var service = new ContextService(store);
                await service.SaveDocumentAsync(projectA, "memory.md", "DISABLED_MEMORY_SENTINEL");
                await service.SetEnabledAsync(projectA, true); await service.SetDocumentEnabledAsync(projectA, "memory.md", false);
                await using var rpc = new RpcChild(this); await rpc.Initialize();
                var result = Structured(await rpc.Tool("context_read", new { projectId = projectA, layer = "memory" }));
                Check(!result.GetRawText().Contains("DISABLED_MEMORY_SENTINEL") && !result.GetProperty("documents").EnumerateObject().Any(), "MCP exposed disabled memory");
                var cli = await Cli("context", "--project", projectA, "--layer", "memory");
                Check(cli.Exit == 0 && !cli.Json.GetRawText().Contains("DISABLED_MEMORY_SENTINEL"), "CLI exposed disabled memory");
            });
            await Test("Confirmed general preferences have a bounded global-only MCP layer", async () =>
            {
                await store.ParseReferenceAsync(id);
                var service = new ContextService(store); await service.SetPreferenceParticipationAsync(projectA, true);
                var entry = await service.ProposePreferenceAsync(projectA, id, "回答时请简洁表述。");
                await service.SetEnabledAsync(null, true);
                await using var rpc = new RpcChild(this); await rpc.Initialize();
                var pending = Structured(await rpc.Tool("context_read", new { layer = "preferences" }));
                Check(!pending.GetRawText().Contains(entry.Text), "candidate preference leaked");
                await service.ConfirmPreferenceAsync(entry.Id, entry.Text);
                var confirmed = Structured(await rpc.Tool("context_read", new { layer = "preferences" }));
                var documents = confirmed.GetProperty("documents");
                Check(documents.EnumerateObject().Count() == 1 && documents.GetProperty("preferences.md").GetString()!.Contains(entry.Text), "confirmed global preference unavailable");
                Check(!documents.GetRawText().Contains(projectA) && !documents.GetRawText().Contains(source) && !documents.GetRawText().Contains(id), "private preference provenance leaked");
                Check((await rpc.Tool("context_read", new { projectId = projectA, layer = "preferences" })).GetProperty("result").GetProperty("isError").GetBoolean(), "preference layer widened a project scope");
                await service.SetPreferenceParticipationAsync(projectA, false);
                Check(!Structured(await rpc.Tool("context_read", new { layer = "preferences" })).GetRawText().Contains(entry.Text), "revoked preference leaked");
            });

            await Test("Search results are bounded, marked truncated and metadata only", async () =>
            {
                for (var index = 0; index < 53; index++)
                {
                    var file = Path.Combine(projectRootB, $"bounded-{index:D3}.txt");
                    await File.WriteAllTextAsync(file, PrivateContents + index);
                    await store.ReferenceFileAsync(file, projectB);
                }
                await using var rpc = new RpcChild(this);
                await rpc.Initialize();
                var result = Structured(await rpc.Tool("artifact_search", new { search = "bounded-", projectId = projectB }));
                Check(result.GetProperty("artifacts").GetArrayLength() == 50 && result.GetProperty("truncated").GetBoolean(), "Unbounded metadata search.");
                Check(!result.GetRawText().Contains(PrivateContents), "Search leaked raw contents.");
                Check(result.GetProperty("artifacts")[0].GetProperty("tags").GetString()!.Length <= 512, "Metadata field unbounded.");
                var limited = Structured(await rpc.Tool("artifact_search", new { search = "bounded-", projectId = projectB, limit = 2 }));
                Check(limited.GetProperty("artifacts").GetArrayLength() == 2 && limited.GetProperty("truncated").GetBoolean(), "Requested limit ignored.");
            });

            await Test("CLI legacy backup and restore cannot write arbitrary project paths", async () =>
            {
                var before=await SourceSnapshot();var archive=Path.Combine(projectRootA,"must-not-create.zip");
                foreach(var args in new[]{new[]{"backup",archive},new[]{"restore",source}}){var result=await Cli(args);Check(result.Exit!=0&&result.Json.TryGetProperty("error",out _),"Legacy maintenance path command accepted");}
                Check(!File.Exists(archive)&&before==await SourceSnapshot(),"Maintenance route changed the project");
            });
            await Test("CLI object cleanup rejects invalid retention and preserves reference sources", async () =>
            {
                Check((await Cli("collect","--retention-days","-1")).Exit!=0,"negative retention accepted");
                var result=await Cli("collect","--retention-days","0");Check(result.Exit==0,result.Error);
                Check(File.Exists(source)&&await File.ReadAllTextAsync(source)==PrivateContents,"CLI cleanup deleted or changed a reference source");
            });
            await Test("Codex Stop ignores old grants and refreshes metadata without copying or parsing", async () =>
            {
                var dir=Path.Combine(projectRootA,"outputs");Directory.CreateDirectory(dir);await File.WriteAllTextAsync(Path.Combine(dir,"hook.md"),"synthetic hook output");
                var hook=JsonSerializer.Serialize(new{cwd=projectRootA,hook_event_name="Stop",turn_id="synthetic-turn",transcript_path=source});
                var collection=new CollectionService(store);await collection.GrantAsync(projectA,dir,true);
                await File.WriteAllTextAsync(source,"changed reference metadata");var before=await SourceSnapshot();
                (int Exit,string Output,string Error) result;
                await using(var locked=new FileStream(source,FileMode.Open,FileAccess.ReadWrite,FileShare.None))result=await RunHook(hook);
                Check(result.Exit==0&&ParseOne(result.Output).GetProperty("continue").GetBoolean(),result.Error);
                Check((await collection.ListReceiptsAsync()).Count==0,"Stop invoked the old collector");
                Check((await store.ListAsync()).Count==2,"Stop indexed unsolicited files");
                Check((await store.GetAsync(id))!.ExtractionStatus.Contains("变化"),"Stop failed to refresh referenced source metadata");
                Check((await new TaskStore(data).ListAsync()).Count==0&&(!Directory.Exists(Path.Combine(data,"objects"))||!Directory.EnumerateFiles(Path.Combine(data,"objects")).Any()),"Stop copied or parsed a file");
                Check(before==await SourceSnapshot(),"Stop changed source files or Codex metadata");
                var count=(await store.ListAsync()).Count;await RunHook(hook);Check((await store.ListAsync()).Count==count,"Stop duplicated references");
                var invalid=await RunHook("{\"cwd\":42}");Check(invalid.Exit!=0&&ParseOne(invalid.Output).GetProperty("continue").GetBoolean(),"invalid hook must not block Codex");
            });
            await Test("Stop rejects undiscovered cwd, oversized or wrong events and never opens transcripts", async () =>
            {
                var transcript=Path.Combine(codexHome,"auth.json");await File.WriteAllTextAsync(transcript,"DO_NOT_READ_TRANSCRIPT_OR_AUTH");
                await using(var locked=new FileStream(transcript,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
                {
                    var accepted=await RunHook(JsonSerializer.Serialize(new{cwd=projectRootA,hook_event_name="Stop",transcript_path=transcript}));
                    Check(accepted.Exit==0,accepted.Error);
                }
                foreach(var input in new[]{JsonSerializer.Serialize(new{cwd=caseRoot,hook_event_name="Stop"}),JsonSerializer.Serialize(new{cwd=projectRootA,hook_event_name="Start"}),new string('x',70000)})
                {var refused=await RunHook(input);Check(refused.Exit!=0&&ParseOne(refused.Output).GetProperty("continue").GetBoolean(),"Invalid hook was accepted or blocked the host");}
            });
            await Test("Installer is dry-run by default and backs up only the selected skill", async () =>
            {
                var skills = Path.Combine(temporary, "install", "skills");
                var script = Path.Combine(root, "plugins", "codex", "Install-Skill.ps1");
                var preview = await RunChild("pwsh", ["-NoProfile", "-File", script, "-SkillsRoot", skills]);
                Check(preview.Exit == 0 && ParseOne(preview.Output).GetProperty("mode").GetString() == "dry-run", preview.Error);
                Check(!Directory.Exists(skills), "Default dry-run changed files.");
                var whatIf = await RunChild("pwsh", ["-NoProfile", "-File", script, "-SkillsRoot", skills, "-Install", "-WhatIf"]);
                Check(whatIf.Exit == 0 && !Directory.Exists(skills), "WhatIf changed files.");
                Directory.CreateDirectory(Path.Combine(skills, "amifolio"));
                Directory.CreateDirectory(Path.Combine(skills, "other"));
                await File.WriteAllTextAsync(Path.Combine(skills, "amifolio", "old.txt"), "old version");
                await File.WriteAllTextAsync(Path.Combine(skills, "other", "keep.txt"), "keep");
                var installed = await RunChild("pwsh", ["-NoProfile", "-File", script, "-SkillsRoot", skills, "-Install"]);
                Check(installed.Exit == 0, installed.Error);
                var receipt = ParseOne(installed.Output);
                var backup = receipt.GetProperty("backup").GetString()!;
                Check(await File.ReadAllTextAsync(Path.Combine(backup, "old.txt")) == "old version", "Existing version lost.");
                Check(File.Exists(Path.Combine(skills, "amifolio", "SKILL.md")) && !File.Exists(Path.Combine(skills, "amifolio", "old.txt")), "Replacement was merged instead of replaced.");
                Check(await File.ReadAllTextAsync(Path.Combine(skills, "other", "keep.txt")) == "keep", "Other skill modified.");
                Check(Directory.GetFiles(Path.Combine(skills, "amifolio"), "*", SearchOption.AllDirectories).Length == 1, "Installer copied files beyond the skill.");
            });
            await LearningTestsAsync();
            await HookLearningTestsAsync();
        }
        catch (Exception e) { failed++; Console.Error.WriteLine("SETUP/TEARDOWN: " + e); }
        finally
        {
            // Only this suite's verified temporary directory may be removed.
            var full = Path.GetFullPath(temporary);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(full).StartsWith("amifolio-bridge-tests-", StringComparison.Ordinal) && Directory.Exists(full))
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                try
                {
                    // Core intentionally marks archive copies read-only. Only relax our own disposable copies.
                    var enumeration = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                    foreach (var file in Directory.EnumerateFiles(full, "*", enumeration))
                    {
                        Check(Path.GetFullPath(file).StartsWith(full + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Cleanup path escaped the suite directory.");
                        File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                    }
                    Directory.Delete(full, true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { failed++; Console.Error.WriteLine("Temporary cleanup failed: " + e.Message); }
            }
        }
        Console.WriteLine($"Bridge integration tests: {passed} passed, {failed} failed.");
        return failed == 0 ? 0 : 1;
    }

    private async Task Test(string name, Func<Task> test)
    {
        if(testFilter is not null&&!name.Contains(testFilter,StringComparison.OrdinalIgnoreCase))return;
        try { await SetUpCaseAsync(); await test().WaitAsync(TimeSpan.FromSeconds(90)); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + e); }
    }

    private async Task SetUpCaseAsync()
    {
        caseRoot=Path.Combine(temporary,$"case-{++caseNumber:D2}");
        codexHome=Path.Combine(caseRoot,"fake-codex-home");data=Path.Combine(caseRoot,"data 中文 with spaces");
        projectRootA=Path.Combine(caseRoot,"project-a");projectRootB=Path.Combine(caseRoot,"project-b");
        Directory.CreateDirectory(codexHome);Directory.CreateDirectory(projectRootA);Directory.CreateDirectory(projectRootB);
        await WriteProjects(("Project A",projectRootA),("Project B",projectRootB));
        source=Path.Combine(projectRootA,"final 中文 report.md");await File.WriteAllTextAsync(source,PrivateContents,new UTF8Encoding(false));
        var other=Path.Combine(projectRootB,"other-project.txt");await File.WriteAllTextAsync(other,"Other discovered project");
        store=new PortfolioStore(data);store.EnableCodexReferenceMode(codexHome);await store.RefreshCodexProjectsAsync();
        projectA=store.CodexProjects.Single(p=>p.RootPath==projectRootA).Id;projectB=store.CodexProjects.Single(p=>p.RootPath==projectRootB).Id;
        id=(await store.ReferenceFileAsync(source,projectA)).Artifact.Id;await store.ReferenceFileAsync(other,projectB);
    }

    private Task WriteProjects(params (string Name,string Root)[] projects)=>File.WriteAllTextAsync(Path.Combine(codexHome,".codex-global-state.json"),
        JsonSerializer.Serialize(new Dictionary<string,object>{["local-projects"]=projects.Select((p,i)=>(p,i)).ToDictionary(x=>"fixture-"+x.i,x=>(object)new{name=x.p.Name,rootPaths=new[]{x.p.Root}})}));

    private async Task<string> SourceSnapshot()
    {
        var rows=new List<string>();
        foreach(var directory in new[]{projectRootA,projectRootB,codexHome})
        foreach(var path in Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var info=new FileInfo(path);var attrs=info.Attributes;var modified=info.LastWriteTimeUtc.Ticks;
            rows.Add(path+"|"+attrs+"|"+modified+"|"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(path))));
        }
        return string.Join('\n',rows);
    }

    private async Task<(int Exit,string Output,string Error)> RunHook(string input)
    {
        using var child=StartChild(Dotnet(),[dll,"--data-root",data,"hook-stop"]);var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();await child.StandardInput.WriteAsync(input);child.StandardInput.Close();await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));return(child.ExitCode,await stdout,await stderr);
    }
    private async Task<(int Exit, JsonElement Json, string Error)> Cli(params string[] args)
    {
        var result = await RunChild(Dotnet(), [dll, "--data-root", data, .. args]);
        return (result.Exit, ParseOne(result.Output), result.Error);
    }

    private static string Dotnet() => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet";
    private static int ErrorCode(JsonElement result) => result.GetProperty("error").GetProperty("code").GetInt32();
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static JsonElement ParseOne(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Check(lines.Length == 1 && Encoding.UTF8.GetByteCount(lines[0]) <= 256 * 1024, "Expected exactly one bounded JSON stdout line: " + output[..Math.Min(200, output.Length)]);
        return JsonDocument.Parse(lines[0]).RootElement.Clone();
    }
    private static JsonElement Structured(JsonElement response, bool expectedError = false)
    {
        var result = response.GetProperty("result");
        Check(result.GetProperty("isError").GetBoolean() == expectedError, "Unexpected tool failure: " + response);
        var structured = result.GetProperty("structuredContent");
        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        Check(JsonElement.DeepEquals(JsonDocument.Parse(text).RootElement, structured), "Text and structured tool results differ.");
        return structured;
    }

    private Process StartChild(string command, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        start.Environment["CODEX_HOME"] = codexHome;
        return Process.Start(start) ?? throw new Exception("Child process did not start.");
    }

    private async Task<(int Exit, string Output, string Error)> RunChild(string command, IEnumerable<string> args)
    {
        using var process = StartChild(command, args);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        catch { process.Kill(true); throw; }
        return (process.ExitCode, await stdout, await stderr);
    }

    private sealed class RpcChild : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task<string> stderr;
        private int nextId;
        public RpcChild(BridgeSuite suite)
        {
            process = suite.StartChild(Dotnet(), [suite.dll, "--data-root", suite.data, "mcp"]);
            process.StandardInput.NewLine = "\n";
            stderr = process.StandardError.ReadToEndAsync();
        }
        public async Task Initialize()
        {
            var response = await Request("initialize", new { protocolVersion = "2025-11-25", capabilities = new { }, clientInfo = new { name = "integration-test", version = "1" } });
            Check(response.GetProperty("result").GetProperty("protocolVersion").GetString() == "2025-11-25", "Initialize failed.");
            await Notify("notifications/initialized", new { });
        }
        public async Task<JsonElement> Request(string method, object parameters)
        {
            var id = ++nextId;
            var result = await Raw(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }));
            Check(result.GetProperty("id").GetInt32() == id, "Response did not match request ID; unexpected notification response.");
            return result;
        }
        public Task<JsonElement> Tool(string name, object arguments) => Request("tools/call", new { name, arguments });
        public async Task Notify(string method, object parameters)
        {
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = parameters }));
            await process.StandardInput.FlushAsync();
        }
        public async Task<JsonElement> Raw(string line)
        {
            await process.StandardInput.WriteLineAsync(line);
            await process.StandardInput.FlushAsync();
            return await Read();
        }
        public async Task InvalidUtf8()
        {
            await process.StandardInput.FlushAsync();
            await process.StandardInput.BaseStream.WriteAsync(new byte[] { 0xc3, 0x28, 0x0a });
            await process.StandardInput.BaseStream.FlushAsync();
        }
        public async Task<JsonElement> Read()
        {
            var line = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Check(line is not null, "MCP stdout closed early. " + (stderr.IsCompleted ? await stderr : ""));
            Check(Encoding.UTF8.GetByteCount(line!) <= 256 * 1024, "Unbounded MCP response.");
            var response = JsonDocument.Parse(line!).RootElement.Clone();
            Check(response.GetProperty("jsonrpc").GetString() == "2.0", "Non-RPC text polluted stdout.");
            return response;
        }
        public async ValueTask DisposeAsync()
        {
            process.StandardInput.Close();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch { process.Kill(true); }
            var diagnostics = await stderr;
            Check(process.ExitCode == 0, "MCP child failed: " + diagnostics);
            process.Dispose();
        }
    }

    private sealed class CandidateHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var suggestion = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["summary.md"] = "UNCONFIRMED_CANDIDATE_729", ["memory.md"] = "UNCONFIRMED_CANDIDATE_729"
            });
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = suggestion } } } }))
            });
        }
    }
}
