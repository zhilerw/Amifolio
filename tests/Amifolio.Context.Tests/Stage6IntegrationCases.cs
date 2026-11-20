using Amifolio.Context;
using Amifolio.Core;
using System.Text.Json;

internal static class Stage6IntegrationCases
{
    internal static (string Name, Func<Task> Run)[] All =>
    [
        ("stage6 full preference and automation state stays withdrawn after library restore", async () =>
        {
            var root = Path.Combine(Path.GetTempPath(), "Amifolio-stage6-recovery-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var project = Directory.CreateDirectory(Path.Combine(root, "project")).FullName;
                var home = Directory.CreateDirectory(Path.Combine(root, "codex")).FullName;
                var source = Path.Combine(project, "notes.md");
                await File.WriteAllTextAsync(source, "用户明确偏好：回答时请简洁，先说明依据。此处为合成验收内容。");
                await File.WriteAllTextAsync(Path.Combine(home, ".codex-global-state.json"), JsonSerializer.Serialize(new Dictionary<string, object> { ["local-projects"] = new Dictionary<string, object> { ["p"] = new { name = "Synthetic isolated source", rootPaths = new[] { project } } } }));
                var data = Path.Combine(root, "data"); var store = new PortfolioStore(data); store.EnableCodexReferenceMode(home); await store.RefreshCodexProjectsAsync();
                var id = store.CodexProjects.Single().Id; var reference = await store.ReferenceFileAsync(source, id); await store.ParseReferenceAsync(reference.Artifact.Id);
                var service = new ContextService(store);
                await service.SaveDocumentAsync(null, "memory.md", "manual global memory");
                await service.SetPreferenceParticipationAsync(id, true);
                var entry = await service.ProposePreferenceAsync(id, reference.Artifact.Id, "回答时请简洁表述。");
                await service.ConfirmPreferenceAsync(entry.Id, entry.Text); await service.SetEnabledAsync(null, true);
                if (!(await service.ReadAsync()).Documents.ContainsKey("preferences.md")) throw new Exception("confirmed preference not in managed global projection");
                await service.SetAutomationAsync(id, new(true, true, true));
                var archive = Path.Combine(root, "backup.zip"); await new PortfolioMaintenance(data).BackupAsync(archive);
                var restored = Path.Combine(root, "restored"); await new PortfolioMaintenance(restored).RestoreAsync(archive);
                var restoredStore = new PortfolioStore(restored); restoredStore.EnableCodexReferenceMode(home);
                var restoredService = new ContextService(restoredStore);
                var restoredEntry = (await restoredService.ReadPreferencesAsync()).Entries.Single();
                if (restoredEntry.Text != entry.Text || restoredEntry.Status != "invalidated" || restoredEntry.Enabled || restoredEntry.ConfirmedAt is not null) throw new Exception("preference confirmation revived or private review text lost");
                if (await restoredService.GetPreferenceParticipationAsync(id)) throw new Exception("restored project auto-authorized");
                if ((await restoredService.ReadAutomationAsync(id)).Options != new ContextAutomationOptions()) throw new Exception("restored automatic send enabled");
                var snapshot = await restoredService.ReadAsync();
                if (snapshot.Enabled || Directory.Exists(snapshot.Directory) || snapshot.Documents.ContainsKey("preferences.md") || snapshot.Documents["memory.md"] != "manual global memory") throw new Exception("restore leaked preference or lost manual document");
                await restoredStore.ParseReferenceAsync(reference.Artifact.Id);
                await restoredService.SetPreferenceParticipationAsync(id, true); await restoredService.SetEnabledAsync(null, true);
                if ((await restoredService.ReadAsync()).Documents.ContainsKey("preferences.md")) throw new Exception("reparse and opt-in silently reconfirmed old preference");
            }
            finally
            {
                Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
                var resolved = Path.GetFullPath(root);
                if (resolved.StartsWith(Path.Combine(Path.GetTempPath(), "Amifolio-stage6-recovery-"), StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, true);
            }
        })
    ];
}
