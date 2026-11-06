namespace Amifolio.Context;

public sealed record ContextSnapshot(bool Enabled, string Directory, IReadOnlyDictionary<string, string> Documents)
{
    public bool OriginalAccessAllowed { get; init; }
    public IReadOnlyDictionary<string, string> CandidateDocuments { get; init; } = new Dictionary<string, string>();
}

public sealed record ModelOptions(string Endpoint, string Model, bool RemoteAllowed);
public sealed record AgentsFileSnapshot(string Path, string Content, string Hash, bool Exists);
