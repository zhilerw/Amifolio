namespace Amifolio.Core;

public sealed record ProjectRecord(string Id, string Name, string? RootPath, DateTimeOffset CreatedAt);

public sealed record ArtifactRecord(
    string Id, string Title, string OriginalName, string Kind, string Source,
    string? ProjectId, string Tags, string ObjectPath, string Hash, long Bytes,
    DateTimeOffset CreatedAt, int Version, string ExtractionStatus, string Preview, bool IsReference = false);

public sealed record ImportResult(ArtifactRecord Artifact, bool AlreadyExists, IReadOnlyList<string> Warnings);

public interface IPortfolioStore
{
    string DataRoot { get; }
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ArtifactRecord>> ListAsync(string? search = null, string? projectId = null, string? kind = null, CancellationToken cancellationToken = default);
    Task<ArtifactRecord?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ProjectRecord>> ProjectsAsync(CancellationToken cancellationToken = default);
    Task<ProjectRecord> CreateProjectAsync(string name, string? rootPath = null, CancellationToken cancellationToken = default);
    Task<ImportResult> ImportFileAsync(string path, string? projectId = null, string source = "用户", string tags = "", CancellationToken cancellationToken = default);
    Task<ImportResult> ImportRepositoryAsync(string path, string? projectId = null, string source = "用户", CancellationToken cancellationToken = default);
    Task UpdateArtifactAsync(string id, string title, string tags, string? projectId, CancellationToken cancellationToken = default);
    Task RemoveArtifactAsync(string id, CancellationToken cancellationToken = default);
    Task<string> GetOriginalPathAsync(string id, CancellationToken cancellationToken = default);
    Task<ImportResult> SaveGeneratedAsync(string name, string content, string? projectId = null, CancellationToken cancellationToken = default);
}
