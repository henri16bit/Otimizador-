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
        _viewModel = viewModel;
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, Math.Max(500, workArea.Width - 24));
        MinHeight = Math.Min(MinHeight, Math.Max(400, workArea.Height - 24));
        Width = Math.Min(Width, workArea.Width - 24);
        Height = Math.Min(Height, workArea.Height - 24);
        DataContext = viewModel;
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
