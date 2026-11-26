using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Amifolio.Core;

internal static partial class LearningMaintenance
{
    // Recovery is monotonic for identities, revisions, undo and provenance. Newly
    // recovered data is historical/unverified; no live grants or leases are imported.
    private static void MergeCapacity(SqliteConnection live,SqliteTransaction read,SqliteConnection db,SqliteTransaction tx,CancellationToken ct)
    {
        var v2=LearningSchema.Number(live,read,"PRAGMA user_version")==LearningSchema.Version;
        foreach(var item in CapacityReceipts(live,read,v2))
        {
            ct.ThrowIfCancellationRequested();
            if(item.Processed==1)MergeWatermark(db,tx,item.Scope,item.Sequence);
            Exec(db,tx,"INSERT OR IGNORE INTO receipt_sequence_origins(id,sequence) VALUES($id,$seq)",("$id",item.Id),("$seq",item.Sequence));
            var prior=LearningSchema.Scalar(db,tx,"SELECT payload FROM "+LearningColdStorage.Source("receipts")+" WHERE id=$id",("$id",item.Id)) as string;
            if(State(item.Body)=="deleted")continue; // The deletion-union pass scrubs all copies below.
            if(prior is not null)
            {
                var archived=Object(prior);
                if(ReceiptIdentity(archived)!=ReceiptIdentity(item.Body))throw new IOException("恢复回合身份冲突。");
                if(State(archived)=="deleted"||Value<long>(archived,"version")>=Value<long>(item.Body,"version"))continue;
            }
            var body=(JsonObject)item.Body.DeepClone();var sequence=item.Sequence;
            if(prior is not null)sequence=Convert.ToInt64(LearningSchema.Scalar(db,tx,"SELECT seq FROM receipts WHERE id=$id",("$id",item.Id)));
            else if(LearningSchema.Scalar(db,tx,"SELECT id FROM receipts WHERE seq=$seq",("$seq",sequence)) is not null)
                sequence=LearningSchema.Number(db,tx,"SELECT COALESCE(MAX(seq),0)+1 FROM receipts");
            body["sequence"]=sequence;body["state"]="pending_evidence";
            if(prior is null)Exec(db,tx,"INSERT INTO receipts(seq,id,scope,fingerprint,payload,eligible,processed,job_id) VALUES($seq,$id,$scope,$hash,$p,0,0,NULL)",("$seq",sequence),("$id",item.Id),("$scope",item.Scope),("$hash",item.Fingerprint),("$p",body.ToJsonString()));
            else
            {
                UpdatePayload(db,tx,"receipts",item.Id,body);
                Exec(db,tx,"UPDATE receipts SET fingerprint=$hash,eligible=0,processed=0,job_id=NULL WHERE id=$id",("$id",item.Id),("$hash",item.Fingerprint));
            }
            // Completed bodies can remain cold after restoration. Pending/failed retry
            // input stays hot; we never turn it into an automatic retry.
            var wasCold=v2&&LearningSchema.Scalar(live,read,"SELECT id FROM receipt_cold WHERE id=$id",("$id",item.Id)) is string;
            if(wasCold||item.Processed==1||State(item.Body)=="no_change")StoreCold(db,tx,"receipts",item.Id,body);
            else
            {
                Exec(db,tx,"DELETE FROM receipt_cold WHERE id=$id",("$id",item.Id));
                Exec(db,tx,"UPDATE receipts SET payload=$p WHERE id=$id",("$id",item.Id),("$p",body.ToJsonString()));
            }
        }
        foreach(var(id,payload)in Payloads(live,read,"evidence"))
        {
            ct.ThrowIfCancellationRequested();_ = Object(payload);
            var scope=Convert.ToString(LearningSchema.Scalar(live,read,"SELECT scope FROM evidence WHERE id=$id",("$id",id)))!;
            Exec(db,tx,"INSERT INTO evidence(id,scope,valid,payload) VALUES($id,$scope,0,$p) ON CONFLICT(id) DO UPDATE SET valid=0",("$id",id),("$scope",scope),("$p",payload));
        }
        foreach(var(id,payload)in Payloads(live,read,"profiles"))
        {
            ct.ThrowIfCancellationRequested();var body=Object(payload);if(State(body)=="deleted")continue;
            var old=LearningSchema.Scalar(db,tx,"SELECT payload FROM "+LearningColdStorage.Source("profiles")+" WHERE id=$id",("$id",id)) as string;
            if(old is not null)
            {
                var archived=Object(old);if(State(archived)=="deleted"||Value<long>(archived,"revision")>=Value<long>(body,"revision"))continue;
                PreserveUndo(db,tx,id,archived);
            }
            if(!Manual(body)&&State(body)=="active"){body["state"]="disabled";body["reason"]="restored";body["revision"]=NextRevision(body);}
            var cold=v2&&LearningSchema.Scalar(live,read,"SELECT id FROM profile_cold WHERE id=$id",("$id",id)) is string;
            Exec(db,tx,"INSERT INTO profiles(id,scope,updated,payload) VALUES($id,$scope,$updated,$p) ON CONFLICT(id) DO UPDATE SET scope=$scope,updated=$updated,payload=$p",("$id",id),("$scope",Value<string>(body,"scope")),("$updated",Value<DateTimeOffset>(body,"updatedAt").ToUnixTimeMilliseconds()),("$p",body.ToJsonString()));
            Exec(db,tx,"DELETE FROM profile_cold WHERE id=$id",("$id",id));
            if(cold&&!Manual(body)&&State(body)!="active")StoreCold(db,tx,"profiles",id,body);
        }
        using(var c=Command(live,read,"SELECT entry_id,payload FROM history ORDER BY seq"))
        using(var r=c.ExecuteReader())while(r.Read()){ct.ThrowIfCancellationRequested();PreserveUndo(db,tx,r.GetString(0),Object(r.GetString(1)));}
        foreach(var(id,payload)in Payloads(live,read,"jobs"))
        {
            ct.ThrowIfCancellationRequested();var job=Object(payload);job["state"]="cancelled";job["token"]="";job["error"]="restored";
            Exec(db,tx,"INSERT OR IGNORE INTO jobs(id,scope,state,created,payload,result) VALUES($id,$scope,'cancelled',0,$p,NULL)",("$id",id),("$scope",Value<string>(job,"scope")),("$p",job.ToJsonString()));
        }
        if(v2)
        {
            using(var c=Command(live,read,"SELECT scope,sequence FROM learning_watermarks"))
            using(var r=c.ExecuteReader())while(r.Read())MergeWatermark(db,tx,r.GetString(0),r.GetInt64(1));
            using(var c=Command(live,read,"SELECT id,sequence FROM receipt_sequence_origins"))
            using(var r=c.ExecuteReader())while(r.Read())Exec(db,tx,"INSERT OR IGNORE INTO receipt_sequence_origins VALUES($id,$seq)",("$id",r.GetString(0)),("$seq",r.GetInt64(1)));
        }
    }
    private static IEnumerable<(string Id,string Scope,long Sequence,string Fingerprint,int Processed,JsonObject Body)> CapacityReceipts(SqliteConnection db,SqliteTransaction tx,bool v2)
    {
        string? after=null;
        while(true)
        {
            var page=new List<(string Id,string Scope,long Sequence,string Fingerprint,int Processed,JsonObject Body)>();
            using(var c=Command(db,tx,"SELECT id,scope,seq,fingerprint,processed,payload FROM "+(v2?LearningColdStorage.Source("receipts"):"receipts")+" WHERE ($after IS NULL OR id>$after) ORDER BY id LIMIT 256",("$after",(object?)after??DBNull.Value)))
            using(var r=c.ExecuteReader())while(r.Read())page.Add((r.GetString(0),r.GetString(1),r.GetInt64(2),r.GetString(3),r.GetInt32(4),Object(r.GetString(5))));
            if(page.Count==0)yield break;foreach(var row in page)yield return row;after=page[^1].Id;
        }
    }
    private static void MergeWatermark(SqliteConnection db,SqliteTransaction tx,string scope,long sequence)=>
        Exec(db,tx,"INSERT INTO learning_watermarks VALUES($scope,$seq) ON CONFLICT(scope) DO UPDATE SET sequence=MAX(sequence,excluded.sequence)",("$scope",scope),("$seq",sequence));
    private static void PreserveUndo(SqliteConnection db, SqliteTransaction tx, string id, JsonObject payload)
    {
        var serialized = payload.ToJsonString();
        using var incoming = JsonDocument.Parse(serialized);
        using (var command = Command(db, tx, "SELECT payload FROM history WHERE entry_id=$id", ("$id", id)))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                using var existing = JsonDocument.Parse(reader.GetString(0));
                // Independent histories can have the same revision number and different content.
                if (JsonElement.DeepEquals(existing.RootElement, incoming.RootElement)) return;
            }
        }
        Exec(db, tx, "INSERT INTO history(entry_id,payload) VALUES($id,$p)", ("$id", id), ("$p", serialized));
    }
    private static void StoreCold(SqliteConnection db,SqliteTransaction tx,string table,string id,JsonObject payload)
    {
        Exec(db,tx,"INSERT INTO "+LearningColdStorage.ColdTable(table)+"(id,payload) VALUES($id,$p) ON CONFLICT(id) DO UPDATE SET payload=$p",("$id",id),("$p",payload.ToJsonString()));
        Exec(db,tx,"UPDATE "+table+" SET payload=$p WHERE id=$id",("$id",id),("$p",LearningColdStorage.Ledger(table,payload.ToJsonString())));
    }
}
