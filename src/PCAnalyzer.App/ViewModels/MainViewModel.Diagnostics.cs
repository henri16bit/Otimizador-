using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using PCAnalyzer.Core;
using PCAnalyzer.App.Services;
using PCAnalyzer.Reports;

namespace PCAnalyzer.App.ViewModels;

public sealed record SystemSummaryRow(string Name, string Value);
public sealed record TestRunRow(TestRunSummary Run)
{
    public Guid Id => Run.Id;
    public string Name => Run.Name;
    public string Status => TestLabels.Status(Run.Status);
    public string Started => Run.StartedAt.LocalDateTime.ToString("dd/MM HH:mm:ss");
    public string Duration => Run.EndedAt is { } end ? $"{(end - Run.StartedAt).TotalSeconds:0.#} s" : "Sem encerramento registrado";
    public string Label => $"{Started} · {Name} · {Status}";
}
public sealed record IndicatorRow(ResultIndicator Indicator)
{
    public string Name => Indicator.Name;
    public string Reading => Indicator.Value is { } v ? MetricPresentation.Format(v, Indicator.Unit) is { } d ? d.Value + " " + d.Unit : "—" : "—";
    public string Detail => Indicator.Detail;
    public MetricInfo Info => MetricCatalog.Resolve(Indicator.Id, Indicator.Unit);
}
public static class TestLabels
{
    public static string Status(TestStatus status) => status switch
    {
        TestStatus.Preparing => "Preparando", TestStatus.Running => "Executando", TestStatus.Completed => "Finalizado",
        TestStatus.Failed => "Falhou", TestStatus.Cancelled => "Cancelado", _ => "Não iniciado"
    };
}

public sealed partial class MainViewModel
{
    private readonly TestExecutionService _execution;
    private TestSession? _displayedExecution, _openedTest;
    private TestDefinition _selectedTest = TestCatalog.Get("system");
    private TestRunRow? _selectedRun, _beforeRun, _afterRun;
    private string _notification = "Escolha um diagnóstico. A execução e o resultado aparecem aqui.";
    private bool _firstUse = true;
    private int _chartWindowMinutes = 5;
    private string _csvQpcFrequency = "", _csvUtcOffset = "";
    public string CsvQpcFrequency { get => _csvQpcFrequency; set => Set(ref _csvQpcFrequency, value); }
    public string CsvUtcOffset { get => _csvUtcOffset; set => Set(ref _csvUtcOffset, value); }
    private PCAnalyzer.Gaming.PresentMonParseOptions ParseFrameImportOptions()
    {
        double? frequency = null; TimeSpan? offset = null;
        if (!string.IsNullOrWhiteSpace(CsvQpcFrequency))
        {
            if (!double.TryParse(CsvQpcFrequency, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0)
                throw new ArgumentException("Frequência QPC: informe um número positivo em Hz obtido na máquina da captura, ou deixe vazio.");
            frequency = value;
        }
        if (!string.IsNullOrWhiteSpace(CsvUtcOffset))
        {
            if (!TimeSpan.TryParse(CsvUtcOffset.Trim().TrimStart('+'), System.Globalization.CultureInfo.InvariantCulture, out var value)
                || Math.Abs(value.TotalHours) > 14 || value.Ticks % TimeSpan.TicksPerMinute != 0)
                throw new ArgumentException("Offset UTC: use o deslocamento real da captura, por exemplo -03:00. Deixe vazio para CSV com tempo relativo.");
            offset = value;
        }
        return new(QpcFrequencyHz: frequency, DateTimeUtcOffset: offset);
    }
    private SelectionItem? _selectedDisk;
    public ObservableCollection<SelectionItem> DiskChoices { get; } = [];
    public SelectionItem? SelectedDisk { get => _selectedDisk; set { if (Set(ref _selectedDisk, value)) RefreshDisplay(); } }
    public bool IsStorageComponent => SelectedComponent?.Id == "nvme";
    public IReadOnlyList<TestDefinition> TestChoices => TestCatalog.All;
    public TestDefinition SelectedTest { get => _selectedTest; set { if (Set(ref _selectedTest, value)) { StartSelectedTestCommand?.Refresh(); Notify(nameof(TestActionCaption)); } } }
    public string TestActionCaption => SelectedTest.Implemented ? "Iniciar diagnóstico…" : "Recurso indisponível";
    public string Notification { get => _notification; private set => Set(ref _notification, value); }
    public bool ShowFirstUse { get => _firstUse; private set => Set(ref _firstUse, value); }
    public string ReadinessSummary => _inventory is null ? "Identificando hardware e fontes…" :
        $"{Capabilities.Count(c => c.Status == Availability.Available)} fontes disponíveis · {Capabilities.Count(c => c.Status != Availability.Available)} com limitações";
    public string CollectorCost => string.Join(" · ", new[] { "collector.cpu.percent", "collector.working_set.bytes", "collector.duration.ms" }.Select(key =>
    {
        var sample = _latest.GetValueOrDefault(key); var reading = sample is null ? new DisplayReading("—", "") : MetricPresentation.Format(sample);
        return MetricCatalog.Resolve(key).Name + ": " + reading.Value + " " + reading.Unit;
    }));
    public ObservableCollection<SystemSummaryRow> SystemSummary { get; } = [];
    public ObservableCollection<TestRunRow> TestHistory { get; } = [];
    public IEnumerable<TestRunRow> RecentTests => TestHistory.Take(5);
    public ObservableCollection<IndicatorRow> ResultIndicators { get; } = [];
    public IEnumerable<IndicatorRow> ExecutionIndicators => (_displayedExecution?.Result?.Indicators ?? []).Select(i => new IndicatorRow(i));
    public ObservableCollection<Finding> TestFindings { get; } = [];
    public ObservableCollection<string> ResultLimitations { get; } = [];
    public ObservableCollection<string> ExecutionLog { get; } = [];
    public bool HasExecution => _displayedExecution is not null;
    public bool TestIsActive => _displayedExecution?.IsActive == true;
    public string ExecutionName => _displayedExecution?.Name ?? "Nenhum teste iniciado";
    public string ExecutionState => TestLabels.Status(_displayedExecution?.Status ?? TestStatus.NotStarted);
    public string ExecutionStage => _displayedExecution?.Progress.Stage ?? "Selecione o diagnóstico abaixo para começar.";
    public string ExecutionDetail => _displayedExecution?.Progress.Detail ?? "";
    public string ExecutionStarted => _displayedExecution?.StartedAt.LocalDateTime.ToString("HH:mm:ss") ?? "—";
    public string ExecutionElapsed => _displayedExecution is { } run ? run.EndedAt is null && !run.IsActive ? "Desconhecido"
        : $"{Math.Max(0, ((run.EndedAt ?? DateTimeOffset.UtcNow) - run.StartedAt).TotalSeconds):0} s" : "—";
    public bool ProgressIndeterminate => _displayedExecution?.Progress.Fraction is null;
    public double ExecutionPercent => (_displayedExecution?.Progress.Fraction ?? 0) * 100;
    public string ExecutionOutcome => _displayedExecution?.Error ?? _displayedExecution?.Result?.Title ?? "O resultado aparecerá aqui.";
    public string PersistenceWarning => _displayedExecution?.PersistenceError ?? "";
    public string OpenedResultContext => _openedTest is { } run ? $"{run.StartedAt.LocalDateTime:g} · {TestLabels.Status(run.Status)} · " +
        (run.EndedAt is { } end ? $"{(end - run.StartedAt).TotalSeconds:0.#} s" : "Duração final desconhecida") : "Resultado da operação selecionada";
    public bool CanExportTest => _openedTest is not null;
    public TestRunRow? SelectedRun { get => _selectedRun; set { Set(ref _selectedRun, value); OpenTestCommand?.Refresh(); } }
    public TestRunRow? BeforeRun { get => _beforeRun; set { Set(ref _beforeRun, value); CompareTestsCommand?.Refresh(); } }
    public TestRunRow? AfterRun { get => _afterRun; set { Set(ref _afterRun, value); CompareTestsCommand?.Refresh(); } }
    public IReadOnlyList<int> ChartWindows { get; } = [1, 5, 15, 60];
    public int ChartWindowMinutes { get => _chartWindowMinutes; set { if (value is 1 or 5 or 15 or 60 && Set(ref _chartWindowMinutes, value)) RefreshChart(); } }
    public string ChartContext => ChartSamples.Count == 0 ? "Aguardando dados — ausência não significa zero." :
        $"Janela de {ChartWindowMinutes} min · {ChartSamples.Count} leituras retidas · atualização {ChartSamples[^1].Timestamp.LocalDateTime:HH:mm:ss}";
    public AsyncCommand StartSelectedTestCommand { get; private set; } = null!;
    public AsyncCommand RefreshTestsCommand { get; private set; } = null!;
    public AsyncCommand OpenTestCommand { get; private set; } = null!;
    public AsyncCommand CompareTestsCommand { get; private set; } = null!;
    public AsyncCommand ExportTestCommand { get; private set; } = null!;
    public RelayCommand OpenLatestTestCommand { get; private set; } = null!;
    public AsyncCommand DismissFirstUseCommand { get; private set; } = null!;
    public RelayCommand OpenFindingCommand { get; private set; } = null!;
    public RelayCommand InvestigateFindingCommand { get; private set; } = null!;
    public RelayCommand DismissNotificationCommand { get; private set; } = null!;
    public RelayCommand OpenDataDirectoryCommand { get; private set; } = null!;

    private void InitializeDiagnosticCommands()
    {
        StartSelectedTestCommand = Command(StartSelectedTestAsync, () => SelectedTest.Implemented);
        RefreshTestsCommand = Command(RefreshTestsAsync);
        OpenTestCommand = Command(async () =>
        {
            if (SelectedRun is not { } selected) return;
            var run = await _backend.LoadTestAsync(selected.Id, OperationToken) ?? throw new InvalidDataException("Teste salvo não encontrado.");
            DisplayTestResult(run); ResultTab = 1; Navigate("resultados");
        }, () => SelectedRun is not null);
        CompareTestsCommand = Command(CompareTestsAsync, () => BeforeRun is not null && AfterRun is not null && BeforeRun.Id != AfterRun.Id);
        ExportTestCommand = Command(ExportTestAsync, () => CanExportTest);
        OpenLatestTestCommand = new RelayCommand(() =>
        {
            if (_displayedExecution is not { } run) return;
            DisplayTestResult(run); ResultTab = 1; Navigate("resultados");
        }, () => HasExecution && !TestIsActive);
        DismissFirstUseCommand = Command(async () => { ShowFirstUse = false; await SavePreferencesAsync(); });
        OpenFindingCommand = new RelayCommand(parameter =>
        {
            if (parameter is not Finding finding) return;
            if (!Findings.Contains(finding)) Findings.Add(finding);
            SelectedFinding = finding; ResultTab = 0; Navigate("resultados");
        });
        InvestigateFindingCommand = new RelayCommand(() =>
        {
            if (SelectedFinding?.Component is "Rede") OpenInvestigationCommand.Execute("rede");
            else if (SelectedFinding?.Component is "DPC/ISR") OpenInvestigationCommand.Execute("drivers");
            else if (SelectedFinding?.Component is "Jogos" or "Correlação") OpenInvestigationCommand.Execute("jogos");
            else OpenComponentCommand.Execute(SelectedFinding?.Component == "RAM" ? "ram" : "cpu");
        });
        DismissNotificationCommand = new RelayCommand(() => Notification = "");
        OpenDataDirectoryCommand = new RelayCommand(() =>
        {
            try { Directory.CreateDirectory(DataDirectory); Process.Start(new ProcessStartInfo(DataDirectory) { UseShellExecute = true }); }
            catch (Exception ex) { RecordFailure("Abrir pasta local", ex); }
        });
    }
    private Task StartSelectedTestAsync() => SelectedTest.Id switch
    {
        "system" => RunTestAsync("system", ObserveSystemAsync),
        "ping" => RunTestAsync("ping", (progress, ct) => _backend.PingAsync(PingHost.Trim(), SelectedAdapter?.Id, progress, ct)),
        "cpu" or "ram" or "disk-read" => LabAsync(SelectedTest.Id),
        "frames" => CaptureFramesAsync(), "frames-import" => ImportFramesAsync(),
        "dpc" => CaptureDpcAsync(), "etl-import" => ImportEtlAsync(),
        _ => throw new InvalidOperationException(SelectedTest.Limitations)
    };
    private async Task<TechnicalResult> ObserveSystemAsync(IProgress<TestProgress> progress, CancellationToken ct)
    {
        if (_paused) throw new InvalidOperationException("Retome a coleta no topo da janela antes da análise do sistema.");
        _diagnosticCadence = true;
        RecordCollectionChange("Análise manual: intervalo alvo temporário de 1 s por 15 s");
        var clock = Stopwatch.StartNew();
        try
        {
            while (clock.Elapsed < TimeSpan.FromSeconds(15))
            {
                progress.Report(new("Observando a carga atual", Math.Min(1, clock.Elapsed.TotalSeconds / 15), Detail: "Coleta de 1 s temporária. Nenhum benchmark iniciado."));
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(1000, Math.Max(1, 15_000 - clock.Elapsed.TotalMilliseconds))), ct);
            }
        }
        finally { _diagnosticCadence = false; RecordCollectionChange("Análise manual encerrada; intervalo do perfil retomado"); }
        progress.Report(new("Analisando a janela observada", 1));
        return new("Análise do sistema concluída", ["A triagem abaixo usa somente fontes disponíveis durante a observação.", "Não há pontuação genérica de saúde ou alteração automática."],
            Limitations: ["Uma janela curta ou cadência longa pode gerar dados insuficientes. Reproduza a carga que apresenta o sintoma."], ComparisonKey: "system:15s:1s-target");
    }
    private async Task RunTestAsync(string id, Func<IProgress<TestProgress>, CancellationToken, Task<TechnicalResult>> execute)
    {
        var definition = TestCatalog.Get(id);
        var started = DateTimeOffset.UtcNow;
        var context = new SessionContext(Workload, Mode, IntervalSeconds, Notes);
        if (id == "system") context = context with { CollectionMode = "Diagnóstico manual", IntervalSeconds = 1,
            Notes = Notes + "\nIntervalo temporário de 1 s; cada amostra preserva sua cadência real, incluindo a transição do perfil anterior." };
        var inventory = _inventory!;
        if (id is "frames-import" or "etl-import" && _loadedSession is { } historical)
        { context = historical.Context; inventory = historical.Inventory; }
        Notification = definition.Name + " iniciado. Acompanhe aqui ou continue navegando.";
        ExecutionLog.Clear();
        var run = await _execution.ExecuteAsync(definition, async (progress, ct) =>
        {
            var result = await execute(progress, ct);
            progress.Report(new("Organizando evidências e resultado"));
            MetricSample[] telemetry;
            lock (_sampleLock) telemetry = id is "frames-import" or "etl-import" ? (_loadedSession?.Samples ?? _samples).ToArray()
                : _samples.Where(s => s.Timestamp >= started).ToArray();
            if (id is "frames-import" or "etl-import")
            {
                if (result.Metrics is { Count: > 0 } imported)
                { var first = imported.Min(s => s.Timestamp); var last = imported.Max(s => s.Timestamp); telemetry = telemetry.Where(s => s.Timestamp >= first.AddSeconds(-1) && s.Timestamp <= last.AddSeconds(1)).ToArray(); }
                else telemetry = [];
            }
            var samples = telemetry.Concat(result.Metrics ?? []).ToArray();
            var findings = _backend.Analyze(inventory, telemetry).Concat(result.Findings ?? [])
                .Concat(TemporalDiagnosticAnalyzer.Analyze(samples)).DistinctBy(f => f.Id).ToArray();
            IReadOnlyList<ChangeRecord> changes;
            try { changes = await _backend.ListChangesAsync(CancellationToken.None); }
            catch (Exception ex)
            {
                _backend.LogError("test.change-context", ex); changes = [];
                result = result with { Limitations = (result.Limitations ?? []).Append("Não foi possível consultar o diário de alterações. As medições foram preservadas; a comparação não conhece todas as mudanças.").ToArray() };
            }
            var ended = DateTimeOffset.UtcNow;
            var session = new DiagnosticSession(Guid.NewGuid(), definition.Name, started, ended, context, inventory, samples, findings,
                changes.Where(c => c.CreatedAt >= started && c.CreatedAt <= ended || c.UpdatedAt >= started && c.UpdatedAt <= ended).ToArray());
            return result with { Session = session, Findings = findings };
        }, OperationToken);
        RefreshExecutionDisplay();
        DisplayTestResult(run);
        if (run.Result?.Metrics is { Count: > 0 } metrics)
        {
            lock (_sampleLock) { _samples.AddRange(metrics); TrimSamples(); }
            foreach (var metric in metrics) if (!_latest.TryGetValue(metric.Key, out var old) || metric.Timestamp >= old.Timestamp) _latest[metric.Key] = metric;
            RefreshDisplay(); AnalyzeDisplay();
        }
        try { await RefreshTestsAsync(); }
        catch (Exception ex) { _backend.LogError("test.history.refresh", ex); Notification = "Resultado disponível; não foi possível atualizar a lista do histórico."; }
        Notification = $"{run.Name}: {TestLabels.Status(run.Status).ToLowerInvariant()}. " +
            (run.PersistenceError ?? "O resumo está na visão geral; os detalhes podem ser abertos abaixo.");
        Status = Notification;
    }
    private void OnExecutionChanged(TestSession session)
    {
        // Coalesce potentially thousands of I/O progress updates to the existing 1 Hz UI timer.
        // Completion is also flushed by RunTestAsync after awaiting the service.
        if (session.Status == TestStatus.Preparing && Application.Current?.Dispatcher is { } dispatcher)
            _ = dispatcher.BeginInvoke(RefreshExecutionDisplay, DispatcherPriority.Background);
    }
    private void RefreshExecutionDisplay()
    {
        var run = _execution.Current ?? _displayedExecution;
        if (run is null) return;
        if (_displayedExecution?.Progress.Stage != run.Progress.Stage || _displayedExecution.Status != run.Status)
        {
            ExecutionLog.Add($"{DateTime.Now:HH:mm:ss} · {run.Progress.Stage}");
            while (ExecutionLog.Count > 30) ExecutionLog.RemoveAt(0);
        }
        _displayedExecution = run;
        if (run.IsActive) Status = $"{run.Name} · {run.Progress.Stage} · {ExecutionElapsed}";
        Progress = ExecutionPercent;
        foreach (var name in new[] { nameof(HasExecution), nameof(TestIsActive), nameof(ExecutionName), nameof(ExecutionState), nameof(ExecutionStage),
            nameof(ExecutionDetail), nameof(ExecutionStarted), nameof(ExecutionElapsed), nameof(ProgressIndeterminate), nameof(ExecutionPercent), nameof(ExecutionOutcome), nameof(PersistenceWarning), nameof(ExecutionIndicators) }) Notify(name);
        OpenLatestTestCommand?.Refresh();
        Notify(nameof(OverviewFindings));
    }
    private void DisplayTestResult(TestSession run)
    {
        _openedTest = run;
        OperationTitle = run.Result?.Title ?? run.Name;
        OperationText = string.Join(Environment.NewLine, (run.Result?.Lines ?? []).Concat(run.Error is null ? [] : new[] { run.Error }));
        Replace(ResultSeries, run.Result?.Series ?? []); SelectedRelativeSeries = ResultSeries.FirstOrDefault();
        Replace(ResultIndicators, (run.Result?.Indicators ?? []).Select(i => new IndicatorRow(i)));
        Replace(TestFindings, run.Result?.Findings ?? []);
        Replace(ResultLimitations, run.Result?.Limitations ?? ["Não há resultado de medição completo nesta execução."]);
        Notify(nameof(HasRelativeSeries)); Notify(nameof(OpenedResultContext)); Notify(nameof(CanExportTest)); ExportTestCommand.Refresh();
    }
    private async Task RefreshTestsAsync()
    {
        var selected = SelectedRun?.Id;
        var history = await _backend.ListTestsAsync(100, _lifetime.Token);
        Replace(TestHistory, history.Select(r => new TestRunRow(r)));
        SelectedRun = TestHistory.FirstOrDefault(r => r.Id == selected) ?? TestHistory.FirstOrDefault();
        if (_displayedExecution is null && TestHistory.FirstOrDefault() is { } latest)
        {
            _displayedExecution = await _backend.LoadTestAsync(latest.Id, _lifetime.Token);
            RefreshExecutionDisplay();
        }
        Notify(nameof(RecentTests));
    }
    private async Task CompareTestsAsync()
    {
        var before = await _backend.LoadTestAsync(BeforeRun!.Id, OperationToken) ?? throw new InvalidDataException("Teste anterior ausente.");
        var after = await _backend.LoadTestAsync(AfterRun!.Id, OperationToken) ?? throw new InvalidDataException("Teste posterior ausente.");
        var result = await Task.Run(() => TestResultComparer.Compare(before, after), OperationToken);
        _openedTest = null; Notify(nameof(CanExportTest)); ExportTestCommand.Refresh();
        Replace(ResultIndicators, []); Replace(TestFindings, []); Replace(ResultLimitations, []);
        await ShowResultAsync(Task.FromResult(result));
        ResultTab = 1; Navigate("resultados");
    }
    private async Task ExportTestAsync()
    {
        if (_openedTest is not { } run) return;
        var dialog = new SaveFileDialog { Filter = "Relatório de teste HTML|*.html|Resultado completo JSON|*.json", FileName = $"PCAnalyzer-teste-{run.StartedAt:yyyyMMdd-HHmmss}.html" };
        if (dialog.ShowDialog() != true) return;
        await TestReportExporter.ExportAsync(run, dialog.FileName, Anonymize, OperationToken);
        Notification = "Resultado exportado com métricas, evidências e limitações.";
    }
    private void UpdateSystemSummary(HardwareInventory? inventory, IReadOnlyList<MetricSample> samples)
    {
        var disks = new[] { new SelectionItem("", "Todos os discos") }.Concat(inventory?.Components.Where(c => c.Id.StartsWith("disk.", StringComparison.Ordinal))
            .Select(c => new SelectionItem(c.Id, c.Name + " · " + c.Id)) ?? []).ToArray();
        if (!DiskChoices.SequenceEqual(disks))
        {
            var previous = _selectedDisk?.Id; Replace(DiskChoices, disks);
            _selectedDisk = DiskChoices.FirstOrDefault(d => d.Id == previous) ?? DiskChoices.FirstOrDefault(); Notify(nameof(SelectedDisk));
        }
        var rows = new List<SystemSummaryRow>();
        foreach (var category in new[] { "CPU", "GPU", "RAM", "Armazenamento", "Rede", "Monitor", "Sistema" })
        {
            var components = inventory?.Components.Where(c => c.Category == category).ToArray() ?? [];
            // Prefer Win32 physical-disk identity for the overview; Storage provider details remain in Components.
            if (category == "Armazenamento" && components.Any(c => c.Id.StartsWith("disk.", StringComparison.Ordinal)))
                components = components.Where(c => c.Id.StartsWith("disk.", StringComparison.Ordinal)).ToArray();
            var value = string.Join(" · ", components.Select(c => c.Name).Distinct());
            if (category == "RAM" && samples.LastOrDefault(s => s.Key == "memory.total.bytes" && MetricPresentation.Available(s)) is { } ram)
            { var display = MetricPresentation.Format(ram); value = display.Value + " " + display.Unit + " · " + components.Length + " módulo(s) detectado(s)"; }
            if (category == "Monitor") value = string.Join(" · ", components.Select(c => c.Name + " " + string.Join(" ", c.Facts.Where(f => f.Name is "Frequência atual Hz" or "Resolução atual").Select(f => f.Value + (f.Name == "Frequência atual Hz" ? " Hz" : "")))));
            rows.Add(new(category == "Armazenamento" ? "Discos / NVMe" : category, value.Length == 0 ? "Dados não disponíveis — consulte as fontes." : value));
        }
        Replace(SystemSummary, rows); Notify(nameof(ReadinessSummary)); Notify(nameof(ChartContext)); Notify(nameof(CollectorCost));
    }
}
