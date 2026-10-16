using System.Windows;
using PCAnalyzer.App.Services;
using PCAnalyzer.App.ViewModels;
using System.Windows.Threading;
using System.Windows.Controls;

namespace PCAnalyzer.App;

public partial class App : Application
{
    // Only the separate friend test assembly supplies its own window/backend.
    internal bool StartupManagedByTest { get; init; }
    private Mutex? _instanceLock;
    private readonly StartupDiagnostics _startup = new(Environment.GetCommandLineArgs().Skip(1).ToArray());

    public App()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            _startup.ReportFailure(args.ExceptionObject as Exception ?? new InvalidOperationException("Falha fatal sem exceção gerenciada."));
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            _startup.ReportFailure(args.Exception);
            Shutdown(1);
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (StartupManagedByTest) { base.OnStartup(e); return; }
        try
        {
            base.OnStartup(e);
            if (!_startup.IsSmokeTest)
            {
                var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
                _instanceLock = new Mutex(false, "Local\\PCAnalyzer-" + sid);
                var acquired = false;
                try { acquired = _instanceLock.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired)
                {
                    _instanceLock.Dispose(); _instanceLock = null;
                    MessageBox.Show("O PC Analyzer já está aberto nesta conta. Use a janela existente para acompanhar os testes.", "PC Analyzer");
                    Shutdown(); return;
                }
            }
            var backend = new ApplicationBackend();
            var window = new MainWindow(new MainViewModel(backend), initializeHardware: !_startup.IsSmokeTest);
            MainWindow = window;
            if (_startup.IsSmokeTest) window.Loaded += async (_, _) => await RunSmokeTestAsync(window);
            window.Show();
        }
        catch (Exception exception)
        {
            _startup.ReportFailure(exception);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instanceLock is not null) { _instanceLock.ReleaseMutex(); _instanceLock.Dispose(); _instanceLock = null; }
        base.OnExit(e);
    }

    private async Task RunSmokeTestAsync(MainWindow window)
    {
        try
        {
            // Exercise resources, templates, bindings and all main views without collecting
            // hardware, generating load, opening dialogs or changing Windows settings.
            var tabs = window.FindName("MainPages") as TabControl ?? throw new InvalidOperationException("As páginas principais não foram encontradas.");
            var navigation = window.FindName("Navigation") as ListBox ?? throw new InvalidOperationException("O menu principal não foi encontrado.");
            var viewModel = (MainViewModel)window.DataContext;
            if (viewModel.VisibleSections.Count() != 6) throw new InvalidOperationException("O modo simples deve mostrar seis destinos.");
            viewModel.InterfaceMode = "Avançado";
            var detailViews = 0;
            var initialWidth = window.Width; var initialHeight = window.Height;
            foreach (var section in viewModel.Sections)
            {
                viewModel.NavigateCommand.Execute(section.Id);
                if (section.Id == "resultados") viewModel.ShowTechnicalDetails = true;
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                if (tabs.SelectedIndex != section.Tab || !Equals(navigation.SelectedItem, section) || viewModel.PageTitle != section.Name)
                    throw new InvalidOperationException("O menu e a página ficaram fora de sincronia: " + section.Id);
                if (section.Id is "dashboard" or "componentes" or "investigar")
                    _startup.CaptureView((FrameworkElement)window.Content, section.Id);
                foreach (var nested in Descendants<TabControl>(tabs).Where(t => t != tabs).ToArray())
                {
                    for (var index = 0; index < nested.Items.Count; index++)
                    {
                        nested.SelectedIndex = index;
                        window.UpdateLayout();
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        detailViews++;
                        foreach (var expander in Descendants<Expander>(nested).ToArray()) expander.IsExpanded = true;
                        window.UpdateLayout();
                        await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                        foreach (var expander in Descendants<Expander>(nested).ToArray()) expander.IsExpanded = false;
                    }
                }
            }
            viewModel.OpenComponentCommand.Execute("ram");
            if (viewModel.SelectedSection?.Id != "componentes" || viewModel.SelectedComponent?.Id != "ram")
                throw new InvalidOperationException("O atalho de componente não selecionou a memória.");
            viewModel.OpenInvestigationCommand.Execute("jogos");
            if (viewModel.SelectedSection?.Id != "investigar" || viewModel.InvestigationTab != 1)
                throw new InvalidOperationException("O atalho de jogos não abriu a investigação correta.");
            viewModel.NavigateCommand.Execute("fontes");
            if (viewModel.SelectedSection?.Id != "resultados" || viewModel.ResultTab != 2)
                throw new InvalidOperationException("O atalho de fontes não abriu a página correta.");
            viewModel.NavigateCommand.Execute("dashboard");
            viewModel.InterfaceMode = "Simples";
            window.Width = window.MinWidth; window.Height = window.MinHeight;
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            _startup.CaptureView((FrameworkElement)window.Content, "compact");
            window.Width = initialWidth; window.Height = initialHeight;
            window.UpdateLayout();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            _startup.CaptureView((FrameworkElement)window.Content, "overview");
            if (!window.IsVisible || window.ActualWidth <= 0 || window.ActualHeight <= 0)
                throw new InvalidOperationException("A janela WPF não ficou visível com dimensões válidas.");
            _startup.ReportSmokeSuccess(tabs.Items.Count, detailViews);
            window.Close();
        }
        catch (Exception exception)
        {
            _startup.ReportFailure(exception);
            Shutdown(1);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
}
