using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCAnalyzer.App;
using PCAnalyzer.App.Services;
using PCAnalyzer.App.ViewModels;
using PCAnalyzer.Core;
using PCAnalyzer.Gaming;
using PCAnalyzer.Network;
using PCAnalyzer.Storage;

namespace PCAnalyzer.WpfHarness;

internal static class Program
{
    private static int _exitCode = 1;
    [STAThread]
    public static int Main()
    {
        var application = new PCAnalyzer.App.App { StartupManagedByTest = true };
        application.InitializeComponent(); application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var bindingErrors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        var backend = new ScenarioBackend(); var vm = new MainViewModel(backend);
        var window = new MainWindow(vm, initializeHardware: false); application.MainWindow = window;
        window.Show();
        _ = window.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await RunAsync(window, vm, backend);
                Require(bindingErrors.Errors.Count == 0, "WPF binding failures: " + string.Join("\n", bindingErrors.Errors));
                Directory.CreateDirectory("artifacts");
                await File.WriteAllTextAsync("artifacts/ui-flow-result.json", JsonSerializer.Serialize(new { success = true,
                    checks = new[] { "readable selection labels", "interrupted execution recovery", "dashboard progress", "navigation during run", "no forced navigation", "cancel", "failure", "reopen stored result", "network read and unavailable property", "missing sensors",
                        "collection before inventory", "six simple navigation entries", "structured general report", "missing GPU is inconclusive", "report history reopen", "failure clears old report", "imports do not change live readings", "no WPF binding errors", "offscreen WPF layouts at 1366x768 and 1920x1080", "gaming opt-in commands", "failed analysis remains latest attempt", "selected finding remains in its module" },
                    fixtureDataOnly = true, hardwareOrConfigurationChanged = false }));
                _exitCode = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); Directory.CreateDirectory("artifacts"); await File.WriteAllTextAsync("artifacts/ui-flow-result.json", JsonSerializer.Serialize(new { success = false, error = error.ToString() })); }
            finally { await vm.DisposeAsync(); window.Close(); application.Shutdown(_exitCode); }
        }, DispatcherPriority.ApplicationIdle);
        return application.Run();
    }
    private static async Task RunAsync(MainWindow window, MainViewModel vm, ScenarioBackend backend)
    {
        await backend.SaveTestAsync(new(Guid.NewGuid(), "ping", "Interrupted fixture", TestStatus.Running,
            DateTimeOffset.UtcNow.AddDays(-1), null, new("Fixture interrupted")));
        var initialization = vm.InitializeAsync();
        await Until(() => backend.CollectionCalls > 0);
        Require(!initialization.IsCompleted, "Basic collection must start while detailed inventory is pending.");
        backend.InventoryReady.SetResult();
        await initialization;
        Require(vm.ExecutionState == "Falhou" && vm.ExecutionElapsed == "Desconhecido" && !vm.TestIsActive,
            "Interrupted execution must not display an invented duration or restart.");
        Require(vm.OverviewCards.All(c => c.Value == "—"), "Unavailable sensors must not become zero.");
        vm.SelectedTest = TestCatalog.Get("ping");
        Require(vm.VisibleSections.Select(s => s.Id).SequenceEqual(new[] { "dashboard", "analisar", "resultados", "ajustes", "avancado", "config" }), "Simple navigation differs from the six intended destinations.");
        vm.NavigateCommand.Execute("analisar");
        window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var durationSelector = Descendants<ComboBox>(window).Single(c => ReferenceEquals(c.ItemsSource, vm.ObservationDurations));
        new StartupDiagnostics(["--smoke-test", "--smoke-output", "artifacts/ui-flow-screenshot.json"]).CaptureView((FrameworkElement)window.Content, "analysis-fixture");
        Require(Descendants<TextBlock>(durationSelector).Any(t => t.Text.Contains("30 segundos")), "Duration must have a readable label. Selected=" + durationSelector.SelectedItem + "; rendered=" + string.Join("|", Descendants<TextBlock>(durationSelector).Select(t => t.Text)));
        Require(vm.AnalyzePcCommand.CanExecute(null) && vm.AnalyzeGamesCommand.CanExecute(null) && !vm.IncludeLatency && !vm.IncludePing,
            "Guided commands must be available and optional active probes disabled by default.");
        vm.NavigateCommand.Execute("dashboard");
        vm.StartSelectedTestCommand.Execute(null);
        await Until(() => vm.TestIsActive && vm.ExecutionStage == "Fixture: pacote 1 de 20");
        window.UpdateLayout();
        Require(Descendants<TextBlock>(window).Any(t => t.Text == vm.ExecutionStage), "Measured stage is not bound into the dashboard view.");
        Require(Descendants<ProgressBar>(window).Any(p => !p.IsIndeterminate && p.Value == 5), "Dashboard progress bar did not update.");
        Require(!vm.ProgressIndeterminate && vm.ExecutionPercent == 5, "Measured progress not visible on dashboard.");
        vm.NavigateCommand.Execute("componentes"); window.UpdateLayout();
        Require(vm.IsBusy && vm.TestIsActive && vm.ContentTab == 1, "Navigation restarted/lost active execution.");
        backend.FinishPing.SetResult(new("Fixture: rede concluída", ["Fixture only; no network traffic"],
            Series: [new("Fixture timeline", "ms", [new(0, 1), new(1000, null)], "relative fixture", "test-only")],
            Indicators: [new("network.ping.rtt.ms", "RTT fixture", 1, "ms", "test-only")], Limitations: ["Fixture test, not a hardware measurement"]));
        await Until(() => !vm.IsBusy);
        Require(vm.SelectedSection?.Id == "componentes", "Completion unexpectedly changed page.");
        Require(vm.ExecutionState == "Finalizado" && vm.ExecutionIndicators.Single().Reading.StartsWith("1"), "Dashboard did not receive result.");
        var firstId = vm.TestHistory[0].Id;
        var saved = await backend.LoadTestAsync(firstId);
        Require(saved?.Result?.Series?.Count == 1, "Full result not persisted.");

        backend.Mode = "cancel"; vm.PingCommand.Execute(null); await Until(() => vm.TestIsActive);
        vm.CancelCommand.Execute(null); await Until(() => !vm.IsBusy);
        Require(vm.ExecutionState == "Cancelado" && vm.ResultSeries.Count == 0, "Cancellation reused stale result.");
        backend.Mode = "failure"; vm.PingCommand.Execute(null); await Until(() => !vm.IsBusy);
        Require(vm.ExecutionState == "Falhou" && vm.OperationText.Contains("fixture denied"), "Failure not surfaced.");

        vm.SelectedRun = vm.TestHistory.Single(r => r.Id == firstId); vm.OpenTestCommand.Execute(null); await Until(() => !vm.IsBusy);
        Require(vm.OperationTitle == "Fixture: rede concluída" && vm.ResultSeries.Count == 1 && vm.SelectedSection?.Id == "resultados", "Saved test did not reopen.");
        Require(vm.ExecutionState == "Falhou" && !vm.ExecutionIndicators.Any(), "Opening old result corrupted latest dashboard run.");
        vm.OpenInvestigationCommand.Execute("rede"); await Until(() => vm.NetworkSettings.Count == 1);
        Require(vm.SelectedAdapter?.Name.Contains("fixture") == true, "Adapter selection missing.");
        vm.InspectNetworkCommand.Execute(null); await Until(() => !vm.IsBusy);
        Require(vm.NetworkSettings.Count == 1 && !vm.NetworkPreviewCommand.CanExecute(null), "Unsupported driver property appears writable.");
        Require(vm.NetworkReadStatus.Contains("Leitura concluída"), "Network read lacks feedback.");
        window.UpdateLayout();
        var adapterSelector = Descendants<ComboBox>(window).Single(c => ReferenceEquals(c.ItemsSource, vm.NetworkAdapters));
        Require(Descendants<TextBlock>(adapterSelector).Any(t => t.Text == vm.SelectedAdapter!.Name), "Adapter selector must show a readable name.");
        var diagnostics = new StartupDiagnostics(["--smoke-test", "--smoke-output", "artifacts/ui-flow-screenshot.json"]);
        diagnostics.CaptureView((FrameworkElement)window.Content, "network-fixture");

        await RunFixture(vm, "general", (progress, ct) => backend.AnalyzePcAsync(new(ObservationSeconds: 1), progress, _ => { }, ct));
        Require(vm.HasDiagnostic && vm.HasSimpleReport && vm.CanExportTest && vm.ReportComponents.Count >= 8, "General analysis lost its structured report.");
        Require(vm.ReportComponents.Single(c => c.Id == "gpu").State == DiagnosticAssessmentState.Inconclusive, "Missing GPU must remain inconclusive.");
        Require(vm.ReportComponents.Single(c => c.Id == "cpu").State == DiagnosticAssessmentState.Normal, "Independent CPU evidence was lost.");
        var analysisId = vm.RecentAnalyses.First().Id;
        var report = (await backend.LoadTestAsync(analysisId))?.Result?.Report;
        Require(report?.Completion == DiagnosticCompletion.Partial && report.Components.Count == vm.ReportComponents.Count, "Structured partial report did not survive SQLite.");
        typeof(MainViewModel).GetMethod("RecordFailure", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, ["fixture action", new IOException("fixture failure")]);
        Require(!vm.CanExportTest && !vm.HasSimpleReport && vm.ResultIndicators.Count == 0 && vm.TestFindings.Count == 0 && vm.ResultSeries.Count == 0,
            "Operational failure retained stale report/export/indicators.");
        Require(vm.HasDiagnostic, "An unrelated operation failure erased the last diagnostic summary.");
        vm.SelectedRun = vm.TestHistory.Single(r => r.Id == analysisId); vm.OpenTestCommand.Execute(null); await Until(() => !vm.IsBusy);
        Require(vm.HasSimpleReport && vm.ReportComponents.Count == report!.Components.Count, "History did not restore the simple report.");
        window.UpdateLayout(); diagnostics.CaptureView((FrameworkElement)window.Content, "report-fixture");
        await RunFixture(vm, "frames-import", (_, _) => Task.FromResult(new TechnicalResult("Imported fixture", ["Fixture only"],
            Metrics: [new("frames.fixture.ms", "Jogos", 17, "ms", "fixture", "fixture", DateTimeOffset.UtcNow, TimeSpan.Zero)])));
        var latest = (Dictionary<string, MetricSample>)typeof(MainViewModel).GetField("_latest", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm)!;
        Require(!latest.ContainsKey("frames.fixture.ms") && !vm.HasSimpleReport, "Imported capture polluted current telemetry or retained the prior report.");
        vm.NavigateCommand.Execute("dashboard"); window.UpdateLayout();
        diagnostics.CaptureView((FrameworkElement)window.Content, "dashboard-fixture");
        var content = (FrameworkElement)window.Content;
        foreach (var size in new[] { new Size(1366, 768), new Size(1920, 1080) })
        {
            // A hosted runner's desktop can cap HWND size. Exercise WPF measure/arrange at
            // exact virtual viewports while preserving the Window ancestry and bindings.
            content.Width = size.Width; content.Height = size.Height;
            content.Measure(size); content.Arrange(new Rect(new Point(), size));
            Require(Math.Abs(content.ActualWidth - size.Width) < 1 && Math.Abs(content.ActualHeight - size.Height) < 1,
                $"WPF viewport did not render {size}: actual {content.ActualWidth}x{content.ActualHeight}.");
            diagnostics.CaptureView(content, "dashboard-" + size.Width);
        }
        content.Width = double.NaN; content.Height = double.NaN;
        window.InvalidateMeasure(); window.UpdateLayout();
        await RunFixture(vm, "general", (_, _) => Task.FromException<TechnicalResult>(new IOException("Fixture general failed before report")));
        Require(vm.HasDiagnostic && vm.LastDiagnosticState == "Falhou" && vm.LastDiagnosticSummary.Contains("Fixture general failed")
            && vm.PriorityRecommendations.Count == 0, "Latest failed analysis was hidden by an older report.");
        var finding = new Finding("fixture.network", "Rede", "Fixture network finding", "Fixture only", FindingCategory.InsufficientData,
            Severity.Information, Confidence.Low, [], "Repeat fixture", "Fixture", "No changes");
        vm.OpenFindingCommand.Execute(finding);
        typeof(MainViewModel).GetMethod("AnalyzeDisplay", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
        Require(vm.SelectedFinding == finding && vm.ShowTechnicalDetails && vm.TestFindings.Count == 1 && vm.TestFindings[0].Component == "Rede",
            "Passive refresh replaced the selected finding or mixed modules.");
    }
    private static Task RunFixture(MainViewModel vm, string id, Func<IProgress<TestProgress>, CancellationToken, Task<TechnicalResult>> run)
    {
        var execute = typeof(MainViewModel).GetMethod("RunTestAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var operation = typeof(MainViewModel).GetMethod("RunOperationAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (Task)operation.Invoke(vm, [new Func<Task>(() => (Task)execute.Invoke(vm, [id, run])!)])!;
    }
    private static async Task Until(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(12);
        while (!condition()) { if (DateTime.UtcNow > until) throw new TimeoutException("UI scenario timed out."); await Task.Delay(25); }
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
    private sealed class BindingErrors : TraceListener
    {
        public List<string> Errors { get; } = [];
        public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Errors.Add(message); }
        public override void WriteLine(string? message) => Write(message);
    }
}

/// <summary>Test-only backend. This project and its fixtures are never packaged with the application.</summary>
internal sealed class ScenarioBackend : IApplicationBackend
{
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "PCAnalyzer-ui-fixture-" + Guid.NewGuid().ToString("N"));
    private readonly SQLiteHistoryStore _history;
    private readonly Guid _adapter = Guid.NewGuid();
    public string Mode { get; set; } = "complete";
    public TaskCompletionSource<TechnicalResult> FinishPing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource InventoryReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int CollectionCalls;
    public ScenarioBackend() => _history = new(Path.Combine(DataDirectory, "fixture.db"));
    public async Task<HardwareInventory> InitializeAsync(CancellationToken ct) { await InventoryReady.Task.WaitAsync(ct); return new(DateTimeOffset.UtcNow, [], [new("Fixture sensor", Availability.Unsupported, "Test only")]); }
    public Task<TelemetryBatch> CollectAsync(TimeSpan interval, CancellationToken ct)
    {
        Interlocked.Increment(ref CollectionCalls);
        return Task.FromResult(new TelemetryBatch(DateTimeOffset.UtcNow,
            [MetricSample.Missing("cpu.total.percent", "CPU", "%", "fixture", DateTimeOffset.UtcNow, Availability.Unsupported, "fixture only")], []));
    }
    public Task ResetTelemetryAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<PresentMonToolInspection> InspectPresentMonAsync(string path, CancellationToken ct) => Task.FromResult(new PresentMonToolInspection(path, Availability.ToolMissing, null, PresentMonCliStyle.Unsupported, "Fixture; no executable started", []));
    public Task<TechnicalResult> AnalyzePcAsync(DiagnosticOptions options, IProgress<TestProgress> progress, Action<TelemetryBatch> publishBatch, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var inventory = new HardwareInventory(now, [], []);
        var samples = Enumerable.Range(0, 12).Select(i => new MetricSample("cpu.total.percent", "CPU", 20, "%", "fixture", "test-only",
            now.AddSeconds(i - 12), TimeSpan.FromSeconds(1))).ToArray();
        var report = new DiagnosticAnalysisEngine().Analyze(inventory, samples, [], [], []);
        var session = new DiagnosticSession(Guid.NewGuid(), "Fixture general", now.AddSeconds(-12), now, new("Fixture", "test-only", 1), inventory, samples, report.Findings);
        return Task.FromResult(new TechnicalResult("Fixture general", [report.Summary], samples, Findings: report.Findings,
            Limitations: report.Limitations, Session: session, Report: report, Partial: true));
    }
    public IReadOnlyList<Finding> Analyze(HardwareInventory inventory, IReadOnlyList<MetricSample> samples) => new DiagnosticRuleEngine().Analyze(inventory, samples);
    public Task SaveSessionAsync(DiagnosticSession session, CancellationToken ct) => _history.SaveSessionAsync(session, ct);
    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken ct) => _history.ListSessionsAsync(cancellationToken: ct);
    public Task<DiagnosticSession?> LoadSessionAsync(Guid id, CancellationToken ct) => _history.LoadSessionAsync(id, ct);
    public Task SaveTestAsync(TestSession session, CancellationToken ct = default) => _history.SaveTestAsync(session, ct);
    public Task<TestSession?> LoadTestAsync(Guid id, CancellationToken ct = default) => _history.LoadTestAsync(id, ct);
    public Task<IReadOnlyList<TestRunSummary>> ListTestsAsync(int limit = 100, CancellationToken ct = default) => _history.ListTestsAsync(limit, ct);
    public Task<IReadOnlyList<ChangeRecord>> ListChangesAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<ChangeRecord>>([]);
    public async Task<TechnicalResult> PingAsync(string host, Guid? adapterId, IProgress<TestProgress> progress, CancellationToken ct)
    {
        if (Mode == "failure") throw new UnauthorizedAccessException("fixture denied");
        progress.Report(new("Fixture: pacote 1 de 20", .05, 1, "Fixture; no ICMP sent"));
        if (Mode == "cancel") { await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException("unreachable"); }
        return await FinishPing.Task.WaitAsync(ct);
    }
    public Task<NetworkSnapshot> DiscoverNetworkAsync(CancellationToken ct) => Task.FromResult(new NetworkSnapshot(DateTimeOffset.UtcNow,
        [new(_adapter, "Ethernet fixture", "Test adapter", "Ethernet", "Up", 1_000_000_000, [], ["192.0.2.1"], 0, 0, 0, 0)], Availability.Available));
    public Task<AdapterDetail> InspectNetworkAsync(Guid id, CancellationToken ct) => Task.FromResult(new AdapterDetail(id, "Ethernet fixture", "Test only", "fixture", null, null, "1 Gbit/s", [], [], [], Availability.Available, []));
    public IReadOnlyList<NetworkSettingView> NetworkSettingsFor(AdapterDetail detail) => [new(detail.InterfaceGuid.ToString(), detail.Name, "*RSS", "RSS fixture", "[]", [], "No values advertised", "indisponível", "Somente leitura")];
    public void LogError(string operation, Exception exception) => Console.WriteLine(operation + ": " + exception.Message);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public Task ExportAsync(DiagnosticSession session, string path, string format, bool anonymize, CancellationToken ct) => throw new NotSupportedException();
    public Task<TechnicalResult> CompareAsync(Guid before, Guid after, CancellationToken ct) => throw new NotSupportedException();
    public Task<TechnicalResult> ImportFramesAsync(string path, PCAnalyzer.Gaming.PresentMonParseOptions options, CancellationToken ct) => throw new NotSupportedException();
    public Task<TechnicalResult> CaptureFramesAsync(string path, int pid, CancellationToken ct) => throw new NotSupportedException();
    public Task<TechnicalResult> CaptureDpcAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<TechnicalResult> ImportEtlAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<SelectionItem>> ListPowerPlansAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<string> PreviewPowerPlanAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    public Task<ChangeRecord> ApplyPowerPlanAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    public Task<string> PreviewNetworkChangeAsync(NetworkSettingView setting, string value, CancellationToken ct) => throw new NotSupportedException();
    public Task<ChangeRecord> ApplyNetworkChangeAsync(NetworkSettingView setting, string value, CancellationToken ct) => throw new NotSupportedException();
    public Task<ChangeRecord> RestoreChangeAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task<TechnicalResult> RunLabAsync(string test, string? path, IProgress<double> progress, CancellationToken ct) => throw new NotSupportedException();
}
