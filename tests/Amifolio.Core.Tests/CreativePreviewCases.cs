using Amifolio.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

internal static class CreativePreviewCases
{
    internal static (string, Func<Fixture, Task>)[] All =>
    [
        ("creative preview round-trips through reload and preserves native reference", RoundTrip),
        ("creative preview provision stays in scope and follows undo", ProvisionAfterUndo),
        ("creative next delivery previews exact saved payload without claiming it", PreviewNextDelivery),
        ("creative preview can undo creation and recreation", UndoCreation),
        ("creative preview clears the last project override to inherit and can undo", ClearToInherit),
        ("creative example unlink preserves authored values and can undo a reference-only profile", UnlinkReference),
        ("creative preview reads historical settings without fabricating undo", HistoricalSettings),
        ("creative preview validates new fields before advancing revision", Validate)
    ];

    private static async Task UnlinkReference(Fixture fixture)
    {
        var store = new LearningStore(fixture.DataRoot);
        var reference = "ref-" + new string('a', 64);
        var favorite = new CreativeStyle("", "", "", "", reference, "山间来信", "留白")
        { ReferenceWorkId = new string('b', 64) };
        var referenceOnly = await store.SaveCreativeAsync("project-a", favorite, false, 0);
        var authored = await store.SaveCreativeAsync("project-b", favorite with { Color = "#24766D" }, true, 0);
        await ExpectRevisionConflict(() => store.RemoveCreativeReferenceAsync("project-a", referenceOnly.Revision - 1));
        var cleared = await store.RemoveCreativeReferenceAsync("project-a", referenceOnly.Revision);
        Check(await store.CreativeAsync("project-a") is null && await store.HasCreativeHistoryAsync("project-a"),
            "unlinking a reference-only profile did not leave an undoable removal");
        Check(await store.CreativeAsync("project-b") == authored, "unlink changed the other project");
        var restored = await store.UndoCreativeRemovalAsync("project-a", cleared);
        Check(restored.Style == favorite && restored.State == "disabled", "undo lost the prior state or association");
        await store.RemoveCreativeReferenceAsync("project-b", authored.Revision);
        Check(await store.CreativeAsync("project-b") is { Style.Color: "#24766D", Style.ReferenceId: null, Style.ReferenceWorkId: null, State: "active" },
            "unlink deleted a saved color or paused the profile");
    }

    private static async Task RoundTrip(Fixture fixture)
    {
        var store = new LearningStore(fixture.DataRoot);
        var saved = await store.SaveCreativeAsync("project-a",
            new(" 华文宋体 ", " #24766d ", "", "", "ref-" + new string('a', 64), "作品", "排版")
            {
                Background = " #fcfaf5 ",
                Spacing = 0,
                Radius = 48,
                SampleText = " 山间来信。\r\n留点时间，慢慢读。 "
            }, true, 0);
        var reopened = await new LearningStore(fixture.DataRoot).CreativeAsync("project-a");
        Check(reopened is not null && reopened == saved && reopened.Style == new CreativeStyle("华文宋体", "#24766D", "", "",
            "ref-" + new string('a', 64), "作品", "排版")
        {
            Background = "#FCFAF5",
            Spacing = 0,
            Radius = 48,
            SampleText = "山间来信。\n留点时间，慢慢读。"
        }, "reopen did not preserve normalized style and reference");
    }

    private static async Task ProvisionAfterUndo(Fixture fixture)
    {
        var store = new LearningStore(fixture.DataRoot);
        var before = await store.SaveCreativeAsync("project-a", new("", "", "", "")
        {
            Background = "#FCFAF5", Spacing = 14, Radius = 4, SampleText = "山间来信。"
        }, true, 0);
        var edited = await store.SaveCreativeAsync("project-a", before.Style with
        {
            Background = "#CBD7F1", Spacing = 36, Radius = 12, SampleText = "海边来信。"
        }, true, before.Revision);

        var session = Guid.NewGuid().ToString("D");
        Check(await store.ProvideCreativeAsync("project-b", session, "one", "设计封面") is null,
            "project B received project A style");
        var provided = await store.ProvideCreativeAsync("project-a", session, "one", "设计封面");
        Check(provided is not null && provided.Contains("#CBD7F1") && provided.Contains("36 px") &&
            provided.Contains("12 px") && provided.Contains("海边来信。"), "current preview values were omitted");
        await store.ConfirmCreativeAsync("project-a", session, "one");
        Check(await store.ProvideCreativeAsync("project-a", session, "two", "设计封面") is null,
            "unchanged confirmed settings were provisioned again");

        await store.UndoEntryAsync(edited.Id, edited.Revision);
        var reopened = await new LearningStore(fixture.DataRoot).CreativeAsync("project-a");
        Check(reopened is not null && reopened.Style == before.Style && reopened.Revision > edited.Revision,
            "undo did not restore the prior style");
        provided = await store.ProvideCreativeAsync("project-a", session, "three", "设计封面");
        Check(provided is not null && provided.Contains("#FCFAF5") && provided.Contains("14 px") &&
            provided.Contains("4 px") && provided.Contains("山间来信。") && !provided.Contains("海边来信。"),
            "undo did not provision the restored revision");
    }

    private static async Task PreviewNextDelivery(Fixture fixture)
    {
        const string project = "project-a";
        var store = new LearningStore(fixture.DataRoot);
        Check(await store.PreviewCreativeDeliveryAsync(project) is null && !store.HasState,
            "empty preview created persisted state");
        await store.SaveCreativeAsync(null, new("Georgia", "", "", ""), true, 0);
        var local = await store.SaveCreativeAsync(project, new("", "#24766D", "", "简明"), true, 0);
        var snapshot = await store.PreviewCreativeDeliveryAsync(project);
        Check(snapshot is not null && snapshot.Contains("全局默认 · 字体 Georgia") &&
            snapshot.Contains("当前项目优先 · 主色 #24766D"), "preview omitted saved scope data");
        var session = Guid.NewGuid().ToString("D");
        Check(await store.ProvideCreativeAsync(project, session, "one", "设计封面") == snapshot,
            "real next delivery differed from preview or preview consumed it");
        await store.ConfirmCreativeAsync(project, session, "one");
        Check(await store.PreviewCreativeDeliveryAsync(project) == snapshot &&
            await store.ProvideCreativeAsync(project, session, "two", "设计封面") is null,
            "preview changed repeat-delivery behavior");
        await store.SetEntryStateAsync(local.Id, "disabled", local.Revision);
        Check(await store.PreviewCreativeDeliveryAsync(project) == await store.PreviewCreativeDeliveryAsync(null) &&
            await store.PreviewCreativeDeliveryAsync("project-b") == await store.PreviewCreativeDeliveryAsync(null),
            "paused or foreign scope was included");
    }

    private static async Task UndoCreation(Fixture fixture)
    {
        var store = new LearningStore(fixture.DataRoot);
        const string scope = "project-a";
        Check(!await store.HasCreativeHistoryAsync(scope), "empty scope can undo");
        var saved = await store.SaveCreativeAsync(scope, new("", "", "", "") { Radius = 0 }, true, 0);
        Check(await store.HasCreativeHistoryAsync(scope) && !await store.HasCreativeHistoryAsync(null),
            "first-save undo availability is incorrect");
        await store.UndoEntryAsync(saved.Id, saved.Revision);
        var empty = await store.CreativeSettingsAsync(scope);
        Check(empty.Preference is null && empty.Revision == saved.Revision + 1 &&
            !await store.HasCreativeHistoryAsync(scope), "undoing creation did not restore an empty scope");
        Check(await store.ProvideCreativeAsync(scope, Guid.NewGuid().ToString("D"), "one", "设计封面") is null,
            "undone first save is still provisioned");

        saved = await store.SaveCreativeAsync(scope, new("宋体", "", "", ""), false, empty.Revision);
        var deleted = await store.SetEntryStateAsync(saved.Id, "deleted", saved.Revision);
        Check(!await store.HasCreativeHistoryAsync(scope), "explicit deletion retained undo history");
        var recreated = await store.SaveCreativeAsync(scope, new("", "", "", "") { Spacing = 0 }, true, deleted.Revision);
        Check(await store.HasCreativeHistoryAsync(scope), "recreation cannot be undone");
        await store.UndoEntryAsync(recreated.Id, recreated.Revision);
        Check(await store.CreativeAsync(scope) is null, "undoing recreation resurrected deleted settings");
    }

    private static async Task ClearToInherit(Fixture fixture)
    {
        const string project = "project-a";
        var store = new LearningStore(fixture.DataRoot);
        var global = await store.SaveCreativeAsync(null, new("Georgia", "#4F7058", "", ""), true, 0);
        var local = await store.SaveCreativeAsync(project, new("", "#24766D", "", ""), true, 0);

        var removedRevision = await store.RemoveCreativeAsync(project, local.Revision);
        var reopened = new LearningStore(fixture.DataRoot);
        Check(await reopened.CreativeSettingsAsync(project) == (null, local.Revision + 1) &&
            await reopened.HasCreativeHistoryAsync(project), "clearing the project override did not leave an undoable revision");
        var inherited = await reopened.ProvideCreativeAsync(project, Guid.NewGuid().ToString("D"), "one", "设计封面");
        Check(inherited is not null && inherited.Contains("全局默认 · 字体 Georgia；主色 #4F7058") &&
            !inherited.Contains("当前项目优先") && await reopened.CreativeAsync(null) == global,
            "the cleared project did not inherit the unchanged global preference");

        await ExpectRevisionConflict(() => reopened.UndoCreativeRemovalAsync(project, local.Revision));
        var restored = await reopened.UndoCreativeRemovalAsync(project, removedRevision);
        Check(restored == local with { Revision = removedRevision + 1 } && await reopened.CreativeAsync(project) == restored &&
            await reopened.CreativeAsync(null) == global, "undo did not restore the project override at the next revision");

        await ExpectRevisionConflict(() => reopened.RemoveCreativeAsync(project, local.Revision));
        removedRevision = await reopened.RemoveCreativeAsync(project, restored.Revision);
        var recreated = await reopened.SaveCreativeAsync(project, new("", "", "", "") { Radius = 0 }, true, removedRevision);
        await reopened.UndoEntryAsync(recreated.Id, recreated.Revision);
        var empty = await reopened.CreativeSettingsAsync(project);
        Check(empty.Preference is null && empty.Revision == recreated.Revision + 1 && await reopened.HasCreativeHistoryAsync(project),
            "undoing a new override did not return to the undoable removal");
        Check((await reopened.UndoCreativeRemovalAsync(project, empty.Revision)).Style == local.Style,
            "the project override could no longer be restored from the latest tombstone");

    }

    private static async Task ExpectRevisionConflict(Func<Task> operation)
    {
        try { await operation(); }
        catch (InvalidOperationException) { return; }
        throw new InvalidOperationException("a stale creative revision was accepted");
    }

    private static async Task HistoricalSettings(Fixture fixture)
    {
        var store = new LearningStore(fixture.DataRoot);
        var legacy = await store.SaveCreativeAsync(null, new("宋体", "#24766D", "", ""), true, 0);
        // Reproduce the on-disk shape of an M1 first save, before the preview fields and creation history.
        using (var db = new SqliteConnection($"Data Source={Path.Combine(fixture.DataRoot, "learning", "state.sqlite")}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "SELECT payload FROM profiles WHERE id=$id";
            command.Parameters.AddWithValue("$id", legacy.Id);
            var payload = JsonNode.Parse((string)command.ExecuteScalar()!)!.AsObject();
            payload["value"] = """{"font":"宋体","color":"#24766D","style":"","voice":""}""";
            command.CommandText = "UPDATE profiles SET payload=$payload WHERE id=$id; DELETE FROM history WHERE entry_id=$id;";
            command.Parameters.AddWithValue("$payload", payload.ToJsonString());
            command.ExecuteNonQuery();
        }
        var historical = await store.CreativeSettingsAsync(null);
        Check(historical.Preference == legacy && !await store.HasCreativeHistoryAsync(null),
            "legacy first save gained synthetic undo or lost data");
        var noChange = await store.SaveCreativeAsync(null, legacy.Style, true, historical.Revision);
        Check(noChange.Revision == historical.Revision && !await store.HasCreativeHistoryAsync(null),
            "saving identical historical values created a revision");
        var updated = await store.SaveCreativeAsync(null, legacy.Style with { Radius = 16 }, true, historical.Revision);
        await store.UndoEntryAsync(updated.Id, updated.Revision);
        Check((await store.CreativeAsync(null))?.Style == legacy.Style && !await store.HasCreativeHistoryAsync(null),
            "undo did not preserve legacy settings");
    }

    private static async Task Validate(Fixture fixture)
    {
        var store = new LearningStore(fixture.DataRoot);
        var empty = new CreativeStyle("", "", "", "");
        foreach (var invalid in new[]
        {
            empty, empty with { Background = "#abc" }, empty with { Spacing = -1 },
            empty with { Spacing = 65 }, empty with { Radius = -1 }, empty with { Radius = 49 },
            empty with { SampleText = new string('字', 501) }, empty with { SampleText = "样\0文" }
        })
        {
            try { await store.SaveCreativeAsync(null, invalid, true, 0); }
            catch (ArgumentException) { continue; }
            throw new InvalidOperationException("invalid preview settings were saved");
        }
        var saved = await store.SaveCreativeAsync(null, empty with { Spacing = 64, Background = " ", SampleText = "\n " }, true, 0);
        Check(saved.Revision == 1 && saved.Style.Background is null && saved.Style.SampleText is null,
            "invalid writes consumed revision or empty optional text was retained");
        var fromExample = await store.SaveCreativeAsync("example-project", empty with
        {
            ReferenceId = "ref-" + new string('e', 64), ReferenceName = "已交付封面", ReferencePart = "标题排版"
        }, true, 0);
        Check(fromExample.Style.HasValues() && fromExample.Style.ReferencePart == "标题排版",
            "a favorite can supply the only creative preference");
        try { await store.RemoveCreativeAsync("example-project", fromExample.Revision); }
        catch (InvalidOperationException)
        {
            Check(await store.CreativeAsync("example-project") == fromExample,
                "rejected field removal changed the saved reference");
            return;
        }
        throw new InvalidOperationException("field removal discarded the saved reference");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
