using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace PCAnalyzer.App.Controls;

public partial class AuditSettingsList : UserControl
{
    public AuditSettingsList() => InitializeComponent();
}

/// <summary>Uses WPF device-independent units, leaving room for navigation and category filters.</summary>
public sealed class AuditListViewportHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is double height && double.IsFinite(height) && height > 0
            ? Math.Clamp(height - 330, 160, 430)
            : 300d;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        DependencyProperty.UnsetValue;
}
