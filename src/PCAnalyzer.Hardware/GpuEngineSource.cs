using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

/// <summary>WDDM process shares are summed within each engine; simultaneous engines are not summed.</summary>
internal static partial class GpuEngineAggregation
{
    [GeneratedRegex(@"^pid_\d+_luid_(0x[0-9a-f]+_0x[0-9a-f]+)_phys_(\d+)_eng_(\d+)_engtype_.+$", RegexOptions.IgnoreCase)]
    private static partial Regex InstancePattern();

    internal static IReadOnlyDictionary<string, double> Aggregate(IEnumerable<(string Instance, double Value)> observations)
    {
        var engines = new Dictionary<(string Adapter, string Engine), double>();
        foreach (var (instance, value) in observations)
        {
            var match = InstancePattern().Match(instance);
            if (!match.Success || !double.IsFinite(value) || value < 0) continue;
            var adapter = "luid_" + match.Groups[1].Value.ToLowerInvariant() + "_phys_" + match.Groups[2].Value;
            var key = (adapter, match.Groups[3].Value);
            engines[key] = engines.GetValueOrDefault(key) + value;
        }
        return engines.GroupBy(pair => pair.Key.Adapter).ToDictionary(group => group.Key,
            group => Math.Clamp(group.Max(pair => pair.Value), 0, 100));
    }
}

[SupportedOSPlatform("windows")]
internal sealed class GpuEngineSource : IPassiveTelemetrySource
{
    private sealed record Counter(PerformanceCounter Reader, long Started);
    private readonly Dictionary<string, Counter> _counters = new(StringComparer.Ordinal);
    private readonly CounterNames _names = new();
    private long _lastRefresh;

    public IReadOnlyList<MetricSample> Read(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var ticks = Stopwatch.GetTimestamp();
        if (_lastRefresh == 0 || Stopwatch.GetElapsedTime(_lastRefresh, ticks) >= TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var names = new PerformanceCounterCategory(_names.Resolve("GPU Engine")).GetInstanceNames().Take(4096).ToHashSet(StringComparer.Ordinal);
            foreach (var old in _counters.Keys.Where(name => !names.Contains(name)).ToArray())
            { _counters[old].Reader.Dispose(); _counters.Remove(old); }
            foreach (var name in names)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_counters.ContainsKey(name)) continue;
                PerformanceCounter? counter = null;
                try
                {
                    counter = new(_names.Resolve("GPU Engine"), _names.Resolve("Utilization Percentage"), name, true);
                    _ = counter.NextValue();
                    _counters.Add(name, new(counter, ticks));
                }
                catch { counter?.Dispose(); /* Process instances can disappear during enumeration. */ }
            }
            _lastRefresh = ticks;
        }
        var observations = new List<(string, double)>();
        var intervals = new List<TimeSpan>();
        var failed = 0;
        foreach (var (name, counter) in _counters.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (counter.Started == ticks) continue; // Rate counter must have two distinct observations.
            try
            {
                var value = counter.Reader.NextValue();
                if (!float.IsFinite(value) || value < 0) { failed++; continue; }
                observations.Add((name, value));
                intervals.Add(Stopwatch.GetElapsedTime(counter.Started, ticks));
                _counters[name] = counter with { Started = ticks };
            }
            catch { failed++; counter.Reader.Dispose(); _counters.Remove(name); }
        }
        if (failed > 0)
            return [MetricSample.Missing("gpu.wddm.telemetry", "GPU WDDM", "%", "Windows GPU Engine", now,
                Availability.InsufficientData, "Instâncias de processos mudaram durante a leitura; aguarde outra amostra completa para estimar utilização.")];
        var values = GpuEngineAggregation.Aggregate(observations);
        if (values.Count == 0)
            return [MetricSample.Missing("gpu.wddm.telemetry", "GPU WDDM", "%", "Windows GPU Engine", now,
                _counters.Count > 0 ? Availability.Collecting : Availability.Unsupported,
                _counters.Count > 0 ? "Utilização requer duas leituras por instância GPU Engine." :
                    "O driver não expôs instâncias GPU Engine; requer driver WDDM compatível. Ausência não significa utilização zero.")];
        return values.Select(pair => new MetricSample("gpu.wddm." + pair.Key + ".utilization.percent", "GPU WDDM " + pair.Key,
            pair.Value, "%", "Windows Performance Counters / GPU Engine",
            "Soma das parcelas de processos por engine físico; maior utilização entre engines, limitada a 100%.", now,
            intervals.Count > 0 ? intervals.Max() : TimeSpan.Zero, Kind: MeasurementKind.Derived,
            Aggregation: Aggregation.IntervalAverage,
            Limitation: "Identidade LUID/physical adapter do Windows; não corresponde automaticamente ao índice NVML/WMI. Engines recém-criados aguardam segunda amostra; até 4096 instâncias, renovadas a cada 5 s. Não mede VRAM, temperatura ou clocks.")).ToArray();
    }

    public void Dispose() { foreach (var counter in _counters.Values) counter.Reader.Dispose(); _counters.Clear(); }
}
