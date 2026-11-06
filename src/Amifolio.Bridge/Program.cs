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
            var store = new PortfolioStore(root);
            await store.InitializeAsync();
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
                "repo" => await operations.SubmitAsync(positional!, project, "Agent", "", true),
                "list" => await operations.SearchAsync(options.GetValueOrDefault("--search"), project),
                "projects" => await operations.ProjectsAsync(),
                "status" => await operations.StatusAsync(positional!),
                "context" => await operations.ContextAsync(project),
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

    internal static (string Code, string Message) Describe(Exception exception) => exception switch
    {
        BridgeException e => (e.Code, e.Message),
        FileNotFoundException or DirectoryNotFoundException => ("not_found", "The requested file or directory does not exist."),
        UnauthorizedAccessException => ("access_denied", "The requested operation was denied."),
        ArgumentException or NotSupportedException => ("invalid_arguments", "The requested value or operation is invalid."),
        IOException => ("io_error", "The local storage operation failed."),
        OperationCanceledException => ("cancelled", "The operation was cancelled."),
        _ => ("internal_error", "The operation failed. No archive success is being acknowledged.")
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
            if (arg is not ("--data-root" or "--project" or "--source" or "--tags" or "--search") ||
                index + 1 == args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal) || options.ContainsKey(arg))
                throw new BridgeException("invalid_arguments", "Unknown, duplicate or valueless option.");
            options.Add(arg, BridgeOperations.Text(args[++index], arg, arg == "--data-root" ? 4096 : 1024, arg is "--tags" or "--search"));
        }
        if (options.Remove("--data-root", out var requestedRoot)) root = requestedRoot;
        if (words.Count == 0) throw new BridgeException("invalid_arguments", "Expected submit, repo, list, projects, status, context or mcp.");
        var command = words[0];
        var positional = command is "submit" or "repo" or "status";
        if (words.Count != (positional ? 2 : 1)) throw new BridgeException("invalid_arguments", "Unexpected or missing positional argument.");
        string[] allowed = command switch
        {
            "submit" => ["--project", "--source", "--tags"],
            "repo" or "context" => ["--project"],
            "list" => ["--project", "--search"],
            "projects" or "status" or "mcp" => [],
            _ => throw new BridgeException("invalid_arguments", "Unknown command.")
        };
        if (options.Keys.Any(key => !allowed.Contains(key))) throw new BridgeException("invalid_arguments", "An option is not supported by this command.");
        return (Path.GetFullPath(root), command, positional ? words[1] : null, options);
    }
}
