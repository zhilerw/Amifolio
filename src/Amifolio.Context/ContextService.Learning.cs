using Amifolio.Core;

namespace Amifolio.Context;

public sealed partial class ContextService
{
    private LearningStore? learningState;
    public LearningStore Learning=>learningState??=new(store.DataRoot);
    public async Task<LearningReceipt> CaptureLearningAsync(LearningCapture capture,string codexHome,CancellationToken ct=default)
    {
        // Consent is checked BEFORE opening a user transcript, including for a failed verification.
        var policy=await Learning.PolicyAsync(capture.ProjectId,ct).ConfigureAwait(false);
        if(!policy.Capture)throw new InvalidOperationException("此项目尚未授权记录会话。");
        var project=await LearningProjectAsync(capture.ProjectId,ct).ConfigureAwait(false);
        var source=await new CodexTurnReader(codexHome).ReadTurnAsync(capture.SessionId,capture.TurnId,project.RootPath,ct).ConfigureAwait(false);
        var outputs=new List<LearningOutput>();
        var references=await store.ListAsync(projectId:capture.ProjectId,cancellationToken:ct).ConfigureAwait(false);
        foreach(var output in capture.Outputs)
        {
            var reference=references.FirstOrDefault(a=>a.Id==output.ReferenceId&&a.ProjectId==capture.ProjectId&&a.OriginalName==output.Name&&a.IsReference);
            var valid=reference is not null;
            if(valid&&store is PortfolioStore sourceStore)
                try{await sourceStore.Catalog.ValidateFileAsync(project,reference!.ObjectPath,ct).ConfigureAwait(false);}
                catch(Exception e)when(e is IOException or UnauthorizedAccessException){valid=false;}
            outputs.Add(output with{Verified=valid});
        }
        var proof=source is null?null:new LearningVerifiedTurn(source.SessionId,source.TurnId,source.RootPath,source.UserTextHash,source.UserText,source.ObservedAt,source.SourceFingerprint);
        return await Learning.CaptureAsync(capture with{Outputs=outputs},proof,ct).ConfigureAwait(false);
    }
    public async Task<LearningCommitReceipt> CompleteLearningAsync(string jobId,string token,IReadOnlyList<MemoryProposal> proposals,string codexHome,CancellationToken ct=default)
    {
        var job=await Learning.OwnedJobAsync(jobId,token,ct).ConfigureAwait(false);
        await LearningProjectAsync(job.Scope,ct).ConfigureAwait(false);
        var related=new List<string>(job.Evidence.Select(e=>e.Evidence.Id));
        foreach(var scope in new[]{job.Scope,"global"})
        {
            var snapshot=await Learning.SnapshotAsync(scope,ct).ConfigureAwait(false);
            related.AddRange(snapshot.Entries.Where(e=>proposals.Any(p=>string.Equals(p.Dimension?.Trim(),e.Dimension,StringComparison.OrdinalIgnoreCase))).SelectMany(e=>e.EvidenceIds));
        }
        var contexts=new Dictionary<string,MemoryEvidence>(StringComparer.Ordinal);
        var live=await VerifyLearningEvidenceAsync(related,codexHome,ct,contexts).ConfigureAwait(false);
        var result=await Learning.CompleteAsync(jobId,token,proposals,live,ct,contexts).ConfigureAwait(false);
        await PublishLearningAsync(job.Scope,ct).ConfigureAwait(false);
        if(proposals.Any(p=>p.Scope=="global"))await PublishLearningAsync(null,ct).ConfigureAwait(false);
        return result;
    }
    public async Task<IReadOnlyDictionary<string,string>> ReadLearningAsync(string? projectId,string layer,string condition,string codexHome,CancellationToken ct=default)
    {
        if(projectId is not null)await LearningProjectAsync(projectId,ct).ConfigureAwait(false);
        var snapshot=await Learning.SnapshotAsync(projectId,ct).ConfigureAwait(false);
        if(!snapshot.Policy.Share)return new Dictionary<string,string>();
        var applicable=snapshot.Entries.Where(e=>e.State=="active"&&(e.Condition.Length==0||condition.Length>0&&MemoryPolicy.Normalize(e.Condition)==MemoryPolicy.Normalize(condition)))
            .Where(e=>layer=="overview"||(layer=="memory"?e.Kind is "memory" or "fact":e.Kind==layer)).Take(24);
        await VerifyLearningEvidenceAsync(applicable.SelectMany(e=>e.EvidenceIds),codexHome,ct).ConfigureAwait(false);
        var documents=await Learning.SharedAsync(projectId,layer,condition,ct).ConfigureAwait(false);
        await Learning.PublishProjectionAsync(projectId,true,ct).ConfigureAwait(false);
        await PublishLearningCompatibilityAsync(projectId,ct).ConfigureAwait(false);return documents;
    }
    public async Task<IReadOnlyList<CodexTurnIdentity>> RecentLearningTurnsAsync(string projectId,string codexHome,CancellationToken ct=default)
    {
        var p=await Learning.PolicyAsync(projectId,ct).ConfigureAwait(false);if(!p.Capture||p.GrantedAt is null)return [];
        var project=await LearningProjectAsync(projectId,ct).ConfigureAwait(false);
        return await new CodexTurnReader(codexHome).RecentTurnsAsync(project.RootPath,p.GrantedAt.Value,ct).ConfigureAwait(false);
    }
    private async Task<CodexProject> LearningProjectAsync(string projectId,CancellationToken ct)
    {
        if(store is not PortfolioStore{ReadOnlySources:true} references)throw new InvalidOperationException("会话学习只支持已发现的本机Codex项目。");
        await references.RefreshCodexProjectsAsync(ct).ConfigureAwait(false);
        return references.CodexProjects.FirstOrDefault(p=>p.Id==projectId)??throw new InvalidOperationException("来源项目不在当前Codex项目中。");
    }
    private async Task<HashSet<string>> VerifyLearningEvidenceAsync(IEnumerable<string> ids,string codexHome,CancellationToken ct,IDictionary<string,MemoryEvidence>? contexts=null)
    {
        var keys=ids.Distinct(StringComparer.Ordinal).Take(129).ToArray();if(keys.Length>128)throw new InvalidOperationException("本批证据过多，请缩小整合范围。");
        var evidence=await Learning.EvidenceAsync(keys,ct).ConfigureAwait(false);
        var verified=new HashSet<string>(StringComparer.Ordinal);var reader=new CodexTurnReader(codexHome);
        var cache=new Dictionary<string,CodexTurnEvidence?>(StringComparer.Ordinal);
        if(store is not PortfolioStore{ReadOnlySources:true} sources)throw new InvalidOperationException("缺少只读Codex来源。");
        await sources.RefreshCodexProjectsAsync(ct).ConfigureAwait(false);
        var projects=sources.CodexProjects.ToDictionary(p=>p.Id,StringComparer.Ordinal);
        foreach(var row in evidence)
        {
            var source=row.Evidence;var identity=source.ProjectId+"/"+source.SessionId+"/"+source.TurnId;
            if(!cache.TryGetValue(identity,out var actual))
            {
                try
                {
                    var project=projects.GetValueOrDefault(source.ProjectId)??throw new InvalidOperationException("来源项目已撤销。");
                    actual=await reader.ReadTurnAsync(source.SessionId,source.TurnId,project.RootPath,ct).ConfigureAwait(false);
                }
                catch(InvalidOperationException){actual=null;}
                cache[identity]=actual;
            }
            if(actual is not null&&actual.UserTextHash==row.UserTextHash&&actual.SourceFingerprint==row.SourceFingerprint&&actual.UserText.Contains(source.Text,StringComparison.Ordinal))
            {
                verified.Add(source.Id);
                // Full source context is transient admission input. Only the selected minimal
                // quote is persisted or sent to a provider; quotations/negation must not be lost.
                if(contexts is not null)contexts[source.Id]=source with{Text=actual.UserText};
            }
            else await Learning.WithdrawEvidenceAsync(source.Id,ct).ConfigureAwait(false);
        }
        return verified;
    }
    private async Task PublishLearningAsync(string? scope,CancellationToken ct)
    {
        await Learning.PublishProjectionAsync(scope,true,ct).ConfigureAwait(false);
        await PublishLearningCompatibilityAsync(scope,ct).ConfigureAwait(false);
    }
}
