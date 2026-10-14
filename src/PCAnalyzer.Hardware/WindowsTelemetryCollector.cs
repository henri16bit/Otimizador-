using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
using Microsoft.Win32;
using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

/// <summary>Lightweight passive telemetry. The caller schedules calls; this collector never generates load.</summary>
public sealed class WindowsTelemetryCollector : ITelemetryCollector, IDisposable
{
    public static long ReadAvailablePhysicalMemoryBytes()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Memória física disponível exige a API Windows GetPerformanceInfo.");
        var info = NativeWindows.ReadMemory();
        return checked((long)info.PhysicalAvailable * (long)info.PageSize);
    }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<CounterBinding> _counters = [];
    private readonly List<(string Key, string Component, string Unit, string Source, Availability Status, string Reason)> _unavailable = [];
    private readonly Dictionary<(int Pid, long Start), (TimeSpan Cpu, long Timestamp)> _processes = [];
    private readonly Dictionary<string, (long Received, long Sent, long Timestamp)> _networks = [];
    private NvmlClient? _nvml;
    private bool _initialized, _disposed, _useTotalCounter;
    private int _logicalProcessorCount = Environment.ProcessorCount;
    private (ulong Idle, ulong Total)? _cpu;
    private long _lastTimestamp;

    private sealed record CounterBinding(string Key, string Component, string Unit, PerformanceCounter Counter,
        string Method, double Scale = 1, bool Gauge = false, string? Limitation = null);

    public async Task<TelemetryBatch> CollectAsync(TimeSpan interval, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (interval < TimeSpan.FromMilliseconds(100) || interval > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(interval), "Intervalo permitido: 100 ms a 5 minutos; recomendado: pelo menos 1 s.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return await Task.Run(() => OperatingSystem.IsWindows() ? CollectWindows(cancellationToken) :
                new TelemetryBatch(DateTimeOffset.UtcNow,
                    [MetricSample.Missing("cpu.total.percent", "CPU", "%", "Windows", DateTimeOffset.UtcNow,
                        Availability.Unsupported, "Coletores de hardware exigem Windows; não há dados simulados.")], []), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A partially sampled batch must not become the rate baseline of a later completed batch.
            _lastTimestamp = 0;
            _cpu = null;
            _processes.Clear();
            _networks.Clear();
            throw;
        }
        finally { _gate.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private TelemetryBatch CollectWindows(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        token.ThrowIfCancellationRequested();
        if (!_initialized) InitializeCounters();
        var timestamp = DateTimeOffset.UtcNow;
        var ticks = Stopwatch.GetTimestamp();
        var actualInterval = _lastTimestamp == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_lastTimestamp, ticks);
        var metrics = new List<MetricSample>();
        foreach (var unavailable in _unavailable)
            metrics.Add(MetricSample.Missing(unavailable.Key, unavailable.Component, unavailable.Unit, unavailable.Source,
                timestamp, unavailable.Status, unavailable.Reason));
        if (!_useTotalCounter) try
        {
            var current = NativeWindows.ReadCpuTimes();
            if (_cpu is { } previous && current.Total > previous.Total && current.Idle >= previous.Idle)
            {
                var total = current.Total - previous.Total;
                var idle = current.Idle - previous.Idle;
                metrics.Add(new("cpu.total.percent", "CPU", Math.Clamp(100d * (total - Math.Min(idle, total)) / total, 0, 100), "%",
                    "GetSystemTimes", "Diferença (kernel + usuário − idle) / (kernel + usuário)", timestamp, actualInterval,
                    Kind: MeasurementKind.Derived, Aggregation: Aggregation.IntervalAverage,
                    Limitation: "Média dos tempos de execução do sistema; não identifica a thread responsável por uma restrição."));
            }
            else metrics.Add(MetricSample.Missing("cpu.total.percent", "CPU", "%", "GetSystemTimes", timestamp,
                Availability.Collecting, "Necessárias duas leituras de tempos do sistema."));
            _cpu = current;
        }
        catch (Exception error) { metrics.Add(Missing("cpu.total.percent", "CPU", "%", "GetSystemTimes", error)); }
        try
        {
            var memory = NativeWindows.ReadMemory();
            var page = (double)memory.PageSize;
            MemoryMetric("memory.total.bytes", (double)memory.PhysicalTotal * page, "PhysicalTotal × PageSize");
            MemoryMetric("memory.available.bytes", (double)memory.PhysicalAvailable * page, "PhysicalAvailable × PageSize");
            MemoryMetric("memory.commit.bytes", (double)memory.CommitTotal * page, "CommitTotal × PageSize");
            MemoryMetric("memory.commit.limit.bytes", (double)memory.CommitLimit * page, "CommitLimit × PageSize");
            MemoryMetric("memory.cache.bytes", (double)memory.SystemCache * page, "SystemCache × PageSize");
        }
        catch (Exception error)
        {
            foreach (var key in new[] { "memory.total.bytes", "memory.available.bytes", "memory.commit.bytes", "memory.commit.limit.bytes" })
                metrics.Add(Missing(key, "RAM", "bytes", "GetPerformanceInfo", error));
        }
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
                    timestamp, binding.Gauge ? TimeSpan.Zero : actualInterval,
                    Kind: binding.Gauge ? MeasurementKind.Direct : MeasurementKind.Derived,
                    Aggregation: binding.Gauge ? Aggregation.Instantaneous : Aggregation.IntervalAverage, Limitation: binding.Limitation));
            }
            catch (Exception error) { metrics.Add(Missing(binding.Key, binding.Component, binding.Unit, "Windows Performance Counters", error)); }
        }
        var processes = ReadProcesses(ticks, timestamp, token, metrics);
        ReadNetworks(ticks, timestamp, metrics);
        try { metrics.AddRange(_nvml!.Read(timestamp)); }
        catch (Exception error) { metrics.Add(Missing("gpu.telemetry", "GPU", "", "NVML", error)); }
        _lastTimestamp = ticks;
        metrics.Add(new("collector.duration.ms", "Aplicativo", stopwatch.Elapsed.TotalMilliseconds, "ms", "Stopwatch",
            "Tempo para obter este lote, incluindo enumeração de processos", DateTimeOffset.UtcNow, TimeSpan.Zero,
            Limitation: "Não inclui renderização WPF, gravação no banco ou processamento posterior."));
        using (var self = Process.GetCurrentProcess())
        {
            metrics.Add(new("collector.working_set.bytes", "Aplicativo", self.WorkingSet64, "bytes", "Process.WorkingSet64",
                "Working set do próprio processo", timestamp, TimeSpan.Zero));
            var selfCpu = processes.FirstOrDefault(p => p.ProcessId == self.Id)?.CpuPercent;
            if (selfCpu.HasValue) metrics.Add(new("collector.cpu.percent", "Aplicativo", selfCpu, "%", "Process.TotalProcessorTime",
                "Delta de tempo CPU do próprio processo normalizado por processadores ativos do sistema", timestamp, actualInterval,
                Kind: MeasurementKind.Derived, Aggregation: Aggregation.IntervalAverage));
        }
        return new(timestamp, TelemetryQuality.MarkDiskLatencyWithoutIo(metrics), processes);

        MetricSample Missing(string key, string component, string unit, string source, Exception error) =>
            MetricSample.Missing(key, component, unit, source, timestamp, WindowsHardwareDiscovery.Status(error), error.Message);
        void MemoryMetric(string key, double value, string method) => metrics.Add(new(key, "RAM", value, "bytes",
            "GetPerformanceInfo (psapi.dll)", method, timestamp, TimeSpan.Zero,
            Limitation: "Memória comprometida difere de RAM residente; o limite de commit inclui RAM e arquivos de paginação."));
    }

    [SupportedOSPlatform("windows")]
    private void InitializeCounters()
    {
        _initialized = true;
        _nvml = new NvmlClient();
        var names = new CounterNames();
        var count = NativeWindows.LogicalProcessorCount;
        if (count > 0) _logicalProcessorCount = (int)count;
        _useTotalCounter = count > 64;
        if (_useTotalCounter) Bind("cpu.total.percent", "CPU", "%", "Processor Information", "% Processor Time", "_Total",
            limitation: "Média do contador Processor Information em todos os grupos; não identifica thread crítica.");
        try
        {
            var category = new PerformanceCounterCategory(names.Resolve("Processor Information"));
            var instances = category.GetInstanceNames().Where(n => !n.Contains("_Total", StringComparison.OrdinalIgnoreCase))
                .OrderBy(ParseCpu).Take(1024).ToArray();
            if (instances.Length == 0) throw new InvalidOperationException("Nenhum processador lógico enumerado.");
            for (var index = 0; index < instances.Length; index++)
            {
                var instance = instances[index];
                Bind("cpu.logical." + index + ".percent", "CPU lógico " + instance, "%", "Processor Information", "% Processor Time", instance,
                    limitation: "Índice do aplicativo mapeado à instância de grupo,lógico na origem; média do intervalo.");
            }
            Bind("cpu.dpc.percent", "CPU", "%", "Processor Information", "% DPC Time", "_Total",
                limitation: "Tempo agregado; não fornece duração de cada DPC nem identifica drivers. Use ETW para investigação.");
            Bind("cpu.interrupt.percent", "CPU", "%", "Processor Information", "% Interrupt Time", "_Total");
            Bind("cpu.interrupts.per_second", "CPU", "interrupções/s", "Processor Information", "Interrupts/sec", "_Total");
            Bind("cpu.frequency.reported.mhz", "CPU", "MHz", "Processor Information", "Processor Frequency", "_Total", gauge: true,
                limitation: "Frequência reportada pelo contador; não é clock efetivo de cada núcleo nem evidência isolada de throttling.");
            Bind("cpu.performance.nominal.percent", "CPU", "% nominal", "Processor Information", "% Processor Performance", "_Total",
                limitation: "Desempenho relativo à frequência nominal; pode exceder 100%; não mede temperatura.");
        }
        catch (Exception error) { Unavailable("cpu.logical.telemetry", "CPU", "%", error); }
        Bind("cpu.queue.length", "CPU", "threads", "System", "Processor Queue Length", gauge: true,
            limitation: "Threads prontas aguardando execução; leitura instantânea não comprova gargalo.");
        Bind("cpu.context_switches.per_second", "CPU", "trocas/s", "System", "Context Switches/sec");
        Bind("memory.page_reads.per_second", "RAM", "leituras/s", "Memory", "Page Reads/sec", limitation:
            "Operações de leitura para resolver hard faults; podem ler arquivos mapeados, não apenas pagefile.");
        Bind("memory.pages_input.per_second", "RAM", "páginas/s", "Memory", "Pages Input/sec");
        Bind("memory.page_writes.per_second", "RAM", "escritas/s", "Memory", "Page Writes/sec");
        try
        {
            var category = new PerformanceCounterCategory(names.Resolve("PhysicalDisk"));
            foreach (var instance in category.GetInstanceNames().Where(n => n != "_Total").Order().Take(128))
            {
                var diskIndex = instance.Split(' ', 2)[0];
                if (!int.TryParse(diskIndex, out _)) diskIndex = "instance_" + string.Concat(instance.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
                var prefix = "disk." + diskIndex + ".";
                var component = "Disco físico " + instance;
                Bind(prefix + "read.bytes_per_second", component, "bytes/s", "PhysicalDisk", "Disk Read Bytes/sec", instance);
                Bind(prefix + "write.bytes_per_second", component, "bytes/s", "PhysicalDisk", "Disk Write Bytes/sec", instance);
                Bind(prefix + "read.operations_per_second", component, "I/O/s", "PhysicalDisk", "Disk Reads/sec", instance);
                Bind(prefix + "write.operations_per_second", component, "I/O/s", "PhysicalDisk", "Disk Writes/sec", instance);
                Bind(prefix + "queue.length", component, "I/O", "PhysicalDisk", "Current Disk Queue Length", instance, gauge: true);
                Bind(prefix + "read.latency.ms", component, "ms", "PhysicalDisk", "Avg. Disk sec/Read", instance, scale: 1000,
                    limitation: "Média por I/O concluído no intervalo; sem I/O, zero não mede latência ociosa. Inclui a pilha Windows/driver.");
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
                _ = reader.NextValue(); // Open/prime off the UI thread. Rate outputs still wait for next collection.
                _counters.Add(new(key, component, unit, reader, category + "\\" + instance + "\\" + counter, scale, gauge, limitation));
            }
            catch (Exception error) { reader?.Dispose(); Unavailable(key, component, unit, error); }
        }
    }

    private void Unavailable(string key, string component, string unit, Exception error) =>
        _unavailable.Add((key, component, unit, "Windows Performance Counters", WindowsHardwareDiscovery.Status(error), error.Message));

    private static (int Group, int Cpu) ParseCpu(string instance)
    {
        var pieces = instance.Split(',');
        return pieces.Length == 2 && int.TryParse(pieces[0], out var group) && int.TryParse(pieces[1], out var cpu) ?
            (group, cpu) : (0, int.TryParse(instance, out var fallback) ? fallback : int.MaxValue);
    }

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<ProcessSample> ReadProcesses(long ticks, DateTimeOffset timestamp, CancellationToken token, List<MetricSample> metrics)
    {
        var output = new List<ProcessSample>();
        var active = new HashSet<(int Pid, long Start)>();
        var denied = 0;
        Process[] snapshots;
        try { snapshots = Process.GetProcesses(); }
        catch (Exception error)
        {
            metrics.Add(MetricSample.Missing("process.telemetry", "Processos", "", "Process", timestamp, WindowsHardwareDiscovery.Status(error), error.Message));
            return output;
        }
        try
        {
            foreach (var process in snapshots.Take(4096))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var key = (process.Id, process.StartTime.ToUniversalTime().Ticks);
                    var cpu = process.TotalProcessorTime;
                    var elapsed = _processes.TryGetValue(key, out var previous) ? Stopwatch.GetElapsedTime(previous.Timestamp, ticks).TotalSeconds : 0;
                    double? percent = elapsed > 0 && cpu >= previous.Cpu ?
                        Math.Clamp((cpu - previous.Cpu).TotalSeconds / elapsed / _logicalProcessorCount * 100, 0, 100) : null;
                    output.Add(new(process.Id, process.ProcessName, percent, process.WorkingSet64, timestamp,
                        percent.HasValue ? "CPU normalizada pelos processadores lógicos ativos do sistema; working set inclui páginas compartilhadas." :
                        "Primeira leitura, processo novo ou contador reiniciado; CPU requer duas amostras com mesma criação/PID."));
                    active.Add(key);
                    _processes[key] = (cpu, ticks);
                }
                catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException or UnauthorizedAccessException or System.Security.SecurityException)
                { denied++; /* Exiting/protected processes must not abort collection. */ }
            }
        }
        finally { foreach (var process in snapshots) process.Dispose(); }
        foreach (var key in _processes.Keys.Where(k => !active.Contains(k)).ToArray()) _processes.Remove(key);
        metrics.Add(new("process.unreadable.count", "Processos", denied, "processos", "Process", "Processos protegidos, encerrados ou sem acesso neste lote",
            timestamp, TimeSpan.Zero, Limitation: "Sem dados destes processos; não representa ausência de atividade."));
        metrics.Add(new("process.omitted.count", "Processos", Math.Max(0, snapshots.Length - 4096), "processos", "Process",
            "Processos omitidos pelo limite conservador de enumeração", timestamp, TimeSpan.Zero));
        var selfId = Environment.ProcessId;
        var retained = output.OrderByDescending(p => p.CpuPercent ?? -1).ThenByDescending(p => p.WorkingSetBytes).Take(255).ToList();
        var self = output.FirstOrDefault(p => p.ProcessId == selfId);
        if (self is not null && retained.All(p => p.ProcessId != selfId)) retained.Add(self);
        return retained;
    }

    private void ReadNetworks(long ticks, DateTimeOffset timestamp, List<MetricSample> metrics)
    {
        var active = new HashSet<string>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback).Take(128))
            {
                var key = "network." + adapter.Id + ".";
                try
                {
                    var stats = adapter.GetIPv4Statistics();
                    var interval = _networks.TryGetValue(adapter.Id, out var previous) ? Stopwatch.GetElapsedTime(previous.Timestamp, ticks) : TimeSpan.Zero;
                    if (interval > TimeSpan.Zero && stats.BytesReceived >= previous.Received && stats.BytesSent >= previous.Sent)
                    {
                        Rate("receive.bytes_per_second", (stats.BytesReceived - previous.Received) / interval.TotalSeconds);
                        Rate("send.bytes_per_second", (stats.BytesSent - previous.Sent) / interval.TotalSeconds);
                    }
                    else
                    {
                        foreach (var suffix in new[] { "receive.bytes_per_second", "send.bytes_per_second" })
                            metrics.Add(MetricSample.Missing(key + suffix, adapter.Name, "bytes/s", "NetworkInterface.GetIPv4Statistics", timestamp,
                                Availability.Collecting, "Necessárias duas amostras; contador pode reiniciar com a conexão."));
                    }
                    metrics.Add(new(key + "link.bits_per_second", adapter.Name, adapter.Speed, "bits/s", "NetworkInterface.Speed",
                        "Velocidade reportada do link", timestamp, TimeSpan.Zero, Limitation: "Não representa throughput disponível nem latência externa."));
                    metrics.Add(new(key + "receive.errors.total", adapter.Name, stats.IncomingPacketsWithErrors, "pacotes", "NetworkInterface.GetIPv4Statistics",
                        "Contagem acumulada reportada pelo adaptador", timestamp, TimeSpan.Zero, Aggregation: Aggregation.Cumulative));
                    metrics.Add(new(key + "send.errors.total", adapter.Name, stats.OutgoingPacketsWithErrors, "pacotes", "NetworkInterface.GetIPv4Statistics",
                        "Contagem acumulada reportada pelo adaptador", timestamp, TimeSpan.Zero, Aggregation: Aggregation.Cumulative));
                    _networks[adapter.Id] = (stats.BytesReceived, stats.BytesSent, ticks);
                    active.Add(adapter.Id);
                    void Rate(string suffix, double rate) => metrics.Add(new(key + suffix, adapter.Name, rate, "bytes/s",
                        "NetworkInterface.GetIPv4Statistics", "Delta de bytes / tempo monotônico transcorrido", timestamp, interval,
                        Kind: MeasurementKind.Derived, Aggregation: Aggregation.IntervalAverage));
                }
                catch (Exception error) { metrics.Add(MetricSample.Missing(key + "telemetry", adapter.Name, "", "NetworkInterface", timestamp,
                    WindowsHardwareDiscovery.Status(error), error.Message)); }
            }
        }
        catch (Exception error) { metrics.Add(MetricSample.Missing("network.telemetry", "Rede", "", "NetworkInterface", timestamp,
            WindowsHardwareDiscovery.Status(error), error.Message)); }
        foreach (var key in _networks.Keys.Where(k => !active.Contains(k)).ToArray()) _networks.Remove(key);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var binding in _counters) binding.Counter.Dispose();
            _counters.Clear();
            _nvml?.Dispose();
            _processes.Clear();
            _networks.Clear();
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    // Performance Counter names are localized on Windows; lookup numeric perflib IDs from English to the current language.
    [SupportedOSPlatform("windows")]
    private sealed class CounterNames
    {
        private readonly Dictionary<string, string> _localized = new(StringComparer.OrdinalIgnoreCase);
        internal CounterNames()
        {
            try
            {
                using var english = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\009");
                using var current = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Perflib\CurrentLanguage");
                if (english?.GetValue("Counter") is not string[] source || current?.GetValue("Counter") is not string[] target) return;
                var lookup = new Dictionary<string, string>();
                for (var index = 0; index + 1 < target.Length; index += 2) lookup[target[index]] = target[index + 1];
                for (var index = 0; index + 1 < source.Length; index += 2)
                    if (lookup.TryGetValue(source[index], out var name)) _localized[source[index + 1]] = name;
            }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or System.IO.IOException)
            { /* English counters still work on English Windows; unavailable ones are recorded per metric. */ }
        }
        internal string Resolve(string english) => _localized.GetValueOrDefault(english, english);
    }
}
