using Amifolio.Core;
using System.Text.Json;

namespace Amifolio.Context;

public sealed partial class ContextService
{
    // These are retirement signals only. Neither signal grants capture, learning,
    // sharing, original access, or remote/model permission.
    public bool LegacyRetired
    {
        get
        {
            var marker=Path.Combine(store.DataRoot,"session-learning.marker");SafeFiles.CheckPath(marker);
            return File.Exists(marker)||StorageLayoutV2();
        }
    }
    private bool StorageLayoutV2()
    {
        var path=Path.Combine(store.DataRoot,"storage-layout.json");SafeFiles.CheckPath(path);
        if(!File.Exists(path))return false;
        // This synchronous property reads at most one small local marker. An invalid
        // or future layout must never silently re-enable legacy writes or HTTP.
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete);
        var bytes=new byte[4097];var count=0;
        while(count<bytes.Length)
        {
            var read=stream.Read(bytes,count,bytes.Length-count);if(read==0)break;count+=read;
        }
        if(count>4096)throw new InvalidDataException("数据目录版本标记超限，未启用旧版生成。");
        try
        {
            using var json=JsonDocument.Parse(bytes.AsMemory(0,count),new JsonDocumentOptions{MaxDepth=8});
            var root=json.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||root.EnumerateObject().Count(p=>p.NameEquals("version"))!=1||
               !root.TryGetProperty("version",out var value)||value.ValueKind!=JsonValueKind.Number||
               !value.TryGetInt32(out var version)||version is not(1 or 2))
                throw new InvalidDataException("数据目录版本标记无效，未启用旧版生成。");
            return version==2;
        }
        catch(JsonException error){throw new InvalidDataException("数据目录版本标记无效，未启用旧版生成。",error);}
    }
    private void ThrowIfLegacyRetired()
    {
        if(LegacyRetired)throw new InvalidOperationException("旧版文件生成已停用，请使用会话学习。");
    }
    public string LearningEntryDirectory(string? scope)=>Path.GetDirectoryName(Learning.ProjectionPath(scope))!;
    public async Task EnableSessionLearningOnlyAsync(CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        await using var lease=await StorageLease.SharedAsync(store.DataRoot,ct).ConfigureAwait(false);
        var marker=Path.Combine(store.DataRoot,"session-learning.marker");SafeFiles.CheckPath(marker);
        if(!File.Exists(marker))await SafeFiles.AtomicWriteAsync(marker,SafeFiles.Utf8.GetBytes("session-learning-v1"),ct).ConfigureAwait(false);
    }
    private async Task PublishLearningCompatibilityAsync(string? scope,CancellationToken ct)
    {
        // Existing AGENTS paths stay useful, but old generations are first preserved by migration.
        if(!StorageLayoutV2())return;
        var policy=await Learning.PolicyAsync(scope,ct).ConfigureAwait(false);
        var canonical=Learning.ProjectionPath(scope);var key=Path.GetFileName(Path.GetDirectoryName(canonical)!);
        var path=Path.Combine(store.DataRoot,"context",key,"START.md");SafeFiles.CheckPath(path);
        SafeFiles.CheckPath(canonical);
        // Only a pointer is retained here: never copy learning content or grant Share.
        // A concurrent revocation removes the canonical target, so a late pointer
        // cannot revive a stale generation or independently expose its contents.
        var text=policy.Share&&File.Exists(canonical)?"# Amifolio\n\n先通过 MCP learning_context 核对当前共享状态，再阅读[会话学习入口]("+Path.GetRelativePath(Path.GetDirectoryName(path)!,canonical).Replace('\\','/')+")，只展开必要层。当前用户要求优先。":"# Amifolio\n\n此范围的共享已关闭。请勿读取历史资料。";
        await SafeFiles.AtomicWriteAsync(path,SafeFiles.Utf8.GetBytes(text),ct).ConfigureAwait(false);
    }
}
