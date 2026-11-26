using Amifolio.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

internal static class CreativePreviewCases
{
    internal static (string, Func<Fixture, Task>)[] All =>
    [
        ("creative preview round-trips through reload and preserves native reference", RoundTrip),
        ("creative preview provision stays in scope and follows undo", ProvisionAfterUndo),
        ("creative preview can undo creation and recreation", UndoCreation),
        ("creative preview reads historical settings without fabricating undo", HistoricalSettings),
        ("creative preview validates new fields before advancing revision", Validate)
    ];

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
        Check(fromExample.Style.ReferencePart == "标题排版", "a favorite can supply the only creative preference");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
