using System.Text.Json;
using System.Text.RegularExpressions;

namespace Amifolio.Core;

public sealed record CodexWorkSource(string Kind, string Path);

public sealed partial class CodexTurnReader
{
    private static readonly Regex DeliveryLink = new(@"\[[^\]\r\n]{1,200}\]\((?<path><[^>\r\n]+>|[^)\r\n]+)\)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex DeliveryLine = new(
        @"^\s*(?:[-*]\s*)?(?:已(?:完成|生成|创建|交付|导出)|交付(?:物|文件)?|生成文件|下载|输出文件|delivered\b|generated\b|created\b|output\b)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public async Task<IReadOnlyList<CodexWorkSource>?> ReadWorksAsync(string sessionId, string turnId, string expectedRoot,
        string finalMessage, CancellationToken ct = default)
    {
        if (!SessionId(sessionId) || !TurnId(turnId) || finalMessage.Length > 32768) return null;
        var result = await ScanAsync(expectedRoot, sessionId, turnId, null, ct, finalMessage).ConfigureAwait(false);
        return result.Works?.Distinct().Take(24).ToArray();
    }

    private static void ReadWorkEvent(TurnState state, JsonElement payload, string sessionId, string finalMessage)
    {
        if (String(payload, "turn_id") != state.Id) return;
        var eventType = String(payload, "type");
        if (eventType == "task_complete")
        {
            state.Works ??= [];
            if (String(payload, "last_agent_message") is { } text && text == finalMessage) AddDelivery(state.Works, text);
            return;
        }

        if (eventType != "item_completed" || String(payload, "thread_id") != sessionId ||
            !payload.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object ||
            !item.TryGetProperty("content", out var parts) || parts.ValueKind != JsonValueKind.Array ||
            parts.GetArrayLength() > 24) return;

        var kind = String(item, "type");
        if (kind == "UserMessage" && Guid.TryParseExact(String(item, "id"), "D", out _))
        {
            state.Works ??= [];
            foreach (var part in parts.EnumerateArray())
                if (part.ValueKind == JsonValueKind.Object && String(part, "type") is "local_image" or "local_file" &&
                    String(part, "path") is { } path)
                    AddWork(state.Works, "upload", path);
        }
        else if (kind == "AgentMessage" && !string.IsNullOrWhiteSpace(finalMessage))
        {
            var text = string.Concat(parts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.Object &&
                String(p, "type") == "Text").Select(p => String(p, "text")));
            // Stop can arrive before task_complete, but must agree with the persisted agent item.
            if (text == finalMessage)
            {
                state.Works ??= [];
                AddDelivery(state.Works, text);
            }
        }
    }

    private static void AddDelivery(List<CodexWorkSource> works, string text)
    {
        if (text.Length > 32768) return;
        foreach (var line in text.Split('\n'))
        {
            if (!DeliveryLine.IsMatch(line)) continue;
            foreach (Match link in DeliveryLink.Matches(line))
            {
                var path = link.Groups["path"].Value.Trim('<', '>', ' ');
                if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && uri.IsFile && path.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
                {
                    if (uri.IsUnc || uri.Host.Length != 0) continue;
                    path = uri.LocalPath;
                }
                else if (path.StartsWith("sandbox:/", StringComparison.Ordinal))
                {
                    path = path["sandbox:".Length..];
                    if (OperatingSystem.IsWindows() && path.Length > 2 && path[0] == '/' && path[2] == ':') path = path[1..];
                    path = Uri.UnescapeDataString(path);
                }
                AddWork(works, "delivery", path);
            }
        }
    }

    private static void AddWork(List<CodexWorkSource> works, string kind, string path)
    {
        if (works.Count >= 48 || path.Any(char.IsControl)) return;
        try
        {
            var local = AbsoluteLocal(path);
            if (Path.HasExtension(local)) works.Add(new(kind, local));
        }
        catch (Exception ex) when (OptionalFailure(ex)) { }
    }
}
