using System.Text.Json;
using Amifolio.Core;

namespace Amifolio.Bridge;

internal static partial class CollectionHook
{
    internal static async Task<int> RunAsync(string dataRoot,TextWriter output)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(50));
        try
        {
            using var bytes=new MemoryStream();var buffer=new byte[8192];var input=Console.OpenStandardInput();int count;
            while((count=await input.ReadAsync(buffer,timeout.Token))>0){bytes.Write(buffer,0,count);if(bytes.Length>65536)throw new IOException("Hook input limit.");}
            using var json=JsonDocument.Parse(bytes.ToArray(),new JsonDocumentOptions{MaxDepth=16});var root=json.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("hook_event_name",out var ev)||ev.GetString()!="Stop"||!root.TryGetProperty("cwd",out var cwd)||cwd.ValueKind!=JsonValueKind.String||!Path.IsPathFullyQualified(cwd.GetString()!))throw new IOException("Invalid Stop hook event.");
            var names=new HashSet<string>(StringComparer.Ordinal);
            foreach(var property in root.EnumerateObject())if(!names.Add(property.Name))throw new IOException("Ambiguous Stop hook event.");
            await using var session=await StorageLease.SessionAsync(dataRoot,timeout.Token);
            var store=await Program.OpenReferenceStoreAsync(dataRoot,timeout.Token);
            var current=Path.GetFullPath(cwd.GetString()!);
            var comparison=OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal;
            var project=store.CodexProjects.Where(p=>current.Equals(p.RootPath,comparison)||current.StartsWith(Path.TrimEndingDirectorySeparator(p.RootPath)+Path.DirectorySeparatorChar,comparison))
                .OrderByDescending(p=>p.RootPath.Length).FirstOrDefault()??throw new IOException("Stop cwd is not a discovered project.");
            // RefreshCodexProjectsAsync also invalidates changed/missing reference metadata.
            // Default remains metadata-only. Only the separate project capture consent permits
            // bounded user-turn evidence reads; a caller transcript_path is never used.
            var learning=await LearningFallbackAsync(store,project,current,root,timeout.Token);
            await output.WriteLineAsync(JsonSerializer.Serialize(new{@continue=true,suppressOutput=true,systemMessage=(string?)null,amifolioLearning=learning},Program.JsonOptions));return 0;
        }
        catch(Exception e)when(e is not OutOfMemoryException)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new{@continue=true,suppressOutput=false,systemMessage="Amifolio 项目引用状态刷新未完成；请在应用中刷新项目。本次未复制或解析项目文件。"},Program.JsonOptions));
            await Console.Error.WriteLineAsync("Amifolio hook: "+e.GetType().Name);return 1;
        }
    }
}
