using System.Collections.Concurrent;
using PCAnalyzer.Core;

namespace PCAnalyzer.Tests;

public sealed class DiagnosticPhasesTests
{
    [Fact]
    public async Task ConfigurationCompletesBeforeBaselineAndTimedObservation()
    {
        var probe = new PhaseProbe { HoldConfiguration = true };
        var running = Run(probe, new(ObservationSeconds: 1));
        await probe.ConfigurationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new[] { "prepare", "configuration" }, probe.Calls.ToArray());
        Assert.Equal(0, probe.Collections);
        probe.ConfigurationReady.SetResult();
        var result = await running;
        var calls = probe.Calls.ToArray();
        Assert.True(Array.IndexOf(calls, "configuration-complete") < Array.IndexOf(calls, "network-start"));
        Assert.True(Array.IndexOf(calls, "network-start") < Array.IndexOf(calls, "reset"));
        Assert.True(Array.IndexOf(calls, "reset") < Array.IndexOf(calls, "collect"));
        Assert.Same(probe.Audit, result.Audit);
        Assert.Contains(result.Report!.Stages, s => s.Id == "configuration");
        Assert.Contains(result.Report.Stages, s => s.Id == "observation");
        Assert.DoesNotContain(calls, s => s is "latency" or "ping" or "frames");
    }

    [Fact]
    public async Task CancelledInventoryKeepsOnlyItsEvidenceAndDoesNotStartObservationOrTests()
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new PhaseProbe { CancelConfiguration = cancellation };
        var result = await Run(probe, new(ObservationSeconds: 30, IncludeLatency: true, IncludePing: true), cancellation.Token);
        Assert.True(result.Cancelled);
        Assert.Same(probe.Audit, result.Audit);
        Assert.Equal(0, probe.Collections);
        Assert.DoesNotContain(probe.Calls, s => s is "reset" or "latency" or "ping" or "frames" or "network-start");
        Assert.Empty(result.Session!.Samples);
        Assert.Contains("Inventário de configurações cancelado", result.Session.Context.Notes);
    }

    [Fact]
    public async Task FailedConfigurationDoesNotDiscardIndependentMeasurements()
    {
        var probe = new PhaseProbe { ConfigurationFails = true };
        var result = await Run(probe, new(ObservationSeconds: 1));
        Assert.True(probe.Collections > 0);
        Assert.Null(result.Audit);
        Assert.True(result.Partial);
        Assert.Contains(result.Report!.Stages, s => s.Id == "configuration" && s.Status == Availability.InsufficientData);
        Assert.Contains(result.Session!.Samples, s => s.Key == "cpu.total.percent");
        Assert.Contains("Inventário de configurações incompleto", result.Session.Context.Notes);
    }

    [Fact]
    public async Task CancelledNetworkIdentificationDoesNotStartObservationOrOptionalCaptures()
    {
        var probe = new PhaseProbe { NetworkCancelled = true };
        var result = await Run(probe, new(ObservationSeconds: 1, IncludeLatency: true, IncludePing: true));
        Assert.True(result.Cancelled);
        Assert.Same(probe.Audit, result.Audit);
        Assert.DoesNotContain(probe.Calls, s => s is "reset" or "collect" or "latency" or "ping" or "frames" or "network-end");
        Assert.Empty(result.Session!.Samples);
    }

    [Fact]
    public async Task ExplicitLatencyDurationAndWorkloadReachOnlyTheSelectedCapture()
    {
        var probe = new PhaseProbe();
        var result = await Run(probe, new(ObservationSeconds: 1, IncludeLatency: true,
            Workload: "Sessão representativa", LatencySeconds: 120));
        Assert.Equal(120, probe.CaptureOptions!.LatencySeconds);
        Assert.Equal("Sessão representativa", probe.CaptureOptions.Workload);
        Assert.Equal(1, probe.Calls.Count(s => s == "latency"));
        Assert.Contains(":120:", result.ComparisonKey);
        Assert.Contains("120 s", result.Session!.Context.Notes);
    }

    [Theory]
    [InlineData(30)] [InlineData(60)] [InlineData(120)]
    public void SupportedDpcDurationsAreExplicitOptions(int seconds) => new DiagnosticOptions(LatencySeconds: seconds).Validate();

    [Theory]
    [InlineData(0)] [InlineData(31)] [InlineData(121)]
    public void UnsupportedDpcDurationIsRejectedBeforeAnyWork(int seconds) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new DiagnosticOptions(LatencySeconds: seconds).Validate());

    [Fact]
    public async Task BaselineFailureIsVisibleAndCannotCertifyTheObservationWindow()
    {
        var probe = new PhaseProbe { ResetFails = true };
        var result = await Run(probe, new(ObservationSeconds: 1));
        Assert.Contains(result.Report!.Stages, s => s.Id == "observation-baseline" && s.Status == Availability.ReadError);
        Assert.Contains(result.Limitations!, l => l.Contains("inventário", StringComparison.OrdinalIgnoreCase));
        Assert.True(result.Partial);
    }

    [Fact]
    public async Task InventoryProgressCountsCompletedChecksIncludingUnavailableReadings()
    {
        var updates = new ConcurrentQueue<TestProgress>();
        var coordinator = new PerformanceAuditCoordinator([new Provider("a", Availability.Available), new Provider("b", Availability.ReadError)]);
        var result = await coordinator.RunAsync(new InlineProgress<TestProgress>(updates.Enqueue), CancellationToken.None);
        Assert.Equal(2, result.Assessments.Count);
        var final = updates.Single(p => p.Fraction == 1);
        Assert.Equal(2, final.CompletedUnits);
        Assert.Contains("2/2 fontes", final.Detail);
        Assert.Contains("ausentes não contam como sucesso", final.Detail);
    }

    [Fact]
    public async Task ConcurrentInventoryProgressNeverRegressesOrLosesTheFinalCount()
    {
        var updates = new ConcurrentQueue<TestProgress>();
        var providers = Enumerable.Range(0, 64).Select(i => new Provider(i.ToString(), Availability.Available));
        await new PerformanceAuditCoordinator(providers).RunAsync(new InlineProgress<TestProgress>(updates.Enqueue), CancellationToken.None);
        var values = updates.ToArray();
        Assert.Equal(64, values[^1].CompletedUnits);
        Assert.Equal(1, values[^1].Fraction);
        Assert.All(values.Zip(values.Skip(1)), pair =>
        {
            Assert.True(pair.First.Fraction <= pair.Second.Fraction);
            Assert.True(pair.First.CompletedUnits <= pair.Second.CompletedUnits);
        });
    }

    private static Task<TechnicalResult> Run(PhaseProbe probe, DiagnosticOptions options, CancellationToken ct = default) =>
        new DiagnosticOrchestrator(probe).RunAsync(options, new InlineProgress<TestProgress>(_ => { }), null, ct);

    private sealed class PhaseProbe : IDiagnosticProbe
    {
        public ConcurrentQueue<string> Calls { get; } = new();
        public int Collections;
        public bool HoldConfiguration, ConfigurationFails, ResetFails, NetworkCancelled;
        public CancellationTokenSource? CancelConfiguration;
        public TaskCompletionSource ConfigurationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ConfigurationReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DiagnosticOptions? CaptureOptions;
        public PerformanceAuditReport Audit { get; } = new(DateTimeOffset.UtcNow, "Inventário fixture", [], []);
        public HardwareInventory? LatestInventory { get; } = new(DateTimeOffset.UtcNow, [], []);
        public Task<HardwareInventory> PrepareAsync(DiagnosticOptions options, IProgress<TestProgress> progress, CancellationToken ct)
        { Calls.Enqueue("prepare"); return Task.FromResult(LatestInventory!); }
        public async Task<TechnicalResult?> ReadConfigurationAsync(DiagnosticOptions options, IProgress<TestProgress> progress, CancellationToken ct)
        {
            Calls.Enqueue("configuration"); ConfigurationEntered.TrySetResult();
            if (ConfigurationFails) throw new IOException("Fixture: source unavailable");
            if (HoldConfiguration) await ConfigurationReady.Task.WaitAsync(ct);
            Calls.Enqueue("configuration-complete"); CancelConfiguration?.Cancel();
            return new("Inventário fixture", [], Audit: Audit, Cancelled: ct.IsCancellationRequested, Partial: ct.IsCancellationRequested);
        }
        public Task PrepareObservationAsync(CancellationToken ct)
        { Calls.Enqueue("reset"); ct.ThrowIfCancellationRequested(); return ResetFails ? Task.FromException(new IOException("Fixture reset failed")) : Task.CompletedTask; }
        public Task<TelemetryBatch> CollectAsync(TimeSpan interval, CancellationToken ct)
        {
            Calls.Enqueue("collect"); Collections++; var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new TelemetryBatch(now, [new("cpu.total.percent", "CPU", 12, "%", "fixture", "fixture", now, interval)], []));
        }
        public Task<TechnicalResult> ReadNetworkAsync(DiagnosticOptions options, bool final, CancellationToken ct)
        { Calls.Enqueue(final ? "network-end" : "network-start"); return Task.FromResult(new TechnicalResult("Network fixture", [], Cancelled: NetworkCancelled)); }
        public Task<TechnicalResult> PingAsync(DiagnosticOptions options, IProgress<TestProgress> progress, CancellationToken ct)
        { Calls.Enqueue("ping"); return Task.FromResult(new TechnicalResult("Ping fixture", [])); }
        public Task<TechnicalResult> CaptureLatencyAsync(CancellationToken ct) => throw new InvalidOperationException("Expected duration-aware capture.");
        public Task<TechnicalResult> CaptureLatencyAsync(DiagnosticOptions options, CancellationToken ct)
        { Calls.Enqueue("latency"); CaptureOptions = options; return Task.FromResult(new TechnicalResult("Latency fixture", [])); }
        public Task<TechnicalResult> CaptureFramesAsync(DiagnosticOptions options, CancellationToken ct)
        { Calls.Enqueue("frames"); return Task.FromResult(new TechnicalResult("Frames fixture", [])); }
    }

    private sealed class Provider(string id, Availability status) : IPerformanceAuditProvider
    {
        public IReadOnlyList<AuditRule> Rules { get; } = [new(id, "fixture", id, "fixture", "", "", "", "", AuditSupport.Documented, "", "", [])];
        public Task<IReadOnlyList<AuditObservation>> ReadAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<AuditObservation>>([new(id, id, id, "fixture", "1", status, "fixture", new Dictionary<string, string>())]);
    }
}
