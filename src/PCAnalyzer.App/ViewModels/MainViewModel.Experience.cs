using System.Collections.ObjectModel;
using System.Windows;
using PCAnalyzer.App.Controls;
using PCAnalyzer.Core;

namespace PCAnalyzer.App.ViewModels;

public sealed record CategoryMeasurement(string Name, string Reading, string State);

public sealed partial class MainViewModel
{
    private IReadOnlyList<AuditSettingPresentation> _auditSettings = [];
    private readonly Dictionary<string, (string Search, string Filter, string Group, string Sort)> _categoryFilters = [];
    private readonly Dictionary<string, TestSession> _categoryTests = [];
    private string _auditSearch = "", _auditFilter = "Todas", _auditSubcategory = "Todos os grupos", _auditSort = "Prioridade";
    private RelativeSeries? _selectedCategorySeries;
    private DateTimeOffset? _settingsCapturedAt;
    private TestSession? _auditSettingsRun;
    public IReadOnlyList<string> AuditFilters { get; } = ["Todas", "Melhorias possíveis", "Vale testar", "Adequadas", "Não avaliadas"];
    public IReadOnlyList<string> AuditSortOptions { get; } = ["Prioridade", "Nome"];
    public ObservableCollection<string> AuditSubcategories { get; } = ["Todos os grupos"];
    public ObservableCollection<AuditSettingPresentation> FilteredAuditSettings { get; } = [];
    public ObservableCollection<AuditSettingPresentation> PriorityAuditSettings { get; } = [];
    public ObservableCollection<RecommendationRow> RecommendedTests { get; } = [];
    public string AuditSearch { get => _auditSearch; set { if (Set(ref _auditSearch, value)) FilterAuditSettings(); } }
    public string SelectedAuditFilter { get => _auditFilter; set { if (value is not null && AuditFilters.Contains(value) && Set(ref _auditFilter, value)) FilterAuditSettings(); } }
    public string SelectedAuditSubcategory { get => _auditSubcategory; set { if (value is not null && Set(ref _auditSubcategory, value)) FilterAuditSettings(); } }
    public string AuditSort { get => _auditSort; set { if (value is not null && AuditSortOptions.Contains(value) && Set(ref _auditSort, value)) FilterAuditSettings(); } }
    public bool HasAuditSettings => _auditSettings.Count > 0;
    public bool HasHomeResult => HasInlineResult || HasAuditSettings || HomeMeasurementConclusions.Count > 0;
    public string HomeAuditState => HasAuditSettings && _auditSettingsRun is { } run ? "Auditoria · " + TestLabels.Status(run.Status).ToLowerInvariant() :
        HomeMeasurementConclusions.Count > 0 ? "Medições para investigar" : SimpleReportState;
    public bool HasPriorityAuditSettings => PriorityAuditSettings.Count > 0;
    public string PriorityAuditSummary => HomeMeasurementConclusions.Count > 0 ? "Há medições com sinais para investigar. Confira o contexto antes de atribuir a causa a uma configuração." : !HasAuditSettings ? "Analise o PC para receber orientações baseadas nas leituras." :
        HasPriorityAuditSettings ? "Pontos que merecem investigação. Nenhuma mudança será aplicada." : "Nenhuma melhoria tem evidência suficiente nesta análise.";
    public string HomeAuditSummary => !HasAuditSettings ? HomeMeasurementConclusions.Count > 0 ? "Conclusões das últimas medições válidas, com data e contexto próprios." :
        _openedTest?.Status == TestStatus.Failed ? "A análise não pôde ser concluída. Consulte os detalhes da leitura." : SimpleReportSummary :
        "Configurações organizadas por categoria. Confira o valor atual, a orientação e as evidências de cada item.";
    public string AuditSettingsSummary => !HasAuditSettings ? "As configurações aparecerão após a análise do PC." :
        $"{FilteredAuditSettings.Count} configurações neste filtro · leitura de {_settingsCapturedAt?.LocalDateTime:dd/MM HH:mm}.";
    public IEnumerable<Section> AuditNavigationCategories => VisibleSections.Where(s => s.Tab == 10);
    public string CategoryTitle => PageTitle;
    public string CategoryDescription => PageDescription;
    public bool IsAuditCategory => SelectedSection?.Id is "windows" or "nvidia" or "network" or "devices" or "hardware" or "games";
    public bool IsWindowsCategory => SelectedSection?.Id == "windows";
    public bool IsNetworkCategory => SelectedSection?.Id == "network";
    public bool IsLatencyCategory => SelectedSection?.Id == "latency";
    public bool IsHardwareCategory => SelectedSection?.Id == "hardware";
    public bool IsGamesCategory => SelectedSection?.Id == "games";
    public string CompactHardwareSummary => string.Join(" · ", SystemSummary
        .Where(r => r.Name is "CPU" or "GPU" or "RAM" or "Sistema" or "Monitor")
        .Where(r => !r.Value.Contains("não disponíveis", StringComparison.OrdinalIgnoreCase))
        .Select(r => r.Value).Distinct().DefaultIfEmpty("Identificando o hardware disponível…"));
    public RelayCommand OpenAuditSettingCommand { get; private set; } = null!;
    public AsyncCommand ExportCategoryTestCommand { get; private set; } = null!;
    public AsyncCommand ExportAuditCommand { get; private set; } = null!;
    public RelayCommand OpenCategoryTestCommand { get; private set; } = null!;
    public RelayCommand OpenAuditReportCommand { get; private set; } = null!;
    public RelayCommand PrepareAuditDetailsCommand { get; private set; } = null!;

    private void InitializeExperienceCommands()
    {
        InvestigatePerformanceCommand = new RelayCommand(() => Navigate("latency"));
        ChooseMeasurementCommand = new RelayCommand(value => Navigate(value is "frames" ? "games" : value is "ping" ? "network" : value is "memory-access" ? "hardware" : "latency"));
        OpenAuditSettingCommand = new RelayCommand(value =>
        {
            if (value is not AuditSettingPresentation setting) return;
            var dialog = new AuditSettingDialog(setting, RequestAuditTest) { Owner = Application.Current.MainWindow };
            dialog.ShowDialog();
        });
        ExportCategoryTestCommand = Command(() => ExportRunAsync(CategoryTest), () => CategoryTest is not null);
        ExportAuditCommand = Command(() => ExportRunAsync(_auditSettingsRun), () => _auditSettingsRun is not null);
        OpenCategoryTestCommand = new RelayCommand(() =>
        {
            if (CategoryTest is not { } run) return;
            DisplayTestResult(run);
            Navigate("resultados");
        }, () => CategoryTest is not null);
        OpenAuditReportCommand = new RelayCommand(() =>
        {
            if (_auditSettingsRun is not { } run) return;
            DisplayTestResult(run); Navigate("resultados");
        }, () => _auditSettingsRun is not null);
        PrepareAuditDetailsCommand = new RelayCommand(() =>
        {
            if (_auditSettingsRun is { } run && run.Id != _openedTest?.Id) DisplayTestResult(run);
        });
    }

    private void RequestAuditTest(string id)
    {
        if (IsBusy) { Notification = "Aguarde ou cancele o teste em andamento."; return; }
        var command = id switch
        {
            "ping" => PingCommand, "memory-access" => MemoryAccessCommand, "disk-read" => DiskReadCommand,
            "dpc" => CaptureDpcCommand, "frames" => CaptureFramesCommand, _ => null
        };
        // Select the corresponding category before starting an explicitly requested test.
        var destination = id switch { "ping" => "network", "memory-access" or "disk-read" => "hardware", "dpc" => "latency", "frames" => "games", _ => null };
        if (destination is not null) Navigate(destination);
        if (id == "frames" && string.IsNullOrWhiteSpace(ProcessIdText))
        { Notification = "Escolha o processo e configure o PresentMon antes da captura."; return; }
        if (command?.CanExecute(null) == true) command.Execute(null);
    }

    private Task RunPingAsync()
    {
        if (!Confirm("Testar latência da rede", $"Enviar 20 pacotes ICMP para {PingHost.Trim()}. Mede ida e volta e variação; não mede a latência física dos comandos nem altera o adaptador.\n\nIniciar teste?"))
            return Task.CompletedTask;
        return RunTestAsync("ping", (progress, ct) => _backend.PingAsync(PingHost.Trim(), SelectedAdapter?.Id, progress, ct));
    }

    private void UpdateAuditSettings(PerformanceAuditReport report)
    {
        _auditSettings = AuditPresentationBuilder.Build(report);
        _settingsCapturedAt = report.CapturedAt;
        RefreshAdapterVisibility();
        RefreshHomePriorities();
        RefreshCategoryGroups(); FilterAuditSettings(); NotifyAuditExperience();
    }

    private static string TestDestination(string id) => id switch
    { "ping" => "network", "dpc" or "etl-import" => "latency", "frames" or "frames-import" or "gaming-analysis" => "games", _ => "hardware" };

    private void ResetAuditSettings()
    {
        _auditSettings = []; _settingsCapturedAt = null; _auditSettingsRun = null;
        PriorityAuditSettings.Clear(); RecommendedTests.Clear(); FilteredAuditSettings.Clear();
        RefreshCategoryGroups(); NotifyAuditExperience();
    }
    private void NotifyAuditExperience()
    {
        foreach (var name in new[] { nameof(HasAuditSettings), nameof(HasHomeResult), nameof(HomeAuditState), nameof(HasPriorityAuditSettings), nameof(PriorityAuditSummary), nameof(HomeAuditSummary), nameof(AuditSettingsSummary) }) Notify(name);
        ExportAuditCommand?.Refresh(); OpenAuditReportCommand?.Refresh();
        NotifyConsultation();
    }
    private void SaveCategoryFilters()
    {
        if (_section is { } section) _categoryFilters[section.Id] = (_auditSearch, _auditFilter, _auditSubcategory, _auditSort);
    }
    private void RefreshCategory()
    {
        var saved = _categoryFilters.GetValueOrDefault(SelectedSection?.Id ?? "", ("", SelectedSection?.Id == "ajustes" ? "Melhorias possíveis" : "Todas", "Todos os grupos", "Prioridade"));
        (_auditSearch, _auditFilter, _auditSubcategory, _auditSort) = saved;
        RefreshCategoryGroups(); FilterAuditSettings();
        foreach (var name in new[] { nameof(AuditSearch), nameof(SelectedAuditFilter), nameof(SelectedAuditSubcategory), nameof(AuditSort),
            nameof(CategoryTitle), nameof(CategoryDescription), nameof(IsAuditCategory), nameof(IsWindowsCategory), nameof(IsNetworkCategory),
            nameof(IsLatencyCategory), nameof(IsHardwareCategory), nameof(IsGamesCategory) }) Notify(name);
        RefreshCategoryResult();
    }
    private IEnumerable<AuditSettingPresentation> CategorySettings()
    {
        var category = SelectedSection?.Id;
        var rows = category is "dashboard" or "ajustes" or "avancado" or "resultados" ? _auditSettings :
            _auditSettings.Where(r => r.CategoryId == category || category == "nvidia" && r.Evidence.Any(a => a.Rule.Id.StartsWith("nvidia.", StringComparison.Ordinal)));
        if (category == "network")
        {
            if (!ShowAllNetworkAdapters) rows = rows.Where(r => !r.Evidence.All(a =>
                bool.TryParse(a.Observation.Facts.GetValueOrDefault("HardwareInterface"), out var hardware) && !hardware));
            if (SelectedAdapter is { } adapter)
                rows = rows.Where(r => r.Evidence.Any(a => !a.Observation.Facts.TryGetValue("InterfaceGuid", out var guid) ||
                    Guid.TryParse(guid, out var id) && id == adapter.Id));
        }
        return rows;
    }
    private void RefreshCategoryGroups()
    {
        var current = _auditSubcategory;
        Replace(AuditSubcategories, new[] { "Todos os grupos" }.Concat(CategorySettings().Select(r => r.Subcategory).Distinct().Order()));
        _auditSubcategory = AuditSubcategories.Contains(current) ? current : "Todos os grupos";
        Notify(nameof(SelectedAuditSubcategory));
    }
    private void FilterAuditSettings()
    {
        var rows = CategorySettings().Where(r => (string.IsNullOrWhiteSpace(AuditSearch) || r.Name.Contains(AuditSearch.Trim(), StringComparison.OrdinalIgnoreCase))
            && (_auditSubcategory == "Todos os grupos" || r.Subcategory == _auditSubcategory)
            && (_auditFilter switch
            {
                "Melhorias possíveis" => r.IsImprovement, "Vale testar" => r.IsComparison,
                "Adequadas" => r.IsAdequate, "Não avaliadas" => r.IsUnassessed, _ => true
            }));
        Replace(FilteredAuditSettings, _auditSort == "Nome" ? rows.OrderBy(r => r.Name) : rows.OrderByDescending(r => r.Priority).ThenBy(r => r.Name));
        Notify(nameof(AuditSettingsSummary));
    }

    private static bool TestBelongsTo(string id, string? category) => category switch
    {
        "network" => id == "ping",
        "devices" => id is "dpc" or "etl-import",
        "games" => id is "frames" or "frames-import" or "gaming-analysis",
        "hardware" => id is "cpu" or "ram" or "memory-access" or "disk-read",
        "latency" => id is "ping" or "dpc" or "etl-import" or "frames" or "frames-import" or "gaming-analysis" or "memory-access" or "cpu" or "ram" or "disk-read",
        _ => false
    };
    private TestSession? CategoryTest => _categoryTests.Values.Where(r => TestBelongsTo(r.TestId, SelectedSection?.Id)).OrderByDescending(r => r.StartedAt).FirstOrDefault();
    public Guid? CategoryTestId => CategoryTest?.Id;
    public bool HasCategoryTestResult => CategoryTest is not null;
    public string CategoryTestTitle => CategoryTest?.Name ?? "Nenhum teste realizado";
    public string CategoryResultState => CategoryTest is { } run ? TestLabels.Status(run.Status) : "Não medido";
    public string CategoryResultContext => CategoryTest is { } run ? $"Executado em {run.StartedAt.LocalDateTime:dd/MM HH:mm:ss} · " +
        (run.Result?.SelectionContext ?? "Contexto original disponível nos detalhes da captura") : "";
    public string CategoryTestSummary => CategoryTest is not { } run ? "Inicie um teste manual para medir o comportamento." :
        run.Status == TestStatus.Failed ? "Não foi possível concluir a leitura. Consulte os detalhes e repita quando a fonte estiver disponível." :
        run.Status == TestStatus.Cancelled ? "Teste cancelado. Somente as medições obtidas nesta execução foram preservadas." :
        run.Result?.Interpretation ?? run.Result?.Findings?.FirstOrDefault()?.Explanation ?? (run.TestId switch
        {
            "ping" => "Resposta do destino medido. Perdas ICMP não comprovam perda no jogo; compare sessões no mesmo destino.",
            "dpc" or "etl-import" => "Durações de execução no kernel. Picos isolados não medem input lag físico nem comprovam defeito.",
            "frames" or "frames-import" or "gaming-analysis" => "Quadros da captura selecionada. Compare a distribuição na mesma carga; FPS isolado não explica a causa de travadas.",
            "memory-access" or "ram" => "Acesso à memória do processo. CPU, caches e agendamento influenciam o resultado; não é latência DRAM isolada.",
            "disk-read" => "Leitura do arquivo selecionado. O cache do Windows pode influenciar a taxa; isso não comprova a saúde física do SSD.",
            _ => "Medição da carga escolhida, salva no histórico. Compare repetições sob condições equivalentes."
        });
    public string CategoryInterpretationStatus => CategoryTest?.Result?.InterpretationStatus ?? "Interpretação da janela";
    public string CategoryNextStep => CategoryTest?.Result?.NextStep ?? CategoryTest?.Result?.Findings?.FirstOrDefault()?.Recommendation ??
        "Compare repetições sob a mesma carga antes de atribuir uma diferença à configuração.";
    public IEnumerable<IndicatorRow> CategoryTestIndicators => (CategoryTest?.Result?.Indicators ?? []).Select(i => new IndicatorRow(i));
    public IEnumerable<string> CategoryTestLimitations => CategoryTest?.Result?.Limitations ?? [];
    public string CategoryTestDetails => string.Join(Environment.NewLine, new[] { CategoryTest?.Error }.Concat(CategoryTest?.Result?.Lines ?? []).Where(s => s is not null));
    public IEnumerable<RelativeSeries> CategoryResultSeries => CategoryTest?.Result?.Series ?? [];
    public RelativeSeries? SelectedCategorySeries { get => _selectedCategorySeries; set => Set(ref _selectedCategorySeries, value); }
    public bool CategoryHasGraph => CategoryResultSeries.Any(s => s.Points.Count > 0);
    public IEnumerable<CategoryMeasurement> CategoryMeasurements => new[]
    {
        Measurement("DPC", ["dpc", "etl-import"], i => i.Id == "dpc.maximum.us"),
        Measurement("ISR", ["dpc", "etl-import"], i => i.Id == "isr.maximum.us"),
        Measurement("Frametime p99", ["frames", "frames-import", "gaming-analysis"], i => i.Id.EndsWith(".p99.ms", StringComparison.Ordinal)),
        Measurement("RTT médio", ["ping"], i => i.Id == "network.ping.rtt.ms"),
        Measurement("Acesso à memória", ["memory-access"], i => i.Unit is "ns/acesso" or "ns/access" or "ns"),
        new CategoryMeasurement("Agendamento", "Não medido", "Requer captura específica; não estimado")
    };
    private CategoryMeasurement Measurement(string name, string[] tests, Func<ResultIndicator, bool> match)
    {
        var run = _categoryTests.Values.Where(r => tests.Contains(r.TestId)).OrderByDescending(r => r.StartedAt).FirstOrDefault();
        var values = run?.Result?.Indicators?.Where(match).ToArray() ?? [];
        // Do not silently choose one game stream or memory-buffer size as if it represented all measurements.
        var reading = values.Length == 1 && values[0].Value is not null ? new IndicatorRow(values[0]).Reading :
            values.Length > 1 ? "Ver medições por cenário" : "Não medido";
        return new(name, reading, run is null ? "Teste manual disponível" : TestLabels.Status(run.Status));
    }
    private void ClearCategoryResult(string id)
    { _categoryTests.Remove(id); RefreshCategoryResult(); }
    private void RememberCategoryResult(TestSession run)
    {
        if (!_categoryTests.TryGetValue(run.TestId, out var previous) || previous.StartedAt <= run.StartedAt) _categoryTests[run.TestId] = run;
        RememberValidMeasurement(run);
        RefreshCategoryResult();
    }
    private void RefreshCategoryResult()
    {
        SelectedCategorySeries = CategoryResultSeries.FirstOrDefault();
        foreach (var name in new[] { nameof(HasCategoryTestResult), nameof(CategoryTestId), nameof(CategoryResultContext), nameof(CategoryTestTitle), nameof(CategoryResultState), nameof(CategoryTestSummary), nameof(CategoryInterpretationStatus), nameof(CategoryNextStep),
            nameof(CategoryTestIndicators), nameof(CategoryTestLimitations), nameof(CategoryTestDetails), nameof(CategoryHasGraph), nameof(CategoryResultSeries), nameof(CategoryMeasurements) }) Notify(name);
        ExportCategoryTestCommand?.Refresh();
        OpenCategoryTestCommand?.Refresh();
    }
}
