using PCAnalyzer.Core;
using PCAnalyzer.Diagnostics;

namespace PCAnalyzer.Tests;

public sealed class LatencyPresentationTests
{
    [Fact]
    public void SeparatesDpcAndIsrAndWeightsMeanByEventWithoutCountingModulesTwice()
    {
        var aggregate = new LatencyAggregator();
        for (var i = 0; i < 9; i++) aggregate.ObserveLatency("DPC", 0, 1, 10);
        aggregate.ObserveLatency("TimerDPC", 1, 1, 100);
        aggregate.ObserveLatency("ThreadedDPC", 1, 1, 140);
        aggregate.ObserveLatency("ISR", 1, 1, 9000);

        var result = Capture(aggregate, 12);
        var dpc = LatencyResultPresentation.Summarize(result).Single(s => s.Kind == "DPC");
        var isr = LatencyResultPresentation.Summarize(result).Single(s => s.Kind == "ISR");
        Assert.Equal(11, dpc.AnalyzedCount);
        Assert.Equal(30, dpc.MeanMicroseconds);
        Assert.Equal(140, dpc.MaximumMicroseconds);
        Assert.Equal(1, isr.AnalyzedCount);
        Assert.Equal(9000, isr.MaximumMicroseconds);
        var indicators = LatencyResultPresentation.Indicators(result);
        Assert.Equal(140, indicators.Single(i => i.Id == "dpc.maximum.us").Value);
        Assert.Equal(9000, indicators.Single(i => i.Id == "isr.maximum.us").Value);
        Assert.All(indicators, indicator => Assert.Equal("µs", indicator.Unit));
        Assert.DoesNotContain(indicators, i => i.Id.StartsWith("etw."));
    }

    [Fact]
    public void PercentilesUseIndividualRetainedEventsRatherThanCpuGroupPercentiles()
    {
        var aggregate = new LatencyAggregator();
        for (var i = 1; i <= 100; i++) aggregate.ObserveLatency("DPC", i % 2, 1, i);
        var capture = Capture(aggregate, 100);
        Assert.All(capture.CpuSummaries, group => Assert.Null(group.P99Microseconds));
        var dpc = LatencyResultPresentation.Summarize(capture).Single(s => s.Kind == "DPC");
        Assert.Equal(100, dpc.PercentileSampleCount);
        Assert.Equal(95.05, dpc.P95Microseconds!.Value, 6);
        Assert.Equal(99.01, dpc.P99Microseconds!.Value, 6);
        var percentile = LatencyResultPresentation.Indicators(capture).Single(i => i.Id == "dpc.p99.us");
        Assert.Contains("100 eventos", percentile.Detail);
        Assert.Contains("timeline retida", percentile.Detail);
    }

    [Fact]
    public void TruncatedTimelinePercentilesStayScopedWhileAggregateMaximumIncludesLaterEvents()
    {
        var aggregate = new LatencyAggregator();
        for (var i = 1; i <= 120; i++) aggregate.ObserveLatency("DPC", 0, 1, i);
        var capture = Capture(aggregate, 120);
        capture = capture with { Timeline = capture.Timeline!.Take(100).ToArray(), TimelineTruncated = true };
        var dpc = LatencyResultPresentation.Summarize(capture).Single(s => s.Kind == "DPC");
        Assert.Equal(120, dpc.AnalyzedCount);
        Assert.Equal(120, dpc.MaximumMicroseconds);
        Assert.Equal(60.5, dpc.MeanMicroseconds);
        Assert.Equal(100, dpc.PercentileSampleCount);
        Assert.Equal(99.01, dpc.P99Microseconds!.Value, 6);
        Assert.Contains("somente o trecho retido", LatencyResultPresentation.Indicators(capture).Single(i => i.Id == "dpc.p99.us").Detail);
    }

    [Fact]
    public void MissingKindAndLegacyTimelineDoNotBecomeZeroDurationOrInventedPercentiles()
    {
        var aggregate = new LatencyAggregator();
        aggregate.ObserveLatency("DPC", 0, 1, 30);
        var capture = Capture(aggregate, 1) with { Timeline = null, EventCounts = null };
        var summaries = LatencyResultPresentation.Summarize(capture);
        Assert.Equal(30, summaries.Single(s => s.Kind == "DPC").MaximumMicroseconds);
        Assert.All(summaries, summary =>
        {
            Assert.Null(summary.P95Microseconds);
            Assert.Null(summary.P99Microseconds);
            Assert.Null(summary.RecurrenceEventCount);
        });
        var isr = summaries.Single(s => s.Kind == "ISR");
        Assert.Null(isr.AnalyzedCount);
        Assert.Null(isr.MaximumMicroseconds);
        Assert.Null(isr.MeanMicroseconds);
        Assert.Contains(LatencyResultPresentation.Details(capture), line => line.Contains("contagens separadas indisponíveis"));
    }

    [Theory]
    [InlineData(19, false, false)]
    [InlineData(20, true, false)]
    [InlineData(99, true, false)]
    [InlineData(100, true, true)]
    public void PercentilesRequireMinimumMeasuredSamples(int count, bool p95, bool p99)
    {
        var aggregate = new LatencyAggregator();
        for (var i = 0; i < count; i++) aggregate.ObserveLatency("ISR", 0, 1, 0);
        var isr = LatencyResultPresentation.Summarize(Capture(aggregate, count)).Single(s => s.Kind == "ISR");
        Assert.Equal(p95, isr.P95Microseconds.HasValue);
        Assert.Equal(p99, isr.P99Microseconds.HasValue);
        Assert.Equal(0, isr.MaximumMicroseconds); // A measured zero remains different from missing data.
    }

    [Fact]
    public void RecurrenceUsesKnownTimePositionsPerKindWithoutDriverOrInputLagClaims()
    {
        var aggregate = new LatencyAggregator();
        aggregate.ObserveLatency("DPC", 0, 1, 1200, 100);
        aggregate.ObserveLatency("TimerDPC", 0, 1, 1500, 500);
        aggregate.ObserveLatency("ThreadedDPC", 0, 1, 2000, 1500);
        aggregate.ObserveLatency("ISR", 0, 1, 3000, 3000);
        aggregate.ObserveLatency("DPC", 0, 1, 9999, double.NaN);
        var shortCapture = Capture(aggregate, 5);
        Assert.Null(LatencyResultPresentation.Summarize(shortCapture).Single(s => s.Kind == "ISR").RecurrenceEventCount);
        var capture = shortCapture with { Duration = TimeSpan.FromSeconds(4) };
        var dpc = LatencyResultPresentation.Summarize(capture).Single(s => s.Kind == "DPC");
        var isr = LatencyResultPresentation.Summarize(capture).Single(s => s.Kind == "ISR");
        Assert.Equal(3, dpc.RecurrenceEventCount);
        Assert.Equal(2, dpc.RecurrenceWindowCount);
        Assert.Equal(1, isr.RecurrenceEventCount);
        Assert.Equal(1, isr.RecurrenceWindowCount);
        Assert.Contains(LatencyResultPresentation.Details(capture), line => line.Contains("não é um limite universal"));
        Assert.Contains("não mede input lag físico", LatencyResultPresentation.Indicators(capture)[0].Detail);
    }

    [Fact]
    public void InvalidTimelineEvidenceNeverBecomesPercentileOrRecurrenceData()
    {
        var capture = Capture(new LatencyAggregator(), 0) with
        {
            Timeline = Enumerable.Range(0, 100).SelectMany(_ => new LatencyEvent[]
            {
                new("DPC", 0, 1, null, double.NaN, 100),
                new("DPC", -1, 1, null, 4000, 100),
                new("DPC", 0, 1, null, -1, 100),
                new("Unknown", 0, 1, null, 9000, 100)
            }).ToArray()
        };
        Assert.All(LatencyResultPresentation.Summarize(capture), summary =>
        {
            Assert.Null(summary.MaximumMicroseconds);
            Assert.Null(summary.P95Microseconds);
            Assert.Null(summary.P99Microseconds);
            Assert.Equal(0, summary.PercentileSampleCount);
            Assert.Null(summary.RecurrenceEventCount);
        });
    }

    private static EtwCaptureResult Capture(LatencyAggregator aggregate, long total)
    {
        var snapshot = aggregate.Snapshot();
        var timeline = aggregate.TimelineSnapshot();
        return new(Availability.Available, "Fixture ETW", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(2), null,
            total, 0, false, false, snapshot.Cpu, snapshot.Modules, snapshot.Limitations, timeline.Events,
            timeline.Truncated, TimeBuckets: timeline.Buckets, EventCounts: aggregate.EventCounts(total));
    }
}
