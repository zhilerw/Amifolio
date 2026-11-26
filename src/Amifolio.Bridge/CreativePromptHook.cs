using System.Text.Json;
using Amifolio.Core;

namespace Amifolio.Bridge;

internal static class CreativePromptHook
{
    internal static async Task<int> RunAsync(string dataRoot, TextWriter output)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var ct = timeout.Token;
        string? context = null;
        try
        {
            using var payload = await HookInput.ReadAsync("UserPromptSubmit", ct);
            var root = payload.RootElement;
            var learning = new LearningStore(dataRoot);
            if (learning.HasState && HookInput.ReadString(root, "prompt", 32768) is { } prompt &&
                HookInput.ReadString(root, "session_id", 128) is { } session &&
                HookInput.ReadString(root, "turn_id", 128) is { } turn &&
                HookInput.ReadString(root, "cwd", 4096) is { } cwd && Path.IsPathFullyQualified(cwd))
            {
                var catalog = new CodexCatalog(Program.CodexHome, dataRoot);
                var projects = await catalog.DiscoverAsync(ct);
                var project = HookInput.ResolveProject(projects.Items, cwd);
                if (!projects.Truncated && project is not null)
                    context = await learning.ProvideCreativeAsync(project.Id, session, turn, prompt, ct);
            }
        }
        catch (Exception ex) when (HookInput.ExpectedFailure(ex))
        {
            await Console.Error.WriteLineAsync("Amifolio preference hook: " + ex.GetType().Name);
        }

        await output.WriteLineAsync(context is null ? "{}" : JsonSerializer.Serialize(new
        {
            hookSpecificOutput = new { hookEventName = "UserPromptSubmit", additionalContext = context }
        }, Program.JsonOptions));
        return 0;
    }
}
