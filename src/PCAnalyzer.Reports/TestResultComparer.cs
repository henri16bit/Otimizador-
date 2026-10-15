using PCAnalyzer.Core;

namespace PCAnalyzer.Reports;

public static class TestResultComparer
{
    public static TechnicalResult Compare(TestSession before, TestSession after)
    {
        var lines = new List<string> { "Diferenças descritivas. Uma alteração aplicada não comprova melhoria.",
            $"Antes: {before.Name} · {before.StartedAt:O}", $"Depois: {after.Name} · {after.StartedAt:O}" };
        if (before.TestId != after.TestId || before.Status != TestStatus.Completed || after.Status != TestStatus.Completed)
            return new("Testes não comparáveis", lines.Concat(["Escolha duas execuções finalizadas do mesmo diagnóstico. Falhas e cancelamentos não produzem uma comparação de desempenho."]).ToArray(), Availability: Availability.InsufficientData);
        if (before.Result?.Session is not { } a || after.Result?.Session is not { } b)
            return new("Falta contexto para comparar", lines.Concat(["Uma execução não contém inventário e contexto da coleta."]).ToArray(), Availability: Availability.InsufficientData);
        if (string.IsNullOrEmpty(before.Result.ComparisonKey) || before.Result.ComparisonKey != after.Result.ComparisonKey)
            return new("Destinos ou métodos não equivalentes", lines.Concat(["O destino, arquivo, método ou identidade da carga difere ou não foi registrado. Nenhuma diferença foi calculada."]).ToArray(), Availability: Availability.InsufficientData);
        var comparison = SessionComparer.Compare(a, b);
        lines.Add(comparison.ConditionsComparable ? "Condições documentadas equivalentes; repetições ainda são necessárias." : "Condições diferentes ou cobertura insuficiente; não conclua melhoria.");
        lines.AddRange(comparison.Warnings);
        foreach (var first in before.Result.Indicators ?? [])
        {
            var second = after.Result.Indicators?.FirstOrDefault(i => i.Id == first.Id && i.Unit == first.Unit);
            if (first.Value is not { } x || second?.Value is not { } y || !double.IsFinite(x) || !double.IsFinite(y)) continue;
            lines.Add($"{first.Name}: {x:0.###} → {y:0.###} {first.Unit}; diferença {y - x:+0.###;-0.###;0}. Verifique a definição e o destino/stream antes de interpretar.");
        }
        lines.AddRange(comparison.Metrics.Where(m => m.Comparable).Select(m => $"{MetricCatalog.Resolve(m.Key).Name}: média {m.BeforeMean:0.###} → {m.AfterMean:0.###} {m.Unit}; p95 {m.BeforeP95:0.###} → {m.AfterP95:0.###}."));
        lines.Add("Alterações registradas entre as janelas:");
        lines.AddRange((a.Changes ?? []).Concat(b.Changes ?? []).DistinctBy(c => (c.Id, c.UpdatedAt))
            .Select(c => $"{c.UpdatedAt:O} · {c.Kind}: {c.Before} → {c.After} · {c.State}"));
        lines.Add("Ausência de registro não exclui mudanças externas. Repita a mesma carga, duração e destino/arquivo.");
        return new("Comparação dos testes", lines);
    }
}
