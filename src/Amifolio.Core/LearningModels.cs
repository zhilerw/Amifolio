namespace Amifolio.Core;

public sealed record LearningPolicy
{
    public bool Capture { get; init; }
    public bool Learn { get; init; }
    public bool AutoActivate { get; init; }
    public bool Share { get; init; }
    public bool Remote { get; init; }
    public bool OriginalPaths { get; init; }
    public bool ContributeGlobal { get; init; }
    public string Mode { get; init; }="chat";
    public int BatchSize { get; init; }=5;
    public string Endpoint { get; init; }="";
    public long Revision { get; init; }
    public DateTimeOffset? GrantedAt { get; init; }
}
public sealed record LearningOutput(string Name,string Purpose,string? ReferenceId=null,bool Verified=false);
public sealed record LearningCapture(string ProjectId,string SessionId,string TurnId,string Summary,string Outcome,
    IReadOnlyList<string> Quotes,IReadOnlyList<LearningOutput> Outputs);
public sealed record LearningEvidence(MemoryEvidence Evidence,string UserTextHash,string SourceFingerprint,bool SourceContextComplete=false);
// Created by the server after bounded host evidence validation; never deserialized from MCP arguments.
public sealed record LearningVerifiedTurn(string SessionId,string TurnId,string RootPath,string UserTextHash,string UserText,DateTimeOffset ObservedAt,string SourceFingerprint);
public sealed record LearningSummary(long Sequence,int Version,string ProjectId,string SessionId,string TurnId,
    string Summary,string Outcome,string State,DateTimeOffset RecordedAt,IReadOnlyList<string> EvidenceIds,IReadOnlyList<LearningOutput> Outputs);
public sealed record LearningReceipt(long Sequence,int Version,string State,int Pending,bool Duplicate);
public sealed record LearningEntry(string Id,string Scope,string Kind,string Dimension,string Value,string Text,string Condition,
    string State,string Reason,bool Manual,long Revision,DateTimeOffset UpdatedAt,IReadOnlyList<string> EvidenceIds);
public sealed record LearningJob(string Id,string Scope,string Mode,string Token,long PolicyRevision,long GlobalRevision,
    string State,DateTimeOffset LeaseUntil,string Fingerprint,IReadOnlyList<LearningSummary> Summaries,IReadOnlyList<LearningEvidence> Evidence,
    string Error="");
public sealed record LearningCommitReceipt(string JobId,string State,long Watermark,int Active,int Candidates);
public sealed record LearningSnapshot(LearningPolicy Policy,IReadOnlyList<LearningSummary> Summaries,IReadOnlyList<LearningEntry> Entries,
    IReadOnlyList<LearningJob> Jobs,int Pending,int WaitingEvidence,int Failed,long Watermark,string Status);
public sealed record LearningCapacityStatus(long HotReceipts,long ColdReceipts,long DeletedReceipts,int ReceiptHotLimit,long ProtectedHotReceipts,
    long HotProfiles,long ColdProfiles,long DeletedProfiles,int ProfileHotLimit,long ProtectedHotProfiles);
public sealed record LearningArchiveResult(int ReceiptsArchived,int ProfilesArchived);
public sealed record LearningReceiptHistoryItem(LearningSummary Summary,bool IsCold);
public sealed record LearningProfileHistoryItem(LearningEntry Entry,bool IsCold);
public sealed record LearningHistoryPage<T>(IReadOnlyList<T> Items,string? NextCursor);
