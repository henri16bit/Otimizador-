using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.Versioning;
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
    private readonly ProcessTelemetryState _processes = new();
    private PassiveTelemetrySource? _counters, _nvml, _gpuEngines;
    private readonly Dictionary<string, (long Received, long Sent, long Timestamp)> _networks = [];
    private bool _initialized, _disposed, _useTotalCounter;
    private int _logicalProcessorCount = Environment.ProcessorCount;
    private (ulong Idle, ulong Total)? _cpu;
    private long _lastTimestamp;

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
            _processes.Reset();
            _networks.Clear();
            _counters?.Reset(); _nvml?.Reset(); _gpuEngines?.Reset();
            throw;
        }
        finally { _gate.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private TelemetryBatch CollectWindows(CancellationToken token)
    {
        var stopwatch = Stopwatch.StartNew();
        token.ThrowIfCancellationRequested();
        if (!_initialized) InitializeSources();
        var timestamp = DateTimeOffset.UtcNow;
        var ticks = Stopwatch.GetTimestamp();
        var actualInterval = _lastTimestamp == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_lastTimestamp, ticks);
        var metrics = new List<MetricSample>();
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
        var processes = ReadProcesses(ticks, timestamp, token, metrics);
        ReadNetworks(ticks, timestamp, metrics);
        token.ThrowIfCancellationRequested();
        // Native optional providers never run on the basic collection path or hold its gate.
        metrics.AddRange(_counters!.Poll());
        var nvml = _nvml!.Poll();
        metrics.AddRange(nvml);
        var engines = _gpuEngines!.Poll();
        // Distinct API identities must never be merged or counted as additional physical GPUs.
        if (!nvml.Any(sample => sample.Key.EndsWith(".utilization.percent", StringComparison.Ordinal) &&
            sample.Status == Availability.Available && sample.Value.HasValue))
            metrics.AddRange(engines);
        if (_useTotalCounter && !metrics.Any(sample => sample.Key == "cpu.total.percent"))
            metrics.Add(MetricSample.Missing("cpu.total.percent", "CPU", "%", "Processor Information", timestamp,
                Availability.Collecting, "Máquina com mais de 64 processadores lógicos: aguardando contador que cobre todos os grupos."));
        token.ThrowIfCancellationRequested();
        _lastTimestamp = ticks;
        metrics.Add(new("collector.duration.ms", "Aplicativo", stopwatch.Elapsed.TotalMilliseconds, "ms", "Stopwatch",
            "Tempo da coleta básica, incluindo processos e consulta não bloqueante das fontes opcionais", DateTimeOffset.UtcNow, TimeSpan.Zero,
            Limitation: "Não inclui espera das fontes opcionais em segundo plano, renderização WPF, banco ou processamento posterior. CPU/RAM do aplicativo incluem suas fontes em execução."));
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
    private void InitializeSources()
    {
        var count = NativeWindows.LogicalProcessorCount;
        if (count > 0) _logicalProcessorCount = (int)count;
        _useTotalCounter = count > 64;
        _counters = new("windows.counters.telemetry", "Contadores Windows", "Windows Performance Counters", () => new WindowsCounterSource());
        _nvml = new("gpu.telemetry", "GPU", "NVIDIA NVML", () => new NvmlClient());
        _gpuEngines = new("gpu.wddm.telemetry", "GPU WDDM", "Windows GPU Engine", () => new GpuEngineSource());
        _initialized = true;
    }

    /// <summary>Invalidate baselines and in-flight optional results without waiting for native providers.</summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _lastTimestamp = 0; _cpu = null; _processes.Reset(); _networks.Clear();
            _counters?.Reset(); _nvml?.Reset(); _gpuEngines?.Reset();
        }
        finally { _gate.Release(); }
    }

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<ProcessSample> ReadProcesses(long ticks, DateTimeOffset timestamp, CancellationToken token, List<MetricSample> metrics)
    {
        var output = new List<ProcessSample>();
        _processes.BeginBatch();
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
                    var sample = _processes.Read(process.Id, () => process.ProcessName,
                        () => process.StartTime.ToUniversalTime().Ticks, () => process.TotalProcessorTime,
                        () => process.WorkingSet64, timestamp, ticks, _logicalProcessorCount, out var partial);
                    output.Add(sample);
                    if (partial) denied++;
                }
                catch (Exception error) when (ProcessTelemetryState.CanSkip(error))
                { denied++; /* An exited process may no longer expose even its PID. */ }
            }
        }
        finally { foreach (var process in snapshots) process.Dispose(); }
        _processes.EndBatch();
        metrics.Add(new("process.unreadable.count", "Processos", denied, "processos", "Process", "Processos com ao menos um campo inacessível ou encerrados neste lote",
            timestamp, TimeSpan.Zero, Limitation: "Nome/PID/memória acessíveis são preservados mesmo sem CPU. Campos ausentes não representam ausência de atividade."));
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
                    var operational = adapter.OperationalStatus;
                    metrics.Add(new(key + "operational.state", adapter.Name, (int)operational, "estado", "NetworkInterface.OperationalStatus",
                        "Estado operacional do adaptador: " + operational, timestamp, TimeSpan.Zero,
                        Limitation: "1 = conectado; 2 = desconectado; demais códigos seguem OperationalStatus. Conectado não prova acesso à Internet."));
                    if (operational != OperationalStatus.Up)
                    {
                        foreach (var suffix in new[] { "receive.bytes_per_second", "send.bytes_per_second" })
                            metrics.Add(MetricSample.Missing(key + suffix, adapter.Name, "bytes/s", "NetworkInterface", timestamp,
                                Availability.InsufficientData, "Adaptador " + operational + "; não se presume tráfego zero."));
                        metrics.Add(MetricSample.Missing(key + "link.bits_per_second", adapter.Name, "bits/s", "NetworkInterface", timestamp,
                            Availability.InsufficientData, "Link sem conexão ativa; velocidade não utilizada."));
                        _networks.Remove(adapter.Id);
                        continue;
                    }
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
            _counters?.Dispose(); _nvml?.Dispose(); _gpuEngines?.Dispose();
            _processes.Reset();
            _networks.Clear();
        }
        finally { _gate.Release(); }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

}
