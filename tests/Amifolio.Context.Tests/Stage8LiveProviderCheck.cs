using System.Text.Json;
using Amifolio.Core;
using Amifolio.Context;

internal static class Stage8LiveProviderCheck
{
    // Explicit opt-in command: saved credentials stay in memory; only synthetic quotes leave the process.
    internal static async Task<int> Run(string configurationRoot,string resultRoot)
    {
        configurationRoot=Path.GetFullPath(configurationRoot);resultRoot=Path.GetFullPath(resultRoot);
        if(resultRoot.StartsWith(configurationRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||configurationRoot.StartsWith(resultRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||resultRoot==configurationRoot)throw new ArgumentException("Separate synthetic root required.");
        Directory.CreateDirectory(resultRoot);var checks=new List<string>();var requests=0;var phase="configuration";
        void Check(bool v,string name){if(!v)throw new InvalidDataException(name);checks.Add(name);}
        try
        {
            var saved=await new ModelConfigurationStore(configurationRoot).LoadAsync();
            Check(saved.RemoteAllowed&&!string.IsNullOrWhiteSpace(saved.ApiKey)&&!string.IsNullOrWhiteSpace(saved.Model),"saved-service-available");
            using var handler=new CountingHandler(new HttpClientHandler{AllowAutoRedirect=false},()=>{if(++requests>1)throw new InvalidOperationException("One request limit.");});
            using var http=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(65)};
            using var f=new LearningContextCases.LearningFixture(http);await f.Initialize();await f.Enable();
            var capture=await f.Turn("以后请简洁回答");
            var receipt=await f.Context.CaptureLearningAsync(capture with{Summary="合成验收：测试表达偏好的学习；不含真实用户资料。"},f.Home);
            Check(receipt.State=="verified","synthetic-source-verified");
            var policy=await f.Context.Learning.PolicyAsync(f.Project!.Id);
            await f.Context.Learning.SetPolicyAsync(f.Project.Id,policy with{Mode="background",Remote=true,Endpoint=saved.Endpoint},policy.Revision);
            await new TaskStore(f.Data).SetRemoteAllowedAsync(true);
            phase="provider";
            Check(await f.Context.RunLearningBackgroundAsync(f.Project.Id,f.Home,new(saved.Endpoint,saved.Model,true),saved.ApiKey,true),"real-background-request-completed");
            var state=await f.Context.Learning.SnapshotAsync(f.Project.Id);
            Check(requests==1&&state.Watermark==receipt.Sequence,"single-request-and-watermark");
            Check(state.Entries.Any(e=>e.Kind=="style"&&e.State=="active"&&e.Text=="偏好简洁回答"),"server-verified-canonical-profile");
            Check(!state.Policy.Share&&(await f.Context.ReadLearningAsync(f.Project.Id,"style","",f.Home)).Count==0,"processing-does-not-grant-sharing");
            Check(!File.Exists(Path.Combine(f.Data,"settings","ai.json")),"credentials-not-copied");
            await File.WriteAllTextAsync(Path.Combine(resultRoot,"report.json"),JsonSerializer.Serialize(new{status="passed",requests,checks,syntheticOnly=true,formalDataModified=false,formalConfigurationModified=false},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PASS stage8 live provider: {requests} request, {checks.Count} checks; synthetic only.");return 0;
        }
        catch(Exception e)
        {
            var category=e is OperationCanceledException?"timeout":e is HttpRequestException?"provider":e is InvalidDataException?"invalid-result":e.GetType().Name;
            await File.WriteAllTextAsync(Path.Combine(resultRoot,"report.json"),JsonSerializer.Serialize(new{status="failed",phase,category,requests,checks,syntheticOnly=true,formalDataModified=false,formalConfigurationModified=false},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"FAIL stage8 live provider: {phase}; {category}; {requests} request; no provider content logged.");return 1;
        }
    }
    private sealed class CountingHandler(HttpMessageHandler inner,Action before):DelegatingHandler(inner)
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){before();return base.SendAsync(request,ct);}}
}
