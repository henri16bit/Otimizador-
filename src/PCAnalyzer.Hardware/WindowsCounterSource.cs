using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Win32;
using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

[SupportedOSPlatform("windows")]
internal sealed class WindowsCounterSource : IPassiveTelemetrySource
{
    private readonly List<Binding> _counters = [];
    private readonly List<(string Key, string Component, string Unit, Exception Error)> _unavailable = [];
    private long _lastTimestamp;
    private sealed record Binding(string Key, string Component, string Unit, PerformanceCounter Counter,
        string Method, double Scale, bool Gauge, string? Limitation);

    public WindowsCounterSource()
    {
        var names = new CounterNames();
        if (NativeWindows.LogicalProcessorCount > 64)
            Bind("cpu.total.percent", "CPU", "%", "Processor Information", "% Processor Time", "_Total",
                limitation: "Média do contador Processor Information em todos os grupos; não identifica thread crítica.");
        try
        {
            var category = new PerformanceCounterCategory(names.Resolve("Processor Information"));
            var instances = category.GetInstanceNames().Where(n => !n.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                .OrderBy(ParseCpu).Take(1024).ToArray();
            if (instances.Length == 0) throw new InvalidOperationException("Nenhum processador lógico enumerado.");
            for (var index = 0; index < instances.Length; index++)
                Bind("cpu.logical." + index + ".percent", "CPU lógico " + instances[index], "%", "Processor Information", "% Processor Time", instances[index],
                    limitation: "Índice do aplicativo mapeado à instância de grupo,lógico na origem; média do intervalo.");
            Bind("cpu.dpc.percent", "CPU", "%", "Processor Information", "% DPC Time", "_Total", limitation:
                "Tempo agregado; não fornece duração de cada DPC nem identifica drivers. Use ETW para investigação.");
            Bind("cpu.interrupt.percent", "CPU", "%", "Processor Information", "% Interrupt Time", "_Total");
            Bind("cpu.interrupts.per_second", "CPU", "interrupções/s", "Processor Information", "Interrupts/sec", "_Total");
            Bind("cpu.frequency.reported.mhz", "CPU", "MHz", "Processor Information", "Processor Frequency", "_Total", gauge: true,
                limitation: "Frequência reportada; não é clock efetivo por núcleo nem prova de throttling.");
            Bind("cpu.performance.nominal.percent", "CPU", "% nominal", "Processor Information", "% Processor Performance", "_Total",
                limitation: "Desempenho relativo à frequência nominal; pode exceder 100%; não mede temperatura.");
        }
        catch (Exception error) { Unavailable("cpu.logical.telemetry", "CPU", "%", error); }
        Bind("cpu.queue.length", "CPU", "threads", "System", "Processor Queue Length", gauge: true,
            limitation: "Threads prontas aguardando execução; leitura instantânea não comprova gargalo.");
        Bind("cpu.context_switches.per_second", "CPU", "trocas/s", "System", "Context Switches/sec");
        Bind("memory.page_reads.per_second", "RAM", "leituras/s", "Memory", "Page Reads/sec", limitation:
            "Operações de leitura para resolver hard faults; incluem arquivos mapeados, não apenas pagefile.");
        Bind("memory.pages_input.per_second", "RAM", "páginas/s", "Memory", "Pages Input/sec");
        Bind("memory.page_writes.per_second", "RAM", "escritas/s", "Memory", "Page Writes/sec");
        try
        {
            var category = new PerformanceCounterCategory(names.Resolve("PhysicalDisk"));
            foreach (var instance in category.GetInstanceNames().Where(n => n != "_Total").Order().Take(128))
            {
                var diskIndex = instance.Split(' ', 2)[0];
                if (!int.TryParse(diskIndex, out _)) diskIndex = "instance_" + string.Concat(instance.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
                var prefix = "disk." + diskIndex + "."; var component = "Disco físico " + instance;
                Bind(prefix + "read.bytes_per_second", component, "bytes/s", "PhysicalDisk", "Disk Read Bytes/sec", instance);
                Bind(prefix + "write.bytes_per_second", component, "bytes/s", "PhysicalDisk", "Disk Write Bytes/sec", instance);
                Bind(prefix + "read.operations_per_second", component, "I/O/s", "PhysicalDisk", "Disk Reads/sec", instance);
                Bind(prefix + "write.operations_per_second", component, "I/O/s", "PhysicalDisk", "Disk Writes/sec", instance);
                Bind(prefix + "queue.length", component, "I/O", "PhysicalDisk", "Current Disk Queue Length", instance, gauge: true);
                Bind(prefix + "read.latency.ms", component, "ms", "PhysicalDisk", "Avg. Disk sec/Read", instance, scale: 1000,
                    limitation: "Média por I/O concluído; sem I/O, zero não mede latência ociosa. Inclui Windows/driver.");
                Bind(prefix + "write.latency.ms", component, "ms", "PhysicalDisk", "Avg. Disk sec/Write", instance, scale: 1000,
                    limitation: "Média por I/O concluído; não corresponde à especificação nominal do SSD.");
            }
        }
        catch (Exception error) { Unavailable("disk.telemetry", "Armazenamento", "", error); }

        void Bind(string key, string component, string unit, string category, string counter, string instance = "",
            double scale = 1, bool gauge = false, string? limitation = null)
        {
            PerformanceCounter? reader = null;
            try
            {
                reader = new(names.Resolve(category), names.Resolve(counter), instance, true);
                _ = reader.NextValue();
                _counters.Add(new(key, component, unit, reader, category + "\\" + instance + "\\" + counter, scale, gauge, limitation));
            }
            catch (Exception error) { reader?.Dispose(); Unavailable(key, component, unit, error); }
        }
    }

    public IReadOnlyList<MetricSample> Read(CancellationToken token)
    {
        var timestamp = DateTimeOffset.UtcNow; var ticks = Stopwatch.GetTimestamp();
        var interval = _lastTimestamp == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_lastTimestamp, ticks);
        var metrics = _unavailable.Select(u => MetricSample.Missing(u.Key, u.Component, u.Unit, "Windows Performance Counters",
            timestamp, WindowsHardwareDiscovery.Status(u.Error), u.Error.Message)).ToList();
        foreach (var binding in _counters)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var value = binding.Counter.NextValue() * binding.Scale;
                if (!double.IsFinite(value)) throw new InvalidOperationException("Contador retornou valor não finito.");
                if (_lastTimestamp == 0 && !binding.Gauge)
                    metrics.Add(MetricSample.Missing(binding.Key, binding.Component, binding.Unit, "Windows Performance Counters", timestamp,
                        Availability.Collecting, "Necessárias duas amostras para taxa/média."));
                else metrics.Add(new(binding.Key, binding.Component, value, binding.Unit, "Windows Performance Counters", binding.Method,
                    timestamp, binding.Gauge ? TimeSpan.Zero : interval,
                    Kind: binding.Gauge ? MeasurementKind.Direct : MeasurementKind.Derived,
                    Aggregation: binding.Gauge ? Aggregation.Instantaneous : Aggregation.IntervalAverage, Limitation: binding.Limitation));
            }
            catch (Exception error) { metrics.Add(MetricSample.Missing(binding.Key, binding.Component, binding.Unit,
                "Windows Performance Counters", timestamp, WindowsHardwareDiscovery.Status(error), error.Message)); }
        }
        _lastTimestamp = ticks;
        return metrics;
    }

    private void Unavailable(string key, string component, string unit, Exception error) => _unavailable.Add((key, component, unit, error));
    private static (int Group, int Cpu) ParseCpu(string instance)
    {
        var pieces = instance.Split(',');
        return pieces.Length == 2 && int.TryParse(pieces[0], out var group) && int.TryParse(pieces[1], out var cpu) ?
            (group, cpu) : (0, int.TryParse(instance, out var fallback) ? fallback : int.MaxValue);
    }
    public void Dispose() { foreach (var binding in _counters) binding.Counter.Dispose(); _counters.Clear(); }
}

/// <summary>Maps English perflib names to this Windows installation's language without modifying the registry.</summary>
[SupportedOSPlatform("windows")]
internal sealed class CounterNames
{
    private readonly Dictionary<string, string> _localized = new(StringComparer.OrdinalIgnoreCase);
    public CounterNames()
    {
        try
        {
            using var english = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\009");
            using var current = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\CurrentLanguage");
            if (english?.GetValue("Counter") is not string[] source || current?.GetValue("Counter") is not string[] target) return;
            var lookup = new Dictionary<string, string>();
            for (var i = 0; i + 1 < target.Length; i += 2) lookup[target[i]] = target[i + 1];
            for (var i = 0; i + 1 < source.Length; i += 2)
                if (lookup.TryGetValue(source[i], out var name)) _localized[source[i + 1]] = name;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
    }
    public string Resolve(string english) => _localized.GetValueOrDefault(english, english);
}
