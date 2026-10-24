using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;
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
                [new("evidence", "source", Timestamp: DateTimeOffset.UtcNow)], "recommend", "verify", "none")],
            Interpretation: "A janela não demonstrou um problema de latência.",
            NextStep: "Repita durante o sintoma e compare os frametimes.", InterpretationStatus: "Inconclusivo");
        var run = await new TestExecutionService(store).ExecuteAsync(TestCatalog.Get("system"), (_, _) => Task.FromResult(result));
        var reopened = new SQLiteHistoryStore(path); var loaded = await reopened.LoadTestAsync(run.Id);
        Assert.Equal(run.Status, loaded!.Status); Assert.Equal(result.Series![0].Points, loaded.Result!.Series![0].Points);
        Assert.Null(loaded.Result.Indicators![0].Value); Assert.Equal(result.Findings![0].Evidence, loaded.Result.Findings![0].Evidence);
        Assert.Equal(result.Interpretation, loaded.Result.Interpretation);
        Assert.Equal(result.NextStep, loaded.Result.NextStep);
        Assert.Equal(result.InterpretationStatus, loaded.Result.InterpretationStatus);
        Assert.Single(await reopened.ListTestsAsync()); Assert.Null(await reopened.LoadTestAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task LegacySqliteResultWithoutInterpretationRemainsReadableAndExportable()
    {
        using var temp = new TemporaryDirectory(); var path = Path.Combine(temp.Path, "history.db");
        var store = new SQLiteHistoryStore(path);
        var result = new TechnicalResult("Legacy measurement", ["Original evidence"],
            Indicators: [new("dpc.maximum.us", "DPC máximo", 87.25, "µs", "Legacy sample")]);
        var run = await new TestExecutionService(store).ExecuteAsync(TestCatalog.Get("dpc"), (_, _) => Task.FromResult(result));
        await using (var connection = new SqliteConnection("Data Source=" + path))
        {
            await connection.OpenAsync();
            await using var read = connection.CreateCommand();
            read.CommandText = "SELECT payload_json FROM test_runs WHERE id=$id;";
            read.Parameters.AddWithValue("$id", run.Id.ToString("D"));
            var payload = JsonNode.Parse((string)(await read.ExecuteScalarAsync())!)!;
            var legacyResult = payload["result"]!.AsObject();
            foreach (var field in new[] { "interpretation", "nextStep", "interpretationStatus" }) legacyResult.Remove(field);
            await using var update = connection.CreateCommand();
            update.CommandText = "UPDATE test_runs SET payload_json=$payload WHERE id=$id;";
            update.Parameters.AddWithValue("$id", run.Id.ToString("D"));
            update.Parameters.AddWithValue("$payload", payload.ToJsonString());
            await update.ExecuteNonQueryAsync();
        }
        var loaded = await new SQLiteHistoryStore(path).LoadTestAsync(run.Id);
        Assert.NotNull(loaded?.Result);
        Assert.Null(loaded.Result.Interpretation); Assert.Null(loaded.Result.NextStep); Assert.Null(loaded.Result.InterpretationStatus);
        Assert.Equal(87.25, Assert.Single(loaded.Result.Indicators!).Value);
        Assert.Equal("Original evidence", Assert.Single(loaded.Result.Lines));
        Assert.Contains("Legacy measurement", TestReportExporter.ExportJson(loaded, false));
        Assert.DoesNotContain("<h2>Interpretação da medição</h2>", TestReportExporter.ExportHtml(loaded, false));
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
            Series: [new("host=private-machine", "ms", [new(10, 123.5)], "relative", "token=secret-value")],
            SelectionContext: "Destino solicitado: private.internal.example");
        var run = new TestSession(Guid.NewGuid(), "cpu", "CPU", TestStatus.Completed, now, now, new("end"), result);
        var json = TestReportExporter.ExportJson(run); var html = TestReportExporter.ExportHtml(run);
        Assert.DoesNotContain("Alice", json); Assert.DoesNotContain("192.168.3.42", json); Assert.DoesNotContain("secret-value", json);
        Assert.Contains("123.5", json); Assert.DoesNotContain("<script>", html); Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("Alice", TestReportExporter.ExportJson(run, false));
        Assert.DoesNotContain("private.internal.example", json);
        Assert.Contains("private.internal.example", TestReportExporter.ExportJson(run, false));
    }

    [Fact]
    public void InterpretationExportRedactsEveryFieldEscapesHtmlAndKeepsOriginalEvidence()
    {
        var now = DateTimeOffset.UtcNow;
        var result = new TechnicalResult("Latency", [],
            Indicators: [new("dpc.maximum.us", "DPC máximo", 123.5, "µs", "Observed")],
            Interpretation: "<script>alert(1)</script> IP=192.168.3.42; host=private-machine",
            NextStep: "Compare <b>frametimes</b>; token=secret-value; arquivo C:\\Users\\Alice\\trace.etl",
            InterpretationStatus: "<img src=x> user=private-user");
        var run = new TestSession(Guid.NewGuid(), "dpc", "DPC", TestStatus.Completed, now, now, new("end"), result);
        var json = TestReportExporter.ExportJson(run);
        var html = TestReportExporter.ExportHtml(run);
        foreach (var text in new[] { json, html })
        {
            foreach (var privateValue in new[] { "192.168.3.42", "private-machine", "secret-value", "Alice", "private-user" })
                Assert.DoesNotContain(privateValue, text);
        }
        // Assert the visible consultation, rather than only its JSON copy in technical details.
        var consultation = html[..html.IndexOf("<h2>Indicadores</h2>", StringComparison.Ordinal)];
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", consultation);
        Assert.Contains("&lt;b&gt;frametimes&lt;/b&gt;", consultation);
        Assert.Contains("<h2>&lt;img src=x&gt;", consultation);
        Assert.Contains("Próximo passo:", consultation);
        Assert.DoesNotContain("<script>", html); Assert.DoesNotContain("<img src=x>", html);
        var exported = JsonNode.Parse(json)!["result"]!;
        Assert.Equal(123.5, exported["indicators"]![0]!["value"]!.GetValue<double>());
        Assert.NotEmpty(exported["interpretation"]!.GetValue<string>());
        Assert.NotEmpty(exported["nextStep"]!.GetValue<string>());
        Assert.NotEmpty(exported["interpretationStatus"]!.GetValue<string>());
        Assert.Equal(result, run.Result);
        Assert.Contains("Alice", TestReportExporter.ExportJson(run, false));
        Assert.Contains("192.168.3.42", TestReportExporter.ExportHtml(run, false));
        Assert.Contains("&lt;script&gt;", TestReportExporter.ExportHtml(run, false));
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
