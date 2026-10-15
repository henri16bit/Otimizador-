using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PCAnalyzer.App.Services;
using PCAnalyzer.Core;

namespace PCAnalyzer.App.ViewModels;

public sealed record Section(string Id, string Name, string Icon, int Tab, string Description);
public sealed record ComponentChoice(string Id, string Name, string? Component);
public sealed record MetricRow(MetricSample Sample)
{
    public string Key => Sample.Key;
    public MetricInfo Info => MetricCatalog.Resolve(Sample.Key, Sample.Unit);
    public string Name => MetricPresentation.Name(Sample);
    public string Component => Sample.Component;
    public string Value => MetricPresentation.Format(Sample).Value;
    public string Unit => MetricPresentation.Format(Sample).Unit;
    public bool IsAvailable => MetricPresentation.Available(Sample);
    public string Source => Sample.Source;
    public string State => MainViewModel.AvailabilityLabel(Sample.Status);
    public string Cadence => Sample.SamplingInterval.TotalSeconds > 0 ? $"{Sample.SamplingInterval.TotalSeconds:0.##} s" : "—";
    public string Time => Sample.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff");
    public string Details => $"Identificador: {Sample.Key}\n{Sample.Method}\n{Sample.Kind} · {Sample.Aggregation}\n{Sample.Limitation ?? "Sem limitação adicional reportada pela fonte."}";
}
public sealed record HardwareRow(string Component, string Name, string? Value, string Source, string State, string? Limitation);
public sealed record ProcessRow(ProcessSample Sample)
{
    public int Id => Sample.ProcessId;
    public string Name => Sample.Name;
    public string Cpu => Sample.CpuPercent is { } v ? $"{v:0.0} %" : "—";
    public string Memory => Sample.WorkingSetBytes is { } v ? $"{v / 1048576d:0.0} MiB" : "—";
    public string? Limitation => Sample.Limitation;
}
public sealed record CollectionPreferences(string Mode, double IntervalSeconds, bool OnboardingDismissed = false);

public sealed partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    private const int SessionSampleLimit = 30_000;
    private readonly IApplicationBackend _backend;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _sampleLock = new();
    private readonly List<MetricSample> _samples = [];
    private readonly List<string> _collectionChanges = [];
    private readonly Dictionary<string, MetricSample> _latest = new(StringComparer.Ordinal);
    private HashSet<string> _liveKeys = [];
    private readonly DispatcherTimer _uiTimer;
    private CancellationTokenSource? _operation;
    private Task? _collectorTask;
    private Task? _initializationTask;
    private TaskCompletionSource? _operationFinished;
    private TelemetryBatch? _pendingBatch;
    private HardwareInventory? _inventory;
    private DiagnosticSession? _loadedSession;
    private Guid _sessionId = Guid.NewGuid();
    private DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastAnalysis = DateTimeOffset.MinValue;
    private int _droppedSamples;
    private bool _closing, _isBusy;
    private volatile bool _minimized, _paused, _diagnosticCadence;
    private string _status = "Identificando o hardware do seu PC…";
    private string _operationText = "Escolha um teste em Investigar. Seu resultado aparecerá aqui quando terminar.";
    private string _operationTitle = "Nenhum teste iniciado";
    private string _mode = "Leve";
    private double _intervalSeconds = 5, _progress;
    private Section? _section;
    private ComponentChoice? _component;
    private MetricRow? _selectedMetric;
    private ProcessRow? _selectedProcess;
    private Finding? _selectedFinding;
    private SelectionItem? _selectedPowerPlan;
    private NetworkSettingView? _selectedNetworkSetting;
    private SelectionItem? _selectedNetworkValue;
    private RelativeSeries? _selectedRelativeSeries;
    private ChangeRecord? _selectedChange;
    private SessionSummary? _selectedSession, _beforeSession, _afterSession;
    private int _contentTab, _resultTab, _investigationTab;
    private IReadOnlyList<MetricSample> _chartSamples = [];
    private IReadOnlyList<MetricSample> _overviewChartSamples = [];
    private string _processFilter = "";
    private string _metricFilter = "", _sessionLabel = "Diagnóstico local", _workload = "Desktop em repouso / informar condição", _notes = "", _pingHost = "gateway", _processIdText = "";
    private bool _anonymize = true;

    public MainViewModel(IApplicationBackend backend)
    {
        _backend = backend;
        _execution = new TestExecutionService(backend, backend.LogError);
        _execution.Changed += OnExecutionChanged;
        Sections = [new("dashboard", "Visão geral", "\uE80F", 0, "Acompanhe seu PC e escolha o que investigar."),
            new("componentes", "Componentes", "\uE950", 1, "Escolha um componente para ver suas leituras e seu histórico."),
            new("processos", "Processos", "\uE9D9", 2, "Encontre os aplicativos que estão usando CPU e memória."),
            new("investigar", "Investigar", "\uE721", 3, "Escolha um teste de acordo com o problema observado."),
            new("resultados", "Resultados", "\uE9D2", 4, "Consulte as observações e o resultado do último teste."),
            new("historico", "Histórico", "\uE81C", 5, "Salve, abra e compare sessões de diagnóstico."),
            new("ajustes", "Ajustes", "\uE9E9", 6, "Revise uma alteração por vez e restaure quando necessário."),
            new("config", "Configurações", "\uE713", 7, "Defina a frequência da coleta e consulte as informações do aplicativo.")];
        ComponentChoices = [new("cpu", "Processador (CPU)", "CPU"), new("gpu", "Placa de vídeo (GPU)", "GPU"),
            new("ram", "Memória (RAM)", "RAM"), new("nvme", "Discos e NVMe", "Disco"), new("rede", "Rede", "Rede"), new("todos", "Todos os componentes", null)];
        _section = Sections[0];
        _component = ComponentChoices[0];
        NavigateCommand = new RelayCommand(parameter => Navigate(parameter?.ToString() ?? "dashboard"));
        OpenComponentCommand = new RelayCommand(parameter =>
        {
            SelectedComponent = ComponentChoices.FirstOrDefault(c => c.Id == parameter?.ToString()) ?? ComponentChoices[0];
            Navigate("componentes");
        });
        OpenInvestigationCommand = new RelayCommand(parameter =>
        {
            InvestigationTab = parameter?.ToString() switch { "jogos" => 1, "drivers" => 2, "testes" => 3, _ => 0 };
            Navigate("investigar");
        });
        SaveCommand = Command(SaveCurrentSessionAsync, () => !IsHistoryView);
        RefreshHistoryCommand = Command(RefreshHistoryAsync);
        LoadSessionCommand = Command(LoadSessionAsync, () => SelectedSession is not null);
        CompareCommand = Command(CompareAsync, () => BeforeSession is not null && AfterSession is not null && BeforeSession.Id != AfterSession.Id);
        ExportJsonCommand = Command(() => ExportAsync("json"));
        ExportCsvCommand = Command(() => ExportAsync("csv"));
        ExportHtmlCommand = Command(() => ExportAsync("html"));
        PingCommand = Command(() => RunTestAsync("ping", (progress, ct) => _backend.PingAsync(PingHost.Trim(), SelectedAdapter?.Id, progress, ct)), () => !string.IsNullOrWhiteSpace(PingHost));
        ImportFramesCommand = Command(ImportFramesAsync);
        CaptureFramesCommand = Command(CaptureFramesAsync, () => int.TryParse(ProcessIdText, out var pid) && pid > 0);
        CaptureDpcCommand = Command(CaptureDpcAsync);
        ImportEtlCommand = Command(ImportEtlAsync);
        RefreshOptimizationCommand = Command(RefreshOptimizationAsync);
        PowerPreviewCommand = Command(PowerPreviewAsync, () => SelectedPowerPlan is not null);
        NetworkPreviewCommand = Command(NetworkPreviewAsync, () => SelectedNetworkSetting is not null && SelectedNetworkValue is not null);
        RestoreCommand = Command(RestoreAsync, () => SelectedChange is { State: not ChangeState.Restored });
        CpuSingleCommand = Command(() => LabAsync("cpu"));
        MemoryCommand = Command(() => LabAsync("ram"));
        DiskReadCommand = Command(() => LabAsync("disk-read"));
        SavePreferencesCommand = Command(SavePreferencesAsync);
        CancelCommand = new RelayCommand(() => { _execution.Cancel(); _operation?.Cancel(); }, () => IsBusy);
        PauseCommand = new RelayCommand(() => { _paused = !_paused; Notify(nameof(PauseCaption)); Notify(nameof(CollectionStateLabel)); Status = _paused ? "Coleta pausada pelo usuário." : "Coleta passiva retomada."; }, () => _inventory is not null && _loadedSession is null);
        LiveCommand = new RelayCommand(() => { _loadedSession = null; Replace(Capabilities, _inventory?.Capabilities ?? []); NotifyViewState(); RefreshDisplay(); AnalyzeDisplay(); Status = "Visualização ao vivo. As leituras seguem o perfil de coleta selecionado."; }, () => _loadedSession is not null);
        NewSessionCommand = Command(NewSessionAsync, () => !IsHistoryView);
        _uiTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => FlushUi(), Dispatcher.CurrentDispatcher);
        _uiTimer.Stop();
        Replace(OverviewCards, MetricPresentation.Overview([], null));
        InitializeDiagnosticCommands();
        InitializeNetworkCommands();
    }

    public IReadOnlyList<Section> Sections { get; }
    public IReadOnlyList<ComponentChoice> ComponentChoices { get; }
    public ObservableCollection<OverviewCard> OverviewCards { get; } = [];
    public ObservableCollection<MetricRow> Metrics { get; } = [];
    public ObservableCollection<HardwareRow> Hardware { get; } = [];
    public ObservableCollection<ProcessRow> Processes { get; } = [];
    public ObservableCollection<Capability> Capabilities { get; } = [];
    public ObservableCollection<Finding> Findings { get; } = [];
    public ObservableCollection<SessionSummary> Sessions { get; } = [];
    public ObservableCollection<SelectionItem> PowerPlans { get; } = [];
    public ObservableCollection<NetworkSettingView> NetworkSettings { get; } = [];
    public ObservableCollection<ChangeRecord> Changes { get; } = [];
    public ObservableCollection<RelativeSeries> ResultSeries { get; } = [];
    public RelativeSeries? SelectedRelativeSeries { get => _selectedRelativeSeries; set => Set(ref _selectedRelativeSeries, value); }
    public bool HasRelativeSeries => ResultSeries.Count > 0;
    public IReadOnlyList<string> CollectionModes { get; } = ["Leve", "Avançado"];
    public IReadOnlyList<double> Intervals { get; } = [1, 2, 5, 10, 15, 30, 60];
    public IReadOnlyList<MetricSample> ChartSamples { get => _chartSamples; private set => Set(ref _chartSamples, value); }
    public IReadOnlyList<MetricSample> OverviewChartSamples { get => _overviewChartSamples; private set => Set(ref _overviewChartSamples, value); }
    public string PageTitle => SelectedSection?.Name ?? "Visão geral";
    public string PageDescription => SelectedSection?.Description ?? "";
    public bool IsHistoryView => _loadedSession is not null;
    public bool IsLiveView => _loadedSession is null;
    public string CollectionStateLabel => IsHistoryView ? "Sessão salva" : _inventory is null ? "Detectando hardware" : _paused ? "Coleta pausada" : "Ao vivo";
    public string MachineSummary => (_loadedSession?.Inventory ?? _inventory)?.Components.FirstOrDefault(c => c.Category == "CPU")?.Name ?? "Identificando os componentes do seu PC…";
    public string ProcessFilter { get => _processFilter; set { if (Set(ref _processFilter, value)) RefreshVisibleProcesses(); } }
    public ObservableCollection<ProcessRow> VisibleProcesses { get; } = [];
    private void RefreshVisibleProcesses() => Replace(VisibleProcesses, Processes.Where(p => string.IsNullOrWhiteSpace(ProcessFilter)
        || p.Name.Contains(ProcessFilter, StringComparison.OrdinalIgnoreCase) || p.Id.ToString(CultureInfo.InvariantCulture).Contains(ProcessFilter)));
    public string FindingsSummary => Findings.Any(f => f.Category is FindingCategory.ObservedProblem or FindingCategory.PossibleProblem)
        ? "Há observações para investigar" : Findings.Any(f => f.Category == FindingCategory.NoAnomaly)
        ? "A triagem não indicou uma alteração" : "Aguardando contexto para a análise";
    public IEnumerable<Finding> OverviewFindings => (_loadedSession is not null ? Findings :
        (_displayedExecution?.Result?.Findings ?? []).Concat(Findings)).DistinctBy(f => f.Id)
        .OrderByDescending(f => f.Severity).Take(5);
    public string SelectedFindingCategory => SelectedFinding?.Category switch
    {
        FindingCategory.ObservedProblem => "Problema observado", FindingCategory.PossibleProblem => "Possível problema",
        FindingCategory.OptimizationOpportunity => "Oportunidade de ajuste", FindingCategory.NoAnomaly => "Sem anomalia na triagem",
        FindingCategory.InsufficientData => "Dados insuficientes", _ => "Selecione uma observação"
    };
    public string SelectedFindingConfidence => SelectedFinding?.Confidence switch { Confidence.High => "Alta", Confidence.Medium => "Média", Confidence.Low => "Baixa", _ => "—" };
    public string DataDirectory => _backend.DataDirectory;
    public bool IsBusy { get => _isBusy; private set { Set(ref _isBusy, value); RefreshCommands(); CancelCommand.Refresh(); } }
    public bool IsMinimized { get => _minimized; set => _minimized = value; }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string OperationText { get => _operationText; private set => Set(ref _operationText, value); }
    public string OperationTitle { get => _operationTitle; private set => Set(ref _operationTitle, value); }
    public string ViewContext => _loadedSession is { } saved ? $"HISTÓRICO: {saved.Label} · {saved.StartedAt.LocalDateTime:g}" : $"AO VIVO · {_startedAt.LocalDateTime:g} · {_mode} · intervalo alvo {IntervalSeconds:0.##} s";
    public string BufferInfo { get { lock (_sampleLock) return $"{_samples.Count:N0} / {SessionSampleLimit:N0} amostras · {_droppedSamples:N0} descartadas · gráfico: últimas 600"; } }
    public string PauseCaption => _paused ? "Retomar coleta" : "Pausar coleta";
    public string Mode { get => _mode; set { if (Set(ref _mode, value)) { RecordCollectionChange($"Perfil alterado para {value}"); IntervalSeconds = value == "Avançado" ? 1 : 5; Notify(nameof(ViewContext)); } } }
    public double IntervalSeconds { get => _intervalSeconds; set { if (value is >= 1 and <= 60 && Set(ref _intervalSeconds, value)) { RecordCollectionChange($"Intervalo alvo alterado para {value:0.##} s"); Notify(nameof(ViewContext)); } } }
    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string MetricFilter { get => _metricFilter; set { if (Set(ref _metricFilter, value)) RefreshDisplay(); } }
    public string SessionLabel { get => _sessionLabel; set => Set(ref _sessionLabel, value); }
    public string Workload { get => _workload; set => Set(ref _workload, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public string PingHost { get => _pingHost; set { Set(ref _pingHost, value); PingCommand.Refresh(); } }
    public bool Anonymize { get => _anonymize; set => Set(ref _anonymize, value); }
    public int ContentTab { get => _contentTab; private set => Set(ref _contentTab, value); }
    public int ResultTab { get => _resultTab; set => Set(ref _resultTab, value); }
    public int InvestigationTab { get => _investigationTab; set => Set(ref _investigationTab, value); }
    public Section? SelectedSection { get => _section; set { if (value is not null && Set(ref _section, value)) { ContentTab = value.Tab; Notify(nameof(PageTitle)); Notify(nameof(PageDescription)); } } }
    public ComponentChoice? SelectedComponent { get => _component; set { if (Set(ref _component, value)) { MetricFilter = ""; RefreshDisplay(); Notify(nameof(IsStorageComponent)); } } }
    public MetricRow? SelectedMetric { get => _selectedMetric; set { if (Set(ref _selectedMetric, value)) RefreshChart(); } }
    public ProcessRow? SelectedProcess { get => _selectedProcess; set { if (Set(ref _selectedProcess, value)) ProcessIdText = value?.Id.ToString(CultureInfo.InvariantCulture) ?? ""; } }
    public string ProcessIdText
    {
        get => _processIdText;
        set
        {
            if (Set(ref _processIdText, value) && _selectedProcess is not null && value != _selectedProcess.Id.ToString(CultureInfo.InvariantCulture))
            {
                // A manually entered PID is a separate explicit selection.
                _selectedProcess = null; Notify(nameof(SelectedProcess));
            }
            CaptureFramesCommand.Refresh();
        }
    }
    public Finding? SelectedFinding { get => _selectedFinding; set { if (Set(ref _selectedFinding, value)) { Notify(nameof(SelectedFindingCategory)); Notify(nameof(SelectedFindingConfidence)); } } }
    public SelectionItem? SelectedPowerPlan { get => _selectedPowerPlan; set { Set(ref _selectedPowerPlan, value); PowerPreviewCommand.Refresh(); } }
    public NetworkSettingView? SelectedNetworkSetting { get => _selectedNetworkSetting; set { Set(ref _selectedNetworkSetting, value); SelectedNetworkValue = null; Notify(nameof(NetworkValues)); Notify(nameof(NetworkSettingState)); } }
    public IReadOnlyList<SelectionItem> NetworkValues => SelectedNetworkSetting?.ValidValues ?? [];
    public SelectionItem? SelectedNetworkValue { get => _selectedNetworkValue; set { Set(ref _selectedNetworkValue, value); NetworkPreviewCommand.Refresh(); } }
    public ChangeRecord? SelectedChange { get => _selectedChange; set { Set(ref _selectedChange, value); RestoreCommand.Refresh(); } }
    public SessionSummary? SelectedSession { get => _selectedSession; set { Set(ref _selectedSession, value); LoadSessionCommand.Refresh(); } }
    public SessionSummary? BeforeSession { get => _beforeSession; set { Set(ref _beforeSession, value); CompareCommand.Refresh(); } }
    public SessionSummary? AfterSession { get => _afterSession; set { Set(ref _afterSession, value); CompareCommand.Refresh(); } }
    public AsyncCommand SaveCommand { get; }
    public AsyncCommand NewSessionCommand { get; }
    public AsyncCommand RefreshHistoryCommand { get; }
    public AsyncCommand LoadSessionCommand { get; }
    public AsyncCommand CompareCommand { get; }
    public AsyncCommand ExportJsonCommand { get; }
    public AsyncCommand ExportCsvCommand { get; }
    public AsyncCommand ExportHtmlCommand { get; }
    public AsyncCommand PingCommand { get; }
    public AsyncCommand ImportFramesCommand { get; }
    public AsyncCommand CaptureFramesCommand { get; }
    public AsyncCommand CaptureDpcCommand { get; }
    public AsyncCommand ImportEtlCommand { get; }
    public AsyncCommand RefreshOptimizationCommand { get; }
    public AsyncCommand PowerPreviewCommand { get; }
    public AsyncCommand NetworkPreviewCommand { get; }
    public AsyncCommand RestoreCommand { get; }
    public AsyncCommand CpuSingleCommand { get; }
    public AsyncCommand MemoryCommand { get; }
    public AsyncCommand DiskReadCommand { get; }
    public AsyncCommand SavePreferencesCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand PauseCommand { get; }
    public RelayCommand LiveCommand { get; }
    public RelayCommand NavigateCommand { get; }
    public RelayCommand OpenComponentCommand { get; }
    public RelayCommand OpenInvestigationCommand { get; }
    private CancellationToken OperationToken => _operation?.Token ?? _lifetime.Token;

    private void Navigate(string id)
    {
        if (id is "fontes" or "observacoes") { ResultTab = id == "fontes" ? 2 : 0; id = "resultados"; }
        SelectedSection = Sections.FirstOrDefault(s => s.Id == id) ?? Sections[0];
    }
    private void NotifyViewState()
    {
        Notify(nameof(ViewContext)); Notify(nameof(CollectionStateLabel)); Notify(nameof(IsHistoryView)); Notify(nameof(IsLiveView)); Notify(nameof(MachineSummary));
        PauseCommand.Refresh(); LiveCommand.Refresh();
        RefreshCommands();
    }

    private AsyncCommand Command(Func<Task> action, Func<bool>? canExecute = null) => new(() => RunOperationAsync(action), () => !IsBusy && _inventory is not null && (canExecute?.Invoke() ?? true));
    private void RefreshCommands()
    {
        foreach (var property in GetType().GetProperties().Where(p => p.PropertyType == typeof(AsyncCommand)))
            ((AsyncCommand?)property.GetValue(this))?.Refresh();
    }

    public Task InitializeAsync() => _initializationTask ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        try
        {
            await LoadPreferencesAsync();
            _inventory = await _backend.InitializeAsync(_lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            Replace(Capabilities, _inventory.Capabilities);
            RefreshDisplay(); AnalyzeDisplay();
            Status = "Coleta ao vivo ativa. Escolha um componente ou um teste para investigar.";
            NotifyViewState(); RefreshCommands(); _uiTimer.Start(); _collectorTask = Task.Run(CollectLoopAsync);
            try { await RefreshHistoryAsync(); } catch (Exception ex) { RecordFailure("Ler histórico", ex); }
            try { await _execution.RecoverInterruptedAsync(_lifetime.Token); await RefreshTestsAsync(); } catch (Exception ex) { RecordFailure("Ler testes salvos", ex); }
        }
        catch (OperationCanceledException) when (_closing) { }
        catch (Exception ex) { RecordFailure("Inicializar", ex); }
    }

    private async Task CollectLoopAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            var cadence = TimeSpan.FromSeconds(_diagnosticCadence ? 1 : _minimized ? Math.Max(15, IntervalSeconds) : IntervalSeconds);
            if (!_paused)
            {
                try
                {
                    var batch = await _backend.CollectAsync(cadence, _lifetime.Token);
                    lock (_sampleLock)
                    {
                        _samples.AddRange(batch.Metrics);
                        TrimSamples();
                        _pendingBatch = batch;
                    }
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _backend.LogError("Coletar telemetria", ex);
                    await Application.Current.Dispatcher.InvokeAsync(() => Status = $"Falha de coleta: {ex.Message}. Nova tentativa no próximo intervalo.");
                }
            }
            try { await Task.Delay(cadence, _lifetime.Token); }
            catch (OperationCanceledException) { break; }
        }
    }
    private void TrimSamples()
    {
        if (_samples.Count <= SessionSampleLimit) return;
        var remove = _samples.Count - SessionSampleLimit;
        _samples.RemoveRange(0, remove); _droppedSamples += remove;
    }
    private void FlushUi()
    {
        if (_closing || _minimized) return;
        RefreshExecutionDisplay();
        TelemetryBatch? batch;
        lock (_sampleLock) { batch = _pendingBatch; _pendingBatch = null; }
        if (batch is null) return;
        var nextKeys = batch.Metrics.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in _liveKeys.Except(nextKeys)) _latest.Remove(key);
        _liveKeys = nextKeys;
        foreach (var sample in batch.Metrics) _latest[sample.Key] = sample;
        if (_loadedSession is null)
        {
            var selectedPid = SelectedProcess?.Id; var selectedName = SelectedProcess?.Name;
            Replace(Processes, batch.Processes.Select(p => new ProcessRow(p)));
            RefreshVisibleProcesses();
            if (selectedPid.HasValue) SelectedProcess = VisibleProcesses.FirstOrDefault(p => p.Id == selectedPid && p.Name == selectedName);
            RefreshDisplay();
            if (DateTimeOffset.UtcNow - _lastAnalysis > TimeSpan.FromSeconds(15)) AnalyzeDisplay();
        }
        Notify(nameof(BufferInfo));
        if (!_paused && !IsBusy) Status = $"Última coleta {batch.Timestamp.LocalDateTime:HH:mm:ss} · {batch.Metrics.Count} métricas · {batch.Processes.Count} processos";
    }
    private bool MatchesComponent(string component)
    {
        var selected = SelectedComponent?.Component;
        return selected is null || component.Contains(selected, StringComparison.OrdinalIgnoreCase)
            || selected == "Disco" && (component.Contains("NVMe", StringComparison.OrdinalIgnoreCase) || component.Contains("Storage", StringComparison.OrdinalIgnoreCase))
            || selected == "Disco" && component.Contains("Armazenamento", StringComparison.OrdinalIgnoreCase)
            || selected == "RAM" && component.Contains("Mem", StringComparison.OrdinalIgnoreCase);
    }
    private bool MatchesMetric(MetricSample sample)
    {
        if (IsStorageComponent && SelectedDisk?.Id is { Length: > 0 } diskId && !sample.Key.StartsWith(diskId + ".", StringComparison.Ordinal)) return false;
        if (MatchesComponent(sample.Component)) return true;
        var key = sample.Key.ToLowerInvariant();
        return SelectedComponent?.Id switch
        {
            "gpu" => key.StartsWith("gpu."),
            "cpu" => key.StartsWith("cpu."),
            "ram" => key.StartsWith("memory.") || key.StartsWith("ram."),
            "nvme" => key.StartsWith("disk.") || key.StartsWith("storage."),
            "rede" => key.StartsWith("network.") || key.StartsWith("net."),
            "dpc" => key.Contains("dpc") || key.Contains("isr") || key.StartsWith("cpu.interrupt"),
            "jogos" => key.StartsWith("gaming.") || key.StartsWith("game.") || key.StartsWith("frames.") || key.StartsWith("presentmon."),
            _ => false
        };
    }
    private void RefreshDisplay()
    {
        var previousKey = SelectedMetric?.Key;
        var source = _loadedSession?.Samples.GroupBy(s => s.Key).Select(g => g.Last()).ToArray() ?? _latest.Values.ToArray();
        Replace(Metrics, source.Where(s => MatchesMetric(s) && (string.IsNullOrWhiteSpace(MetricFilter) || s.Key.Contains(MetricFilter, StringComparison.OrdinalIgnoreCase) || MetricPresentation.Name(s).Contains(MetricFilter, StringComparison.OrdinalIgnoreCase) || s.Source.Contains(MetricFilter, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(s => s.Component).ThenBy(s => s.Key).Select(s => new MetricRow(s)));
        var preferred = SelectedComponent?.Id switch { "ram" => "memory.available.bytes", "gpu" => ".utilization.percent", "nvme" => ".read.bytes_per_second", "rede" => ".receive.bytes_per_second", _ => "cpu.total.percent" };
        SelectedMetric = Metrics.FirstOrDefault(m => m.Key == previousKey)
            ?? Metrics.FirstOrDefault(m => m.Key == preferred || preferred.StartsWith('.') && m.Key.EndsWith(preferred, StringComparison.Ordinal))
            ?? Metrics.FirstOrDefault(m => m.IsAvailable) ?? Metrics.FirstOrDefault();
        var inventory = _loadedSession?.Inventory ?? _inventory;
        Replace(Hardware, inventory?.Components.Where(c => MatchesComponent(c.Category) && (!IsStorageComponent || string.IsNullOrEmpty(SelectedDisk?.Id) || c.Id == SelectedDisk.Id)).SelectMany(c => c.Facts.Select(f => new HardwareRow(c.Name, f.Name, f.Value ?? "—", f.Source, AvailabilityLabel(f.Status), f.Limitation))) ?? []);
        Replace(OverviewCards, MetricPresentation.Overview(source, inventory));
        UpdateSystemSummary(inventory, source);
        if (_loadedSession is { } historical) OverviewChartSamples = historical.Samples.Where(s => s.Key == "cpu.total.percent").TakeLast(600).ToArray();
        else lock (_sampleLock) OverviewChartSamples = _samples.Where(s => s.Key == "cpu.total.percent").TakeLast(600).ToArray();
        RefreshChart();
    }
    private void RefreshChart()
    {
        if (SelectedMetric is null) { ChartSamples = []; return; }
        var selected = SelectedMetric.Key;
        MetricSample[] series;
        if (_loadedSession is { } saved) series = saved.Samples.Where(s => s.Key == selected).OrderBy(s => s.Timestamp).ToArray();
        else lock (_sampleLock) series = _samples.Where(s => s.Key == selected).OrderBy(s => s.Timestamp).ToArray();
        var end = series.LastOrDefault()?.Timestamp ?? DateTimeOffset.UtcNow;
        ChartSamples = series.Where(s => s.Timestamp >= end.AddMinutes(-ChartWindowMinutes)).TakeLast(600).ToArray();
        Notify(nameof(ChartContext));
    }
    private void AnalyzeDisplay()
    {
        var inventory = _loadedSession?.Inventory ?? _inventory;
        if (inventory is null) return;
        IReadOnlyList<Finding> findings;
        if (_loadedSession is { } saved) findings = saved.Findings;
        else { MetricSample[] samples; lock (_sampleLock) samples = _samples.ToArray(); findings = _backend.Analyze(inventory, samples); }
        var previous = SelectedFinding?.Id;
        Replace(Findings, findings);
        SelectedFinding = Findings.FirstOrDefault(f => f.Id == previous) ?? Findings.FirstOrDefault();
        Notify(nameof(FindingsSummary)); Notify(nameof(OverviewFindings));
        _lastAnalysis = DateTimeOffset.UtcNow;
    }
    private DiagnosticSession Snapshot()
    {
        if (_inventory is null) throw new InvalidOperationException("Descoberta ainda não concluída.");
        MetricSample[] samples; string[] collectionChanges;
        lock (_sampleLock) { samples = _samples.ToArray(); collectionChanges = _collectionChanges.ToArray(); }
        var note = Notes + (_droppedSamples > 0 ? $"\nBuffer limitado: {_droppedSamples} amostras antigas descartadas; este relatório contém somente a janela retida." : "")
            + (collectionChanges.Length > 0 ? "\nMudanças durante a sessão:\n" + string.Join("\n", collectionChanges) : "");
        return new(_sessionId, string.IsNullOrWhiteSpace(SessionLabel) ? "Diagnóstico local" : SessionLabel.Trim(), _startedAt, DateTimeOffset.UtcNow,
            new(Workload, Mode, IntervalSeconds, note), _inventory, samples, _backend.Analyze(_inventory, samples));
    }
    private void RecordCollectionChange(string detail)
    {
        if (_inventory is null) return;
        lock (_sampleLock)
        {
            if (_collectionChanges.Count == 100) _collectionChanges.RemoveAt(0);
            _collectionChanges.Add($"{DateTimeOffset.UtcNow:O}: {detail}. A comparação deve considerar os diferentes intervalos.");
        }
    }
    private async Task RunOperationAsync(Func<Task> action)
    {
        if (_closing || IsBusy) return;
        IsBusy = true; Progress = 0;
        _operationFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        try { await action(); }
        catch (OperationCanceledException) { Status = "Operação cancelada. A coleta passiva continua."; }
        catch (Exception ex) { RecordFailure("Operação manual", ex); }
        finally { _operation.Dispose(); _operation = null; IsBusy = false; _operationFinished.TrySetResult(); }
    }
    private void RecordFailure(string operation, Exception ex)
    {
        _backend.LogError(operation, ex); Status = $"{operation}: {ex.Message}";
        OperationTitle = $"Falha · {operation}";
        Notification = Status;
        Replace(ResultSeries, []); SelectedRelativeSeries = null; Notify(nameof(HasRelativeSeries));
        OperationText = $"{ex.Message}\n\nA indisponibilidade não é evidência de problema de hardware. Consulte o log local em {DataDirectory}.";
    }
    private async Task SaveCurrentSessionAsync()
    {
        var session = Snapshot(); await _backend.SaveSessionAsync(session, OperationToken); await RefreshHistoryAsync();
        Status = $"Sessão salva localmente: {session.Samples.Count:N0} amostras.";
    }
    private async Task NewSessionAsync()
    {
        await SaveCurrentSessionAsync();
        lock (_sampleLock) { _samples.Clear(); _droppedSamples = 0; _pendingBatch = null; _collectionChanges.Clear(); }
        _sessionId = Guid.NewGuid(); _startedAt = DateTimeOffset.UtcNow; _loadedSession = null; _latest.Clear();
        NotifyViewState(); Notify(nameof(BufferInfo)); RefreshDisplay(); AnalyzeDisplay(); Status = "Nova janela de diagnóstico; sessão anterior salva.";
    }
    private async Task RefreshHistoryAsync() => Replace(Sessions, await _backend.ListSessionsAsync(OperationToken));
    private async Task LoadSessionAsync()
    {
        if (SelectedSession is null) return;
        _loadedSession = await _backend.LoadSessionAsync(SelectedSession.Id, OperationToken) ?? throw new InvalidOperationException("Sessão não encontrada.");
        Processes.Clear();
        RefreshVisibleProcesses();
        Replace(Capabilities, _loadedSession.Inventory.Capabilities);
        NotifyViewState(); Navigate("dashboard"); RefreshDisplay(); AnalyzeDisplay();
        Status = "Visualização de sessão salva. A coleta ao vivo segue em segundo plano; use Voltar ao vivo.";
    }
    private async Task CompareAsync()
    {
        _openedTest = null; Replace(ResultIndicators, []); Replace(TestFindings, []); Replace(ResultLimitations, []);
        Notify(nameof(CanExportTest)); ExportTestCommand.Refresh();
        await ShowResultAsync(_backend.CompareAsync(BeforeSession!.Id, AfterSession!.Id, OperationToken));
        ResultTab = 1; Navigate("resultados");
    }
    private async Task ExportAsync(string format)
    {
        var dialog = new SaveFileDialog { Filter = $"Relatório {format.ToUpperInvariant()}|*.{format}", DefaultExt = format, FileName = $"PCAnalyzer-{DateTime.Now:yyyyMMdd-HHmmss}.{format}" };
        if (dialog.ShowDialog() != true) return;
        await _backend.ExportAsync(_loadedSession ?? Snapshot(), dialog.FileName, format, Anonymize, OperationToken);
        Status = $"Relatório {format.ToUpperInvariant()} exportado ({(Anonymize ? "anonimização habilitada" : "inclui identificadores")}).";
    }
    private async Task ShowResultAsync(Task<TechnicalResult> task)
    {
        var result = await task;
        OperationTitle = result.Title; OperationText = string.Join(Environment.NewLine, result.Lines);
        Replace(ResultSeries, result.Series ?? []);
        SelectedRelativeSeries = ResultSeries.FirstOrDefault();
        Notify(nameof(HasRelativeSeries));
        Notification = result.Title;
        if (result.Metrics is { Count: > 0 } metrics)
        {
            if (_loadedSession is not null) { _loadedSession = null; Replace(Capabilities, _inventory?.Capabilities ?? []); NotifyViewState(); }
            lock (_sampleLock) { _samples.AddRange(metrics); TrimSamples(); }
            foreach (var metric in metrics) _latest[metric.Key] = metric;
            RefreshDisplay(); AnalyzeDisplay(); Notify(nameof(BufferInfo));
        }
        Status = result.Title;
    }
    private async Task ImportFramesAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Captura PresentMon CSV|*.csv", CheckFileExists = true };
        if (dialog.ShowDialog() == true)
        {
            var options = ParseFrameImportOptions();
            await RunTestAsync("frames-import", (_, ct) => _backend.ImportFramesAsync(dialog.FileName, options, ct));
        }
    }
    private async Task CaptureFramesAsync()
    {
        if (!int.TryParse(ProcessIdText, out var pid) || pid <= 0) throw new InvalidOperationException("Selecione um processo ou informe um PID positivo.");
        var dialog = new OpenFileDialog { Filter = "PresentMon oficial|*.exe", Title = "Selecionar PresentMon Console obtido do projeto oficial", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        if (!Confirm("GameSession · captura manual de 30 s", $"Executável selecionado:\n{dialog.FileName}\nPID alvo: {pid}\n\nSerá executada uma captura externa limitada a 30 s, sem overlay ou injeção de código. A compatibilidade com Vanguard não foi validada nesta máquina e não é garantida. Use importação de CSV se preferir analisar uma captura existente. A operação pode exigir permissões adicionais e acrescentar overhead.\n\nIniciar captura agora?")) return;
        await RunTestAsync("frames", (progress, ct) => { progress.Report(new("Capturando frames · até 30 s; aguardando o PresentMon")); return _backend.CaptureFramesAsync(dialog.FileName, pid, ct); });
    }
    private async Task ImportEtlAsync()
    {
        var dialog = new OpenFileDialog { Filter = "Eventos ETW|*.etl", CheckFileExists = true };
        if (dialog.ShowDialog() == true) await RunTestAsync("etl-import", (_, ct) => _backend.ImportEtlAsync(dialog.FileName, ct));
    }
    private async Task CaptureDpcAsync()
    {
        if (!Confirm("Captura DPC / ISR de 30 segundos", "Será iniciada uma sessão ETW de kernel limitada a 30 s. A operação solicita elevação pelo Windows e pode adicionar overhead. Reproduza a condição que deseja investigar. O cancelamento encerra a sessão criada pelo aplicativo.\n\nIniciar captura agora?")) return;
        await RunTestAsync("dpc", (progress, ct) => { progress.Report(new("Aguardando UAC / captura ETW de até 30 s")); return _backend.CaptureDpcAsync(ct); });
    }
    private async Task RefreshOptimizationAsync()
    {
        Replace(PowerPlans, await _backend.ListPowerPlansAsync(OperationToken));
        await DiscoverNetworkAsync();
        if (SelectedAdapter is not null) await InspectNetworkAsync();
        Replace(Changes, await _backend.ListChangesAsync(OperationToken));
        Status = "Planos e propriedades suportadas lidos; nenhuma alteração aplicada.";
    }
    private async Task PowerPreviewAsync()
    {
        if (SelectedPowerPlan is not { } plan) return;
        var preview = await _backend.PreviewPowerPlanAsync(plan.Id, OperationToken);
        OperationTitle = "Pré-visualização · energia"; OperationText = preview;
        ResultTab = 1;
        if (!Confirm("Confirmar alteração do plano de energia", preview + "\n\nAplicar esta alteração e registrar os valores para restauração?")) return;
        await ShowChangeAsync(await _backend.ApplyPowerPlanAsync(plan.Id, OperationToken));
    }
    private async Task NetworkPreviewAsync()
    {
        if (SelectedNetworkSetting is not { } setting || SelectedNetworkValue is not { } value) return;
        var preview = await _backend.PreviewNetworkChangeAsync(setting, value.Id, OperationToken);
        OperationTitle = "Pré-visualização · rede"; OperationText = preview;
        ResultTab = 1;
        if (!Confirm("Confirmar alteração de rede", preview + "\n\nO valor será gravado com -NoRestart. A ativação pode exigir um reinício posterior autorizado separadamente. Aplicar exatamente esta alteração?")) return;
        await ShowChangeAsync(await _backend.ApplyNetworkChangeAsync(setting, value.Id, OperationToken));
    }
    private async Task RestoreAsync()
    {
        if (SelectedChange is not { } change) return;
        if (!Confirm("Restaurar valor anterior", $"Componente: {change.Target}\nValor anterior: {change.Before}\nValor aplicado: {change.After}\nRisco: {change.Risk}\n{change.RecoveryInstructions}\n\nRestaurar e verificar o estado real?")) return;
        await ShowChangeAsync(await _backend.RestoreChangeAsync(change.Id, OperationToken));
    }
    private async Task ShowChangeAsync(ChangeRecord change)
    {
        OperationTitle = $"Alteração: {ChangeLabelConverter.Label(change.State)}";
        ResultTab = 1;
        OperationText = $"{change.Kind} · {change.Target}\nAntes: {change.Before}\nDepois: {change.After}\nEstado verificado: {change.State}\n{change.Error}\n{change.RecoveryInstructions}";
        Replace(Changes, await _backend.ListChangesAsync(OperationToken)); Status = OperationTitle;
        if (change.Kind == "network-advanced-property" && SelectedAdapter is not null)
        {
            try { await InspectNetworkAsync(); }
            catch (Exception ex) { _backend.LogError("network.refresh-after-change", ex); }
        }
        Notification = OperationTitle + ". Confira o registro abaixo; aplicação não comprova melhoria.";
    }
    private async Task LabAsync(string test)
    {
        string? path = null;
        if (test == "disk-read")
        {
            var dialog = new OpenFileDialog { Title = "Selecionar arquivo existente para leitura, sem gravação", CheckFileExists = true, Filter = "Arquivo existente|*.*" };
            if (dialog.ShowDialog() != true) return; path = dialog.FileName;
        }
        var details = test switch
        {
            "cpu" => "Carga SHA-256 em uma thread por até 5 s. A temperatura e o consumo podem aumentar; o resultado não é uma pontuação comparável entre sistemas. Sensores térmicos podem estar indisponíveis.",
            "ram" => "Alocação e verificação de um buffer de 32 MiB em quatro passagens. Não comprova integridade física da RAM. Pode reduzir a memória disponível temporariamente.",
            _ => $"Leitura de até 64 MiB de um arquivo existente:\n{path}\nNenhuma gravação de benchmark. Cache do Windows pode influenciar o resultado; não representa velocidade nominal do SSD."
        };
        if (!Confirm("Iniciar teste controlado", details + "\n\nO botão Cancelar interrompe a operação. Iniciar agora?")) return;
        await RunTestAsync(test, (progress, ct) => _backend.RunLabAsync(test, path,
            new InlineProgress<double>(value => progress.Report(new(test == "cpu" ? "Carga de CPU" : test == "ram" ? "Verificando buffer de memória" : "Lendo arquivo", value))), ct));
    }
    private static bool Confirm(string title, string text) => MessageBox.Show(Application.Current.MainWindow, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    private string PreferencesPath => Path.Combine(DataDirectory, "collection-settings.json");
    private async Task LoadPreferencesAsync()
    {
        try
        {
            if (!File.Exists(PreferencesPath)) return;
            var preferences = JsonSerializer.Deserialize<CollectionPreferences>(await File.ReadAllTextAsync(PreferencesPath, _lifetime.Token));
            if (preferences is null) return;
            Mode = CollectionModes.Contains(preferences.Mode) ? preferences.Mode : "Leve"; IntervalSeconds = preferences.IntervalSeconds;
            ShowFirstUse = !preferences.OnboardingDismissed;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { _backend.LogError("Ler preferências", ex); }
    }
    private async Task SavePreferencesAsync()
    {
        Directory.CreateDirectory(DataDirectory);
        await File.WriteAllTextAsync(PreferencesPath, JsonSerializer.Serialize(new CollectionPreferences(Mode, IntervalSeconds, !ShowFirstUse)), OperationToken);
        Status = "Intervalo e modo de coleta salvos localmente.";
    }
    public async ValueTask DisposeAsync()
    {
        if (_closing) return;
        _closing = true; _uiTimer.Stop(); _lifetime.Cancel(); _operation?.Cancel();
        _execution.Cancel(); _execution.Changed -= OnExecutionChanged;
        if (_initializationTask is not null) await _initializationTask;
        if (_operationFinished is { Task.IsCompleted: false }) await _operationFinished.Task;
        if (_collectorTask is not null) { try { await _collectorTask; } catch (OperationCanceledException) { } }
        try { if (_inventory is not null && _samples.Count > 0) await _backend.SaveSessionAsync(Snapshot(), CancellationToken.None); }
        catch (Exception ex) { _backend.LogError("Salvar sessão ao sair", ex); }
        try { await _backend.DisposeAsync(); }
        catch (Exception ex) { _backend.LogError("Encerrar coletores", ex); }
        _lifetime.Dispose();
    }
    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear(); foreach (var value in values) collection.Add(value);
    }
    public static string AvailabilityLabel(Availability availability) => availability switch
    {
        Availability.Available => "Disponível", Availability.Collecting => "Coletando", Availability.Unsupported => "Sem suporte",
        Availability.PermissionRequired => "Permissão necessária", Availability.ToolMissing => "Ferramenta ausente",
        Availability.ReadError => "Falha de leitura", _ => "Dados insuficientes"
    };
}
