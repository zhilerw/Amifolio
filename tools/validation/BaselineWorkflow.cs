using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amifolio.Core;
using Amifolio.Context;

// Run through Test-BaselineWorkflow.ps1 against a supplied, freshly built package.
// All source material, host instructions and data stay inside a unique disposable run.
var package = Path.GetFullPath(args[0]);
var run = Path.GetFullPath(args[1]);
var phase = args.Length > 2 ? args[2] : "prepare";
Directory.CreateDirectory(run);
var data = Path.Combine(run, "data 中文 with spaces");
var input = Path.Combine(run, "sample project 中文");
Directory.CreateDirectory(input);
var bridge = Path.Combine(package, "bridge", "Amifolio.Bridge.exe");
var checks = new List<string>();
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks.Add(message); }
var store = new PortfolioStore(data);
await store.InitializeAsync();
var context = new ContextService(store);
async Task<JsonElement> Cli(params string[] words)
{
    var start = new ProcessStartInfo(bridge) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
    foreach (var word in new[] { "--data-root", data }.Concat(words)) start.ArgumentList.Add(word);
    using var child = Process.Start(start) ?? throw new Exception("Bridge did not start");
    var stdout = child.StandardOutput.ReadToEndAsync();
    var stderr = child.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try { await child.WaitForExitAsync(timeout.Token); }
    catch { if (!child.HasExited) child.Kill(true); throw; }
    var output = await stdout; var error = await stderr;
    Check(child.ExitCode == 0, "bridge " + words[0] + " exits successfully: " + error);
    using var json = JsonDocument.Parse(output);
    return json.RootElement.Clone();
}
if (phase == "prepare")
{
    var project = await store.CreateProjectAsync("阶段一验证项目", input);
    var note = Path.Combine(input, "交付说明.md");
    const string original = "# 交付说明\n海盐检索词：正文可检索。\n作品集只用于本轮验证。\n";
    await File.WriteAllTextAsync(note, original, new UTF8Encoding(false));
    var saved = await store.ImportFileAsync(note, project.Id, "用户", "阶段一,文档");
    var id = saved.Artifact.Id;
    Check(File.Exists(saved.Artifact.ObjectPath) && saved.Artifact.ObjectPath != note, "user import owns an independent copy");
    Check((await store.ListAsync("海盐检索词", project.Id)).Count == 1, "Chinese body search finds imported document");
    await store.UpdateArtifactAsync(id, "已整理的说明", "已确认,阶段一", project.Id);
    Check((await store.GetAsync(id))!.Title == "已整理的说明", "metadata edit persists");
    var duplicate = await store.ImportFileAsync(note, project.Id, "用户", "阶段一,文档");
    Check(duplicate.AlreadyExists && duplicate.Artifact.Id == id, "duplicate import returns original receipt");
    await File.WriteAllTextAsync(note, original + "新增一个版本。\n", new UTF8Encoding(false));
    var newer = await store.ImportFileAsync(note, project.Id, "用户");
    Check(newer.Artifact.Version == 2 && await File.ReadAllTextAsync(saved.Artifact.ObjectPath) == original, "new version retains original archived bytes");
    var generated = await store.SaveGeneratedAsync("应用生成的说明.md", "内部生成唯一词", project.Id);
    Check(generated.Artifact.Source == "应用", "application generated file has reserved provenance");
    var agentFile = Path.Combine(input, "Agent 最终报告.md");
    await File.WriteAllTextAsync(agentFile, "# Agent 报告\n桥接持久化唯一词。\n");
    var receipt = await Cli("submit", agentFile, "--project", project.Id, "--source", "Codex", "--tags", "交付");
    var agentId = receipt.GetProperty("id").GetString()!;
    Check(receipt.GetProperty("status").GetString() == "archived" && File.Exists(receipt.GetProperty("path").GetString()), "agent receives durable receipt");
    var status = await Cli("status", agentId);
    Check(status.GetProperty("status").GetString() == "archived", "fresh bridge process sees archive");
    Check((await store.GetAsync(agentId))!.ProjectId == project.Id, "agent submission uses correct project");
    await context.SaveDocumentAsync(project.Id, "memory.md", "# 项目记忆\n人工确认：交付前先核对来源。\n");
    await context.SetEnabledAsync(project.Id, true);
    await context.PublishAsync(project.Id);
    var snapshot = await context.ReadAsync(project.Id);
    Check(snapshot.Enabled && snapshot.Documents["memory.md"].Contains("人工确认"), "manual memory is published");
    Check(snapshot.Documents["sources.md"].Contains(id) && !snapshot.Documents["sources.md"].Contains(generated.Artifact.Id)
        && !snapshot.Documents["overview.md"].Contains("应用生成的说明"), "source index includes user artifact and excludes application generated artifact");
    var global = await context.ReadAsync();
    Check(!global.Enabled && !global.Documents["sources.md"].Contains(id) && !global.Documents["overview.md"].Contains("已整理的说明"), "global remains disabled and excludes project artifact identity and title");
    var agentsPath = Path.Combine(input, "AGENTS.md");
    await File.WriteAllTextAsync(agentsPath, "# 项目规则\r\n保留已有规则。\r\n");
    var agents = new AgentsFileService();
    var before = await agents.ReadAsync(agentsPath);
    var proposal = agents.PreviewManagedBlock(before, snapshot.Directory, true);
    var backup = await agents.SaveAsync(before, proposal);
    Check(backup is not null && File.Exists(backup) && await File.ReadAllTextAsync(backup) == before.Content, "AGENTS save makes a restorable backup");
    Check(proposal.Contains("保留已有规则") && proposal.Contains("先读 START.md"), "managed entry preserves user rules and progressive reading");
    Check(File.Exists(Path.Combine(snapshot.Directory, "START.md")), "published entry is a real file");
    var memory = await Cli("context", "--project", project.Id);
    Check(memory.GetRawText().Contains("overview"), "fresh bridge reads published project context");
    await File.WriteAllTextAsync(Path.Combine(run, "scenario.json"), JsonSerializer.Serialize(new { projectId = project.Id, artifactId = id, agentId, generatedId = generated.Artifact.Id, agentsPath, data, archiveHash = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(saved.Artifact.ObjectPath))) }, new JsonSerializerOptions { WriteIndented = true }));
}
else if (phase == "verify")
{
    using var scenario = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(run, "scenario.json")));
    var saved = scenario.RootElement;
    var projectId = saved.GetProperty("projectId").GetString()!;
    var item = await store.GetAsync(saved.GetProperty("artifactId").GetString()!);
    Check(item is not null && item.Title == "已整理的说明" && item.Tags.Contains("已确认"), "fresh workflow process retains edited metadata");
    Check(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(item!.ObjectPath))) == saved.GetProperty("archiveHash").GetString(), "fresh process verifies original bytes");
    var all = await store.ListAsync(projectId: projectId);
    Check(all.Count == 4, "fresh process sees both versions and all three sources");
    var snapshot = await context.ReadAsync(projectId);
    Check(snapshot.Enabled && snapshot.Documents["memory.md"].Contains("人工确认"), "fresh process retains sharing setting and manual memory");
    var read = await Cli("context", "--project", projectId);
    Check(read.GetRawText().Contains("overview"), "separate bridge can reopen context after UI use");
    var listing = await Cli("list", "--project", projectId, "--search", "桥接持久化唯一词");
    Check(listing.GetProperty("artifacts").GetArrayLength() == 1, "agent artifact is searchable after reopen");
    await context.SetEnabledAsync(projectId, false);
    var disabled = await Cli("context", "--project", projectId);
    Check(!disabled.GetProperty("enabled").GetBoolean(), "sharing withdrawal is visible to a new bridge process");
    await context.SetEnabledAsync(projectId, true);
}
else throw new ArgumentException("Expected prepare or verify");
await File.WriteAllTextAsync(Path.Combine(run, phase + "-report.json"), JsonSerializer.Serialize(new { phase, checks, dataRoot = data, limitation = "Local API/CLI journey; not proof of real Codex host behavior or physical input." }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"PASS baseline workflow {phase}: {checks.Count} checks; {run}");
