using PCAnalyzer.Core;

namespace PCAnalyzer.App.ViewModels;

public sealed record HomeMeasurementRow(string Name, string Reading, string Context, string TestId, string Interpretation);

public sealed partial class MainViewModel
{
    private readonly Dictionary<Guid, TestSession> _validMeasurementTests = [];
    private readonly HashSet<Guid> _restoredMeasurementHistory = [];
    private int _latencySeconds = 30;
    private string _latencyGoal = "Observação rápida";
    public IReadOnlyList<int> LatencyDurations { get; } = [30, 60, 120];
    public IReadOnlyList<string> LatencyGoals { get; } = ["Observação rápida", "Durante o jogo", "Reproduzir travadas esporádicas"];
    public int LatencySeconds
    {
        get => _latencySeconds;
        set { if (LatencyDurations.Contains(value) && Set(ref _latencySeconds, value)) Notify(nameof(LatencyCaptureLabel)); }
    }
    public string LatencyGoal
    {
        get => _latencyGoal;
        set
        {
            if (!LatencyGoals.Contains(value) || !Set(ref _latencyGoal, value)) return;
            LatencySeconds = value == "Durante o jogo" ? 60 : value == "Reproduzir travadas esporádicas" ? 120 : 30;
            Notify(nameof(LatencyDurationGuidance));
        }
    }
    public string LatencyCaptureLabel => $"Capturar DPC / ISR · {LatencySeconds} s…";
    public string LatencyDurationGuidance => LatencyGoal switch
    {
        "Durante o jogo" => "60 s sugeridos para reproduzir uma situação do jogo. A duração não comprova representatividade.",
        "Reproduzir travadas esporádicas" => "120 s sugeridos para tentar capturar o sintoma. Um evento pode não ocorrer nessa janela.",
        _ => "30 s oferecem um recorte inicial. Repita durante o sintoma para investigar recorrência."
    };
    public string GeneralAnalysisScope => $"Inventário até concluir as leituras; depois observação passiva de {ObservationSeconds} s. Não certifica desempenho ideal.";
    public RelayCommand InvestigatePerformanceCommand { get; private set; } = null!;
    public RelayCommand ChooseMeasurementCommand { get; private set; } = null!;
    public IEnumerable<AuditSettingPresentation> HomeAdequateSettings => _auditSettings.Where(s => s.IsAdequate)
        .OrderByDescending(s => s.CategoryId == "games").ThenBy(s => s.Name).Take(4);
    public string HomeAdequateSummary => HomeAdequateSettings.Any()
        ? "Leituras para as quais a regra não sustenta uma alteração. Isso não certifica todo o computador."
        : "Ainda não há evidência suficiente para destacar configurações adequadas.";
    public string HomeCoverage => _auditSettingsRun?.Result?.Audit is { } report
        ? $"{report.Assessments.Count(a => a.Observation.Status == Availability.Available)} de {report.Assessments.Count} leituras disponíveis · snapshot de {report.CapturedAt.LocalDateTime:dd/MM HH:mm}. A quantidade não é nota de desempenho."
        : "Nenhum inventário de configurações concluído nesta sessão.";
    public IReadOnlyList<RecommendationRow> HomeMeasurementConclusions => _validMeasurementTests.Values
        .OrderByDescending(r => r.StartedAt)
        .DistinctBy(r => TestDestination(r.TestId))
        .SelectMany(r => (r.Result?.Findings ?? [])
            .Where(f => f.Category is FindingCategory.ObservedProblem or FindingCategory.PossibleProblem)
            .Take(1).Select(f => new RecommendationRow(f.Title,
                $"{r.StartedAt.LocalDateTime:dd/MM HH:mm} · {f.Explanation} Próximo passo: {f.Recommendation}",
                "Ver resultado", new SuggestedNextAction("measurement:" + r.Id, "Ver resultado", TestDestination(r.TestId), f.Recommendation))))
        .Take(3).ToArray();
    public IEnumerable<HomeMeasurementRow> HomeMeasurements => new[]
    {
        LastMeasurement("DPC máximo", ["dpc", "etl-import"], "dpc", i => i.Id == "dpc.maximum.us"),
        LastMeasurement("ISR máximo", ["dpc", "etl-import"], "dpc", i => i.Id == "isr.maximum.us"),
        LastMeasurement("RTT médio", ["ping"], "ping", i => i.Id == "network.ping.rtt.ms"),
        LastMeasurement("Variação do RTT", ["ping"], "ping", i => i.Id.Contains("jitter", StringComparison.Ordinal)),
        LastMeasurement("Frametime p99", ["frames", "frames-import", "gaming-analysis"], "frames", i => i.Id.EndsWith(".p99.ms", StringComparison.Ordinal)),
        LastMeasurement("Acesso à memória", ["memory-access"], "memory-access", i => i.Unit is "ns/acesso" or "ns/access" or "ns")
    };
    private HomeMeasurementRow LastMeasurement(string name, string[] ids, string test, Func<ResultIndicator, bool> match)
    {
        var run = _validMeasurementTests.Values.Where(r => ids.Contains(r.TestId) && r.Result?.Indicators?.Any(i => match(i) && i.Value is { } n && double.IsFinite(n)) == true)
            .OrderByDescending(r => r.StartedAt).FirstOrDefault();
        var values = run?.Result?.Indicators?.Where(match).ToArray() ?? [];
        var reading = values.Length == 1 ? new IndicatorRow(values[0]).Reading : values.Length > 1 ? "Ver resultados por cenário" : "Não medido";
        return new(name, reading, run is null ? "Escolha uma investigação manual." :
            $"{run.StartedAt.LocalDateTime:dd/MM HH:mm} · {run.Result?.SelectionContext ?? "Contexto não registrado"}", test,
            run?.Result?.Interpretation ?? run?.Result?.Findings?.FirstOrDefault()?.Explanation ??
                (run is null ? "" : "Sem conclusão fundamentada para esta medição; consulte o contexto e os limites no resultado."));
    }
    private void RememberValidMeasurement(TestSession run)
    {
        if (run.Status != TestStatus.Completed || run.Result is not { Availability: Availability.Available, Cancelled: false } result ||
            result.Indicators?.Any(i => i.Value is { } value && double.IsFinite(value)) != true) return;
        _validMeasurementTests[run.Id] = run;
        // A completed ping with 100% loss has a valid loss count but no RTT or jitter.
        // Keep the newest valid source of each indicator, not merely the newest test.
        var retained = _validMeasurementTests.Values.OrderByDescending(r => r.StartedAt)
            .SelectMany(r => (r.Result?.Indicators ?? []).Where(i => i.Value is { } value && double.IsFinite(value))
                .Select(i => (Key: (r.TestId, i.Id), RunId: r.Id)))
            .DistinctBy(item => item.Key).Select(item => item.RunId).ToHashSet();
        foreach (var id in _validMeasurementTests.Keys.Where(id => !retained.Contains(id)).ToArray()) _validMeasurementTests.Remove(id);
        RefreshHomePriorities();
        NotifyConsultation();
    }
    private async Task RestoreMeasurementHistoryAsync(IReadOnlyList<TestRunSummary> history)
    {
        foreach (var item in history.Where(r => r.Status == TestStatus.Completed &&
            r.TestId is "dpc" or "etl-import" or "ping" or "frames" or "frames-import" or "gaming-analysis" or "memory-access")
            .OrderByDescending(r => r.StartedAt))
        {
            if (_restoredMeasurementHistory.Contains(item.Id)) continue;
            if (await _backend.LoadTestAsync(item.Id, _lifetime.Token) is { } run)
            {
                RememberValidMeasurement(run);
                _restoredMeasurementHistory.Add(item.Id);
            }
        }
    }
    private void RefreshHomePriorities()
    {
        // Keep the main page's total at five; measurement findings include their own evidence and date.
        Replace(PriorityAuditSettings, _auditSettings.Where(r => r.IsImprovement).OrderByDescending(r => r.Priority)
            .Take(5 - HomeMeasurementConclusions.Count));
        Replace(RecommendedTests, _auditSettings.Where(r => r.IsImprovement && r.TestId is not null)
            .OrderByDescending(r => r.Priority).DistinctBy(r => r.TestId).Take(3)
            .Select(r => new RecommendationRow(r.Name, r.Validation, "Escolher teste",
                new SuggestedNextAction("audit-test:" + r.TestId, "Escolher teste", TestDestination(r.TestId!), r.Reason))));
        Notify(nameof(HasPriorityAuditSettings)); Notify(nameof(PriorityAuditSummary));
    }
    private void NotifyConsultation()
    {
        foreach (var name in new[] { nameof(HomeAdequateSettings), nameof(HomeAdequateSummary), nameof(HomeCoverage), nameof(HomeMeasurements), nameof(HomeMeasurementConclusions), nameof(HasHomeResult), nameof(HomeAuditState), nameof(HomeAuditSummary) }) Notify(name);
    }
}
