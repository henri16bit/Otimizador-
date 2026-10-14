using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

/// <summary>Passive, best effort inventory. SMBIOS/WMI values are not SPD or physical sensors.</summary>
public sealed class WindowsHardwareDiscovery : IHardwareDiscovery
{
    public Task<HardwareInventory> DiscoverAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => OperatingSystem.IsWindows() ? DiscoverWindows(cancellationToken) :
            new HardwareInventory(DateTimeOffset.UtcNow, [], [new("Inventário Windows", Availability.Unsupported,
                "WMI e Win32 exigem Windows; nenhum hardware foi simulado.")]), cancellationToken);

    [SupportedOSPlatform("windows")]
    private static HardwareInventory DiscoverWindows(CancellationToken token)
    {
        var components = new List<HardwareComponent>();
        var capabilities = new List<Capability>();
        var osRows = Query("Win32_OperatingSystem", capabilities, token);
        foreach (var row in osRows)
            Add("system", "Sistema", row, "Caption", "Version", "BuildNumber", "OSArchitecture", "LastBootUpTime", "SystemDrive");
        foreach (var row in Query("Win32_ComputerSystem", capabilities, token))
            Add("computer", "Sistema", row, "Model", "Manufacturer", "TotalPhysicalMemory", "NumberOfLogicalProcessors");
        foreach (var row in Query("Win32_Processor", capabilities, token))
            Add("cpu." + Value(row, "DeviceID"), "CPU", row, "Name", "Manufacturer", "NumberOfCores", "NumberOfLogicalProcessors",
                "MaxClockSpeed", "CurrentClockSpeed", "SocketDesignation", "Architecture", "ProcessorId");
        foreach (var row in Query("Win32_BaseBoard", capabilities, token))
        {
            var facts = Facts(row, "Win32_BaseBoard", "Manufacturer", "Product", "Version").ToList();
            facts.Add(Unknown("Chipset", "Win32_BaseBoard", "O modelo da placa não demonstra o chipset em uso."));
            components.Add(new("board." + Value(row, "Tag"), "Placa-mãe", Value(row, "Product") ?? "Placa-mãe", facts));
        }
        foreach (var row in Query("Win32_BIOS", capabilities, token))
            Add("bios", "BIOS", row, "Manufacturer", "SMBIOSBIOSVersion", "ReleaseDate", "Version", "SMBIOSMajorVersion", "SMBIOSMinorVersion");
        foreach (var row in Query("Win32_PhysicalMemory", capabilities, token))
        {
            var facts = Facts(row, "Win32_PhysicalMemory", "Manufacturer", "PartNumber", "Capacity", "DeviceLocator", "BankLabel",
                "ConfiguredClockSpeed", "Speed", "SMBIOSMemoryType", "DataWidth", "TotalWidth").ToList();
            facts.Add(new("Interpretação de velocidade", "Speed = capacidade reportada pelo SMBIOS; ConfiguredClockSpeed = configuração reportada pelo SMBIOS",
                "Microsoft Win32_PhysicalMemory", Limitation: "ConfiguredClockSpeed documenta MHz; Speed herda unidade ns no esquema CIM, embora SMBIOS DDR moderno costume reportar MT/s. Preservar dados brutos sem converter para clock elétrico, SPD ou XMP. Campos podem estar incorretos."));
            facts.Add(Unknown("Timings / SPD / XMP", "Win32_PhysicalMemory", "Não expostos pela API usada; não inferidos da marca ou velocidade."));
            facts.Add(Unknown("Modo de canais", "Win32_PhysicalMemory", "Dois módulos ou BankLabel não comprovam dual-channel."));
            components.Add(new("ram." + (Value(row, "DeviceLocator") ?? components.Count.ToString(CultureInfo.InvariantCulture)), "RAM",
                Value(row, "PartNumber") ?? "Módulo RAM", facts));
        }
        foreach (var row in Query("Win32_VideoController", capabilities, token))
        {
            var facts = Facts(row, "Win32_VideoController", "Name", "AdapterCompatibility", "DriverVersion", "DriverDate", "PNPDeviceID").ToList();
            facts.Add(Unknown("VRAM precisa", "Win32_VideoController", "AdapterRAM é um inteiro de 32 bits e não representa corretamente placas acima de 4 GiB; consultar NVML quando disponível."));
            components.Add(new("gpu.wmi." + Value(row, "DeviceID"), "GPU", Value(row, "Name") ?? "GPU", facts));
        }
        DiscoverStorage(components, capabilities, token, osRows.FirstOrDefault());
        var adapters = Query("Win32_NetworkAdapter", capabilities, token).Where(r => r.GetValueOrDefault("PhysicalAdapter") is true).ToList();
        var drivers = Query("Win32_PnPSignedDriver", capabilities, token)
            .Where(r => Value(r, "DeviceClass")?.Equals("NET", StringComparison.OrdinalIgnoreCase) is true).ToList();
        foreach (var row in adapters)
        {
            var facts = Facts(row, "Win32_NetworkAdapter", "Name", "Manufacturer", "NetConnectionID", "NetConnectionStatus", "Speed", "PNPDeviceID", "GUID").ToList();
            var driver = drivers.FirstOrDefault(d => string.Equals(Value(d, "DeviceID"), Value(row, "PNPDeviceID"), StringComparison.OrdinalIgnoreCase));
            facts.AddRange(driver is null ? [Unknown("DriverVersion", "Win32_PnPSignedDriver", "Driver não identificado.")] :
                Facts(driver, "Win32_PnPSignedDriver", "DriverVersion", "DriverDate", "DriverProviderName"));
            facts.Add(new("Adaptador em uso", "Não determinado por tráfego instantâneo", "Inventário", Availability.InsufficientData,
                "Status conectado não identifica qual adaptador transporta um processo ou rota específica."));
            components.Add(new("network." + Value(row, "GUID"), "Rede", Value(row, "Name") ?? "Adaptador", facts));
        }
        foreach (var monitor in Query("WmiMonitorID", capabilities, token, @"root\wmi"))
        {
            components.Add(new("monitor." + Value(monitor, "InstanceName"), "Monitor", DecodeEdid(monitor.GetValueOrDefault("UserFriendlyName")) ?? "Monitor EDID",
            [new("Fabricante EDID", DecodeEdid(monitor.GetValueOrDefault("ManufacturerName")), "WmiMonitorID"),
             new("Modelo EDID", DecodeEdid(monitor.GetValueOrDefault("ProductCodeID")), "WmiMonitorID"),
             new("Ativo", Value(monitor, "Active"), "WmiMonitorID"),
             new("Instância", Value(monitor, "InstanceName"), "WmiMonitorID")]));
        }
        try { components.AddRange(NativeWindows.GetDisplays()); }
        catch (Exception error) { capabilities.Add(new("Modo de exibição", Status(error), error.Message)); }
        try
        {
            if (NativeWindows.TryGetFirmwareType(out var firmware))
                components.Add(new("firmware", "Sistema", "Inicialização", [new("Firmware", firmware == 2 ? "UEFI" : firmware == 1 ? "BIOS legado" : "Desconhecido", "GetFirmwareType")]));
            if (NativeWindows.TryGetPowerScheme(out var scheme))
                components.Add(new("power", "Sistema", "Energia", [new("Plano ativo GUID", scheme.ToString(), "PowerGetActiveScheme")]));
        }
        catch (Exception error) { capabilities.Add(new("Firmware / energia", Status(error), error.Message)); }
        using (var nvml = new NvmlClient())
        {
            capabilities.Add(new("NVIDIA NVML", nvml.Status, nvml.Detail));
            components.AddRange(nvml.Inventory());
        }
        capabilities.Add(new("Sensores CPU / SMART NVMe", Availability.Unsupported,
            "Não há driver kernel próprio. Temperatura CPU, timings, SPD direto, SMART/desgaste NVMe exigem provedores adicionais; indisponíveis nesta implementação."));
        capabilities.Add(new("Topologia CPU", Availability.Available,
            "Núcleos e threads vêm de Win32_Processor; a coleta por lógico usa instâncias Processor Information, preservando grupo e índice na origem."));
        return new(DateTimeOffset.UtcNow, components, capabilities);

        void Add(string id, string category, Dictionary<string, object?> row, params string[] names) =>
            components.Add(new(id, category, Value(row, "Name") ?? Value(row, "Caption") ?? category,
                Facts(row, "WMI " + (category switch { "CPU" => "Win32_Processor", "BIOS" => "Win32_BIOS", _ => category }), names)));
    }

    [SupportedOSPlatform("windows")]
    private static void DiscoverStorage(List<HardwareComponent> components, List<Capability> capabilities,
        CancellationToken token, Dictionary<string, object?>? os)
    {
        var disks = Query("Win32_DiskDrive", capabilities, token);
        var partitions = Query("Win32_DiskPartition", capabilities, token);
        var volumes = Query("Win32_LogicalDisk", capabilities, token);
        var partitionVolumes = Query("Win32_LogicalDiskToPartition", capabilities, token);
        var pagefiles = Query("Win32_PageFileUsage", capabilities, token);
        foreach (var disk in disks)
        {
            var facts = Facts(disk, "Win32_DiskDrive", "Model", "FirmwareRevision", "Size", "InterfaceType", "MediaType", "Index", "DeviceID", "PNPDeviceID").ToList();
            var index = Value(disk, "Index");
            foreach (var partition in partitions.Where(p => Value(p, "DiskIndex") == index))
            {
                facts.Add(new("Partição", Value(partition, "DeviceID"), "Win32_DiskPartition"));
                foreach (var link in partitionVolumes.Where(l => ReferenceId(Value(l, "Antecedent")) == Value(partition, "DeviceID")))
                {
                    var volumeId = ReferenceId(Value(link, "Dependent"));
                    var volume = volumes.FirstOrDefault(v => Value(v, "DeviceID") == volumeId);
                    if (volume is null) continue;
                    facts.Add(new("Volume " + volumeId, $"{Value(volume, "FileSystem")}; capacidade {Value(volume, "Size")} bytes; livre {Value(volume, "FreeSpace")} bytes", "Win32_LogicalDiskToPartition / Win32_LogicalDisk"));
                    if (Value(os, "SystemDrive") == volumeId)
                        facts.Add(new("Volume do Windows", volumeId, "Win32_OperatingSystem.SystemDrive + associação WMI"));
                    foreach (var pagefile in pagefiles.Where(p => Value(p, "Name")?.StartsWith(volumeId ?? "?", StringComparison.OrdinalIgnoreCase) is true))
                        facts.Add(new("Arquivo de paginação", Value(pagefile, "Name"), "Win32_PageFileUsage"));
                }
            }
            facts.Add(Unknown("Temperatura / SMART / desgaste", "Win32_DiskDrive", "Não expostos por este provedor. Ausência de leitura não prova saúde da unidade."));
            facts.Add(new("Interface", Value(disk, "InterfaceType"), "Win32_DiskDrive", Limitation:
                "SCSI pode ser a apresentação do driver de um SSD NVMe; InterfaceType sozinho não identifica NVMe."));
            components.Add(new("disk." + index, "Armazenamento", Value(disk, "Model") ?? "Disco", facts));
        }
        // The storage namespace reports actual bus type when its provider is present, independently of legacy SCSI labels.
        foreach (var physical in Query("MSFT_PhysicalDisk", capabilities, token, @"root\Microsoft\Windows\Storage"))
        {
            components.Add(new("storage." + Value(physical, "DeviceId"), "Armazenamento", Value(physical, "FriendlyName") ?? "Disco físico (Storage)",
                Facts(physical, "MSFT_PhysicalDisk (BusType 17 = NVMe)", "DeviceId", "FriendlyName", "BusType", "MediaType", "Size", "FirmwareVersion", "HealthStatus", "OperationalStatus")));
        }
        capabilities.Add(new("Localização do jogo", Availability.InsufficientData,
            "Selecione o executável no módulo de jogos; o inventário não varre pastas pessoais nem presume a unidade de instalação."));
    }

    internal static string? Value(Dictionary<string, object?>? row, string name)
    {
        var item = row?.GetValueOrDefault(name);
        return item is null ? null : item is Array values ? string.Join(", ", values.Cast<object>()) : Convert.ToString(item, CultureInfo.InvariantCulture)?.Trim();
    }

    private static IReadOnlyList<HardwareFact> Facts(Dictionary<string, object?> row, string source, params string[] names) =>
        names.Select(name => string.IsNullOrWhiteSpace(Value(row, name)) || (name is "ConfiguredClockSpeed" or "Speed" && Value(row, name) == "0") ?
            Unknown(name, source, "O provedor não retornou o campo ou informa velocidade desconhecida (zero).") :
            new HardwareFact(name, Value(row, name), source)).ToArray();

    private static HardwareFact Unknown(string name, string source, string reason) => new(name, null, source, Availability.Unsupported, reason);

    private static string? DecodeEdid(object? value) => value is ushort[] words ? new string(words.Where(v => v != 0 && v <= 127).Select(v => (char)v).ToArray()) : null;

    private static string? ReferenceId(string? reference)
    {
        if (reference is null) return null;
        var start = reference.IndexOf("DeviceID=\"", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return null;
        return reference[(start + 10)..].TrimEnd('"').Replace("\\\\", "\\", StringComparison.Ordinal);
    }

    internal static Availability Status(Exception error)
    {
        if (error is UnauthorizedAccessException or System.Security.SecurityException ||
            error is System.ComponentModel.Win32Exception { NativeErrorCode: 5 }) return Availability.PermissionRequired;
        if (OperatingSystem.IsWindows() && error is ManagementException management)
        {
            if (management.ErrorCode == ManagementStatus.AccessDenied) return Availability.PermissionRequired;
            if (management.ErrorCode is ManagementStatus.InvalidClass or ManagementStatus.InvalidNamespace or ManagementStatus.NotSupported)
                return Availability.Unsupported;
        }
        return Availability.ReadError;
    }

    [SupportedOSPlatform("windows")]
    internal static List<Dictionary<string, object?>> Query(string className, List<Capability> capabilities, CancellationToken token,
        string scope = @"root\cimv2")
    {
        var rows = new List<Dictionary<string, object?>>();
        try
        {
            token.ThrowIfCancellationRequested();
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery("SELECT * FROM " + className),
                new System.Management.EnumerationOptions { ReturnImmediately = false, Timeout = TimeSpan.FromSeconds(10) });
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
            {
                using (item)
                {
                    token.ThrowIfCancellationRequested();
                    if (rows.Count >= 4096) break;
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (PropertyData property in item.Properties) row[property.Name] = property.Value;
                    rows.Add(row);
                }
            }
            capabilities.Add(new(className, rows.Count > 0 ? Availability.Available : Availability.InsufficientData,
                $"{rows.Count} registro(s); provedor {scope}."));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { capabilities.Add(new(className, Status(error), error.Message)); }
        return rows;
    }
}
