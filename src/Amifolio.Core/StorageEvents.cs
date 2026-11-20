using System.Diagnostics.Tracing;

namespace Amifolio.Core;

/// <summary>Opt-in operation checkpoints; never contains paths, document content or credentials.</summary>
[EventSource(Name = "Amifolio-Storage")]
public sealed class StorageEvents : EventSource
{
    public static readonly StorageEvents Log = new();
    private StorageEvents() { }
    [Event(1, Level = EventLevel.Informational)]
    public void Checkpoint(string stage) { StorageFaults.Handler.Value?.Invoke(stage); if (IsEnabled()) WriteEvent(1, stage); }
}
