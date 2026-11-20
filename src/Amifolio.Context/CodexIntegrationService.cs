using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Amifolio.Context;

/// <summary>Explicit local setup only. No model commands, downloads or automatic host restart.</summary>
public sealed partial class CodexIntegrationService
{
    private enum SetupFile { Config, Hooks, Skill }
    private readonly SetupFiles files;
    public string CodexHome { get; }
    public string ConfigPath => Path.Combine(CodexHome,"config.toml");
    public string HooksPath => Path.Combine(CodexHome,"hooks.json");
    public string UserHome { get; }
    public string SkillDirectory => Path.Combine(UserHome,".agents","skills","amifolio");
    public CodexIntegrationService(string? codexHome=null,string? userHome=null)
    {
        UserHome=Path.GetFullPath(userHome??Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        CodexHome=Path.GetFullPath(codexHome??Environment.GetEnvironmentVariable("CODEX_HOME")??Path.Combine(UserHome,".codex"));
        SafeFiles.CheckPath(CodexHome);SafeFiles.CheckPath(SkillDirectory);
        files=new SetupFiles(CodexHome,SkillDirectory);
    }
    public Task<AgentsFileSnapshot> ReadConfigAsync(CancellationToken ct=default)=>files.ReadAsync(SetupFile.Config,ct);
    public string McpSnippet(string bridge,string dataRoot)=>"[mcp_servers.amifolio]\ncommand = "+Toml(Path.GetFullPath(bridge))+"\nargs = [\"--data-root\", "+Toml(Path.GetFullPath(dataRoot))+", \"mcp\"]\n";
    private static string Toml(string value)
    {
        if(value.Any(char.IsControl))throw new IOException("路径包含控制字符。");
        return JsonSerializer.Serialize(value,new JsonSerializerOptions{Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping});
    }
    public async Task<string?> AppendMcpAsync(AgentsFileSnapshot snapshot,string snippet,CancellationToken ct=default)
    {
        if(!snapshot.Path.Equals(ConfigPath,StringComparison.OrdinalIgnoreCase))throw new IOException("配置路径已改变。");
        var text=snapshot.Content;var nl=text.Contains("\r\n",StringComparison.Ordinal)?"\r\n":"\n";snippet=snippet.Replace("\r\n","\n").Replace("\n",nl);
        var block="# Amifolio MCP begin"+nl+snippet+"# Amifolio MCP end";
        var statements=TomlStatements(text);var normalized=statements.Select(line=>Regex.Replace(Regex.Replace(line,"\"(?:[^\"\\\\]|\\\\.)*\"",m=>JsonSerializer.Deserialize<string>(m.Value)??""),"[' \\t]","")).ToArray();
        var existingBlocks=statements.Count(line=>line.Trim()=="# Amifolio MCP begin");
        var targetHeaders=normalized.Count(line=>Regex.IsMatch(line,@"^\[\[?mcp_servers\.amifolio(?:\]|\.)"));
        if(text.Contains(block,StringComparison.Ordinal))
        {
            if(existingBlocks!=1||targetHeaders!=1)throw new IOException("存在含歧义的 MCP 示例或重复配置，请手动比较。");
            await files.SaveAsync(SetupFile.Config,snapshot,text,ct);return null;
        }
        var parent=false;
        foreach(var line in normalized)
        {
            if(line.StartsWith('[')){parent=line=="[mcp_servers]";if(Regex.IsMatch(line,@"^\[\[?mcp_servers\.amifolio(?:\]|\.)"))throw new IOException("已有 Amifolio MCP 配置，未覆盖。");}
            else if(Regex.IsMatch(line,@"^mcp_servers(?:\.amifolio(?:\.|=)|=)")||(parent&&line.StartsWith("amifolio=",StringComparison.Ordinal)))throw new IOException("已有内联 MCP 配置，请先手动比较。");
        }
        if(existingBlocks!=0)throw new IOException("已有不同的 Amifolio 托管配置，未覆盖。");
        return await files.SaveAsync(SetupFile.Config,snapshot,text+(text.Length==0?"":text.EndsWith('\n')?nl:nl+nl)+block+nl,ct);
    }
    private static List<string> TomlStatements(string text)
    {
        var rows=new List<string>();char multi='\0';
        foreach(var line in text.Replace("\r\n","\n").Split('\n'))
        {
            var top=multi=='\0';char quote='\0';var comment=line.Length;
            for(var i=0;i<line.Length;i++)
            {
                var c=line[i];
                if(multi!='\0'){if(c==multi&&i+2<line.Length&&line[i+1]==multi&&line[i+2]==multi){multi='\0';i+=2;}else if(c=='\\'&&multi=='"')i++;continue;}
                if(quote!='\0'){if(c=='\\'&&quote=='"')i++;else if(c==quote)quote='\0';continue;}
                if(c=='#'){comment=i;break;}
                if(c is '\'' or '"'){if(i+2<line.Length&&line[i+1]==c&&line[i+2]==c){multi=c;i+=2;}else quote=c;}
            }
            if(quote!='\0')throw new IOException("TOML 字符串不完整，请先修正。");
            if(top)rows.Add(line.TrimStart().StartsWith("# Amifolio MCP ",StringComparison.Ordinal)?line:line[..comment]);
        }
        if(multi!='\0')throw new IOException("TOML 多行字符串不完整，请先修正。");return rows;
    }
    public async Task InstallSkillAsync(string text,CancellationToken ct=default)
    {
        ValidateSkill(text);SafeFiles.CheckPath(SkillDirectory);
        var legacy=Path.Combine(CodexHome,"skills","amifolio");
        if(Directory.Exists(legacy)&&!legacy.Equals(SkillDirectory,StringComparison.OrdinalIgnoreCase))throw new IOException("旧 Codex skills 目录已有 amifolio，请先移除重复安装。");
        var path=Path.Combine(SkillDirectory,"SKILL.md");
        await using var guard=await SafeFiles.LockAsync(SkillDirectory+".install.lock",ct);
        var snapshot=await files.ReadAsync(SetupFile.Skill,ct);
        if(snapshot.Exists && snapshot.Content!=text)throw new IOException("已安装的 skill 有不同内容，未覆盖。");
        if(Directory.Exists(SkillDirectory)&&Directory.EnumerateFileSystemEntries(SkillDirectory).Any(p=>!p.Equals(path,StringComparison.OrdinalIgnoreCase)&&!p.Equals(path+".amifolio.lock",StringComparison.OrdinalIgnoreCase)))throw new IOException("目标 skill 目录包含其他文件，未覆盖。");
        await files.SaveAsync(SetupFile.Skill,snapshot,text,ct);
    }
    public byte[] SkillArchive(string text)
    {
        ValidateSkill(text);using var bytes=new MemoryStream();using(var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true)){var entry=zip.CreateEntry("amifolio/SKILL.md",CompressionLevel.Fastest);using var writer=new StreamWriter(entry.Open(),new UTF8Encoding(false));writer.Write(text);}return bytes.ToArray();
    }
    private static void ValidateSkill(string text)
    {
        if(Encoding.UTF8.GetByteCount(text)>262144||!text.StartsWith("---",StringComparison.Ordinal)||!Regex.IsMatch(text,@"(?m)^name: amifolio\r?$"))throw new IOException("skill 包格式无效。");
    }
    public string SkillPrompt=> $"将可信下载包中的 amifolio 文件夹安装到 `{SkillDirectory}`。先检查 SKILL.md 的 name 为 amifolio；已有不同内容或旧目录 `{Path.Combine(CodexHome,"skills","amifolio")}` 存在时停止并报告，不覆盖、不执行包内脚本。安装后核对文件内容与路径，重新打开 Codex 会话验证。不要读取或转发 API Key。";
    public async Task<string?> InstallStopHookAsync(string bridge,string dataRoot,CancellationToken ct=default)
    {
        var snapshot=await files.ReadAsync(SetupFile.Hooks,ct);JsonObject doc;
        try{doc=snapshot.Exists?JsonNode.Parse(snapshot.Content) as JsonObject??throw new JsonException():new JsonObject();}
        catch(JsonException e){throw new IOException("hooks.json 无效，未修改。",e);}
        var hooks=doc["hooks"] as JsonObject;
        if(hooks is null){if(doc["hooks"] is not null)throw new IOException("hooks 格式不支持。");hooks=new JsonObject();doc["hooks"]=hooks;}
        var stops=hooks["Stop"] as JsonArray;
        if(stops is null){if(hooks["Stop"] is not null)throw new IOException("Stop 钩子格式不支持。");stops=new JsonArray();hooks["Stop"]=stops;}
        static string Quote(string p){if(!Path.IsPathFullyQualified(p)||p.Any(char.IsControl))throw new IOException("钩子路径无效。");return "'"+p.Replace("'","''")+"'";}
        var script="& "+Quote(bridge)+" '--data-root' "+Quote(dataRoot)+" 'hook-stop'; exit $LASTEXITCODE";
        var command="powershell.exe -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var handler=new JsonObject{{"type","command"},{"command",command},{"timeout",60},{"statusMessage","Amifolio 项目扫描"}};
        var owned=new List<JsonNode>();
        foreach(var stop in stops)
            if(stop?["hooks"] is JsonArray entries)
                foreach(var existing in entries)
                    if(existing?["statusMessage"]?.GetValue<string>() is "Amifolio 收纳核对" or "Amifolio 项目扫描")
                        owned.Add(existing!);
        if(owned.Count>0)
        {
            if(owned.Count==1&&JsonNode.DeepEquals(owned[0],handler))
            {await files.SaveAsync(SetupFile.Hooks,snapshot,snapshot.Content,ct);return null;}
            throw new IOException("已有旧版、不同或重复的 Amifolio Stop 钩子。请在 hooks.json 中手动删除旧的 Amifolio Stop block 后重新安装，并保留其他钩子；不会自动覆盖或删除旧 block。");
        }
        stops.Add(new JsonObject{{"hooks",new JsonArray(handler)}});
        return await files.SaveAsync(SetupFile.Hooks,snapshot,doc.ToJsonString(new JsonSerializerOptions{WriteIndented=true})+"\n",ct);
    }

    // Only this explicit setup service can use these three built-in, instance-bound
    // paths. No arbitrary path or extension is accepted by its read/write methods.
    // AgentsFileService remains independently restricted to AGENTS.md.
    private sealed class SetupFiles(string codexHome,string skillDirectory)
    {
        private const int MaxBytes=4*1024*1024;
        private readonly string config=Path.Combine(codexHome,"config.toml");
        private readonly string hooks=Path.Combine(codexHome,"hooks.json");
        private readonly string skill=Path.Combine(skillDirectory,"SKILL.md");
        private string PathFor(SetupFile target)=>target switch
        {
            SetupFile.Config=>config,SetupFile.Hooks=>hooks,SetupFile.Skill=>skill,
            _=>throw new ArgumentOutOfRangeException(nameof(target))
        };
        internal async Task<AgentsFileSnapshot> ReadAsync(SetupFile target,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();var path=PathFor(target);SafeFiles.CheckPath(path);
            try
            {
                var bytes=await SafeFiles.ReadBoundedAsync(path,MaxBytes,ct).ConfigureAwait(false);
                return new(path,Decode(bytes).Text,SafeFiles.Hash(bytes),true);
            }
            catch(FileNotFoundException){return new(path,"",SafeFiles.Hash([]),false);}
            catch(DirectoryNotFoundException){return new(path,"",SafeFiles.Hash([]),false);}
        }
        internal async Task<string?> SaveAsync(SetupFile target,AgentsFileSnapshot snapshot,string content,CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(snapshot);ArgumentNullException.ThrowIfNull(content);
            ct.ThrowIfCancellationRequested();var path=PathFor(target);
            var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
            if(!string.Equals(snapshot.Path,path,comparison))throw new IOException("接入目标路径已改变，未写入。");
            SafeFiles.CheckPath(path);
            await using var guard=await SafeFiles.LockAsync(path+".amifolio.lock",ct).ConfigureAwait(false);
            var current=await ReadAsync(target,ct).ConfigureAwait(false);
            if(snapshot.Exists!=current.Exists||snapshot.Hash!=current.Hash)throw new IOException("接入文件已被外部修改，请重新读取并比较。");
            if(content==current.Content)return null;
            await using var original=current.Exists?new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete,4096,true):null;
            var originalBytes=original is null?Array.Empty<byte>():await SafeFiles.ReadBoundedAsync(original,MaxBytes,ct).ConfigureAwait(false);
            if(current.Exists&&SafeFiles.Hash(originalBytes)!=snapshot.Hash)throw new IOException("接入文件已被外部修改，未覆盖。");
            var encoding=current.Exists?Decode(originalBytes):(Text:"",Encoding:(Encoding)SafeFiles.Utf8,Bom:Array.Empty<byte>());
            var body=encoding.Encoding.GetBytes(content);
            if(body.Length+encoding.Bom.Length>MaxBytes)throw new InvalidDataException("接入文件超过大小上限。");
            var bytes=encoding.Bom.Concat(body).ToArray();
            var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                await SafeFiles.WriteNewAsync(temp,bytes,ct).ConfigureAwait(false);
                var last=await ReadAsync(target,ct).ConfigureAwait(false);
                if(last.Exists!=snapshot.Exists||last.Hash!=snapshot.Hash)throw new IOException("接入文件已被外部修改，未覆盖。");
                ct.ThrowIfCancellationRequested();SafeFiles.CheckPath(path);
                if(!current.Exists){File.Move(temp,path,false);return null;}
                var backup=path+"."+DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ")+"."+Guid.NewGuid().ToString("N")+".bak";
                File.Replace(temp,path,backup,false);return backup;
            }
            finally{if(File.Exists(temp))File.Delete(temp);}
        }
        private static (string Text,Encoding Encoding,byte[] Bom) Decode(byte[] bytes)
        {
            Encoding encoding=SafeFiles.Utf8;var bom=0;
            if(bytes.AsSpan().StartsWith(new byte[]{0xff,0xfe,0,0})){encoding=new UTF32Encoding(false,false,true);bom=4;}
            else if(bytes.AsSpan().StartsWith(new byte[]{0,0,0xfe,0xff})){encoding=new UTF32Encoding(true,false,true);bom=4;}
            else if(bytes.AsSpan().StartsWith(new byte[]{0xef,0xbb,0xbf}))bom=3;
            else if(bytes.AsSpan().StartsWith(new byte[]{0xff,0xfe})){encoding=new UnicodeEncoding(false,false,true);bom=2;}
            else if(bytes.AsSpan().StartsWith(new byte[]{0xfe,0xff})){encoding=new UnicodeEncoding(true,false,true);bom=2;}
            try{return(encoding.GetString(bytes,bom,bytes.Length-bom),encoding,bytes[..bom]);}
            catch(DecoderFallbackException e){throw new InvalidDataException("接入文件编码无效，原文件未更改。",e);}
        }
    }
}
