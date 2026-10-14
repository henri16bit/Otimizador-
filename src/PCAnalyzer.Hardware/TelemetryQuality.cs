using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

public static class TelemetryQuality
{
    /// <summary>Windows returns zero cooked disk latency with no completed I/O; zero then is not an observed latency.</summary>
    public static IReadOnlyList<MetricSample> MarkDiskLatencyWithoutIo(IReadOnlyList<MetricSample> samples)
    {
        var rates = samples.Where(sample => sample.Key.StartsWith("disk.", StringComparison.Ordinal) &&
            sample.Key.EndsWith(".operations_per_second", StringComparison.Ordinal))
            .GroupBy(sample => sample.Key).ToDictionary(group => group.Key, group => group.Last());
        return samples.Select(sample =>
        {
            if (!sample.Key.StartsWith("disk.", StringComparison.Ordinal) || !sample.Key.EndsWith(".latency.ms", StringComparison.Ordinal) ||
                sample.Status != Availability.Available) return sample;
            var key = sample.Key[..^"latency.ms".Length] + "operations_per_second";
            var available = rates.TryGetValue(key, out var rate) && rate.Status == Availability.Available && rate.Value.HasValue && double.IsFinite(rate.Value.Value);
            if (available && rate!.Value > 0) return sample;
            return sample with
            {
                Value = null,
                Status = Availability.InsufficientData,
                Limitation = available ? "Nenhum I/O concluído no intervalo; o zero do contador não é uma latência medida." :
                    "Taxa de I/O indisponível; não é possível verificar se o contador de latência representa operações concluídas."
            };
        }).ToArray();
    }
}
