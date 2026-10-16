using PCAnalyzer.Core;

namespace PCAnalyzer.Diagnostics;

public static class LatencyInterpretation
{
    public static IReadOnlyList<Finding> Analyze(EtwCaptureResult capture)
    {
        var result = new List<Finding>();
        var coverage = DescribeCoverage(capture);
        var partial = IsPartial(capture);
        var candidates = (capture.Timeline ?? []).Where(e => !string.IsNullOrWhiteSpace(e.Module) && double.IsFinite(e.DurationMicroseconds) &&
            e.DurationMicroseconds >= 1000 && e.TraceRelativeMilliseconds is { } t && t >= 0 && double.IsFinite(t)).GroupBy(e => (e.Module, e.Kind));
        foreach (var group in candidates)
        {
            var events = group.ToArray();
            if (events.Length < 3 || events.Select(e => (long)(e.TraceRelativeMilliseconds!.Value / 1000)).Distinct().Count() < 2) continue;
            result.Add(new("dpc-recurring:" + group.Key.Module + ":" + group.Key.Kind, "DPC/ISR", "Durações recorrentes para investigar · " + Path.GetFileName(group.Key.Module),
                $"Foram retidos {events.Length} eventos {group.Key.Kind} de pelo menos 1.000 µs em duas ou mais janelas de um segundo. Esse é um critério de triagem, não um limite de defeito nem prova de input lag. {coverage}",
                FindingCategory.PossibleProblem, Severity.Warning, Confidence.Low,
                events.OrderByDescending(e => e.DurationMicroseconds).Take(3).Select(e => new Evidence(
                    $"{e.Kind}; CPU {e.Cpu}; módulo {e.Module}; t={e.TraceRelativeMilliseconds:0.###} ms", "Kernel ETW / TraceEvent",
                    "dpc.cpu." + e.Cpu + "." + e.Kind.ToLowerInvariant() + ".duration.us", e.DurationMicroseconds, "µs", e.EndedAtUtc)).ToArray(),
                "Verifique a versão e o comportamento do driver durante a carga antes de mudar configurações. Repita a captura.",
                partial ? "A cobertura é parcial. Repita sem perdas/limites antes de comparar distribuições; a recorrência descreve apenas eventos retidos." :
                    "Compare distribuição e recorrência sob a mesma carga; confirme o contexto do módulo antes de qualquer ajuste.", "Nenhuma configuração foi alterada."));
        }
        if (result.Count == 0)
            result.Add(new("dpc-coverage", "DPC/ISR", "Sem atribuição recorrente demonstrada nesta triagem",
                "O critério de recorrência não encontrou módulos com cobertura suficiente. Isso não exclui eventos não retidos, perdas ou problemas fora da janela.",
                FindingCategory.InsufficientData, Severity.Information, Confidence.Low,
                [new(coverage, "Kernel ETW")],
                "Examine a distribuição e repita durante o sintoma, preservando as limitações do trace.", "Não atribua eventos sem módulo a um driver por suposição.", "Nenhuma configuração foi alterada."));
        if (partial)
            result.Add(new("dpc-partial", "DPC/ISR", "Cobertura DPC/ISR parcial",
                coverage + " Eventos perdidos, rejeitados ou fora da timeline não podem ser reconstruídos por suposição.",
                FindingCategory.InsufficientData, Severity.Information, Confidence.Low, [new(coverage, "Kernel ETW")],
                "Repita a captura durante o sintoma e confira a cobertura antes de atribuir comportamento a um driver.",
                "O total ETW inclui eventos auxiliares; somente DPC/ISR válidos entram nos resumos de duração.", "Nenhuma configuração foi alterada."));
        return result;
    }

    public static string DescribeCoverage(EtwCaptureResult capture)
    {
        var counts = capture.EventCounts;
        var detail = counts is null
            ? $"Total ETW: {capture.EventsSeen}; contagens separadas indisponíveis neste resultado antigo."
            : $"Total ETW: {counts.TotalEtwEvents}; DPC (inclui timer/threaded): {counts.DpcEvents}; ISR: {counts.IsrEvents}; auxiliares: {counts.AuxiliaryEvents}; " +
                $"rejeitados por CPU/duração inválida: {counts.RejectedLatencyEvents}; analisados: {counts.AnalyzedLatencyEvents}; sem módulo conhecido: {counts.UnmappedLatencyEvents}; timeline retida: {counts.TimelineEventsRetained}.";
        return detail + $" Perdidos pela fonte: {capture.EventsLost}; timeline truncada: {(capture.TimelineTruncated ? "sim" : "não")}; " +
            $"limite atingido: {(capture.LimitReached ? "sim" : "não")}; cancelado: {(capture.Cancelled ? "sim" : "não")}; estado: {capture.Status}.";
    }

    private static bool IsPartial(EtwCaptureResult capture) => capture.EventsLost > 0 || capture.TimelineTruncated ||
        capture.LimitReached || capture.Cancelled || capture.Status != Availability.Available || capture.EventCounts?.RejectedLatencyEvents > 0;
}
