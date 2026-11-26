using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Amifolio.Bridge;

internal sealed partial class McpServer(BridgeOperations operations, Stream input, TextWriter output)
{
    internal const int MaxMessageBytes = 64 * 1024;
    internal const int MaxResponseBytes = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly string ServerVersion = typeof(McpServer).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    private bool initialized;
    private bool ready;

    internal static object Error(object? id, int code, string message) => new { jsonrpc = "2.0", id, error = new { code, message } };
    private static object Success(object id, object result) => new { jsonrpc = "2.0", id, result };

    public async Task RunAsync()
    {
        try
        {
            await foreach (var frame in ReadFramesAsync())
            {
                if (frame.Error is not null)
                {
                    await SendAsync(Error(null, -32700, frame.Error));
                    continue;
                }
                await HandleAsync(frame.Text!);
            }
        }
        catch (IOException)
        {
            await Console.Error.WriteLineAsync("Amifolio: MCP stream read/write failed.");
            // Best effort only: a closed output cannot carry another error.
            try { await SendAsync(Error(null, -32000, "MCP stream read failed.")); } catch (IOException) { }
        }
    }

    private async Task SendAsync(object message)
    {
        var json = JsonSerializer.Serialize(message, Program.JsonOptions);
        if (Encoding.UTF8.GetByteCount(json) > MaxResponseBytes)
        {
            var serialized = JsonSerializer.SerializeToElement(message, Program.JsonOptions);
            object? id = serialized.TryGetProperty("id", out var value) ? value.Clone() : null;
            json = JsonSerializer.Serialize(Error(id, -32603, "Response exceeds the 256 KiB limit."), Program.JsonOptions);
        }
        await output.WriteLineAsync(json);
        await output.FlushAsync();
    }

    private async Task HandleAsync(string line)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { await SendAsync(Error(null, -32700, "Invalid JSON or nesting exceeds 32 levels.")); return; }
        using (document)
        {
            var root = document.RootElement;
            object? id = null;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("id", out var idValue))
            {
                if (idValue.ValueKind == JsonValueKind.String && idValue.GetString()!.Length <= 128 ||
                    idValue.ValueKind == JsonValueKind.Number && idValue.TryGetInt64(out _)) id = idValue.Clone();
                else { await SendAsync(Error(null, -32600, "id must be a string up to 128 characters or a 64-bit integer.")); return; }
            }
            if (root.ValueKind != JsonValueKind.Object || !UniqueProperties(root) ||
                !root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
                !root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String ||
                root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _))
            { await SendAsync(Error(id, -32600, "Expected one JSON-RPC 2.0 request or notification; batches are unsupported.")); return; }

            var method = methodElement.GetString()!;
            var parameters = root.TryGetProperty("params", out var p) ? p : JsonSerializer.SerializeToElement(new { });
            // Never execute a mutating tool supplied as a notification; notifications receive no response.
            if (id is null)
            {
                if (method == "notifications/initialized" && initialized && parameters.ValueKind == JsonValueKind.Object) ready = true;
                return;
            }
            try
            {
                if (parameters.ValueKind != JsonValueKind.Object || !UniqueProperties(parameters))
                    throw new BridgeException("invalid_arguments", "params must be an object with unique keys.");
                object result;
                if (method == "initialize")
                {
                    if (initialized) { await SendAsync(Error(id, -32600, "This connection is already initialized.")); return; }
                    var requested = Required(parameters, "protocolVersion", 128);
                    if (!parameters.TryGetProperty("capabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Object ||
                        !parameters.TryGetProperty("clientInfo", out var client) || client.ValueKind != JsonValueKind.Object)
                        throw new BridgeException("invalid_arguments", "initialize requires capabilities and clientInfo objects.");
                    Required(client, "name", 128);
                    Required(client, "version", 128);
                    result = new
                    {
                        protocolVersion = requested is "2025-11-25" or "2025-06-18" ? requested : "2025-11-25",
                        capabilities = new { tools = new { listChanged = false } },
                        serverInfo = new { name = "amifolio", version = ServerVersion },
                        instructions = "Reference files only within discovered local Codex projects. Never copy, snapshot, parse, upload or edit project originals. artifact_submit returns referenced, not archived. Parsing requires an explicit user action in the app. Search is metadata-only; context is scoped and read-only. Resolve the exact project, check learning_status, and act only on already granted permissions. Capture one bounded turn summary and exact user quotes; a receipt is not caller authentication. Claim/complete only chat-mode batches. Never grant permissions or invent source proof. Read enabled scoped summaries before accessing any original."
                    };
                    initialized = true;
                }
                else if (method == "ping") result = new { };
                else if (!ready) { await SendAsync(Error(id, -32002, "Initialize and send notifications/initialized before using tools.")); return; }
                else if (method == "tools/list")
                {
                    Only(parameters, "cursor", "_meta");
                    if (parameters.TryGetProperty("cursor", out _)) throw new BridgeException("invalid_arguments", "No pagination cursor is valid for this fixed tool list.");
                    result = new { tools = ToolDefinitions() };
                }
                else if (method == "tools/call") result = await CallAsync(parameters);
                else { await SendAsync(Error(id, -32601, "Method not found.")); return; }
                await SendAsync(Success(id, result));
            }
            catch (BridgeException e) { await SendAsync(Error(id, -32602, e.Message)); }
            catch (Exception e)
            {
                var (_, message) = Program.Describe(e);
                await Console.Error.WriteLineAsync($"Amifolio: {e.GetType().Name}: {message}");
                await SendAsync(Error(id, -32603, message));
            }
        }
    }

    private async Task<object> CallAsync(JsonElement parameters)
    {
        Only(parameters, "name", "arguments", "_meta");
        var name = Required(parameters, "name", 128);
        var args = parameters.TryGetProperty("arguments", out var a) ? a : JsonSerializer.SerializeToElement(new { });
        if (args.ValueKind != JsonValueKind.Object || !UniqueProperties(args))
            throw new BridgeException("invalid_arguments", "arguments must be an object with unique keys.");
        string? projectId;
        // Validate all arguments before any I/O; tool execution failures use isError instead of RPC errors.
        Func<Task<object>> operation;
        switch (name)
        {
            case "artifact_submit":
                Only(args, "path", "projectId", "source", "tags");
                var path = Required(args, "path", 4096);
                projectId = Optional(args, "projectId", 128);
                var source = Optional(args, "source", 128) ?? "Agent";
                if (source.Trim().Equals("应用", StringComparison.OrdinalIgnoreCase))
                    throw new BridgeException("invalid_arguments", "The application-generated source is reserved; MCP callers must identify their agent source.");
                var tags = Optional(args, "tags", 1024, true) ?? "";
                operation = () => operations.SubmitAsync(path, projectId, source, tags);
                break;
            case "artifact_status":
                Only(args, "id");
                var artifactId = Required(args, "id", 128);
                operation = () => operations.StatusAsync(artifactId);
                break;
            case "artifact_search":
                Only(args, "search", "projectId", "limit");
                var search = Optional(args, "search", 1024, true);
                projectId = Optional(args, "projectId", 128);
                var limit = BridgeOperations.MaxResults;
                if (args.TryGetProperty("limit", out var requestedLimit) &&
                    (requestedLimit.ValueKind != JsonValueKind.Number || !requestedLimit.TryGetInt32(out limit) || limit is < 1 or > BridgeOperations.MaxResults))
                    throw new BridgeException("invalid_arguments", "limit must be an integer from 1 to 50.");
                operation = () => operations.SearchAsync(search, projectId, limit);
                break;
            case "context_read":
                Only(args, "projectId", "layer");
                projectId = Optional(args, "projectId", 128);
                var layer = Optional(args, "layer", 32) ?? "overview";
                if (layer is not ("overview" or "memory" or "style" or "summary" or "preferences"))
                    throw new BridgeException("invalid_arguments", "Unknown context layer.");
                operation = () => operations.ContextAsync(projectId, layer);
                break;
            case "projects_list":
                Only(args);
                operation = operations.ProjectsAsync;
                break;
            default: operation=LearningOperation(name,args)??throw new BridgeException("invalid_arguments", "Unknown tool.");break;
        }
        try { return ToolResult(await operation()); }
        catch (Exception e)
        {
            var (code, message) = Program.Describe(e);
            await Console.Error.WriteLineAsync($"Amifolio: {e.GetType().Name}: {message}");
            return ToolResult(new { error = new { code, message } }, true);
        }
    }

    private static object ToolResult(object value, bool isError = false) => new
    {
        content = new[] { new { type = "text", text = JsonSerializer.Serialize(value, Program.JsonOptions) } },
        structuredContent = value, isError
    };

    private static string Required(JsonElement value, string name, int maximum) => Optional(value, name, maximum)
        ?? throw new BridgeException("invalid_arguments", $"Missing {name}.");

    private static string? Optional(JsonElement value, string name, int maximum, bool allowEmpty = false)
    {
        if (!value.TryGetProperty(name, out var property)) return null;
        if (property.ValueKind != JsonValueKind.String) throw new BridgeException("invalid_arguments", $"{name} must be a string when provided.");
        return BridgeOperations.Text(property.GetString()!, name, maximum, allowEmpty);
    }

    private static void Only(JsonElement value, params string[] allowed)
    {
        if (value.EnumerateObject().Any(property => !allowed.Contains(property.Name, StringComparer.Ordinal)))
            throw new BridgeException("invalid_arguments", "Unknown argument.");
    }

    private static bool UniqueProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !UniqueProperties(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) if (!UniqueProperties(item)) return false;
        return true;
    }

    private static object[] ToolDefinitions()
    {
        static object TextSchema(int maxLength, int minLength = 1) => new { type = "string", minLength, maxLength };
        static object Tool(string name, string description, Dictionary<string, object> properties, string[] required, bool readOnly) => new
        {
            name, description,
            inputSchema = new { type = "object", properties, required, additionalProperties = false },
            annotations = new { readOnlyHint = readOnly, destructiveHint = false, openWorldHint = false }
        };
        return new object[]
        {
            Tool("artifact_submit", "Register a reference to one existing file in a discovered local Codex project. Returns id, original path, status=referenced after metadata commit. Never copies, parses, uploads or edits the file. Optional source/tags are compatibility labels and do not modify metadata.",
                new() { ["path"] = TextSchema(4096), ["projectId"] = TextSchema(128), ["source"] = TextSchema(128), ["tags"] = TextSchema(1024, 0) }, ["path"], false),
            Tool("artifact_status", "Check reference source metadata by known ID: referenced, changed, missing or unavailable. Never parses or returns file contents.", new() { ["id"] = TextSchema(128) }, ["id"], true),
            Tool("artifact_search", "Search bounded metadata only (no preview, raw content or object paths). Omit projectId for a library-wide metadata search.",
                new() { ["search"] = TextSchema(1024, 0), ["projectId"] = TextSchema(128), ["limit"] = new { type = "integer", minimum = 1, maximum = 50, @default = 50 } }, [], true),
            Tool("context_read", "Read only enabled context documents in the exact scope. Default overview includes START.md. Other layers: memory, style, summary, and global-only confirmed general preferences. No candidates, private provenance, raw artifacts or source index; never changes sharing settings.",
                new() { ["projectId"] = TextSchema(128), ["layer"] = new { type = "string", @enum = new[] { "overview", "memory", "style", "summary", "preferences" }, @default = "overview" } }, [], true),
            Tool("projects_list", "List at most 50 project IDs and names. Does not expose root paths or change projects.", new(), [], true)
        }.Concat(LearningToolDefinitions()).ToArray();
    }

    private async IAsyncEnumerable<(string? Text, string? Error)> ReadFramesAsync()
    {
        var chunk = new byte[8192];
        using var line = new MemoryStream();
        var overflow = false;
        while (true)
        {
            var count = await input.ReadAsync(chunk);
            if (count == 0) break;
            for (var index = 0; index < count; index++)
            {
                var item = chunk[index];
                if (item == (byte)'\n')
                {
                    yield return Decode(line, overflow);
                    line.SetLength(0);
                    line.Position = 0;
                    overflow = false;
                }
                else if (!overflow)
                {
                    if (line.Length == MaxMessageBytes) { overflow = true; line.SetLength(0); line.Position = 0; }
                    else line.WriteByte(item);
                }
            }
        }
        if (overflow || line.Length > 0) yield return Decode(line, overflow);
    }

    private static (string? Text, string? Error) Decode(MemoryStream line, bool overflow)
    {
        if (overflow) return (null, "Message exceeds the 64 KiB limit.");
        try { return (StrictUtf8.GetString(line.GetBuffer(), 0, (int)line.Length), null); }
        catch (DecoderFallbackException) { return (null, "Message is not valid UTF-8."); }
    }
}
