using Amifolio.Context;
using Amifolio.Core;

namespace Amifolio.Bridge;

internal sealed class BridgeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed class BridgeOperations(IPortfolioStore store)
{
    public const int MaxResults = 50;
    public static string Text(string value, string name, int maximum, bool allowEmpty = false)
    {
        if ((!allowEmpty && string.IsNullOrWhiteSpace(value)) || value.Length > maximum || value.Contains('\0'))
            throw new BridgeException("invalid_arguments", $"{name} must contain {(allowEmpty ? "0" : "1")}..{maximum} characters and no NUL.");
        return value;
    }

    private static string Clip(string? value, int maximum) => (value ?? "")[..Math.Min(value?.Length ?? 0, maximum)];

    public async Task ValidateProjectAsync(string? projectId)
    {
        if (projectId is null) return;
        Text(projectId, "projectId", 128);
        if (!(await store.ProjectsAsync()).Any(p => p.Id == projectId))
            throw new BridgeException("project_not_found", "The exact project ID does not exist; no global fallback was used.");
    }

    public async Task<object> SubmitAsync(string path, string? projectId, string source, string tags, bool repository = false)
    {
        Text(path, "path", 4096);
        Text(source, "source", 128);
        Text(tags, "tags", 1024, true);
        await ValidateProjectAsync(projectId);
        // Import completion is the Core commit boundary. Do not acknowledge before it.
        var result = repository
            ? await store.ImportRepositoryAsync(path, projectId, source)
            : await store.ImportFileAsync(path, projectId, source, tags);
        var committed = await store.GetAsync(result.Artifact.Id)
            ?? throw new BridgeException("commit_unverified", "The imported record cannot be verified.");
        var original = await store.GetOriginalPathAsync(committed.Id);
        if (!File.Exists(original))
            throw new BridgeException("commit_unverified", "The committed archive copy cannot be verified.");
        return new { id = committed.Id, path = original, status = "archived" };
    }

    public async Task<object> StatusAsync(string id)
    {
        Text(id, "id", 128);
        var record = await store.GetAsync(id)
            ?? throw new BridgeException("not_found", "Artifact not found.");
        var path = await store.GetOriginalPathAsync(record.Id);
        return new { id = record.Id, status = File.Exists(path) ? "archived" : "missing", extractionStatus = Clip(record.ExtractionStatus, 128), version = record.Version };
    }

    public async Task<object> SearchAsync(string? search, string? projectId, int limit = MaxResults)
    {
        if (search is not null) Text(search, "search", 1024, true);
        if (limit is < 1 or > MaxResults)
            throw new BridgeException("invalid_arguments", $"limit must be 1..{MaxResults}.");
        await ValidateProjectAsync(projectId);
        var records = (await store.ListAsync(search, projectId))
            .Where(a => projectId is null || a.ProjectId == projectId).Take(limit + 1).ToArray();
        return new
        {
            artifacts = records.Take(limit).Select(a => new
            {
                id = Clip(a.Id, 128), title = Clip(a.Title, 256), originalName = Clip(a.OriginalName, 256),
                kind = Clip(a.Kind, 64), source = Clip(a.Source, 128), projectId = a.ProjectId is null ? null : Clip(a.ProjectId, 128),
                tags = Clip(a.Tags, 512), bytes = a.Bytes, createdAt = a.CreatedAt, version = a.Version,
                extractionStatus = Clip(a.ExtractionStatus, 128)
            }).ToArray(),
            truncated = records.Length > limit, limit
        };
    }

    public async Task<object> ProjectsAsync()
    {
        var projects = (await store.ProjectsAsync()).Take(MaxResults + 1).ToArray();
        return new
        {
            projects = projects.Take(MaxResults).Select(p => new { id = Clip(p.Id, 128), name = Clip(p.Name, 256), createdAt = p.CreatedAt }).ToArray(),
            truncated = projects.Length > MaxResults, limit = MaxResults
        };
    }

    public async Task<object> ContextAsync(string? projectId, string layer = "overview")
    {
        if (layer is not ("overview" or "memory" or "style" or "summary"))
            throw new BridgeException("invalid_arguments", "layer must be overview, memory, style or summary.");
        await ValidateProjectAsync(projectId);
        // Read only the requested scope. Never call PublishAsync, read arbitrary paths, or expose the source index.
        var snapshot = await new ContextService(store).ReadAsync(projectId);
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        var truncated = false;
        if (snapshot.Enabled)
        {
            var names = layer == "overview" ? new[] { "START.md", "overview.md" } : new[] { layer + ".md" };
            foreach (var name in names)
                // CandidateDocuments are unconfirmed suggestions and are never exposed by this bridge.
                if (snapshot.Documents.TryGetValue(name, out var content))
                {
                    var maximum = name == "START.md" ? 4096 : 12000;
                    documents[name] = Clip(content, maximum);
                    truncated |= content.Length > maximum;
                }
        }
        return new { enabled = snapshot.Enabled, scope = projectId is null ? "global" : "project", projectId, layer, documents, truncated };
    }
}
