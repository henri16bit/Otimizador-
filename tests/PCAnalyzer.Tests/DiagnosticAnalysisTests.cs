using PCAnalyzer.Core;

namespace PCAnalyzer.Tests;

public sealed class DiagnosticAnalysisTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-10-15T12:00:00Z");
    private static readonly HardwareInventory Inventory = new(Start,
        [new("cpu", "CPU", "CPU fixture", []), new("memory", "RAM", "RAM fixture", []),
         new("gpu.0", "GPU (NVML)", "GPU fixture", []), new("disk.0", "Armazenamento", "SSD fixture", [])], []);

    [Fact]
    public void InventoryWithoutMeasurementsFailsInsteadOfReportingNormalComponents()
    {
        var report = Analyze([]);
        Assert.Equal(DiagnosticCompletion.Failed, report.Completion);
        Assert.All(report.Components, c => Assert.Equal(DiagnosticAssessmentState.Inconclusive, c.State));
        Assert.Contains(report.Components, c => c.Id == "gpu.0" && c.NextAction.Target == "jogos");
        Assert.NotEmpty(report.Recommendations);
    }

    [Theory]
    [InlineData(9, 1)] [InlineData(10, .5)]
    public void InsufficientDistinctWindowCannotSupportCpuAttribution(int count, double interval)
    {
        var samples = Window("cpu.total.percent", 99, "%", count, interval);
        var report = Analyze(samples);
        Assert.Equal(DiagnosticAssessmentState.Inconclusive, Component(report, "cpu").State);
        Assert.DoesNotContain(report.Findings, f => f.Id.StartsWith("cpu-load", StringComparison.Ordinal));
    }

    [Fact]
    public void DuplicatedObservationDoesNotIncreaseCoverage()
    {
        var samples = Window("cpu.total.percent", 99, "%", 2, 9);
        var report = Analyze(Enumerable.Repeat(samples, 20).SelectMany(s => s).ToArray());
        Assert.Equal(DiagnosticAssessmentState.Inconclusive, Component(report, "cpu").State);
    }

    [Fact]
    public void ValidCpuWindowDoesNotValidateMissingGpuOrOtherComponents()
    {
        var report = Analyze(Window("cpu.total.percent", 20, "%"));
        Assert.Equal(DiagnosticCompletion.Partial, report.Completion);
        Assert.Equal(DiagnosticAssessmentState.Normal, Component(report, "cpu").State);
        Assert.Contains("nesta janela", Component(report, "cpu").Summary);
        Assert.Equal(DiagnosticAssessmentState.Inconclusive, Component(report, "gpu.0").State);
        Assert.Equal(DiagnosticAssessmentState.Inconclusive, Component(report, "ram").State);
        Assert.DoesNotContain(report.Findings, f => f.Id == "no-observed-anomaly");
    }

    [Theory]
    [InlineData("cpu.total.percent", "cpu")]
    [InlineData("memory.available.bytes", "ram")]
    [InlineData("gpu.telemetry", "gpu.0")]
    public void MissingComponentKeepsTheActualSourceFailure(string key, string component)
    {
        var unavailable = MetricSample.Missing(key, component, "", "fixture source", Start,
            Availability.ReadError, "Provider failed; check the collector");
        var report = Analyze([unavailable]);
        Assert.Equal(DiagnosticAssessmentState.Inconclusive, Component(report, component).State);
        Assert.Contains("fixture source: Provider failed; check the collector", Component(report, component).Limitations);
        Assert.Contains("fixture source: Provider failed; check the collector", report.Limitations);
    }

    [Fact]
    public void SustainedCpuLoadIsAttentionWithoutClaimingHardwareFailure()
    {
        var report = Analyze(Window("cpu.total.percent", 97, "%"));
        var cpu = Component(report, "cpu");
        Assert.Equal(DiagnosticAssessmentState.Attention, cpu.State);
        Assert.Contains("não prova perda de FPS", cpu.Summary);
        Assert.All(cpu.Evidence, e => Assert.NotNull(e.Timestamp));
        Assert.Contains(report.Findings, f => f.Id == "cpu-load:cpu.total.percent");
        Assert.Equal("processos", cpu.NextAction.Target);
    }

    [Fact]
    public void OutOfRangePercentAndNonFiniteDataDoNotInventCpuPressure()
    {
        var samples = Window("cpu.total.percent", 101, "%").Concat(Window("cpu.total.percent", double.NaN, "%"))
            .Concat(Window("cpu.total.percent", 20, "%").Select(s => s with { Timestamp = s.Timestamp.AddSeconds(20) })).ToArray();
        var report = Analyze(samples);
        Assert.Equal(DiagnosticAssessmentState.Normal, Component(report, "cpu").State);
        Assert.DoesNotContain(report.Findings, f => f.Id.StartsWith("cpu-load", StringComparison.Ordinal));
    }

    [Fact]
    public void UnsynchronizedCommitDoesNotValidateRam()
    {
        var samples = Memory().Select(s => s.Key == "memory.commit.bytes" ? s with { Timestamp = s.Timestamp.AddMinutes(1) } : s).ToArray();
        Assert.Equal(DiagnosticAssessmentState.Inconclusive, Component(Analyze(samples), "ram").State);
    }

    [Fact]
    public void ObservedMemoryPressureRetainsMeasurementsWithoutInferringBadDimms()
    {
        var report = Analyze(Memory(100 * 1024 * 1024, 950, 1000));
        var ram = Component(report, "ram");
        Assert.Equal(DiagnosticAssessmentState.Problem, ram.State);
        Assert.Equal(Confidence.High, ram.Confidence);
        Assert.Contains(ram.Limitations, l => l.Contains("defeitos físicos", StringComparison.Ordinal));
        Assert.Contains(report.Findings, f => f.Id == "memory-pressure");
    }

    [Fact]
    public void OneGpuDoesNotValidateAnotherOrCopyItsWarning()
    {
        var inventory = Inventory with { Components = Inventory.Components.Append(new("gpu.1", "GPU (NVML)", "Second GPU", [])).ToArray() };
        var finding = Finding("gpu-load", "GPU", FindingCategory.PossibleProblem, "gpu.0.utilization.percent");
        var report = Analyze(Window("gpu.0.utilization.percent", 95, "%"), [finding], inventory);
        Assert.Equal(DiagnosticAssessmentState.Attention, Component(report, "gpu.0").State);
        Assert.Equal(DiagnosticAssessmentState.Inconclusive, Component(report, "gpu.1").State);
    }

    [Fact]
    public void IdleDiskDoesNotBecomeZeroLatencyOrHealthyStorage()
    {
        var report = Analyze(Window("disk.0.read.operations_per_second", 0, "I/O/s"));
        var disk = Component(report, "disk.0");
        Assert.Equal(DiagnosticAssessmentState.Inconclusive, disk.State);
        Assert.Contains("Sem operações", disk.Summary);
        Assert.DoesNotContain(disk.Evidence, e => e.Unit == "ms" && e.Value == 0);
    }

    [Fact]
    public void UnmappedPhysicalDiskIdentifiersAreNotAssumedToMatchCounters()
    {
        var inventory = Inventory with { Components = [new("storage.0", "MSFT_PhysicalDisk", "Storage fixture", [])] };
        var report = Analyze(Window("disk.0.read.operations_per_second", 100, "I/O/s"), inventory: inventory);
        Assert.Equal(DiagnosticAssessmentState.Inconclusive, Component(report, "storage").State);
    }

    [Fact]
    public void NetworkInventoryAndLinkDoNotCertifyConnectionQuality()
    {
        var coverage = Finding("network.fixture.errors", "Rede", FindingCategory.InsufficientData, null) with
        { Explanation = "Falta segunda leitura dos contadores", Recommendation = "Repita a leitura" };
        var report = Analyze(Window("cpu.total.percent", 20, "%"),
            [Finding("network.fixture.link", "Rede", FindingCategory.NoAnomaly, "network.fixture.link.bits_per_second"), coverage]);
        Assert.Equal(DiagnosticAssessmentState.Inconclusive, Component(report, "network").State);
        Assert.Contains("não foi estabelecida", Component(report, "network").Summary);
        Assert.Contains(coverage, report.Findings);
        Assert.Contains(coverage.Explanation, Component(report, "network").Limitations);
    }

    [Fact]
    public void NetworkErrorEvidenceCanIdentifyObservedLocalProblem()
    {
        var finding = Finding("network.fixture.errors", "Rede", FindingCategory.ObservedProblem, "network.fixture.receive.errors.total");
        var report = Analyze(Window("cpu.total.percent", 20, "%"), [finding]);
        Assert.Equal(DiagnosticAssessmentState.Problem, Component(report, "network").State);
        Assert.Contains(finding, report.Findings);
        Assert.Contains(Component(report, "network").Limitations, l => l.Contains("não comprovam", StringComparison.Ordinal));
    }

    [Fact]
    public void GamingAndLatencyEvidenceRemainScopedAndPartialCaptureLimitationsSurvive()
    {
        var dpc = Finding("dpc-recurring:fixture", "DPC/ISR", FindingCategory.PossibleProblem, "dpc.cpu.0.dpc.duration.us");
        var frames = Finding("frame-variation:fixture", "Jogos", FindingCategory.PossibleProblem, "frames.frametime.ms");
        var partial = Finding("dpc-partial", "DPC/ISR", FindingCategory.InsufficientData, null) with { Explanation = "Eventos perdidos: 3" };
        var report = Analyze(Window("cpu.total.percent", 20, "%"), [dpc, frames, partial]);
        Assert.Equal(DiagnosticAssessmentState.Attention, Component(report, "drivers").State);
        Assert.Equal(DiagnosticAssessmentState.Attention, Component(report, "gaming").State);
        Assert.Contains(partial.Explanation, Component(report, "drivers").Limitations);
        Assert.Equal(DiagnosticAssessmentState.Inconclusive, Component(report, "stability").State);
    }

    [Fact]
    public void RelativeCaptureEvidenceRemainsUsefulWhenUtcTelemetryIsMissing()
    {
        var dpc = Finding("dpc-recurring:fixture", "DPC/ISR", FindingCategory.PossibleProblem, null) with
        { Evidence = [new("Repeated delays at 200 ms and 1500 ms from the capture start", "ETW fixture", Value: 1000, Unit: "µs")] };
        var report = Analyze([], [dpc]);
        Assert.Equal(DiagnosticCompletion.Partial, report.Completion);
        Assert.Equal(DiagnosticAssessmentState.Attention, Component(report, "drivers").State);
        Assert.Equal(DiagnosticAssessmentState.Inconclusive, Component(report, "cpu").State);
        Assert.Contains(dpc, report.Findings);
        Assert.DoesNotContain("Não foi possível obter", report.Summary);
    }

    [Fact]
    public void FindingsWithoutEvidenceDoNotTurnMissingMeasurementsIntoSuccess()
    {
        var dpc = Finding("dpc-recurring:fixture", "DPC/ISR", FindingCategory.PossibleProblem, null) with { Evidence = [] };
        var report = Analyze([], [dpc]);
        Assert.Equal(DiagnosticCompletion.Failed, report.Completion);
        Assert.Equal(DiagnosticAssessmentState.Inconclusive, Component(report, "drivers").State);
        Assert.DoesNotContain(dpc, report.Findings);
    }

    [Fact]
    public void CancellationPreservesCoverageAndNeverReportsCompleted()
    {
        var report = new DiagnosticAnalysisEngine().Analyze(Inventory, Window("cpu.total.percent", 20, "%"), [], [], [], true);
        Assert.Equal(DiagnosticCompletion.Cancelled, report.Completion);
        Assert.NotEmpty(Component(report, "cpu").Evidence);
        Assert.Equal(DiagnosticAssessmentState.Inconclusive, Component(report, "gpu.0").State);
    }

    [Fact]
    public void UnavailableStageIsRecordedAsLimitation()
    {
        var stage = new DiagnosticStage("latency", "DPC/ISR", Availability.PermissionRequired, "Permissão negada", Start, Start.AddSeconds(1));
        var report = new DiagnosticAnalysisEngine().Analyze(Inventory, Window("cpu.total.percent", 20, "%"), [], [], [stage]);
        Assert.Equal(DiagnosticCompletion.Partial, report.Completion);
        Assert.Contains("DPC/ISR: Permissão negada", report.Limitations);
        Assert.Equal(stage, Assert.Single(report.Stages));
    }

    private static DiagnosticReport Analyze(IReadOnlyList<MetricSample> samples, IReadOnlyList<Finding>? findings = null, HardwareInventory? inventory = null) =>
        new DiagnosticAnalysisEngine().Analyze(inventory ?? Inventory, samples, [], findings ?? [], []);
    private static ComponentAssessment Component(DiagnosticReport report, string id) => Assert.Single(report.Components, c => c.Id == id);
    private static MetricSample[] Window(string key, double value, string unit, int count = 10, double interval = 1) =>
        Enumerable.Range(0, count).Select(i => new MetricSample(key, "fixture", value, unit, "fixture source", "fixture method",
            Start.AddSeconds(i * interval), TimeSpan.FromSeconds(interval))).ToArray();
    private static MetricSample[] Memory(double available = 2L * 1024 * 1024 * 1024, double commit = 500, double limit = 1000) =>
        Window("memory.available.bytes", available, "bytes").Concat(Window("memory.commit.bytes", commit, "bytes"))
            .Concat(Window("memory.commit.limit.bytes", limit, "bytes")).ToArray();
    private static Finding Finding(string id, string component, FindingCategory category, string? metric) => new(id, component,
        "Fixture finding", "Fixture observed scope", category, Severity.Warning, Confidence.Medium,
        [new("Fixture evidence", "Fixture source", metric, 5, "fixture unit", Start)], "Review fixture", "Repeat fixture", "No change");
}
