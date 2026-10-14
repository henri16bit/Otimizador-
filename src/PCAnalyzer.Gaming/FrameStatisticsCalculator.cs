using PCAnalyzer.Core;

namespace PCAnalyzer.Gaming;

public static class FrameStatisticsCalculator
{
    /// <summary>
    /// FPS = 1000 / mean(frame duration), never the mean of instantaneous FPS.
    /// Low FPS = 1000 / mean of the slowest ceil(n * fraction) durations, with at least ten tail samples.
    /// Percentiles use R-7 interpolation. These are descriptions of this capture, not confidence intervals.
    /// </summary>
    public static FrameStatistics Calculate(IEnumerable<double> durationsMilliseconds, double? observedSeconds = null)
    {
        var values = durationsMilliseconds.Where(x => double.IsFinite(x) && x > 0).Order().ToArray();
        var n = values.Length;
        var limitations = new List<string>();
        if (n < 30) limitations.Add("Menos de 30 intervalos válidos: amostra insuficiente para uma conclusão sobre desempenho.");
        if (observedSeconds is < 1) limitations.Add("A captura cobre menos de um segundo; repita por pelo menos 30 segundos no mesmo cenário.");
        if (n < 20) limitations.Add("p95 indisponível: requer pelo menos 20 intervalos.");
        if (n < 100) limitations.Add("p99 indisponível: requer pelo menos 100 intervalos.");
        if (n < 1000) limitations.Add("p99,9 indisponível: requer pelo menos 1.000 intervalos.");
        if (n < 1000) limitations.Add("1% low indisponível: requer 1.000 intervalos, incluindo pelo menos 10 no extremo lento.");
        if (n < 10000) limitations.Add("0,1% low indisponível: requer 10.000 intervalos, incluindo pelo menos 10 no extremo lento.");
        limitations.Add("Percentis descrevem a amostra; extremos com poucos frames variam entre execuções. Compare capturas repetidas no mesmo cenário.");
        var mean = n == 0 ? (double?)null : StableMean(values);
        var averageFps = mean is > 0 ? FiniteFps(mean.Value) : null;
        if (mean.HasValue && !averageFps.HasValue) limitations.Add("FPS não representável como número finito; confira unidades e integridade das durações da captura.");
        return new FrameStatistics(n, mean, averageFps,
            n >= 20 ? Statistics.Percentile(values, .95) : null,
            n >= 100 ? Statistics.Percentile(values, .99) : null,
            n >= 1000 ? Statistics.Percentile(values, .999) : null,
            n >= 1000 ? TailFps(values, .01) : null,
            n >= 10000 ? TailFps(values, .001) : null,
            n >= 30 && observedSeconds is not < 1 && averageFps.HasValue ? Availability.Available : Availability.InsufficientData,
            limitations);
    }

    private static double? TailFps(double[] sorted, double fraction)
    {
        var count = (int)Math.Ceiling(sorted.Length * fraction);
        return FiniteFps(StableMean(sorted.AsSpan(sorted.Length - count)));
    }

    private static double? FiniteFps(double milliseconds)
    {
        var fps = 1000 / milliseconds;
        return double.IsFinite(fps) ? fps : null;
    }

    private static double StableMean(ReadOnlySpan<double> values)
    {
        var mean = 0.0;
        for (var i = 0; i < values.Length; i++) mean += (values[i] - mean) / (i + 1);
        return mean;
    }
}
