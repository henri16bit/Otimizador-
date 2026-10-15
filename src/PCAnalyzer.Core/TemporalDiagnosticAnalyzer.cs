namespace PCAnalyzer.Core;

public static class TemporalDiagnosticAnalyzer
{
    /// <summary>Co-occurrence around frame peaks on a known UTC clock; never invents a relative-to-UTC anchor.</summary>
    public static IReadOnlyList<Finding> Analyze(IReadOnlyList<MetricSample> samples)
    {
        var valid = samples.Where(s => s.Status == Availability.Available && s.Value is { } v && double.IsFinite(v)).ToArray();
        var findings = new List<Finding>();
        foreach (var stream in valid.Where(s => s.Key.StartsWith("frames.", StringComparison.Ordinal) && s.Key.EndsWith(".frametime.ms", StringComparison.Ordinal) && s.Unit == "ms" && s.Value > 0).GroupBy(s => s.Key))
        {
            var frames = stream.ToArray();
            if (frames.Length < 100) continue;
            var median = Statistics.Percentile(frames.Select(s => s.Value!.Value), .5)!.Value;
            foreach (var peak in frames.Where(s => s.Value > Math.Max(2 * median, median + 8)).OrderByDescending(s => s.Value).Take(3))
            {
                var window = valid.Where(s => !s.Key.StartsWith("frames.", StringComparison.Ordinal)
                    && Math.Abs((s.Timestamp - peak.Timestamp).TotalMilliseconds) <= 250
                    && (s.SamplingInterval == TimeSpan.Zero || s.SamplingInterval <= TimeSpan.FromSeconds(1)))
                    .GroupBy(s => s.Key).Select(g => g.MinBy(s => Math.Abs((s.Timestamp - peak.Timestamp).TotalMilliseconds))!).Take(40).ToArray();
                findings.Add(new($"temporal:{peak.Key}:{peak.Timestamp:O}", "Correlação", window.Length == 0 ? "Pico sem telemetria temporal suficiente" : "Leituras próximas a um pico de frametime",
                    $"Janela UTC de ±250 ms ao redor de {peak.Timestamp:O}. {window.Length} séries com intervalo de até 1 s ou eventos individuais. Médias de intervalo não resolvem o instante exato do frame; proximidade não comprova causa.",
                    FindingCategory.InsufficientData, Severity.Information, Confidence.Low,
                    new[] { new Evidence("Pico observado na stream de frames", peak.Source, peak.Key, peak.Value, peak.Unit, peak.Timestamp) }
                        .Concat(window.Select(s => new Evidence($"{MetricCatalog.Resolve(s.Key).Name}; média/intervalo {s.SamplingInterval.TotalMilliseconds:0.###} ms", s.Source, s.Key, s.Value, s.Unit, s.Timestamp))).ToArray(),
                    window.Length == 0 ? "Colete fontes alinhadas ao relógio UTC e use intervalo apropriado para investigar essa janela." : "Compare outras ocorrências e uma janela de controle antes de atribuir o sintoma a um componente.",
                    "Não alinhe capturas relativas usando o horário em que o arquivo foi importado.", "Nenhuma configuração foi alterada."));
            }
        }
        return findings;
    }
}
