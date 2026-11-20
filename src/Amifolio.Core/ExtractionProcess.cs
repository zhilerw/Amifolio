using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Amifolio.Core;

internal sealed class ExtractionProcess
{
    private readonly IReadOnlyList<string>? command;
    private readonly TimeSpan timeout;
    internal Action<int,string>? ObserveExit { get; init; }
    internal ExtractionProcess(IReadOnlyList<string>? command=null,TimeSpan? timeout=null){this.command=command;this.timeout=timeout??TimeSpan.FromSeconds(30);}
    internal async Task<Extraction> RunAsync(ArtifactRecord artifact,CancellationToken ct)
    {
        var command=this.command??LocateWorker();var start=new ProcessStartInfo(command[0]){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in command.Skip(1))start.ArgumentList.Add(arg);
        using var child=Process.Start(start)??throw new IOException("worker_unavailable");
        try
        {
            using var job=WorkerJob.Attach(child);
            StorageEvents.Log.Checkpoint("worker-started:"+child.Id);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(timeout);
            var output=WorkerProtocol.ReadBoundedAsync(child.StandardOutput.BaseStream,1024*1024,deadline.Token);
            var errors=WorkerProtocol.ReadBoundedAsync(child.StandardError.BaseStream,8192,deadline.Token);
            // Kill a writer that exceeds IPC bounds instead of waiting on its blocked pipe.
            _=output.ContinueWith(_=>Kill(child),CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted,TaskScheduler.Default);
            _=errors.ContinueWith(_=>Kill(child),CancellationToken.None,TaskContinuationOptions.OnlyOnFaulted,TaskScheduler.Default);
            try
            {
                var request=JsonSerializer.SerializeToUtf8Bytes(new WorkerProtocol.Request(artifact.ObjectPath,artifact.OriginalName,artifact.Hash));
                await child.StandardInput.BaseStream.WriteAsync(request,deadline.Token).ConfigureAwait(false);child.StandardInput.Close();
                await System.Threading.Tasks.Task.WhenAll(output,errors,child.WaitForExitAsync(deadline.Token)).ConfigureAwait(false);
                ObserveExit?.Invoke(child.ExitCode,System.Text.Encoding.UTF8.GetString(await errors.ConfigureAwait(false)));
                if(child.ExitCode!=0)throw new IOException("worker_crash");
                var reply=JsonSerializer.Deserialize<WorkerProtocol.Reply>(await output.ConfigureAwait(false))??throw new InvalidDataException();
                if(reply.ProcessId!=child.Id || reply.Kind is null || reply.Kind.Length>64 || reply.Status is null || reply.Status.Length>256 || reply.Preview is null || reply.Preview.Length>ArchiveSafety.PreviewChars)throw new InvalidDataException();
                return new(reply.Kind,reply.Status,reply.Preview);
            }
            catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new IOException("worker_timeout");}
            catch(Exception e)when(e is JsonException or InvalidDataException){throw new IOException("worker_protocol");}
        }
        finally{Kill(child);try{await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);}catch(TimeoutException){}}
    }
    private static void Kill(Process process){try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}}
    private static IReadOnlyList<string> LocateWorker()
    {
        foreach(var path in new[]{Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","worker","Amifolio.Worker.exe")),Path.Combine(AppContext.BaseDirectory,"Amifolio.Worker.exe")})
            if(File.Exists(path)&&File.Exists(Path.ChangeExtension(path,".dll"))){ArchiveSafety.NoReparse(path);return [path];}
        // Development test hosts build the separate worker through a project dependency.
        if(Assembly.GetEntryAssembly()?.GetName().Name is string entry && (entry.EndsWith(".Tests",StringComparison.Ordinal)||entry is "Amifolio.Bridge" or "Amifolio.Desktop"))
        {
            for(var directory=new DirectoryInfo(AppContext.BaseDirectory);directory is not null;directory=directory.Parent)
                if(File.Exists(Path.Combine(directory.FullName,"Amifolio.slnx")))
                {var configuration=new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;var path=Path.Combine(directory.FullName,"src","Amifolio.Worker","bin",configuration,"net10.0","Amifolio.Worker.exe");if(File.Exists(path))return [path];break;}
        }
        throw new IOException("worker_unavailable");
    }
}
