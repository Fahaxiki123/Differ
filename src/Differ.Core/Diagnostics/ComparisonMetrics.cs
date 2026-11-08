using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Differ.Core.Diagnostics;

/// <summary>Content-free stage measurements. Allocation/GC counters are process-wide.</summary>
[EventSource(Name = "Differ-Comparison")]
public sealed class ComparisonMetrics : EventSource
{
    public static ComparisonMetrics Log { get; } = new();

    private ComparisonMetrics() { }

    [NonEvent]
    public static IDisposable Measure(string operation, string kind, long units = 0) =>
        new Measurement(operation, kind, units);

    [Event(1, Level = EventLevel.Informational)]
    public void Stage(string operation, string kind, double elapsedMilliseconds,
        long allocatedBytes, long units, int gen0, int gen1, int gen2, long workingSet)
    {
        if (IsEnabled()) WriteEvent(1, operation, kind, elapsedMilliseconds,
            allocatedBytes, units, gen0, gen1, gen2, workingSet);
    }

    private sealed class Measurement(string operation, string kind, long units) : IDisposable
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        private readonly long _allocated = GC.GetTotalAllocatedBytes(false);
        private readonly int _gen0 = GC.CollectionCount(0);
        private readonly int _gen1 = GC.CollectionCount(1);
        private readonly int _gen2 = GC.CollectionCount(2);
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Log.Stage(operation, kind, Stopwatch.GetElapsedTime(_start).TotalMilliseconds,
                Math.Max(0, GC.GetTotalAllocatedBytes(false) - _allocated), units,
                GC.CollectionCount(0) - _gen0, GC.CollectionCount(1) - _gen1,
                GC.CollectionCount(2) - _gen2, Environment.WorkingSet);
        }
    }
}
