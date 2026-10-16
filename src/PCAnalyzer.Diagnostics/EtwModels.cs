using PCAnalyzer.Core;

namespace PCAnalyzer.Diagnostics;

public sealed record EtwCaptureOptions(TimeSpan Duration, int BufferBudgetMiB = 32, long MaximumEvents = 1_000_000)
{
    public void Validate()
    {
        if (Duration < TimeSpan.FromSeconds(1) || Duration > TimeSpan.FromSeconds(120))
            throw new ArgumentOutOfRangeException(nameof(Duration), "A captura deve durar entre 1 e 120 segundos.");
        if (BufferBudgetMiB is < 16 or > 64)
            throw new ArgumentOutOfRangeException(nameof(BufferBudgetMiB), "O orçamento de buffers ETW deve estar entre 16 e 64 MiB.");
        if (MaximumEvents is < 1 or > 2_000_000)
            throw new ArgumentOutOfRangeException(nameof(MaximumEvents), "Limite de eventos entre 1 e 2.000.000.");
    }
}

public sealed record LatencySummary(int Cpu, string Kind, string? Module, long Count,
    double TotalMicroseconds, double MeanMicroseconds, double MaximumMicroseconds,
    double? P95Microseconds = null, double? P99Microseconds = null, long PercentileSampleCount = 0);

public sealed record LatencyEvent(string Kind, int Cpu, ulong RoutineAddress, string? Module,
    double DurationMicroseconds, double? TraceRelativeMilliseconds = null,
    DateTimeOffset? EndedAtUtc = null, long? RawTimestampTicks = null);

/// <summary>One-second trace-relative buckets; final/limited buckets may cover partial intervals.</summary>
public sealed record LatencyTimeBucket(double StartMilliseconds, double EndMilliseconds, string Kind,
    long Count, double TotalMicroseconds, double MaximumMicroseconds);

/// <summary>
/// Source events are counted once. DPC includes ordinary, timer and threaded DPC callbacks;
/// rejected events have invalid processor/duration and never enter aggregates. Unknown modules
/// remain usable latency events. Lost events are reported separately by the ETW source.
/// </summary>
public sealed record EtwEventCounts(long TotalEtwEvents, long DpcEvents, long IsrEvents,
    long AuxiliaryEvents, long RejectedLatencyEvents, long AnalyzedLatencyEvents,
    long UnmappedLatencyEvents, long TimelineEventsRetained);

public sealed record EtwCaptureResult(Availability Status, string Detail, DateTimeOffset StartedAt,
    TimeSpan Duration, string? SessionName, long EventsSeen, int EventsLost, bool Cancelled,
    bool LimitReached, IReadOnlyList<LatencySummary> CpuSummaries,
    IReadOnlyList<LatencySummary> ModuleSummaries, IReadOnlyList<string> Limitations,
    IReadOnlyList<LatencyEvent>? Timeline = null, bool TimelineTruncated = false,
    string TimelineTimeBasis = "ETW relative ms; UTC derived by TraceEvent from trace clock synchronization",
    DateTimeOffset? TraceStartUtc = null, IReadOnlyList<LatencyTimeBucket>? TimeBuckets = null,
    EtwEventCounts? EventCounts = null);
