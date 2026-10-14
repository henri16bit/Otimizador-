using PCAnalyzer.Core;

namespace PCAnalyzer.Diagnostics;

/// <summary>Bounded aggregation of measured ETW durations and observed kernel image mappings.</summary>
public sealed class LatencyAggregator
{
    private const int MaximumImages = 8_192;
    private const int MaximumBuckets = 32_768;
    public const int MaximumTimelineEvents = 100_000;
    private const int MaximumTimeBuckets = 4_096;
    private readonly object gate = new();
    private readonly Dictionary<ulong, ImageRange> images = [];
    private readonly Dictionary<(int Cpu, string Kind), Counter> cpu = [];
    private readonly Dictionary<(string? Module, string Kind), Counter> modules = [];
    private readonly List<LatencyEvent> timeline = [];
    private readonly Dictionary<(long Second, string Kind), Counter> timeBuckets = [];
    private long invalidEvents;
    private long unmappedEvents;
    private bool boundReached;
    private bool timelineTruncated;
    private bool timeBucketsTruncated;

    public void ObserveKernelImage(int processId, ulong address, long bytes, string fileName)
    {
        // User process address spaces must never be used to resolve a kernel routine.
        if (processId is not (0 or 4) || address == 0 || bytes <= 0 || string.IsNullOrWhiteSpace(fileName)) return;
        lock (gate)
        {
            if (!images.ContainsKey(address) && images.Count >= MaximumImages) { boundReached = true; return; }
            images[address] = new(address, (ulong)bytes, fileName);
        }
    }

    public void ForgetKernelImage(int processId, ulong address)
    {
        if (processId is not (0 or 4)) return;
        lock (gate) images.Remove(address);
    }

    public void ObserveLatency(string kind, int processor, ulong routineAddress, double microseconds,
        double? traceRelativeMilliseconds = null, DateTimeOffset? endedAtUtc = null, long? rawTimestampTicks = null)
    {
        if (kind is not ("DPC" or "TimerDPC" or "ThreadedDPC" or "ISR"))
            throw new ArgumentException("Tipo ETW desconhecido.", nameof(kind));
        lock (gate)
        {
            if (processor < 0 || processor > 65_535 || !double.IsFinite(microseconds) || microseconds < 0)
            { invalidEvents++; return; }
            string? module = null;
            var matches = 0;
            foreach (var image in images.Values)
            {
                // Subtraction avoids an overflowing address+size comparison.
                if (routineAddress >= image.Address && routineAddress - image.Address < image.Bytes)
                { module = image.FileName; matches++; }
            }
            // Multiple overlapping ranges are ambiguous evidence, never a best guess.
            if (matches != 1) { module = null; unmappedEvents++; }
            Add(cpu, (processor, kind), microseconds);
            Add(modules, (module, kind), microseconds);
            if (timeline.Count < MaximumTimelineEvents)
                timeline.Add(new(kind, processor, routineAddress, module, microseconds,
                    traceRelativeMilliseconds, endedAtUtc, rawTimestampTicks));
            else timelineTruncated = true;
            if (traceRelativeMilliseconds is { } milliseconds && double.IsFinite(milliseconds) && milliseconds >= 0 && milliseconds / 1000 < long.MaxValue)
            {
                var key = ((long)Math.Floor(milliseconds / 1000), kind);
                if (!timeBuckets.ContainsKey(key) && timeBuckets.Count >= MaximumTimeBuckets) timeBucketsTruncated = true;
                else Add(timeBuckets, key, microseconds);
            }
        }
    }

    public (IReadOnlyList<LatencySummary> Cpu, IReadOnlyList<LatencySummary> Modules,
        IReadOnlyList<string> Limitations) Snapshot()
    {
        lock (gate)
        {
            var limitations = new List<string>
            {
                "Durações de execução DPC/ISR registradas pelo kernel; não medem latência fim a fim, fila de DPC nem atraso de entrada.",
                "Atribuição de módulo usa somente a faixa de endereço de imagens do kernel observada no ETW. Não identifica um dispositivo ou prova culpa de um driver.",
                "Tempos ISR podem estar contidos em intervalos DPC; não some os tipos como uma utilização total de CPU."
            };
            if (unmappedEvents > 0) limitations.Add($"{unmappedEvents} eventos sem uma faixa de módulo única: módulo desconhecido.");
            if (invalidEvents > 0) limitations.Add($"{invalidEvents} eventos com CPU/duração inválida foram descartados.");
            if (boundReached) limitations.Add("Limite de imagens/grupos atingido: os resumos podem estar incompletos.");
            if (timelineTruncated) limitations.Add("Timeline limitada aos primeiros 100.000 eventos válidos. Percentis de um grupo só aparecem quando todas as suas durações foram preservadas; não extrapolam uma amostra parcial.");
            if (timeBucketsTruncated) limitations.Add("Limite de 4.096 intervalos/tipos de recorrência atingido: série temporal parcial.");
            limitations.Add("Percentis R-7: p95 requer ao menos 20 eventos completos por grupo; p99 requer 100. Janelas/tipos devem ser comparados sob carga equivalente.");
            limitations.Add("Timeline preserva eventos processados com duração/endereço/CPU e timestamps, não pacotes ETW brutos nem stacks. UTC é derivado da sincronização do relógio do trace; não há correlação automática com frametimes PresentMon.");
            var cpuSamples = timeline.GroupBy(t => (t.Cpu, t.Kind)).ToDictionary(g => g.Key, g => g.Select(t => t.DurationMicroseconds).ToArray());
            var moduleSamples = timeline.GroupBy(t => (t.Module, t.Kind)).ToDictionary(g => g.Key, g => g.Select(t => t.DurationMicroseconds).ToArray());
            return (
                cpu.Select(p => Summary(p.Key.Cpu, p.Key.Kind, null, p.Value, cpuSamples.GetValueOrDefault(p.Key)))
                    .OrderBy(p => p.Cpu).ThenBy(p => p.Kind).ToArray(),
                modules.Select(p => Summary(-1, p.Key.Kind, p.Key.Module, p.Value, moduleSamples.GetValueOrDefault(p.Key)))
                    .OrderByDescending(p => p.TotalMicroseconds).ToArray(), limitations);
        }
    }

    public (IReadOnlyList<LatencyEvent> Events, bool Truncated, IReadOnlyList<LatencyTimeBucket> Buckets) TimelineSnapshot()
    {
        lock (gate)
            return (timeline.ToArray(), timelineTruncated,
                timeBuckets.Select(p => new LatencyTimeBucket(p.Key.Second * 1000d,
                    (p.Key.Second + 1d) * 1000d, p.Key.Kind, p.Value.Count, p.Value.Total, p.Value.Maximum))
                    .OrderBy(p => p.StartMilliseconds).ThenBy(p => p.Kind).ToArray());
    }

    private void Add<TKey>(Dictionary<TKey, Counter> target, TKey key, double duration) where TKey : notnull
    {
        if (!target.TryGetValue(key, out var count))
        {
            if (target.Count >= MaximumBuckets) { boundReached = true; return; }
            target[key] = count = new();
        }
        count.Count++;
        count.Total += duration;
        count.Maximum = Math.Max(count.Maximum, duration);
    }

    private static LatencySummary Summary(int processor, string kind, string? module, Counter value, double[]? durations)
    {
        var complete = durations?.LongLength == value.Count;
        return new(processor, kind, module, value.Count, value.Total, value.Total / value.Count, value.Maximum,
            complete && value.Count >= 20 ? Statistics.Percentile(durations!, 0.95) : null,
            complete && value.Count >= 100 ? Statistics.Percentile(durations!, 0.99) : null,
            durations?.LongLength ?? 0);
    }
    private sealed record ImageRange(ulong Address, ulong Bytes, string FileName);
    private sealed class Counter { public long Count; public double Total; public double Maximum; }
}
