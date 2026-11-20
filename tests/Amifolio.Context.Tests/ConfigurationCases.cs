using System.Reflection;
using System.Text.Json;
using Amifolio.Context;
using Amifolio.Core;

internal static class ConfigurationCases
{
    internal static readonly (string Name, Func<Task> Run)[] All =
    [
        ("stage2 AI configuration survives restart with encrypted key", async () => {
            using var f = new ConfigurationFixture();
            await f.Save("dummy-key-never-log", true);
            var saved = await f.Load();
            Check(saved.GetProperty("ApiKey").GetString()=="dummy-key-never-log" && saved.GetProperty("RemoteAllowed").GetBoolean(), "configuration not restored");
            var disk = await File.ReadAllTextAsync(Path.Combine(f.Root,"settings","ai.json"));
            Check(!disk.Contains("dummy-key-never-log") && disk.Contains("ProtectedApiKey"),"plaintext key saved");
        }),
        ("stage2 clear configuration removes key and remote permission", async () => {
            using var f = new ConfigurationFixture();await f.Save("dummy",true);await f.Call("ClearAsync",CancellationToken.None);
            var loaded=await f.Load();Check(loaded.GetProperty("ApiKey").GetString()=="" && !loaded.GetProperty("RemoteAllowed").GetBoolean(),"clear retained key/permission");
        }),
        ("stage2 corrupt configuration is reported and preserved", async () => {
            using var f = new ConfigurationFixture();var path=Path.Combine(f.Root,"settings","ai.json");Directory.CreateDirectory(Path.GetDirectoryName(path)!);await File.WriteAllTextAsync(path,"{bad");
            var failed=false;try{await f.Load();}catch(IOException){failed=true;}
            Check(failed && await File.ReadAllTextAsync(path)=="{bad","corruption silently overwritten");
        }),
        ("stage2 cancelled save preserves prior configuration", async () => {
            using var f = new ConfigurationFixture();await f.Save("before",false);
            using var cts=new CancellationTokenSource();cts.Cancel();try{await f.Save("after",true,cts.Token);}catch(OperationCanceledException){}
            Check((await f.Load()).GetProperty("ApiKey").GetString()=="before","cancel overwrote saved key");
        }),
    ];
    private static void Check(bool good,string message){if(!good)throw new Exception(message);}
    private sealed class ConfigurationFixture:IDisposable
    {
        internal readonly string Root=Path.Combine(Path.GetTempPath(),"Amifolio-configuration-tests",Guid.NewGuid().ToString("N"));
        private object Store()=>Activator.CreateInstance(typeof(ContextService).Assembly.GetType("Amifolio.Context.ModelConfigurationStore")??throw new Exception("configuration store missing"),Root)!;
        internal async Task<object?> Call(string method,params object[] args)
        {
            var store=Store();try{var task=(Task)store.GetType().GetMethod(method)!.Invoke(store,args)!;await task;return task.GetType().GetProperty("Result")?.GetValue(task);}catch(TargetInvocationException e){throw e.InnerException!;}
        }
        internal async Task Save(string key,bool remote,CancellationToken ct=default)
        {
            var type=typeof(ContextService).Assembly.GetType("Amifolio.Context.SavedModelConfiguration")??throw new Exception("saved configuration missing");
            var value=Activator.CreateInstance(type,"https://example.com/v1","test-model",remote,key)!;await Call("SaveAsync",value,ct);
        }
        internal async Task<JsonElement> Load()=>JsonSerializer.SerializeToElement(await Call("LoadAsync",CancellationToken.None));
        public void Dispose(){if(Directory.Exists(Root))Directory.Delete(Root,true);foreach(var suffix in new[]{".store.lock",".session.lock"})if(File.Exists(Root+suffix))File.Delete(Root+suffix);}
    }
}
