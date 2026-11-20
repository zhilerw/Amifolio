using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Amifolio.Core;

public static class WorkerProtocol
{
    internal sealed record Request(string Path,string OriginalName,string Sha256);
    internal sealed record Reply(string Kind,string Status,string Preview,int ProcessId);
    internal static readonly JsonSerializerOptions Json=new(){Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    public static async Task<int> RunAsync(Stream input,Stream output,CancellationToken ct=default)
    {
        try
        {
            var bytes=await ReadBoundedAsync(input,65_536,ct).ConfigureAwait(false);
            var request=JsonSerializer.Deserialize<Request>(bytes)??throw new InvalidDataException();
            var path=ArchiveSafety.LocalPath(request.Path);ArchiveSafety.Name(request.OriginalName);
            using var file=ArchiveSafety.OpenRead(path);
            if(file.Length>ArchiveSafety.MaxFileBytes || request.Sha256.Length!=64 || !Convert.ToHexString(SHA256.HashData(file)).Equals(request.Sha256,StringComparison.OrdinalIgnoreCase))throw new IOException();
            // Keep this read handle open: writers and renames cannot replace the bytes during parsing.
            var result=await TextExtraction.ExtractAsync(path,request.OriginalName,ct).ConfigureAwait(false);
            var response=JsonSerializer.SerializeToUtf8Bytes(new Reply(result.Kind,result.Status,result.Preview,Environment.ProcessId),Json);
            if(response.Length>1024*1024)throw new InvalidDataException();
            await output.WriteAsync(response,ct).ConfigureAwait(false);await output.FlushAsync(ct).ConfigureAwait(false);return 0;
        }
        catch(Exception e)when(e is IOException or ArgumentException or JsonException or OperationCanceledException or UnauthorizedAccessException)
        {return 2;}
    }
    internal static async Task<byte[]> ReadBoundedAsync(Stream input,int limit,CancellationToken ct)
    {
        using var output=new MemoryStream();var buffer=new byte[8192];int count;
        while((count=await input.ReadAsync(buffer,ct).ConfigureAwait(false))>0){if(output.Length+count>limit)throw new InvalidDataException("Worker output limit exceeded.");output.Write(buffer,0,count);}
        return output.ToArray();
    }
}
