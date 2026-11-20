using Amifolio.Core;
using System.Diagnostics.Tracing;

internal static class WorkerObservationCases
{
    internal static async Task Run()
    {
        using var listener=new WorkerListener();
        var artifact=new ArtifactRecord("synthetic","synthetic","input.md","笔记","Codex",null,"",Path.Combine(Path.GetTempPath(),"not-opened-by-fixture.md"),"",0,DateTimeOffset.UtcNow,1,"未解析","");
        // This existing fixture exits with a protocol error after consuming stdin. No file is opened.
        var extractor=new ExtractionProcess(["dotnet",typeof(WorkerObservationCases).Assembly.Location,"--worker-fixture","invalid"]);
        try{await extractor.RunAsync(artifact,CancellationToken.None);}catch(IOException){}
        if(listener.ProcessId<=0||listener.ProcessId==Environment.ProcessId)throw new Exception("worker identity not observable");
    }
    private sealed class WorkerListener:EventListener
    {
        internal int ProcessId;
        internal WorkerListener(){EnableEvents(StorageEvents.Log,EventLevel.Informational);}
        protected override void OnEventWritten(EventWrittenEventArgs e)
        {if(e.EventId==1&&e.Payload?[0] is string stage&&stage.StartsWith("worker-started:",StringComparison.Ordinal))int.TryParse(stage[15..],out ProcessId);}
    }
}
