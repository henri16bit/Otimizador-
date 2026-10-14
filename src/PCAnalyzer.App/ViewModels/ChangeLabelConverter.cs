using System.Globalization;
using System.Windows.Data;
using PCAnalyzer.Core;

namespace PCAnalyzer.App.ViewModels;

public sealed class ChangeLabelConverter : IValueConverter
{
    public static string Label(object? value) => value switch
    {
        ChangeState.Prepared => "Alteração pendente", ChangeState.Applied => "Aplicada",
        ChangeState.Failed => "Falha ao aplicar", ChangeState.Restored => "Restaurada",
        ChangeState.RestorePrepared => "Restauração pendente", ChangeState.RestoreFailed => "Falha ao restaurar",
        "active-power-plan" => "Plano de energia", "network-advanced-property" => "Propriedade de rede",
        _ => value?.ToString() ?? "—"
    };
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Label(value);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
