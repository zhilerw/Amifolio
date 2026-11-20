using System.Text.Json.Nodes;
using System.Text.Json;
using System.IO.Compression;
using Amifolio.Core;

internal static class Stage6RecoveryCases
{
    internal static (string Name, Func<Fixture, Task> Run)[] All =>
    [
        ("stage6 restore disables automatic network consent and preserves manual controls", async f =>
        {
            await f.Store.InitializeAsync();
            var path = Path.Combine(f.DataRoot, "context-state", "global", "state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await System.IO.File.WriteAllTextAsync(path, """
                {"Enabled":true,"OriginalAccessAllowed":true,"AutomationOptions":{"LearningEnabled":true,"SummaryEnabled":true,"RemoteAllowed":true},"AutomationPolicyRevision":4,"AutomationClaim":"old-claim","Documents":{"memory.md":"manual-stays","summary.md":"derived-summary"},"Manual":["memory.md"],"DisabledDocuments":["style.md"],"Deleted":["overview.md"],"Candidates":{"candidate-summary.md":"unconfirmed"}}
                """);
            var zip = Path.Combine(f.Root, "stage6-recovery.zip");
            await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            var restored = Path.Combine(f.Root, "stage6-restored");
            await new PortfolioMaintenance(restored).RestoreAsync(zip);
            var state = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(Path.Combine(restored, "context-state", "global", "state.json")))!;
            var options = state["AutomationOptions"]!;
            if (options["LearningEnabled"]!.GetValue<bool>() || options["SummaryEnabled"]!.GetValue<bool>() || options["RemoteAllowed"]!.GetValue<bool>()) throw new Exception("restore retained automatic network consent");
            if (state["AutomationClaim"] is not null) throw new Exception("restore retained an in-flight automatic claim");
            if (state["Documents"]!["memory.md"]!.GetValue<string>() != "manual-stays" || !state["DisabledDocuments"]!.AsArray().Any(x => x!.GetValue<string>() == "style.md") || !state["Deleted"]!.AsArray().Any(x => x!.GetValue<string>() == "overview.md")) throw new Exception("restore lost manual content or user controls");
            if (state["Candidates"]!.AsObject().Count != 0 || state["Documents"]!["summary.md"] is not null) throw new Exception("restore revived derived candidates");
        }),
        ("stage6 preference backup preserves text but restore withdraws participation and confirmation", async f =>
        {
            await f.Store.InitializeAsync();
            var path = Path.Combine(f.DataRoot, "preference-state", "state.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await System.IO.File.WriteAllTextAsync(path, """
                {"SchemaVersion":1,"Revision":8,"Participation":{"p":{"Enabled":true,"Revision":3}},"Entries":[{"Id":"pref1","Text":"回答先列依据","Status":"confirmed","Enabled":true,"ConfirmedAt":"2026-11-13T00:00:00Z"},{"Id":"pref2","Text":"已拒绝候选","Status":"rejected","Enabled":false}],"DeletedProposals":["deleted-token"],"NeedsPublication":false}
                """);
            var zip = Path.Combine(f.Root, "stage6-preferences.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(zip);
            using (var archive = ZipFile.OpenRead(zip)) if (archive.GetEntry("preference-state/state.json") is null) throw new Exception("preference ledger omitted from backup");
            var restored = Path.Combine(f.Root, "prefs-restored"); await new PortfolioMaintenance(restored).RestoreAsync(zip);
            var state = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(Path.Combine(restored, "preference-state", "state.json")))!;
            if (state["Participation"]!["p"]!["Enabled"]!.GetValue<bool>() || state["Participation"]!["p"]!["Revision"]!.GetValue<long>() <= 3) throw new Exception("restore reauthorized preference participation");
            var entry = state["Entries"]![0]!;
            if (entry["Text"]!.GetValue<string>() != "回答先列依据" || entry["Status"]!.GetValue<string>() != "invalidated" || entry["Enabled"]!.GetValue<bool>() || entry["ConfirmedAt"] is not null) throw new Exception("restore lost text or revived preference confirmation");
            if (state["Entries"]![1]!["Status"]!.GetValue<string>() != "rejected" || state["DeletedProposals"]![0]!.GetValue<string>() != "deleted-token") throw new Exception("restore lost rejection/tombstone");
        }),
        ("stage6 automatic budget is backed up and restoring an older backup cannot lower current usage", async f =>
        {
            await f.Store.InitializeAsync();
            var path = Path.Combine(f.DataRoot, "context-state", "automation-budget.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await System.IO.File.WriteAllTextAsync(path, "{\"Day\":\"2026-11-13\",\"Requests\":3,\"LastAttempt\":\"2026-11-13T12:00:00Z\"}");
            var archive = Path.Combine(f.Root, "budget.zip"); await new PortfolioMaintenance(f.DataRoot).BackupAsync(archive);
            using (var zip = ZipFile.OpenRead(archive)) if (zip.GetEntry("context-state/automation-budget.json") is null) throw new Exception("automatic request budget omitted from backup");
            var restored = Path.Combine(f.Root, "budget-restored"); await new PortfolioMaintenance(restored).RestoreAsync(archive);
            if (JsonNode.Parse(await System.IO.File.ReadAllTextAsync(Path.Combine(restored, "context-state", "automation-budget.json")))!["Requests"]!.GetValue<int>() != 3) throw new Exception("fresh restore reset request count");
            await System.IO.File.WriteAllTextAsync(path, "{\"Day\":\"2026-11-13\",\"Requests\":7,\"LastAttempt\":\"2026-11-13T13:00:00Z\"}");
            await new PortfolioMaintenance(f.DataRoot).RestoreAsync(archive);
            var budget = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(path))!;
            if (budget["Requests"]!.GetValue<int>() != 7 || budget["LastAttempt"]!.GetValue<DateTimeOffset>() != DateTimeOffset.Parse("2026-11-13T13:00:00Z")) throw new Exception("older backup lowered live request usage or cooldown");
        }),
        ("stage6 external edit of adopted export stays private after source revocation", async f =>
        {
            foreach (var accidentalManual in new[] { false, true })
            {
                var (store, project, source) = await ParsedReferenceAsync(f, "adopted-" + accidentalManual);
                var scope = RevocationJournal.Scope(project);
                var (path, export, editedPath) = await PublishedStateAsync(f, scope, "summary.md", adopted: true, accidentalManual);
                const string edited = "<!-- amifolio:adopted; source-derived -->\nPRIVATE_DERIVED_EXTERNAL_EDIT\n人工修改。";
                await System.IO.File.WriteAllTextAsync(editedPath, edited);
                await System.IO.File.WriteAllTextAsync(source, "changed reference source with a different size");
                await store.RefreshSourceStatesAsync();
                var state = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(path))!;
                if (state["Documents"]!["summary.md"] is not null || state["Manual"]!.AsArray().Any(x => x!.GetValue<string>() == "summary.md")) throw new Exception("revocation promoted externally edited adopted content to manual");
                if (state["ExternalEdits"]?["summary.md"]?.GetValue<string>() != edited || state["ExternalEdits"]?["style.md"]?.GetValue<string>() != "prior private edit") throw new Exception("revocation lost private external edits");
                if (state["Documents"]!["memory.md"]!.GetValue<string>() != "independent manual memory" || Directory.Exists(export)) throw new Exception("revocation lost manual memory or retained a derived export");
                await store.ListAsync();
                await new TaskStore(f.DataRoot).ListAsync();
                if (System.IO.File.Exists(Path.Combine(f.DataRoot, ".revocations.pending"))) throw new Exception("revocation journal did not finish");
                var archive = Path.Combine(f.Root, "external-adopted-" + accidentalManual + ".zip");
                await new PortfolioMaintenance(f.DataRoot).BackupAsync(archive);
                var restored = Path.Combine(f.Root, "external-adopted-restored-" + accidentalManual);
                await new PortfolioMaintenance(restored).RestoreAsync(archive);
                var recovered = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(Path.Combine(restored, "context-state", scope, "state.json")))!;
                if (recovered["ExternalEdits"]?["summary.md"]?.GetValue<string>() != edited || recovered["Documents"]!["summary.md"] is not null || Directory.Exists(Path.Combine(restored, "context", scope))) throw new Exception("restore lost private recovery text or revived derived content");
            }
        }),
        ("stage6 external edit of preferences cannot block library revocation", async f =>
        {
            var (store, _, source) = await ParsedReferenceAsync(f, "preferences");
            var ledger = Path.Combine(f.DataRoot, "preference-state", "state.json");
            Directory.CreateDirectory(Path.GetDirectoryName(ledger)!);
            await System.IO.File.WriteAllTextAsync(ledger, "{\"SchemaVersion\":1}");
            var (path, export, editedPath) = await PublishedStateAsync(f, "global", "preferences.md", adopted: false);
            const string edited = "PRIVATE_PREFERENCE_EXTERNAL_EDIT\n保留人工修改，不视为已确认偏好。";
            await System.IO.File.WriteAllTextAsync(editedPath, edited);
            await System.IO.File.WriteAllTextAsync(source, "changed reference source with a different size");
            await store.RefreshSourceStatesAsync();
            // Both read and write leases must remain usable after replay, including a cold store.
            await new PortfolioStore(f.DataRoot).ListAsync();
            await new TaskStore(f.DataRoot).SetRemoteAllowedAsync(false);
            var state = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(path))!;
            if (state["ExternalEdits"]?["preferences.md"]?.GetValue<string>() != edited || state["ExternalEdits"]?["style.md"]?.GetValue<string>() != "prior private edit") throw new Exception("preference withdrawal lost private edited bytes");
            if (state["Documents"]!["preferences.md"] is not null || state["Manual"]!.AsArray().Any(x => x!.GetValue<string>() == "preferences.md") || Directory.Exists(export)) throw new Exception("external preference text remained shared or manual");
            if (state["Documents"]!["memory.md"]!.GetValue<string>() != "independent manual memory" || System.IO.File.Exists(Path.Combine(f.DataRoot, ".revocations.pending"))) throw new Exception("journal remained pending or lost independent manual memory");
        }),
        ("stage6 reference change immediately withdraws derived global preferences", async f =>
        {
            var projectRoot = Directory.CreateDirectory(Path.Combine(f.ReferenceRoot, "project")).FullName;
            var home = Directory.CreateDirectory(Path.Combine(f.ReferenceRoot, "codex")).FullName;
            var source = Path.Combine(projectRoot, "notes.md"); await System.IO.File.WriteAllTextAsync(source, "initial reference");
            await System.IO.File.WriteAllTextAsync(Path.Combine(home, ".codex-global-state.json"), JsonSerializer.Serialize(new Dictionary<string, object> { ["local-projects"] = new Dictionary<string, object> { ["p"] = new { name = "Synthetic project", rootPaths = new[] { projectRoot } } } }));
            var store = new PortfolioStore(f.DataRoot); store.EnableCodexReferenceMode(home); await store.RefreshCodexProjectsAsync();
            var reference = await store.ReferenceFileAsync(source, store.CodexProjects.Single().Id); await store.ParseReferenceAsync(reference.Artifact.Id);
            var preferences = Path.Combine(f.DataRoot, "preference-state", "state.json"); Directory.CreateDirectory(Path.GetDirectoryName(preferences)!); await System.IO.File.WriteAllTextAsync(preferences, "{\"SchemaVersion\":1}");
            var global = Path.Combine(f.DataRoot, "context-state", "global", "state.json"); Directory.CreateDirectory(Path.GetDirectoryName(global)!);
            await System.IO.File.WriteAllTextAsync(global, "{\"Enabled\":true,\"Documents\":{\"preferences.md\":\"stale preference\",\"memory.md\":\"manual keep\"},\"Manual\":[\"memory.md\"]}");
            var export = Directory.CreateDirectory(Path.Combine(f.DataRoot, "context", "global")).FullName; await System.IO.File.WriteAllTextAsync(Path.Combine(export, "START.md"), "stale public pointer");
            await System.IO.File.WriteAllTextAsync(source, "changed reference source"); await store.RefreshSourceStatesAsync();
            if (Directory.Exists(export)) throw new Exception("global preferences export survived source revocation");
            var state = JsonNode.Parse(await System.IO.File.ReadAllTextAsync(global))!;
            if (state["Documents"]!["preferences.md"] is not null || state["Documents"]!["memory.md"]!.GetValue<string>() != "manual keep") throw new Exception("source revocation retained projection or lost manual memory");
        })
    ];

    private static async Task<(PortfolioStore Store, string Project, string Source)> ParsedReferenceAsync(Fixture f, string name)
    {
        var projectRoot = Directory.CreateDirectory(Path.Combine(f.ReferenceRoot, name, "project")).FullName;
        var home = Directory.CreateDirectory(Path.Combine(f.ReferenceRoot, name, "codex")).FullName;
        var source = Path.Combine(projectRoot, "notes.md");
        await System.IO.File.WriteAllTextAsync(source, "initial parsed reference");
        await System.IO.File.WriteAllTextAsync(Path.Combine(home, ".codex-global-state.json"), JsonSerializer.Serialize(new Dictionary<string, object> { ["local-projects"] = new Dictionary<string, object> { ["p"] = new { name = "Synthetic project", rootPaths = new[] { projectRoot } } } }));
        var store = new PortfolioStore(f.DataRoot); store.EnableCodexReferenceMode(home);
        await store.RefreshCodexProjectsAsync();
        var project = store.CodexProjects.Single().Id;
        var reference = await store.ReferenceFileAsync(source, project);
        await store.ParseReferenceAsync(reference.Artifact.Id);
        return (store, project, source);
    }

    private static async Task<(string State, string Export, string Document)> PublishedStateAsync(Fixture f, string scope, string name, bool adopted, bool accidentalManual = false)
    {
        var generation = Guid.NewGuid().ToString("N");
        var export = Path.Combine(f.DataRoot, "context", scope);
        var directory = Directory.CreateDirectory(Path.Combine(export, "generations", generation)).FullName;
        const string original = "original managed document";
        const string pointer = "managed START pointer";
        await System.IO.File.WriteAllTextAsync(Path.Combine(export, "START.md"), pointer);
        await System.IO.File.WriteAllTextAsync(Path.Combine(directory, name), original);
        var manual = new JsonArray("memory.md"); if (accidentalManual) manual.Add(name);
        var state = new JsonObject
        {
            ["Enabled"] = true, ["Generation"] = generation, ["StartHash"] = Hash(pointer),
            ["Documents"] = new JsonObject { [name] = original, ["memory.md"] = "independent manual memory" },
            ["PublishedHashes"] = new JsonObject { [name] = Hash(original) }, ["Manual"] = manual,
            ["AdoptedDocuments"] = adopted ? new JsonObject { [name] = Hash("source input") } : new JsonObject(),
            ["ExternalEdits"] = new JsonObject { ["style.md"] = "prior private edit" }
        };
        var path = Path.Combine(f.DataRoot, "context-state", scope, "state.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await System.IO.File.WriteAllTextAsync(path, state.ToJsonString());
        return (path, export, Path.Combine(directory, name));
    }

    private static string Hash(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)));
}
