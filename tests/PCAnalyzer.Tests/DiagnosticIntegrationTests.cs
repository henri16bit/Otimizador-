using PCAnalyzer.Core;
using PCAnalyzer.Diagnostics;
using PCAnalyzer.Gaming;
using PCAnalyzer.Network;
using PCAnalyzer.Reports;

namespace PCAnalyzer.Tests;

public sealed class DiagnosticIntegrationTests
{
    [Fact]
    public void GatewayRequiresSelectionWhenMultipleConnectionsAreActive()
    {
        var first = Adapter("192.168.0.1"); var second = Adapter("10.0.0.1");
        var snapshot = new NetworkSnapshot(DateTimeOffset.UtcNow, [first, second], Availability.Available);
        Assert.Throws<InvalidOperationException>(() => GatewaySelection.Select(snapshot, null));
        Assert.Equal("10.0.0.1", GatewaySelection.Select(snapshot, second.InterfaceGuid));
        Assert.Throws<InvalidOperationException>(() => GatewaySelection.Select(snapshot, Guid.NewGuid()));
    }
    [Fact]
    public void GatewayDoesNotUseDisconnectedOrUnspecifiedAddresses()
    {
        var adapter = Adapter("0.0.0.0") with { Gateways = ["0.0.0.0", "::", "192.168.1.1"] };
        Assert.Equal("192.168.1.1", GatewaySelection.Select(new(DateTimeOffset.UtcNow, [adapter], Availability.Available), null));
        Assert.Throws<InvalidOperationException>(() => GatewaySelection.Select(new(DateTimeOffset.UtcNow, [adapter with { Status = "Down" }], Availability.Available), null));
    }
    [Fact]
    public void UnadvertisedOriginalNetworkValueIsReadOnlyForSafeRestoration()
    {
        var property = new NetworkProperty("*RSS", "RSS", ["9"], "unknown", ["0", "1"], ["off", "on"], "function", "impact", "recommendation", "unknown");
        Assert.False(property.CanChange); Assert.True((property with { RegistryValues = ["1"] }).CanChange);
    }
    [Fact]
    public void PingInterpretationSeparatesInsufficientCoverageFromNoReply()
    {
        var now = DateTimeOffset.UtcNow;
        var observations = Enumerable.Range(0, 20).Select(i => new PingObservation(i + 1, now.AddSeconds(i), i == 19 ? "TimedOut" : "Success", i == 19 ? null : 1d)).ToArray();
        var measurement = ManualPingService.Summarize("fixture", now, 20, observations);
        Assert.Equal(FindingCategory.PossibleProblem, Assert.Single(PingInterpretation.Analyze(measurement)).Category);
        Assert.Contains("não demonstra perda no jogo", PingInterpretation.Analyze(measurement)[0].Explanation);
        Assert.Equal(FindingCategory.InsufficientData, PingInterpretation.Analyze(measurement with { Cancelled = true })[0].Category);
    }
    [Fact]
    public void DpcAttributionRequiresKnownModuleAndRecurrenceAcrossTimeBuckets()
    {
        var events = Enumerable.Range(0, 3).Select(i => new LatencyEvent("DPC", 0, 123, "driver.sys", 1500, i * 1100, DateTimeOffset.UtcNow.AddMilliseconds(i * 1100))).ToArray();
        var capture = new EtwCaptureResult(Availability.Available, "fixture", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(3), null, 3, 0, false, false, [], [], [], events);
        Assert.Contains(LatencyInterpretation.Analyze(capture), f => f.Category == FindingCategory.PossibleProblem && f.Evidence.Count == 3);
        Assert.DoesNotContain(LatencyInterpretation.Analyze(capture with { Timeline = events.Select(e => e with { Module = null }).ToArray() }), f => f.Category == FindingCategory.PossibleProblem);
        Assert.DoesNotContain(LatencyInterpretation.Analyze(capture with { Timeline = events.Select(e => e with { TraceRelativeMilliseconds = 1 }).ToArray() }), f => f.Category == FindingCategory.PossibleProblem);
    }
    [Fact]
    public void RelativeFramesNeverAcquireAnInventedUtcClock()
    {
        var frames = Enumerable.Range(0, 120).Select(i => new FrameSample(i, i * 10, i < 3 ? 35 : 10, false, null)).ToArray();
        var stats = new FrameStatistics(120, 10, 100, 10, 35, 35, 30, 30, Availability.Available, []);
        var stream = new FrameStream(new("fixture", 1, "0x1"), "fixture", "TimeInSeconds", "Tempo da captura em ms, sem época civil", frames, 0, 0, stats, stats, []);
        Assert.Null(FrameInterpretation.UtcTimestamp(stream, frames[0]));
        var finding = Assert.Single(FrameInterpretation.Analyze(new([stream], 120, 0, 0, true, [])));
        Assert.Equal(FindingCategory.PossibleProblem, finding.Category); Assert.All(finding.Evidence, e => Assert.Null(e.Timestamp));
    }
    [Fact]
    public void CorrelationUsesOnlyAlignedFineEnoughSamplesAndMakesNoCausalClaim()
    {
        var start = DateTimeOffset.UtcNow;
        var frames = Enumerable.Range(0, 100).Select(i => new MetricSample("frames.1.frametime.ms", "Jogos", i == 50 ? 50 : 10,
            "ms", "fixture UTC", "fixture", start.AddMilliseconds(i * 10), TimeSpan.Zero));
        var cpu = new MetricSample("cpu.total.percent", "CPU", 92, "%", "fixture", "counter", start.AddMilliseconds(500), TimeSpan.FromSeconds(1));
        var slow = cpu with { Key = "memory.available.bytes", SamplingInterval = TimeSpan.FromSeconds(5) };
        var stale = cpu with { Key = "gpu.0.utilization.percent", Timestamp = start.AddMinutes(-1) };
        var finding = Assert.Single(TemporalDiagnosticAnalyzer.Analyze(frames.Concat([cpu, slow, stale]).ToArray()));
        Assert.Contains(finding.Evidence, e => e.MetricKey == cpu.Key);
        Assert.DoesNotContain(finding.Evidence, e => e.MetricKey == slow.Key || e.MetricKey == stale.Key);
        Assert.Contains("não comprova causa", finding.Explanation); Assert.Equal(Confidence.Low, finding.Confidence);
    }
    [Fact]
    public void DifferentOrCancelledTestsAreNotComparedAsPerformanceGains()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new TestSession(Guid.NewGuid(), "cpu", "CPU", TestStatus.Completed, now, now, new("end"));
        Assert.Equal(Availability.InsufficientData, TestResultComparer.Compare(a, a with { TestId = "ram" }).Availability);
        Assert.Equal(Availability.InsufficientData, TestResultComparer.Compare(a, a with { Status = TestStatus.Cancelled }).Availability);
    }
    [Fact]
    public void PingComparisonsRequireTheSameDestinationAndMethod()
    {
        var now = DateTimeOffset.UtcNow;
        var context = new DiagnosticSession(Guid.NewGuid(), "fixture", now, now, new("idle", "normal", 1), new(now, [], []), [], []);
        var result = new TechnicalResult("ping", [], Indicators: [new("rtt", "RTT", 10, "ms", "fixture")],
            Session: context, ComparisonKey: "ping:192.0.2.1:20:32:1000");
        var before = new TestSession(Guid.NewGuid(), "ping", "Ping", TestStatus.Completed, now, now, new("end"), result);
        var otherDestination = before with { Result = result with { ComparisonKey = "ping:192.0.2.2:20:32:1000" } };
        Assert.Equal(Availability.InsufficientData, TestResultComparer.Compare(before, otherDestination).Availability);
        Assert.Equal(Availability.InsufficientData, TestResultComparer.Compare(before, before with { Result = result with { ComparisonKey = null } }).Availability);
        Assert.Contains(TestResultComparer.Compare(before, before).Lines, line => line.StartsWith("RTT:"));
    }
    private static NetworkAdapter Adapter(string gateway) => new(Guid.NewGuid(), "Ethernet", "fixture", "Ethernet", "Up", 1_000_000_000,
        [], [gateway], 0, 0, 0, 0);
}
