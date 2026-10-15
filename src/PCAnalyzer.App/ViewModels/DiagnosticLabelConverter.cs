using System.Globalization;
using System.Windows.Data;
using PCAnalyzer.Core;

namespace PCAnalyzer.App.ViewModels;
public sealed class DiagnosticLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        FindingCategory.ObservedProblem => "Problema observado", FindingCategory.PossibleProblem => "Possível problema",
        FindingCategory.OptimizationOpportunity => "Oportunidade de ajuste", FindingCategory.NoAnomaly => "Sem anomalia nesta triagem",
        FindingCategory.InsufficientData => "Dados insuficientes", Confidence.High => "Confiança alta", Confidence.Medium => "Confiança média", Confidence.Low => "Confiança baixa",
        Severity.Information => "Informativo", Severity.Warning => "Investigar", Severity.Critical => "Crítico", _ => value?.ToString() ?? "—"
    };
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
