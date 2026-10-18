using Microsoft.Data.Sqlite;
using PCAnalyzer.Core;
using PCAnalyzer.Reports;
using PCAnalyzer.Storage;

namespace PCAnalyzer.Tests;

public sealed class TestExecutionTests
{
    [Fact]
    public async Task CancellationAfterBackendReturnsCannotLeaveACompletedReport()
    {
        var history = new MemoryTestHistory();
        var service = new TestExecutionService(history);
        var report = new DiagnosticReport(DiagnosticCompletion.Completed, "Measured", [], [], [], [], []);
        var run = await service.ExecuteAsync(TestCatalog.Get("system"), (_, _) =>
        {
            service.Cancel();
            return Task.FromResult(new TechnicalResult("Already measured", ["evidence"], Report: report));
        });
        Assert.Equal(TestStatus.Cancelled, run.Status);
        Assert.True(run.Result!.Cancelled);
        Assert.Equal(DiagnosticCompletion.Cancelled, run.Result.Report!.Completion);
        Assert.Equal("evidence", Assert.Single(run.Result.Lines));
        Assert.Equal(DiagnosticCompletion.Cancelled, (await history.LoadTestAsync(run.Id))!.Result!.Report!.Completion);
    }

    [Fact]
    public async Task StateTransitionsPublishMeasuredProgressAndPersistFullResult()
    {
        var history = new MemoryTestHistory(); var service = new TestExecutionService(history);
        var observed = new List<TestSession>(); service.Changed += observed.Add;
        Assert.Null(service.Current); Assert.False(service.IsRunning);
        var result = new TechnicalResult("Resultado real da fixture", ["Limitação conhecida"],
            [new("cpu.total.percent", "CPU", 12, "%", "test-only", "fixture", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1))],
            [new("Série fixture", "ms", [new(0, null), new(1, 3)], "relativa", "test-only")]);
        var run = await service.ExecuteAsync(TestCatalog.Get("system"), (progress, _) =>
        { progress.Report(new("3 de 10", .3, 3)); return Task.FromResult(result); });
        Assert.Equal(new[] { TestStatus.Preparing, TestStatus.Running, TestStatus.Completed }, observed.Select(s => s.Status).Distinct());
        Assert.Contains(observed, s => s.Progress.Fraction == .3 && s.Progress.CompletedUnits == 3);
        Assert.Equal(result, run.Result); Assert.False(service.IsRunning);
        Assert.Equal(run, await history.LoadTestAsync(run.Id));
    }

    [Fact]
    public async Task ConflictingRunIsRejectedAndCancellationReleasesLifecycle()
    {
        var history = new MemoryTestHistory(); var service = new TestExecutionService(history);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = service.ExecuteAsync(TestCatalog.Get("cpu"), async (_, ct) =>
        { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return new("unreachable", []); });
        await started.Task;
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync(TestCatalog.Get("ram"), (_, _) => Task.FromResult(new TechnicalResult("wrong", []))));
        service.Cancel(); var run = await running;
        Assert.Equal(TestStatus.Cancelled, run.Status); Assert.Null(run.Result); Assert.False(service.IsRunning);
        Assert.Equal(TestStatus.Cancelled, (await history.LoadTestAsync(run.Id))!.Status);
        Assert.Equal(TestStatus.Completed, (await service.ExecuteAsync(TestCatalog.Get("ram"), (_, _) => Task.FromResult(new TechnicalResult("next", [])))).Status);
    }

    [Fact]
    public async Task PartialCancellationKeepsDataButDoesNotBecomeSuccess()
    {
        var service = new TestExecutionService(new MemoryTestHistory());
        var run = await service.ExecuteAsync(TestCatalog.Get("ping"), (_, _) => Task.FromResult(new TechnicalResult("Partial", ["two samples"], Cancelled: true)));
        Assert.Equal(TestStatus.Cancelled, run.Status); Assert.NotNull(run.Result);
    }

    [Theory]
    [InlineData(Availability.ToolMissing)] [InlineData(Availability.PermissionRequired)] [InlineData(Availability.ReadError)] [InlineData(Availability.Unsupported)]
    public async Task UnavailableBackendResultIsFailureInsteadOfGreenSuccess(Availability availability)
    {
        var service = new TestExecutionService(new MemoryTestHistory());
        var run = await service.ExecuteAsync(TestCatalog.Get("dpc"), (_, _) => Task.FromResult(new TechnicalResult("Sem captura", ["Fonte indisponível"], Availability: availability)));
        Assert.Equal(TestStatus.Failed, run.Status); Assert.Contains("Fonte indisponível", run.Error);
    }

    [Fact]
    public async Task FailureAndLateProgressCannotReplaceTerminalState()
    {
        var service = new TestExecutionService(new MemoryTestHistory()); IProgress<TestProgress>? callback = null;
        var run = await service.ExecuteAsync(TestCatalog.Get("cpu"), (p, _) =>
        { callback = p; throw new InvalidOperationException("fixture failure"); });
        callback!.Report(new("late", .8));
        Assert.Equal(TestStatus.Failed, run.Status); Assert.Contains("fixture failure", run.Error);
        Assert.DoesNotContain("late", service.Current!.Progress.Stage);
    }

    [Fact]
    public async Task UnknownAndInvalidProgressRemainsIndeterminate()
    {
        var service = new TestExecutionService(new MemoryTestHistory()); var fractions = new List<double?>();
        service.Changed += s => fractions.Add(s.Progress.Fraction);
        await service.ExecuteAsync(TestCatalog.Get("cpu"), (progress, _) =>
        { progress.Report(new("unknown")); progress.Report(new("nan", double.NaN)); progress.Report(new("overflow", 1.5)); return Task.FromResult(new TechnicalResult("ok", [])); });
        Assert.All(fractions, Assert.Null);
    }

    [Fact]
    public async Task PersistenceFailureDoesNotDiscardResultOrPretendItWasSaved()
    {
        var history = new MemoryTestHistory { FailTerminalSave = true };
        var run = await new TestExecutionService(history).ExecuteAsync(TestCatalog.Get("cpu"), (_, _) => Task.FromResult(new TechnicalResult("measured", [])));
        Assert.Equal(TestStatus.Completed, run.Status); Assert.NotNull(run.Result); Assert.NotNull(run.PersistenceError);
        Assert.Contains("somente", run.Progress.Stage);
    }

    [Fact]
    public async Task InitialPersistenceFailureDoesNotStartWork()
    {
        var called = false; var service = new TestExecutionService(new MemoryTestHistory { FailAllSaves = true });
        var run = await service.ExecuteAsync(TestCatalog.Get("cpu"), (_, _) => { called = true; return Task.FromResult(new TechnicalResult("bad", [])); });
        Assert.False(called); Assert.Equal(TestStatus.Failed, run.Status); Assert.NotNull(run.PersistenceError);
    }

    [Fact]
    public async Task InterruptedRunIsRecoveredWithoutInventingDuration()
    {
        var history = new MemoryTestHistory(); var prior = new TestSession(Guid.NewGuid(), "cpu", "CPU", TestStatus.Running,
            DateTimeOffset.UtcNow.AddMinutes(-1), null, new("running"));
        await history.SaveTestAsync(prior);
        await new TestExecutionService(history).RecoverInterruptedAsync();
        var recovered = await history.LoadTestAsync(prior.Id);
        Assert.Equal(TestStatus.Failed, recovered!.Status); Assert.Null(recovered.EndedAt); Assert.Contains("desconhecidos", recovered.Error);
    }

    [Fact]
    public async Task SqliteReopensTestWithSeriesFindingsAndUnavailableValues()
    {
        using var temp = new TemporaryDirectory(); var path = Path.Combine(temp.Path, "history.db");
        var store = new SQLiteHistoryStore(path);
        var result = new TechnicalResult("fixture", ["notes"], Series: [new("gaps", "ms", [new(0, null), new(2, 4)], "relative", "fixture")],
            Indicators: [new("unknown", "missing", null, "ms", "unsupported")],
            Findings: [new("id", "CPU", "finding", "explanation", FindingCategory.InsufficientData, Severity.Information, Confidence.Low,
                [new("evidence", "source", Timestamp: DateTimeOffset.UtcNow)], "recommend", "verify", "none")]);
        var run = await new TestExecutionService(store).ExecuteAsync(TestCatalog.Get("system"), (_, _) => Task.FromResult(result));
        var reopened = new SQLiteHistoryStore(path); var loaded = await reopened.LoadTestAsync(run.Id);
        Assert.Equal(run.Status, loaded!.Status); Assert.Equal(result.Series![0].Points, loaded.Result!.Series![0].Points);
        Assert.Null(loaded.Result.Indicators![0].Value); Assert.Equal(result.Findings![0].Evidence, loaded.Result.Findings![0].Evidence);
        Assert.Single(await reopened.ListTestsAsync()); Assert.Null(await reopened.LoadTestAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task SchemaOneUpgradePreservesSessionsAndChangeJournal()
    {
        using var temp = new TemporaryDirectory(); var path = Path.Combine(temp.Path, "history.db");
        var store = new SQLiteHistoryStore(path); await store.InitializeAsync();
        var now = DateTimeOffset.UtcNow; var change = new ChangeRecord(Guid.NewGuid(), "kind", "target", "old", "new", ChangeRisk.Low, ChangeState.Applied, now, now);
        await store.SaveChangeAsync(change);
        await using (var connection = new SqliteConnection("Data Source=" + path))
        { await connection.OpenAsync(); await using var command = connection.CreateCommand(); command.CommandText = "DROP TABLE test_runs; PRAGMA user_version=1;"; await command.ExecuteNonQueryAsync(); }
        var upgraded = new SQLiteHistoryStore(path); await upgraded.InitializeAsync();
        Assert.Equal(change, await upgraded.LoadChangeAsync(change.Id)); Assert.Empty(await upgraded.ListTestsAsync());
    }

    [Fact]
    public void TestExportRedactsTextSeriesAndContextWithoutChangingMeasurements()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new TechnicalResult("test", [@"C:\Users\Alice\trace.csv IP=192.168.3.42 <script>"],
            Series: [new("host=private-machine", "ms", [new(10, 123.5)], "relative", "token=secret-value")]);
        var run = new TestSession(Guid.NewGuid(), "cpu", "CPU", TestStatus.Completed, now, now, new("end"), result);
        var json = TestReportExporter.ExportJson(run); var html = TestReportExporter.ExportHtml(run);
        Assert.DoesNotContain("Alice", json); Assert.DoesNotContain("192.168.3.42", json); Assert.DoesNotContain("secret-value", json);
        Assert.Contains("123.5", json); Assert.DoesNotContain("<script>", html); Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Alice", TestReportExporter.ExportJson(run, false));
    }

    [Fact]
    public void CatalogExplainsProductionMetricAndUnavailableBenchmark()
    {
        foreach (var key in new[] { "cpu.total.percent", "cpu.logical.12.percent", "gpu.0.clock.limit.flags", "disk.1.read.latency.ms", "network.guid.receive.bytes_per_second", "frames.game.1.fps", "dpc.cpu.0.isr.duration.us" })
        { var info = MetricCatalog.Resolve(key); Assert.NotEqual(key, info.Name); Assert.NotEmpty(info.Description); Assert.NotEmpty(info.Interpretation); Assert.NotEmpty(info.Limitations); }
        Assert.False(TestCatalog.Get("gpu").Implemented);
        Assert.Equal(MetricCatalog.All.Count, MetricCatalog.All.Select(i => i.Id).Distinct().Count());
    }

    private sealed class MemoryTestHistory : ITestHistoryStore
    {
        private readonly Dictionary<Guid, TestSession> _runs = [];
        public bool FailTerminalSave { get; init; }
        public bool FailAllSaves { get; init; }
        public Task SaveTestAsync(TestSession run, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); if (FailAllSaves || FailTerminalSave && !run.IsActive) throw new IOException("fixture disk full"); _runs[run.Id] = run; return Task.CompletedTask; }
        public Task<TestSession?> LoadTestAsync(Guid id, CancellationToken ct = default) => Task.FromResult(_runs.GetValueOrDefault(id));
        public Task<IReadOnlyList<TestRunSummary>> ListTestsAsync(int limit = 100, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TestRunSummary>>(
            _runs.Values.Take(limit).Select(r => new TestRunSummary(r.Id, r.TestId, r.Name, r.Status, r.StartedAt, r.EndedAt, r.Result?.Title)).ToArray());
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PCAnalyzer-execution-tests-" + Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(Path, true); }
    }
}
