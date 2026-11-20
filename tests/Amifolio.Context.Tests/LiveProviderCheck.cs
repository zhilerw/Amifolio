using System.IO.Compression;
using System.Net;
using System.Text.Json;
using Amifolio.Context;
using Amifolio.Core;

internal static class LiveProviderCheck
{
    internal static async Task<int> Run(string configRoot,string testBase)
    {
        configRoot=Path.GetFullPath(configRoot);testBase=Path.GetFullPath(testBase);
        if(testBase.StartsWith(configRoot+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||configRoot.StartsWith(testBase+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||configRoot==testBase)throw new ArgumentException("Live tests require a separate library.");
        Directory.CreateDirectory(testBase);var root=Path.Combine(testBase,"data");var report=Path.Combine(testBase,"report.json");
        if(Directory.Exists(root))throw new InvalidOperationException("Live test library must be fresh.");
        var checks=new List<string>();var phase="configuration";var requests=0;
        void Check(bool value,string name){if(!value)throw new InvalidDataException(name);checks.Add(name);}
        try
        {
            var saved=await new ModelConfigurationStore(configRoot).LoadAsync();
            Check(!string.IsNullOrWhiteSpace(saved.ApiKey)&&!string.IsNullOrWhiteSpace(saved.Model),"saved-configuration-available");
            await new ModelConfigurationStore(root).SaveAsync(saved with{RemoteAllowed=false});
            var reopened=await new ModelConfigurationStore(root).LoadAsync();Check(reopened.ApiKey==saved.ApiKey&&reopened.Model==saved.Model,"isolated-configuration-reopens");
            Check(!(await File.ReadAllTextAsync(Path.Combine(root,"settings","ai.json"))).Contains(saved.ApiKey,StringComparison.Ordinal),"no-plaintext-key");
            phase="archive";var md=Path.Combine(testBase,"synthetic-notes.md");
            await File.WriteAllTextAsync(md,"# 虚构验收项目\n这是一份专用合成测试资料，与真实用户资料无关。用户明确要求界面文案简洁，使用米白与松石绿。项目下一步是检查导入与候选生成功能。\n");
            var docx=Path.Combine(testBase,"synthetic-report.docx");using(var zip=ZipFile.Open(docx,ZipArchiveMode.Create)){using var writer=new StreamWriter(zip.CreateEntry("word/document.xml").Open());writer.Write("<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>虚构验收报告：文件导入成功后，人工检查总结候选。不得将本测试推断为真实用户身份或经历。</w:t></w:r></w:p></w:body></w:document>");}
            var store=new PortfolioStore(root);await store.ImportFileAsync(md);await store.ImportFileAsync(docx);
            Check((await store.ListAsync()).Count==2&&(await store.ListAsync()).All(x=>x.ExtractionStatus=="已提取"),"text-and-office-extraction");
            using var transport=new HttpClientHandler{AllowAutoRedirect=false};
            using var handler=new CountingHandler(transport,()=>{
                var counter=Path.Combine(Path.GetDirectoryName(testBase)!,"live-request-count.txt");
                using var file=new FileStream(counter,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
                using var reader=new StreamReader(file,leaveOpen:true);var raw=reader.ReadToEnd();var count=int.TryParse(raw,out var n)?n:0;
                if(count>=2)throw new InvalidOperationException("Live request budget exhausted.");
                file.Position=0;file.SetLength(0);using var writer=new StreamWriter(file,leaveOpen:true);writer.Write(count+1);writer.Flush();file.Flush(true);requests++;
            });
            using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(60)};var service=new ContextService(store,client);
            await service.SetEnabledAsync(null,true);var payload=await service.PreviewGenerationAsync();Check(payload.Contains("synthetic-notes.md")&&!payload.Contains(configRoot,StringComparison.OrdinalIgnoreCase),"synthetic-only-preview");
            phase="provider";await service.GenerateAsync(null,new(saved.Endpoint,saved.Model,true),saved.ApiKey);
            var snapshot=await service.ReadAsync();Check(snapshot.CandidateDocuments.ContainsKey("candidate-summary.md"),"real-provider-summary-candidate");
            Check((await new TaskStore(root).ListAsync()).Any(x=>x.Kind=="generate"&&x.State=="completed"),"generation-task-completed");
            Check(!snapshot.Documents["summary.md"].Contains(snapshot.CandidateDocuments["candidate-summary.md"],StringComparison.Ordinal),"candidate-not-automatically-adopted");
            phase="adoption";await service.SaveDocumentAsync(null,"summary.md",snapshot.CandidateDocuments["candidate-summary.md"]);await service.PublishAsync();
            Check((await service.ReadAsync()).Documents["summary.md"]==snapshot.CandidateDocuments["candidate-summary.md"],"manual-adoption-persists");
            await service.DeleteDocumentAsync(null,"summary.md");Check(!(await service.ReadAsync()).Documents.ContainsKey("summary.md"),"delete-persists");
            await service.SetEnabledAsync(null,false);Check(!Directory.Exists(snapshot.Directory),"disable-withdraws-export");
            await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{status="passed",requests,checks,formalLibraryModified=false,syntheticOnly=true},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PASS live provider: {requests} request, {checks.Count} checks; synthetic documents only.");return 0;
        }
        catch(Exception e)
        {
            // Deliberately omit exception messages, endpoint, request/response bodies and key.
            var kind=e switch{HttpRequestException h=>"http-"+(h.StatusCode is null?"transport":((int)h.StatusCode).ToString()),OperationCanceledException=>"timeout-or-cancel",InvalidDataException=>"invalid-result",_=>e.GetType().Name};
            await File.WriteAllTextAsync(report,JsonSerializer.Serialize(new{status="failed",phase,kind,requests,checks,formalLibraryModified=false},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"FAIL live provider: phase={phase}; category={kind}; requests={requests}. No provider content logged.");return 1;
        }
    }
    private sealed class CountingHandler(HttpMessageHandler inner,Action before):DelegatingHandler(inner)
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){before();return base.SendAsync(request,ct);}}
}
