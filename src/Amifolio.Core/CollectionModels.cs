namespace Amifolio.Core;

public sealed record CollectionEntry(string Path,string RelativePath,long Bytes,string Hash,string Kind,string State,string? Error=null);
public sealed record CollectionPreview(string Root,string? ProjectId,string Source,IReadOnlyList<CollectionEntry> Entries,bool Truncated);
public sealed record CollectionReceipt(string BatchId,string Path,string Hash,string State,string? ArtifactId,string? Error,DateTimeOffset CreatedAt);
public sealed record CollectionGrant(string ProjectId,string Root,bool Enabled,int Revision);
internal sealed record CollectionCommit(string BatchId,string Path,string Hash,string? GrantProject,int GrantRevision);
