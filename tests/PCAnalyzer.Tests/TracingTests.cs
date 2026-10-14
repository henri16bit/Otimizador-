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
