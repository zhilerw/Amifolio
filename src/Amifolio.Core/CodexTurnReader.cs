using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Amifolio.Core;

public sealed record CodexTurnEvidence(string SessionId, string TurnId, string RootPath, string UserTextHash,
    string UserText, DateTimeOffset ObservedAt, string SourceFingerprint);

public sealed record CodexTurnIdentity(string SessionId, string TurnId, string RootPath, DateTimeOffset ObservedAt);

/// <summary>
/// Local trusted-file evidence, NOT transport/client authentication. The caller must authorize
/// collection and the exact project before calling either method. No project files are opened.
/// Only session_meta.id/cwd, turn_context.turn_id/cwd, and top-level user events are evidence.
/// Modern item_completed requires an exact UserMessage item, GUID item identity and matching
/// session/turn; assistant/tool items, response_item and compacted history are never evidence.
/// </summary>
/// <remarks>
/// Supports context -> user, and task_started(turn_id) -> user -> matching context.
/// A context without task_started also binds subsequent user events; a preceding orphan does not.
/// Unknown item schemas, non-text input and older contexts without turn_id intentionally yield none.
/// Reads only a 128 KiB first-line header and a 2 MiB tail, not entire historical conversations.
/// Limits are inclusive: 4096 directory entries, 256 rollout files, 3 date-directory levels,
/// 128 MiB file length, 256 KiB JSONL record, JSON depth 32, 64 KiB UTF8 user text,
/// 20000 records/file, 256 turn states/file and 256 returned headers, 16 MiB total IO/call.
/// Five-second cooperative deadline; cancellation is propagated. Limits/errors fail closed;
/// empty results do not prove that there are no missed receipts outside the bounded window.
/// </remarks>
public sealed class CodexTurnReader(string codexHome)
{
    private const int HeaderBytes = 128 * 1024;
    private const int TailBytes = 2 * 1024 * 1024;
    private const int LineBytes = 256 * 1024;
    private const int TextBytes = 64 * 1024;
    private const long FileBytes = 128L * 1024 * 1024;
    private const int MaxEntries = 4096;
    private const int MaxFiles = 256;
    private const int MaxTurns = 256;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32 };

    public async Task<CodexTurnEvidence?> ReadTurnAsync(string sessionId, string turnId, string expectedRoot, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (!SessionId(sessionId) || !TurnId(turnId)) return null;
        var result = await ScanAsync(expectedRoot, sessionId, turnId, null, ct).ConfigureAwait(false);
        return result.Evidence;
    }

    public async Task<IReadOnlyList<CodexTurnIdentity>> RecentTurnsAsync(string projectRoot, DateTimeOffset notBefore, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var result = await ScanAsync(projectRoot, null, null, notBefore, ct).ConfigureAwait(false);
        return result.Headers;
    }

    private Task<ScanResult> ScanAsync(string expectedRoot, string? sessionId, string? turnId, DateTimeOffset? notBefore, CancellationToken ct) =>
        Task.Run(async () =>
        {
            var budget = new Budget(ct);
            try
            {
                var home = AbsoluteLocal(codexHome);
                var root = AbsoluteLocal(expectedRoot); // Lexical identity only: never open the project.
                ArchiveSafety.NoReparse(home);
                var files = Discover(home, budget);
                var matches = new Dictionary<string, List<TurnState>>(StringComparer.Ordinal);
                var sessions = new HashSet<string>(StringComparer.Ordinal);
                var duplicateSessions = new HashSet<string>(StringComparer.Ordinal);
                foreach (var path in files)
                {
                    budget.Check();
                    var filenameId = FileSessionId(path);
                    try
                    {
                        await using var stream = OpenShared(path);
                        var length = stream.Length;
                        if (length <= 0 || length > FileBytes)
                        {
                            if (filenameId is not null) duplicateSessions.Add(filenameId);
                            continue;
                        }
                        var header = await ReadHeaderAsync(stream, length, budget).ConfigureAwait(false);
                        if (header is null)
                        {
                            if (filenameId is not null) duplicateSessions.Add(filenameId);
                            continue;
                        }
                        var (id, cwd, headerLength, headerHash) = header.Value;
                        if (sessionId is not null && id != sessionId) continue;
                        if (!sessions.Add(id)) duplicateSessions.Add(id);
                        if (!cwd.Equals(root, ArchiveSafety.PathComparison)) continue;

                        var states = await ReadTailAsync(stream, length, cwd, id, sessionId is null ? null : turnId, budget).ConfigureAwait(false);
                        var headerAgain = await ReadAsync(stream, 0, headerLength, budget).ConfigureAwait(false);
                        if (Hash(headerAgain) != headerHash) continue;
                        // Appends are allowed; a truncated or replaced open source is not evidence.
                        VerifyHandle(stream, path);
                        if (stream.Length < length) continue;
                        if (!matches.TryAdd(id, states)) duplicateSessions.Add(id);
                    }
                    catch (LimitException) { throw; }
                    catch (Exception ex) when (OptionalFailure(ex))
                    {
                        // A candidate that cannot be checked cannot disambiguate the requested thread.
                        if (filenameId is not null) duplicateSessions.Add(filenameId);
                        if (sessionId is not null && filenameId == sessionId) return ScanResult.Empty;
                    }
                }
                budget.Check();
                if (sessionId is not null)
                {
                    if (duplicateSessions.Contains(sessionId) || !matches.TryGetValue(sessionId, out var states)) return ScanResult.Empty;
                    var state = states.SingleOrDefault(t => t.Id == turnId && t.Valid);
                    if (state?.Text is null) return ScanResult.Empty;
                    var hash = Hash(Utf8.GetBytes(state.Text));
                    // Domain-separated, length-delimited structured identity. Never hash the appendable file,
                    // filename, archive location, byte offset, or unrelated assistant/tool output.
                    var fingerprint = Hash(JsonSerializer.SerializeToUtf8Bytes(new[] {
                        "amifolio.codex.user-turn.v1", sessionId, state.Id, PathKey(root), hash,
                        state.ObservedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) }));
                    return new(new(sessionId, state.Id, root, hash, state.Text, state.ObservedAt, fingerprint), []);
                }
                var headers = matches.Where(p => !duplicateSessions.Contains(p.Key))
                    .SelectMany(p => p.Value.Where(t => t.Valid && t.ObservedAt >= notBefore)
                        .Select(t => new CodexTurnIdentity(p.Key, t.Id, root, t.ObservedAt)))
                    .OrderByDescending(t => t.ObservedAt).ThenBy(t => t.SessionId, StringComparer.Ordinal).ThenBy(t => t.TurnId, StringComparer.Ordinal).ToArray();
                if (headers.Length > MaxTurns) return ScanResult.Empty;
                return new(null, headers);
            }
            catch (Exception ex) when (OptionalFailure(ex)) { return ScanResult.Empty; }
        }, ct);

    private static List<string> Discover(string home, Budget budget)
    {
        var paths = new List<string>();
        foreach (var name in new[] { "sessions", "archived_sessions" })
        {
            var basePath = Path.Combine(home, name);
            ArchiveSafety.NoReparse(basePath);
            if (!Directory.Exists(basePath)) continue;
            Walk(basePath, 0);
        }
        paths.Sort(StringComparer.Ordinal);
        return paths;

        void Walk(string directory, int depth)
        {
            budget.Check();
            ArchiveSafety.NoReparse(directory);
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly))
            {
                budget.Entry();
                if (!ArchiveSafety.Within(entry, home)) throw new IOException("Session path escaped home.");
                var attrs = File.GetAttributes(entry);
                if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
                if ((attrs & FileAttributes.Directory) != 0)
                {
                    var segment = Path.GetFileName(entry);
                    // Codex sessions/YYYY/MM/DD and flat or dated archived_sessions only.
                    if (depth < 3 && segment.Length == (depth == 0 ? 4 : 2) && segment.All(char.IsAsciiDigit)) Walk(entry, depth + 1);
                }
                else if (Path.GetFileName(entry).StartsWith("rollout-", StringComparison.Ordinal) && entry.EndsWith(".jsonl", StringComparison.Ordinal))
                {
                    if (paths.Count >= MaxFiles) throw new LimitException();
                    paths.Add(entry);
                }
            }
        }
    }

    private static async Task<(string Id, string Root, int Length, string Hash)?> ReadHeaderAsync(FileStream stream, long length, Budget budget)
    {
        // Stop at the first header record, with at most one 4 KiB read-ahead block. In particular,
        // never allocate/read an entire tail belonging to an unauthorized project.
        var bytes = new byte[(int)Math.Min(HeaderBytes, length)];
        var used = 0;
        var end = -1;
        stream.Position = 0;
        while (used < bytes.Length)
        {
            budget.Check();
            var count = Math.Min(4096, bytes.Length - used);
            budget.Bytes(count);
            var n = await stream.ReadAsync(bytes.AsMemory(used, count), budget.Token).ConfigureAwait(false);
            if (n == 0) throw new IOException("Rollout truncated while reading header.");
            end = Array.IndexOf(bytes, (byte)'\n', used, n);
            used += n;
            if (end >= 0) break;
        }
        if (end < 0) return null; // Never accept an incomplete JSONL record, even valid JSON without LF.
        var start = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
        using var doc = Parse(bytes.AsMemory(start, end - start));
        var row = doc.RootElement;
        if (String(row, "type") != "session_meta" || !row.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return null;
        var id = String(payload, "id");
        var cwd = String(payload, "cwd");
        if (!SessionId(id) || cwd is null) return null;
        // Child-agent replays are not independent user turns. Unknown structured sources fail closed.
        if(payload.TryGetProperty("source",out var source)&&
            (source.ValueKind!=JsonValueKind.String||source.GetString() is not("cli" or "vscode" or "desktop" or "exec")))return null;
        // session_meta.session_id may be a different runtime ID. Only id is the stable thread ID.
        return (id!, AbsoluteLocal(cwd), end + 1, Hash(bytes[..(end + 1)]));
    }

    private static async Task<List<TurnState>> ReadTailAsync(FileStream stream, long length, string root, string sessionId, string? wantedTurn, Budget budget)
    {
        var offset = Math.Max(0, length - TailBytes);
        var bytes = await ReadAsync(stream, offset, (int)(length - offset), budget).ConfigureAwait(false);
        var start = 0;
        if (offset > 0)
        {
            // Discard the first record fragment. No correlation carries across a skipped prefix.
            start = Array.IndexOf(bytes, (byte)'\n') + 1;
            if (start == 0) return [];
        }
        else if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) start = 3;
        var turns = new Dictionary<string, TurnState>(StringComparer.Ordinal);
        TurnState? active = null;
        var records = 0;
        while (start < bytes.Length)
        {
            budget.Check();
            if (++records > 20_000) throw new LimitException();
            var end = Array.IndexOf(bytes, (byte)'\n', start);
            if (end < 0)
            {
                // An in-progress write may be a conflicting context or duplicate user event.
                // Without a complete record, none of this source can yet be disambiguated.
                return [];
            }
            if (end - start > LineBytes) throw new LimitException();
            using var doc = Parse(bytes.AsMemory(start, end - start));
            start = end + 1;
            var row = doc.RootElement;
            var type = String(row, "type");
            if (!row.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) throw new JsonException();
            if (type == "session_meta")
            {
                // Only the actual first header is allowed; embedded/copied headers are ambiguous.
                if (offset != 0 || records != 1) throw new JsonException();
                continue;
            }
            if (type == "compacted") { active = null; continue; }
            if (type == "turn_context")
            {
                var id = String(payload, "turn_id");
                if (!TurnId(id)) { active = null; continue; }
                var conflict = active is { Started: true } && active.Id != id;
                if (conflict) active!.Bad = true;
                active = State(id!);
                if (conflict) active.Bad = true;
                var cwd = String(payload, "cwd");
                active.Context = true;
                if (cwd is null || !AbsoluteLocal(cwd).Equals(root, ArchiveSafety.PathComparison)) active.Bad = true;
                continue;
            }
            if (type != "event_msg") continue;
            switch (String(payload, "type"))
            {
                case "task_started":
                {
                    var id = String(payload, "turn_id");
                    active = TurnId(id) ? State(id!) : null;
                    if (active is not null && active.Started) active.Bad = true;
                    if (active is not null) active.Started = true;
                    break;
                }
                case "task_complete":
                case "turn_aborted":
                {
                    var id = String(payload, "turn_id");
                    if (active is not null && id is not null && id != active.Id) active.Bad = true;
                    active = null;
                    break;
                }
                case "thread_rolled_back": return []; // This adapter does not reconstruct rollback history.
                case "item_completed":
                {
                    if(!payload.TryGetProperty("item",out var item)||item.ValueKind!=JsonValueKind.Object||String(item,"type")!="UserMessage")break;
                    if(active is null)break;
                    if(String(payload,"thread_id")!=sessionId||String(payload,"turn_id")!=active.Id||!Guid.TryParseExact(String(item,"id"),"D",out _))
                    {active.Bad=true;break;}
                    if(!item.TryGetProperty("content",out var parts)||parts.ValueKind!=JsonValueKind.Array||parts.GetArrayLength() is 0 or >16)
                    {active.Bad=true;break;}
                    var text=new StringBuilder();var valid=true;
                    foreach(var part in parts.EnumerateArray())
                    {
                        if(part.ValueKind!=JsonValueKind.Object||String(part,"type")!="text"||String(part,"text") is not string value||
                            part.EnumerateObject().Any(p=>p.Name is not("type" or "text" or "text_elements"))||
                            part.TryGetProperty("text_elements",out var elements)&&(elements.ValueKind!=JsonValueKind.Array||elements.GetArrayLength()!=0))
                        {valid=false;break;}
                        text.Append(value);
                        if(Utf8.GetByteCount(text.ToString())>TextBytes){valid=false;break;}
                    }
                    active.Users++;
                    if(!valid||active.Users!=1||text.Length==0||!Timestamp(row,out var at)){active.Bad=true;break;}
                    active.ObservedAt=at;if(wantedTurn==active.Id)active.Text=text.ToString();
                    break;
                }
                case "user_message":
                {
                    if (active is null) break;
                    var id = String(payload, "turn_id");
                    if (payload.TryGetProperty("turn_id", out _) && id != active.Id) { active.Bad = true; break; }
                    active.Users++;
                    if (active.Users != 1) { active.Bad = true; break; }
                    var text = String(payload, "message");
                    if (text is null || text.Length == 0 || Utf8.GetByteCount(text) > TextBytes || !Timestamp(row, out var at))
                    { active.Bad = true; break; }
                    active.ObservedAt = at;
                    // RecentTurns retains no user texts; targeted read keeps only the requested turn.
                    if (wantedTurn == active.Id) active.Text = text;
                    break;
                }
            }
        }
        // The writer may append without changing this snapshot, but cannot revise already-read
        // bytes and leave evidence assembled from two revisions. Recheck the captured range.
        await VerifyAsync(stream, offset, bytes, budget).ConfigureAwait(false);
        return turns.Values.ToList();

        TurnState State(string id)
        {
            if (turns.TryGetValue(id, out var found)) return found;
            if (turns.Count >= MaxTurns) throw new LimitException();
            var value = new TurnState(id); turns.Add(id, value); return value;
        }
    }

    private static JsonDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        // STJ otherwise accepts duplicate properties with last-value-wins semantics.
        var document = JsonDocument.Parse(bytes, JsonOptions);
        try
        {
            Unique(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            return document;
        }
        catch (InvalidOperationException ex) { document.Dispose(); throw new JsonException("Invalid JSON string encoding.", ex); }
        catch { document.Dispose(); throw; }

        static void Unique(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in node.EnumerateObject())
                {
                    if (!seen.Add(property.Name)) throw new JsonException();
                    Unique(property.Value);
                }
            }
            else if (node.ValueKind == JsonValueKind.Array)
                foreach (var item in node.EnumerateArray()) Unique(item);
        }
    }

    private static string? String(JsonElement node, string name)
    {
        try { return node.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null; }
        catch (InvalidOperationException ex) { throw new JsonException("Invalid JSON string encoding.", ex); }
    }

    private static bool Timestamp(JsonElement row, out DateTimeOffset value)
    {
        var text = String(row, "timestamp");
        value = default;
        // Require an explicit UTC offset: never interpret a local-machine timezone as source time.
        return text is not null && text.Length is >= 20 and <= 40 && (text.EndsWith('Z') || text[^6] is '+' or '-')
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }

    private static bool SessionId(string? value) => value is not null && Guid.TryParseExact(value, "D", out _);
    private static string? FileSessionId(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var id = stem.Length >= 37 && stem[^37] == '-' ? stem[^36..] : null;
        return SessionId(id) ? id : null; // A veto for uncertainty only; never positive identity evidence.
    }
    private static bool TurnId(string? value) => value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string PathKey(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    private static string AbsoluteLocal(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !Path.IsPathFullyQualified(path)
            || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || path.Split(['/', '\\']).Any(p => p is "." or ".." || p.EndsWith(' ') || p.EndsWith('.')))
            throw new IOException("Only absolute local paths without traversal are supported.");
        return ArchiveSafety.LocalPath(path);
    }

    private static FileStream OpenShared(string path)
    {
        ArchiveSafety.NoReparse(path);
        var attrs = File.GetAttributes(path);
        if ((attrs & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0) throw new IOException("Not a regular rollout file.");
        // ArchiveSafety.OpenRead denies an existing Codex writer on Windows. Preserve its handle
        // verification here while sharing append access; all reads use the captured length.
        // Deny replacement while the handle is held. Disable FileStream read buffering so the
        // second snapshot pass reaches the file rather than reusing an earlier cached block.
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            1, FileOptions.Asynchronous | FileOptions.RandomAccess);
        try
        {
            VerifyHandle(stream, path);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static void VerifyHandle(FileStream stream, string path)
    {
        ArchiveSafety.NoReparse(path);
        var attrs = File.GetAttributes(path);
        if ((attrs & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0) throw new IOException("Not a regular rollout file.");
        if (!OperatingSystem.IsWindows()) return;
        var buffer = new StringBuilder(32_768);
        var size = GetFinalPathNameByHandle(stream.SafeFileHandle, buffer, (uint)buffer.Capacity, 0);
        if (size == 0 || size >= buffer.Capacity) throw new IOException("Cannot verify rollout handle.");
        var actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!ArchiveSafety.LocalPath(actual).Equals(path, ArchiveSafety.PathComparison)) throw new IOException("Redirected rollout handle.");
        ArchiveSafety.NoReparse(path);
    }

    private static async Task<byte[]> ReadAsync(FileStream stream, long offset, int count, Budget budget)
    {
        budget.Bytes(count);
        stream.Position = offset;
        var buffer = new byte[count];
        var used = 0;
        while (used < count)
        {
            budget.Check();
            var n = await stream.ReadAsync(buffer.AsMemory(used, Math.Min(16 * 1024, count - used)), budget.Token).ConfigureAwait(false);
            if (n == 0) throw new IOException("Rollout truncated while reading.");
            used += n;
        }
        budget.Check();
        return buffer;
    }

    private static async Task VerifyAsync(FileStream stream, long offset, byte[] expected, Budget budget)
    {
        stream.Position = offset;
        var buffer = new byte[16 * 1024];
        var used = 0;
        while (used < expected.Length)
        {
            var count = Math.Min(buffer.Length, expected.Length - used);
            budget.Bytes(count);
            var n = await stream.ReadAsync(buffer.AsMemory(0, count), budget.Token).ConfigureAwait(false);
            if (n == 0 || !buffer.AsSpan(0, n).SequenceEqual(expected.AsSpan(used, n)))
                throw new IOException("Rollout revised while reading.");
            used += n;
        }
    }

    private static bool OptionalFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or JsonException;
    private sealed class LimitException : IOException;
    private sealed class Budget(CancellationToken token)
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        private int entries;
        private long bytes;
        internal CancellationToken Token => token;
        internal void Check() { token.ThrowIfCancellationRequested(); if (watch.Elapsed >= TimeSpan.FromSeconds(5)) throw new LimitException(); }
        internal void Entry() { Check(); if (++entries > MaxEntries) throw new LimitException(); }
        internal void Bytes(int count) { Check(); bytes += count; if (bytes > 16 * 1024 * 1024) throw new LimitException(); }
    }
    private sealed class TurnState(string id)
    {
        internal string Id { get; } = id;
        internal bool Context, Started, Bad;
        internal int Users;
        internal string? Text;
        internal DateTimeOffset ObservedAt;
        internal bool Valid => Context && !Bad && Users == 1;
    }
    private sealed record ScanResult(CodexTurnEvidence? Evidence, IReadOnlyList<CodexTurnIdentity> Headers)
    {
        internal static ScanResult Empty { get; } = new(null, []);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}
