using System.ComponentModel;
using System.Windows;
using PCAnalyzer.App.ViewModels;

namespace PCAnalyzer.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private bool _canClose;
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel; DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeAsync();
        StateChanged += (_, _) => viewModel.IsMinimized = WindowState == WindowState.Minimized;
    }
    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (_canClose) return;
        args.Cancel = true; IsEnabled = false;
        try { await _viewModel.DisposeAsync(); }
        finally { _canClose = true; Close(); }
    }
}
