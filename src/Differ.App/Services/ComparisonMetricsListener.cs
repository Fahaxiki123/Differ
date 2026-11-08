using System.Diagnostics.Tracing;
using System.Globalization;

namespace Differ.App.Services;

internal sealed class ComparisonMetricsListener : EventListener
{
    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Differ-Comparison") EnableEvents(eventSource, EventLevel.Informational);
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        if (eventData.EventId != 1 || eventData.Payload is not { Count: 9 } p) return;
        PerformanceLog.Write((string)p[0]!, (string)p[1]!,
            TimeSpan.FromMilliseconds((double)p[2]!), allocatedBytes: (long)p[3]!, units: (long)p[4]!,
            counters: string.Create(CultureInfo.InvariantCulture,
                $"gc0={p[5]} gc1={p[6]} gc2={p[7]} working_set={p[8]}"));
    }
}
