using Amifolio.Context;
using Amifolio.Core;

namespace Amifolio.Bridge;

internal sealed class BridgeException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

internal sealed partial class BridgeOperations(PortfolioStore store)
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
        // Revalidate on each request in long-lived MCP sessions; revoked roots cannot remain authorized.
        await store.RefreshCodexProjectsAsync();
        if (projectId is null) return;
        Text(projectId, "projectId", 128);
        if (!(await store.ProjectsAsync()).Any(p => p.Id == projectId))
            throw new BridgeException("project_not_found", "The exact project ID does not exist; no global fallback was used.");
    }

    public async Task<object> SubmitAsync(string path, string? projectId, string source, string tags)
    {
        Text(path, "path", 4096);
        if (!Path.IsPathFullyQualified(path))
            throw new BridgeException("invalid_arguments", "path must be an absolute file path inside a discovered project.");
        Text(source, "source", 128);
        Text(tags, "tags", 1024, true);
        await ValidateProjectAsync(projectId);
        // source/tags remain validated compatibility arguments, not authority to edit project metadata.
        // Core infers only a currently discovered project; registration reads metadata, never parses/copies.
        var result = await store.ReferenceFileAsync(path, projectId);
        var committed = (await store.ListAsync()).FirstOrDefault(a => a.Id == result.Artifact.Id && a.IsReference)
            ?? throw new BridgeException("commit_unverified", "The source reference record cannot be verified.");
        var project = store.CodexProjects.FirstOrDefault(p => p.Id == committed.ProjectId)
            ?? throw new BridgeException("project_not_found", "The source project is no longer discovered.");
        var original = await store.Catalog.ValidateFileAsync(project, committed.ObjectPath);
        return new { id = committed.Id, path = original, status = "referenced" };
    }

    public async Task<object> StatusAsync(string id)
    {
        Text(id, "id", 128);
        await store.RefreshCodexProjectsAsync();
        // ListAsync refreshes metadata only. GetAsync may hash an explicitly parsed source;
        // status must not turn into an implicit original-content read.
        var record = (await store.ListAsync()).FirstOrDefault(a => a.Id == id && a.IsReference)
            ?? throw new BridgeException("not_found", "Artifact not found.");
        var status = record.ExtractionStatus.Contains("缺失", StringComparison.Ordinal) ? "missing"
            : record.ExtractionStatus.Contains("变化", StringComparison.Ordinal) ? "changed" : "referenced";
        try
        {
            var project = store.CodexProjects.First(p => p.Id == record.ProjectId);
            await store.Catalog.ValidateFileAsync(project, record.ObjectPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (status != "missing") status = "unavailable";
        }
        return new { id = record.Id, status, extractionStatus = Clip(record.ExtractionStatus, 128), version = record.Version };
    }

    public async Task<object> SearchAsync(string? search, string? projectId, int limit = MaxResults)
    {
        if (search is not null) Text(search, "search", 1024, true);
        if (limit is < 1 or > MaxResults)
            throw new BridgeException("invalid_arguments", $"limit must be 1..{MaxResults}.");
        await ValidateProjectAsync(projectId);
        var records = (await store.ListAsync(projectId: projectId))
            .Where(a => a.IsReference && (projectId is null || a.ProjectId == projectId))
            .Where(a => string.IsNullOrEmpty(search) || string.Join('\n', a.Title, a.OriginalName, a.Tags, a.Source).Contains(search, StringComparison.OrdinalIgnoreCase))
            .Take(limit + 1).ToArray();
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
        await store.RefreshCodexProjectsAsync();
        var projects = (await store.ProjectsAsync()).Take(MaxResults + 1).ToArray();
        return new
        {
            projects = projects.Take(MaxResults).Select(p => new { id = Clip(p.Id, 128), name = Clip(p.Name, 256), createdAt = p.CreatedAt }).ToArray(),
            truncated = projects.Length > MaxResults, limit = MaxResults
        };
    }

    public async Task<object> ContextAsync(string? projectId, string layer = "overview")
    {
        if (layer is not ("overview" or "memory" or "style" or "summary" or "preferences") || layer=="preferences" && projectId is not null)
            throw new BridgeException("invalid_arguments", "Use a known context layer; preferences is global-only.");
        await ValidateProjectAsync(projectId);
        if(LearningService.LegacyRetired)
        {
            var requested=layer=="preferences"?"memory":layer;
            var current=await LearningService.ReadLearningAsync(projectId,requested,"",LearningHome);
            return new { enabled=(await LearningService.Learning.PolicyAsync(projectId)).Share,scope=projectId is null?"global":"project",projectId,layer,documents=current,truncated=false };
        }
        // Read only the requested scope. Never call PublishAsync, read arbitrary paths, or expose the source index.
        var snapshot = await new ContextService(store).ReadAsync(projectId);
        var documents = new Dictionary<string, string>(StringComparer.Ordinal);
        var truncated = false;
        if (snapshot.Enabled)
        {
            var names = layer == "overview" ? new[] { "START.md", "overview.md" } : new[] { layer + ".md" };
            foreach (var name in names)
                // CandidateDocuments are unconfirmed suggestions and are never exposed by this bridge.
                if (!snapshot.DisabledDocuments.Contains(name) && snapshot.Documents.TryGetValue(name, out var content))
                {
                    var maximum = name == "START.md" ? 4096 : 12000;
                    documents[name] = Clip(content, maximum);
                    truncated |= content.Length > maximum;
                }
        }
        if(snapshot.Enabled&&layer!="preferences")
        {
            var learning=await LearningService.ReadLearningAsync(projectId,layer,"",LearningHome);
            foreach(var(name,text)in learning){documents["learned-"+name]=Clip(text,6000);truncated|=text.Length>6000;}
        }
        return new { enabled = snapshot.Enabled, scope = projectId is null ? "global" : "project", projectId, layer, documents, truncated };
    }
}
