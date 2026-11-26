using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Amifolio.Context;

public enum NativeMemoryVersion { V1, V2 }
public enum NativeMemoryReadiness { Unsupported }
public enum NativeMemoryReadStatus { Available, Missing, Untrusted, Unreadable }
public enum NativeMemoryProjectMatch { Unknown, Matched, Different }
public enum NativeMemorySourceKind
{
    Overview, Memory, RolloutSummary,
    // A note input on disk. Its presence does not attest to Codex processing it.
    PendingNote
}

/// <summary>Available entries carry the verified file path and mtime. Other paths are expected locations.</summary>
public sealed record NativeMemorySource(NativeMemorySourceKind Kind, string Path, NativeMemoryReadStatus Status,
    string? Content, DateTimeOffset? LastWriteTimeUtc, bool IsTruncated, NativeMemoryProjectMatch ProjectMatch);

public sealed record NativeMemoryStoreSnapshot(NativeMemoryVersion Version, string RootPath,
    NativeMemoryReadStatus Status, IReadOnlyList<NativeMemorySource> Sources, bool IsLimited);

public sealed record NativeMemorySnapshot(string CodexHome, NativeMemoryStoreSnapshot V1, NativeMemoryStoreSnapshot V2,
    NativeMemoryVersion? SelectedVersion, string? SelectionEvidence, NativeMemoryReadiness V2Readiness);

/// <summary>Reads the native Codex memory artifacts on Windows without starting or modifying Codex.</summary>
public sealed class NativeMemoryService
{
    private const int OverviewBytes = 256 * 1024;
    private const int SourceBytes = 64 * 1024;
    private const int MaxSourcesPerDirectory = 48;
    private const int MaxDirectoryEntries = 256;
    private const int MaxProjectDirectoryEntries = 1024;
    private static readonly Regex SummaryReference = new(
        @"^\s*-\s+`?(?<qualified>rollout_summaries/)?(?<name>[\p{L}\p{N}_.-]{1,236}\.md)`?(?:\s|$)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly string home;
    private readonly bool hasLocalPath;

    public NativeMemoryService(string? codexHome = null)
    {
        var environmentHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        var requested = codexHome ?? (string.IsNullOrWhiteSpace(environmentHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : environmentHome);
        var normalized = NativeMemoryFiles.NormalizeLocalPath(requested);
        home = normalized ?? requested;
        hasLocalPath = normalized is not null;
    }

    /// <summary>Annotates source summaries against an optional project path. The caller controls filtering.</summary>
    public Task<NativeMemorySnapshot> ReadAsync(string? projectPath = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadCoreAsync(projectPath, cancellationToken), cancellationToken);

    private async Task<NativeMemorySnapshot> ReadCoreAsync(string? projectPath, CancellationToken token)
    {
        var homeStatus = hasLocalPath ? NativeMemoryFiles.ProbeHome(home) : NativeMemoryReadStatus.Untrusted;
        var project = NativeMemoryFiles.NormalizeLocalPath(projectPath);
        var v1 = await ReadStoreAsync(NativeMemoryVersion.V1, homeStatus, project, token).ConfigureAwait(false);
        var v2 = await ReadStoreAsync(NativeMemoryVersion.V2, homeStatus, project, token).ConfigureAwait(false);
        // Both stores can coexist with dual-write. Neither the store presence nor the
        // summary format marker resolves a thread's configuration. Readiness also
        // depends on host state, and the local service never starts a host.
        return new(home, v1, v2, null, null, NativeMemoryReadiness.Unsupported);
    }

    private async Task<NativeMemoryStoreSnapshot> ReadStoreAsync(NativeMemoryVersion version, NativeMemoryReadStatus homeStatus,
        string? project, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = Path.Combine(home, version == NativeMemoryVersion.V1 ? "memories" : "memories_v2");
        var status = homeStatus == NativeMemoryReadStatus.Available ? NativeMemoryFiles.Probe(root, directory: true) : homeStatus;
        if (status != NativeMemoryReadStatus.Available)
            return new(version, root, status, [], project is not null && status != NativeMemoryReadStatus.Missing);

        var overview = await NativeMemoryFiles.ReadAsync(Path.Combine(root, "memory_summary.md"),
            NativeMemorySourceKind.Overview, OverviewBytes, token).ConfigureAwait(false);
        var sources = new List<NativeMemorySource> { overview };
        if (version == NativeMemoryVersion.V1)
        {
            var handbook = await NativeMemoryFiles.ReadAsync(Path.Combine(root, "MEMORY.md"),
                NativeMemorySourceKind.Memory, OverviewBytes, token).ConfigureAwait(false);
            sources.Add(handbook);
        }

        var references = sources.SelectMany(ReadSummaryReferences);
        var limited = await ReadDirectoryAsync(sources, Path.Combine(root, "rollout_summaries"),
            NativeMemorySourceKind.RolloutSummary, references, project, token).ConfigureAwait(false);
        if (version == NativeMemoryVersion.V2)
            limited |= await ReadDirectoryAsync(sources, Path.Combine(root, "extensions", "ad_hoc", "notes"),
                NativeMemorySourceKind.PendingNote, [], null, token).ConfigureAwait(false);

        status = sources.Any(source => source.Status == NativeMemoryReadStatus.Available) ? NativeMemoryReadStatus.Available
            : sources.Any(source => source.Status == NativeMemoryReadStatus.Untrusted) ? NativeMemoryReadStatus.Untrusted
            : sources.Any(source => source.Status == NativeMemoryReadStatus.Unreadable) ? NativeMemoryReadStatus.Unreadable
            : NativeMemoryReadStatus.Missing;
        return new(version, root, status, sources.AsReadOnly(), limited || sources.Any(source => source.IsTruncated));
    }

    private static async Task<bool> ReadDirectoryAsync(List<NativeMemorySource> sources, string directory,
        NativeMemorySourceKind kind, IEnumerable<string> references, string? project, CancellationToken token)
    {
        var comparer = NativeMemoryFiles.PathComparer;
        var cited = project is null ? references.Distinct(comparer).Take(MaxSourcesPerDirectory + 1).ToArray() : [];
        using var handle = NativeMemoryFiles.OpenDirectory(directory, out var status);
        if (handle is null) return AddUnavailableSources(sources, directory, kind, status, cited, project is not null);

        var (found, scanStatus, limited) = ScanDirectory(handle, project is not null, token);
        if (scanStatus != NativeMemoryReadStatus.Available)
            return AddUnavailableSources(sources, directory, kind, scanStatus, cited, project is not null);

        var names = cited.Concat(found.Order(comparer)).Distinct(comparer).ToArray();
        if (project is null) limited |= names.Length > MaxSourcesPerDirectory;
        var candidates = project is null ? names.Take(MaxSourcesPerDirectory).Order(comparer) : names.AsEnumerable();
        var collected = new List<NativeMemorySource>();
        var matches = 0;
        var otherSources = 0;
        foreach (var name in candidates)
        {
            token.ThrowIfCancellationRequested();
            if (project is not null && matches == MaxSourcesPerDirectory)
            {
                limited = true;
                break;
            }
            var path = Path.Combine(directory, name);
            var source = await NativeMemoryFiles.ReadAsync(path, kind, SourceBytes, token).ConfigureAwait(false);
            var match = MatchProject(source, project);
            if (project is not null)
            {
                limited |= source.Status != NativeMemoryReadStatus.Available ||
                    (source.IsTruncated && match == NativeMemoryProjectMatch.Unknown);
                if (match == NativeMemoryProjectMatch.Matched) matches++;
                else if (otherSources == MaxSourcesPerDirectory) continue;
                else otherSources++;
            }
            collected.Add(source with { ProjectMatch = match });
        }

        if (!NativeMemoryFiles.IsVerifiedDirectory(handle, directory))
            return AddUnavailableSources(sources, directory, kind, NativeMemoryReadStatus.Untrusted, cited, project is not null);
        sources.AddRange(collected);
        return limited;
    }

    private static bool AddUnavailableSources(List<NativeMemorySource> sources, string directory, NativeMemorySourceKind kind,
        NativeMemoryReadStatus status, string[] cited, bool projectScoped)
    {
        if (cited.Length == 0 && status != NativeMemoryReadStatus.Missing)
            sources.Add(NativeMemoryFiles.Unavailable(directory, kind, status));
        foreach (var name in cited.Take(MaxSourcesPerDirectory).Order(NativeMemoryFiles.PathComparer))
            sources.Add(NativeMemoryFiles.Unavailable(Path.Combine(directory, name), kind, status));
        return cited.Length > MaxSourcesPerDirectory || (projectScoped && status != NativeMemoryReadStatus.Missing);
    }

    private static (IEnumerable<string> Names, NativeMemoryReadStatus Status, bool IsLimited) ScanDirectory(
        SafeFileHandle handle, bool projectScoped, CancellationToken token)
    {
        var names = new HashSet<string>(NativeMemoryFiles.PathComparer);
        var entryLimit = projectScoped ? MaxProjectDirectoryEntries : MaxDirectoryEntries;
        var entries = 0;
        try
        {
            foreach (var name in NativeMemoryFiles.DirectoryNames(handle, token))
            {
                if (++entries > entryLimit) return (names, NativeMemoryReadStatus.Available, true);
                if (!IsSourceName(name) || names.Contains(name)) continue;
                if (names.Count == MaxDirectoryEntries) return (names, NativeMemoryReadStatus.Available, true);
                names.Add(name);
            }
        }
        catch (Exception ex) when (NativeMemoryFiles.IsPathFailure(ex))
        {
            return ([], NativeMemoryFiles.StatusOf(ex), false);
        }

        return (names, NativeMemoryReadStatus.Available, false);
    }

    private static IEnumerable<string> ReadSummaryReferences(NativeMemorySource source)
    {
        if (source.Content is null) yield break;
        using var reader = new StringReader(source.Content);
        var inV1Sources = false;
        while (reader.ReadLine() is { } line)
        {
            if (source.IsTruncated && reader.Peek() == -1) yield break;
            if (line.StartsWith('#')) inV1Sources = line == "### rollout_summary_files";
            var match = SummaryReference.Match(line);
            if (!match.Success || (!match.Groups["qualified"].Success && !inV1Sources)) continue;
            var name = match.Groups["name"].Value;
            if (IsSourceName(name)) yield return name;
        }
    }

    private static bool IsSourceName(string name) => name.Length is > 3 and <= 240 && name[0] != '.' &&
        name.EndsWith(".md", StringComparison.OrdinalIgnoreCase) &&
        name.All(character => char.IsLetterOrDigit(character) || character is '.' or '-' or '_') &&
        !NativeMemoryFiles.IsDeviceName(name);

    private static NativeMemoryProjectMatch MatchProject(NativeMemorySource source, string? project)
    {
        if (source.Kind != NativeMemorySourceKind.RolloutSummary || source.Content is null || project is null)
            return NativeMemoryProjectMatch.Unknown;

        // The writer emits these fields before a blank line and the model-authored body.
        using var reader = new StringReader(source.Content);
        if (reader.ReadLine() is not { } thread || !thread.StartsWith("thread_id: ", StringComparison.Ordinal) ||
            !Guid.TryParseExact(thread.AsSpan("thread_id: ".Length), "D", out _) ||
            reader.ReadLine() is not { } update || !update.StartsWith("updated_at: ", StringComparison.Ordinal) ||
            !DateTimeOffset.TryParse(update.AsSpan("updated_at: ".Length), CultureInfo.InvariantCulture, DateTimeStyles.None, out _) ||
            reader.ReadLine()?.StartsWith("rollout_path: ", StringComparison.Ordinal) != true ||
            reader.ReadLine() is not { } cwd || !cwd.StartsWith("cwd: ", StringComparison.Ordinal))
            return NativeMemoryProjectMatch.Unknown;

        var next = reader.ReadLine();
        if (next?.StartsWith("git_branch: ", StringComparison.Ordinal) == true) next = reader.ReadLine();
        if (next != "") return NativeMemoryProjectMatch.Unknown;
        var recorded = NativeMemoryFiles.NormalizeLocalPath(cwd["cwd: ".Length..]);
        if (recorded is null) return NativeMemoryProjectMatch.Unknown;
        return NativeMemoryFiles.PathComparer.Equals(recorded, project) ? NativeMemoryProjectMatch.Matched : NativeMemoryProjectMatch.Different;
    }
}
