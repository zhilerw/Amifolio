namespace Amifolio.Core;

public sealed partial class PortfolioStore
{
    public async Task<Stream?> OpenCreativeExampleImageAsync(string projectId, string referenceId,
        DateTimeOffset expectedModifiedAt, CancellationToken ct)
    {
        var reference = await ReadCreativeExampleMetadataAsync(projectId, referenceId, ct).ConfigureAwait(false);
        return await OpenScopedCardImageAsync(CodexProjects.FirstOrDefault(item => item.Id == projectId),
            reference, expectedModifiedAt, ct).ConfigureAwait(false);
    }

    public Task<ArtifactRecord?> ReadCreativeExampleMetadataAsync(string projectId, string referenceId, CancellationToken ct) =>
        DbAsync(db =>
        {
            var project = CodexProjects.FirstOrDefault(item => item.Id == projectId);
            if (project is null) return null;
            var reference = Find(db, "id=$id AND project_id=$project AND deleted=0 AND object_name LIKE 'ref:%'",
                ("$id", referenceId), ("$project", projectId));
            return reference is { IsReference: true } && ReferenceMetadataInScope(project, reference.ObjectPath)
                ? LiveReference(reference) : null;
        }, ct);
}
