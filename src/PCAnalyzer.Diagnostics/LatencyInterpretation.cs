using PCAnalyzer.Core;

namespace PCAnalyzer.Diagnostics;

public static class LatencyInterpretation
{
    public static IReadOnlyList<Finding> Analyze(EtwCaptureResult capture)
    {
        var result = new List<Finding>();
        var candidates = (capture.Timeline ?? []).Where(e => e.Module is not null && double.IsFinite(e.DurationMicroseconds) &&
            e.DurationMicroseconds >= 1000 && e.TraceRelativeMilliseconds is >= 0).GroupBy(e => (e.Module, e.Kind));
        foreach (var group in candidates)
        {
            var events = group.ToArray();
            if (events.Length < 3 || events.Select(e => (long)(e.TraceRelativeMilliseconds!.Value / 1000)).Distinct().Count() < 2) continue;
            result.Add(new("dpc-recurring:" + group.Key.Module + ":" + group.Key.Kind, "DPC/ISR", "Durações recorrentes para investigar · " + Path.GetFileName(group.Key.Module),
                $"Foram retidos {events.Length} eventos {group.Key.Kind} de pelo menos 1.000 µs em duas ou mais janelas de um segundo. Esse é um critério de triagem, não um limite de defeito nem prova de input lag.",
                FindingCategory.PossibleProblem, Severity.Warning, Confidence.Low,
                events.OrderByDescending(e => e.DurationMicroseconds).Take(3).Select(e => new Evidence(
                    $"{e.Kind}; CPU {e.Cpu}; módulo {e.Module}; t={e.TraceRelativeMilliseconds:0.###} ms", "Kernel ETW / TraceEvent",
                    "dpc.cpu." + e.Cpu + "." + e.Kind.ToLowerInvariant() + ".duration.us", e.DurationMicroseconds, "µs", e.EndedAtUtc)).ToArray(),
                "Verifique a versão e o comportamento do driver durante a carga antes de mudar configurações. Repita a captura.",
                "Compare distribuição e recorrência sob a mesma carga. Perdas ou timeline truncada reduzem a cobertura.", "Nenhuma configuração foi alterada."));
        }
        if (result.Count == 0)
            result.Add(new("dpc-coverage", "DPC/ISR", "Sem atribuição recorrente demonstrada nesta triagem",
                "O critério de recorrência não encontrou módulos com cobertura suficiente. Isso não exclui eventos não retidos, perdas ou problemas fora da janela.",
                FindingCategory.InsufficientData, Severity.Information, Confidence.Low,
                [new($"Eventos vistos: {capture.EventsSeen}; perdidos: {capture.EventsLost}; timeline truncada: {capture.TimelineTruncated}", "Kernel ETW")],
                "Examine a distribuição e repita durante o sintoma, preservando as limitações do trace.", "Não atribua eventos sem módulo a um driver por suposição.", "Nenhuma configuração foi alterada."));
        return result;
    }
}
