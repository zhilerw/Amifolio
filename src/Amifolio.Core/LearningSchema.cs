using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace Amifolio.Core;

internal static class LearningSchema
{
    internal const int Version=2;
    internal static void Ensure(SqliteConnection db,string path,CancellationToken ct)
    {
        var version=Number(db,null,"PRAGMA user_version");
        if(version is not(0 or 1 or Version))throw new IOException("学习数据库版本不受支持。");
        if(Scalar(db,null,"PRAGMA quick_check") as string!="ok")throw new IOException("学习数据库损坏，未重建空库。");
        if(version==0&&Number(db,null,"SELECT COUNT(*) FROM sqlite_master WHERE type='table'")!=0)throw new IOException("未标记的学习数据库不能作为空库重建。");
        if(version!=0)
        {
            foreach(var(table,columns)in new[]{("policies","id,payload"),("receipts","seq,id,scope,fingerprint,payload,eligible,processed,job_id"),("profiles","id,scope,updated,payload"),("evidence","id,scope,valid,payload"),("jobs","id,scope,state,created,payload,result"),("suppressions","id,payload"),("history","seq,entry_id,payload")})
            {using var check=db.CreateCommand();check.CommandText="SELECT "+columns+" FROM "+table+" LIMIT 0";using var rows=check.ExecuteReader();}
        }
        if(version==Version)
        {
            foreach(var(table,columns)in new[]{("receipt_cold","id,payload"),("profile_cold","id,payload"),("learning_watermarks","scope,sequence"),("receipt_sequence_origins","id,sequence")})
            {using var check=db.CreateCommand();check.CommandText="SELECT "+columns+" FROM "+table+" LIMIT 0";using var rows=check.ExecuteReader();}
            LearningColdStorage.Validate(db);
        }
        if(version==1)
        {
            // Keep a standalone recovery point, never overwrite a previous recovery file.
            var recovery=Path.Combine(Path.GetDirectoryName(path)!,"schema-v1-recovery.sqlite");ArchiveSafety.NoReparse(recovery);
            if(!File.Exists(recovery))
            {
                var temporary=Path.Combine(Path.GetDirectoryName(path)!,".v1-"+Guid.NewGuid().ToString("N")[..12]+".tmp");
                try
                {
                    using(var snapshot=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=temporary,Pooling=false}.ToString()))
                    {
                        snapshot.Open();
                        // This private, unpublished snapshot can be discarded on failure.
                        // A disk journal adds eight characters to the temporary path and
                        // can exceed SQLite's Windows path limit during staged restore.
                        Exec(snapshot,null,"PRAGMA journal_mode=MEMORY");
                        db.BackupDatabase(snapshot);Exec(snapshot,null,"PRAGMA journal_mode=DELETE");
                    }
                    ct.ThrowIfCancellationRequested();
                    try{File.Move(temporary,recovery,false);}catch(IOException)when(File.Exists(recovery)){}
                }
                finally{if(File.Exists(temporary))File.Delete(temporary);}
            }
        }
        using var tx=db.BeginTransaction(deferred:false);ct.ThrowIfCancellationRequested();
        var current=Number(db,tx,"PRAGMA user_version");
        if(current is not(0 or 1 or Version))throw new IOException("学习数据库版本已改变。");
        Exec(db,tx,"""
            CREATE TABLE IF NOT EXISTS policies(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS receipts(seq INTEGER PRIMARY KEY AUTOINCREMENT,id TEXT UNIQUE NOT NULL,scope TEXT NOT NULL,fingerprint TEXT NOT NULL,payload TEXT NOT NULL,eligible INTEGER NOT NULL,processed INTEGER NOT NULL,job_id TEXT);
            CREATE INDEX IF NOT EXISTS ix_learning_pending ON receipts(scope,processed,eligible,seq);
            CREATE TABLE IF NOT EXISTS evidence(id TEXT PRIMARY KEY,scope TEXT NOT NULL,valid INTEGER NOT NULL,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS profiles(id TEXT PRIMARY KEY,scope TEXT NOT NULL,updated INTEGER NOT NULL,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY,scope TEXT NOT NULL,state TEXT NOT NULL,created INTEGER NOT NULL,payload TEXT NOT NULL,result TEXT);
            CREATE TABLE IF NOT EXISTS suppressions(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS history(seq INTEGER PRIMARY KEY AUTOINCREMENT,entry_id TEXT NOT NULL,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS receipt_cold(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS profile_cold(id TEXT PRIMARY KEY,payload TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS learning_watermarks(scope TEXT PRIMARY KEY,sequence INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS receipt_sequence_origins(id TEXT NOT NULL,sequence INTEGER NOT NULL,PRIMARY KEY(id,sequence));
            INSERT OR IGNORE INTO receipt_sequence_origins(id,sequence) SELECT id,seq FROM receipts;
            INSERT INTO learning_watermarks(scope,sequence) SELECT scope,MAX(seq) FROM receipts WHERE processed=1 GROUP BY scope
            ON CONFLICT(scope) DO UPDATE SET sequence=MAX(sequence,excluded.sequence);
            CREATE INDEX IF NOT EXISTS ix_learning_receipt_scope ON receipts(scope,seq);
            CREATE INDEX IF NOT EXISTS ix_learning_profile_scope ON profiles(scope,id);
            PRAGMA user_version=2;
            """);
        if(current!=Version)StorageEvents.Log.Checkpoint("learning-schema-upgraded");
        ct.ThrowIfCancellationRequested();tx.Commit();
    }
    internal static object? Scalar(SqliteConnection db,SqliteTransaction? tx,string sql,params (string,object?)[] values)
    {using var c=Command(db,tx,sql,values);return c.ExecuteScalar();}
    internal static long Number(SqliteConnection db,SqliteTransaction? tx,string sql)=>Convert.ToInt64(Scalar(db,tx,sql));
    internal static void Exec(SqliteConnection db,SqliteTransaction? tx,string sql,params (string,object?)[] values)
    {using var c=Command(db,tx,sql,values);c.ExecuteNonQuery();}
    private static SqliteCommand Command(SqliteConnection db,SqliteTransaction? tx,string sql,(string,object?)[] values)
    {var c=db.CreateCommand();c.Transaction=tx;c.CommandText=sql;foreach(var(k,v)in values)c.Parameters.AddWithValue(k,v??DBNull.Value);return c;}
}

internal static class LearningColdStorage
{
    internal static string Source(string table)=>table switch
    {
        "receipts"=>"(SELECT r.seq,r.id,r.scope,r.fingerprint,COALESCE(c.payload,r.payload) AS payload,r.eligible,r.processed,r.job_id FROM receipts r LEFT JOIN receipt_cold c ON c.id=r.id)",
        "profiles"=>"(SELECT r.id,r.scope,r.updated,COALESCE(c.payload,r.payload) AS payload FROM profiles r LEFT JOIN profile_cold c ON c.id=r.id)",
        _=>table
    };
    internal static string ColdTable(string table)=>table=="receipts"?"receipt_cold":"profile_cold";
    internal static string Ledger(string table,string payload)
    {
        var value=Object(payload);
        if(table=="receipts"){value["summary"]="";value["outputs"]=new JsonArray();}
        else{value["text"]="";value["value"]="";value["condition"]="";}
        return value.ToJsonString();
    }
    internal static JsonObject Object(string payload)
    {
        try{return JsonNode.Parse(payload,new JsonNodeOptions{PropertyNameCaseInsensitive=true},new System.Text.Json.JsonDocumentOptions{MaxDepth=32}) as JsonObject??throw new IOException("学习正文无效。");}
        catch(Exception e)when(e is System.Text.Json.JsonException or ArgumentException){throw new IOException("学习正文损坏，未修改冷热数据。",e);}
    }
    internal static void Write(SqliteConnection db,SqliteTransaction tx,string table,string id,string payload)
    {
        var cold=ColdTable(table);var value=Object(payload);
        var deleted=value["state"]?.GetValue<string>()=="deleted";
        var exists=LearningSchema.Scalar(db,tx,"SELECT id FROM "+cold+" WHERE id=$id",("$id",id)) is string;
        if(deleted)LearningSchema.Exec(db,tx,"DELETE FROM "+cold+" WHERE id=$id",("$id",id));
        else if(exists)LearningSchema.Exec(db,tx,"UPDATE "+cold+" SET payload=$p WHERE id=$id",("$p",payload),("$id",id));
        LearningSchema.Exec(db,tx,"UPDATE "+table+" SET payload=$p WHERE id=$id",("$id",id),("$p",exists&&!deleted?Ledger(table,payload):payload));
    }
    internal static void Validate(SqliteConnection db,SqliteTransaction? tx=null,CancellationToken ct=default)
    {
        foreach(var table in new[]{"receipts","profiles"})
        {
            using var c=db.CreateCommand();c.Transaction=tx;c.CommandText="SELECT c.payload,r.payload FROM "+ColdTable(table)+" c LEFT JOIN "+table+" r ON r.id=c.id";
            using var rows=c.ExecuteReader();
            while(rows.Read())
            {
                ct.ThrowIfCancellationRequested();
                if(rows.IsDBNull(1))throw new IOException("冷正文缺少永久身份账本。");
                var body=Object(rows.GetString(0));var ledger=Object(rows.GetString(1));
                if(body["state"]?.GetValue<string>()=="deleted"||!JsonNode.DeepEquals(Object(Ledger(table,rows.GetString(0))),ledger))throw new IOException("冷热正文与身份账本不一致。");
                if(table=="profiles"&&(body["manual"]?.GetValue<bool>()==true||body["state"]?.GetValue<string>()=="active"))throw new IOException("受保护画像不能处于冷区。");
            }
        }
    }
}
