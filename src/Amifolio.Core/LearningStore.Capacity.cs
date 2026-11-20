using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Amifolio.Core;

public sealed partial class LearningStore
{
    private const int ReceiptHotLimit=10000,ProfileHotLimit=2000;
    private const string ReceiptCapacityError="回合热数据已达10000条，本批未能安全腾出空间；请分批冷藏已处理资料，或明确处理待学习/可重试回合后重试。历史身份不会被删除。";
    private const string ProfileCapacityError="画像热数据已达2000条，本批未能安全腾出空间；请分批冷藏未启用自动画像，或明确处理人工/active画像后重试。";
    private static string HotWhere(string table)=>"NOT EXISTS(SELECT 1 FROM "+LearningColdStorage.ColdTable(table)+" c WHERE c.id="+table+".id) AND json_extract(payload,'$.state')<>'deleted'";
    private static long HotCount(Db db,string table)=>Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM "+table+" WHERE "+HotWhere(table)));
    private static bool Cold(Db db,string table,string id)=>db.Scalar("SELECT id FROM "+LearningColdStorage.ColdTable(table)+" WHERE id=$id",("$id",id)) is string;
    private static string ReceiptArchivable=>"(processed=1 OR json_extract(payload,'$.state')='no_change') AND NOT EXISTS(SELECT 1 FROM jobs j WHERE j.id=receipts.job_id AND j.state='running')";
    private static string ProfileArchivable=>"json_extract(payload,'$.manual')=0 AND json_extract(payload,'$.state') IN('candidate','disabled')";
    private static int Archive(Db db,string table,int maximum)
    {
        var ids=new List<(string Id,string Payload)>();
        using(var command=db.Command("SELECT id,payload FROM "+table+" WHERE "+HotWhere(table)+" AND "+(table=="receipts"?ReceiptArchivable:ProfileArchivable)+" ORDER BY "+(table=="receipts"?"seq":"updated,id")+" LIMIT $limit",("$limit",maximum)))
        using(var rows=command.ExecuteReader())while(rows.Read())ids.Add((rows.GetString(0),rows.GetString(1)));
        foreach(var(id,payload)in ids)
        {
            db.Cancellation.ThrowIfCancellationRequested();
            db.Exec("INSERT INTO "+LearningColdStorage.ColdTable(table)+"(id,payload) VALUES($id,$p)",("$id",id),("$p",payload));
            db.Exec("UPDATE "+table+" SET payload=$p WHERE id=$id",("$id",id),("$p",LearningColdStorage.Ledger(table,payload)));
            StorageEvents.Log.Checkpoint(table=="receipts"?"learning-receipt-cold":"learning-profile-cold");
        }
        return ids.Count;
    }
    private static void EnsureHotRoom(Db db,string table)
    {
        var limit=table=="receipts"?ReceiptHotLimit:ProfileHotLimit;
        if(HotCount(db,table)<limit)return;
        Archive(db,table,256);
        if(HotCount(db,table)>=limit)throw new InvalidOperationException(table=="receipts"?ReceiptCapacityError:ProfileCapacityError);
    }
    private static void Warm(Db db,string table,string id)
    {
        var payload=db.Scalar("SELECT payload FROM "+LearningColdStorage.ColdTable(table)+" WHERE id=$id",("$id",id)) as string;
        if(payload is null)return;
        EnsureHotRoom(db,table);
        db.Exec("DELETE FROM "+LearningColdStorage.ColdTable(table)+" WHERE id=$id",("$id",id));
        db.Exec("UPDATE "+table+" SET payload=$p WHERE id=$id",("$id",id),("$p",payload));
    }
    public Task<LearningCapacityStatus> CapacityAsync(CancellationToken ct=default)=>UseAsync(db=>new LearningCapacityStatus(
        HotCount(db,"receipts"),Count(db,"receipt_cold"),DeletedCount(db,"receipts"),ReceiptHotLimit,
        Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM receipts WHERE "+HotWhere("receipts")+" AND NOT("+ReceiptArchivable+")")),
        HotCount(db,"profiles"),Count(db,"profile_cold"),DeletedCount(db,"profiles"),ProfileHotLimit,
        Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM profiles WHERE "+HotWhere("profiles")+" AND NOT("+ProfileArchivable+")"))),ct);
    private static long Count(Db db,string table)=>Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM "+table));
    private static long DeletedCount(Db db,string table)=>Convert.ToInt64(db.Scalar("SELECT COUNT(*) FROM "+table+" WHERE json_extract(payload,'$.state')='deleted'"));
    public Task<LearningArchiveResult> ArchiveColdAsync(int batchSize=256,CancellationToken ct=default)
    {
        if(batchSize is <1 or >1000)throw new ArgumentOutOfRangeException(nameof(batchSize));
        return UseAsync(db=>new LearningArchiveResult(Archive(db,"receipts",batchSize),Archive(db,"profiles",batchSize)),ct);
    }
    public Task<LearningSummary> RestoreReceiptAsync(long sequence,CancellationToken ct=default)=>UseAsync(db=>
    {
        var summary=One<LearningSummary>(db,"SELECT payload FROM receipts WHERE seq=$seq",("$seq",sequence))??throw new InvalidOperationException("回合不存在。");
        if(summary.State=="deleted")throw new InvalidOperationException("已删除回合不能恢复正文。");
        var id=(string)db.Scalar("SELECT id FROM receipts WHERE seq=$seq",("$seq",sequence))!;Warm(db,"receipts",id);return summary;
    },ct);
    public Task<LearningEntry> RestoreEntryAsync(string id,CancellationToken ct=default)=>UseAsync(db=>
    {
        Text(id,128);var entry=One<LearningEntry>(db,"SELECT payload FROM profiles WHERE id=$id",("$id",id))??throw new InvalidOperationException("画像不存在。");
        if(entry.State=="deleted")throw new InvalidOperationException("已删除画像不能恢复正文。");
        Warm(db,"profiles",id);return entry;
    },ct);
    private sealed record HistoryCursor(string Kind,string Scope,string Position);
    private static string? CursorPosition(string? cursor,string scope,string kind)
    {
        if(cursor is null)return null;
        if(cursor.Length>1024)throw new ArgumentException("历史游标无效。");
        try
        {
            var value=JsonSerializer.Deserialize<HistoryCursor>(Convert.FromBase64String(cursor))??throw new ArgumentException("历史游标无效。");
            if(value.Kind!=kind||value.Scope!=scope||value.Position is null||value.Position.Length>128)throw new ArgumentException("历史游标不属于此范围。");
            return value.Position;
        }
        catch(Exception e)when(e is JsonException or FormatException){throw new ArgumentException("历史游标无效。",e);}
    }
    private static string NextCursor(string kind,string scope,string value)=>Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new HistoryCursor(kind,scope,value)));
    public Task<LearningHistoryPage<LearningReceiptHistoryItem>> ReadReceiptHistoryAsync(string? scope,string? cursor=null,int pageSize=50,CancellationToken ct=default)
    {
        var key=Key(scope);if(pageSize is <1 or >100)throw new ArgumentOutOfRangeException(nameof(pageSize));
        var position=CursorPosition(cursor,key,"receipts");
        if(position is not null&&(!long.TryParse(position,NumberStyles.None,CultureInfo.InvariantCulture,out var parsed)||parsed<=0))throw new ArgumentException("历史游标无效。");
        var before=position is null?long.MaxValue:long.Parse(position,CultureInfo.InvariantCulture);
        return UseAsync(db=>
        {
            var rows=Rows<LearningSummary>(db,"receipts","scope=$scope AND seq<$before ORDER BY seq DESC LIMIT $limit",("$scope",key),("$before",before),("$limit",pageSize+1));
            var items=rows.Take(pageSize).Select(s=>new LearningReceiptHistoryItem(s,db.Scalar("SELECT 1 FROM receipt_cold c JOIN receipts r ON r.id=c.id WHERE r.seq=$seq",("$seq",s.Sequence)) is not null)).ToArray();
            return new LearningHistoryPage<LearningReceiptHistoryItem>(items,rows.Count>pageSize?NextCursor("receipts",key,items[^1].Summary.Sequence.ToString(CultureInfo.InvariantCulture)):null);
        },ct);
    }
    public Task<LearningHistoryPage<LearningProfileHistoryItem>> ReadProfileHistoryAsync(string? scope,string? cursor=null,int pageSize=50,CancellationToken ct=default)
    {
        var key=Key(scope);if(pageSize is <1 or >100)throw new ArgumentOutOfRangeException(nameof(pageSize));var after=CursorPosition(cursor,key,"profiles")??"";
        return UseAsync(db=>
        {
            var rows=Rows<LearningEntry>(db,"profiles","scope=$scope AND id>$after ORDER BY id COLLATE BINARY LIMIT $limit",("$scope",key),("$after",after),("$limit",pageSize+1));
            var items=rows.Take(pageSize).Select(e=>new LearningProfileHistoryItem(e,Cold(db,"profiles",e.Id))).ToArray();
            return new LearningHistoryPage<LearningProfileHistoryItem>(items,rows.Count>pageSize?NextCursor("profiles",key,items[^1].Entry.Id):null);
        },ct);
    }
}
