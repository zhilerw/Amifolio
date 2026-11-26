using System.Text.Encodings.Web;
using System.Text.Json;
using Amifolio.Core;

static class CodexPluginCases
{
    internal static async Task<int> Run(string action, string home, string data, string studio)
    {
        var store = new PortfolioStore(data);
        store.EnableCodexReferenceMode(home);
        await store.InitializeAsync();
        await store.RefreshCodexProjectsAsync();
        var project = store.CodexProjects.Single(p => Path.TrimEndingDirectorySeparator(p.RootPath)
            .Equals(Path.TrimEndingDirectorySeparator(studio), StringComparison.OrdinalIgnoreCase));
        var learning = new LearningStore(data);
        var settings = await learning.CreativeSettingsAsync(project.Id);
        switch (action)
        {
            case "configure":
                var reference = await store.ReferenceFileAsync(Path.Combine(studio, "approved-cover.md"), project.Id);
                await learning.SaveCreativeAsync(project.Id,
                    new("Cormorant Garamond", "#24766d", "flat editorial", "casual",
                        reference.Artifact.Id, reference.Artifact.Title, "typography"), true, settings.Revision);
                await learning.SetPolicyAsync(project.Id, new() { Capture = true, Learn = true, AutoActivate = true, Share = true });
                await store.SetWorkSyncAsync(project.Id, true);
                break;
            case "revise":
                await learning.SaveCreativeAsync(project.Id, settings.Preference!.Style with { Color = "#C03754" }, true, settings.Revision);
                break;
            case "disable":
                await learning.SetEntryStateAsync(settings.Preference!.Id, "disabled", settings.Revision);
                await store.SetWorkSyncAsync(project.Id, false);
                break;
            case "delete":
                await learning.SetEntryStateAsync(settings.Preference!.Id, "deleted", settings.Revision);
                break;
            case "undo":
                await learning.UndoEntryAsync(settings.Preference!.Id, settings.Revision);
                break;
            case "backup":
                await new PortfolioMaintenance(data).BackupAsync(data + ".zip");
                break;
            case "restore":
                await new PortfolioMaintenance(data).RestoreAsync(data + ".zip");
                break;
            case "query":
                break;
            default:
                throw new ArgumentException("Unsupported isolated fixture action.");
        }
        var snapshot = await learning.SnapshotAsync(project.Id);
        var works = await store.ListCodexWorksAsync(project.Id);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            project.Id,
            Sync = await store.WorkSyncAsync(project.Id),
            Creative = await learning.CreativeAsync(project.Id),
            Works = works,
            Search = await store.ListCodexWorksAsync(project.Id, "cover"),
            snapshot.Summaries,
            snapshot.Entries,
            Evidence = await learning.EvidenceAsync(snapshot.Entries.SelectMany(e => e.EvidenceIds))
        }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        return 0;
    }
}
