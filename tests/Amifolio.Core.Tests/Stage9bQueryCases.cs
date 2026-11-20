using System.Reflection;
using System.Text;
using System.Text.Json;
using Amifolio.Core;
using Microsoft.Data.Sqlite;

internal static class Stage9bQueryCases
{
    internal static (string Name,Func<Task> Test)[] All =>
    [
        ("retired v4 upgrade keeps index recovery outside legacy migration",RetiredUpgrade),
        ("directory query is complete and immediate including missing references",DirectoryQuery),
        ("directory query validates scope exclusions and cancellation",DirectorySafety),
        ("persistent search candidates preserve literal full-content semantics",SearchSemantics),
        ("search triggers follow updates tombstones hard deletes and raw connections",SearchUpdates),
        ("v4 upgrade and restore rebuild derived search without changing source files",MigrationRestore)
    ];
    private static async Task RetiredUpgrade()
    {
        var f=await Fixture.Create();MakeV4(f);
        await File.WriteAllTextAsync(Path.Combine(f.Data,"storage-layout.json"),"{\"version\":2}");
        await File.WriteAllTextAsync(Path.Combine(f.Data,"session-learning.marker"),"session-learning-v1");
        var upgraded=new PortfolioStore(f.Data);upgraded.EnableCodexReferenceMode(f.Home);await upgraded.InitializeAsync();
        Check(!Directory.Exists(Path.Combine(f.Data,"recovery")),"retired-upgrade-must-not-recreate-legacy-recovery");
        Check(Directory.Exists(Path.Combine(f.Data,"index-recovery"))&&Directory.EnumerateFiles(Path.Combine(f.Data,"index-recovery"),"migration-*.db").Any(),"retired-upgrade-keeps-index-snapshot");
        Check((await new LegacyDataService(f.Data).MigrateAsync()).Status=="already","normal-schema-upgrade-not-old-process-conflict");
        Check((await upgraded.ListAsync("tail-needle")).Count==12,"retired-upgrade-retains-search");
    }
    private static void MakeV4(Fixture f)
    {
        using var db=f.Open();using var c=db.CreateCommand();c.CommandText="""
            DROP TRIGGER IF EXISTS artifact_query_insert;
            DROP TRIGGER IF EXISTS artifact_query_update;
            DROP TRIGGER IF EXISTS artifact_query_delete;
            DROP TRIGGER IF EXISTS artifact_query_revision_insert;
            DROP TRIGGER IF EXISTS artifact_query_revision_update;
            DROP TRIGGER IF EXISTS artifact_query_revision_delete;
            DROP TABLE IF EXISTS artifact_query_fts;
            DROP TABLE IF EXISTS artifact_query_literal;
            DROP TABLE IF EXISTS artifact_query_docs;
            DELETE FROM store_meta WHERE key IN('query_database_id','query_revision','query_codec');
            PRAGMA user_version=4;
            """;c.ExecuteNonQuery();
    }
    internal static async Task<int> Run()
    {
        foreach(var (name,test) in All){await test();Console.WriteLine("PASS "+name);}
        return 0;
    }
    private static async Task DirectoryQuery()
    {
        var f=await Fixture.Create();
        var method=typeof(PortfolioStore).GetMethod("ListDirectoryAsync",[typeof(string),typeof(string),typeof(CancellationToken)]);
        Check(method is not null,"complete-directory-api-missing");
        async Task<IReadOnlyList<ArtifactRecord>> List(string directory)=>await (Task<IReadOnlyList<ArtifactRecord>>)method!.Invoke(f.Store,[f.Project.Id,directory,CancellationToken.None])!;
        var parent=Path.GetDirectoryName(f.Files[0])!;
        var rows=await List(parent);
        Check(rows.Count==6&&rows.All(r=>Path.GetDirectoryName(r.ObjectPath)==parent),"only-immediate-complete-rows");
        Check((await List(f.Project.RootPath)).Count==0,"root-query-does-not-return-descendants");
        Check((await f.Store.ListAsync()).Count==12,"full-api-not-truncated");
        File.Delete(f.Files[0]);
        var live=await List(parent);
        Check(live.Count==6&&live.Single(r=>r.ObjectPath==f.Files[0]) is{Preview:"",ExtractionStatus:"源文件缺失"},"missing-row-retained");
        Check((await List(parent.ToUpperInvariant())).Count==6,"directory-case-insensitive");
        var special=Path.Combine(f.Project.RootPath,"percent%_folder");Directory.CreateDirectory(special);
        var path=Path.Combine(special,"extra.md");await File.WriteAllTextAsync(path,"synthetic");
        await f.Store.ReferenceFileAsync(path,f.Project.Id);
        Check((await List(special)).Count==1,"directory-wildcards-are-literal");
        var unicode=Path.Combine(f.Project.RootPath,"Ä-case");Directory.CreateDirectory(unicode);
        var unicodeFile=Path.Combine(unicode,"unicode.md");await File.WriteAllTextAsync(unicodeFile,"synthetic");
        await f.Store.ReferenceFileAsync(unicodeFile,f.Project.Id);
        Check((await List(unicode.ToLowerInvariant())).Count==1,"directory-unicode-case-is-not-silently-dropped");
    }
    private static async Task DirectorySafety()
    {
        var f=await Fixture.Create();
        var method=typeof(PortfolioStore).GetMethod("ListDirectoryAsync",[typeof(string),typeof(string),typeof(CancellationToken)])!;
        foreach(var directory in new[]{f.Root,Path.Combine(f.Project.RootPath,".ssh"),f.Data})
        {
            Directory.CreateDirectory(directory);
            var rejected=false;
            try{await (Task)method.Invoke(f.Store,[f.Project.Id,directory,CancellationToken.None])!;}
            catch(IOException){rejected=true;}
            Check(rejected,"directory-boundary-rejected");
        }
        using var cts=new CancellationTokenSource();cts.Cancel();
        var cancelled=false;
        try{await (Task)method.Invoke(f.Store,[f.Project.Id,f.Project.RootPath,cts.Token])!;}
        catch(OperationCanceledException){cancelled=true;}
        Check(cancelled,"directory-cancellation");
    }
    private static async Task SearchSemantics()
    {
        var f=await Fixture.Create();
        await f.Store.ListAsync(); // Also exercise any warm application-metadata path.
        using(var db=f.Open())using(var c=db.CreateCommand())
        {
            c.CommandText="SELECT COUNT(*) FROM sqlite_master WHERE name='artifact_query_fts'";
            Check(Convert.ToInt32(c.ExecuteScalar())==1,"persistent-search-index-missing");
        }
        var cold=new PortfolioStore(f.Data);cold.EnableCodexReferenceMode(f.Home);cold.QueryMetadataCacheBudget=0;
        foreach(var store in new[]{cold,f.Store})
        foreach(var query in new[]{"中","中文","甲中文","ASCII","ascii","tail-needle","😀","%_","\0z","z","x","Codex\nSYNTHETIC","item000",".md\n","不存在","","\n","A\"B","\"","\"\"","B\"","Ä","ä","Ä中","中Ä","😀中","\0","z after-nul-needle","after-nul-needle","\0z after","e\u0301","\u0301中"})
        {
            var expected=f.Oracle(query);
            var actual=(await store.ListAsync(query)).Select(r=>r.Id).ToArray();
            Check(expected.SequenceEqual(actual),"search-exact-literal-oracle: "+JsonSerializer.Serialize(query)+" warm="+(store==f.Store));
        }
        var longQuery=new string('x',2100)+" tail-needle";
        Check(f.Oracle("tail-needle").Length==12,"tail-oracle-must-be-positive");
        Check(f.Oracle(longQuery).SequenceEqual((await f.Store.ListAsync(longQuery)).Select(r=>r.Id)),"long-tail-match");
    }
    private static async Task SearchUpdates()
    {
        var f=await Fixture.Create();
        f.Execute("UPDATE artifacts SET preview='SYNTHETIC new-change-token' WHERE id=$id",f.Ids[0]);
        Check((await f.Store.ListAsync("new-change-token")).Single().Id==f.Ids[0],"update-indexed");
        f.Execute("UPDATE artifacts SET deleted=1 WHERE id=$id",f.Ids[0]);
        Check((await f.Store.ListAsync("new-change-token")).Count==0,"tombstone-not-searchable");
        f.Execute("UPDATE artifacts SET deleted=0 WHERE id=$id",f.Ids[0]);
        Check((await f.Store.ListAsync("new-change-token")).Count==1,"undelete-indexed");
        f.Execute("DELETE FROM artifacts WHERE id=$id",f.Ids[0]);
        Check((await f.Store.ListAsync("new-change-token")).Count==0,"hard-delete-not-searchable");
        using var db=f.Open();using var c=db.CreateCommand();
        c.CommandText="SELECT COUNT(*) FROM artifact_query_docs WHERE artifact_id=$id";c.Parameters.AddWithValue("$id",f.Ids[0]);
        Check(Convert.ToInt32(c.ExecuteScalar())==0,"hard-delete-cleans-doc-mapping");
        c.Parameters.Clear();c.CommandText="VACUUM";c.ExecuteNonQuery();
        Check(f.Oracle("tail-needle").SequenceEqual((await f.Store.ListAsync("tail-needle")).Select(r=>r.Id)),"vacuum-keeps-index-artifact-identity");
    }
    private static async Task MigrationRestore()
    {
        var f=await Fixture.Create();var before=await File.ReadAllBytesAsync(f.Files[0]);
        MakeV4(f);
        var upgraded=new PortfolioStore(f.Data);upgraded.EnableCodexReferenceMode(f.Home);
        await upgraded.InitializeAsync();await upgraded.RefreshCodexProjectsAsync();
        Check(f.Oracle("中文").SequenceEqual((await upgraded.ListAsync("中文")).Select(r=>r.Id)),"v4-rebuild-complete");
        Check(Directory.EnumerateFiles(Path.Combine(f.Data,"recovery"),"migration-*.db").Any(),"upgrade-has-prior-schema-backup");
        var archive=Path.Combine(f.Root,"owned-backup.zip");await new PortfolioMaintenance(f.Data).BackupAsync(archive);
        var restoredRoot=Path.Combine(f.Root,"restored");await new PortfolioMaintenance(restoredRoot).RestoreAsync(archive);
        var restored=new PortfolioStore(restoredRoot);restored.EnableCodexReferenceMode(f.Home);
        Check((await restored.ListAsync("tail-needle")).Count==0,"restore-cleared-preview-not-revived-by-index");
        Check((await restored.ListAsync()).Count==12,"restore-retains-all-references");
        var after=await File.ReadAllBytesAsync(f.Files[0]);Check(before.SequenceEqual(after),"source-bytes-unchanged");
    }
    private static void Check(bool value,string code){if(!value)throw new Exception(code);}
    internal sealed record Fixture(string Root,string Home,string Data,PortfolioStore Store,CodexProject Project,string[] Files,string[] Ids)
    {
        internal SqliteConnection Open(){var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=Path.Combine(Data,"portfolio.db"),Pooling=false}.ToString());c.Open();return c;}
        internal void Execute(string sql,string id){using var db=Open();using var c=db.CreateCommand();c.CommandText=sql;c.Parameters.AddWithValue("$id",id);c.ExecuteNonQuery();}
        internal string[] Oracle(string query)
        {
            using var db=Open();using var c=db.CreateCommand();c.CommandText="""
                SELECT id FROM artifacts WHERE deleted=0 AND
                instr(lower(title||char(10)||original_name||char(10)||tags||char(10)||source||char(10)||preview),lower($q))>0
                ORDER BY created_at DESC,id
                """;c.Parameters.AddWithValue("$q",query);using var r=c.ExecuteReader();var ids=new List<string>();while(r.Read())ids.Add(r.GetString(0));return ids.ToArray();
        }
        internal static async Task<Fixture> Create()
        {
            var root=Path.Combine(Path.GetTempPath(),"Amifolio-stage9b-cases-"+Guid.NewGuid().ToString("N"));
            var home=Path.Combine(root,"catalog");var data=Path.Combine(root,"data");var projectRoot=Path.Combine(root,"project");
            Directory.CreateDirectory(home);Directory.CreateDirectory(projectRoot);
            await File.WriteAllTextAsync(Path.Combine(home,".codex-global-state.json"),JsonSerializer.Serialize(new Dictionary<string,object>{["local-projects"]=new Dictionary<string,object>{["p"]=new{name="Synthetic",rootPaths=new[]{projectRoot}}}}));
            var store=new PortfolioStore(data);store.EnableCodexReferenceMode(home);await store.RefreshCodexProjectsAsync();var project=store.CodexProjects.Single();
            var files=new string[12];var ids=new string[12];
            for(var i=0;i<files.Length;i++)
            {
                var parent=Path.Combine(projectRoot,$"batch{i/6}");Directory.CreateDirectory(parent);files[i]=Path.Combine(parent,$"item{i:D5}.md");
                await File.WriteAllTextAsync(files[i],"SYNTHETIC SOURCE; NOT PARSER OUTPUT");ids[i]=(await store.ReferenceFileAsync(files[i],project.Id)).Artifact.Id;
            }
            var f=new Fixture(root,home,data,store,project,files,ids);
            using(var db=f.Open())using(var c=db.CreateCommand())
            {
                c.CommandText="UPDATE artifacts SET preview=$text";c.Parameters.AddWithValue("$text","SYNTHETIC query fixture; 甲中文乙 ASCII 😀 %_ A\"B\"\" Ä中ä 中Ä 😀中 e\u0301中 "+new string('x',2200)+" tail-needle\0z after-nul-needle");c.ExecuteNonQuery();
            }
            return f;
        }
    }
}
