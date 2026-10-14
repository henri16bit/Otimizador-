using System.Globalization;
using PCAnalyzer.Core;

namespace PCAnalyzer.App.ViewModels;

public sealed record DisplayReading(string Value, string Unit);
public sealed record DisplayScale(double Factor, string Unit);
public sealed record OverviewCard(string Id, string Title, string Icon, string Value, string Unit,
    string Detail, double Percentage = 0, bool HasPercentage = false);

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

    public static string Name(MetricSample sample) => sample.Key switch
    {
        "cpu.total.percent" => "Uso total da CPU",
        "cpu.queue.length" => "Threads aguardando a CPU",
        "cpu.dpc.percent" => "Tempo de CPU em DPC",
        "cpu.interrupt.percent" => "Tempo de CPU em interrupções",
        "cpu.interrupts.per_second" => "Interrupções por segundo",
        "cpu.context_switches.per_second" => "Trocas de contexto",
        "cpu.frequency.reported.mhz" => "Frequência reportada",
        "cpu.performance.nominal.percent" => "Desempenho relativo ao clock nominal",
        "memory.total.bytes" => "RAM física total",
        "memory.available.bytes" => "RAM disponível",
        "memory.commit.bytes" => "Memória comprometida (commit)",
        "memory.commit.limit.bytes" => "Limite de commit",
        "memory.cache.bytes" => "Cache do sistema",
        "memory.page_reads.per_second" => "Leituras de paginação",
        "memory.pages_input.per_second" => "Páginas lidas para a memória",
        "memory.page_writes.per_second" => "Escritas de paginação",
        "network.ping.rtt.ms" => "Tempo de resposta do ping",
        _ when sample.Key.StartsWith("cpu.logical.", StringComparison.Ordinal) && sample.Key.EndsWith(".percent", StringComparison.Ordinal)
            => "Uso do processador lógico " + sample.Key[12..^8],
        _ when sample.Key.StartsWith("gpu.", StringComparison.Ordinal) => SuffixName(sample.Key, GpuNames),
        _ when sample.Key.StartsWith("disk.", StringComparison.Ordinal) => SuffixName(sample.Key, DiskNames),
        _ when sample.Key.StartsWith("network.", StringComparison.Ordinal) => SuffixName(sample.Key, NetworkNames),
        _ => sample.Key
    };

    private static readonly (string Suffix, string Name)[] GpuNames =
    [
        ("utilization.percent", "Uso da GPU"), ("memory.activity.percent", "Atividade da memória da GPU"),
        ("memory.used.bytes", "VRAM usada"), ("memory.total.bytes", "VRAM total"), ("memory.free.bytes", "VRAM livre"),
        ("temperature.celsius", "Temperatura da GPU"), ("clock.graphics.mhz", "Clock gráfico"),
        ("clock.memory.mhz", "Clock de memória da GPU"), ("power.watts", "Potência da GPU"),
        ("performance.state", "Estado de desempenho da GPU")
    ];
    private static readonly (string Suffix, string Name)[] DiskNames =
    [
        ("read.bytes_per_second", "Velocidade de leitura"), ("write.bytes_per_second", "Velocidade de escrita"),
        ("read.operations_per_second", "Operações de leitura"), ("write.operations_per_second", "Operações de escrita"),
        ("queue.length", "Fila de operações do disco"), ("read.latency.ms", "Latência média de leitura"),
        ("write.latency.ms", "Latência média de escrita")
    ];
    private static readonly (string Suffix, string Name)[] NetworkNames =
    [
        ("receive.bytes_per_second", "Recebimento de dados"), ("send.bytes_per_second", "Envio de dados"),
        ("link.bits_per_second", "Velocidade do link"), ("receive.errors.total", "Erros de recebimento acumulados"),
        ("send.errors.total", "Erros de envio acumulados")
    ];
    private static string SuffixName(string key, (string Suffix, string Name)[] names) =>
        names.FirstOrDefault(item => key.EndsWith("." + item.Suffix, StringComparison.Ordinal)).Name ?? key;

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
                validCpu ? cpu!.Value!.Value : 0, validCpu)
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
                $"{usedText.Value} {usedText.Unit} de {totalText.Value} {totalText.Unit} · RAM física", percent, true));
        }
        else cards.Add(new("ram", "RAM usada", "\uE964", "—", "%", missing));
        var gpu = latest.Values.Where(s => s.Key.StartsWith("gpu.", StringComparison.Ordinal)
            && s.Key.EndsWith(".utilization.percent", StringComparison.Ordinal)).OrderBy(s => s.Key)
            .FirstOrDefault(s => Available(s) && s.Unit == "%" && s.Value is >= 0 and <= 100);
        var gpuReading = gpu is not null ? Format(gpu) : new("—", "%");
        cards.Add(new("gpu", "GPU em uso", "\uE7F4", gpuReading.Value, gpuReading.Unit,
            gpu?.Component ?? missing, gpu is not null ? Math.Clamp(gpu.Value!.Value, 0, 100) : 0, gpu is not null));
        var network = latest.Values.Where(s => s.Key.StartsWith("network.", StringComparison.Ordinal)
            && s.Key.EndsWith(".receive.bytes_per_second", StringComparison.Ordinal) && Available(s) && s.Unit == "bytes/s" && s.Value >= 0)
            .OrderByDescending(s => s.Value).ThenBy(s => s.Key).FirstOrDefault();
        var networkReading = network is not null ? Format(network) : new("—", "B/s");
        cards.Add(new("rede", "Rede · recebimento", "\uE968", networkReading.Value, networkReading.Unit,
            network?.Component ?? missing));
        return cards;
    }

    public static bool Available(MetricSample? sample) => sample?.Status == Availability.Available
        && sample.Value is { } value && double.IsFinite(value);
}
