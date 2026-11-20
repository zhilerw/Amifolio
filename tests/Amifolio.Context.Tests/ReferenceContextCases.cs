using Amifolio.Core;
using Amifolio.Context;
using System.Text.Json;

internal static class ReferenceContextCases
{
    internal static (string Name,Func<Task> Run)[] All =>
    [
        ("reference context rejects an inflight equal-time content replacement",async()=>{using var f=new ReferenceFixture();await f.Store.RefreshCodexProjectsAsync();var project=f.Store.CodexProjects.Single();var r=await f.Store.ReferenceFileAsync(f.Source,project.Id);await f.Store.ParseReferenceAsync(r.Artifact.Id);using var handler=new ReplaceSource(f.Source);using var client=new HttpClient(handler);var service=new ContextService(f.Store,client);await service.SetEnabledAsync(project.Id,true);await new TaskStore(f.Store.DataRoot).SetRemoteAllowedAsync(true);try{await service.GenerateAsync(project.Id,new("https://synthetic.invalid/v1","fake",true),"dummy");throw new Exception("stale model result accepted");}catch(OperationCanceledException){}var got=await service.ReadAsync(project.Id);if(handler.Calls!=1||got.CandidateDocuments.Count!=0)throw new Exception("stale candidate survived or synthetic provider was not called");}),
        ("reference context explicit preview verifies equal-size equal-time replacement",async()=>{using var f=new ReferenceFixture();await f.Store.RefreshCodexProjectsAsync();var project=f.Store.CodexProjects.Single();var r=await f.Store.ReferenceFileAsync(f.Source,project.Id);await f.Store.ParseReferenceAsync(r.Artifact.Id);var service=new ContextService(f.Store);await service.SetEnabledAsync(project.Id,true);var stamp=File.GetLastWriteTimeUtc(f.Source);await File.WriteAllTextAsync(f.Source,"changed source");File.SetLastWriteTimeUtc(f.Source,stamp);var preview=await service.PreviewGenerationAsync(project.Id);if(preview.Contains("initial source"))throw new Exception("old source would be sent after equal-size equal-time replacement");}),
        ("reference context cold discovery does not upgrade a held read lease",async()=>{using var f=new ReferenceFixture();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));try{await new ContextService(f.Store).ReadAsync(cancellationToken:timeout.Token);}catch(Exception e){throw new Exception(e.ToString());}}),
        ("reference context source change withdraws old publication without deadlock",async()=>{using var f=new ReferenceFixture();await f.Store.RefreshCodexProjectsAsync();var project=f.Store.CodexProjects.Single();var r=await f.Store.ReferenceFileAsync(f.Source,project.Id);await f.Store.ParseReferenceAsync(r.Artifact.Id);var service=new ContextService(f.Store);await service.SetEnabledAsync(project.Id,true);await service.SaveDocumentAsync(project.Id,"memory.md","old-source-memory");await service.PublishAsync(project.Id);var before=await service.ReadAsync(project.Id);var generations=Directory.GetDirectories(Path.Combine(before.Directory,"generations"));await File.WriteAllTextAsync(f.Source,"different source");using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));var got=await service.ReadAsync(project.Id,timeout.Token);if(generations.Any(Directory.Exists)||got.Documents["memory.md"]!="old-source-memory")throw new Exception("stale generation survived or manual memory was lost");})
    ];
    private sealed class ReplaceSource(string source):HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        {Calls++;var stamp=File.GetLastWriteTimeUtc(source);await File.WriteAllTextAsync(source,"changed source",ct);File.SetLastWriteTimeUtc(source,stamp);return new(System.Net.HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{content="{\"summary.md\":\"stale-response\"}"}}}}))};}
    }
    private sealed class ReferenceFixture:IDisposable
    {
        private readonly string root=Path.Combine(Path.GetTempPath(),"Amifolio-reference-context-"+Guid.NewGuid().ToString("N"));
        internal string Source{get;}
        internal PortfolioStore Store{get;}
        internal ReferenceFixture(){var project=Directory.CreateDirectory(Path.Combine(root,"project")).FullName;var home=Directory.CreateDirectory(Path.Combine(root,"codex")).FullName;Source=Path.Combine(project,"notes.md");File.WriteAllText(Source,"initial source");File.WriteAllText(Path.Combine(home,".codex-global-state.json"),JsonSerializer.Serialize(new Dictionary<string,object>{{"local-projects",new Dictionary<string,object>{{"p",new{name="Synthetic",rootPaths=new[]{project}}}}}}));Store=new PortfolioStore(Path.Combine(root,"data"));Store.EnableCodexReferenceMode(home);}
        public void Dispose(){Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();if(!root.StartsWith(Path.Combine(Path.GetTempPath(),"Amifolio-reference-context-"),StringComparison.OrdinalIgnoreCase))throw new Exception("unsafe cleanup");Directory.Delete(root,true);}
    }
}
