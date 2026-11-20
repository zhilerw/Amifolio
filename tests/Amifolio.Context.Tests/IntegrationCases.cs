using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Amifolio.Context;

internal static class IntegrationCases
{
    internal static (string, Func<Task>)[] All => [("integration MCP preserves UTF16 and repeats without duplicate", Mcp), ("integration MCP conflict and external edits are protected", Conflict), ("integration MCP parent inline and unicode names cannot duplicate", ComplexMcp), ("integration skill install precise path and preserves user edits", Skill), ("integration hooks preserve other events and reject duplicates", Hooks), ("integration AGENTS delimiters preserve user content", Delimiters), ("integration setup rejects retargeted snapshots without source writes", SetupBoundary), ("integration old hooks require manual removal without overwriting", LegacyHooks), ("integration changed or duplicate owned hooks require manual removal", ChangedHooks)];
    private static async Task SetupBoundary()
    {
        var p=Root();var s=Service(p);Directory.CreateDirectory(s.CodexHome);
        await File.WriteAllTextAsync(s.ConfigPath,"model='mine'\n",Encoding.Unicode);
        var configBefore=await File.ReadAllBytesAsync(s.ConfigPath);
        var snippet=s.McpSnippet(Path.Combine(p,"bridge.exe"),p);
        foreach(var relative in new[]{"notes.txt","outside/config.toml","outside/hooks.json","outside/SKILL.md","codex/hooks.json","codex/config.toml.bak"})
        {
            var path=Path.Combine(p,relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            const string original="user-owned bytes\r\n";await File.WriteAllTextAsync(path,original,Encoding.Unicode);
            var before=await File.ReadAllBytesAsync(path);
            var entries=Directory.GetFileSystemEntries(p,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
            var snapshot=new AgentsFileSnapshot(path,original,Convert.ToHexStringLower(SHA256.HashData(before)),true);
            await Reject(()=>s.AppendMcpAsync(snapshot,snippet));
            var after=await File.ReadAllBytesAsync(path);var configAfter=await File.ReadAllBytesAsync(s.ConfigPath);
            Check(before.SequenceEqual(after)&&configBefore.SequenceEqual(configAfter));
            Check(entries.SequenceEqual(Directory.GetFileSystemEntries(p,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal)));
        }
    }
    private static async Task LegacyHooks()
    {
        var p=Root();var s=Service(p);Directory.CreateDirectory(s.CodexHome);
        var text="{\"hooks\":{\"SessionStart\":[{\"hooks\":[]}],\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"old command\",\"statusMessage\":\"Amifolio 收纳核对\"}]}]}}";
        await File.WriteAllTextAsync(s.HooksPath,text,Encoding.Unicode);
        await RejectHookUnchanged(s,p);
    }
    private static async Task ChangedHooks()
    {
        foreach(var duplicate in new[]{false,true})
        {
            var p=Root();var s=Service(p);Directory.CreateDirectory(s.CodexHome);
            var bridge=Path.Combine(p,"bridge.exe");var data=Path.Combine(p,"data");
            await s.InstallStopHookAsync(bridge,data);
            var doc=System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(s.HooksPath))!;
            var stops=(System.Text.Json.Nodes.JsonArray)doc["hooks"]!["Stop"]!;
            if(duplicate)stops.Add(stops[0]!.DeepClone());
            else stops[0]!["hooks"]![0]!["command"]="different user-edited command";
            await File.WriteAllTextAsync(s.HooksPath,doc.ToJsonString());
            await RejectHookUnchanged(s,p);
        }
    }
    private static async Task RejectHookUnchanged(CodexIntegrationService service,string root)
    {
        var before=await File.ReadAllBytesAsync(service.HooksPath);
        var entries=Directory.GetFileSystemEntries(root,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        IOException? conflict=null;
        try{await service.InstallStopHookAsync(Path.Combine(root,"bridge.exe"),Path.Combine(root,"data"));}
        catch(IOException e){conflict=e;}
        var after=await File.ReadAllBytesAsync(service.HooksPath);
        Check(before.SequenceEqual(after));
        Check(entries.SequenceEqual(Directory.GetFileSystemEntries(root,"*",SearchOption.AllDirectories).Order(StringComparer.Ordinal)));
        Check(conflict is not null&&conflict.Message.Contains("手动删除",StringComparison.Ordinal));
    }
    private static async Task ComplexMcp()
    {
        var p=Root();var s=Service(p);Directory.CreateDirectory(s.CodexHome);var snippet=s.McpSnippet(Path.Combine(p,"bridge.exe"),p);
        foreach(var text in new[]{"[mcp_servers]\namifolio = {command = 'mine'}\n","[mcp_servers.\"\\u0061mifolio\"]\ncommand='mine'\n","note = '''\n# Amifolio MCP begin\n"+snippet+"# Amifolio MCP end\n'''\n"})
        {await File.WriteAllTextAsync(s.ConfigPath,text);var snap=await s.ReadConfigAsync();await Reject(()=>s.AppendMcpAsync(snap,snippet));Check(await File.ReadAllTextAsync(s.ConfigPath)==text);}
    }
    private static void Check(bool value) { if (!value) throw new Exception("integration invariant failed"); }
    private static async Task Reject(Func<Task> action) { try { await action(); } catch (IOException) { return; } throw new Exception("expected protected conflict"); }
    private static string Root() { var p=Path.Combine(Path.GetTempPath(),"amifolio-integration",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(p);return p; }
    private static CodexIntegrationService Service(string p) => new(Path.Combine(p,"codex"),p);
    private static async Task Mcp()
    {
        var p=Root();var s=Service(p);Directory.CreateDirectory(s.CodexHome);var original="model = \"example\"\r\n[other]\r\nkey = 3\r\n";await File.WriteAllTextAsync(s.ConfigPath,original,Encoding.Unicode);
        var snap=await s.ReadConfigAsync();var snippet=s.McpSnippet(Path.Combine(p,"bridge.exe"),Path.Combine(p,"data"));var backup=await s.AppendMcpAsync(snap,snippet);
        Check(backup is not null && await File.ReadAllTextAsync(backup)==original);Check((await File.ReadAllTextAsync(s.ConfigPath)).StartsWith(original));Check((await File.ReadAllBytesAsync(s.ConfigPath))[0]==255);
        Check(await s.AppendMcpAsync(await s.ReadConfigAsync(),snippet) is null);
    }
    private static async Task Conflict()
    {
        var p=Root();var s=Service(p);Directory.CreateDirectory(s.CodexHome);await File.WriteAllTextAsync(s.ConfigPath,"[mcp_servers.\"amifolio\"]\ncommand='mine'\n");var snap=await s.ReadConfigAsync();
        await Reject(()=>s.AppendMcpAsync(snap,s.McpSnippet(Path.Combine(p,"bridge.exe"),p)));
        await File.WriteAllTextAsync(s.ConfigPath,"model='a'\n");snap=await s.ReadConfigAsync();await File.AppendAllTextAsync(s.ConfigPath,"# external\n");await Reject(()=>s.AppendMcpAsync(snap,s.McpSnippet(Path.Combine(p,"bridge.exe"),p)));
    }
    private static async Task Skill()
    {
        var p=Root();var s=Service(p);var text="---\nname: amifolio\ndescription: Archive deliverables.\n---\nWorkflow\n";
        await s.InstallSkillAsync(text);Check(s.SkillDirectory==Path.Combine(p,".agents","skills","amifolio"));Check(await File.ReadAllTextAsync(Path.Combine(s.SkillDirectory,"SKILL.md"))==text);await s.InstallSkillAsync(text);
        await File.AppendAllTextAsync(Path.Combine(s.SkillDirectory,"SKILL.md"),"mine");await Reject(()=>s.InstallSkillAsync(text));
        using var mem=new MemoryStream(s.SkillArchive(text));using var zip=new ZipArchive(mem);Check(zip.Entries.Single().FullName=="amifolio/SKILL.md");
    }
    private static async Task Hooks()
    {
        var p=Root();var s=Service(p);Directory.CreateDirectory(s.CodexHome);await File.WriteAllTextAsync(s.HooksPath,"{\"hooks\":{\"SessionStart\":[{\"hooks\":[]}]}}");
        await s.InstallStopHookAsync(Path.Combine(p,"bridge.exe"),Path.Combine(p,"data"));var first=await File.ReadAllTextAsync(s.HooksPath);await s.InstallStopHookAsync(Path.Combine(p,"bridge.exe"),Path.Combine(p,"data"));using var doc=System.Text.Json.JsonDocument.Parse(first);var handler=doc.RootElement.GetProperty("hooks").GetProperty("Stop")[0].GetProperty("hooks")[0];var command=handler.GetProperty("command").GetString()!;Check(first==await File.ReadAllTextAsync(s.HooksPath) && first.Contains("SessionStart") && handler.GetProperty("statusMessage").GetString()=="Amifolio 项目扫描" && Encoding.Unicode.GetString(Convert.FromBase64String(command.Split(' ')[^1])).Contains("hook-stop"));
    }
    private static Task Delimiters()
    {
        var s=new AgentsFileService();var content="# 用户规则\r\n---\r\n\r\n保留我。\r\n";var directory=Path.Combine(Root(),"context");var snap=new AgentsFileSnapshot(Path.Combine(Root(),"AGENTS.md"),content,"",true);var updated=s.PreviewManagedBlock(snap,directory,true);
        Check(updated.StartsWith(content) && updated.Contains("---\r\n<!-- folio:begin") && updated.Contains("<!-- folio:end -->\r\n---"));Check(s.PreviewManagedBlock(snap with {Content=updated},directory,false).StartsWith(content));Check(s.PreviewManagedBlock(snap with {Content=updated},directory,true)==updated);return Task.CompletedTask;
    }
}
