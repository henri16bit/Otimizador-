using System.ComponentModel;
using System.Windows;
using PCAnalyzer.App.ViewModels;

namespace PCAnalyzer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _canClose;
    public MainWindow(MainViewModel viewModel, bool initializeHardware = true)
    {
        InitializeComponent();
        _viewModel = viewModel; DataContext = viewModel;
        if (initializeHardware) Loaded += async (_, _) => await viewModel.InitializeAsync();
        StateChanged += (_, _) => viewModel.IsMinimized = WindowState == WindowState.Minimized;
    }
    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_canClose) return;
        args.Cancel = true; IsEnabled = false;
        try { await _viewModel.DisposeAsync(); }
        finally
        {
            _canClose = true;
            // DisposeAsync can complete synchronously. Wait until the canceled Closing
            // event has returned before closing again, otherwise WPF rejects reentry.
            _ = Dispatcher.BeginInvoke(new Action(Close));
        }
    }
}
