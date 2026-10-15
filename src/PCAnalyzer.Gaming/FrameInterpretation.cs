using PCAnalyzer.Core;

namespace PCAnalyzer.Gaming;

public static class FrameInterpretation
{
    public static IReadOnlyList<Finding> Analyze(PresentMonImportResult capture)
    {
        var findings = new List<Finding>();
        foreach (var stream in capture.Streams)
        {
            var valid = stream.Frames.Where(f => f.FrameTimeMilliseconds is > 0 && double.IsFinite(f.FrameTimeMilliseconds.Value)).ToArray();
            if (valid.Length < 100) continue;
            var median = Statistics.Percentile(valid.Select(f => f.FrameTimeMilliseconds!.Value), .5)!.Value;
            var threshold = Math.Max(median * 2, median + 8);
            var peaks = valid.Where(f => f.FrameTimeMilliseconds > threshold).ToArray();
            if (peaks.Length < 3) continue;
            var id = $"{stream.Identity.ProcessId}:{stream.Identity.SwapChainAddress}:{stream.Identity.Segment}";
            findings.Add(new("frame-variation:" + id, "Jogos", "Variações recorrentes de frametime · " + stream.Identity.Application,
                $"{peaks.Length} intervalos excederam max(2 × mediana, mediana + 8 ms) nesta stream. Mediana={median:0.###} ms; critério={threshold:0.###} ms. É uma triagem de variação, sem atribuir causa.",
                FindingCategory.PossibleProblem, Severity.Warning, Confidence.Medium,
                peaks.OrderByDescending(f => f.FrameTimeMilliseconds).Take(5).Select(f => new Evidence(
                    $"PID {stream.Identity.ProcessId}; swapchain {stream.Identity.SwapChainAddress}; segmento {stream.Identity.Segment}; t={f.TimestampMilliseconds:0.###} ms; base: {stream.TimestampBasis}",
                    stream.DurationSource, "frames.frametime.ms", f.FrameTimeMilliseconds, "ms", UtcTimestamp(stream, f))).ToArray(),
                "Investigue a mesma janela em CPU, GPU, memória e I/O quando houver relógios alinhados. Reproduza a mesma cena.",
                "Compare streams equivalentes e a distribuição de frametimes. Coincidência temporal não prova causalidade.", "Nenhuma configuração foi alterada."));
        }
        if (findings.Count == 0) findings.Add(new("frames-coverage", "Jogos", "Sem variação recorrente demonstrada pela triagem",
            "A regra requer pelo menos 100 intervalos válidos e três picos por stream. Verifique a quantidade de amostras, percentis e limitações antes de concluir estabilidade.",
            FindingCategory.InsufficientData, Severity.Information, Confidence.Low, [], "Capture durante a ocorrência e repita a mesma cena.",
            "Sem dados suficientes não há conclusão de ausência de stuttering.", "Nenhuma configuração foi alterada."));
        return findings;
    }
    public static DateTimeOffset? UtcTimestamp(FrameStream stream, FrameSample frame) =>
        stream.TimestampBasis == "Data/hora local com offset explicitamente informado, convertida para UTC" &&
        double.IsFinite(frame.TimestampMilliseconds) && frame.TimestampMilliseconds is >= -62135596800000 and <= 253402300799999
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)frame.TimestampMilliseconds) : null;
}
