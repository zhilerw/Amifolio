using Microsoft.Data.Sqlite;

namespace Amifolio.Core;

internal static class StoreSchema
{
    internal const int Version=5;
    internal static void Ensure(SqliteConnection db,string root,bool existingLibrary=false)
    {
        using var c=db.CreateCommand();c.CommandText="PRAGMA user_version";var old=Convert.ToInt32(c.ExecuteScalar());
        if(old>Version || old<0)throw new IOException("此作品库由更新版本创建，请使用相应版本打开。");
        c.CommandText="PRAGMA quick_check";if(!string.Equals(c.ExecuteScalar() as string,"ok",StringComparison.Ordinal))throw new IOException("数据库检查未通过，请从备份恢复；未创建新库。");
        c.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='artifacts'";var existing=Convert.ToInt32(c.ExecuteScalar())!=0;
        if(existingLibrary && !existing)throw new IOException("数据库缺少作品结构，请从完整备份恢复；未创建空库。");
        if(existing && old<Version)
        {
            var layout=Path.Combine(root,"storage-layout.json");var session=Path.Combine(root,"session-learning.marker");
            ArchiveSafety.NoReparse(layout);ArchiveSafety.NoReparse(session);
            var recovery=File.Exists(layout)||File.Exists(session)?"index-recovery":"recovery";
            var path=Path.Combine(root,recovery,"migration-"+Guid.NewGuid().ToString("N")+".db");ArchiveSafety.NoReparse(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var checkpoint=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());checkpoint.Open();db.BackupDatabase(checkpoint);
        }
        using var tx=db.BeginTransaction(deferred:false);c.Transaction=tx;
        c.CommandText="""
            CREATE TABLE IF NOT EXISTS projects(id TEXT PRIMARY KEY,name TEXT NOT NULL,root_path TEXT,created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS series(logical_key TEXT PRIMARY KEY,last_version INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS artifacts(id TEXT PRIMARY KEY,title TEXT NOT NULL,original_name TEXT NOT NULL,kind TEXT NOT NULL,source TEXT NOT NULL,project_id TEXT REFERENCES projects(id),tags TEXT NOT NULL,object_name TEXT NOT NULL UNIQUE,hash TEXT NOT NULL,bytes INTEGER NOT NULL,created_at TEXT NOT NULL,version INTEGER NOT NULL,extraction_status TEXT NOT NULL,preview TEXT NOT NULL,logical_key TEXT NOT NULL,deleted INTEGER NOT NULL DEFAULT 0,deleted_at TEXT);
            CREATE INDEX IF NOT EXISTS ix_artifacts_identity ON artifacts(logical_key,hash,deleted);
            CREATE INDEX IF NOT EXISTS ix_artifacts_project ON artifacts(project_id,deleted);
            CREATE TABLE IF NOT EXISTS store_meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS revocations(scope TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS work_items(
                id TEXT PRIMARY KEY,kind TEXT NOT NULL,artifact_id TEXT,project_id TEXT,input_hash TEXT NOT NULL,input_revision INTEGER NOT NULL,
                state TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,max_attempts INTEGER NOT NULL DEFAULT 2,
                error_code TEXT,created_at TEXT NOT NULL,updated_at TEXT NOT NULL,owner TEXT);
            CREATE INDEX IF NOT EXISTS ix_work_state ON work_items(state,created_at);
            CREATE INDEX IF NOT EXISTS ix_work_artifact ON work_items(artifact_id,state);
            CREATE TABLE IF NOT EXISTS collection_grants(project_id TEXT PRIMARY KEY REFERENCES projects(id),root TEXT NOT NULL,enabled INTEGER NOT NULL,revision INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS collection_batches(id TEXT PRIMARY KEY,root TEXT NOT NULL,project_id TEXT,source TEXT NOT NULL,created_at TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS collection_items(batch_id TEXT NOT NULL REFERENCES collection_batches(id),path TEXT NOT NULL,hash TEXT NOT NULL,state TEXT NOT NULL,artifact_id TEXT,error TEXT,PRIMARY KEY(batch_id,path));
            CREATE INDEX IF NOT EXISTS ix_collection_state ON collection_items(state,batch_id);
            CREATE TABLE IF NOT EXISTS artifact_origins(artifact_id TEXT NOT NULL REFERENCES artifacts(id),path TEXT NOT NULL,PRIMARY KEY(artifact_id,path));
            CREATE INDEX IF NOT EXISTS ix_origin_path ON artifact_origins(path);
            """;c.ExecuteNonQuery();
        c.CommandText="PRAGMA table_info(artifacts)";var hasDate=false;using(var r=c.ExecuteReader()){while(r.Read())hasDate|=r.GetString(1)=="deleted_at";}
        if(!hasDate){c.CommandText="ALTER TABLE artifacts ADD COLUMN deleted_at TEXT";c.ExecuteNonQuery();}
        c.CommandText="INSERT OR IGNORE INTO store_meta VALUES('format_version','1')";c.ExecuteNonQuery();
        c.CommandText="SELECT value FROM store_meta WHERE key='format_version'";if(c.ExecuteScalar() as string!="1")throw new IOException("不支持此仓库文件格式。");
        EnsureQueryIndex(c);
        c.CommandText="SELECT 1 FROM sqlite_master WHERE type='table' AND name='codex_works'";
        if(c.ExecuteScalar() is not null){c.CommandText=PortfolioStore.WorkReferenceIndex;c.ExecuteNonQuery();}
        // Restore validates its owned <root>.restore-<guid> staging directory here
        // before the atomic exchange. A backup's persistent ID must not be reused by
        // an already-open store at the destination, even if its revision is identical.
        var restoreName=Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        var restoreMarker=restoreName.LastIndexOf(".restore-",StringComparison.Ordinal);
        if(restoreMarker>=0&&Guid.TryParseExact(restoreName[(restoreMarker+9)..],"N",out _))
        {
            c.CommandText="UPDATE store_meta SET value=lower(hex(randomblob(16))) WHERE key='query_database_id'";
            c.ExecuteNonQuery();
        }
        c.CommandText="PRAGMA user_version=5";c.ExecuteNonQuery();tx.Commit();
        c.Transaction=null;c.CommandText="PRAGMA journal_mode=WAL";c.ExecuteNonQuery();
    }

    // Keep the exact historical SQLite ASCII lower/concatenation semantics. The index
    // The byte-hex index produces candidates (short queries are rechecked). The literal
    // index stores this exact expression for complete >=3-codepoint substring phrases.
    internal static string QueryText(string prefix="")=>
        $"lower({prefix}title||char(10)||{prefix}original_name||char(10)||{prefix}tags||char(10)||{prefix}source||char(10)||{prefix}preview)";
    private static string QueryEncoded(string prefix="")=>$"'Z'||hex({QueryText(prefix)})||'Z'";
    private static string QueryDirectory(string prefix="")=>
        $"rtrim(replace({prefix}object_name,'/','\\'),replace(replace({prefix}object_name,'/','\\'),'\\',''))";

    private static void EnsureQueryIndex(SqliteCommand c)
    {
        c.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name='artifact_query_fts'";
        var rebuild=Convert.ToInt32(c.ExecuteScalar())==0;
        c.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name='artifact_query_literal'";
        var rebuildLiteral=Convert.ToInt32(c.ExecuteScalar())==0;
        c.CommandText="""
            CREATE TABLE IF NOT EXISTS artifact_query_docs(
                docid INTEGER PRIMARY KEY,artifact_id TEXT NOT NULL UNIQUE,reference_directory TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS ix_artifact_query_directory ON artifact_query_docs(reference_directory COLLATE NOCASE,artifact_id);
            CREATE INDEX IF NOT EXISTS ix_artifacts_list_order ON artifacts(deleted,created_at DESC,id);
            CREATE VIRTUAL TABLE IF NOT EXISTS artifact_query_fts USING fts5(encoded,content='',tokenize='trigram case_sensitive 1');
            CREATE VIRTUAL TABLE IF NOT EXISTS artifact_query_literal USING fts5(literal,content='',tokenize='trigram case_sensitive 1');
            INSERT OR IGNORE INTO store_meta VALUES('query_database_id',lower(hex(randomblob(16))));
            INSERT OR IGNORE INTO store_meta VALUES('query_revision',lower(hex(randomblob(16))));
            """;
        c.ExecuteNonQuery();
        c.CommandText="SELECT value FROM store_meta WHERE key='query_codec'";
        var installTriggers=rebuild||rebuildLiteral||c.ExecuteScalar() as string!="2";
        // FTS rowids belong to an INTEGER PRIMARY KEY mapping, not artifacts' implicit
        // rowids: VACUUM/backup/restore cannot silently change the artifact association.
        if(installTriggers)
        {
        c.CommandText="DROP TRIGGER IF EXISTS artifact_query_insert;DROP TRIGGER IF EXISTS artifact_query_update;DROP TRIGGER IF EXISTS artifact_query_delete;";c.ExecuteNonQuery();
        c.CommandText=$"""
            CREATE TRIGGER IF NOT EXISTS artifact_query_insert AFTER INSERT ON artifacts BEGIN
                INSERT INTO artifact_query_docs(artifact_id,reference_directory) VALUES(new.id,{QueryDirectory("new.")});
                INSERT INTO artifact_query_fts(rowid,encoded)
                    SELECT docid,{QueryEncoded("new.")} FROM artifact_query_docs WHERE artifact_id=new.id AND new.deleted=0;
                INSERT INTO artifact_query_literal(rowid,literal)
                    SELECT docid,{QueryText("new.")} FROM artifact_query_docs WHERE artifact_id=new.id AND new.deleted=0;
            END;
            CREATE TRIGGER IF NOT EXISTS artifact_query_delete AFTER DELETE ON artifacts BEGIN
                INSERT INTO artifact_query_fts(artifact_query_fts,rowid,encoded)
                    SELECT 'delete',docid,{QueryEncoded("old.")} FROM artifact_query_docs WHERE artifact_id=old.id AND old.deleted=0;
                INSERT INTO artifact_query_literal(artifact_query_literal,rowid,literal)
                    SELECT 'delete',docid,{QueryText("old.")} FROM artifact_query_docs WHERE artifact_id=old.id AND old.deleted=0;
                DELETE FROM artifact_query_docs WHERE artifact_id=old.id;
            END;
            CREATE TRIGGER IF NOT EXISTS artifact_query_update
                AFTER UPDATE OF id,title,original_name,tags,source,preview,deleted,object_name ON artifacts BEGIN
                INSERT INTO artifact_query_fts(artifact_query_fts,rowid,encoded)
                    SELECT 'delete',docid,{QueryEncoded("old.")} FROM artifact_query_docs WHERE artifact_id=old.id AND old.deleted=0;
                INSERT INTO artifact_query_literal(artifact_query_literal,rowid,literal)
                    SELECT 'delete',docid,{QueryText("old.")} FROM artifact_query_docs WHERE artifact_id=old.id AND old.deleted=0;
                UPDATE artifact_query_docs SET artifact_id=new.id,reference_directory={QueryDirectory("new.")} WHERE artifact_id=old.id;
                INSERT INTO artifact_query_fts(rowid,encoded)
                    SELECT docid,{QueryEncoded("new.")} FROM artifact_query_docs WHERE artifact_id=new.id AND new.deleted=0;
                INSERT INTO artifact_query_literal(rowid,literal)
                    SELECT docid,{QueryText("new.")} FROM artifact_query_docs WHERE artifact_id=new.id AND new.deleted=0;
            END;
            CREATE TRIGGER IF NOT EXISTS artifact_query_revision_insert AFTER INSERT ON artifacts BEGIN
                UPDATE store_meta SET value=lower(hex(randomblob(16))) WHERE key='query_revision'; END;
            CREATE TRIGGER IF NOT EXISTS artifact_query_revision_update AFTER UPDATE ON artifacts BEGIN
                UPDATE store_meta SET value=lower(hex(randomblob(16))) WHERE key='query_revision'; END;
            CREATE TRIGGER IF NOT EXISTS artifact_query_revision_delete AFTER DELETE ON artifacts BEGIN
                UPDATE store_meta SET value=lower(hex(randomblob(16))) WHERE key='query_revision'; END;
            INSERT INTO store_meta(key,value) VALUES('query_codec','2') ON CONFLICT(key) DO UPDATE SET value='2';
            """;
        c.ExecuteNonQuery();
        }
        if(rebuild)
        {
            c.CommandText=$"""
                DELETE FROM artifact_query_docs WHERE NOT EXISTS(SELECT 1 FROM artifacts WHERE id=artifact_id);
                INSERT OR IGNORE INTO artifact_query_docs(artifact_id,reference_directory)
                    SELECT id,{QueryDirectory()} FROM artifacts;
                INSERT INTO artifact_query_fts(rowid,encoded)
                    SELECT d.docid,{QueryEncoded("a.")} FROM artifacts a JOIN artifact_query_docs d ON d.artifact_id=a.id WHERE a.deleted=0;
                """;
            c.ExecuteNonQuery();
        }
        if(rebuildLiteral)
        {
            c.CommandText=$"INSERT INTO artifact_query_literal(rowid,literal) SELECT d.docid,{QueryText("a.")} FROM artifacts a JOIN artifact_query_docs d ON d.artifact_id=a.id WHERE a.deleted=0";
            c.ExecuteNonQuery();
        }
    }
}
