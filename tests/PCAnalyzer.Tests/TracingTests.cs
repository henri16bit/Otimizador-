using PCAnalyzer.Core;
using PCAnalyzer.Diagnostics;
using Xunit;

namespace PCAnalyzer.Tests;

public sealed class TracingTests
{
    [Fact]
    public void AttributesOnlyUniqueObservedKernelImageAndRespectsUnload()
    {
        var aggregate = new LatencyAggregator();
        aggregate.ObserveKernelImage(100, 0x1000, 100, "application.dll");
        aggregate.ObserveLatency("DPC", 0, 0x1010, 10);
        Assert.Null(Assert.Single(aggregate.Snapshot().Modules).Module);

        aggregate.ObserveKernelImage(4, 0x1000, 100, "driver.sys");
        aggregate.ObserveLatency("DPC", 0, 0x1010, 30);
        aggregate.ObserveKernelImage(0, 0x1005, 20, "overlap.sys");
        aggregate.ObserveLatency("ISR", 1, 0x1010, 5);
        Assert.Null(aggregate.Snapshot().Modules.Single(x => x.Kind == "ISR").Module);

        aggregate.ForgetKernelImage(0, 0x1005);
        aggregate.ObserveLatency("ISR", 1, 0x1010, 15);
        var resolved = aggregate.Snapshot().Modules.Single(x => x.Kind == "ISR" && x.Module == "driver.sys");
        Assert.Equal(1, resolved.Count);
        Assert.Equal(15, resolved.MeanMicroseconds);
        Assert.Equal("driver.sys", aggregate.TimelineSnapshot().Events.Last().Module);
    }

    [Fact]
    public void RejectsInvalidDurationsAndExcludesImageUpperBoundary()
    {
        var aggregate = new LatencyAggregator();
        aggregate.ObserveKernelImage(4, 0x1000, 10, "driver.sys");
        aggregate.ObserveLatency("DPC", 0, 0x1009, 10);
        aggregate.ObserveLatency("DPC", 0, 0x100a, 30);
        aggregate.ObserveLatency("DPC", 0, 0x1001, double.NaN);
        aggregate.ObserveLatency("DPC", 0, 0x1001, -1);
        aggregate.ObserveLatency("DPC", -1, 0x1001, 5);
        var summary = Assert.Single(aggregate.Snapshot().Cpu);
        Assert.Equal(2, summary.Count);
        Assert.Equal(20, summary.MeanMicroseconds);
        Assert.Equal(30, summary.MaximumMicroseconds);
        Assert.Null(aggregate.TimelineSnapshot().Events.Last().Module);
        Assert.Contains(aggregate.Snapshot().Limitations, x => x.Contains("3 eventos"));
    }

    [Fact]
    public void PreservesTimestampsAndComputesCompletePercentilesAndRecurrence()
    {
        var aggregate = new LatencyAggregator();
        var utc = DateTimeOffset.Parse("2026-10-14T12:00:00Z");
        for (var i = 0; i < 100; i++)
            aggregate.ObserveLatency("DPC", 0, 0x1234, i + 1, i * 20, utc.AddMilliseconds(i * 20), 10_000 + i);
        var summary = Assert.Single(aggregate.Snapshot().Cpu);
        Assert.Equal(100, summary.Count);
        Assert.Equal(50.5, summary.MeanMicroseconds);
        Assert.Equal(95.05, summary.P95Microseconds!.Value, 6);
        Assert.Equal(99.01, summary.P99Microseconds!.Value, 6);
        Assert.Equal(100, summary.PercentileSampleCount);
        var timeline = aggregate.TimelineSnapshot();
        Assert.False(timeline.Truncated);
        Assert.Equal(10_099, timeline.Events.Last().RawTimestampTicks);
        Assert.Equal(utc.AddMilliseconds(1980), timeline.Events.Last().EndedAtUtc);
        Assert.Equal(1980, timeline.Events.Last().TraceRelativeMilliseconds);
        Assert.Equal(2, timeline.Buckets.Count);
        Assert.All(timeline.Buckets, b => Assert.Equal(50, b.Count));
    }

    [Fact]
    public void KeepsAllAggregatesButDoesNotInventPercentilesAfterTimelineLimit()
    {
        var aggregate = new LatencyAggregator();
        for (var i = 0; i <= LatencyAggregator.MaximumTimelineEvents; i++)
            aggregate.ObserveLatency("DPC", 0, 0x1234, 5);
        var timeline = aggregate.TimelineSnapshot();
        Assert.True(timeline.Truncated);
        Assert.Equal(LatencyAggregator.MaximumTimelineEvents, timeline.Events.Count);
        var summary = Assert.Single(aggregate.Snapshot().Cpu);
        Assert.Equal(LatencyAggregator.MaximumTimelineEvents + 1, summary.Count);
        Assert.Equal(5, summary.MeanMicroseconds);
        Assert.Null(summary.P95Microseconds);
        Assert.Null(summary.P99Microseconds);
        Assert.Equal(LatencyAggregator.MaximumTimelineEvents, summary.PercentileSampleCount);
        var counts = aggregate.EventCounts(LatencyAggregator.MaximumTimelineEvents + 4);
        Assert.Equal(3, counts.AuxiliaryEvents);
        Assert.Equal(LatencyAggregator.MaximumTimelineEvents + 1, counts.AnalyzedLatencyEvents);
        Assert.Equal(LatencyAggregator.MaximumTimelineEvents, counts.TimelineEventsRetained);
    }

    [Fact]
    public void EventCountsAreAdditiveAndRejectedEventsNeverBecomeAnalyzedDurations()
    {
        var aggregate = new LatencyAggregator();
        aggregate.ObserveKernelImage(4, 0x1000, 100, "known.sys");
        aggregate.ObserveLatency("DPC", 0, 0x1010, 10);
        aggregate.ObserveLatency("TimerDPC", 0, 0x1010, 20);
        aggregate.ObserveLatency("ThreadedDPC", 0, 0x1010, double.NaN);
        aggregate.ObserveLatency("ISR", 0, 0x9000, 5);
        aggregate.ObserveLatency("ISR", -1, 0x1010, 2);
        var counts = aggregate.EventCounts(8);
        Assert.Equal(3, counts.DpcEvents);
        Assert.Equal(2, counts.IsrEvents);
        Assert.Equal(3, counts.AuxiliaryEvents);
        Assert.Equal(2, counts.RejectedLatencyEvents);
        Assert.Equal(3, counts.AnalyzedLatencyEvents);
        Assert.Equal(1, counts.UnmappedLatencyEvents);
        Assert.Equal(3, counts.TimelineEventsRetained);
        Assert.Equal(counts.TotalEtwEvents, counts.DpcEvents + counts.IsrEvents + counts.AuxiliaryEvents);
        Assert.Equal(counts.DpcEvents + counts.IsrEvents, counts.RejectedLatencyEvents + counts.AnalyzedLatencyEvents);
        Assert.Equal(counts.AnalyzedLatencyEvents, aggregate.Snapshot().Cpu.Sum(s => s.Count));
        Assert.Equal(counts.AnalyzedLatencyEvents, aggregate.Snapshot().Modules.Sum(s => s.Count));
        Assert.Throws<ArgumentOutOfRangeException>(() => aggregate.EventCounts(4));
    }

    [Fact]
    public void InvalidRelativeClocksPreserveDurationWithoutInventingPositionOrBreakingPersistence()
    {
        var aggregate = new LatencyAggregator();
        foreach (var relative in new[] { double.NaN, double.PositiveInfinity, -1d, double.MaxValue })
            aggregate.ObserveLatency("DPC", 0, 0x1234, 10, relative);
        var result = Result(aggregate, 4);
        Assert.Equal(4, result.EventCounts!.AnalyzedLatencyEvents);
        Assert.All(result.Timeline!, e => Assert.Null(e.TraceRelativeMilliseconds));
        Assert.Empty(result.TimeBuckets!);
        Assert.Equal(40, Assert.Single(result.CpuSummaries).TotalMicroseconds);
        Assert.Contains(result.Limitations, limitation => limitation.Contains("4 eventos com timestamp relativo inválido"));
        var restored = System.Text.Json.JsonSerializer.Deserialize<EtwCaptureResult>(System.Text.Json.JsonSerializer.Serialize(result))!;
        Assert.Equal(4, restored.Timeline!.Count);
        Assert.DoesNotContain(LatencyInterpretation.Analyze(restored), finding => finding.Category == FindingCategory.PossibleProblem);
    }

    [Fact]
    public void LegacyTraceHistoryWithoutSeparateCountsRemainsReadableWithoutInventingCounts()
    {
        const string legacyJson = """
            {"Status":0,"Detail":"Legacy ETW","StartedAt":"2026-10-14T12:00:00Z","Duration":"00:00:01",
             "SessionName":null,"EventsSeen":9,"EventsLost":0,"Cancelled":false,"LimitReached":false,
             "CpuSummaries":[],"ModuleSummaries":[],"Limitations":[]}
            """;
        var result = System.Text.Json.JsonSerializer.Deserialize<EtwCaptureResult>(legacyJson)!;
        Assert.Null(result.EventCounts);
        Assert.Equal(9, result.EventsSeen);
        Assert.Contains("contagens separadas indisponíveis", LatencyInterpretation.DescribeCoverage(result));
        Assert.Equal(FindingCategory.InsufficientData, Assert.Single(LatencyInterpretation.Analyze(result)).Category);
    }

    [Fact]
    public void AMaximumAloneNeverNamesADriverAsTheCause()
    {
        var aggregate = new LatencyAggregator();
        aggregate.ObserveKernelImage(4, 0x1000, 100, "driver.sys");
        aggregate.ObserveLatency("DPC", 0, 0x1010, 20_000, 1000);
        var result = Result(aggregate, 1);
        var finding = Assert.Single(LatencyInterpretation.Analyze(result));
        Assert.Equal(FindingCategory.InsufficientData, finding.Category);
        Assert.Equal(Confidence.Low, finding.Confidence);
        Assert.Equal("dpc-coverage", finding.Id);
    }

    [Fact]
    public void PartialTraceKeepsObservedRecurrenceButRequiresCoverageWarning()
    {
        var aggregate = new LatencyAggregator();
        aggregate.ObserveKernelImage(4, 0x1000, 100, "driver.sys");
        aggregate.ObserveLatency("DPC", 0, 0x1010, 1200, 100);
        aggregate.ObserveLatency("DPC", 0, 0x1010, 1500, 500);
        aggregate.ObserveLatency("DPC", 0, 0x1010, 1800, 1200);
        var result = Result(aggregate, 4) with { Status = Availability.ReadError, EventsLost = 2, Cancelled = true };
        var findings = LatencyInterpretation.Analyze(result);
        var recurrence = Assert.Single(findings, f => f.Category == FindingCategory.PossibleProblem);
        Assert.Equal(Confidence.Low, recurrence.Confidence);
        Assert.Contains("não", recurrence.Explanation);
        Assert.Contains(findings, f => f.Id == "dpc-partial" && f.Category == FindingCategory.InsufficientData);
        var coverage = LatencyInterpretation.DescribeCoverage(result);
        Assert.Contains("auxiliares: 1", coverage);
        Assert.Contains("Perdidos pela fonte: 2", coverage);
        Assert.Equal(3, result.Timeline!.Count);
    }

    private static EtwCaptureResult Result(LatencyAggregator aggregate, long total)
    {
        var snapshot = aggregate.Snapshot();
        var timeline = aggregate.TimelineSnapshot();
        return new(Availability.Available, "Fixture ETW", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2), null,
            total, 0, false, false, snapshot.Cpu, snapshot.Modules, snapshot.Limitations, timeline.Events,
            timeline.Truncated, TimeBuckets: timeline.Buckets, EventCounts: aggregate.EventCounts(total));
    }

    [Theory]
    [InlineData(0, 32, 1_000_000)]
    [InlineData(121, 32, 1_000_000)]
    [InlineData(10, 65, 1_000_000)]
    [InlineData(10, 15, 1_000_000)]
    [InlineData(10, 32, 2_000_001)]
    [InlineData(10, 32, 0)]
    public void RejectsUnboundedCaptureOptions(int seconds, int memoryMiB, long events) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new EtwCaptureOptions(TimeSpan.FromSeconds(seconds), memoryMiB, events).Validate());

    [Fact]
    public async Task ReportsUnsupportedOnNonWindowsWithoutAttemptingElevation()
    {
        if (OperatingSystem.IsWindows()) return;
        var result = await new EtwTraceService().CaptureAsync(new EtwCaptureOptions(TimeSpan.FromSeconds(1)));
        Assert.Equal(Availability.Unsupported, result.Status);
        Assert.Empty(result.CpuSummaries);
        Assert.Null(result.SessionName);
    }
}
