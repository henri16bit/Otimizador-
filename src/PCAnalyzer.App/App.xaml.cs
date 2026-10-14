using System.Windows;
using PCAnalyzer.App.Services;
using PCAnalyzer.App.ViewModels;

namespace PCAnalyzer.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var backend = new ApplicationBackend();
        var window = new MainWindow(new MainViewModel(backend));
        MainWindow = window;
        window.Show();
    }
}
