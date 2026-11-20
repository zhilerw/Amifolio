using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Amifolio.Core;

internal static class RevocationJournal
{
    internal static string Scope(string? id)=>id is null?"global":"project-"+Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    internal static void Enqueue(SqliteConnection db,SqliteTransaction tx,string root,string? project)
    {
        TaskStore.CancelScope(db,tx,project);
        StorageFiles.Write(Path.Combine(root,".revocations.pending"),"pending"u8.ToArray());
        using var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText="INSERT OR IGNORE INTO revocations VALUES($scope)";cmd.Parameters.AddWithValue("$scope",Scope(project));cmd.ExecuteNonQuery();
        // A project can also support private provenance behind a global general preference.
        // Conservatively withdraw that projection before this source change becomes visible.
        var preferences=Path.Combine(root,"preference-state","state.json");ArchiveSafety.NoReparse(preferences);
        if(project is not null && File.Exists(preferences))
        {
            TaskStore.CancelScope(db,tx,null);
            cmd.Parameters.Clear();cmd.CommandText="INSERT OR IGNORE INTO revocations VALUES('global')";cmd.ExecuteNonQuery();
        }
        // Existing application-owned Markdown snapshots are derived copies, not independent user originals.
        cmd.Parameters.Clear();cmd.CommandText="UPDATE artifacts SET deleted=1,preview='',tags='',deleted_at=$date WHERE source='应用' AND original_name GLOB 'context-*.md' AND project_id IS $project AND deleted=0";
        cmd.Parameters.AddWithValue("$date",DateTimeOffset.UtcNow.ToString("O"));cmd.Parameters.AddWithValue("$project",(object?)project??DBNull.Value);cmd.ExecuteNonQuery();
    }
    internal static void Replay(string root)
    {
        var marker=Path.Combine(root,".revocations.pending");if(!File.Exists(marker))return;
        ArchiveSafety.NoReparse(marker);
        using var db=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(root,"portfolio.db"),Mode=SqliteOpenMode.ReadWrite,Pooling=false}.ToString());db.Open();
        using(var version=db.CreateCommand()){version.CommandText="PRAGMA user_version";if(Convert.ToInt32(version.ExecuteScalar())>StoreSchema.Version)throw new IOException("请使用更新版本恢复此作品库。");}
        using var cmd=db.CreateCommand();cmd.CommandText="SELECT scope FROM revocations";var scopes=new List<string>();using(var r=cmd.ExecuteReader()){while(r.Read())scopes.Add(r.GetString(0));}
        foreach(var scope in scopes)
        {
            if(scope!="global" && !System.Text.RegularExpressions.Regex.IsMatch(scope,"^project-[a-f0-9]{64}$"))throw new InvalidDataException("Invalid recovery scope");
            Withdraw(root,scope,false);
            cmd.CommandText="DELETE FROM revocations WHERE scope=$scope";cmd.Parameters.Clear();cmd.Parameters.AddWithValue("$scope",scope);cmd.ExecuteNonQuery();
        }
        File.Delete(marker);
    }

    internal static byte[] CaptureState(string root, string scope, string path, string? withdrawnExport=null)
    {
        var bytes=ReadLimited(path,4*1024*1024);JsonObject state;
        try{state=JsonNode.Parse(bytes)?.AsObject()??throw new JsonException();}
        catch(Exception e)when(e is JsonException or InvalidOperationException){throw new IOException("上下文状态损坏，备份已停止。",e);}
        RemoveDerivedManualEntries(state);
        // Reconcile the currently published generation, including direct edits in its Markdown files.
        var generation=state["Generation"]?.GetValue<string>();
        if(generation is null || state["Enabled"]?.GetValue<bool>()!=true)return SerializeState(state);
        if(!Guid.TryParseExact(generation,"N",out _))throw new IOException("上下文发布版本无效。");
        var documents=state["Documents"] as JsonObject??throw new IOException("上下文文档无效。");
        var manual=state["Manual"] as JsonArray??new JsonArray();var deleted=state["Deleted"] as JsonArray??new JsonArray();
        void MarkDeleted(string name){documents.Remove(name);if(!deleted.Any(x=>x?.GetValue<string>()==name))deleted.Add(name);}
        var export=withdrawnExport??Path.Combine(root,"context",scope);var start=Path.Combine(export,"START.md");ArchiveSafety.NoReparse(start);
        if(!File.Exists(start))MarkDeleted("START.md");
        else if(Convert.ToHexStringLower(SHA256.HashData(ReadLimited(start,512*1024)))!=state["StartHash"]?.GetValue<string>())throw new IOException("START.md 已被外部修改，请先处理后再备份。");
        if(state["PublishedHashes"] is JsonObject hashes)foreach(var pair in hashes)
        {
            var name=pair.Key;
            if(name is not("overview.md" or "memory.md" or "style.md" or "summary.md" or "sources.md" or "preferences.md"))throw new IOException("上下文文档路径无效。");
            var full=Path.Combine(export,"generations",generation,name);ArchiveSafety.NoReparse(full);
            if(!File.Exists(full)){MarkDeleted(name);continue;}
            var content=ReadLimited(full,256*1024);
            if(Convert.ToHexStringLower(SHA256.HashData(content))!=pair.Value?.GetValue<string>())
            {
                var text=new UTF8Encoding(false,true).GetString(content);
                if(name=="preferences.md" || (state["AdoptedDocuments"] as JsonObject)?.ContainsKey(name)==true)
                {
                    // Preserve edits privately; editing an export never grants manual-detach consent.
                    if(state["ExternalEdits"] is null)state["ExternalEdits"]=new JsonObject();
                    if(state["ExternalEdits"] is not JsonObject external)throw new IOException("外部修改恢复状态无效。");
                    external[name]=text;
                    if(name!="preferences.md")documents[name]=text;
                }
                else { documents[name]=text;if(!manual.Any(x=>x?.GetValue<string>()==name))manual.Add(name); }
            }
        }
        state["Manual"]=manual.DeepClone();state["Deleted"]=deleted.DeepClone();
        // Candidates are not promoted by backup; a restore always invalidates them.
        return SerializeState(state);
    }

    private static void RemoveDerivedManualEntries(JsonObject state)
    {
        if(state["Manual"] is not JsonArray manual)return;
        var adopted=state["AdoptedDocuments"] as JsonObject;
        for(var i=manual.Count-1;i>=0;i--)
            if(manual[i]?.GetValue<string>() is { } name && (name=="preferences.md" || adopted?.ContainsKey(name)==true))manual.RemoveAt(i);
    }

    private static byte[] SerializeState(JsonObject state)
    {
        if(state["ExternalEdits"] is { } node)
        {
            if(node is not JsonObject edits)throw new IOException("外部修改恢复状态无效。");
            foreach(var pair in edits)
                if(pair.Value is not JsonValue value || !value.TryGetValue<string>(out var text) || text is null || Encoding.UTF8.GetByteCount(text)>256*1024)
                    throw new IOException("外部修改文档无效或超过256 KiB上限。");
        }
        var bytes=JsonSerializer.SerializeToUtf8Bytes(state);
        if(bytes.Length>4*1024*1024)throw new IOException("上下文状态超过4 MiB上限。");
        return bytes;
    }

    private static byte[] ReadLimited(string path,int maximum)
    {
        using var stream=ArchiveSafety.OpenRead(path);if(stream.Length>maximum)throw new IOException("上下文文档超过大小上限。");
        using var output=new MemoryStream();stream.CopyTo(output);if(output.Length>maximum)throw new IOException("上下文文档超过大小上限。");return output.ToArray();
    }
    internal static byte[] CapturePreferences(string path,bool restore)
    {
        JsonObject state;
        try{state=JsonNode.Parse(ReadLimited(path,4*1024*1024))?.AsObject()??throw new JsonException();}
        catch(Exception error)when(error is JsonException or InvalidOperationException){throw new IOException("通用偏好状态损坏，请保留原库并检查。",error);}
        if(restore)
        {
            state["Revision"]=(state["Revision"]?.GetValue<long>()??0)+1;
            state["NeedsPublication"]=true;
            if(state["Participation"] is JsonObject participation)foreach(var pair in participation)
                if(pair.Value is JsonObject item){item["Enabled"]=false;item["Revision"]=(item["Revision"]?.GetValue<long>()??0)+1;}
            if(state["Entries"] is JsonArray entries)foreach(var node in entries)
                if(node is JsonObject item)
                {
                    item["Enabled"]=false;
                    if(item["Status"]?.GetValue<string>() is "confirmed" or "candidate"){item["Status"]="invalidated";item["ConfirmedAt"]=null;}
                }
        }
        return JsonSerializer.SerializeToUtf8Bytes(state);
    }
    internal static void Withdraw(string root,string scope,bool restore)
    {
        // Withdraw the public path first, preserving bytes privately until human edits are durable.
        var export=Path.Combine(root,"context",scope);var withdrawn=Path.Combine(root,"context-state",scope,"withdrawn");
        ArchiveSafety.NoReparse(export);ArchiveSafety.NoReparse(withdrawn);
        if(Directory.Exists(export))
        {
            if(Directory.Exists(withdrawn))throw new IOException("尚有未完成的上下文撤回，请保留资料后检查。");
            Directory.CreateDirectory(Path.GetDirectoryName(withdrawn)!);Directory.Move(export,withdrawn);
        }
        var path=Path.Combine(root,"context-state",scope,"state.json");ArchiveSafety.NoReparse(path);if(!File.Exists(path))return;
        if(new FileInfo(path).Length>4*1024*1024)throw new InvalidDataException("Context state exceeds limit");
        var captured=!restore && Directory.Exists(withdrawn)?CaptureState(root,scope,path,withdrawn):ReadLimited(path,4*1024*1024);
        var state=JsonNode.Parse(captured)?.AsObject()??throw new InvalidDataException("Invalid context state");
        RemoveDerivedManualEntries(state);
        var manual=(state["Manual"] as JsonArray)?.Select(x=>x!.GetValue<string>()).ToHashSet()??[];
        if(state["Documents"] is JsonObject docs)foreach(var name in new[]{"overview.md","sources.md","summary.md","memory.md","style.md","preferences.md"})if(!manual.Contains(name)||name=="preferences.md")docs.Remove(name);
        state["Candidates"]=new JsonObject();state["CandidateFingerprint"]=null;state["SourceVersions"]=new JsonObject();
        state["Generation"]=null;state["PendingGeneration"]=null;state["StartHash"]=null;state["PendingStartHash"]=null;
        state["PublishedHashes"]=new JsonObject();state["PendingHashes"]=new JsonObject();state["NeedsPurge"]=true;
        state["Revision"]=(state["Revision"]?.GetValue<long>()??0)+1;
        state["AutomationClaim"]=null;
        state["AutomationPolicyRevision"]=(state["AutomationPolicyRevision"]?.GetValue<long>()??0)+1;
        if(restore)
        {
            state["Enabled"]=false;state["OriginalAccessAllowed"]=false;
            state["AutomationOptions"]=new JsonObject { ["LearningEnabled"]=false,["SummaryEnabled"]=false,["RemoteAllowed"]=false };
            state["AutomationStatus"]="disabled";
            state["AutomationAttempts"]=new JsonArray();
        }
        StorageFiles.Write(path,SerializeState(state));
        StorageFiles.DeleteTree(withdrawn);
    }
}
