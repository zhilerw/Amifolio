using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Amifolio.Core;

namespace Amifolio.Bridge;

internal static class Program
{
    internal static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        // Own stdout exclusively; dependency diagnostics cannot corrupt the wire format.
        using var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
        Console.SetOut(Console.Error);
        var mcp = false;
        try
        {
            var (root, command, positional, options) = Parse(args);
            mcp = command == "mcp";
            if(command=="hook-stop")return await CollectionHook.RunAsync(root,output);
            if (command is "repo" or "backup" or "restore")
                throw new BridgeException("reference_only", "Project snapshots and arbitrary-path backup/restore are unavailable in reference mode. Manage application recovery explicitly in the app.");
            if (command is "task-retry" or "process-tasks")
                throw new BridgeException("confirmation_required", "Start or retry parsing and model work only through an explicit user action in the app.");
            if (command == "collect")
            {
                if (!int.TryParse(options.GetValueOrDefault("--retention-days", "30"), out var days) || days < 0 || days > 36500) throw new BridgeException("invalid_arguments", "Retention days must be between 0 and 36500.");
                var maintenanceResult = new { ok = true, removedObjects = await new PortfolioMaintenance(root).CollectAsync(TimeSpan.FromDays(days)) };
                await output.WriteLineAsync(JsonSerializer.Serialize(maintenanceResult, JsonOptions)); return 0;
            }
            await using var session = await StorageLease.SessionAsync(root);
            var store = await OpenReferenceStoreAsync(root);
            if(command is "tasks" or "task-cancel")
            {
                var tasks=new TaskStore(root);await tasks.RecoverAsync();
                if(command=="task-cancel")await tasks.CancelAsync(positional!);
                await output.WriteLineAsync(JsonSerializer.Serialize(new{tasks=await tasks.ListAsync()},JsonOptions));return 0;
            }
            var operations = new BridgeOperations(store);
            if (command == "mcp")
            {
                await new McpServer(operations, Console.OpenStandardInput(), output).RunAsync();
                return 0;
            }
            var project = options.GetValueOrDefault("--project");
            var source = options.GetValueOrDefault("--source", "Agent");
            var tags = options.GetValueOrDefault("--tags", "");
            object result = command switch
            {
                "submit" => await operations.SubmitAsync(positional!, project, source, tags),
                "list" => await operations.SearchAsync(options.GetValueOrDefault("--search"), project),
                "projects" => await operations.ProjectsAsync(),
                "status" => await operations.StatusAsync(positional!),
                "context" => await operations.ContextAsync(project, options.GetValueOrDefault("--layer", "overview")),
                _ => throw new BridgeException("invalid_arguments", "Unknown command.")
            };
            await output.WriteLineAsync(JsonSerializer.Serialize(result, JsonOptions));
            return 0;
        }
        catch (Exception exception)
        {
            var (code, message) = Describe(exception);
            await Console.Error.WriteLineAsync($"Amifolio: {exception.GetType().Name}: {message}");
            object error = mcp
                ? McpServer.Error(null, code == "invalid_arguments" ? -32602 : -32603, message)
                : new { error = new { code, message } };
            await output.WriteLineAsync(JsonSerializer.Serialize(error, JsonOptions));
            return 1;
        }
    }

    internal static async Task<PortfolioStore> OpenReferenceStoreAsync(string root, CancellationToken ct = default)
    {
        var home = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(home)) home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var store = new PortfolioStore(root);
        // Enable before initialization/queries; normal product operations never use the legacy copy store.
        store.EnableCodexReferenceMode(Path.GetFullPath(home));
        await store.InitializeAsync(ct);
        await store.RefreshCodexProjectsAsync(ct);
        return store;
    }

    internal static (string Code, string Message) Describe(Exception exception) => exception switch
    {
        BridgeException e => (e.Code, e.Message),
        FileNotFoundException or DirectoryNotFoundException => ("not_found", "The requested file or directory does not exist."),
        UnauthorizedAccessException => ("access_denied", "The requested operation was denied."),
        ArgumentException or NotSupportedException => ("invalid_arguments", "The requested value or operation is invalid."),
        IOException => ("io_error", "The local storage operation failed."),
        OperationCanceledException => ("cancelled", "The operation was cancelled."),
        _ => ("internal_error", "The operation failed. No reference success is being acknowledged.")
    };

    private static (string Root, string Command, string? Positional, Dictionary<string, string> Options) Parse(string[] args)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amifolio");
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var words = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            var arg = args[index];
            if (!arg.StartsWith("--", StringComparison.Ordinal)) { words.Add(arg); continue; }
            if (arg is not ("--data-root" or "--project" or "--source" or "--tags" or "--search" or "--retention-days" or "--layer") ||
                index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal) || options.ContainsKey(arg))
                throw new BridgeException("invalid_arguments", "Unknown, duplicate or valueless option.");
            options.Add(arg, BridgeOperations.Text(args[++index], arg, arg == "--data-root" ? 4096 : 1024, arg is "--tags" or "--search"));
        }
        if (options.Remove("--data-root", out var requestedRoot)) root = requestedRoot;
        if (words.Count == 0) throw new BridgeException("invalid_arguments", "Expected submit, repo, list, projects, status, context or mcp.");
        var command = words[0];
        var positional = command is "submit" or "repo" or "status" or "backup" or "restore" or "task-cancel" or "task-retry";
        if (words.Count != (positional ? 2 : 1)) throw new BridgeException("invalid_arguments", "Unexpected or missing positional argument.");
        string[] allowed = command switch
        {
            "submit" => ["--project", "--source", "--tags"],
            "repo" => ["--project"],
            "context" => ["--project", "--layer"],
            "list" => ["--project", "--search"],
            "projects" or "status" or "mcp" or "tasks" or "task-cancel" or "task-retry" or "process-tasks" or "hook-stop" => [],
            "backup" or "restore" => [],
            "collect" => ["--retention-days"],
            _ => throw new BridgeException("invalid_arguments", "Unknown command.")
        };
        if (options.Keys.Any(key => !allowed.Contains(key))) throw new BridgeException("invalid_arguments", "An option is not supported by this command.");
        return (Path.GetFullPath(root), command, positional ? words[1] : null, options);
    }
}
