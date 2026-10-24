using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PCAnalyzer.App.Controls;
using PCAnalyzer.App;
using PCAnalyzer.App.Services;
using PCAnalyzer.App.ViewModels;
using PCAnalyzer.Core;
using PCAnalyzer.Diagnostics;
using PCAnalyzer.Gaming;
using PCAnalyzer.Network;
using PCAnalyzer.Storage;

namespace PCAnalyzer.WpfHarness;

internal static class Program
{
    private static int _exitCode = 1;
    private static int _confirmationCalls;
    private static bool _acceptConfirmation = true;
    private static string _confirmationText = "";
    [STAThread]
    public static int Main()
    {
        var application = new PCAnalyzer.App.App { StartupManagedByTest = true };
        application.InitializeComponent(); application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var bindingErrors = new BindingErrors();
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingErrors);
        var backend = new ScenarioBackend(); var vm = new MainViewModel(backend, confirmation: (title, body) =>
        { _confirmationCalls++; _confirmationText = title + "\n" + body; return _acceptConfirmation; });
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
                        "collection before inventory", "ten category navigation entries", "structured general report", "missing GPU is inconclusive", "report history reopen", "failure clears old report", "imports do not change live readings", "no WPF binding errors", "offscreen WPF layouts at 1366x768 and 1920x1080", "gaming opt-in commands", "failed analysis remains latest attempt", "selected finding remains in its module",
                        "general result on same page", "audit command and same-page coverage", "audit SQLite history reopen", "read-only consultation without apply controls", "audit cancellation and failure clear stale conclusions",
                        "audit partial state and export", "cancelled audit retains only newly collected evidence", "audit layouts at requested viewports", "memory access test is discoverable and manual", "category filters search and ordering", "large audit list virtualizes containers",
                        "compact rows hide technical identities", "detail modal closes with keyboard and preserves context", "priority recommendations exclude disabled and ignored entries",
                        "manual network result remains in its category and preserves audit", "simulated 100 125 and 150 percent layouts", "one-click passive analysis needs no confirmation", "rendered setting and modal values meet contrast checks",
                        "visual captures preserve the complete viewport despite layout offsets", "short reasons explain compact recommendations",
                        "single-scroll modal pages technical evidence with stable footer and keyboard focus", "unknown properties never become optimization opportunities",
                        "manual DPC duration and context reach confirmation and backend", "declined DPC confirmation does not capture",
                        "last valid measurements survive failed attempts", "history restores older valid measurement after incomplete result",
                        "category export and open commands select their own result", "measurement navigation never starts a test",
                        "loss-only ping preserves the last valid RTT and jitter", "measurement recommendation opens its original persisted session",
                        "imported ETL description does not invent workload context" },
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
        VerifyCaptureViewport();
        VerifyImportedEtwContext();
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
        Require(backend.AuditCalls == 0 && backend.LabCalls == 0, "Startup must not initiate an audit or benchmark.");
        vm.SelectedTest = TestCatalog.Get("ping");
        Require(vm.VisibleSections.Select(s => s.Id).SequenceEqual(new[] { "dashboard", "windows", "nvidia", "network", "devices", "latency", "hardware", "games", "ajustes", "avancado" }), "Navigation differs from the ten intended category destinations.");
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
        Require(vm.NetworkSettings.Count == 1 && !vm.NetworkSettings[0].CanChange, "Unsupported driver property appears writable.");
        Require(vm.NetworkReadStatus.Contains("Leitura concluída"), "Network read lacks feedback.");
        window.UpdateLayout();
        var adapterSelector = Descendants<ComboBox>(window).Single(c => ReferenceEquals(c.ItemsSource, vm.NetworkAdapters));
        Require(Descendants<TextBlock>(adapterSelector).Any(t => t.Text == vm.SelectedAdapter!.Name), "Adapter selector must show a readable name.");
        var diagnostics = new StartupDiagnostics(["--smoke-test", "--smoke-output", "artifacts/ui-flow-screenshot.json"]);
        diagnostics.CaptureView((FrameworkElement)window.Content, "network-fixture");

        vm.NavigateCommand.Execute("analisar");
        await RunFixture(vm, "general", (progress, ct) => backend.AnalyzePcAsync(new(ObservationSeconds: 1), progress, _ => { }, ct));
        window.UpdateLayout();
        Require(vm.SelectedSection?.Id == "analisar" && vm.HasInlineResult && Descendants<TextBlock>(window).Any(t => t.Text == vm.HomeAuditSummary),
            "General result must be rendered under the analysis action without forced navigation.");
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

        backend.AuditMode = "complete";
        vm.NavigateCommand.Execute("dashboard"); vm.AuditConfigurationCommand.Execute(null); await Until(() => !vm.IsBusy);
        window.UpdateLayout();
        Require(backend.AuditCalls == 1 && vm.HasAuditReport && vm.HasInlineResult && vm.SelectedSection?.Id == "dashboard" && !vm.HasSimpleReport,
            "Audit must use its command and display an independent report on the initiating page.");
        Require(vm.AuditCategories.Count == 2 && vm.AuditCoverage.Contains("1 de 2 áreas") && vm.AuditHighlights.Count == 0 && vm.AuditRecommendations.Count == 0,
            "Audit coverage must distinguish available observations from unavailable areas and avoid artificial recommendations.");
        Require(vm.ExecutionState == "Resultado parcial" && vm.SimpleReportState.Contains("parcial") && vm.CanExportTest,
            "Incomplete audit must remain visibly partial while allowing export of the evidence obtained.");
        var auditDetails = Descendants<Expander>(window).Single(e => Equals(e.Header, "Detalhes técnicos da análise"));
        Require(!auditDetails.IsExpanded, "The audit must begin with a short summary and collapsed technical details.");
        auditDetails.IsExpanded = true; window.UpdateLayout();
        Require(Descendants<TextBlock>(auditDetails).Any(t => t.Text == vm.AuditCoverage), "Audit coverage is not reachable from the dashboard's technical details.");
        auditDetails.IsExpanded = false; window.UpdateLayout();
        diagnostics.CaptureView((FrameworkElement)window.Content, "audit-fixture");
        var auditId = vm.RecentAnalyses.First().Id;
        var audit = await backend.LoadTestAsync(auditId);
        Require(audit?.Result?.Audit?.Assessments.Count == 2 && audit.Result.Audit.Assessments[0].Observation.RawValue == "0x26 (38)",
            "Audit evidence did not survive SQLite persistence.");
        vm.NavigateCommand.Execute("ajustes"); window.UpdateLayout();
        Require(vm.PageTitle == "Recomendações" && !Descendants<Button>(window).Any(b => b.Content is string label &&
            (label.Contains("Aplicar", StringComparison.OrdinalIgnoreCase) || label.Contains("Restaurar", StringComparison.OrdinalIgnoreCase) || label.Contains("Revisar alteração", StringComparison.OrdinalIgnoreCase))),
            "Consultation still exposes a Windows write/restore action.");
        vm.NavigateCommand.Execute("windows"); window.UpdateLayout();
        Require(vm.FilteredAuditSettings.Any(), "The Windows category did not expose its audit rows.");
        Require(Descendants<AuditSettingRow>(window).Any(), "Audit rows were not rendered in the category.");
        Require(typeof(MainViewModel).GetProperties().All(p => p.Name is not ("PowerPreviewCommand" or "NetworkPreviewCommand" or "RestoreCommand")),
            "Write commands remain exposed by the view model.");
        foreach (var size in new[] { new Size(1366, 768), new Size(1920, 1080) })
        {
            content.Width = size.Width; content.Height = size.Height;
            content.Measure(size); content.Arrange(new Rect(new Point(), size));
            Require(Math.Abs(content.ActualWidth - size.Width) < 1 && Math.Abs(content.ActualHeight - size.Height) < 1
                && Descendants<AuditSettingRow>(content).Any(), "Audit report failed to render at " + size);
            diagnostics.CaptureView(content, "audit-" + size.Width);
        }
        content.Width = double.NaN; content.Height = double.NaN;
        window.InvalidateMeasure(); window.UpdateLayout();
        backend.AuditMode = "cancel"; vm.AuditConfigurationCommand.Execute(null); await Until(() => vm.TestIsActive);
        Require(!vm.HasAuditReport && !vm.HasAuditSettings && !vm.HasInlineResult && vm.AuditCategories.Count == 0 && vm.PriorityAuditSettings.Count == 0,
            "A new audit displayed conclusions from the previous execution while still running.");
        vm.CancelCommand.Execute(null); await Until(() => !vm.IsBusy);
        Require(vm.ExecutionState == "Cancelado" && !vm.HasAuditReport && !vm.HasAuditSettings && vm.AuditCategories.Count == 0 && vm.AuditRecommendations.Count == 0 && vm.HasInlineResult,
            "Cancelled audit reused old conclusions.");
        var previousAuditCalls = backend.AuditCalls;
        backend.AuditMode = "partial-cancel"; vm.AuditConfigurationCommand.Execute(null);
        // Preparing is already active, but cancellation there legitimately returns no evidence.
        // This scenario requires the current backend invocation to reach its evidence-reading stage.
        await Until(() => backend.AuditCalls == previousAuditCalls + 1 && vm.TestIsActive
            && vm.ExecutionStage == "Fixture: lendo configurações");
        vm.CancelCommand.Execute(null); await Until(() => !vm.IsBusy);
        Require(vm.ExecutionState == "Cancelado" && vm.HasAuditReport && vm.HasAuditSettings && vm.CanExportTest && vm.SimpleReportState.Contains("cancelado")
            && vm.AuditCategories.SelectMany(c => c.Assessments).Single(a => a.Assessment.Observation.Status == Availability.Available).RawValue == "0x2A (42)",
            "Cancelled audit must retain only evidence returned by the current execution and show its cancelled state. "
            + $"State={vm.ExecutionState}; Report={vm.HasAuditReport}; Export={vm.CanExportTest}; Label={vm.SimpleReportState}");
        var cancelledAudit = await backend.LoadTestAsync(vm.RecentAnalyses.First().Id);
        Require(cancelledAudit?.Status == TestStatus.Cancelled && cancelledAudit.Result?.Audit?.Assessments[0].Observation.RawValue == "0x2A (42)",
            "New evidence from cancelled audit was lost from SQLite.");
        backend.AuditMode = "failure"; vm.AuditConfigurationCommand.Execute(null); await Until(() => !vm.IsBusy);
        Require(vm.LastDiagnosticState == "Falhou" && vm.SimpleReportSummary.Contains("fixture audit denied") && !vm.HasAuditReport && !vm.HasAuditSettings,
            "Failed audit hid its error or reused old conclusions.");
        vm.SelectedRun = vm.TestHistory.Single(r => r.Id == auditId); vm.OpenTestCommand.Execute(null); await Until(() => !vm.IsBusy);
        Require(vm.HasAuditReport && vm.AuditCategories.Count == 2 && vm.LastDiagnosticState == "Falhou",
            "Opening saved audit lost evidence or replaced the latest failed attempt.");
        await VerifyAuditExperienceAsync(window, vm, backend, diagnostics);
        await VerifyConsultationAsync(window, vm, backend);
        await VerifyMeasurementHistoryAsync();
        vm.OpenInvestigationCommand.Execute("testes"); window.UpdateLayout();
        Require(vm.MemoryAccessCommand.CanExecute(null) && Descendants<Button>(window).Any(b => ReferenceEquals(b.Command, vm.MemoryAccessCommand))
            && backend.LabCalls == 0, "Memory access benchmark needs an explicit discoverable action and must not run while navigating.");
    }
    private static async Task VerifyAuditExperienceAsync(MainWindow window, MainViewModel vm, ScenarioBackend backend, StartupDiagnostics diagnostics)
    {
        vm.NavigateCommand.Execute("dashboard");
        var confirmations = _confirmationCalls;
        window.UpdateLayout();
        var primary = Descendants<Button>(window).Single(b => ReferenceEquals(b.Command, vm.AnalyzePcCommand));
        Require(window.FontSize >= 14 && Descendants<TextBlock>(primary).Any(t => t.Text == "ANALISAR MEU PC" && t.FontSize >= 14 && Contrast(t.Foreground, primary.Background) >= 4.5),
            "The main action must render its label with readable contrast.");
        vm.IncludePing = true; vm.IncludeLatency = true;
        vm.AnalyzePcCommand.Execute(null);
        await Until(() => !vm.IsBusy);
        Require(_confirmationCalls == confirmations && vm.HasSimpleReport && vm.SelectedSection?.Id == "dashboard"
            && backend.LastAnalysisOptions is { IncludePing: false, IncludeLatency: false, Gaming: false },
            "The passive general analysis must start with one click, finish on its initiating page and not request active-test confirmation.");
        vm.IncludePing = false; vm.IncludeLatency = false;

        var report = ExperienceFixture();
        await RunFixture(vm, "audit.configuration", (_, _) => Task.FromResult(new TechnicalResult("Fixture de experiência", [report.Summary], Audit: report, Partial: true)));
        Require(vm.PriorityAuditSettings.Count <= 5 && vm.PriorityAuditSettings.All(r => r.IsImprovement && r.Name != "Microsoft Kernel Debug Network Adapter" && !r.Name.Contains("GPU Priority")),
            "Priority recommendations must be bounded and must not promote disabled devices or ignored MMCSS fields.");
        Require(vm.PriorityAuditSettings.Any(), "The fixture's supported hypotheses disappeared from the priority summary.");
        foreach (var category in vm.VisibleSections.ToArray())
        {
            vm.NavigateCommand.Execute(category.Id); window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(vm.SelectedSection?.Id == category.Id && vm.PageTitle == category.Name, "Category navigation lost selection: " + category.Id);
        }
        vm.NavigateCommand.Execute("windows");
        vm.AuditSearch = "";
        vm.SelectedAuditFilter = vm.AuditFilters.First();
        vm.SelectedAuditSubcategory = vm.AuditSubcategories.First();
        window.UpdateLayout();
        Require(vm.FilteredAuditSettings.Count > 1000, "Large fixture did not reach the real category list.");
        var list = Descendants<ListBox>(window).Single(l => l.Name == "AuditSettingsItems");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var realized = Descendants<ListBoxItem>(list).Count();
        Require(VirtualizingPanel.GetIsVirtualizing(list) && realized > 0 && realized < 100 && list.Items.Count > 1000,
            $"The audit list must virtualize real containers. Total={list.Items.Count}; realized={realized}.");
        list.ScrollIntoView(list.Items[^1]); window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        Require(list.ItemContainerGenerator.ContainerFromIndex(list.Items.Count - 1) is ListBoxItem,
            "Virtualized rows cannot be reached by scrolling.");
        Require(Descendants<ListBoxItem>(list).Count() < 100, "Scrolling materialized the entire audit list.");

        vm.AuditSearch = "Energy fixture";
        window.UpdateLayout();
        Require(vm.FilteredAuditSettings.Count == 1 && vm.FilteredAuditSettings[0].Name == "Energy fixture",
            "Search must filter actual configuration names.");
        var selected = vm.FilteredAuditSettings[0];
        list.SelectedItem = selected;
        list.ScrollIntoView(selected); window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var row = Descendants<AuditSettingRow>(list).Single(r => ReferenceEquals(r.DataContext, selected));
        var texts = Descendants<TextBlock>(row).Select(t => t.Text).ToArray();
        Require(texts.Contains(selected.CurrentValue) && texts.Contains(selected.RecommendedValue) && texts.Contains(selected.StatusLabel)
            && !string.IsNullOrWhiteSpace(selected.ShortReason) && texts.Contains(selected.ShortReason)
            && texts.All(t => !t.Contains("HKEY_", StringComparison.OrdinalIgnoreCase) && !t.Contains("5d9ec6fe-3e84-4fe1-bf8e-73b371bef067", StringComparison.OrdinalIgnoreCase)),
            "Compact rows must show current/recommended/status and keep original registry paths and identities in details.");
        var details = Descendants<Button>(row).Single(b => b.Name == "SettingDetailsButton");
        details.BringIntoView(); details.Focus();
        Require(details.IsKeyboardFocused, "The setting details action must be reachable by keyboard before opening the modal.");
        Exception? modalFailure = null;
        _ = window.Dispatcher.BeginInvoke(async () =>
        {
            AuditSettingDialog? dialog = null;
            try
            {
                dialog = Application.Current.Windows.OfType<AuditSettingDialog>().Single();
                Require(ReferenceEquals(dialog.Setting, selected), "The detail modal opened the wrong configuration.");
                var technical = (Expander)dialog.FindName("TechnicalDetailsExpander");
                var close = (Button)dialog.FindName("CloseDetailsButton");
                Require(!technical.IsExpanded && close.IsKeyboardFocused, "Technical details must start collapsed and the modal must receive keyboard focus.");
                Require(dialog.ActualWidth <= 680 && dialog.ActualHeight <= SystemParameters.WorkArea.Height,
                    "The explanation modal exceeds the available work area.");
                foreach (var value in new[] { selected.CurrentValue, selected.RecommendedValue, selected.Reason, selected.Validation })
                    Require(Descendants<TextBlock>(dialog).Any(t => t.Text == value && Contrast(t.Foreground, dialog.Background) >= 4.5),
                        "A key explanation value is unreadable: " + value);
                technical.IsExpanded = true; dialog.UpdateLayout();
                Require(Descendants<TextBox>(dialog).Any(t => t.IsReadOnly && t.Text.Contains("HKEY_LOCAL_MACHINE", StringComparison.Ordinal)),
                    "The original registry evidence is no longer accessible in technical details.");
                technical.IsExpanded = false;
                await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                dialog.UpdateLayout();
                diagnostics.CaptureView((FrameworkElement)dialog.Content, "setting-modal");
                dialog.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(dialog), Environment.TickCount, Key.Escape)
                    { RoutedEvent = Keyboard.PreviewKeyDownEvent });
                Require(!dialog.IsVisible, "Escape did not close the setting explanation.");
            }
            catch (Exception error) { modalFailure = error; }
            finally { if (dialog?.IsVisible == true) dialog.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        vm.OpenAuditSettingCommand.Execute(selected);
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        if (modalFailure is not null) throw new InvalidOperationException("Modal fixture failed.", modalFailure);
        Require(vm.SelectedSection?.Id == "windows" && vm.AuditSearch == "Energy fixture" && ReferenceEquals(list.SelectedItem, selected)
            && details.IsKeyboardFocused, "Closing details lost the category, search, selected row or keyboard focus.");
        vm.NavigateCommand.Execute("network");
        Require(vm.AuditSearch.Length == 0, "The Windows search leaked into a different category.");
        vm.NavigateCommand.Execute("windows");
        Require(vm.AuditSearch == "Energy fixture" && vm.FilteredAuditSettings.Count == 1,
            "Returning to a category lost its saved search and filtered settings.");

        vm.AuditSearch = "";
        vm.SelectedAuditFilter = vm.AuditFilters.First();
        var allWindowsRows = vm.FilteredAuditSettings.ToArray();
        foreach (var filter in vm.AuditFilters)
        {
            vm.SelectedAuditFilter = filter;
            var rows = vm.FilteredAuditSettings;
            var expected = allWindowsRows.Where(r => filter switch
            {
                "Melhorias possíveis" => r.IsImprovement,
                "Vale testar" => r.IsComparison,
                "Adequadas" => r.IsAdequate,
                "Não avaliadas" => r.IsUnassessed,
                _ => true
            }).Select(r => r.Key).Order().ToArray();
            Require(rows.Select(r => r.Key).Order().SequenceEqual(expected), "A category filter lost or included incompatible rows: " + filter);
        }
        Require(allWindowsRows.Any(r => r.IsImprovement) && allWindowsRows.Any(r => r.IsAdequate) && allWindowsRows.Any(r => r.IsUnassessed),
            "Fixture must exercise supported investigation, justified keep and unassessed rows.");
        Require(allWindowsRows.Where(r => r.Name.StartsWith("Background fixture", StringComparison.Ordinal)).All(r =>
            !r.IsImprovement && !r.IsComparison && r.RecommendedValue == "Sem recomendação fundamentada"),
            "Unknown properties became optimization recommendations merely because they were present.");
        vm.SelectedAuditFilter = vm.AuditFilters.First();
        vm.SelectedAuditSubcategory = "Energia";
        Require(vm.FilteredAuditSettings.Count == 1 && vm.FilteredAuditSettings[0].Subcategory == "Energia", "Subcategory selection did not restrict the list.");
        vm.SelectedAuditSubcategory = vm.AuditSubcategories.First();
        vm.AuditSort = "Nome";
        Require(vm.FilteredAuditSettings.Select(r => r.Name).SequenceEqual(vm.FilteredAuditSettings.Select(r => r.Name).Order()),
            "Name ordering did not sort the actual category list.");
        vm.AuditSort = "Prioridade";
        Require(vm.FilteredAuditSettings.Select(r => r.Priority).SequenceEqual(vm.FilteredAuditSettings.Select(r => r.Priority).OrderDescending()),
            "Priority ordering did not sort the actual category list.");

        var content = (FrameworkElement)window.Content;
        foreach (var pixels in new[] { new Size(1366, 768), new Size(1920, 1080) })
        foreach (var scale in new[] { 1d, 1.25d, 1.5d })
        {
            // A virtual DIP viewport checks layout pressure at the requested scale. This is
            // a simulation, not a physical monitor DPI change or a per-monitor DPI assertion.
            var viewport = new Size(pixels.Width / scale, pixels.Height / scale);
            content.Width = viewport.Width; content.Height = viewport.Height;
            content.Measure(viewport); content.Arrange(new Rect(new Point(), viewport));
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            content.UpdateLayout();
            Require(Math.Abs(content.ActualWidth - viewport.Width) < 1 && Math.Abs(content.ActualHeight - viewport.Height) < 1,
                $"Simulated viewport failed: {pixels}, scale={scale}.");
            Require(Descendants<AuditSettingRow>(content).Any() && Descendants<Button>(content).Where(b => b.Name == "SettingDetailsButton").All(b => b.ActualWidth >= 50),
                $"Compact audit rows lost usable detail controls at {pixels}, scale={scale}.");
            foreach (var visibleRow in Descendants<AuditSettingRow>(content).Where(r => r.DataContext is AuditSettingPresentation))
            {
                var model = (AuditSettingPresentation)visibleRow.DataContext;
                foreach (var value in new[] { model.Name, model.CurrentValue, model.RecommendedValue })
                    Require(Descendants<TextBlock>(visibleRow).Any(t => t.Text == value && t.ActualWidth > 0 && Contrast(t.Foreground, (Brush)window.FindResource("Background")) >= 4.5),
                        $"An audit row did not finish rendering legible values at {pixels}, scale={scale}: {value}");
            }
            diagnostics.CaptureView(content, $"audit-{pixels.Width}-scale-{scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}-simulated");
        }
        content.Width = double.NaN; content.Height = double.NaN; window.InvalidateMeasure(); window.UpdateLayout();

        vm.NavigateCommand.Execute("network"); window.UpdateLayout();
        var beforeRows = vm.FilteredAuditSettings.Select(r => r.Key).ToArray();
        Require(beforeRows.Length == 1, "Network fixture did not reach its category.");
        backend.Mode = "complete"; confirmations = _confirmationCalls;
        vm.PingCommand.Execute(null); await Until(() => !vm.IsBusy);
        Require(_confirmationCalls == confirmations + 1 && vm.SelectedSection?.Id == "network" && vm.HasCategoryTestResult
            && vm.CategoryTestIndicators.Any(i => i.Reading.StartsWith("1")) && vm.HasAuditSettings
            && beforeRows.SequenceEqual(vm.FilteredAuditSettings.Select(r => r.Key)),
            "A confirmed manual network test must display its result in the category and preserve the audited settings.");
        window.UpdateLayout();
        Require(Descendants<TextBlock>(window).Any(t => t.Text == vm.CategoryTestTitle), "The category result was not bound into its view.");
        diagnostics.CaptureView(content, "network-result-fixture");
        var networkRunId = vm.CategoryTestId;
        Require(networkRunId is not null, "The category result must retain its persisted session identity.");
        vm.NavigateCommand.Execute("hardware");
        await RunFixture(vm, "memory-access", (_, _) => Task.FromResult(new TechnicalResult("Memory category fixture", ["Fixture only; no memory benchmark executed"],
            Indicators: [new("memory.fixture.access", "Acesso fixture", 12, "ns/acesso", "test-only")])));
        Require(vm.SelectedSection?.Id == "hardware" && vm.CategoryTestId != networkRunId && vm.CategoryTestIndicators.Any(i => i.Reading.StartsWith("12")),
            "A memory result must remain in Hardware and have a different session from the network result.");
        vm.NavigateCommand.Execute("windows");
        Require(!vm.HasCategoryTestResult, "Network measurements leaked into the Windows category.");
        vm.NavigateCommand.Execute("network");
        Require(vm.HasCategoryTestResult && vm.CategoryTestId == networkRunId && beforeRows.SequenceEqual(vm.FilteredAuditSettings.Select(r => r.Key)),
            "Navigating away lost the category's measurement or audit rows.");
        window.UpdateLayout();
        var categoryDetails = Descendants<Expander>(window).Single(e => Equals(e.Header, "Limites e detalhes técnicos"));
        categoryDetails.IsExpanded = true; window.UpdateLayout();
        Require(Descendants<Button>(window).Any(b => ReferenceEquals(b.Command, vm.ExportCategoryTestCommand) && b.IsEnabled)
            && Descendants<Button>(categoryDetails).Any(b => ReferenceEquals(b.Command, vm.OpenCategoryTestCommand)),
            "Category actions must bind their own result commands after a different category completed a newer result.");
        vm.OpenCategoryTestCommand.Execute(null);
        Require(vm.SelectedSection?.Id == "resultados" && vm.OperationTitle == "Fixture: rede concluída" && vm.CanExportTest
            && vm.ResultIndicators.Any(i => i.Reading.StartsWith("1")),
            "Opening the network category result must select its own session, even after another category completed a newer test.");
        vm.NavigateCommand.Execute("dashboard"); window.UpdateLayout();
        var retainedDetails = Descendants<Expander>(window).Single(e => Equals(e.Header, "Detalhes técnicos da análise"));
        retainedDetails.IsExpanded = true; window.UpdateLayout();
        Require(vm.SelectedSection?.Id == "dashboard" && vm.HasAuditReport && vm.OperationTitle == "Fixture de experiência",
            "Opening retained audit details must select the audit snapshot, not the most recent network or memory result.");
        retainedDetails.IsExpanded = false;
    }

    private static async Task VerifyConsultationAsync(MainWindow window, MainViewModel vm, ScenarioBackend backend)
    {
        var setting = AuditPresentationBuilder.Build(ExperienceFixture()).Single(r => r.Name == "Energy fixture") with
        { TechnicalDetails = Enumerable.Range(1, 45).Select(i => "Evidence fixture " + i).ToArray() };
        var dialog = new AuditSettingDialog(setting) { Owner = window };
        try
        {
            dialog.Show();
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var technical = (Expander)dialog.FindName("TechnicalDetailsExpander");
            technical.IsExpanded = true; dialog.UpdateLayout();
            var scroll = (ScrollViewer)dialog.FindName("ExplanationScrollViewer");
            var evidence = (ItemsControl)dialog.FindName("TechnicalEvidenceItems");
            var next = (Button)dialog.FindName("NextEvidenceButton");
            var previous = (Button)dialog.FindName("PreviousEvidenceButton");
            var close = (Button)dialog.FindName("CloseDetailsButton");
            Require(Descendants<ScrollViewer>(dialog).Count(s => s.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled) == 1
                && !close.IsDescendantOf(scroll) && evidence is not ListBox,
                "Explanation must have one scrolling area and keep its footer outside it.");
            Require(evidence.Items.Count == 20 && !previous.IsEnabled && next.IsEnabled,
                "First evidence page must contain at most twenty entries with correct navigation state.");
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); dialog.UpdateLayout();
            Require(evidence.Items.Count == 20 && Equals(evidence.Items[0], "Evidence fixture 21") && previous.IsEnabled
                && Descendants<TextBox>(evidence).Any(t => t.IsKeyboardFocused && t.IsReadOnly),
                "Paging must expose the next evidence and move keyboard focus to copyable text.");
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); dialog.UpdateLayout();
            Require(evidence.Items.Count == 5 && Equals(evidence.Items[^1], "Evidence fixture 45") && !next.IsEnabled
                && Descendants<TextBox>(evidence).Any(t => t.IsKeyboardFocused),
                "Last evidence page lost items or left focus on a disabled navigation control.");
            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await dialog.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Require(evidence.Items.Count == 20 && Equals(evidence.Items[0], "Evidence fixture 21"), "Previous page did not restore its evidence.");
            close.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!dialog.IsVisible, "Fixed footer did not close the explanation.");
        }
        finally { if (dialog.IsVisible) dialog.Close(); }

        var dpcCalls = backend.DpcCalls;
        var labCalls = backend.LabCalls;
        var pingCalls = backend.PingCalls;
        vm.InvestigatePerformanceCommand.Execute(null); window.UpdateLayout();
        Require(vm.SelectedSection?.Id == "latency" && backend.DpcCalls == dpcCalls && backend.LabCalls == labCalls && backend.PingCalls == pingCalls,
            "The investigation action must open a choice without running a capture or benchmark.");
        Require(Descendants<ComboBox>(window).Any(c => ReferenceEquals(c.ItemsSource, vm.LatencyDurations))
            && Descendants<ComboBox>(window).Any(c => ReferenceEquals(c.ItemsSource, vm.LatencyGoals)),
            "Capture duration and context must be discoverable in the investigation view.");
        vm.Workload = "Jogo fixture; cena A";
        foreach (var (goal, seconds) in new[] { ("Observação rápida", 30), ("Durante o jogo", 60), ("Reproduzir travadas esporádicas", 120) })
        {
            vm.LatencyGoal = goal;
            Require(vm.LatencySeconds == seconds && vm.LatencyCaptureLabel.Contains(seconds.ToString()), "Latency goal did not select its suggested duration.");
            var confirmations = _confirmationCalls;
            vm.CaptureDpcCommand.Execute(null); await Until(() => !vm.IsBusy);
            Require(_confirmationCalls == confirmations + 1 && _confirmationText.Contains(seconds + " segundos")
                && _confirmationText.Contains("Jogo fixture; cena A") && backend.LastDpcSeconds == seconds
                && backend.LastDpcContext?.Contains(goal) == true && vm.SelectedSection?.Id == "latency",
                "A manual DPC capture lost its explicit confirmation, selected duration, workload, or initiating category.");
        }
        vm.LatencySeconds = 17;
        Require(vm.LatencySeconds == 120, "Unsupported DPC durations must be rejected.");
        window.UpdateLayout();
        Require(vm.CategoryInterpretationStatus == "Sem anormalidade demonstrada"
            && Descendants<TextBlock>(window).Any(t => t.Text == vm.CategoryTestSummary)
            && Descendants<TextBlock>(window).Any(t => t.Text == vm.CategoryNextStep),
            "The category must render the measurement interpretation and its next step.");
        var dpc = vm.HomeMeasurements.Single(r => r.Name == "DPC máximo");
        Require(dpc.Reading.StartsWith("120") && dpc.Context.Contains("Jogo fixture; cena A"),
            "Home must identify the last measured DPC value and its original context.");
        vm.NavigateCommand.Execute("dashboard"); window.UpdateLayout();
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        Require(Descendants<TextBlock>(window).Any(t => t.Text.Contains("DPC máximo") && t.Text.Contains(dpc.Reading))
            && Descendants<TextBlock>(window).Any(t => t.Text == dpc.Context)
            && Descendants<TextBlock>(window).Any(t => t.Text == dpc.Interpretation),
            "Home must render the valid measurement, capture context and interpretation together.");
        vm.NavigateCommand.Execute("latency"); window.UpdateLayout();
        dpcCalls = backend.DpcCalls;
        _acceptConfirmation = false;
        try { vm.CaptureDpcCommand.Execute(null); await Until(() => !vm.IsBusy); }
        finally { _acceptConfirmation = true; }
        Require(backend.DpcCalls == dpcCalls, "Declining the DPC confirmation still invoked the capture backend.");
        await RunFixture(vm, "dpc", (_, _) => Task.FromException<TechnicalResult>(new IOException("Fixture capture failed")));
        Require(vm.CategoryResultState == "Falhou" && vm.HomeMeasurements.Single(r => r.Name == "DPC máximo") == dpc,
            "A failed capture must remain visible as the latest attempt without erasing the last valid measurement.");
        foreach (var (test, category) in new[] { ("ping", "network"), ("frames", "games"), ("memory-access", "hardware"), ("dpc", "latency") })
        {
            vm.ChooseMeasurementCommand.Execute(test);
            Require(vm.SelectedSection?.Id == category && backend.DpcCalls == dpcCalls && backend.LabCalls == labCalls && backend.PingCalls == pingCalls && !vm.IsBusy,
                "Choosing a measurement must navigate without executing it: " + test);
        }

        vm.NavigateCommand.Execute("network");
        await RunFixture(vm, "ping", (_, _) => Task.FromResult(new TechnicalResult("Valid RTT and jitter fixture", [],
            Indicators: [new("network.ping.rtt.ms", "RTT", 8, "ms", "Fixture only"),
                new("network.ping.jitter.ms", "Variação", 2, "ms", "Fixture only")], SelectionContext: "Valid fixture route")));
        var rtt = vm.HomeMeasurements.Single(r => r.Name == "RTT médio");
        var jitter = vm.HomeMeasurements.Single(r => r.Name == "Variação do RTT");
        await RunFixture(vm, "ping", (_, _) => Task.FromResult(LossOnlyFixture()));
        var lossId = vm.CategoryTestId;
        Require(lossId is not null && vm.HomeMeasurements.Single(r => r.Name == "RTT médio") == rtt
            && vm.HomeMeasurements.Single(r => r.Name == "Variação do RTT") == jitter,
            "A later ping with measured loss but no RTT/jitter must not erase those independent valid indicators.");
        vm.NavigateCommand.Execute("hardware");
        await RunFixture(vm, "memory-access", (_, _) => Task.FromResult(new TechnicalResult("Newer memory fixture", [],
            Indicators: [new("memory.fixture.access", "Memory", 12, "ns/acesso", "Fixture only")])));
        vm.NavigateCommand.Execute("dashboard"); window.UpdateLayout();
        var conclusion = vm.HomeMeasurementConclusions.Single(r => r.Title == "Sem respostas ICMP na fixture");
        Require(conclusion.Action.Id == "measurement:" + lossId && vm.HomeMeasurementConclusions.Count + vm.PriorityAuditSettings.Count <= 5,
            "Measurement priorities must retain their source session and keep the combined home list within five items.");
        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        var conclusionButton = Descendants<Button>(window).Single(b => ReferenceEquals(b.Command, vm.SuggestedActionCommand)
            && b.CommandParameter is SuggestedNextAction action && action.Id == conclusion.Action.Id);
        Require(Descendants<TextBlock>(window).Any(t => t.Text == conclusion.Title), "The measurement conclusion did not render on Home.");
        conclusionButton.Command.Execute(conclusionButton.CommandParameter);
        var opened = (TestSession?)typeof(MainViewModel).GetField("_openedTest", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(vm);
        Require(vm.SelectedSection?.Id == "resultados" && opened?.Id == lossId && vm.OperationTitle == "No ICMP replies fixture",
            "A measurement recommendation opened a different session after a newer test completed.");
    }

    private static TechnicalResult LossOnlyFixture() => new("No ICMP replies fixture", ["Fixture only; no packets sent"],
        Indicators: [new("network.ping.rtt.ms", "RTT", null, "ms", "No replies"),
            new("network.ping.jitter.ms", "Variação", null, "ms", "No replies"), new("network.ping.loss.percent", "Perda", 100, "%", "Fixture only")],
        Findings: [new("fixture.loss", "Rede", "Sem respostas ICMP na fixture", "O destino não respondeu nesta janela sintética.",
            FindingCategory.PossibleProblem, Severity.Warning, Confidence.Medium, [], "Confira o destino e compare outra janela.", "Fixture only", "Nenhuma alteração")],
        SelectionContext: "Loss fixture route");

    private static async Task VerifyMeasurementHistoryAsync()
    {
        var backend = new ScenarioBackend();
        backend.InventoryReady.SetResult();
        var now = DateTimeOffset.UtcNow;
        var valid = new TechnicalResult("Older measured RTT", [],
            Indicators: [new("network.ping.rtt.ms", "RTT", 8, "ms", "Fixture only"),
                new("network.ping.jitter.ms", "Variação", 2, "ms", "Fixture only")], SelectionContext: "Valid fixture route");
        await backend.SaveTestAsync(new(Guid.NewGuid(), "ping", "Valid fixture", TestStatus.Completed,
            now.AddMinutes(-5), now.AddMinutes(-4), new("Completed"), valid));
        await backend.SaveTestAsync(new(Guid.NewGuid(), "ping", "Unreadable newer fixture", TestStatus.Completed,
            now.AddMinutes(-2), now.AddMinutes(-1), new("Completed"), new TechnicalResult("No finite measurement", [],
                Indicators: [new("network.ping.rtt.ms", "RTT", null, "ms", "No reading")], SelectionContext: "New unavailable route")));
        await backend.SaveTestAsync(new(Guid.NewGuid(), "ping", "Loss-only newer fixture", TestStatus.Completed,
            now.AddSeconds(-30), now.AddSeconds(-10), new("Completed"), LossOnlyFixture()));
        var vm = new MainViewModel(backend, confirmation: (_, _) => throw new InvalidOperationException("History must not confirm a new test."));
        try
        {
            await vm.InitializeAsync();
            var reading = vm.HomeMeasurements.Single(r => r.Name == "RTT médio");
            Require(reading.Reading.StartsWith("8") && reading.Context.Contains("Valid fixture route") && backend.DpcCalls == 0,
                "History restoration must look past a newer result without measurements and retain the original context of the last valid result.");
            var jitter = vm.HomeMeasurements.Single(r => r.Name == "Variação do RTT");
            Require(jitter.Reading.StartsWith("2") && jitter.Context.Contains("Valid fixture route")
                && vm.HomeMeasurementConclusions.Any(r => r.Title == "Sem respostas ICMP na fixture"),
                "History must restore each valid indicator while keeping the useful finding from a newer loss-only measurement.");
        }
        finally { await vm.DisposeAsync(); }
    }

    private static PerformanceAuditReport ExperienceFixture()
    {
        var rows = new List<AuditAssessment>();
        void Add(string id, string instance, string name, AuditClassification classification, Availability availability = Availability.Available,
            AuditSupport support = AuditSupport.Conditional, IReadOnlyDictionary<string, string>? facts = null)
        {
            var rule = new AuditRule(id, "Fixture category", name, "Fixture only", "Fixture", "Fixture", "Fixture evidence",
                "No measured gain", support, "Test only; no hardware accessed", "Compare equivalent observations", ["https://example.test/fixture"]);
            var observation = new AuditObservation(id, instance, name, "Fixture scope", availability == Availability.Available ? facts?.GetValueOrDefault("decimal") ?? "8" : null,
                availability, "Fixture source", facts ?? new Dictionary<string, string>());
            rows.Add(new(observation, rule, classification, "Fixture interpretation", "Fixture rationale", "No gain demonstrated",
                Confidence.Medium, "Fixture risk", "Fixture validation"));
        }
        for (var i = 0; i < 1100; i++) Add("windows.fixture.bulk", "bulk-" + i, "Background fixture " + i, AuditClassification.InsufficientData);
        Add("windows.power.policy", "5d9ec6fe-3e84-4fe1-bf8e-73b371bef067", "Energy fixture", AuditClassification.ComparisonOpportunity,
            facts: new Dictionary<string, string> { ["registryPath"] = @"HKEY_LOCAL_MACHINE\Software\Fixture", ["decimal"] = "8" });
        Add("windows.scheduler.separation", "healthy", "Healthy fixture", AuditClassification.Adequate,
            facts: new Dictionary<string, string> { ["decimal"] = "42", ["registryType"] = "DWord" });
        Add("windows.mmcss.responsiveness", "mmcss-disabled", "MMCSS disabled fixture", AuditClassification.AppliedUnproven,
            facts: new Dictionary<string, string> { ["decimal"] = "100", ["registryType"] = "DWord" });
        Add("windows.fixture.unavailable", "missing", "Unavailable fixture", AuditClassification.Unsupported, Availability.Unsupported);
        Add("windows.mmcss.gpu", "ignored", "GPU Priority", AuditClassification.NoDocumentedEffect, support: AuditSupport.Ignored);
        Add("devices.identity", @"ROOT\FIXTURE\0", "Microsoft Kernel Debug Network Adapter", AuditClassification.PossibleProblem,
            facts: new Dictionary<string, string> { ["pnpProblemCode"] = "22", ["class"] = "Net" });
        Add("network.advanced", "network-fixture", "Interrupt Moderation", AuditClassification.ComparisonOpportunity,
            facts: new Dictionary<string, string> { ["DisplayValue"] = "Enabled" });
        Add("nvidia.fixture", "nvidia-fixture", "Perfil global fixture", AuditClassification.AppliedUnproven);
        Add("memory.modules", "memory-fixture", "Módulo fixture", AuditClassification.AppliedUnproven,
            facts: new Dictionary<string, string> { ["Capacity"] = "8589934592", ["ConfiguredTransferRateMTs"] = "2933" });
        Add("nvidia.fixture", "game-fixture", "VALORANT fixture", AuditClassification.AppliedUnproven);
        return new(DateTimeOffset.UtcNow, "Dados sintéticos somente para validar a interface.", rows, ["Fixture; no hardware accessed"]);
    }

    private static void VerifyCaptureViewport()
    {
        var canvas = new Canvas { Width = 120, Height = 100 };
        var panel = new Border { Width = 64, Height = 48, Background = Brushes.Red };
        canvas.Children.Add(panel);
        foreach (var offset in new[] { new Point(17, 13), new Point(-9, -7) })
        {
            Canvas.SetLeft(panel, offset.X); Canvas.SetTop(panel, offset.Y);
            canvas.Measure(new Size(120, 100)); canvas.Arrange(new Rect(0, 0, 120, 100)); canvas.UpdateLayout();
            new StartupDiagnostics(["--smoke-output", "artifacts/ui-capture.json"]).CaptureView(panel, "offset");
            using var file = File.OpenRead("artifacts/ui-capture-offset.png");
            var frame = BitmapDecoder.Create(file, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            var pixels = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            Require(pixels.PixelWidth == 64 && pixels.PixelHeight == 48, "Capture changed the element viewport size.");
            foreach (var point in new[] { new Point(0, 0), new Point(63, 0), new Point(0, 47), new Point(63, 47) })
            {
                var pixel = new byte[4];
                pixels.CopyPixels(new Int32Rect((int)point.X, (int)point.Y, 1, 1), pixel, 4, 0);
                Require(pixel.SequenceEqual(new byte[] { 0, 0, 255, 255 }),
                    $"Capture clipped an edge at {point} when the visual had layout offset {offset}; BGRA={string.Join(',', pixel)}.");
            }
        }
    }

    private static void VerifyImportedEtwContext()
    {
        var started = DateTimeOffset.Parse("2026-10-21T12:00:00+00:00");
        var aggregate = new LatencyAggregator();
        aggregate.ObserveKernelImage(4, 0x1000, 100, @"C:\Windows\System32\drivers\fixture.sys");
        for (var i = 0; i < 100; i++)
        {
            var time = i * 29_999d / 99;
            aggregate.ObserveLatency("DPC", 0, 0x1010, i + 1, time, started.AddMilliseconds(time));
            aggregate.ObserveLatency("ISR", 1, 0x1010, (i + 1) / 2d, time, started.AddMilliseconds(time));
        }
        var summaries = aggregate.Snapshot();
        var timeline = aggregate.TimelineSnapshot();
        var capture = new EtwCaptureResult(Availability.Available, "Imported ETL fixture", started, TimeSpan.FromSeconds(30), null,
            200, 0, false, false, summaries.Cpu, summaries.Modules, summaries.Limitations, timeline.Events, timeline.Truncated,
            TraceStartUtc: started, TimeBuckets: timeline.Buckets, EventCounts: aggregate.EventCounts(200));
        var describe = typeof(ApplicationBackend).GetMethod("DescribeEtw", BindingFlags.NonPublic | BindingFlags.Static)!;
        const string description = "Captura ETL importada; contexto de carga não confirmado.";
        var imported = (TechnicalResult)describe.Invoke(null, [capture, description, null, null, false])!;
        var known = (TechnicalResult)describe.Invoke(null, [capture, "Jogo fixture; cena A", null, 30, true])!;
        Require(imported.InterpretationStatus == "Inconclusivo" && imported.SelectionContext == description
            && imported.Findings?.Any(f => f.Category == FindingCategory.NoAnomaly) == false,
            "An import description must remain provenance text and must not become confirmed workload context.");
        Require(known.InterpretationStatus == "Sem anomalia relevante demonstrada na janela"
            && known.Findings?.Any(f => f.Category == FindingCategory.NoAnomaly) == true,
            "Explicit workload context was lost from an otherwise complete distributed capture.");
        var repeated = (TechnicalResult)describe.Invoke(null,
            [capture with { Duration = TimeSpan.FromMilliseconds(30_300) }, "Jogo fixture; cena A", null, 30, true])!;
        Require(known.ComparisonKey == repeated.ComparisonKey && imported.ComparisonKey != known.ComparisonKey,
            "Equivalent requested durations must remain comparable despite small measured timing differences; imported captures retain separate context.");
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
    private static double Contrast(Brush foreground, Brush background)
    {
        if (foreground is not SolidColorBrush f || background is not SolidColorBrush b) return 0;
        static double Channel(byte value) { var s = value / 255d; return s <= .04045 ? s / 12.92 : Math.Pow((s + .055) / 1.055, 2.4); }
        static double Light(Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        var x = Light(f.Color); var y = Light(b.Color);
        return (Math.Max(x, y) + .05) / (Math.Min(x, y) + .05);
    }
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
    public string AuditMode { get; set; } = "complete";
    public DiagnosticOptions? LastAnalysisOptions { get; private set; }
    public int AuditCalls { get; private set; }
    public int LabCalls { get; private set; }
    public int DpcCalls { get; private set; }
    public int PingCalls { get; private set; }
    public int LastDpcSeconds { get; private set; }
    public string? LastDpcContext { get; private set; }
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
    public async Task<TechnicalResult> AuditConfigurationAsync(IProgress<TestProgress> progress, CancellationToken ct)
    {
        AuditCalls++;
        progress.Report(new("Fixture: lendo configurações", .5));
        if (AuditMode == "cancel") await Task.Delay(Timeout.Infinite, ct);
        if (AuditMode == "partial-cancel")
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        }
        if (AuditMode == "failure") throw new UnauthorizedAccessException("fixture audit denied");
        var now = DateTimeOffset.UtcNow;
        AuditRule rule = new("fixture.registry", "Windows", "Agendamento fixture", "Fixture read", "Fixture", "Fixture only",
            "Snapshot", "Sem benefício medido", AuditSupport.Documented, "Fixture; nenhum acesso ao Registro", "Comparar medições equivalentes", ["https://example.test/fixture"]);
        var unavailable = rule with { Id = "fixture.nvidia", Category = "GPU NVIDIA", Name = "Perfil fixture" };
        var report = PerformanceAuditEngine.Evaluate([rule, unavailable],
            [new(rule.Id, "fixture", rule.Name, "Fixture scope", AuditMode == "partial-cancel" ? "0x2A (42)" : "0x26 (38)", Availability.Available, "Test-only backend", new Dictionary<string, string> { ["decimal"] = AuditMode == "partial-cancel" ? "42" : "38" }),
             new(unavailable.Id, "fixture", unavailable.Name, "Fixture scope", null, Availability.Unsupported, "Test-only backend", new Dictionary<string, string>(), "Fixture without GPU")], now);
        return new TechnicalResult("Fixture audit", [report.Summary], Audit: report, Partial: true, Cancelled: ct.IsCancellationRequested);
    }
    public Task<PresentMonToolInspection> InspectPresentMonAsync(string path, CancellationToken ct) => Task.FromResult(new PresentMonToolInspection(path, Availability.ToolMissing, null, PresentMonCliStyle.Unsupported, "Fixture; no executable started", []));
    public Task<TechnicalResult> AnalyzePcAsync(DiagnosticOptions options, IProgress<TestProgress> progress, Action<TelemetryBatch> publishBatch, CancellationToken ct)
    {
        LastAnalysisOptions = options;
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
        PingCalls++;
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
    public Task<TechnicalResult> CaptureDpcAsync(int durationSeconds, string? context, CancellationToken ct)
    {
        DpcCalls++; LastDpcSeconds = durationSeconds; LastDpcContext = context;
        return Task.FromResult(new TechnicalResult("DPC / ISR fixture", ["No ETW capture or hardware access occurred"],
            Indicators: [new("dpc.maximum.us", "DPC máximo", 120, "µs", "Fixture only"), new("isr.maximum.us", "ISR máximo", 20, "µs", "Fixture only")],
            SelectionContext: context, Interpretation: "Os eventos desta fixture não demonstram anormalidade.",
            NextStep: "Repita durante o sintoma e compare frametimes equivalentes.", InterpretationStatus: "Sem anormalidade demonstrada"));
    }
    public Task<TechnicalResult> ImportEtlAsync(string path, CancellationToken ct) => throw new NotSupportedException();
    public Task<IReadOnlyList<SelectionItem>> ListPowerPlansAsync(CancellationToken ct) => throw new NotSupportedException();
    public Task<string> PreviewPowerPlanAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    public Task<ChangeRecord> ApplyPowerPlanAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    public Task<string> PreviewNetworkChangeAsync(NetworkSettingView setting, string value, CancellationToken ct) => throw new NotSupportedException();
    public Task<ChangeRecord> ApplyNetworkChangeAsync(NetworkSettingView setting, string value, CancellationToken ct) => throw new NotSupportedException();
    public Task<ChangeRecord> RestoreChangeAsync(Guid id, CancellationToken ct) => throw new NotSupportedException();
    public Task<TechnicalResult> RunLabAsync(string test, string? path, IProgress<double> progress, CancellationToken ct)
    { LabCalls++; throw new NotSupportedException(); }
}
