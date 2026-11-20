using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Amifolio.Context;
using Amifolio.Core;
using Microsoft.Data.Sqlite;

internal static class LearningBackgroundBoundaryCases
{
    internal static (string, Func<Task>)[] All =>
    [
        ("learning boundary global remote revocation after budget wait sends zero HTTP", GlobalRevocation),
        ("learning boundary project policy revocation after budget wait sends zero HTTP", ProjectRevocation),
        ("learning boundary global learning revision after budget wait sends zero HTTP", GlobalLearningRevision),
        ("learning boundary terminal lease after budget wait sends zero HTTP", TerminalLease),
        ("learning boundary expired running lease after budget wait sends zero HTTP", ExpiredLease),
        ("learning boundary replaced lease token after budget wait sends zero HTTP", ReplacedToken),
        ("learning boundary existing budget requires all persisted fields", MissingFields),
        ("learning boundary invalid or future budget dates fail closed", InvalidDates),
        ("learning boundary malformed counts timestamps and duplicate fields fail closed", InvalidFields),
        ("learning boundary malformed and oversized JSON budgets fail closed", MalformedBudget),
        ("learning boundary first absent budget is normal and records one reservation", FirstBudget),
        ("learning boundary valid old day rolls over without HTTP", PreviousDay),
        ("learning boundary daily cap and cooldown send zero HTTP", Limits),
        ("learning boundary retired learning budget never recreates legacy state", RetiredBudget)
    ];

    private const string Reserved = "learning-budget-reserved";
    private static string Day => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Budget(string? day = null, int count = 0, DateTimeOffset? last = null) =>
        JsonSerializer.Serialize(new { Day = day ?? Day, Requests = count, LastAttempt = last });

    private static async Task GlobalRevocation()
    {
        using var f = new Fixture(); await f.Ready();
        await AtReservation(f, () => f.Tasks.SetRemoteAllowedAsync(false));
        Check(await f.Tasks.RemoteAllowedAsync() == false && (await f.Service.Learning.PolicyAsync(f.Project)).Remote,
            "global revocation was not independently exercised");
    }
    private static async Task ProjectRevocation()
    {
        using var f = new Fixture(); await f.Ready();
        await AtReservation(f, async () =>
        {
            var p = await f.Service.Learning.PolicyAsync(f.Project);
            await f.Service.Learning.SetPolicyAsync(f.Project, p with { Learn = false }, p.Revision);
        });
        Check(await f.Tasks.RemoteAllowedAsync() == true && !(await f.Service.Learning.PolicyAsync(f.Project)).Learn,
            "project revocation was not independently exercised");
    }
    private static async Task GlobalLearningRevision()
    {
        using var f = new Fixture(); await f.Ready();
        var global = await f.Service.Learning.SetPolicyAsync(null, new() { Learn = true });
        var p = await f.Service.Learning.PolicyAsync(f.Project);
        await f.Service.Learning.SetPolicyAsync(f.Project, p with { ContributeGlobal = true }, p.Revision);
        await AtReservation(f, async () => { await f.Service.Learning.SetPolicyAsync(null, global with { Learn = false }, global.Revision); });
    }
    private static async Task TerminalLease()
    {
        using var f = new Fixture(); await f.Ready();
        await AtReservation(f, async () => { var job = f.Job(); await f.Service.Learning.FailAsync(job.Id, job.Token, "cancelled"); });
    }
    private static async Task ExpiredLease()
    {
        using var f = new Fixture(); await f.Ready();
        await AtReservation(f, () => { f.ChangeJob(j => j["leaseUntil"] = DateTimeOffset.UtcNow.AddMinutes(-1)); return Task.CompletedTask; });
    }
    private static async Task ReplacedToken()
    {
        using var f = new Fixture(); await f.Ready();
        await AtReservation(f, () => { f.ChangeJob(j => j["token"] = "replacement-owner"); return Task.CompletedTask; });
    }
    private static async Task MissingFields()
    {
        using var f = new Fixture(); await f.Ready();
        foreach (var value in new[] { "{}", "{\"Requests\":0,\"LastAttempt\":null}", "{\"Day\":\"" + Day + "\",\"LastAttempt\":null}",
            "{\"Day\":\"" + Day + "\",\"Requests\":0}" }) await RejectBudget(f, value);
    }
    private static async Task InvalidDates()
    {
        using var f = new Fixture(); await f.Ready();
        foreach (var day in new[] { "", "not-a-day", "2026-02-30", "2026-1-1", DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) })
            await RejectBudget(f, Budget(day));
    }
    private static async Task InvalidFields()
    {
        using var f = new Fixture(); await f.Ready();
        foreach (var value in new[] {
            Budget(count: -1), Budget(count: 9),
            "{\"Day\":\"" + Day + "\",\"Requests\":\"0\",\"LastAttempt\":null}",
            "{\"Day\":\"" + Day + "\",\"Requests\":0.5,\"LastAttempt\":null}",
            "{\"Day\":\"" + Day + "\",\"Requests\":null,\"LastAttempt\":null}",
            "{\"Day\":\"" + Day + "\",\"Requests\":0,\"LastAttempt\":\"invalid\"}",
            "{\"Day\":\"" + Day + "\",\"Requests\":0,\"LastAttempt\":false}",
            "{\"Day\":\"" + Day + "\",\"Day\":\"" + Day + "\",\"Requests\":0,\"LastAttempt\":null}",
            "{\"Day\":\"" + Day + "\",\"Requests\":8,\"Requests\":0,\"LastAttempt\":null}",
            "{\"Day\":\"" + Day + "\",\"Requests\":0,\"LastAttempt\":null,\"FutureFormat\":1}" })
            await RejectBudget(f, value);
    }
    private static async Task MalformedBudget()
    {
        using var f = new Fixture(); await f.Ready();
        foreach (var value in new[] { "", "null", "[]", "{", new string(' ', 4097) }) await RejectBudget(f, value);
    }
    private static async Task FirstBudget()
    {
        using var f = new Fixture(); await f.Ready(); Check(!File.Exists(f.BudgetPath), "fixture pre-created budget");
        await AtReservation(f, () => f.Tasks.SetRemoteAllowedAsync(false));
        Check(f.BudgetCount() == 1, "first absent budget did not record one reservation");
    }
    private static async Task PreviousDay()
    {
        using var f = new Fixture(); await f.Ready();
        await f.WriteBudget(Budget(DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), 8, DateTimeOffset.UtcNow.AddDays(-1)));
        await AtReservation(f, () => f.Tasks.SetRemoteAllowedAsync(false));
        Check(f.BudgetCount() == 1, "valid old day did not roll over once");
    }
    private static async Task Limits()
    {
        using var f = new Fixture(); await f.Ready();
        foreach (var value in new[] { Budget(count: 8), Budget(count: 1, last: DateTimeOffset.UtcNow) })
        {
            await f.WriteBudget(value); Check(!await f.Run(), "cap/cooldown did not stop background"); f.NoHttp();
            Check(await File.ReadAllTextAsync(f.BudgetPath) == value && (await f.Service.Learning.SnapshotAsync(f.Project)).Jobs.Count == 0,
                "denied budget was rewritten or claimed a job");
        }
    }
    private static async Task RetiredBudget()
    {
        using var f = new Fixture(); await f.Ready(); await new LegacyDataService(f.Data).MigrateAsync();
        await AtReservation(f, () => f.Tasks.SetRemoteAllowedAsync(false));
        Check(File.Exists(Path.Combine(f.Data, "learning", "automation-budget.json")), "retired budget missing from learning directory");
        foreach (var name in new[] { "objects", "context-state", "preference-state", "recovery" })
            Check(!Directory.Exists(Path.Combine(f.Data, name)), "retired directory recreated: " + name);
        Check((await new LegacyDataService(f.Data).MigrateAsync()).Status == "already", "new learning budget conflicts with completed migration");
    }

    private static async Task RejectBudget(Fixture f, string value)
    {
        await f.WriteBudget(value); var rejected = false;
        try { await f.Run(); } catch (Exception e) when (e is IOException or InvalidDataException or JsonException) { rejected = true; }
        f.NoHttp(); Check(rejected, "existing invalid budget was accepted: " + value[..Math.Min(value.Length, 100)]);
        Check(await File.ReadAllTextAsync(f.BudgetPath) == value, "invalid budget was overwritten");
        Check((await f.Service.Learning.SnapshotAsync(f.Project)).Jobs.Count == 0, "invalid budget claimed a job");
    }
    private static async Task AtReservation(Fixture f, Func<Task> mutate)
    {
        var previous = StorageFaults.Handler.Value; var hits = 0;
        StorageFaults.Handler.Value = stage =>
        {
            if (stage != Reserved) return;
            hits++; mutate().GetAwaiter().GetResult();
        };
        try
        {
            Check(!await f.Run(), "revoked work reported success");
            f.NoHttp(); Check(hits == 1, "reservation boundary not exercised exactly once");
        }
        finally { StorageFaults.Handler.Value = previous; }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "Amifolio-learning-boundary-" + Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(root, "data");
        private string Home => Path.Combine(root, "codex");
        internal string Project = "";
        internal string BudgetPath => Path.Combine(Data, Service.LegacyRetired ? "learning" : "context-state", "automation-budget.json");
        internal readonly PortfolioStore Store;
        internal readonly ContextService Service;
        internal readonly TaskStore Tasks;
        private readonly NoNetwork handler = new();
        private readonly HttpClient client;
        internal Fixture() { Store = new(Data); client = new(handler); Service = new(Store, client); Tasks = new(Data); }
        internal async Task Ready()
        {
            Directory.CreateDirectory(Home); var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
            Store.EnableCodexReferenceMode(Home); await Store.InitializeAsync();
            await File.WriteAllTextAsync(Path.Combine(Home, ".codex-global-state.json"), JsonSerializer.Serialize(new Dictionary<string, object>
            { ["local-projects"] = new Dictionary<string, object> { ["synthetic"] = new { name = "synthetic", rootPaths = new[] { source } } } }));
            await Store.RefreshCodexProjectsAsync(); Project = Store.CodexProjects.Single().Id;
            await Service.Learning.SetPolicyAsync(Project, new() { Capture = true, Learn = true, Mode = "background", Remote = true, Endpoint = "https://synthetic.invalid/v1", BatchSize = 1 });
            await Tasks.SetRemoteAllowedAsync(true);
            var session = Guid.NewGuid().ToString(); var now = DateTimeOffset.UtcNow.ToString("O");
            var directory = Path.Combine(Home, "sessions", DateTime.UtcNow.ToString("yyyy"), DateTime.UtcNow.ToString("MM"), DateTime.UtcNow.ToString("dd")); Directory.CreateDirectory(directory);
            var rows = new object[] { new { timestamp = now, type = "session_meta", payload = new { id = session, cwd = source } },
                new { timestamp = now, type = "turn_context", payload = new { turn_id = "turn-1", cwd = source } },
                new { timestamp = now, type = "event_msg", payload = new { type = "user_message", message = "以后请简洁回答" } } };
            await File.WriteAllLinesAsync(Path.Combine(directory, "rollout-" + session + ".jsonl"), rows.Select(row => JsonSerializer.Serialize(row)));
            await Service.CaptureLearningAsync(new(Project, session, "turn-1", "讨论表达方式。", "completed", ["以后请简洁回答"], []), Home);
        }
        internal Task<bool> Run() => Service.RunLearningBackgroundAsync(Project, Home, new("https://synthetic.invalid/v1", "synthetic-model", true), "synthetic-key", true);
        internal async Task WriteBudget(string value) { Directory.CreateDirectory(Path.GetDirectoryName(BudgetPath)!); await File.WriteAllTextAsync(BudgetPath, value); }
        internal int BudgetCount() { using var json = JsonDocument.Parse(File.ReadAllText(BudgetPath)); Check(json.RootElement.GetProperty("Day").GetString() == Day, "budget day not current"); return json.RootElement.GetProperty("Requests").GetInt32(); }
        internal LearningJob Job()
        {
            using var db = OpenLearning(); using var command = db.CreateCommand(); command.CommandText = "SELECT payload FROM jobs ORDER BY created DESC LIMIT 1";
            return JsonSerializer.Deserialize<LearningJob>((string)command.ExecuteScalar()!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        }
        internal void ChangeJob(Action<JsonObject> change)
        {
            var job = Job(); using var db = OpenLearning(); using var command = db.CreateCommand();
            var json = JsonSerializer.SerializeToNode(job, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject(); change(json);
            command.CommandText = "UPDATE jobs SET payload=$payload WHERE id=$id"; command.Parameters.AddWithValue("$payload", json.ToJsonString()); command.Parameters.AddWithValue("$id", job.Id); command.ExecuteNonQuery();
        }
        private SqliteConnection OpenLearning()
        {
            var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Data, "learning", "state.sqlite"), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()); db.Open(); return db;
        }
        internal void NoHttp() => Check(handler.Calls == 0, "boundary sent HTTP after denial (calls=" + handler.Calls + ")");
        public void Dispose() { client.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class NoNetwork : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref Calls); throw new HttpRequestException("Synthetic handler forbids network"); }
    }
}
