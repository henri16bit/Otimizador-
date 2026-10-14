using System.Globalization;
using System.Windows.Data;
using PCAnalyzer.Core;

namespace PCAnalyzer.App.ViewModels;

public sealed class AvailabilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Availability availability ? MainViewModel.AvailabilityLabel(availability) : "Dados insuficientes";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
