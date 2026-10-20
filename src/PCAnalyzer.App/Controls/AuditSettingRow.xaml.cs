using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace PCAnalyzer.App.Controls;

/// <summary>Displays an already interpreted setting; it never interprets or edits collected values.</summary>
public partial class AuditSettingRow : UserControl
{
    public AuditSettingRow() => InitializeComponent();

    private void OnOpenDetails(object sender, MouseButtonEventArgs e)
    {
        // The explicit button handles its own click. Clicking the rest of the row opens the same details.
        for (var source = e.OriginalSource as DependencyObject; source is not null && source != this;
             source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
            if (source is ButtonBase) return;
        var command = SettingDetailsButton.Command;
        if (command?.CanExecute(DataContext) != true) return;
        e.Handled = true;
        command.Execute(DataContext);
    }
}
