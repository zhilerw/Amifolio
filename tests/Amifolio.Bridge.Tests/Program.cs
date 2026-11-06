using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Amifolio.Context;
using Amifolio.Core;

var suite = new BridgeSuite();
return await suite.RunAsync();

internal sealed class BridgeSuite
{
    private readonly string temporary = Path.Combine(Path.GetTempPath(), "amifolio-bridge-tests-" + Guid.NewGuid().ToString("N"));
    private string root = null!, dll = null!, data = null!, source = null!, projectA = null!, projectB = null!, id = null!;
    private int passed, failed;
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
            data = Path.Combine(temporary, "data 中文 with spaces");
            Directory.CreateDirectory(temporary);
            source = Path.Combine(temporary, "final 中文 report.md");
            await File.WriteAllTextAsync(source, PrivateContents, new UTF8Encoding(false));
            var store = new PortfolioStore(data);
            await store.InitializeAsync();
            Directory.CreateDirectory(Path.Combine(temporary, "project-a"));
            Directory.CreateDirectory(Path.Combine(temporary, "project-b"));
            projectA = (await store.CreateProjectAsync("Project A", Path.Combine(temporary, "project-a"))).Id;
            projectB = (await store.CreateProjectAsync("Project B", Path.Combine(temporary, "project-b"))).Id;

            await Test("CLI submit acknowledges only a committed copy", async () =>
            {
                var result = await Cli("submit", source, "--project", projectA, "--source", "Codex", "--tags", "final,report");
                Check(result.Exit == 0, result.Error);
                Check(result.Json.EnumerateObject().Count() == 3, "Submit receipt exposes unexpected fields.");
                id = result.Json.GetProperty("id").GetString()!;
                var archived = result.Json.GetProperty("path").GetString()!;
                Check(archived != source && File.Exists(archived), "Archive path is not a durable copy.");
                Check(await File.ReadAllTextAsync(archived) == PrivateContents, "Archive copy differs.");
                Check(await File.ReadAllTextAsync(source) == PrivateContents, "Source changed.");
                Check(result.Json.GetProperty("status").GetString() == "archived", "Archive status missing.");
                Check((await store.GetAsync(id)) is not null, "Receipt was returned without a committed record.");
            });
            await Test("CLI fresh process verifies status and project metadata", async () =>
            {
                var status = await Cli("status", id);
                Check(status.Exit == 0 && status.Json.GetProperty("status").GetString() == "archived", status.Error);
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
            await Test("CLI repo snapshots files without requiring raw-read MCP", async () =>
            {
                var repo = Path.Combine(temporary, "repo");
                Directory.CreateDirectory(repo);
                await File.WriteAllTextAsync(Path.Combine(repo, "README.md"), "repository deliverable");
                var result = await Cli("repo", repo, "--project", projectB);
                Check(result.Exit == 0 && File.Exists(result.Json.GetProperty("path").GetString()), result.Error);
                Check(result.Json.GetProperty("status").GetString() == "archived", "Repo archive not durable.");
            });
            await Test("CLI global options work after the command and Agent is the default source", async () =>
            {
                var file = Path.Combine(temporary, "default.txt");
                await File.WriteAllTextAsync(file, "default source");
                var result = await Cli("submit", file);
                Check(result.Exit == 0, result.Error);
                Check((await store.GetAsync(result.Json.GetProperty("id").GetString()!))!.Source == "Agent", "Wrong default source.");
                var process = await RunChild(Dotnet(), [dll, "projects", "--data-root", data]);
                Check(process.Exit == 0 && ParseOne(process.Output).GetProperty("projects").GetArrayLength() == 2, process.Error);
            });

            await Test("MCP lifecycle, five tools, status, search, projects and receipts", async () =>
            {
                await using var rpc = new RpcChild(Dotnet(), dll, data);
                var early = await rpc.Request("tools/list", new { });
                Check(ErrorCode(early) == -32002, "Pre-initialize tools were accepted.");
                Check((await rpc.Request("ping", new { })).TryGetProperty("result", out _), "Early ping failed.");
                await rpc.Initialize();
                var tools = (await rpc.Request("tools/list", new { })).GetProperty("result").GetProperty("tools");
                var names = tools.EnumerateArray().Select(t => t.GetProperty("name").GetString()).Order().ToArray();
                Check(names.SequenceEqual(new[] { "artifact_search", "artifact_status", "artifact_submit", "context_read", "projects_list" }), "Unexpected tool surface.");
                var status = Structured(await rpc.Tool("artifact_status", new { id }));
                Check(status.GetProperty("status").GetString() == "archived", "MCP status failed.");
                var search = Structured(await rpc.Tool("artifact_search", new { search = "report", projectId = projectA, limit = 1 }));
                Check(search.GetProperty("artifacts").GetArrayLength() == 1, "Scoped search failed.");
                var metadata = search.GetProperty("artifacts")[0];
                Check(!metadata.TryGetProperty("preview", out _) && !metadata.TryGetProperty("objectPath", out _) && !metadata.TryGetProperty("hash", out _), "Raw/archive fields exposed.");
                Check(!search.GetRawText().Contains(PrivateContents), "Raw content leaked.");
                var allMetadata = Structured(await rpc.Tool("artifact_search", new { }));
                Check(allMetadata.GetProperty("artifacts").EnumerateArray().Any(item => item.GetProperty("projectId").GetString() == projectB), "Unfiltered local portfolio metadata contract changed.");
                Check(search.GetProperty("artifacts").EnumerateArray().All(item => item.GetProperty("projectId").GetString() == projectA), "Project metadata filter leaked another project.");
                Check(Structured(await rpc.Tool("projects_list", new { })).GetProperty("projects").GetArrayLength() == 2, "Projects tool failed.");
                var final = Path.Combine(temporary, "rpc-deliverable.md");
                await File.WriteAllTextAsync(final, "MCP user deliverable");
                var receipt = Structured(await rpc.Tool("artifact_submit", new { path = final, projectId = projectA, source = "Codex", tags = "final" }));
                Check(receipt.EnumerateObject().Count() == 3 && File.Exists(receipt.GetProperty("path").GetString()), "Invalid MCP receipt.");
                Check(Structured(await rpc.Tool("artifact_status", new { id = receipt.GetProperty("id").GetString() })).GetProperty("status").GetString() == "archived", "Receipt status failed.");
            });

            await Test("MCP parse and validation errors recover without extra stdout", async () =>
            {
                await using var rpc = new RpcChild(Dotnet(), dll, data);
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
                var file = Path.Combine(temporary, "notification-must-not-submit.md");
                await File.WriteAllTextAsync(file, "not authorized as a request");
                await using var rpc = new RpcChild(Dotnet(), dll, data);
                await rpc.Initialize();
                await rpc.Notify("notifications/progress", new { progress = 1 });
                await rpc.Notify("tools/call", new { name = "artifact_submit", arguments = new { path = file } });
                var ping = await rpc.Request("ping", new { });
                Check(ping.GetProperty("result").EnumerateObject().Count() == 0, "A notification received a response.");
                Check(!(await store.ListAsync()).Any(a => a.OriginalName == Path.GetFileName(file)), "Notification invoked a mutation.");
            });

            await Test("MCP execution failures are tool errors and do not acknowledge storage", async () =>
            {
                await using var rpc = new RpcChild(Dotnet(), dll, data);
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
                await using var rpc = new RpcChild(Dotnet(), dll, data);
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

            await Test("Search results are bounded, marked truncated and metadata only", async () =>
            {
                for (var index = 0; index < 53; index++)
                {
                    var file = Path.Combine(temporary, $"bounded-{index:D3}.txt");
                    await File.WriteAllTextAsync(file, PrivateContents + index);
                    await store.ImportFileAsync(file, projectB, "Codex", new string('t', 900));
                }
                await using var rpc = new RpcChild(Dotnet(), dll, data);
                await rpc.Initialize();
                var result = Structured(await rpc.Tool("artifact_search", new { search = "bounded-", projectId = projectB }));
                Check(result.GetProperty("artifacts").GetArrayLength() == 50 && result.GetProperty("truncated").GetBoolean(), "Unbounded metadata search.");
                Check(!result.GetRawText().Contains(PrivateContents), "Search leaked raw contents.");
                Check(result.GetProperty("artifacts")[0].GetProperty("tags").GetString()!.Length <= 512, "Metadata field unbounded.");
                var limited = Structured(await rpc.Tool("artifact_search", new { search = "bounded-", projectId = projectB, limit = 2 }));
                Check(limited.GetProperty("artifacts").GetArrayLength() == 2 && limited.GetProperty("truncated").GetBoolean(), "Requested limit ignored.");
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
        try { await test().WaitAsync(TimeSpan.FromSeconds(90)); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception e) { failed++; Console.Error.WriteLine("FAIL " + name + ": " + e); }
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

    private static Process StartChild(string command, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo(command) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        start.Environment["DOTNET_NOLOGO"] = "1";
        start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        return Process.Start(start) ?? throw new Exception("Child process did not start.");
    }

    private static async Task<(int Exit, string Output, string Error)> RunChild(string command, IEnumerable<string> args)
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
        public RpcChild(string command, string dll, string data)
        {
            process = StartChild(command, [dll, "--data-root", data, "mcp"]);
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
