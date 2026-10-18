using System.Text.Json;
using PCAnalyzer.Core;
using PCAnalyzer.Reports;
using PCAnalyzer.Storage;

namespace PCAnalyzer.Tests;

public sealed class PerformanceAuditIntegrationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-17T08:00:00Z");
    private static readonly AuditRule Rule = new("test.config", "Windows", "Configuração de teste", "fixture", "Fixture",
        "Fixture", "Valor e origem", "Hipótese sem ganho demonstrado", AuditSupport.Conditional,
        "A presença não prova efeito", "Comparar a mesma carga após mudança externa", ["https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service"]);

    [Theory]
    [InlineData(true, DiagnosticCompletion.Cancelled)]
    [InlineData(false, DiagnosticCompletion.Partial)]
    public void InterruptedAuditPreservesMeasurementsAndKeepsAllCompletionStatesConsistent(bool cancelled, DiagnosticCompletion expected)
    {
        var sample = new MetricSample("cpu.total.percent", "CPU", 12, "%", "fixture", "fixture", Now, TimeSpan.FromSeconds(1));
        var report = new DiagnosticReport(DiagnosticCompletion.Completed, "Observação concluída", [], [], [], [], []);
        var diagnosis = new TechnicalResult("Análise", [], Metrics: [sample], Report: report);
        var combined = DiagnosticAuditIntegration.Merge(diagnosis, new("Auditoria", [], Partial: true, Cancelled: cancelled,
            Availability: Availability.InsufficientData, Limitations: ["Leitura interrompida"]));
        Assert.Equal(cancelled, combined.Cancelled);
        Assert.True(combined.Partial);
        Assert.Equal(expected, combined.Report!.Completion);
        Assert.Equal(sample, Assert.Single(combined.Metrics!));
        Assert.Contains("Leitura interrompida", combined.Report.Limitations);
    }

    [Fact]
    public async Task SlowNativeProviderIsNotQueuedAgainAndOtherEvidenceSurvives()
    {
        var pending = new TaskCompletionSource<IReadOnlyList<AuditObservation>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new Provider(Rule with { Id = "test.slow" }, _ => pending.Task);
        var good = new Provider(Rule, _ => Task.FromResult<IReadOnlyList<AuditObservation>>([Observation("38")]));
        var runner = new PerformanceAuditCoordinator([slow, good], TimeSpan.FromMilliseconds(100));
        try
        {
            var first = await runner.RunAsync(new InlineProgress<TestProgress>(_ => { }), CancellationToken.None);
            var second = await runner.RunAsync(new InlineProgress<TestProgress>(_ => { }), CancellationToken.None);
            Assert.Equal(1, slow.Reads);
            Assert.Equal(2, good.Reads);
            Assert.Contains(first.Assessments, a => a.Observation.RawValue == "38" && a.Observation.Status == Availability.Available);
            Assert.Contains(second.Assessments, a => a.Observation.RuleId == "test.slow" && a.Observation.Status == Availability.Collecting);
            Assert.DoesNotContain(first.Assessments, a => a.Classification == AuditClassification.DemonstratedProblem);
        }
        finally { pending.SetResult([]); }
    }

    [Fact]
    public async Task FaultedSourceAndEmptyReadCannotCountAsAvailableCoverage()
    {
        var provider = new Provider(Rule, _ => throw new UnauthorizedAccessException("private-error"));
        var report = await new PerformanceAuditCoordinator([provider]).RunAsync(new InlineProgress<TestProgress>(_ => { }), CancellationToken.None);
        Assert.Equal(Availability.PermissionRequired, Assert.Single(report.Assessments).Observation.Status);
        Assert.DoesNotContain("private-error", JsonSerializer.Serialize(report));
        var empty = new Provider(Rule, _ => Task.FromResult<IReadOnlyList<AuditObservation>>([]));
        var noValues = await new PerformanceAuditCoordinator([empty]).RunAsync(new InlineProgress<TestProgress>(_ => { }), CancellationToken.None);
        Assert.Equal(AuditClassification.InsufficientData, Assert.Single(noValues.Assessments).Classification);
    }

    [Fact]
    public async Task AuditRoundTripsThroughSqliteAndOldResultsRemainReadable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "audit-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SQLiteHistoryStore(Path.Combine(directory, "history.db"));
            var run = Run("38");
            await store.SaveTestAsync(run);
            var loaded = await store.LoadTestAsync(run.Id);
            Assert.NotNull(loaded?.Result?.Audit);
            Assert.Equal(TestReportExporter.ExportJson(run, false), TestReportExporter.ExportJson(loaded!, false));
            var legacy = JsonSerializer.Deserialize<TechnicalResult>("{\"Title\":\"old\",\"Lines\":[]}");
            Assert.Null(legacy!.Audit);
        }
        finally
        {
            // Pooled SQLite handles keep the fixture database open on Windows after Dispose.
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void ExportRedactsAuditOnlyIdentifiersPreservesNumbersAndEscapesHtml()
    {
        var run = Run("38");
        var row = run.Result!.Audit!.Assessments[0];
        var privateObservation = row.Observation with
        {
            Name = "<script>unsafe()</script>", InstanceId = "PRIVATE-PNP-DEVICE-123",
            RawValue = "SERIAL-PRIVATE-456", Scope = @"C:\Users\Alice\game.exe",
            Facts = new Dictionary<string, string> { ["SerialNumber"] = "SERIAL-PRIVATE-456", ["decimal"] = "38", ["InterfaceGuid"] = "80f584b2-4d18-4dda-866f-ebccf7723000" }
        };
        run = run with { Result = run.Result with { Audit = run.Result.Audit with { Assessments = [row with { Observation = privateObservation }] } } };
        foreach (var text in new[] { TestReportExporter.ExportJson(run), TestReportExporter.ExportHtml(run) })
            foreach (var identifier in new[] { "SERIAL-PRIVATE-456", "PRIVATE-PNP-DEVICE-123", "Alice", "80f584b2-4d18-4dda-866f-ebccf7723000" })
                Assert.DoesNotContain(identifier, text);
        var html = TestReportExporter.ExportHtml(run);
        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
        Assert.Contains("B. Otimizações do Windows", System.Net.WebUtility.HtmlDecode(html));
        Assert.Contains("K. Ajustes que não precisam ser alterados", System.Net.WebUtility.HtmlDecode(html));
        using var json = JsonDocument.Parse(TestReportExporter.ExportJson(run));
        Assert.Equal("38", json.RootElement.GetProperty("result").GetProperty("audit").GetProperty("assessments")[0]
            .GetProperty("observation").GetProperty("facts").GetProperty("decimal").GetString());
        Assert.Equal("SERIAL-PRIVATE-456", run.Result.Audit.Assessments[0].Observation.RawValue);
    }

    [Fact]
    public void ComparisonShowsChangedValuesButDoesNotTreatReadFailuresAsChanges()
    {
        var before = Run("38");
        var after = Run("42");
        var compared = TestResultComparer.Compare(before, after);
        Assert.Contains(compared.Lines, l => l.Contains("38 → 42", StringComparison.Ordinal));
        Assert.Contains(compared.Lines, l => l.Contains("não comprova", StringComparison.Ordinal));
        var row = after.Result!.Audit!.Assessments[0];
        var unavailable = after.Result.Audit with { Assessments = [row with { Observation = row.Observation with { Status = Availability.ReadError, RawValue = null } }] };
        var failed = AuditSnapshotComparer.Describe(before.Result!.Audit!, unavailable);
        Assert.DoesNotContain(failed, l => l.Contains("38 →", StringComparison.Ordinal));
        Assert.Contains(failed, l => l.Contains("1 itens sem leitura equivalente", StringComparison.Ordinal));
    }

    [Fact]
    public void ComparisonRejectsUnknownOrDifferentMachineIdentity()
    {
        var before = Run("38");
        var after = Run("42");
        after = after with { Result = after.Result! with { Session = after.Result!.Session! with { Inventory = new(Now, [], []) } } };
        var comparison = TestResultComparer.Compare(before, after);
        Assert.Equal(Availability.InsufficientData, comparison.Availability);
        Assert.DoesNotContain(comparison.Lines, l => l.Contains("38 → 42", StringComparison.Ordinal));
    }

    [Fact]
    public void SameGenericComponentIdDoesNotHidePhysicalHardwareReplacement()
    {
        var before = Run("38"); var after = Run("42");
        var inventory = after.Result!.Session!.Inventory;
        after = after with { Result = after.Result with { Session = after.Result.Session with
        { Inventory = inventory with { Components = [inventory.Components[0] with { Name = "Different CPU", Facts = [new("ProcessorId", "replacement", "fixture")] }] } } } };
        Assert.Equal(Availability.InsufficientData, TestResultComparer.Compare(before, after).Availability);
    }

    [Fact]
    public void SameRawValueDoesNotHideChangedDriverOrPnpState()
    {
        var before = Run("same service").Result!.Audit!;
        var first = before.Assessments[0];
        before = before with { Assessments = [first with { Observation = first.Observation with
        { RuleId = "devices.identity", Facts = new Dictionary<string, string> { ["DriverVersion"] = "1", ["pnpProblemCode"] = "0" } } }] };
        var after = before with { Assessments = [before.Assessments[0] with { Observation = before.Assessments[0].Observation with
        { Facts = new Dictionary<string, string> { ["DriverVersion"] = "2", ["pnpProblemCode"] = "10" } } }] };
        var lines = AuditSnapshotComparer.Describe(before, after);
        Assert.Contains(lines, l => l.Contains("DriverVersion: 1 → 2", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("pnpProblemCode: 0 → 10", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("2026-10-18T12:00:00.0000000Z")]
    public void ProcessPidWithoutMatchingCreationTimeCannotProducePriorityComparison(string? nextStart)
    {
        var before = Run("Normal").Result!.Audit!;
        var original = before.Assessments[0];
        before = before with { Assessments = [original with { Observation = original.Observation with
        { RuleId = "windows.process.scheduling", Facts = new Dictionary<string, string>
        { ["ExecutableName"] = "game.exe", ["ProcessStartedAtUtc"] = "2026-10-17T12:00:00.0000000Z" } } }] };
        var afterFacts = new Dictionary<string, string> { ["ExecutableName"] = "game.exe" };
        if (nextStart is not null) afterFacts["ProcessStartedAtUtc"] = nextStart;
        var after = before with { Assessments = [before.Assessments[0] with { Observation = before.Assessments[0].Observation with
        { RawValue = "High", Facts = afterFacts } }] };
        var lines = AuditSnapshotComparer.Describe(before, after);
        Assert.DoesNotContain(lines, l => l.Contains("Normal → High", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("identidade de processo não confirmada", StringComparison.Ordinal));
    }

    private static AuditObservation Observation(string raw) => new(Rule.Id, "device", "Configuração de teste", "Sistema", raw,
        Availability.Available, "fixture", new Dictionary<string, string> { ["presence"] = "present", ["decimal"] = raw });

    private static TestSession Run(string raw)
    {
        var audit = PerformanceAuditEngine.Evaluate([Rule], [Observation(raw)], Now);
        var session = new DiagnosticSession(Guid.NewGuid(), "Audit", Now, Now.AddSeconds(1), new("snapshot", "audit", 0),
            new(Now, [new("device", "CPU", "Fixture CPU", [])], []), [], []);
        return new(Guid.NewGuid(), "audit.configuration", "Auditoria", TestStatus.Completed, Now, Now.AddSeconds(1), new("Concluído"),
            new("Auditoria", [], Session: session, ComparisonKey: "audit:configuration:v1", Audit: audit));
    }

    private sealed class Provider(AuditRule rule, Func<CancellationToken, Task<IReadOnlyList<AuditObservation>>> read) : IPerformanceAuditProvider
    {
        public int Reads;
        public IReadOnlyList<AuditRule> Rules => [rule];
        public Task<IReadOnlyList<AuditObservation>> ReadAsync(CancellationToken ct) { Interlocked.Increment(ref Reads); return read(ct); }
    }
}
