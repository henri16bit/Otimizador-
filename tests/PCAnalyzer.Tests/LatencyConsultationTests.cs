using PCAnalyzer.Core;
using PCAnalyzer.Diagnostics;

namespace PCAnalyzer.Tests;

public sealed class LatencyConsultationTests
{
    [Fact]
    public void CompleteDistributedCaptureProducesOnlyAScopedConclusion()
    {
        var capture = CompleteCapture();
        var result = LatencyInterpretation.Consult(capture, "VALORANT · sessão representativa");
        Assert.Equal(LatencyConsultationClassification.NoneDemonstrated, result.Classification);
        Assert.False(result.Partial);
        Assert.Contains("não comprova", result.Summary);
        Assert.Contains("input lag físico", result.Summary);
        Assert.Contains("frametimes", result.NextStep);
        Assert.Contains(result.Details, d => d.Contains("primeiro=") && d.Contains("último="));
        Assert.Contains(result.Details, d => d.Contains("CPU ETW 0"));
        Assert.Contains(result.Details, d => d.Contains("100 eventos válidos"));
        Assert.Equal(Confidence.Medium, result.Confidence);
        Assert.DoesNotContain(result.Findings, f => f.Category == FindingCategory.ObservedProblem);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(15)]
    [InlineData(29)]
    public void ShortCaptureNeverCertifiesLowInputLag(int seconds)
    {
        var capture = CompleteCapture(seconds);
        var result = LatencyInterpretation.Consult(capture, "Jogo em carga");
        Assert.Equal(LatencyConsultationClassification.Inconclusive, result.Classification);
        Assert.Contains("observação é curta", result.Summary);
        Assert.Contains("60 ou 120", result.NextStep);
        Assert.DoesNotContain(result.Findings, f => f.Category == FindingCategory.NoAnomaly);
    }

    [Fact]
    public void LowMaximumWithoutContextOrTemporalSpreadIsNotEnough()
    {
        var capture = CompleteCapture();
        Assert.Equal(LatencyConsultationClassification.Inconclusive, LatencyInterpretation.Consult(capture).Classification);
        var concentrated = capture with { Timeline = capture.Timeline!.Select(e => e with { TraceRelativeMilliseconds = 100 }).ToArray() };
        var result = LatencyInterpretation.Consult(concentrated, "Sessão de jogo");
        Assert.Equal(LatencyConsultationClassification.Inconclusive, result.Classification);
        Assert.Contains("distribuição temporal", result.Summary);
    }

    [Theory]
    [InlineData("loss")]
    [InlineData("truncated")]
    [InlineData("cancelled")]
    [InlineData("limit")]
    [InlineData("status")]
    [InlineData("missing-clock")]
    public void IncompleteCaptureCannotBecomeAnAdequateWindow(string failure)
    {
        var capture = CompleteCapture();
        capture = failure switch
        {
            "loss" => capture with { EventsLost = 3 },
            "truncated" => capture with { TimelineTruncated = true },
            "cancelled" => capture with { Cancelled = true },
            "limit" => capture with { LimitReached = true },
            "status" => capture with { Status = Availability.ReadError },
            _ => capture with { Timeline = capture.Timeline!.Select(e => e with { TraceRelativeMilliseconds = null }).ToArray() }
        };
        var result = LatencyInterpretation.Consult(capture, "Jogo em carga");
        Assert.Equal(LatencyConsultationClassification.Inconclusive, result.Classification);
        Assert.True(result.Partial);
        Assert.Contains(result.Findings, f => f.Id == "dpc-partial");
        Assert.DoesNotContain(result.Findings, f => f.Category == FindingCategory.NoAnomaly);
    }

    [Fact]
    public void EmptyCaptureAndMalformedCountsDoNotBecomeMeasuredZeros()
    {
        var capture = CompleteCapture() with { CpuSummaries = [], ModuleSummaries = [], Timeline = [], EventCounts = new(0, 0, 0, 0, 0, 0, 0, 0), EventsSeen = 0 };
        var result = LatencyInterpretation.Consult(capture, "Sessão vazia");
        Assert.Equal(LatencyConsultationClassification.InvalidOrInsufficient, result.Classification);
        Assert.Contains("Ausência de eventos", result.Summary);
        Assert.All(LatencyResultPresentation.Summarize(capture), s => Assert.Null(s.MaximumMicroseconds));
        var bad = CompleteCapture() with { EventCounts = new(200, -1, 100, 0, 0, 200, 0, 200) };
        Assert.Equal(LatencyConsultationClassification.InvalidOrInsufficient, LatencyInterpretation.Consult(bad, "Sessão").Classification);
    }

    [Fact]
    public void RecurrenceIsAnInvestigationLeadAndNeverAUniversalFault()
    {
        var capture = CompleteCapture(recurrent: true);
        var result = LatencyInterpretation.Consult(capture, "Travadas durante o jogo");
        Assert.Equal(LatencyConsultationClassification.Investigate, result.Classification);
        Assert.Contains("não um limite universal", result.Summary);
        Assert.Contains(result.Findings, f => f.Id.StartsWith("dpc-recurring:") && f.Evidence.Count == 3);
        Assert.DoesNotContain(result.Findings, f => f.Category == FindingCategory.ObservedProblem);
        var partial = LatencyInterpretation.Consult(capture with { EventsLost = 1 }, "Travadas");
        Assert.Equal(LatencyConsultationClassification.Inconclusive, partial.Classification);
        Assert.Contains(partial.Findings, f => f.Id.StartsWith("dpc-recurring:"));
    }

    [Fact]
    public void AHiddenLargeRetainedEventInvalidatesASmallAggregateMaximum()
    {
        var capture = CompleteCapture();
        capture = capture with { Timeline = capture.Timeline!.Select((e, i) => i == 0 ? e with { DurationMicroseconds = 15_000 } : e).ToArray() };
        var result = LatencyInterpretation.Consult(capture, "Carga conhecida");
        Assert.Equal(LatencyConsultationClassification.Inconclusive, result.Classification);
        Assert.True(result.Partial);
        Assert.Contains(result.Details, d => d.Contains("excede o máximo dos agregados"));
    }

    [Fact]
    public void OverflowingOrImpossibleAggregatesStayUnavailable()
    {
        var capture = CompleteCapture() with { CpuSummaries = [
            new(0, "DPC", null, long.MaxValue, 10, 0, 10),
            new(1, "DPC", null, long.MaxValue, 10, 0, 10),
            new(0, "ISR", null, 1, 10, 10, 20)] };
        var summaries = LatencyResultPresentation.Summarize(capture);
        Assert.Null(summaries.Single(s => s.Kind == "DPC").AnalyzedCount);
        Assert.Null(summaries.Single(s => s.Kind == "ISR").MaximumMicroseconds);
        Assert.Equal(LatencyConsultationClassification.Inconclusive, LatencyInterpretation.Consult(capture, "Carga conhecida").Classification);
    }

    [Fact]
    public void AnAggregateMeanAboveItsMaximumIsNotReportedAsAValidMeasurement()
    {
        var capture = CompleteCapture();
        capture = capture with { CpuSummaries = capture.CpuSummaries.Select(s => s.Kind == "DPC"
            ? s with { TotalMicroseconds = s.Count * (s.MaximumMicroseconds + 1) } : s).ToArray() };
        Assert.Null(LatencyResultPresentation.Summarize(capture).Single(s => s.Kind == "DPC").MeanMicroseconds);
        var result = LatencyInterpretation.Consult(capture, "Carga conhecida");
        Assert.Equal(LatencyConsultationClassification.Inconclusive, result.Classification);
        Assert.True(result.Partial);
    }

    [Theory]
    [InlineData("kind-count")]
    [InlineData("cpu-count")]
    [InlineData("duration-total")]
    public void ContradictoryDetailedDistributionsCannotProduceANoAnomalyConclusion(string contradiction)
    {
        var capture = CompleteCapture();
        capture = contradiction switch
        {
            "kind-count" => capture with { EventCounts = capture.EventCounts! with { DpcEvents = 99, IsrEvents = 101 } },
            "cpu-count" => capture with { CpuSummaries = capture.CpuSummaries.Select(s => s with { Cpu = s.Cpu + 10 }).ToArray() },
            _ => capture with { CpuSummaries = capture.CpuSummaries.Select(s => s with { TotalMicroseconds = s.TotalMicroseconds + 1 }).ToArray() }
        };
        var result = LatencyInterpretation.Consult(capture, "Carga conhecida");
        Assert.Equal(LatencyConsultationClassification.Inconclusive, result.Classification);
        Assert.True(result.Partial);
        Assert.Contains(result.Details, d => d.Contains("diverge", StringComparison.Ordinal));
    }

    [Fact]
    public void EventsOutsideTheWindowDoNotContributeToReportedTemporalRecurrence()
    {
        var capture = CompleteCapture(recurrent: true);
        capture = capture with { Timeline = capture.Timeline!.Select(e => e with { TraceRelativeMilliseconds = 31_000 }).ToArray() };
        Assert.All(LatencyResultPresentation.Summarize(capture), summary => Assert.Null(summary.RecurrenceEventCount));
        var result = LatencyInterpretation.Consult(capture, "Carga conhecida");
        Assert.Equal(LatencyConsultationClassification.Inconclusive, result.Classification);
        Assert.DoesNotContain(result.Findings, f => f.Id.StartsWith("dpc-recurring:", StringComparison.Ordinal));
        Assert.Contains(Assert.Single(result.DeviceRelations).TechnicalDetails, d => d.Contains("posição temporal válida em 0 eventos"));
    }

    [Fact]
    public void ExactDriverPathLinksObservedPoliciesWithoutClaimingCausality()
    {
        var capture = CompleteCapture();
        var device = Device("physical-a", @"C:\Windows\System32\drivers\netdriver.sys");
        var msi = device with { Observation = device.Observation with { RuleId = "devices.interrupt.msi", RawValue = "1" } };
        var report = new PerformanceAuditReport(capture.StartedAt, "Fixture audit", [device, msi], []);
        var relation = Assert.Single(LatencyDeviceCorrelation.Analyze(capture, report));
        Assert.True(relation.ExactAssociation);
        Assert.Equal("physical-a", relation.DeviceId);
        Assert.Contains("não demonstra", relation.Observation);
        Assert.Contains(relation.TechnicalDetails, d => d.Contains("devices.interrupt.msi: 1"));
        Assert.Contains(relation.TechnicalDetails, d => d.Contains("CPU ETW 0") && d.Contains("100 eventos"));
        Assert.Contains(relation.TechnicalDetails, d => d.Contains("CPU compartilhada não é conflito"));
    }

    [Fact]
    public void SharedBinaryDoesNotChooseADeviceAndFilenameAloneIsOnlyACandidate()
    {
        var capture = CompleteCapture();
        var a = Device("a", @"C:\Windows\System32\drivers\netdriver.sys");
        var b = Device("b", @"C:\Windows\System32\drivers\netdriver.sys");
        var report = new PerformanceAuditReport(capture.StartedAt, "Fixture", [a, b], []);
        var ambiguous = Assert.Single(LatencyDeviceCorrelation.Analyze(capture, report));
        Assert.Null(ambiguous.DeviceId); Assert.False(ambiguous.ExactAssociation);
        Assert.Equal("Dispositivo ambíguo", ambiguous.DeviceName);
        var candidate = Assert.Single(LatencyDeviceCorrelation.Analyze(capture, report with { Assessments = [Device("a", "netdriver.sys")] }));
        Assert.False(candidate.ExactAssociation);
        Assert.Contains("caminho completo não foi confirmado", candidate.Observation);
    }

    [Fact]
    public void ContradictoryPathCannotBeOverriddenByMatchingServiceName()
    {
        var capture = CompleteCapture();
        var device = Device("a", @"D:\Other\netdriver.sys", "netdriver");
        var relation = Assert.Single(LatencyDeviceCorrelation.Analyze(capture, new(capture.StartedAt, "Fixture", [device], [])));
        Assert.Null(relation.DeviceId);
        Assert.False(relation.ExactAssociation);
    }

    [Fact]
    public void MissingAuditAndHistoricalSnapshotRemainExplicitLimits()
    {
        var capture = CompleteCapture() with { SessionName = null };
        var missing = Assert.Single(LatencyDeviceCorrelation.Analyze(capture, null));
        Assert.Null(missing.DeviceId);
        Assert.Contains(missing.TechnicalDetails, d => d.Contains("Snapshot de dispositivos indisponível"));
        var report = new PerformanceAuditReport(capture.StartedAt.AddDays(1), "Fixture", [Device("a", @"C:\Windows\System32\drivers\netdriver.sys")], []);
        var historical = Assert.Single(LatencyDeviceCorrelation.Analyze(capture, report));
        Assert.Contains(historical.TechnicalDetails, d => d.Contains("captura histórica/importada"));
        Assert.Contains(historical.TechnicalDetails, d => d.Contains("não comprova a configuração vigente"));
    }

    private static EtwCaptureResult CompleteCapture(int seconds = 30, bool recurrent = false)
    {
        var started = DateTimeOffset.Parse("2026-10-21T12:00:00+00:00");
        var aggregate = new LatencyAggregator();
        aggregate.ObserveKernelImage(4, 0x1000, 100, @"C:\Windows\System32\drivers\netdriver.sys");
        for (var i = 0; i < 100; i++)
        {
            var time = i * (seconds * 1000 - 1d) / 99;
            aggregate.ObserveLatency("DPC", 0, 0x1010, recurrent && i % 20 == 0 ? 1500 : i + 1, time, started.AddMilliseconds(time));
            aggregate.ObserveLatency("ISR", 1, 0x1010, (i + 1) / 2d, time, started.AddMilliseconds(time));
        }
        var values = aggregate.Snapshot(); var timeline = aggregate.TimelineSnapshot();
        return new(Availability.Available, "Synthetic fixture", started, TimeSpan.FromSeconds(seconds), "fixture-owned-session", 200, 0, false, false,
            values.Cpu, values.Modules, values.Limitations, timeline.Events, timeline.Truncated, TraceStartUtc: started,
            TimeBuckets: timeline.Buckets, EventCounts: aggregate.EventCounts(200));
    }

    private static AuditAssessment Device(string id, string image, string service = "netdriver")
    {
        var rule = new AuditRule("devices.identity", "devices", "Device identity", "Fixture", "", "", "", "", AuditSupport.Documented, "", "", []);
        var observation = new AuditObservation(rule.Id, id, "Ethernet fixture", "Device", "Present", Availability.Available, "Fixture",
            new Dictionary<string, string> { ["driverImagePath"] = image, ["service"] = service, ["pnpProblemCode"] = "0" });
        return new(observation, rule, AuditClassification.AppliedUnproven, "Fixture", "Fixture", "", Confidence.High, "", "");
    }
}
