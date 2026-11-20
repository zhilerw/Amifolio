using Amifolio.Core;
using System.Text.Json;

internal static class CatalogPolicyCases
{
    internal static (string Name,Func<Task> Test)[] All=>
    [
        ("catalog captures protected roots once per operation rather than per entry",OncePerOperation),
        ("catalog refreshes protected roots on the next operation",FreshOperation),
        ("catalog policy snapshot does not bypass exclusions and source immutability",Exclusions)
    ];
    private static async Task OncePerOperation()
    {
        using var f=new TestDirectory();var calls=0;var temporary=0;
        var catalog=new CodexCatalog(f.Home,f.Data){FolderPathResolver=folder=>{calls++;return Environment.GetFolderPath(folder);},TempPathResolver=()=>{temporary++;return Path.GetTempPath();}};
        var p=(await catalog.DiscoverAsync()).Items.Single();calls=0;temporary=0;
        var timer=System.Diagnostics.Stopwatch.StartNew();var entries=await catalog.BrowseAsync(p);timer.Stop();
        Console.WriteLine($"catalog-policy 100 entries: {timer.Elapsed.TotalMilliseconds:F2}ms, folder calls={calls}, temp calls={temporary}");
        Check(entries.Items.Count==100&&!entries.Truncated&&entries.Warnings.Count==0,"complete browse");
        Check(calls==8&&temporary==1,"repeated system folder lookups");
    }
    private static async Task FreshOperation()
    {
        using var f=new TestDirectory();var changed=false;
        var catalog=new CodexCatalog(f.Home,f.Data){FolderPathResolver=folder=>changed&&folder==Environment.SpecialFolder.Windows?f.Project:Environment.GetFolderPath(folder)};
        var p=(await catalog.DiscoverAsync()).Items.Single();Check((await catalog.BrowseAsync(p)).Items.Count==100,"initial allowed root");changed=true;
        var refused=false;try{await catalog.BrowseAsync(p);}catch(IOException){refused=true;}
        Check(refused,"stale policy reused across operations");
    }
    private static async Task Exclusions()
    {
        using var f=new TestDirectory();var source=Path.Combine(f.Project,"sample000.md");var bytes=await File.ReadAllBytesAsync(source);var time=File.GetLastWriteTimeUtc(source);
        await File.WriteAllTextAsync(Path.Combine(f.Project,"auth.json"),"synthetic secret");
        var catalog=new CodexCatalog(f.Home,f.Data);var p=(await catalog.DiscoverAsync()).Items.Single();var result=await catalog.BrowseAsync(p);
        Check(result.Items.Single(e=>e.Name=="auth.json").State=="excluded","configuration exposed");
        Check((await File.ReadAllBytesAsync(source)).SequenceEqual(bytes)&&File.GetLastWriteTimeUtc(source)==time,"source changed");
    }
    private static void Check(bool valid,string reason){if(!valid)throw new Exception(reason);}
    private sealed class TestDirectory:IDisposable
    {
        private readonly string root=Path.Combine(Path.GetTempPath(),"Amifolio-catalog-policy-"+Guid.NewGuid().ToString("N"));
        internal string Home=>Path.Combine(root,"codex");internal string Project=>Path.Combine(root,"project");internal string Data=>Path.Combine(root,"data");
        internal TestDirectory()
        {
            Directory.CreateDirectory(Home);Directory.CreateDirectory(Project);
            for(var i=0;i<100;i++)File.WriteAllText(Path.Combine(Project,$"sample{i:D3}.md"),"synthetic source");
            File.WriteAllText(Path.Combine(Home,".codex-global-state.json"),JsonSerializer.Serialize(new Dictionary<string,object>{{"local-projects",new Dictionary<string,object>{{"one",new{name="synthetic",rootPaths=new[]{Project}}}}}}));
        }
        public void Dispose(){if(!root.StartsWith(Path.Combine(Path.GetTempPath(),"Amifolio-catalog-policy-"),StringComparison.OrdinalIgnoreCase))throw new Exception("unsafe test cleanup");Directory.Delete(root,true);}
    }
}
