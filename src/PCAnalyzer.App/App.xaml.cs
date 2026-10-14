using System.Windows;
using PCAnalyzer.App.Services;
using PCAnalyzer.App.ViewModels;
using System.Windows.Threading;

namespace PCAnalyzer.App;

public partial class App : Application
{
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
        try
        {
            base.OnStartup(e);
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

    private async Task RunSmokeTestAsync(MainWindow window)
    {
        try
        {
            // Exercise resources, templates, bindings and all main views without collecting
            // hardware, generating load, opening dialogs or changing Windows settings.
            var tabs = FindTabs(window) ?? throw new InvalidOperationException("As abas da janela principal não foram encontradas.");
            for (var index = 0; index < tabs.Items.Count; index++)
            {
                tabs.SelectedIndex = index;
                window.UpdateLayout();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            }
            if (!window.IsVisible || window.ActualWidth <= 0 || window.ActualHeight <= 0)
                throw new InvalidOperationException("A janela WPF não ficou visível com dimensões válidas.");
            _startup.ReportSmokeSuccess(tabs.Items.Count);
            window.Close();
        }
        catch (Exception exception)
        {
            _startup.ReportFailure(exception);
            Shutdown(1);
        }
    }

    private static System.Windows.Controls.TabControl? FindTabs(DependencyObject parent)
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, index);
            if (child is System.Windows.Controls.TabControl tabs) return tabs;
            if (FindTabs(child) is { } nested) return nested;
        }
        return null;
    }
}
