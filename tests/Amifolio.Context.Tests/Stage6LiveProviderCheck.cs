using System.Text;
using System.Text.Json;
using Amifolio.Context;
using Amifolio.Core;

internal static class Stage6LiveProviderCheck
{
    // Explicit command only. Never logs provider content, endpoint, model or credentials.
    internal static async Task<int> Run(string configurationRoot, string testRoot)
    {
        configurationRoot = Path.GetFullPath(configurationRoot); testRoot = Path.GetFullPath(testRoot);
        if (testRoot == configurationRoot || testRoot.StartsWith(configurationRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || configurationRoot.StartsWith(testRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Separate synthetic library required.");
        Directory.CreateDirectory(testRoot);
        var report = Path.Combine(testRoot, "report.json");
        var data = Path.Combine(testRoot, "data");
        if (Directory.Exists(data)) throw new InvalidOperationException("Fresh test root required.");
        var sourceRoot = Path.Combine(Path.GetTempPath(), "Amifolio-stage6-live-" + Guid.NewGuid().ToString("N"));
        var checks = new List<string>(); var requests = 0; var phase = "saved-configuration";
        void Check(bool value, string name) { if (!value) throw new InvalidDataException(name); checks.Add(name); }
        try
        {
            var saved = await new ModelConfigurationStore(configurationRoot).LoadAsync();
            Check(!string.IsNullOrWhiteSpace(saved.ApiKey) && !string.IsNullOrWhiteSpace(saved.Model), "saved-configuration-available");
            var project = Directory.CreateDirectory(Path.Combine(sourceRoot, "project")).FullName;
            var home = Directory.CreateDirectory(Path.Combine(sourceRoot, "home")).FullName;
            var source = Path.Combine(project, "synthetic-instructions.md");
            var content = "# 合成验收资料\n\n此文档和项目均为测试夹具，不代表真实用户。\n夹具显式要求：回答简洁，先列依据再列待确认项。界面使用米白与松石绿。\n本次测试目标：只读扫描、显式解析、生成未确认候选，禁止修改原文件。\n不推断人格、身份、经历或未声明的偏好。\n";
            await File.WriteAllTextAsync(source, content, new UTF8Encoding(false));
            var stamp = File.GetLastWriteTimeUtc(source); var attributes = File.GetAttributes(source);
            await File.WriteAllTextAsync(Path.Combine(home, ".codex-global-state.json"), JsonSerializer.Serialize(new Dictionary<string, object> { ["local-projects"] = new Dictionary<string, object> { ["synthetic-project"] = new { name = "Synthetic fixture", rootPaths = new[] { project } } } }));
            var store = new PortfolioStore(data); store.EnableCodexReferenceMode(home);
            await store.RefreshCodexProjectsAsync(); var projectId = store.CodexProjects.Single().Id;
            var reference = await store.ReferenceFileAsync(source, projectId);
            Check(reference.Artifact.Preview.Length == 0, "reference-does-not-auto-parse");
            await store.ParseReferenceAsync(reference.Artifact.Id);
            Check((await store.GetAsync(reference.Artifact.Id))?.Preview.Contains("合成验收资料") == true, "explicit-reference-parse");
            using var handler = new BudgetHandler(new HttpClientHandler { AllowAutoRedirect = false }, () =>
            {
                var budgetPath = Path.Combine(Path.GetDirectoryName(testRoot)!, "live-request-count.txt");
                using var file = new FileStream(budgetPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                using var reader = new StreamReader(file, leaveOpen: true);
                var count = int.TryParse(reader.ReadToEnd(), out var old) ? old : 0;
                if (count >= 2) throw new InvalidOperationException("Synthetic live request budget exhausted.");
                file.Position = 0; file.SetLength(0);
                using var writer = new StreamWriter(file, leaveOpen: true); writer.Write(count + 1); writer.Flush(); file.Flush(true); requests++;
            });
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(65) };
            var service = new ContextService(store, http);
            // Persist only non-secret consent in the isolated library; keep the loaded key in memory.
            await new ModelConfigurationStore(data).SaveAsync(new(saved.Endpoint, saved.Model, true, ""));
            await new TaskStore(data).SetRemoteAllowedAsync(true);
            await service.SetAutomationAsync(projectId, new(true, true, true));
            phase = "automatic-provider";
            Check(await service.RunAutomaticAsync(projectId, new(saved.Endpoint, saved.Model, true), saved.ApiKey), "real-automatic-provider-completed");
            var snapshot = await service.ReadAsync(projectId);
            Check(snapshot.CandidateDocuments.ContainsKey("candidate-summary.md"), "real-summary-candidate");
            Check(!snapshot.Enabled && !Directory.Exists(snapshot.Directory), "generation-does-not-enable-sharing");
            Check(!await service.RunAutomaticAsync(projectId, new(saved.Endpoint, saved.Model, true), saved.ApiKey) && requests == 1, "unchanged-input-not-resent");
            phase = "candidate-lifecycle";
            await service.AdoptCandidateAsync(projectId, "candidate-summary.md");
            var adopted = await service.ReadAsync(projectId);
            Check(adopted.Documents.TryGetValue("summary.md", out var summary) && !string.IsNullOrWhiteSpace(summary), "explicit-adoption");
            await service.SetDocumentEnabledAsync(projectId, "summary.md", false);
            Check((await service.ReadAsync(projectId)).DisabledDocuments.Contains("summary.md"), "document-can-disable");
            await service.SetAutomationAsync(projectId, new());
            Check(!await service.RunAutomaticAsync(projectId, new(saved.Endpoint, saved.Model, true), saved.ApiKey) && requests == 1, "disabled-automation-no-request");
            Check(await File.ReadAllTextAsync(source) == content && File.GetLastWriteTimeUtc(source) == stamp && File.GetAttributes(source) == attributes, "original-bytes-time-attributes-unchanged");
            Check(!Directory.EnumerateFiles(Path.Combine(data, "objects")).Any(), "reference-no-copy");
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { status = "passed", requests, checks, syntheticOnly = true, formalLibraryModified = false, formalConfigurationModified = false }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"PASS stage6 live provider: {requests} request, {checks.Count} checks; synthetic reference only."); return 0;
        }
        catch (Exception error)
        {
            var category = error switch { HttpRequestException h => "http-" + (h.StatusCode is null ? "transport" : ((int)h.StatusCode).ToString()), OperationCanceledException => "timeout-or-cancel", InvalidDataException => "invalid-result", _ => error.GetType().Name };
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new { status = "failed", phase, category, requests, checks, syntheticOnly = true, formalLibraryModified = false, formalConfigurationModified = false }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"FAIL stage6 live provider: {phase}; {category}; {requests} request. No provider content logged."); return 1;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var resolved = Path.GetFullPath(sourceRoot);
            if (resolved.StartsWith(Path.Combine(Path.GetTempPath(), "Amifolio-stage6-live-"), StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved)) Directory.Delete(resolved, true);
        }
    }
    private sealed class BudgetHandler(HttpMessageHandler inner, Action before) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { before(); return base.SendAsync(request, ct); }
    }
}
