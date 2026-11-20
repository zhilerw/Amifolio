namespace Amifolio.Context;

public sealed record ContextSnapshot(bool Enabled, string Directory, IReadOnlyDictionary<string, string> Documents)
{
    public bool OriginalAccessAllowed { get; init; }
    public IReadOnlyDictionary<string, string> CandidateDocuments { get; init; } = new Dictionary<string, string>();
    public IReadOnlyCollection<string> DisabledDocuments { get; init; } = Array.Empty<string>();
    public IReadOnlyCollection<string> DerivedDocuments { get; init; } = Array.Empty<string>();
}

public sealed record ContextAutomationOptions(bool LearningEnabled = false, bool SummaryEnabled = false, bool RemoteAllowed = false);
public sealed record AutomationStatus(ContextAutomationOptions Options, string Status, int RequestsToday, int DailyLimit, DateTimeOffset? LastAttempt);

public sealed record ModelOptions(string Endpoint, string Model, bool RemoteAllowed);
public sealed record AgentsFileSnapshot(string Path, string Content, string Hash, bool Exists);
