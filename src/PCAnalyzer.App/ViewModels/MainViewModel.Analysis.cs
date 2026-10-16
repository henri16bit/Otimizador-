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
    public IEnumerable<Section> VisibleSections => InterfaceMode == "Avançado" ? Sections :
        new[] { "dashboard", "analisar", "resultados", "ajustes", "avancado", "config" }.Select(id => Sections.Single(s => s.Id == id));
    public Section? MenuSelection
    {
        get => InterfaceMode == "Avançado" ? SelectedSection : Sections.FirstOrDefault(s => s.Id == (SelectedSection?.Id switch
        { "componentes" or "processos" or "investigar" => "avancado", "historico" => "resultados", var id => id }));
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
    public bool HasSimpleReport => _simpleReport is not null;
    public string SimpleReportTitle => HasSimpleReport ? "O que a análise encontrou" : OperationTitle;
    public string SimpleReportSummary => _simpleReport?.Summary ?? "Abra uma análise do histórico ou inicie uma nova. Os resultados de ferramentas específicas ficam nos detalhes abaixo.";
    public string SimpleReportState => _simpleReport is { } report ? CompletionLabel(report.Completion) : "Resultado da ferramenta selecionada";
    public bool HasDiagnostic => _lastDiagnostic is not null;
    public string LastDiagnosticSummary => _lastDiagnostic?.Result?.Report is { } report ? ReportHeadline(report) : _lastDiagnostic?.Error ?? _lastDiagnostic?.Result?.Title
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
    public AsyncCommand AnalyzeGamesCommand { get; private set; } = null!;
    public AsyncCommand ConfigurePresentMonCommand { get; private set; } = null!;
    public AsyncCommand RetryTelemetryCommand { get; private set; } = null!;
    public RelayCommand OpenGameAnalysisCommand { get; private set; } = null!;
    public RelayCommand OpenLastDiagnosticCommand { get; private set; } = null!;
    public RelayCommand SuggestedActionCommand { get; private set; } = null!;

    private void InitializeAnalysisCommands()
    {
        AnalyzePcCommand = Command(() => StartAnalysisAsync(false));
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
        OpenGameAnalysisCommand = new RelayCommand(() => Navigate("analisar"));
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
    private async Task StartAnalysisAsync(bool gaming)
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
        var options = new DiagnosticOptions(seconds, IncludePing, IncludeLatency, gaming,
            PingHost.Trim(), SelectedAdapter?.Id, Workload, PresentMonPath, pid,
            gaming && pid is null ? GameWaitProcessName.Trim() : null, GameWaitSeconds);
        var explanation = $"Observar o uso atual por {seconds} segundos e salvar um relatório. Use o PC como de costume durante a análise.\n\nNenhum teste de carga ou ajuste de configuração será iniciado.";
        if (IncludePing) explanation += "\n• Rede: enviar 20 pacotes ICMP para " + PingHost.Trim() + ".";
        if (IncludeLatency) explanation += "\n• Drivers: captura ETW de 30 s, com confirmação UAC do Windows e possível custo adicional.";
        if (gaming) explanation += $"\n• Jogo: captura externa de quadros por 30 s. {(pid is { } p ? "PID " + p : "Aguardar " + GameWaitProcessName + " por até " + GameWaitSeconds + " s")}.";
        if (!Confirm(gaming ? "Analisar durante o jogo" : "Analisar meu PC", explanation + "\n\nIniciar análise?")) return;
        var initialPage = SelectedSection?.Id;
        try
        {
            await RunTestAsync(gaming ? "gaming-analysis" : "general", (progress, ct) => _backend.AnalyzePcAsync(options, progress, batch =>
            {
                _coordinatedCollection = true;
                AcceptBatch(batch);
            }, ct));
        }
        finally { _coordinatedCollection = false; }
        if (SelectedSection?.Id == initialPage && initialPage is "dashboard" or "analisar") Navigate("resultados");
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
        Replace(ReportComponents, _simpleReport?.Components ?? []);
        Replace(ReportRecommendations, Recommendations(_simpleReport));
        ShowTechnicalDetails = result is not null && !HasSimpleReport;
        foreach (var name in new[] { nameof(HasSimpleReport), nameof(SimpleReportTitle), nameof(SimpleReportSummary), nameof(SimpleReportState) }) Notify(name);
    }
    private static IEnumerable<RecommendationRow> Recommendations(DiagnosticReport? report) => report?.Recommendations.Select(action =>
        new RecommendationRow(report.Components.FirstOrDefault(c => c.NextAction == action)?.Name ?? "Próximo passo", action.Reason, action.Label, action)) ?? [];
    private void UpdateLastDiagnostic(TestSession run)
    {
        if (run.TestId is not ("general" or "gaming-analysis") || _lastDiagnostic is { } previous && previous.StartedAt > run.StartedAt) return;
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
    }
}
