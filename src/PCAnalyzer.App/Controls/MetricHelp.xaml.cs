using System.Windows;
using System.Windows.Controls;
using PCAnalyzer.Core;

namespace PCAnalyzer.App.Controls;

public partial class MetricHelp : UserControl
{
    public static readonly DependencyProperty InfoProperty = DependencyProperty.Register(nameof(Info),
        typeof(MetricInfo), typeof(MetricHelp), new PropertyMetadata(null));
    public MetricInfo? Info { get => (MetricInfo?)GetValue(InfoProperty); set => SetValue(InfoProperty, value); }
    public MetricHelp() => InitializeComponent();
}
