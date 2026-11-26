using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Amifolio.Core;

public sealed record CodexProject(string Id, string Name, string RootPath, int SessionCount);
public sealed record CatalogEntry(string Path, string Name, bool IsDirectory, string Kind, long Bytes, DateTimeOffset ModifiedAt, string State);
public sealed record CatalogResult<T>(IReadOnlyList<T> Items, bool Truncated, IReadOnlyList<string> Warnings);

/// <summary>
/// Read-only adapter for local Codex metadata, not an official host protocol. Directory scans
/// read attributes only; no project content, index, copy, host process or watcher is created.
/// Browse/validation require an ID/root pair from this instance's last completed DiscoverAsync.
/// Refresh replaces that allowlist; a cancelled discovery leaves the prior snapshot intact.
/// Explicit project registries are authoritative. Session-only discovery is a labeled fallback,
/// not a claim to reproduce the official Codex sidebar. Revalidate before opening a file.
/// </summary>
public sealed partial class CodexCatalog
{
    public const int MaxMetadataBytes = 4 * 1024 * 1024;
    public const int MaxSessionHeaderBytes = 256 * 1024;
    public const int MaxSessionFiles = 2048;
    public const int MaxEntries = 5000;
    public const int MaxProjects = 2048;
    public const int MaxVisitedEntries = 10000;
    public const long MaxReadBytes = 32L * 1024 * 1024;
    public const long MaxFileBytes = ArchiveSafety.MaxFileBytes;
    private static readonly TimeSpan OperationLimit = TimeSpan.FromSeconds(20);
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 32 };
    private static readonly HashSet<string> ExcludedDirectories = new(Paths)
    {
        ".git", ".hg", ".svn", ".codex", ".agents", ".claude", ".ssh", ".aws", ".azure", ".gnupg", ".kube", ".docker", ".config",
        "secrets", ".secrets", "credentials", ".credentials", "keys", ".keys", "config", "node_modules", "bin", "obj", "venv", ".venv",
        "__pycache__", ".cache", "cache", "temp", "tmp", "__amifolio__", "context-state", ".superpowers"
    };
    private static readonly HashSet<string> ExcludedFiles = new(Paths)
    {
        "auth.json", "config.json", "config.toml", "config.yaml", "config.yml", "settings.json",
        "AGENTS.md", "CLAUDE.md", "GEMINI.md", ".codex-global-state.json", ".gitconfig", ".git-credentials",
        "appsettings.json", "launchSettings.json"
    };
    private readonly string home;
    private readonly string dataRoot;
    internal Func<Environment.SpecialFolder,string> FolderPathResolver { get; init; }=folder=>Environment.GetFolderPath(folder);
    internal Func<string> TempPathResolver { get; init; }=Path.GetTempPath;
    // Published once per completed discovery and never mutated after publication.
    private IReadOnlyDictionary<string, string> allowedProjects = new Dictionary<string, string>(Paths);

    public CodexCatalog(string codexHome, string? appDataRoot = null)
    {
        home = LocalAbsolute(codexHome);
        dataRoot = LocalAbsolute(appDataRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Amifolio"));
    }

    /// <summary>Discovers registry roots, or labeled session fallback roots if no registry is available. Counts are bounded.</summary>
    public Task<CatalogResult<CodexProject>> DiscoverAsync(CancellationToken ct = default) =>
        Task.Run(() => DiscoverCoreAsync(ct), ct);

    /// <summary>Lists exactly the immediate children. Directory must be an absolute path within the project.</summary>
    public Task<CatalogResult<CatalogEntry>> BrowseAsync(CodexProject project, string? directory = null, CancellationToken ct = default) =>
        Task.Run(() => BrowseCore(project, directory, ct), ct);

    /// <summary>
    /// Returns the normalized absolute path or throws IOException for an unsafe, missing,
    /// unreadable, oversized or non-regular file. Opens only an allowed read handle; reads no bytes.
    /// A successful check is not a lease: the eventual reader must also guard against path changes.
    /// </summary>
    public Task<string> ValidateFileAsync(CodexProject project, string path, CancellationToken ct = default) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var policy=CapturePathPolicy();
        var root = ProjectRoot(project,policy);
        var full = ScopedPath(root, path,policy);
        var entry = Describe(full,policy);
        if (entry.IsDirectory || entry.State != "ready") throw new IOException("只能读取项目内可用的普通文件：" + full);
        try
        {
            // Secret/config/link exclusions are checked before any file handle is opened.
            using var handle = ArchiveSafety.OpenRead(full);
            if (handle.Length > MaxFileBytes) throw new IOException("文件超过大小上限。");
            ct.ThrowIfCancellationRequested();
            return full;
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("文件不可读：" + full, ex); }
    }, ct);

    private async Task<CatalogResult<CodexProject>> DiscoverCoreAsync(CancellationToken ct)
    {
        var policy=CapturePathPolicy();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(OperationLimit);
        var budget = new Budget(ct, timeout.Token);
        var projects = new Dictionary<string, CodexProject>(Paths);
        var recognized = false;
        var hasRegistry = false;
        var ignoredSessions = 0;

        void AddRoot(string root, string? name, bool session)
        {
            if (!budget.Continue()) return;
            try
            {
                root = ValidateRoot(root,policy);
                if (projects.TryGetValue(root, out var existing))
                {
                    if (session) projects[root] = existing with { SessionCount = existing.SessionCount + 1 };
                    return;
                }
                if (projects.Count >= MaxProjects) { budget.Limit("项目数量达到上限，发现结果不完整。"); return; }
                var id = "codex-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(root.ToUpperInvariant())));
                projects.Add(root, new(id, string.IsNullOrWhiteSpace(name) ? Path.GetFileName(root) : name, root, session ? 1 : 0));
            }
            catch (Exception ex) when (PathFailure(ex)) { budget.Warn("跳过无效、缺失或受限项目根目录。"); }
        }

        try
        {
            ArchiveSafety.NoReparse(home);
            if (!Directory.Exists(home))
            {
                budget.Warn("CODEX_HOME 不存在或不可访问，未进行项目发现。");
                ct.ThrowIfCancellationRequested();
                return Finish();
            }
            var statePath = Path.Combine(home, ".codex-global-state.json");
            // Missing state is normal for session-only installations. Other failures remain visible.
            try
            {
                var bytes = await ReadMetadataAsync(statePath, MaxMetadataBytes, firstLine: false, budget).ConfigureAwait(false);
                if (bytes is not null)
                {
                    using var document = JsonDocument.Parse(bytes, JsonOptions);
                    var state = document.RootElement;
                    if (state.ValueKind != JsonValueKind.Object) budget.Warn("Codex 项目元数据不是可识别的对象。");
                    else
                    {
                        if (state.TryGetProperty("local-projects", out var current))
                        {
                            // Presence, including an empty or unsupported registry, prevents a
                            // historical-session union. Unsupported shapes fail closed with warnings.
                            hasRegistry = true;
                            if (current.ValueKind != JsonValueKind.Object) budget.Warn("无法识别 local-projects 格式。");
                            else
                            {
                                recognized = true;
                                var rows = 0;
                                foreach (var row in current.EnumerateObject())
                                {
                                    if (!budget.Continue()) break;
                                    if (++rows > MaxVisitedEntries) { budget.Limit("项目元数据条目达到上限。"); break; }
                                    if (row.Value.ValueKind != JsonValueKind.Object ||
                                        !row.Value.TryGetProperty("rootPaths", out var roots) || roots.ValueKind != JsonValueKind.Array)
                                    { budget.Warn("跳过无法识别的 local-projects 项目条目。"); continue; }
                                    var name = row.Value.TryGetProperty("name", out var label) && label.ValueKind == JsonValueKind.String ? label.GetString() : null;
                                    foreach (var root in roots.EnumerateArray())
                                    {
                                        if (!budget.Continue()) break;
                                        if (++rows > MaxVisitedEntries) { budget.Limit("项目元数据路径达到上限。"); break; }
                                        if (root.ValueKind == JsonValueKind.String) AddRoot(root.GetString()!, name, false);
                                        else budget.Warn("跳过非字符串项目根路径。");
                                    }
                                }
                            }
                        }
                        // Current registry takes precedence over stale roots retained by older hosts.
                        if (!hasRegistry && state.TryGetProperty("electron-saved-workspace-roots", out var legacy))
                        {
                            hasRegistry = true;
                            if (legacy.ValueKind != JsonValueKind.Array) budget.Warn("无法识别旧版工作区根目录格式。");
                            else
                            {
                                recognized = true;
                                var labels = new Dictionary<string, string>(Paths);
                                if (state.TryGetProperty("electron-workspace-root-labels", out var savedLabels))
                                {
                                    if (savedLabels.ValueKind != JsonValueKind.Object) budget.Warn("无法识别旧版工作区标签格式。");
                                    else
                                    {
                                        var count = 0;
                                        foreach (var label in savedLabels.EnumerateObject())
                                        {
                                            if (!budget.Continue()) break;
                                            if (++count > MaxVisitedEntries) { budget.Limit("工作区标签达到上限。"); break; }
                                            if (label.Value.ValueKind == JsonValueKind.String)
                                            {
                                                try { labels[LocalAbsolute(label.Name)] = label.Value.GetString()!; }
                                                catch (Exception ex) when (PathFailure(ex)) { budget.Warn("跳过无效的旧版标签路径。"); }
                                            }
                                            else budget.Warn("跳过无法识别的旧版工作区标签。");
                                        }
                                    }
                                }
                                var countRoots = 0;
                                foreach (var root in legacy.EnumerateArray())
                                {
                                    if (!budget.Continue()) break;
                                    if (++countRoots > MaxVisitedEntries) { budget.Limit("旧版工作区根目录达到上限。"); break; }
                                    if (root.ValueKind != JsonValueKind.String) { budget.Warn("跳过非字符串旧版工作区根目录。"); continue; }
                                    var value = root.GetString()!;
                                    string? label = null;
                                    try { labels.TryGetValue(LocalAbsolute(value), out label); }
                                    catch (Exception ex) when (PathFailure(ex)) { }
                                    AddRoot(value, label, false);
                                }
                            }
                        }
                        if (!recognized) budget.Warn(hasRegistry
                            ? "项目注册表格式不可识别；不会将历史会话目录补作项目。"
                            : "Codex 项目元数据格式不可识别；将尝试会话首行回退。");
                    }
                }
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception ex) when (PathFailure(ex) || ex is JsonException)
            { budget.Warn(hasRegistry ? "项目注册表未能完整读取；不会补入历史会话目录。" : "Codex 项目元数据损坏、受限或不可读；将尝试会话首行回退。"); }

            if (!hasRegistry) budget.Warn("未读取到项目注册表；当前使用会话首行 cwd 目录回退，不是精确的 Codex 官方侧栏项目结构。");
            CodexProject[] registryRoots = hasRegistry ? projects.Values.OrderByDescending(p => p.RootPath.Length).ThenBy(p => p.RootPath, Paths).ToArray() : [];

            void CountSession(string cwd)
            {
                if (!hasRegistry) { AddRoot(cwd, null, true); return; }
                string full;
                try { full = LocalAbsolute(cwd); }
                catch (Exception ex) when (PathFailure(ex)) { ignoredSessions++; return; }
                // Metadata association only: never browse or open the cwd of an unrelated session.
                var nearest = registryRoots.FirstOrDefault(p => Within(full, p.RootPath));
                if (nearest is null) { ignoredSessions++; return; }
                var current = projects[nearest.RootPath];
                projects[nearest.RootPath] = current with { SessionCount = current.SessionCount + 1 };
            }

            // Only this approved subtree is traversed, never auth/config/state databases or dialogue records.
            var sessions = Path.Combine(home, "sessions");
            var stack = new Stack<(string Path, int Depth)>();
            try
            {
                ArchiveSafety.NoReparse(sessions);
                if (Directory.Exists(sessions)) stack.Push((sessions, 0));
            }
            catch (Exception ex) when (PathFailure(ex)) { budget.Warn("会话目录是链接或不可访问，已跳过。"); }
            var visited = 0; var files = 0;
            while (stack.Count > 0 && budget.Continue())
            {
                var (directory, depth) = stack.Pop();
                try
                {
                    ArchiveSafety.NoReparse(directory);
                    foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                    {
                        if (!budget.Continue()) break;
                        if (++visited > MaxVisitedEntries) { budget.Limit("会话目录条目达到上限，计数可能不完整。"); stack.Clear(); break; }
                        try
                        {
                            var attrs = File.GetAttributes(path);
                            if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                            { budget.Warn("跳过会话目录中的链接或设备：" + path); continue; }
                            if ((attrs & FileAttributes.Directory) != 0)
                            {
                                if (ExcludedDirectories.Contains(Path.GetFileName(path))) continue;
                                if (depth >= 8) budget.Limit("会话目录深度达到上限。");
                                else stack.Push((path, depth + 1));
                                continue;
                            }
                            if (!Path.GetExtension(path).Equals(".jsonl", StringComparison.OrdinalIgnoreCase) || ExcludedName(Path.GetFileName(path))) continue;
                            if (++files > MaxSessionFiles) { budget.Limit("会话文件数量达到上限，计数可能不完整。"); stack.Clear(); break; }
                            var header = await ReadMetadataAsync(path, MaxSessionHeaderBytes, firstLine: true, budget).ConfigureAwait(false);
                            if (header is null) continue;
                            using var document = JsonDocument.Parse(header, JsonOptions);
                            var meta = document.RootElement;
                            if (meta.ValueKind == JsonValueKind.Object && meta.TryGetProperty("type", out var type) &&
                                type.ValueKind == JsonValueKind.String && type.GetString() == "session_meta" &&
                                meta.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object &&
                                payload.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String)
                            {
                                recognized = true;
                                CountSession(cwd.GetString()!);
                            }
                            else budget.Warn("无法识别会话首行 session_meta/cwd：" + path);
                        }
                        catch (Exception ex) when (PathFailure(ex) || ex is JsonException)
                        { budget.Warn("跳过无效或不可读的会话首行：" + path); }
                    }
                }
                catch (Exception ex) when (PathFailure(ex)) { budget.Warn("跳过不可访问的会话子目录：" + directory); }
            }
            if (!recognized && !hasRegistry) budget.Warn("未找到可识别的 Codex 项目或会话元数据；这不表示项目列表已确认为空。");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { budget.Limit("项目发现达到时间上限，结果不完整。"); }
        catch (Exception ex) when (PathFailure(ex)) { budget.Warn("CODEX_HOME 是链接、受限或不可读目录，未完成项目发现。"); }
        ct.ThrowIfCancellationRequested();
        return Finish();

        CatalogResult<CodexProject> Finish()
        {
            // One bounded summary, never the ignored cwd values, including on timeout.
            if (ignoredSessions > 0) budget.Warn($"已忽略 {ignoredSessions} 个未关联已注册项目的会话目录；未将其作为新项目。");
            var items = projects.Values.OrderBy(p => p.Name, Paths).ThenBy(p => p.RootPath, Paths).ThenBy(p => p.RootPath, StringComparer.Ordinal).ToArray();
            IReadOnlyDictionary<string, string> snapshot = items.ToDictionary(p => p.RootPath, p => p.Id, Paths);
            Volatile.Write(ref allowedProjects, snapshot);
            return new(items, budget.Truncated, budget.Warnings());
        }
    }

    private CatalogResult<CatalogEntry> BrowseCore(CodexProject project, string? directory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var policy=CapturePathPolicy();
        var root = ProjectRoot(project,policy);
        var target = ScopedPath(root, directory ?? root,policy);
        RequireDirectory(target);
        var entries = new List<CatalogEntry>();
        var budget = new Budget(ct, ct);
        try
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(target))
            {
                if (!budget.Continue()) break;
                if (entries.Count >= MaxEntries) { budget.Limit("当前目录条目达到上限，请缩小范围或稍后刷新。"); break; }
                var entry = Describe(path,policy);
                entries.Add(entry);
                if (entry.State is "missing" or "unreadable") budget.Warn("目录条目缺失或不可读：" + path);
            }
        }
        catch (Exception ex) when (PathFailure(ex)) { budget.Warn("当前目录未能完整读取，请刷新后重试。"); budget.Truncated = true; }
        ct.ThrowIfCancellationRequested();
        return new(entries.OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, Paths).ThenBy(e => e.Path, StringComparer.Ordinal).ToArray(), budget.Truncated, budget.Warnings());
    }

    private string ProjectRoot(CodexProject project,PathPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(project);
        var root = LocalAbsolute(project.RootPath);
        var snapshot = Volatile.Read(ref allowedProjects);
        if (!snapshot.TryGetValue(root, out var id) || !string.Equals(id, project.Id, StringComparison.Ordinal))
            throw new IOException("项目不在此目录服务最近一次发现的范围内，请先刷新项目。");
        return ValidateRoot(root,policy);
    }

    private string ValidateRoot(string path,PathPolicy policy)
    {
        try
        {
            var root = LocalAbsolute(path);
            if (root.Equals(Path.GetPathRoot(root), StringComparison.OrdinalIgnoreCase)) throw new IOException("不能浏览磁盘根目录。");
            if (root.Equals(policy.TemporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new IOException("不能将系统临时根目录作为项目。");
            CheckProtectedPath(root,policy);
            ArchiveSafety.NoReparse(root);
            RequireDirectory(root);
            return root;
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("项目目录不可读。", ex); }
    }

    private string ScopedPath(string root, string path,PathPolicy policy)
    {
        try
        {
            var full = LocalAbsolute(path);
            if (!Within(full, root)) throw new IOException("目录或文件越过项目范围。");
            CheckProtectedPath(full,policy);
            ArchiveSafety.NoReparse(full);
            return full;
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("目录或文件不可读。", ex); }
    }

    private sealed record PathPolicy(string[] Subtrees,string[] ExactRoots,string TemporaryRoot);
    private PathPolicy CapturePathPolicy()
    {
        string[] Resolve(Environment.SpecialFolder[] folders)=>folders.Select(folder=>FolderPathResolver(folder)).Where(path=>!string.IsNullOrEmpty(path)).Select(path=>Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))).ToArray();
        return new(Resolve([Environment.SpecialFolder.Windows,Environment.SpecialFolder.System,Environment.SpecialFolder.ProgramFiles,Environment.SpecialFolder.ProgramFilesX86,Environment.SpecialFolder.CommonApplicationData]),
            Resolve([Environment.SpecialFolder.UserProfile,Environment.SpecialFolder.LocalApplicationData,Environment.SpecialFolder.ApplicationData]),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(TempPathResolver())));
    }
    private void CheckProtectedPath(string path,PathPolicy policy)
    {
        if (ArchiveSafety.ManagedPath(path, dataRoot)) throw new IOException("不能浏览应用自己的数据或恢复资料。");
        if (Within(path, home) && !Within(path, Path.Combine(home, "worktrees"))) throw new IOException("不能浏览 Codex 配置和会话目录。");
        foreach (var system in policy.Subtrees)
        {
            if (Within(path, system)) throw new IOException("不能浏览 Windows 或系统管理目录。");
        }
        foreach (var system in policy.ExactRoots)
        {
            if (path.Equals(system, StringComparison.OrdinalIgnoreCase))
                throw new IOException("不能将用户或应用数据根目录作为项目。");
        }
        // Walk all ancestors so selecting an excluded directory's child cannot bypass exclusion.
        // Codex-managed worktrees are the sole exception to the .codex ancestor exclusion.
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            if (current.Equals(home, StringComparison.OrdinalIgnoreCase) && Within(path, Path.Combine(home, "worktrees"))) continue;
            // Real projects may live in the OS temporary directory. Only that exact ancestor is
            // exempt; selecting a cache/temp subtree inside a project is still forbidden.
            if (current.Equals(policy.TemporaryRoot, StringComparison.OrdinalIgnoreCase)) continue;
            if (ExcludedName(Path.GetFileName(current))) throw new IOException("路径包含凭据、配置、缓存或保留目录。");
        }
    }

    private CatalogEntry Describe(string path,PathPolicy policy)
    {
        var name = Path.GetFileName(path);
        var isDirectory = false;
        try
        {
            var attrs = File.GetAttributes(path);
            isDirectory = (attrs & FileAttributes.Directory) != 0;
            if ((attrs & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 || ExcludedName(name) || ArchiveSafety.ManagedPath(path, dataRoot) ||
                (Within(path, home) && !Within(path, Path.Combine(home, "worktrees")))) return Entry("excluded");
            if ((attrs & FileAttributes.Offline) != 0) return Entry("unreadable");
            CheckProtectedPath(path,policy);
            ArchiveSafety.NoReparse(path);
            var modified = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
            var bytes = isDirectory ? 0 : new FileInfo(path).Length;
            return new(path, name, isDirectory, isDirectory ? "目录" : TextExtraction.ClassifyName(name), bytes, modified, bytes > MaxFileBytes ? "too_large" : "ready");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return Entry("missing"); }
        catch (Exception ex) when (PathFailure(ex)) { return Entry("unreadable"); }
        CatalogEntry Entry(string state) => new(path, name, isDirectory, isDirectory ? "目录" : TextExtraction.ClassifyName(name), 0, default, state);
    }

    private static bool ExcludedName(string name) => ExcludedDirectories.Contains(name) || ExcludedFiles.Contains(name) || RepositorySnapshot.IsSecret(name) ||
        name.StartsWith("auth.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("config.", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("appsettings.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("context-", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("~$", StringComparison.Ordinal) || new[] { ".ini", ".config", ".toml", ".tmp", ".partial", ".bak", ".lock", ".swp" }.Contains(Path.GetExtension(name), Paths);

    private static string LocalAbsolute(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal) ||
            (OperatingSystem.IsWindows() && (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] is not ('\\' or '/'))))
            throw new IOException("需要本机绝对路径，不支持网络、设备或相对路径。");
        if (OperatingSystem.IsWindows() && path.Split('\\', '/').Skip(1).Any(part => part is not ("." or "..") && (part.EndsWith('.') || part.EndsWith(' '))))
            throw new IOException("不能使用末尾带点或空格的路径别名。");
        try { return ArchiveSafety.LocalPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { throw new IOException("无效的本机路径。", ex); }
    }

    private static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void RequireDirectory(string path)
    {
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & FileAttributes.Directory) == 0 || (attrs & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new IOException("项目或浏览目标必须是真实目录。");
        }
        catch (UnauthorizedAccessException ex) { throw new IOException("目录不可读。", ex); }
    }

    private static bool PathFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

    private static async Task<byte[]?> ReadMetadataAsync(string path, int maximum, bool firstLine, Budget budget)
    {
        if (!budget.Continue()) return null;
        await using var stream = ArchiveSafety.OpenRead(path);
        if (firstLine) return await ReadFirstLineAsync(stream, maximum, budget).ConfigureAwait(false);
        if (stream.Length > maximum) { budget.Limit("项目元数据超过大小上限，已跳过。"); return null; }
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        while (budget.Continue())
        {
            var capacity = Math.Min(buffer.Length, maximum + 1 - checked((int)output.Length));
            var remaining = MaxReadBytes - budget.ReadBytes;
            if (remaining <= 0) { budget.Limit("元数据读取总量达到上限。"); return null; }
            var read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(capacity, remaining)), budget.Token).ConfigureAwait(false);
            budget.ReadBytes += read;
            if (read == 0) return CleanBom(output.ToArray());
            if (output.Length + read > maximum) { budget.Limit("项目元数据超过大小上限，已跳过。"); return null; }
            output.Write(buffer, 0, read);
        }
        return null;
    }

    /// <summary>
    /// Reads exactly through the first LF (or EOF), never requesting dialogue bytes. File reads
    /// use one-byte RandomAccess ranges on the already path-validated handle: FileStream's
    /// buffered Read/ReadByte APIs must not be used, even with a one-byte destination.
    /// Runs on discovery's background task; cancellation/time/total-byte limits are checked per byte.
    /// </summary>
    internal static async Task<byte[]?> ReadFirstLineAsync(Stream input, int maximum, Budget budget)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        using var output = new MemoryStream();
        var one = new byte[1];
        var file = input as FileStream;
        var offset = file?.Position ?? 0;
        try
        {
            while (budget.Continue())
            {
                if (budget.ReadBytes >= MaxReadBytes) { budget.Limit("元数据读取总量达到上限。"); return null; }
                // RandomAccess bypasses FileStream's 64 KiB buffer entirely and reads exactly
                // the requested one-byte range. A synchronous single-byte operation keeps the
                // long-header path finite without one asynchronous OS completion per byte.
                var read = file is null
                    ? await input.ReadAsync(one.AsMemory(), budget.Token).ConfigureAwait(false)
                    : RandomAccess.Read(file.SafeFileHandle, one.AsSpan(), offset);
                offset += read;
                budget.ReadBytes += read;
                if (read == 0 || one[0] == (byte)'\n') return CleanBom(output.ToArray());
                if (output.Length >= maximum) { budget.Limit("会话首行超过大小上限，已跳过。"); return null; }
                output.WriteByte(one[0]);
            }
            return null;
        }
        finally { if (file is not null) file.Position = offset; }
    }

    private static byte[] CleanBom(byte[] bytes) => bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? bytes[3..] : bytes;

    internal sealed class Budget(CancellationToken caller, CancellationToken token)
    {
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly List<string> warnings = [];
        private int suppressed;
        internal CancellationToken Token => token;
        internal long ReadBytes;
        internal bool Truncated;
        internal bool Continue()
        {
            caller.ThrowIfCancellationRequested();
            if (elapsed.Elapsed < OperationLimit && !token.IsCancellationRequested) return true;
            Limit("操作达到时间上限，结果不完整。");
            return false;
        }
        internal void Limit(string warning) { Truncated = true; Warn(warning); }
        internal void Warn(string warning)
        {
            if (warnings.Count < 128) warnings.Add(warning);
            else suppressed++;
        }
        internal IReadOnlyList<string> Warnings() => suppressed == 0 ? warnings.ToArray() : [.. warnings, $"另有 {suppressed} 条警告未显示。"];
    }
}
