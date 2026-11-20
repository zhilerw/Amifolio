using System.Diagnostics;

namespace Amifolio.Desktop;

// Only phase names and clocks are retained. Normal launches never write timing files.
internal static class StartupTiming
{
    private static readonly long origin = Stopwatch.GetTimestamp();
    private static readonly DateTimeOffset originUtc = DateTimeOffset.UtcNow;
    private static readonly DateTimeOffset processStartedAt = ProcessStart();
    private static readonly double initialProcessAge = Math.Max(0, (originUtc - processStartedAt).TotalMilliseconds);
    private static readonly object gate = new();
    private static readonly List<StartupCheckpoint> checkpoints =
        [new("process.start", 0, 0, processStartedAt, 0)];

    internal static void Mark(string stage)
    {
        var managed = Stopwatch.GetElapsedTime(origin).TotalMilliseconds;
        lock (gate)
            checkpoints.Add(new(stage, initialProcessAge + managed, managed, DateTimeOffset.UtcNow, Environment.CurrentManagedThreadId));
    }

    internal static IDisposable Measure(string stage) => new Phase(stage);

    internal static StartupTimeline Snapshot()
    {
        lock (gate)
        {
            var marks = checkpoints.ToArray();
            return new(1, Environment.ProcessId, processStartedAt,
                "OS process start plus monotonic elapsed time; process.start predates managed instrumentation",
                "Initial cached library populated and laid out, controls restored, UI dispatcher heartbeat delivered; not physical input or frame pacing",
                marks.FirstOrDefault(m => m.stage == "startup.ready")?.elapsedMilliseconds, marks);
        }
    }

    private static DateTimeOffset ProcessStart()
    {
        using var process = Process.GetCurrentProcess();
        return new(process.StartTime.ToUniversalTime());
    }

    private sealed class Phase : IDisposable
    {
        private readonly string stage;
        private int ended;
        internal Phase(string stage) { this.stage = stage; Mark(stage + ".begin"); }
        public void Dispose() { if (Interlocked.Exchange(ref ended, 1) == 0) Mark(stage + ".end"); }
    }
}

internal sealed record StartupCheckpoint(string stage, double elapsedMilliseconds, double managedMilliseconds, DateTimeOffset timestampUtc, int threadId);
internal sealed record StartupTimeline(int version, int pid, DateTimeOffset processStartedAt, string clock, string readyDefinition,
    double? readyMilliseconds, IReadOnlyList<StartupCheckpoint> marks);
