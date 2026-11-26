using System.Text.Json;
using Amifolio.Core;

namespace Amifolio.Bridge;

internal static partial class CollectionHook
{
    private static async Task<string> SyncWorksAsync(PortfolioStore store, CodexProject project, JsonElement hook, CancellationToken ct)
    {
        try
        {
            if (!(await store.WorkSyncAsync(project.Id, ct)).Enabled) return "disabled";
            if (hook.TryGetProperty("stop_hook_active", out var active) && active.ValueKind != JsonValueKind.False)
                return "continuation_ignored";
            if (HookInput.ReadString(hook, "session_id", 128) is not { } session ||
                HookInput.ReadString(hook, "turn_id", 128) is not { } turn)
                return "missing_identity";

            var reader = new CodexTurnReader(Program.CodexHome);
            var final = HookInput.ReadString(hook, "last_assistant_message", 32768) ?? "";
            var works = await reader.ReadWorksAsync(session, turn, project.RootPath, final, ct);
            await store.RecordCodexWorksAsync(project, session, turn, works, ct);
            return works is null ? "unsupported" : "ok";
        }
        catch (Exception ex) when (HookInput.ExpectedFailure(ex)) { return "unavailable"; }
    }
}
