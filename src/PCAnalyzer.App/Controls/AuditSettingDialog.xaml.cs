using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using PCAnalyzer.Core;

namespace PCAnalyzer.App.Controls;

/// <summary>Displays the presentation policy's values and evidence without interpreting or editing settings.</summary>
public partial class AuditSettingDialog : Window
{
    private const int EvidencePageSize = 20;
    private readonly Action<string>? _requestTest;
    private bool _fittingWorkArea;
    private int _evidencePage;

    public AuditSettingPresentation Setting { get; }

    public AuditSettingDialog(AuditSettingPresentation setting, Action<string>? requestTest = null)
    {
        ArgumentNullException.ThrowIfNull(setting);
        Setting = setting;
        _requestTest = requestTest;
        InitializeComponent();
        DataContext = setting;
        RequestTestButton.Visibility = string.IsNullOrWhiteSpace(setting.TestId) ? Visibility.Collapsed : Visibility.Visible;
        RequestTestButton.IsEnabled = requestTest is not null;
        TechnicalRisksPanel.Visibility = string.IsNullOrWhiteSpace(setting.Risks) ? Visibility.Collapsed : Visibility.Visible;
        TechnicalDetailsExpander.Visibility = setting.TechnicalDetails.Count == 0 && string.IsNullOrWhiteSpace(setting.Risks)
            ? Visibility.Collapsed : Visibility.Visible;
        UpdateEvidencePage();
        Loaded += (_, _) =>
        {
            FitWorkArea();
            // A safe initial action, with Tab access to the test and technical evidence.
            CloseDetailsButton.Focus();
        };
        DpiChanged += (_, _) => FitWorkArea();
    }

    private void OnCloseDetails(object sender, RoutedEventArgs e) => Close();

    private void UpdateEvidencePage()
    {
        var start = _evidencePage * EvidencePageSize;
        TechnicalEvidenceItems.ItemsSource = Setting.TechnicalDetails.Skip(start).Take(EvidencePageSize).ToArray();
        var end = Math.Min(start + EvidencePageSize, Setting.TechnicalDetails.Count);
        TechnicalEvidencePageText.Text = Setting.TechnicalDetails.Count == 0 ? "Sem evidências adicionais." :
            $"Evidências {start + 1}–{end} de {Setting.TechnicalDetails.Count}";
        System.Windows.Automation.AutomationProperties.SetName(TechnicalEvidenceItems, TechnicalEvidencePageText.Text);
        TechnicalEvidencePager.Visibility = Setting.TechnicalDetails.Count > EvidencePageSize ? Visibility.Visible : Visibility.Collapsed;
        PreviousEvidenceButton.IsEnabled = _evidencePage > 0;
        NextEvidenceButton.IsEnabled = end < Setting.TechnicalDetails.Count;
    }

    private void OnPreviousEvidence(object sender, RoutedEventArgs e)
    {
        if (_evidencePage == 0) return;
        _evidencePage--;
        UpdateEvidencePage();
        FocusEvidencePage();
    }

    private void OnNextEvidence(object sender, RoutedEventArgs e)
    {
        if ((_evidencePage + 1) * EvidencePageSize >= Setting.TechnicalDetails.Count) return;
        _evidencePage++;
        UpdateEvidencePage();
        FocusEvidencePage();
    }

    private void FocusEvidencePage()
    {
        // Wait for the new item containers. Focus follows the newly displayed evidence,
        // including on the last page where the previously focused Next button is disabled.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsVisible || !TechnicalDetailsExpander.IsExpanded) return;
            TechnicalEvidenceItems.UpdateLayout();
            if (FirstTextBox(TechnicalEvidenceItems) is not { } first) return;
            first.Focus();
            first.BringIntoView(new Rect(0, 0, Math.Max(1, first.ActualWidth), Math.Min(24, Math.Max(1, first.ActualHeight))));
        }));
    }

    private static TextBox? FirstTextBox(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox text) return text;
            if (FirstTextBox(child) is { } nested) return nested;
        }
        return null;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void OnRequestTest(object sender, RoutedEventArgs e)
    {
        if (_requestTest is null || string.IsNullOrWhiteSpace(Setting.TestId)) return;
        var testId = Setting.TestId;
        RequestTestButton.IsEnabled = false;
        Close();
        // Leave the modal loop before the existing command displays its own test confirmation.
        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => _requestTest(testId)));
    }

    private void FitWorkArea()
    {
        if (_fittingWorkArea) return;
        _fittingWorkArea = true;
        try
        {
            var area = SystemParameters.WorkArea; // Already expressed in WPF device-independent units.
            var handle = new WindowInteropHelper(this).Handle;
            var monitor = MonitorFromWindow(handle, 2 /* MONITOR_DEFAULTTONEAREST */);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
            {
                var dpi = VisualTreeHelper.GetDpi(this);
                area = new Rect(info.Work.Left / dpi.DpiScaleX, info.Work.Top / dpi.DpiScaleY,
                    (info.Work.Right - info.Work.Left) / dpi.DpiScaleX,
                    (info.Work.Bottom - info.Work.Top) / dpi.DpiScaleY);
            }
            MaxWidth = Math.Min(680, Math.Max(1, area.Width - 24));
            MaxHeight = Math.Max(1, area.Height - 24);
            Width = Math.Min(660, MaxWidth);
            Height = Math.Min(640, MaxHeight);
            var centerX = Owner is { IsVisible: true } owner ? owner.Left + owner.ActualWidth / 2 : area.Left + area.Width / 2;
            var centerY = Owner is { IsVisible: true } ownerWindow ? ownerWindow.Top + ownerWindow.ActualHeight / 2 : area.Top + area.Height / 2;
            Left = Math.Clamp(centerX - Width / 2, area.Left + 12, area.Right - Width - 12);
            Top = Math.Clamp(centerY - Height / 2, area.Top + 12, area.Bottom - Height - 12);
        }
        finally { _fittingWorkArea = false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
}
