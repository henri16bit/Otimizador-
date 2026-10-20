using PCAnalyzer.Core;

namespace PCAnalyzer.Diagnostics;

public sealed record LatencyKindResult(string Kind, long? AnalyzedCount, double? MaximumMicroseconds,
    double? MeanMicroseconds, double? P95Microseconds, double? P99Microseconds, int PercentileSampleCount,
    long? RecurrenceEventCount, int? RecurrenceWindowCount);

/// <summary>Presentation of measured durations; CPU and module aggregates must never be added together.</summary>
public static class LatencyResultPresentation
{
    public static IReadOnlyList<LatencyKindResult> Summarize(EtwCaptureResult capture) =>
        new[] { "DPC", "ISR" }.Select(kind => SummarizeKind(capture, kind)).ToArray();

    public static IReadOnlyList<ResultIndicator> Indicators(EtwCaptureResult capture) => Summarize(capture)
        .SelectMany(summary =>
        {
            var id = summary.Kind.ToLowerInvariant();
            var aggregateScope = $"Kernel ETW / TraceEvent. Agregados por CPU: {Count(summary.AnalyzedCount)} eventos válidos {KindScope(summary.Kind)}. " +
                "Duração de execução no kernel; não mede input lag físico. Perdas e limites restringem a cobertura.";
            var percentileScope = $"Kernel ETW / TraceEvent. Percentil R-7 de {summary.PercentileSampleCount} eventos {KindScope(summary.Kind)} na timeline retida; " +
                "p95 requer 20 amostras e p99 requer 100. Não representa eventos ausentes ou descartados." +
                (capture.TimelineTruncated ? " Timeline truncada: descreve somente o trecho retido." : "");
            return new ResultIndicator[]
            {
                new(id + ".maximum.us", "Maior duração " + summary.Kind, summary.MaximumMicroseconds, "µs", aggregateScope),
                new(id + ".mean.us", "Duração média " + summary.Kind, summary.MeanMicroseconds, "µs", aggregateScope + " Soma das durações / número de eventos; ponderada por evento."),
                new(id + ".p95.us", summary.Kind + " p95 · eventos retidos", summary.P95Microseconds, "µs", percentileScope),
                new(id + ".p99.us", summary.Kind + " p99 · eventos retidos", summary.P99Microseconds, "µs", percentileScope)
            };
        }).ToArray();

    public static IReadOnlyList<string> Details(EtwCaptureResult capture)
    {
        var lines = new List<string> { LatencyInterpretation.DescribeCoverage(capture) };
        foreach (var summary in Summarize(capture))
        {
            lines.Add($"{KindScope(summary.Kind)}: {Count(summary.AnalyzedCount)} eventos válidos nos agregados por CPU; " +
                $"média ponderada por evento={Number(summary.MeanMicroseconds)} µs; máximo={Number(summary.MaximumMicroseconds)} µs. " +
                "Agregados de módulos não são somados novamente.");
            lines.Add($"{summary.Kind}: p95={Number(summary.P95Microseconds)} µs; p99={Number(summary.P99Microseconds)} µs; " +
                $"n percentis={summary.PercentileSampleCount}; R-7 sobre a timeline retida, não média de percentis por CPU. " +
                "Mínimo de 20 amostras para p95 e 100 para p99; não extrapola eventos não retidos.");
            lines.Add(summary.RecurrenceEventCount.HasValue
                ? $"{summary.Kind}: {summary.RecurrenceEventCount} eventos de pelo menos 1.000 µs em {summary.RecurrenceWindowCount} janelas de um segundo na timeline retida com posição temporal conhecida. " +
                    "Critério de triagem; não é um limite universal de defeito nem atribui causa a um driver."
                : $"{summary.Kind}: recorrência indisponível; não há eventos retidos com posição temporal válida.");
        }
        return lines;
    }

    private static LatencyKindResult SummarizeKind(EtwCaptureResult capture, string kind)
    {
        var aggregates = capture.CpuSummaries.Where(s => Matches(s.Kind, kind) && s.Cpu is >= 0 and <= 65_535 && s.Count > 0 &&
            double.IsFinite(s.TotalMicroseconds) && s.TotalMicroseconds >= 0 &&
            double.IsFinite(s.MaximumMicroseconds) && s.MaximumMicroseconds >= 0).ToArray();
        long? count = aggregates.Length > 0 ? aggregates.Sum(s => s.Count) : null;
        double? mean = count is > 0 ? aggregates.Sum(s => s.TotalMicroseconds / count.Value) : null;
        if (mean is { } value && !double.IsFinite(value)) mean = null;
        double? maximum = aggregates.Length > 0 ? aggregates.Max(s => s.MaximumMicroseconds) : null;
        var timeline = (capture.Timeline ?? []).Where(e => Matches(e.Kind, kind) && e.Cpu is >= 0 and <= 65_535 &&
            double.IsFinite(e.DurationMicroseconds) && e.DurationMicroseconds >= 0).ToArray();
        var durations = timeline.Select(e => e.DurationMicroseconds).ToArray();
        var timed = timeline.Where(e => e.TraceRelativeMilliseconds is { } t && double.IsFinite(t) && t >= 0 &&
            t < TimeSpan.MaxValue.TotalMilliseconds).ToArray();
        var recurring = timed.Where(e => e.DurationMicroseconds >= 1000).ToArray();
        return new(kind, count, maximum, mean,
            durations.Length >= 20 ? Statistics.Percentile(durations, .95) : null,
            durations.Length >= 100 ? Statistics.Percentile(durations, .99) : null,
            durations.Length, timed.Length > 0 ? recurring.LongLength : null,
            timed.Length > 0 ? recurring.Select(e => (long)(e.TraceRelativeMilliseconds!.Value / 1000)).Distinct().Count() : null);
    }

    private static bool Matches(string actual, string expected) => expected == "ISR" ? actual == "ISR" : actual is "DPC" or "TimerDPC" or "ThreadedDPC";
    private static string KindScope(string kind) => kind == "DPC" ? "DPC (inclui timer/threaded)" : "ISR";
    private static string Count(long? count) => count?.ToString() ?? "indisponível";
    private static string Number(double? value) => value?.ToString("0.###") ?? "indisponível";
}
