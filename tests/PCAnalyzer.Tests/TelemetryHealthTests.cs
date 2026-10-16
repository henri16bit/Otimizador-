using PCAnalyzer.Core;

namespace PCAnalyzer.Tests;

public sealed class TelemetryHealthTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
    private readonly TelemetryHealthService _service = new();

    [Fact]
    public void BeforeFirstBatchSourcesWaitWithoutPretendingZero()
    {
        var health = Evaluate([]);
        Assert.Equal(new[] { "cpu", "ram", "gpu", "network", "processes" }, health.Select(h => h.Id));
        Assert.All(health, h => { Assert.Equal(TelemetrySourceState.Waiting, h.State); Assert.Null(h.LastSampleAt); });
    }

    [Fact]
    public void LongStartupWithoutDataBecomesStaleInsteadOfWaitingForever() =>
        Assert.All(Evaluate([], started: Now.AddSeconds(-20)), h => Assert.Equal(TelemetrySourceState.Stale, h.State));

    [Fact]
    public void SourceAbsentFromRecentBatchIsUnavailable()
    {
        var health = Evaluate([Cpu(10)], lastBatch: Now);
        Assert.Equal(TelemetrySourceState.Healthy, Source(health, "cpu").State);
        Assert.Equal(TelemetrySourceState.Unavailable, Source(health, "gpu").State);
        Assert.Null(Source(health, "gpu").LastSampleAt);
    }

    [Fact]
    public void CpuAndGpuFrequencyAloneDoNotValidateUtilizationSource()
    {
        var health = Evaluate([Cpu(3500) with { Key = "cpu.clock.mhz", Unit = "MHz" }, Cpu(1000) with { Key = "gpu.0.clock.graphics.mhz", Unit = "MHz" }], lastBatch: Now);
        Assert.Equal(TelemetrySourceState.Unavailable, Source(health, "cpu").State);
        Assert.Equal(TelemetrySourceState.Unavailable, Source(health, "gpu").State);
    }

    [Theory]
    [InlineData(Availability.Unsupported, TelemetrySourceState.Unavailable)]
    [InlineData(Availability.PermissionRequired, TelemetrySourceState.Unavailable)]
    [InlineData(Availability.ToolMissing, TelemetrySourceState.Unavailable)]
    [InlineData(Availability.ReadError, TelemetrySourceState.Error)]
    [InlineData(Availability.Collecting, TelemetrySourceState.Waiting)]
    public void SourceFailureAndWarmupReasonsRemainVisible(Availability status, TelemetrySourceState expected)
    {
        var health = Source(Evaluate([MetricSample.Missing("cpu.total.percent", "CPU", "%", "fixture", Now, status, "fixture reason")]), "cpu");
        Assert.Equal(expected, health.State);
        Assert.Contains("fixture reason", health.Summary);
        Assert.Null(health.LastSampleAt);
    }

    [Fact]
    public void CurrentFailureOverridesOlderValidObservationAndPreservesItsTimestamp()
    {
        var old = Cpu(25) with { Timestamp = Now.AddSeconds(-1) };
        var health = Source(Evaluate([old, MetricSample.Missing(old.Key, old.Component, old.Unit, old.Source, Now, Availability.ReadError, "read failed")]), "cpu");
        Assert.Equal(TelemetrySourceState.Error, health.State);
        Assert.Equal(old.Timestamp, health.LastSampleAt);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void FailureWithSameTimestampCannotBeHiddenByAValidReading(bool failureFirst)
    {
        var valid = Cpu(25);
        var failure = MetricSample.Missing(valid.Key, valid.Component, valid.Unit, valid.Source, Now,
            Availability.ReadError, "read failed in the same batch");
        var health = Source(Evaluate(failureFirst ? [failure, valid] : [valid, failure]), "cpu");
        Assert.Equal(TelemetrySourceState.Error, health.State);
        Assert.Equal(valid.Timestamp, health.LastSampleAt);
        Assert.Contains("same batch", health.Summary);
    }

    [Fact]
    public void PausingKeepsPreviousTimeWithoutPresentingItAsLive()
    {
        var old = Cpu(25) with { Timestamp = Now.AddMinutes(-1) };
        var health = Source(_service.Evaluate([old], [], Now, TimeSpan.FromSeconds(1), true), "cpu");
        Assert.Equal(TelemetrySourceState.Paused, health.State);
        Assert.Equal(old.Timestamp, health.LastSampleAt);
        Assert.Equal("resume-telemetry", health.NextAction.Id);
    }

    [Fact]
    public void FreshnessUsesExpectedCadenceAndOldDataCannotBeHealthy()
    {
        var old = Cpu(25) with { Timestamp = Now.AddSeconds(-20) };
        Assert.Equal(TelemetrySourceState.Stale, Source(Evaluate([old]), "cpu").State);
        Assert.Equal(TelemetrySourceState.Healthy, Source(_service.Evaluate([old], [], Now, TimeSpan.FromSeconds(10)), "cpu").State);
    }

    [Fact]
    public void FreshWarmupMarkersDoNotHideAStuckSource()
    {
        var health = Source(Evaluate([MetricSample.Missing("gpu.telemetry", "GPU", "", "fixture", Now, Availability.Collecting, "warming")], started: Now.AddMinutes(-1)), "gpu");
        Assert.Equal(TelemetrySourceState.Stale, health.State);
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-1)] [InlineData(101)]
    public void InvalidCpuValueNeverCountsAsCurrentHealthyData(double value)
    {
        var health = Source(Evaluate([Cpu(value)]), "cpu");
        Assert.Equal(TelemetrySourceState.Unavailable, health.State);
        Assert.Null(health.LastSampleAt);
    }

    [Fact]
    public void FutureSampleIsReportedAsClockError()
    {
        var health = Source(Evaluate([Cpu(10) with { Timestamp = Now.AddMinutes(1) }]), "cpu");
        Assert.Equal(TelemetrySourceState.Error, health.State);
        Assert.Null(health.LastSampleAt);
    }

    [Fact]
    public void ProcessWorkingSetIsValidEvenWhileCpuRequiresSecondSample()
    {
        var health = _service.Evaluate([], [new(42, "fixture", null, 1024, Now)], Now, TimeSpan.FromSeconds(1));
        Assert.Equal(TelemetrySourceState.Healthy, Source(health, "processes").State);
        Assert.Equal(Now, Source(health, "processes").LastSampleAt);
    }

    private IReadOnlyList<TelemetrySourceHealth> Evaluate(IReadOnlyList<MetricSample> samples, DateTimeOffset? started = null, DateTimeOffset? lastBatch = null) =>
        _service.Evaluate(samples, [], Now, TimeSpan.FromSeconds(1), collectionStartedAt: started, lastBatchAt: lastBatch);
    private static TelemetrySourceHealth Source(IReadOnlyList<TelemetrySourceHealth> health, string id) => Assert.Single(health, h => h.Id == id);
    private static MetricSample Cpu(double value) => new("cpu.total.percent", "CPU", value, "%", "fixture", "fixture", Now, TimeSpan.FromSeconds(1));
}
