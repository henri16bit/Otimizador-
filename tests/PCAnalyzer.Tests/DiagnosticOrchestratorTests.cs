using PCAnalyzer.Core;

namespace PCAnalyzer.Tests;

public sealed class DiagnosticOrchestratorTests
{
    [Fact]
    public async Task DefaultObservationNeverStartsOptionalCapturesAndKeepsProcessEvidence()
    {
        var probe = new Probe();
        var published = 0;
        var result = await Run(probe, new(ObservationSeconds: 1), _ => published++);
        Assert.Equal(0, probe.Pings + probe.Latency + probe.Frames);
        Assert.Equal(2, probe.NetworkReads);
        Assert.Contains("network:fixture-adapter", result.ComparisonKey);
        Assert.True(published > 0);
        Assert.NotNull(result.Session);
        Assert.NotEmpty(result.Session.Processes!);
        Assert.Contains(result.Session.Samples, s => s.Key == "cpu.total.percent" && s.Value == 24);
        Assert.Equal(DiagnosticCompletion.Partial, result.Report!.Completion);
        Assert.Equal(DiagnosticAssessmentState.Inconclusive, Assert.Single(result.Report.Components, c => c.Id == "gpu").State);
    }

    [Fact]
    public async Task PreparationAndLatencyFailuresPreserveIndependentCollection()
    {
        var probe = new Probe { PreparationFails = true, LatencyFails = true };
        var result = await Run(probe, new(ObservationSeconds: 1, IncludeLatency: true, IncludePing: true));
        Assert.Equal(1, probe.Pings);
        Assert.Equal(1, probe.Latency);
        Assert.Equal(0, probe.Frames);
        Assert.NotEmpty(result.Session!.Samples);
        Assert.Equal(Availability.ReadError, Assert.Single(result.Report!.Stages, s => s.Id == "preparation").Status);
        Assert.Equal(Availability.PermissionRequired, Assert.Single(result.Report.Stages, s => s.Id == "latency").Status);
        Assert.True(result.Partial);
    }

    [Fact]
    public async Task CancellationPreservesAlreadyObservedMeasurementsAndCanBePersisted()
    {
        using var cancellation = new CancellationTokenSource();
        var probe = new Probe();
        var history = new MemoryHistory();
        var service = new TestExecutionService(history);
        var saved = await service.ExecuteAsync(TestCatalog.Get("system"), (progress, ct) =>
            new DiagnosticOrchestrator(probe).RunAsync(new(ObservationSeconds: 2), progress,
                _ => cancellation.Cancel(), ct), cancellation.Token);
        Assert.Equal(TestStatus.Cancelled, saved.Status);
        Assert.Equal(DiagnosticCompletion.Cancelled, saved.Result!.Report!.Completion);
        Assert.NotEmpty(saved.Result.Session!.Samples);
        Assert.Equal(saved, await history.LoadTestAsync(saved.Id));
    }

    [Fact]
    public async Task PartialReportKeepsItsStatusThroughExecutionPersistence()
    {
        var history = new MemoryHistory();
        var saved = await new TestExecutionService(history).ExecuteAsync(TestCatalog.Get("system"),
            (progress, ct) => new DiagnosticOrchestrator(new Probe()).RunAsync(new(ObservationSeconds: 1), progress, null, ct));
        Assert.Equal(TestStatus.Partial, saved.Status);
        Assert.Equal(saved.Result!.Report, (await history.LoadTestAsync(saved.Id))!.Result!.Report);
        Assert.Contains("parcial", saved.Progress.Stage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StalledCollectionDoesNotQueueMoreNativeReads()
    {
        var pending = new TaskCompletionSource<TelemetryBatch>(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new Probe { PendingCollection = pending.Task };
        var result = await Run(probe, new(ObservationSeconds: 1));
        Assert.Equal(1, probe.Collections);
        Assert.Contains(result.Report!.Stages, s => s.Id == "observation" && s.Detail.Contains("cinco segundos"));
        Assert.Equal(DiagnosticCompletion.Failed, result.Report.Completion);
        pending.SetException(new IOException("Late native failure"));
    }

    private static Task<TechnicalResult> Run(Probe probe, DiagnosticOptions options, Action<TelemetryBatch>? publish = null) =>
        new DiagnosticOrchestrator(probe).RunAsync(options, new InlineProgress<TestProgress>(_ => { }), publish, CancellationToken.None);

    private sealed class Probe : IDiagnosticProbe
    {
        public bool PreparationFails, LatencyFails;
        public int Pings, Latency, Frames, Collections, NetworkReads;
        public Task<TelemetryBatch>? PendingCollection;
        public HardwareInventory? LatestInventory { get; } = new(DateTimeOffset.UtcNow, [], []);
        public Task<HardwareInventory> PrepareAsync(DiagnosticOptions options, IProgress<TestProgress> progress, CancellationToken ct) =>
            PreparationFails ? Task.FromException<HardwareInventory>(new IOException("WMI unavailable")) : Task.FromResult(LatestInventory!);
        public Task<TelemetryBatch> CollectAsync(TimeSpan interval, CancellationToken ct)
        {
            Collections++;
            var now = DateTimeOffset.UtcNow;
            return PendingCollection ?? Task.FromResult(new TelemetryBatch(now,
                [new("cpu.total.percent", "CPU", 24, "%", "fixture", "fixture", now, interval)],
                [new(123, "fixture process", 1, 1024, now)]));
        }
        public Task<TechnicalResult> ReadNetworkAsync(DiagnosticOptions options, bool final, CancellationToken ct)
        { NetworkReads++; return Task.FromResult(new TechnicalResult("Network fixture", ["Fixture only"], Availability: Availability.InsufficientData, ComparisonKey: "network:fixture-adapter")); }
        public Task<TechnicalResult> PingAsync(DiagnosticOptions options, IProgress<TestProgress> progress, CancellationToken ct)
        { Pings++; return Task.FromResult(new TechnicalResult("Ping fixture", ["Fixture only"])); }
        public Task<TechnicalResult> CaptureLatencyAsync(CancellationToken ct)
        { Latency++; return LatencyFails ? Task.FromException<TechnicalResult>(new UnauthorizedAccessException("UAC denied")) : Task.FromResult(new TechnicalResult("ETW fixture", [])); }
        public Task<TechnicalResult> CaptureFramesAsync(DiagnosticOptions options, CancellationToken ct)
        { Frames++; return Task.FromResult(new TechnicalResult("Frames fixture", [])); }
    }
    private sealed class MemoryHistory : ITestHistoryStore
    {
        private readonly Dictionary<Guid, TestSession> _sessions = [];
        public Task SaveTestAsync(TestSession session, CancellationToken ct = default) { _sessions[session.Id] = session; return Task.CompletedTask; }
        public Task<TestSession?> LoadTestAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_sessions.GetValueOrDefault(id));
        public Task<IReadOnlyList<TestRunSummary>> ListTestsAsync(int limit = 100, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TestRunSummary>>([]);
    }
}
