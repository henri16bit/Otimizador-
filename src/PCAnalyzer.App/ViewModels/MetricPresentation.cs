using System.Globalization;
using PCAnalyzer.Core;

namespace PCAnalyzer.App.ViewModels;

public sealed record DisplayReading(string Value, string Unit);
public sealed record DisplayScale(double Factor, string Unit);
public sealed record OverviewCard(string Id, string Title, string Icon, string Value, string Unit,
    string Detail, double Percentage = 0, bool HasPercentage = false, MetricSample? Sample = null)
{
    public MetricInfo Info => MetricCatalog.Resolve(Sample?.Key ?? (Id switch { "cpu" => "cpu.total.percent", "ram" => "memory.available.bytes", "gpu" => "gpu.0.utilization.percent", _ => "network.adapter.receive.bytes_per_second" }));
    public string Provenance => Sample is null ? "Dados não disponíveis — verifique permissões ou suporte do dispositivo." :
        $"{Sample.Source} · {Sample.Timestamp.LocalDateTime:HH:mm:ss} · intervalo {Sample.SamplingInterval.TotalSeconds:0.###} s\n{Sample.Method}\n{Sample.Limitation}";
}

/// <summary>Display-only conversions. Stored samples and reports retain original keys and units.</summary>
public static class MetricPresentation
{
    public static DisplayReading Format(MetricSample sample)
    {
        if (!Available(sample)) return new("—", sample.Unit);
        return Format(sample.Value!.Value, sample.Unit);
    }

    public static DisplayReading Format(double value, string unit)
    {
        if (!double.IsFinite(value)) return new("—", unit);
        var scale = Scale(unit, Math.Abs(value));
        return new(Number(value / scale.Factor), scale.Unit);
    }

    public static string Number(double value) => value.ToString(value != 0 && (Math.Abs(value) < .001 || Math.Abs(value) >= 1_000_000_000)
        ? "0.###E+0" : "0.###", CultureInfo.CurrentCulture);

    public static DisplayScale Scale(string unit, double maximumMagnitude)
    {
        if (!double.IsFinite(maximumMagnitude)) return new(1, unit);
        if (unit is "bytes" or "bytes/s")
        {
            var suffixes = unit == "bytes" ? new[] { "B", "KiB", "MiB", "GiB", "TiB" }
                : new[] { "B/s", "KiB/s", "MiB/s", "GiB/s", "TiB/s" };
            var index = 0; var factor = 1d;
            while (Math.Abs(maximumMagnitude) / factor >= 1024 && index < suffixes.Length - 1) { factor *= 1024; index++; }
            return new(factor, suffixes[index]);
        }
        return unit == "bits/s" && Math.Abs(maximumMagnitude) >= 1_000_000 ? new(1_000_000, "Mbit/s") : new(1, unit);
    }

    public static string Name(MetricSample sample) => MetricCatalog.Resolve(sample.Key, sample.Unit).Name;

    public static IReadOnlyList<OverviewCard> Overview(IEnumerable<MetricSample> samples, HardwareInventory? inventory)
    {
        var latest = samples.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.MaxBy(s => s.Timestamp)!, StringComparer.Ordinal);
        var missing = inventory is null ? "Aguardando as primeiras leituras" : "Leitura indisponível · consulte as fontes";
        latest.TryGetValue("cpu.total.percent", out var cpu);
        var validCpu = Available(cpu) && cpu!.Unit == "%" && cpu.Value is >= 0 and <= 100;
        var cpuReading = validCpu ? Format(cpu!) : new("—", "%");
        var cards = new List<OverviewCard>
        {
            new("cpu", "CPU em uso", "\uE950", cpuReading.Value, cpuReading.Unit,
                inventory?.Components.FirstOrDefault(c => c.Category == "CPU")?.Name ?? missing,
                validCpu ? cpu!.Value!.Value : 0, validCpu, cpu)
        };
        latest.TryGetValue("memory.total.bytes", out var total);
        latest.TryGetValue("memory.available.bytes", out var free);
        if (Available(total) && Available(free) && total!.Unit == "bytes" && free!.Unit == "bytes"
            && total.Value > 0 && free.Value >= 0 && free.Value <= total.Value
            && (total.Timestamp - free.Timestamp).Duration() <= TimeSpan.FromSeconds(2))
        {
            var used = total.Value!.Value - free.Value!.Value;
            var percent = used / total.Value.Value * 100;
            var usedText = Format(used, "bytes"); var totalText = Format(total.Value.Value, "bytes");
            cards.Add(new("ram", "RAM usada", "\uE964", percent.ToString("0.#", CultureInfo.CurrentCulture), "%",
                $"{usedText.Value} {usedText.Unit} de {totalText.Value} {totalText.Unit} · RAM física", percent, true,
                free with { Key = "memory.used.percent", Value = percent, Unit = "%", Kind = MeasurementKind.Derived,
                    Method = "RAM usada derivada: (total − disponível) / total × 100. " + free.Method }));
        }
        else cards.Add(new("ram", "RAM usada", "\uE964", "—", "%", missing));
        var gpu = latest.Values.Where(s => s.Key.StartsWith("gpu.", StringComparison.Ordinal)
            && s.Key.EndsWith(".utilization.percent", StringComparison.Ordinal)).OrderBy(s => s.Key)
            .FirstOrDefault(s => Available(s) && s.Unit == "%" && s.Value is >= 0 and <= 100);
        var gpuReading = gpu is not null ? Format(gpu) : new("—", "%");
        cards.Add(new("gpu", "GPU em uso", "\uE7F4", gpuReading.Value, gpuReading.Unit,
            gpu?.Component ?? missing, gpu is not null ? Math.Clamp(gpu.Value!.Value, 0, 100) : 0, gpu is not null, gpu));
        var network = latest.Values.Where(s => s.Key.StartsWith("network.", StringComparison.Ordinal)
            && s.Key.EndsWith(".receive.bytes_per_second", StringComparison.Ordinal) && Available(s) && s.Unit == "bytes/s" && s.Value >= 0)
            .OrderByDescending(s => s.Value).ThenBy(s => s.Key).FirstOrDefault();
        var networkReading = network is not null ? Format(network) : new("—", "B/s");
        cards.Add(new("rede", "Rede · recebimento", "\uE968", networkReading.Value, networkReading.Unit,
            network?.Component ?? missing, Sample: network));
        return cards;
    }

    public static bool Available(MetricSample? sample) => sample?.Status == Availability.Available
        && sample.Value is { } value && double.IsFinite(value);
}
