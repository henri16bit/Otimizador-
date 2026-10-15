using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using PCAnalyzer.App;
using PCAnalyzer.App.Services;
using PCAnalyzer.App.ViewModels;
using PCAnalyzer.Core;
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
        var backend = new ScenarioBackend(); var vm = new MainViewModel(backend);
        var window = new MainWindow(vm, initializeHardware: false); application.MainWindow = window;
        window.Show();
        _ = window.Dispatcher.BeginInvoke(async () =>
        {
            try
            {
                await RunAsync(window, vm, backend);
                Directory.CreateDirectory("artifacts");
                await File.WriteAllTextAsync("artifacts/ui-flow-result.json", JsonSerializer.Serialize(new { success = true,
                    checks = new[] { "interrupted execution recovery", "dashboard progress", "navigation during run", "no forced navigation", "cancel", "failure", "reopen stored result", "network read and unavailable property", "missing sensors" },
                    fixtureDataOnly = true, hardwareOrConfigurationChanged = false }));
                _exitCode = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); Directory.CreateDirectory("artifacts"); await File.WriteAllTextAsync("artifacts/ui-flow-result.json", JsonSerializer.Serialize(new { success = false, error = error.ToString() })); }
            finally { await vm.DisposeAsync(); window.Close(); application.Shutdown(_exitCode); Dispatcher.ExitAllFrames(); }
        }, DispatcherPriority.ApplicationIdle);
        Dispatcher.Run();
        return _exitCode;
    }
    private static async Task RunAsync(MainWindow window, MainViewModel vm, ScenarioBackend backend)
    {
        await backend.SaveTestAsync(new(Guid.NewGuid(), "ping", "Interrupted fixture", TestStatus.Running,
            DateTimeOffset.UtcNow.AddDays(-1), null, new("Fixture interrupted")));
        await vm.InitializeAsync();
        Require(vm.ExecutionState == "Falhou" && vm.ExecutionElapsed == "Desconhecido" && !vm.TestIsActive,
            "Interrupted execution must not display an invented duration or restart.");
        Require(vm.OverviewCards.All(c => c.Value == "—"), "Unavailable sensors must not become zero.");
        vm.SelectedTest = TestCatalog.Get("ping");
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
        vm.OpenInvestigationCommand.Execute("rede"); vm.DiscoverNetworkCommand.Execute(null); await Until(() => !vm.IsBusy);
        Require(vm.SelectedAdapter?.Name.Contains("fixture") == true, "Adapter selection missing.");
        vm.InspectNetworkCommand.Execute(null); await Until(() => !vm.IsBusy);
        Require(vm.NetworkSettings.Count == 1 && !vm.NetworkPreviewCommand.CanExecute(null), "Unsupported driver property appears writable.");
        Require(vm.NetworkReadStatus.Contains("Leitura concluída"), "Network read lacks feedback.");
        vm.NavigateCommand.Execute("dashboard"); window.UpdateLayout();
        var diagnostics = new StartupDiagnostics(["--smoke-test", "--smoke-output", "artifacts/ui-flow-screenshot.json"]);
        diagnostics.CaptureView((FrameworkElement)window.Content, "dashboard-fixture");
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
}

/// <summary>Test-only backend. This project and its fixtures are never packaged with the application.</summary>
internal sealed class ScenarioBackend : IApplicationBackend
{
    public string DataDirectory { get; } = Path.Combine(Path.GetTempPath(), "PCAnalyzer-ui-fixture-" + Guid.NewGuid().ToString("N"));
    private readonly SQLiteHistoryStore _history;
    private readonly Guid _adapter = Guid.NewGuid();
    public string Mode { get; set; } = "complete";
    public TaskCompletionSource<TechnicalResult> FinishPing { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public ScenarioBackend() => _history = new(Path.Combine(DataDirectory, "fixture.db"));
    public Task<HardwareInventory> InitializeAsync(CancellationToken ct) => Task.FromResult(new HardwareInventory(DateTimeOffset.UtcNow, [], [new("Fixture sensor", Availability.Unsupported, "Test only")]));
    public Task<TelemetryBatch> CollectAsync(TimeSpan interval, CancellationToken ct) => Task.FromResult(new TelemetryBatch(DateTimeOffset.UtcNow,
        [MetricSample.Missing("cpu.total.percent", "CPU", "%", "fixture", DateTimeOffset.UtcNow, Availability.Unsupported, "fixture only")], []));
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
