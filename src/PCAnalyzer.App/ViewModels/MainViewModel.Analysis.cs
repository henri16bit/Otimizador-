using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.Win32;
using PCAnalyzer.Core;

namespace PCAnalyzer.App.ViewModels;

public sealed record CompactSystemRow(string Name, string Value, string Status, string Detail);
public sealed record RecommendationRow(string Title, string Description, string ActionLabel, SuggestedNextAction Action);
public sealed record TelemetryHealthRow(TelemetrySourceHealth Health)
{
    public string Name => Health.Name;
    public string StateLabel => Health.State switch
    {
        TelemetrySourceState.Healthy => "Leitura recente", TelemetrySourceState.Waiting => "Aguardando leitura",
        TelemetrySourceState.Stale => "Sem atualização", TelemetrySourceState.Paused => "Pausada",
        TelemetrySourceState.Error => "Falha de leitura", _ => "Indisponível"
    };
    public string Detail => Health.Summary;
    public string LastUpdated => Health.LastSampleAt is { } time ? "Último dado: " + time.LocalDateTime.ToString("HH:mm:ss") : "Ainda sem medição válida";
}

public sealed partial class MainViewModel
{
    private readonly TelemetryHealthService _health = new();
    private volatile bool _coordinatedCollection;
    private DateTimeOffset? _lastBatchAt;
    private IReadOnlyList<ProcessSample> _liveProcesses = [];
    private DateTimeOffset _collectionStartedAt = DateTimeOffset.UtcNow;
    private TestSession? _lastDiagnostic;
    private DiagnosticReport? _simpleReport;
    private PerformanceAuditReport? _auditReport;
    private string _interfaceMode = "Simples", _presentMonPath = "", _gameWaitProcessName = "";
    private string _presentMonStatus = "Configure o PresentMon uma vez para capturar os quadros do jogo.";
    private int _observationSeconds = 30, _gameWaitSeconds = 120;
    private bool _includePing, _includeLatency, _showTechnicalDetails;
    public IReadOnlyList<string> InterfaceModes { get; } = ["Simples", "Avançado"];
    public string InterfaceMode
    {
        get => _interfaceMode;
        set { if (InterfaceModes.Contains(value) && Set(ref _interfaceMode, value)) { Notify(nameof(VisibleSections)); Notify(nameof(MenuSelection)); Notify(nameof(HeaderIsAdvancedView)); } }
    }
    public IEnumerable<Section> VisibleSections =>
        new[] { "dashboard", "windows", "nvidia", "network", "devices", "latency", "hardware", "games", "ajustes", "avancado" }.Select(id => Sections.Single(s => s.Id == id));
    public Section? MenuSelection
    {
        get => VisibleSections.Contains(SelectedSection!) ? SelectedSection : Sections.Single(s => s.Id == "avancado");
        set { if (value is not null) SelectedSection = value; }
    }
    public bool HeaderIsAdvancedView => InterfaceMode == "Avançado" || SelectedSection?.Id is "componentes" or "processos" or "investigar" or "avancado";
    public IReadOnlyList<int> ObservationDurations { get; } = [15, 30, 60];
    public int ObservationSeconds { get => _observationSeconds; set { if (ObservationDurations.Contains(value)) Set(ref _observationSeconds, value); } }
    public bool IncludePing { get => _includePing; set => Set(ref _includePing, value); }
    public bool IncludeLatency { get => _includeLatency; set => Set(ref _includeLatency, value); }
    public string PresentMonPath { get => _presentMonPath; private set => Set(ref _presentMonPath, value); }
    public string PresentMonStatus { get => _presentMonStatus; private set => Set(ref _presentMonStatus, value); }
    public string GameWaitProcessName { get => _gameWaitProcessName; set => Set(ref _gameWaitProcessName, value); }
    public int GameWaitSeconds { get => _gameWaitSeconds; set => Set(ref _gameWaitSeconds, value); }
    public bool ShowTechnicalDetails { get => _showTechnicalDetails; set => Set(ref _showTechnicalDetails, value); }
    public ObservableCollection<CompactSystemRow> CompactSystemRows { get; } = [];
    public ObservableCollection<TelemetryHealthRow> TelemetryHealthRows { get; } = [];
    public ObservableCollection<ComponentAssessment> ReportComponents { get; } = [];
    public ObservableCollection<RecommendationRow> ReportRecommendations { get; } = [];
    public ObservableCollection<RecommendationRow> PriorityRecommendations { get; } = [];
    public ObservableCollection<TestRunRow> RecentAnalyses { get; } = [];
    public ObservableCollection<AuditCategoryRow> AuditCategories { get; } = [];
    public ObservableCollection<AuditAssessmentRow> AuditRecommendations { get; } = [];
    public ObservableCollection<AuditAssessmentRow> AuditHighlights { get; } = [];
    public ObservableCollection<string> AuditLimitations { get; } = [];
    public bool HasSimpleReport => _simpleReport is not null;
    public bool HasAuditReport => _auditReport is not null;
    public bool HasConsultationReport => HasSimpleReport || HasAuditReport;
    public bool HasInlineResult => _openedTest?.TestId is "general" or "gaming-analysis" or "audit.configuration";
    public string AuditCoverage => _auditReport is { } report ?
        $"{report.Assessments.Where(a => a.Observation.Status == Availability.Available).Select(a => a.Rule.Category).Distinct().Count()} de {AuditCategories.Count} áreas com leituras · {report.Assessments.Count(a => a.Observation.Status == Availability.Available)} verificações disponíveis · {report.Assessments.Count(a => a.Observation.Status != Availability.Available)} com limites" :
        "Ainda sem auditoria. Leia as configurações para verificar a cobertura real.";
    public string AuditSummary => _auditReport?.Summary ?? "A auditoria identifica ajustes e explica o que ainda precisa de medição.";
    public string AuditRecommendationsSummary => !HasAuditReport ? "Faça uma auditoria para receber orientações baseadas na configuração encontrada." :
        AuditRecommendations.Count > 0 ? $"{AuditRecommendations.Count} pontos priorizados para investigação ou comparação. Nenhuma alteração será executada." :
        "Esta auditoria não encontrou evidência suficiente para recomendar uma mudança. Confira a cobertura e os limites antes de concluir.";
    public string AuditAdequateSummary => _auditReport is { } report ?
        $"{report.Assessments.Count(a => a.Classification == AuditClassification.Adequate)} verificações adequadas nas condições avaliadas. Ausência de dados não significa configuração adequada." : "";
    public IEnumerable<ComponentAssessment> LatencyAssessments => ReportComponents.Where(c => c.Id is "frames" or "stability" || c.Name.Contains("latência", StringComparison.OrdinalIgnoreCase));
    public string SimpleReportTitle => HasConsultationReport ? "Principais descobertas" : OperationTitle;
    public string SimpleReportSummary => _simpleReport?.Summary ?? _auditReport?.Summary ?? _openedTest?.Error ??
        (_openedTest?.Status == TestStatus.Cancelled ? "A execução foi cancelada. Nenhuma conclusão anterior foi reutilizada." : "Abra uma análise do histórico ou inicie uma nova. Os resultados de ferramentas específicas ficam nos detalhes abaixo.");
    public string SimpleReportState => _simpleReport is { } report ? CompletionLabel(report.Completion) : HasAuditReport ? "Auditoria de configurações · " + TestLabels.Status(_openedTest!.Status).ToLowerInvariant() :
        _openedTest is { } run ? TestLabels.Status(run.Status) : "Nenhum resultado aberto";
    public bool HasDiagnostic => _lastDiagnostic is not null;
    public string LastDiagnosticSummary => _lastDiagnostic?.Result?.Report is { } report ? ReportHeadline(report) : _lastDiagnostic?.Error ?? _lastDiagnostic?.Result?.Audit?.Summary ?? _lastDiagnostic?.Result?.Title
        ?? "Faça uma análise para conhecer as medições, os limites e os próximos passos.";
    public string LastDiagnosticDate => _lastDiagnostic?.StartedAt.LocalDateTime.ToString("dd/MM/yyyy HH:mm") ?? "Nenhuma análise realizada";
    public string LastDiagnosticState => _lastDiagnostic?.Result?.Report is { } report ? CompletionLabel(report.Completion) :
        _lastDiagnostic is { } run ? TestLabels.Status(run.Status) : "Pronto para começar";
    public string LastDiagnosticCounts => _lastDiagnostic?.Result?.Report is { } report
        ? $"{report.Components.Count(c => c.State == DiagnosticAssessmentState.Problem)} problemas · {report.Components.Count(c => c.State == DiagnosticAssessmentState.Attention)} atenções · {report.Components.Count(c => c.State == DiagnosticAssessmentState.Inconclusive)} inconclusivos" : "";
    public string ProcessEmptyMessage => IsHistoryView ? "Esta sessão não contém processos registrados." :
        !string.IsNullOrWhiteSpace(ProcessFilter) && Processes.Count > 0 ? "Nenhum processo corresponde ao filtro. Limpe a busca para ver a lista." :
        TelemetryHealthRows.FirstOrDefault(h => h.Health.Id == "processes")?.Detail ?? "Aguardando a primeira leitura de processos.";
    public AsyncCommand AnalyzePcCommand { get; private set; } = null!;
    public AsyncCommand AuditConfigurationCommand { get; private set; } = null!;
    public AsyncCommand AnalyzeGamesCommand { get; private set; } = null!;
    public AsyncCommand ConfigurePresentMonCommand { get; private set; } = null!;
    public AsyncCommand RetryTelemetryCommand { get; private set; } = null!;
    public RelayCommand OpenGameAnalysisCommand { get; private set; } = null!;
    public RelayCommand OpenLastDiagnosticCommand { get; private set; } = null!;
    public RelayCommand SuggestedActionCommand { get; private set; } = null!;

    private void InitializeAnalysisCommands()
    {
        AnalyzePcCommand = Command(() => StartAnalysisAsync(false, SelectedSection?.Id == "dashboard"));
        AuditConfigurationCommand = Command(() => RunTestAsync("audit.configuration", (progress, ct) => _backend.AuditConfigurationAsync(progress, ct)));
        AnalyzeGamesCommand = Command(() => StartAnalysisAsync(true));
        ConfigurePresentMonCommand = Command(ConfigurePresentMonAsync);
        RetryTelemetryCommand = Command(async () =>
        {
            await _backend.ResetTelemetryAsync(OperationToken);
            _collectionStartedAt = DateTimeOffset.UtcNow;
            _latest.Clear(); _liveProcesses = []; Processes.Clear(); RefreshVisibleProcesses(); _lastBatchAt = null;
            lock (_sampleLock) _pendingBatch = null;
            if (_inventoryTask?.IsCompleted != false) _inventoryTask = RefreshInventoryAsync();
            RefreshDisplay(); RefreshTelemetryHealth();
            Status = "Fontes reiniciadas. Aguarde as próximas leituras; nenhum ajuste do Windows foi alterado.";
        });
        OpenGameAnalysisCommand = new RelayCommand(() => Navigate("games"));
        OpenLastDiagnosticCommand = new RelayCommand(() =>
        { if (_lastDiagnostic is { } run) { DisplayTestResult(run); Navigate("resultados"); } }, () => HasDiagnostic);
        SuggestedActionCommand = new RelayCommand(parameter =>
        {
            if (parameter is not SuggestedNextAction action) return;
            switch (action.Target)
            {
                case "rede": OpenInvestigationCommand.Execute("rede"); break;
                case "drivers": OpenInvestigationCommand.Execute("drivers"); break;
                case "jogos": Navigate("analisar"); break;
                case "armazenamento": OpenComponentCommand.Execute("nvme"); break;
                case "fontes": Navigate("config"); break;
                default: Navigate(action.Target); break;
            }
            Notification = action.Reason;
        });
        RefreshTelemetryHealth();
    }
    private async Task ConfigurePresentMonAsync()
    {
        var dialog = new OpenFileDialog { Filter = "PresentMon Console|*.exe", Title = "Selecionar PresentMon oficial", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        PresentMonStatus = "Verificando assinatura e compatibilidade…";
        var tool = await _backend.InspectPresentMonAsync(dialog.FileName, OperationToken);
        PresentMonStatus = tool.Detail;
        if (tool.Status != Availability.Available) return;
        PresentMonPath = tool.ExecutablePath;
        await SavePreferencesAsync();
    }
    private async Task StartAnalysisAsync(bool gaming, bool passiveOnly = false)
    {
        int? pid = null;
        if (gaming)
        {
            if (string.IsNullOrWhiteSpace(PresentMonPath)) throw new InvalidOperationException("Use Configurar PresentMon antes de analisar o jogo. A análise geral do PC continua disponível.");
            if (string.IsNullOrWhiteSpace(GameWaitProcessName))
            {
                if (!int.TryParse(ProcessIdText, NumberStyles.None, CultureInfo.InvariantCulture, out var selected) || selected <= 0)
                    throw new InvalidOperationException("Escolha o jogo aberto na lista ou informe o nome do processo para aguardar sua abertura.");
                pid = selected;
            }
            if (GameWaitSeconds is < 1 or > 300) throw new InvalidOperationException("O tempo de espera deve ficar entre 1 e 300 segundos.");
        }
        var seconds = gaming ? 30 : ObservationSeconds;
        var includePing = !passiveOnly && IncludePing;
        var includeLatency = !passiveOnly && IncludeLatency;
        var options = new DiagnosticOptions(seconds, includePing, includeLatency, gaming,
            PingHost.Trim(), SelectedAdapter?.Id, Workload, PresentMonPath, pid,
            gaming && pid is null ? GameWaitProcessName.Trim() : null, GameWaitSeconds);
        var explanation = $"Observar o uso atual por {seconds} segundos e salvar um relatório. Use o PC como de costume durante a análise.\n\nNenhum teste de carga ou ajuste de configuração será iniciado.";
        if (includePing) explanation += "\n• Rede: enviar 20 pacotes ICMP para " + PingHost.Trim() + ".";
        if (includeLatency) explanation += "\n• Drivers: captura ETW de 30 s, com confirmação UAC do Windows e possível custo adicional.";
        if (gaming) explanation += $"\n• Jogo: captura externa de quadros por 30 s. {(pid is { } p ? "PID " + p : "Aguardar " + GameWaitProcessName + " por até " + GameWaitSeconds + " s")}.";
        if ((gaming || includePing || includeLatency) && !Confirm(gaming ? "Analisar durante o jogo" : "Confirmar testes opcionais", explanation + "\n\nIniciar análise?")) return;
        try
        {
            await RunTestAsync(gaming ? "gaming-analysis" : "general", (progress, ct) => _backend.AnalyzePcAsync(options, progress, batch =>
            {
                _coordinatedCollection = true;
                AcceptBatch(batch);
            }, ct));
        }
        finally { _coordinatedCollection = false; }
    }
    private void AcceptBatch(TelemetryBatch batch)
    {
        lock (_sampleLock)
        {
            _samples.AddRange(batch.Metrics); TrimSamples();
            // An older passive read can finish after the coordinated one.
            if (_pendingBatch is null || batch.Timestamp >= _pendingBatch.Timestamp) _pendingBatch = batch;
        }
    }
    private void RefreshSimpleReport(TechnicalResult? result)
    {
        _simpleReport = result?.Report;
        _auditReport = result?.Audit;
        if (result?.Audit is { } audit) UpdateAuditSettings(audit);
        Replace(ReportComponents, _simpleReport?.Components ?? []);
        Replace(ReportRecommendations, Recommendations(_simpleReport));
        Replace(AuditCategories, (_auditReport?.Assessments ?? []).GroupBy(a => a.Rule.Category)
            .Select(g => new AuditCategoryRow(g.Key, g.Select(a => new AuditAssessmentRow(a)).ToArray())));
        Replace(AuditRecommendations, PriorityAuditSettings.Select(r => new AuditAssessmentRow(r.Evidence[0])));
        Replace(AuditHighlights, (_auditReport?.Assessments ?? [])
            .Where(a => a.Classification is AuditClassification.DemonstratedProblem or AuditClassification.PossibleProblem
                or AuditClassification.NoDocumentedEffect or AuditClassification.AppliedUnproven or AuditClassification.ComparisonOpportunity)
            .OrderBy(a => a.Classification switch { AuditClassification.DemonstratedProblem => 0, AuditClassification.PossibleProblem => 1,
                AuditClassification.NoDocumentedEffect => 2, AuditClassification.AppliedUnproven => 3, _ => 4 })
            .Take(5).Select(a => new AuditAssessmentRow(a)));
        Replace(AuditLimitations, _auditReport?.Limitations.Distinct() ?? []);
        ShowTechnicalDetails = result is not null && !HasConsultationReport;
        foreach (var name in new[] { nameof(HasSimpleReport), nameof(HasAuditReport), nameof(HasConsultationReport), nameof(HasInlineResult), nameof(SimpleReportTitle), nameof(SimpleReportSummary), nameof(SimpleReportState),
            nameof(AuditCoverage), nameof(AuditSummary), nameof(AuditRecommendationsSummary), nameof(AuditAdequateSummary), nameof(LatencyAssessments) }) Notify(name);
        NotifyAuditExperience();
    }
    private static IEnumerable<RecommendationRow> Recommendations(DiagnosticReport? report) => report?.Recommendations.Select(action =>
        new RecommendationRow(report.Components.FirstOrDefault(c => c.NextAction == action)?.Name ?? "Próximo passo", action.Reason, action.Label, action)) ?? [];
    private void UpdateLastDiagnostic(TestSession run)
    {
        if (run.TestId is not ("general" or "gaming-analysis" or "audit.configuration") || _lastDiagnostic is { } previous && previous.StartedAt > run.StartedAt) return;
        _lastDiagnostic = run;
        Replace(PriorityRecommendations, Recommendations(run.Result?.Report).Take(3));
        foreach (var name in new[] { nameof(HasDiagnostic), nameof(LastDiagnosticSummary), nameof(LastDiagnosticDate), nameof(LastDiagnosticState), nameof(LastDiagnosticCounts) }) Notify(name);
        OpenLastDiagnosticCommand?.Refresh();
    }
    private static string CompletionLabel(DiagnosticCompletion completion) => completion switch
    { DiagnosticCompletion.Completed => "Análise concluída", DiagnosticCompletion.Partial => "Análise parcial · veja as limitações", DiagnosticCompletion.Cancelled => "Cancelada · dados disponíveis preservados", _ => "Não foi possível concluir a análise" };
    private static string ReportHeadline(DiagnosticReport report)
    {
        var main = report.Components.Where(c => c.State is DiagnosticAssessmentState.Problem or DiagnosticAssessmentState.Attention)
            .OrderByDescending(c => c.State == DiagnosticAssessmentState.Problem).FirstOrDefault();
        return main is null ? report.Summary : main.Name + ": " + main.Summary;
    }
    private void RefreshTelemetryHealth()
    {
        var health = _health.Evaluate(_latest.Values.ToArray(), _liveProcesses, DateTimeOffset.UtcNow,
            TimeSpan.FromSeconds(_coordinatedCollection || _diagnosticCadence ? 1 : _minimized ? Math.Max(15, IntervalSeconds) : IntervalSeconds),
            _paused && !_coordinatedCollection, _collectionStartedAt, _lastBatchAt);
        Replace(TelemetryHealthRows, health.Select(h => new TelemetryHealthRow(h)));
        var inventory = _loadedSession?.Inventory ?? _inventory;
        Replace(CompactSystemRows, new[] { ("CPU", "cpu"), ("GPU", "gpu"), ("RAM", "ram"), ("Discos", "disk"), ("Rede", "network"), ("Sistema", "os") }.Select(pair =>
        {
            var category = pair.Item1 switch { "Discos" => "Armazenamento", _ => pair.Item1 };
            var name = string.Join(" · ", inventory?.Components.Where(c => c.Category == category).Select(c => c.Name).Distinct() ?? []);
            var source = TelemetryHealthRows.FirstOrDefault(h => h.Health.Id == pair.Item2);
            var status = IsHistoryView ? "Identificação da sessão salva" : source?.StateLabel ?? (name.Length > 0 ? "Identificado" : "Identificação indisponível");
            return new CompactSystemRow(pair.Item1, name.Length > 0 ? name : "Identificação ainda indisponível", status,
                source?.Detail ?? "Consulte os detalhes em Avançado → Componentes.");
        }));
        Notify(nameof(CollectionStateLabel)); Notify(nameof(ViewContext)); Notify(nameof(ProcessEmptyMessage));
        Notify(nameof(CompactHardwareSummary));
    }
}
