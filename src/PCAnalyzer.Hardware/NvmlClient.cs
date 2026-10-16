using System.Runtime.InteropServices;
using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

/// <summary>Loads the NVIDIA user-mode DLL installed with its display driver, never a kernel driver.</summary>
internal sealed class NvmlClient : IPassiveTelemetrySource
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NoArgs();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Count(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Handle(uint index, out nint device);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Text(nint device, nint text, uint size);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Scalar(nint device, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Scalar64(nint device, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Indexed(nint device, uint index, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Memory(nint device, out MemoryInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Utilization(nint device, out UtilizationInfo info);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ComputeCapability(nint device, out int major, out int minor);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryInfo { public ulong Total, Free, Used; }
    [StructLayout(LayoutKind.Sequential)] private struct UtilizationInfo { public uint Gpu, Memory; }
    private sealed record Device(uint Index, nint Handle, string Name, uint? Architecture, int? CudaMajor, int? CudaMinor);
    private readonly List<Device> _devices = [];
    private nint _library;
    private bool _initialized;
    public Availability Status { get; private set; } = Availability.ToolMissing;
    public string Detail { get; private set; } = "nvml.dll não encontrada em locais oficiais do driver NVIDIA.";

    public NvmlClient()
    {
        if (!OperatingSystem.IsWindows()) { Status = Availability.Unsupported; Detail = "NVML Windows indisponível nesta plataforma."; return; }
        try
        {
            var paths = new[] { Path.Combine(Environment.SystemDirectory, "nvml.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll") };
            foreach (var path in paths)
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out _library)) break;
            if (_library == 0) return;
            var initialize = Export<NoArgs>("nvmlInit_v2");
            var getCount = Export<Count>("nvmlDeviceGetCount_v2");
            var getHandle = Export<Handle>("nvmlDeviceGetHandleByIndex_v2");
            if (initialize is null || getCount is null || getHandle is null)
            { Status = Availability.Unsupported; Detail = "A DLL NVML não expõe a API v2 necessária."; return; }
            var result = initialize();
            if (result != 0) { Status = ToStatus(result); Detail = "nvmlInit_v2: " + Error(result); return; }
            _initialized = true;
            result = getCount(out var count);
            if (result != 0) { Status = ToStatus(result); Detail = "nvmlDeviceGetCount_v2: " + Error(result); return; }
            for (uint index = 0; index < Math.Min(count, 32u); index++)
                if (getHandle(index, out var device) == 0)
                {
                    var architectureApi = Export<Scalar>("nvmlDeviceGetArchitecture");
                    uint architecture = 0;
                    var architectureKnown = architectureApi is not null && architectureApi(device, out architecture) == 0 && architecture is not (0 or uint.MaxValue);
                    var capabilityApi = Export<ComputeCapability>("nvmlDeviceGetCudaComputeCapability");
                    int major = 0, minor = 0;
                    var capabilityKnown = capabilityApi is not null && capabilityApi(device, out major, out minor) == 0;
                    _devices.Add(new(index, device, ReadText(device, "nvmlDeviceGetName") ?? "GPU NVIDIA",
                        architectureKnown ? architecture : null, capabilityKnown ? major : null, capabilityKnown ? minor : null));
                }
            Status = _devices.Count > 0 ? Availability.Available : Availability.InsufficientData;
            Detail = $"{_devices.Count} GPU(s). NVML pode não oferecer todos os sensores em placas GeForce/WDDM.";
        }
        catch (Exception error) { Status = Availability.ReadError; Detail = error.Message; }
    }

    public IReadOnlyList<HardwareComponent> Inventory()
    {
        var output = new List<HardwareComponent>();
        foreach (var device in _devices)
        {
            var facts = new List<HardwareFact> { new("Nome", device.Name, "NVML nvmlDeviceGetName") };
            var getMemory = Export<Memory>("nvmlDeviceGetMemoryInfo");
            if (getMemory is not null && getMemory(device.Handle, out var memory) == 0)
                facts.Add(new("VRAM total bytes", memory.Total.ToString(), "NVML nvmlDeviceGetMemoryInfo", Limitation:
                    "Memória reportada pelo driver; reserva do sistema/driver pode afetar disponibilidade."));
            else facts.Add(new("VRAM total bytes", null, "NVML", Availability.Unsupported, "Leitura não suportada pelo driver."));
            var uuid = ReadText(device.Handle, "nvmlDeviceGetUUID");
            facts.Add(new("UUID", uuid, "NVML nvmlDeviceGetUUID", uuid is null ? Availability.Unsupported : Availability.Available));
            facts.Add(new("Identificação", "Índice NVML separado de DeviceID WMI", "NVIDIA NVML", Limitation:
                "Não se presume equivalência entre índices das duas APIs; não somar os registros WMI e NVML como GPUs físicas distintas."));
            output.Add(new("gpu." + device.Index, "GPU (NVML)", device.Name, facts));
        }
        return output;
    }

    public IReadOnlyList<MetricSample> Read(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Read(DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<MetricSample> Read(DateTimeOffset timestamp)
    {
        var metrics = new List<MetricSample>();
        if (_devices.Count == 0)
        {
            metrics.Add(MetricSample.Missing("gpu.telemetry", "GPU", "", "NVIDIA NVML", timestamp, Status, Detail));
            return metrics;
        }
        foreach (var device in _devices)
        {
            var prefix = "gpu." + device.Index + ".";
            try
            {
                var util = Export<Utilization>("nvmlDeviceGetUtilizationRates");
                UtilizationInfo utilization = default;
                var result = util is null ? 3 : util(device.Handle, out utilization);
                if (result == 0)
                {
                    Add("utilization.percent", utilization.Gpu, "%", "nvmlDeviceGetUtilizationRates", Aggregation.IntervalAverage,
                        "Janela interna do driver (varia por produto, aproximadamente 1/6–1 s); não é média da sessão.");
                    Add("memory.activity.percent", utilization.Memory, "%", "nvmlDeviceGetUtilizationRates", Aggregation.IntervalAverage,
                        "Percentual da janela interna com leitura/escrita de memória; não é ocupação da VRAM.");
                }
                else
                {
                    Missing("utilization.percent", "%", "nvmlDeviceGetUtilizationRates", result);
                    Missing("memory.activity.percent", "%", "nvmlDeviceGetUtilizationRates", result);
                }
                var memory = Export<Memory>("nvmlDeviceGetMemoryInfo");
                MemoryInfo info = default;
                result = memory is null ? 3 : memory(device.Handle, out info);
                if (result == 0)
                {
                    Add("memory.used.bytes", info.Used, "bytes", "nvmlDeviceGetMemoryInfo");
                    Add("memory.total.bytes", info.Total, "bytes", "nvmlDeviceGetMemoryInfo");
                    Add("memory.free.bytes", info.Free, "bytes", "nvmlDeviceGetMemoryInfo");
                }
                else
                {
                    foreach (var suffix in new[] { "memory.used.bytes", "memory.total.bytes", "memory.free.bytes" })
                        Missing(suffix, "bytes", "nvmlDeviceGetMemoryInfo", result);
                }
                ReadIndexed("temperature.celsius", "°C", "nvmlDeviceGetTemperature", 0);
                ReadIndexed("clock.graphics.mhz", "MHz", "nvmlDeviceGetClockInfo", 0);
                ReadIndexed("clock.memory.mhz", "MHz", "nvmlDeviceGetClockInfo", 2);
                var powerAverage = PowerIsAveraged(device);
                if (powerAverage.HasValue)
                    ReadScalar("power.watts", "W", "nvmlDeviceGetPowerUsage", 0.001,
                        powerAverage.Value ? "Média interna de 1 s em Ampere (exceto GA100) ou mais recente; inclui GPU e seus circuitos, não o computador." :
                            "Potência instantânea reportada em GA100/arquiteturas antigas; inclui GPU e seus circuitos, não o computador.",
                        powerAverage.Value ? Aggregation.IntervalAverage : Aggregation.Instantaneous,
                        powerAverage.Value ? TimeSpan.FromSeconds(1) : TimeSpan.Zero);
                else metrics.Add(MetricSample.Missing(prefix + "power.watts", device.Name, "W", "NVML", timestamp,
                    Availability.InsufficientData, "Arquitetura/capacidade CUDA indisponível; não é possível identificar se a leitura de potência é instantânea ou média de 1 s."));
                ReadScalar("performance.state", "P-state", "nvmlDeviceGetPerformanceState");
                var reasonsMethod = Export<Scalar64>("nvmlDeviceGetCurrentClocksEventReasons") is not null ?
                    "nvmlDeviceGetCurrentClocksEventReasons" : "nvmlDeviceGetCurrentClocksThrottleReasons";
                var reasons = Export<Scalar64>(reasonsMethod);
                ulong mask = 0;
                result = reasons is null ? 3 : reasons(device.Handle, out mask);
                if (result == 0)
                    Add("clock.limit.flags", mask, "bitmask", reasonsMethod, limitation:
                        "Flags do driver (incluem ociosidade e limitações normais); presença não prova defeito ou throttling térmico.");
                else Missing("clock.limit.flags", "bitmask", reasonsMethod, result);
            }
            catch (Exception error)
            { metrics.Add(MetricSample.Missing(prefix + "telemetry", device.Name, "", "NVML", timestamp, Availability.ReadError, error.Message)); }

            void Add(string suffix, double value, string unit, string method, Aggregation aggregation = Aggregation.Instantaneous, string? limitation = null,
                TimeSpan? samplingInterval = null) =>
                metrics.Add(new(prefix + suffix, device.Name, value, unit, "NVIDIA NVML", method, timestamp, samplingInterval ?? TimeSpan.Zero,
                    Aggregation: aggregation, Limitation: limitation));
            void Missing(string suffix, string unit, string method, int code) => metrics.Add(MetricSample.Missing(prefix + suffix,
                device.Name, unit, "NVIDIA NVML " + method, timestamp, ToStatus(code), Error(code)));
            void ReadIndexed(string suffix, string unit, string method, uint index)
            {
                var reader = Export<Indexed>(method);
                uint value = 0;
                var code = reader is null ? 3 : reader(device.Handle, index, out value);
                if (code == 0) Add(suffix, value, unit, method); else Missing(suffix, unit, method, code);
            }
            void ReadScalar(string suffix, string unit, string method, double scale = 1, string? limitation = null,
                Aggregation aggregation = Aggregation.Instantaneous, TimeSpan? samplingInterval = null)
            {
                var reader = Export<Scalar>(method);
                uint value = 0;
                var code = reader is null ? 3 : reader(device.Handle, out value);
                if (code == 0) Add(suffix, value * scale, unit, method, aggregation, limitation, samplingInterval); else Missing(suffix, unit, method, code);
            }
        }
        return metrics;
    }

    private static bool? PowerIsAveraged(Device device)
    {
        // NVIDIA NVML: NVML_DEVICE_ARCH_AMPERE = 7. GA100 is CUDA capability 8.0.
        if (device.Architecture is null) return null;
        if (device.Architecture < 7) return false;
        if (device.Architecture > 7) return true;
        if (device.CudaMajor is null || device.CudaMinor is null) return null;
        return !(device.CudaMajor == 8 && device.CudaMinor == 0);
    }

    private T? Export<T>(string name) where T : Delegate => _library != 0 && NativeLibrary.TryGetExport(_library, name, out var address)
        ? Marshal.GetDelegateForFunctionPointer<T>(address) : null;

    private string? ReadText(nint device, string function)
    {
        var reader = Export<Text>(function);
        if (reader is null) return null;
        var buffer = Marshal.AllocHGlobal(256);
        try { return reader(device, buffer, 256) == 0 ? Marshal.PtrToStringAnsi(buffer) : null; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static Availability ToStatus(int code) => code switch
    { 0 => Availability.Available, 3 => Availability.Unsupported, 4 => Availability.PermissionRequired, 9 => Availability.ToolMissing, _ => Availability.ReadError };
    private static string Error(int code) => code switch
    { 3 => "Sensor/API não suportado pelo dispositivo ou driver.", 4 => "Permissão insuficiente para esta leitura.",
      9 => "Driver NVIDIA não carregado.", _ => "Retorno NVML " + code + "; consultar referência NVIDIA nvmlReturn_t." };

    public void Dispose()
    {
        if (_library == 0) return;
        if (_initialized) { try { Export<NoArgs>("nvmlShutdown")?.Invoke(); } catch { /* Best-effort release after driver unload. */ } }
        NativeLibrary.Free(_library);
        _library = 0;
        _devices.Clear();
    }
}
