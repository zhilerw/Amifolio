namespace Amifolio.Context;

/// <summary>Private provenance. Never include these fields in shared preference Markdown.</summary>
public sealed record PreferenceSource(string Id, string Hash, int Version, string Checksum);

public sealed record PreferenceEntry(
    string Id, string Text, string ProjectId, string SourceId, string SourceHash, int SourceVersion,
    DateTimeOffset CreatedAt, DateTimeOffset? ConfirmedAt, string Status, bool Enabled)
{
    public IReadOnlyList<PreferenceSource> Sources { get; init; } = Array.Empty<PreferenceSource>();
    public string? CandidateName { get; init; }
    public string? CandidateHash { get; init; }
    public string ProposalKey { get; init; } = "";
    public long ParticipationRevision { get; init; }
}

public sealed record PreferenceSnapshot(IReadOnlyList<PreferenceEntry> Entries);
