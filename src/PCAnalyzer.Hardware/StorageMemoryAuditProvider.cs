using System.Globalization;
using System.Management;
using System.Runtime.Versioning;
using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

/// <summary>Reads SMBIOS and Storage WMI. Never benchmarks, writes or opens a physical disk for raw access.</summary>
public sealed class StorageMemoryAuditProvider : IPerformanceAuditProvider
{
    public IReadOnlyList<AuditRule> Rules { get; } =
    [
        Rule("memory.modules", "RAM", "Módulos e configuração da memória", "Win32_PhysicalMemory / SMBIOS",
            "Capacidade, localização, fabricante, part number, Speed e ConfiguredClockSpeed.",
            "O firmware pode preencher campos incorretamente. Número de módulos e BankLabel não comprovam canais; não há SPD direto, XMP ou timings.",
            "https://learn.microsoft.com/windows/win32/cimwin32prov/win32-physicalmemory"),
        Rule("storage.physical", "Armazenamento", "Identidade e estado de cada disco", "MSFT_PhysicalDisk / Storage WMI",
            "DeviceId, UniqueId, modelo, firmware, BusType, MediaType, tamanho e saúde reportada.",
            "Estado Healthy reportado pelo provedor não comprova ausência de falhas, desempenho ou estabilidade durante o jogo.",
            "https://learn.microsoft.com/windows-hardware/drivers/storage/msft-physicaldisk"),
        Rule("storage.reliability", "Armazenamento", "Temperatura e confiabilidade por disco", "MSFT_StorageReliabilityCounter + associação física",
            "Associação única entre disco e contadores; temperatura, desgaste, erros e latências máximas quando expostos.",
            "O driver pode omitir campos. Máximas e contadores acumulados não medem uma janela do jogo nem equivalem a um log NVMe SMART completo.",
            "https://learn.microsoft.com/windows-hardware/drivers/storage/msft-storagereliabilitycounter"),
        Rule("storage.location", "Armazenamento", "Volumes, Windows e paginação", "MSFT_Disk / MSFT_Partition / Win32_PageFileUsage",
            "UniqueId físico e lógico iguais e únicos; número de disco, partição e caminho de acesso.",
            "Não associa unidades por semelhança do nome. A localização do jogo exige um executável selecionado; não há busca em pastas pessoais.",
            "https://learn.microsoft.com/windows-hardware/drivers/storage/msft-partition")
    ];

    public Task<IReadOnlyList<AuditObservation>> ReadAsync(CancellationToken ct) => Task.Run<IReadOnlyList<AuditObservation>>(() =>
        OperatingSystem.IsWindows() ? ReadWindows(ct) : Rules.Select(r => Missing(r.Id, r.Id, r.Name, "Sistema",
            Availability.Unsupported, "As consultas SMBIOS e Storage WMI exigem Windows.")).ToArray(), ct);

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<AuditObservation> ReadWindows(CancellationToken ct)
    {
        var observations = new List<AuditObservation>();
        const string storage = @"root\Microsoft\Windows\Storage";
        try
        {
            var memory = Read("Win32_PhysicalMemory", @"root\cimv2", ct);
            var processors = Read("Win32_Processor", @"root\cimv2", ct);
            var boards = Read("Win32_BaseBoard", @"root\cimv2", ct);
            observations.AddRange(ReadMemory(memory.Rows, memory.Status,
                MemoryPlatformFacts(processors.Rows, processors.Status, boards.Rows, boards.Status)));
            var physical = Read("MSFT_PhysicalDisk", storage, ct);
            observations.AddRange(ReadDisks(physical.Rows, physical.Status));
            if (physical.Rows.Count == 0) return observations;
            var reliability = Read("MSFT_StorageReliabilityCounter", storage, ct);
            var links = Read("MSFT_PhysicalDiskToStorageReliabilityCounter", storage, ct);
            observations.AddRange(ReadReliability(physical.Rows, reliability.Rows, links.Rows,
                reliability.Status != Availability.Available ? reliability.Status : links.Status));
            var disks = Read("MSFT_Disk", storage, ct);
            var partitions = Read("MSFT_Partition", storage, ct);
            var system = Read("Win32_OperatingSystem", @"root\cimv2", ct);
            var pagefiles = Read("Win32_PageFileUsage", @"root\cimv2", ct);
            observations.AddRange(ReadLocations(physical.Rows, disks.Rows, partitions.Rows, system.Rows, pagefiles.Rows,
                disks.Status != Availability.Available ? disks.Status : partitions.Status, system.Status, pagefiles.Status));
        }
        catch (OperationCanceledException)
        {
            observations.Add(Missing("storage.location", "cancelled", "Consulta interrompida", "Armazenamento", Availability.InsufficientData,
                "A consulta foi cancelada; leituras já concluídas foram preservadas."));
        }
        return observations;
    }

    internal static IReadOnlyDictionary<string, string> MemoryPlatformFacts(
        IReadOnlyList<Dictionary<string, object?>> processors, Availability processorStatus,
        IReadOnlyList<Dictionary<string, object?>> boards, Availability boardStatus)
    {
        var facts = new Dictionary<string, string>
        {
            ["CpuContextStatus"] = processorStatus.ToString(), ["BaseboardContextStatus"] = boardStatus.ToString(),
            ["PlatformContextSource"] = "Win32_Processor.Name / Win32_BaseBoard.Product; contexto SMBIOS/WMI, não leitura direta do chipset."
        };
        var cpuNames = processors.Select(p => Value(p, "Name")).OfType<string>().Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var boardNames = boards.Select(b => Value(b, "Product")).OfType<string>().Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (processorStatus == Availability.Available && cpuNames.Length == 1) facts["CpuName"] = cpuNames[0];
        if (boardStatus == Availability.Available && boardNames.Length == 1) facts["BaseboardProduct"] = boardNames[0];
        return facts;
    }

    internal static IReadOnlyList<AuditObservation> ReadMemory(IReadOnlyList<Dictionary<string, object?>> rows, Availability status,
        IReadOnlyDictionary<string, string>? platform = null)
    {
        if (rows.Count == 0) return [Missing("memory.modules", "memory", "Módulos de RAM", "Memória", status == Availability.Available ? Availability.InsufficientData : status, "SMBIOS não retornou módulos verificáveis.")];
        return rows.Select((row, index) =>
        {
            var facts = Facts(row, "Manufacturer", "PartNumber", "Capacity", "DeviceLocator", "BankLabel", "ConfiguredClockSpeed", "Speed", "SMBIOSMemoryType", "DataWidth", "TotalWidth");
            foreach (var pair in platform ?? new Dictionary<string, string>()) facts[pair.Key] = pair.Value;
            facts["configured"] = "true";
            facts["SPD"] = "Não lido"; facts["Timings"] = "Não expostos"; facts["Channels"] = "Não verificados"; facts["XMP"] = "Não verificado";
            var locator = Value(row, "DeviceLocator") ?? $"Módulo {index + 1}";
            var configured = Number(row, "ConfiguredClockSpeed");
            // SMBIOS type 17 defines Configured Memory Speed in MT/s. WMI still labels the
            // projection MHz. For a recognized DDR type, show the unmodified firmware transfer
            // rate; never multiply it by two or claim an independently measured electrical clock.
            var ddr = Number(row, "SMBIOSMemoryType") is 18 or 19 or 20 or 24 or 26 or 27 or 28 or 29 or 30 or 34 or 35;
            facts["ConfiguredTransferRateMTs"] = configured is > 0 && ddr ? configured.Value.ToString(CultureInfo.InvariantCulture) : "Não verificada";
            var speed = configured is > 0 ? ddr
                ? $"{configured.Value.ToString(CultureInfo.InvariantCulture)} MT/s configurados, informados pelo SMBIOS"
                : $"configuração SMBIOS {configured.Value.ToString(CultureInfo.InvariantCulture)} (tipo/unidade não confirmados)"
                : "velocidade configurada não informada";
            return new AuditObservation("memory.modules", locator + ":" + index, "Módulo de RAM", locator,
                $"{Value(row, "PartNumber") ?? "Modelo não informado"}; capacidade {Value(row, "Capacity") ?? "não informada"} bytes; {speed}",
                Number(row, "Capacity") is > 0 ? Availability.Available : Availability.InsufficientData,
                "Win32_PhysicalMemory / SMBIOS", facts,
                "Valores brutos preservados: SMBIOS define a velocidade configurada em MT/s, embora a projeção WMI documente MHz. Não é clock elétrico medido. Speed não comprova frequência nominal comercial; SPD, XMP, timings e canais não foram lidos.");
        }).ToArray();
    }

    internal static IReadOnlyList<AuditObservation> ReadDisks(IReadOnlyList<Dictionary<string, object?>> disks, Availability status)
    {
        if (disks.Count == 0) return [Missing("storage.physical", "storage", "Discos físicos", "Armazenamento", status == Availability.Available ? Availability.InsufficientData : status, "O provedor Storage não retornou discos físicos.")];
        return disks.Select((disk, index) =>
        {
            var facts = Facts(disk, "DeviceId", "UniqueId", "FriendlyName", "Model", "FirmwareVersion", "BusType", "MediaType", "Size", "HealthStatus", "OperationalStatus", "PhysicalLocation", "IsWriteCacheEnabled", "IsPowerProtected");
            facts["configured"] = "false";
            var health = Number(disk, "HealthStatus") switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", 5 => "Unknown", _ => "Não informado" };
            facts["effective"] = "Estado informado pelo provedor Storage: " + health + ".";
            facts["BusInterpretation"] = Number(disk, "BusType") switch { 17 => "NVMe", 11 => "SATA", 7 => "USB", 1 => "SCSI", _ => "Outro ou não informado; consulte BusType bruto" };
            return new AuditObservation("storage.physical", Id(disk, index), "Disco físico", DiskName(disk),
                $"{DiskName(disk)}; firmware {Value(disk, "FirmwareVersion") ?? "não informado"}; {facts["BusInterpretation"]}; estado {health}",
                string.IsNullOrWhiteSpace(Value(disk, "DeviceId")) ? Availability.InsufficientData : Availability.Available,
                "MSFT_PhysicalDisk", facts,
                "Identidade e estado do provedor; não constituem benchmark. Controlador e driver específicos não foram associados nesta leitura. Saúde reportada não substitui erros e comportamento observado.");
        }).ToArray();
    }

    internal static IReadOnlyList<AuditObservation> ReadReliability(IReadOnlyList<Dictionary<string, object?>> disks,
        IReadOnlyList<Dictionary<string, object?>> counters, IReadOnlyList<Dictionary<string, object?>> links, Availability status)
    {
        var observations = new List<AuditObservation>();
        foreach (var (disk, index) in disks.Select((d, i) => (d, i)))
        {
            var id = Id(disk, index);
            var path = Value(disk, "__PATH");
            var deviceId = Value(disk, "DeviceId");
            var uniquePhysical = path is not null && deviceId is not null &&
                disks.Count(d => SamePath(Value(d, "__PATH"), path)) == 1 &&
                disks.Count(d => string.Equals(Value(d, "DeviceId"), deviceId, StringComparison.OrdinalIgnoreCase)) == 1;
            var associated = !uniquePhysical ? [] : links.Where(link => SamePath(Value(link, "PhysicalDisk"), path)).ToArray();
            var target = associated.Length == 1 ? Value(associated[0], "StorageReliabilityCounter") : null;
            var found = target is null || links.Count(l => SamePath(Value(l, "StorageReliabilityCounter"), target)) != 1
                ? [] : counters.Where(c => SamePath(Value(c, "__PATH"), target)).ToArray();
            if (found.Length != 1 || !string.Equals(Value(found[0], "DeviceId"), deviceId, StringComparison.OrdinalIgnoreCase))
            {
                observations.Add(Missing("storage.reliability", id, "Confiabilidade do disco", DiskName(disk), status == Availability.Available ? Availability.InsufficientData : status,
                    "Não houve associação única entre este disco e os contadores. Nenhuma temperatura, desgaste ou erro de outra unidade foi utilizado."));
                continue;
            }
            foreach (var field in new[] { "Temperature", "TemperatureMax", "Wear", "PowerOnHours", "ReadErrorsTotal", "ReadErrorsCorrected", "ReadErrorsUncorrected", "WriteErrorsTotal", "WriteErrorsCorrected", "WriteErrorsUncorrected", "ReadLatencyMax", "WriteLatencyMax", "FlushLatencyMax" })
            {
                var value = Number(found[0], field);
                var valid = value.HasValue && value >= 0 && (!field.StartsWith("Temperature", StringComparison.Ordinal) || value > 0);
                var unit = field.StartsWith("Temperature", StringComparison.Ordinal) ? "°C" : field.EndsWith("LatencyMax", StringComparison.Ordinal) ? "ms" : field == "Wear" ? "% da vida de desgaste consumida" : field == "PowerOnHours" ? "horas" : "eventos acumulados";
                observations.Add(new("storage.reliability", id + ":" + field, field, DiskName(disk), valid ? $"{value!.Value.ToString(CultureInfo.InvariantCulture)} {unit}" : null,
                    valid ? Availability.Available : Availability.InsufficientData, "MSFT_StorageReliabilityCounter / MSFT_PhysicalDiskToStorageReliabilityCounter",
                    new Dictionary<string, string> { ["configured"] = "false", ["effective"] = "Valor de confiabilidade reportado para esta unidade; não é uma medição durante o jogo.", ["RawValue"] = Value(found[0], field) ?? "não informado", ["Unit"] = unit },
                    valid ? "Máximas e totais pertencem ao período do provedor. Não houve benchmark, reset de contadores ou leitura de SMART bruto." : "O driver não forneceu uma leitura válida; zero de temperatura não foi tratado como um sensor funcional."));
            }
        }
        return observations;
    }

    internal static IReadOnlyList<AuditObservation> ReadLocations(IReadOnlyList<Dictionary<string, object?>> physical,
        IReadOnlyList<Dictionary<string, object?>> disks, IReadOnlyList<Dictionary<string, object?>> partitions,
        IReadOnlyList<Dictionary<string, object?>> systems, IReadOnlyList<Dictionary<string, object?>> pagefiles, Availability status,
        Availability systemStatus = Availability.Available, Availability pagefileStatus = Availability.Available)
    {
        return physical.Select((disk, index) =>
        {
            var unique = Value(disk, "UniqueId");
            var candidates = string.IsNullOrWhiteSpace(unique) || physical.Count(p => string.Equals(Value(p, "UniqueId"), unique, StringComparison.OrdinalIgnoreCase)) != 1 ? [] :
                disks.Where(d => string.Equals(Value(d, "UniqueId"), unique, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length != 1 || Value(candidates[0], "Number") is not { } number || disks.Count(d => Value(d, "Number") == number) != 1)
                return Missing("storage.location", Id(disk, index), "Volumes do disco", DiskName(disk), status == Availability.Available ? Availability.InsufficientData : status,
                    "Não foi possível vincular esta unidade a um número de disco com identidade única. Windows, paginação e jogo não foram presumidos.");
            var paths = partitions.Where(p => Value(p, "DiskNumber") == number).SelectMany(p => p.GetValueOrDefault("AccessPaths") is Array a ? a.Cast<object>().Select(x => Convert.ToString(x, CultureInfo.InvariantCulture) ?? "") : []).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var systemDrives = systems.Select(s => Value(s, "SystemDrive")).Where(s => s is not null).ToArray();
            var hostsWindows = paths.Any(p => systemDrives.Any(s => p.TrimEnd('\\').Equals(s, StringComparison.OrdinalIgnoreCase)));
            var paging = pagefiles.Select(p => Value(p, "Name")).Where(p => p is not null && paths.Any(root => root.EndsWith('\\') && p.StartsWith(root, StringComparison.OrdinalIgnoreCase))).ToArray();
            return new AuditObservation("storage.location", Id(disk, index), "Volumes do disco", DiskName(disk), string.Join("; ", paths),
                paths.Length == 0 ? Availability.InsufficientData : Availability.Available, "MSFT_Disk.UniqueId + MSFT_Partition.DiskNumber + Win32_PageFileUsage",
                new Dictionary<string, string> { ["configured"] = "false", ["effective"] = "Volumes associados por identidade única e número de disco.",
                    ["DiskNumber"] = number, ["WindowsVolume"] = systemStatus != Availability.Available ? "Consulta indisponível" : hostsWindows ? "Identificado" : "Não identificado nesta unidade",
                    ["Pagefiles"] = pagefileStatus is not (Availability.Available or Availability.InsufficientData) ? "Consulta indisponível" : paging.Length == 0 ? "Não identificados nesta unidade" : string.Join("; ", paging),
                    ["WindowsSourceStatus"] = systemStatus.ToString(), ["PagefileSourceStatus"] = pagefileStatus.ToString(),
                    ["GameLocation"] = "Não avaliada; depende do executável selecionado." },
                "Não foram feitas leituras de arquivos de jogo nem testes de escrita. Ausência de paginação identificada não confirma que a paginação está desativada. A disponibilidade de Windows e paginação é registrada separadamente.");
        }).ToArray();
    }

    private static bool SamePath(string? a, string? b) => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b) && a.Equals(b, StringComparison.OrdinalIgnoreCase);
    private static string Id(Dictionary<string, object?> row, int index) => "physical:" + (Value(row, "UniqueId") ?? Value(row, "DeviceId") ?? "unknown:" + index);
    private static string DiskName(Dictionary<string, object?> row) => Value(row, "Model") ?? Value(row, "FriendlyName") ?? "Disco físico";
    private static string? Value(Dictionary<string, object?> row, string field) => WindowsHardwareDiscovery.Value(row, field) is { } value && !string.IsNullOrWhiteSpace(value) ? value : null;
    private static decimal? Number(Dictionary<string, object?> row, string field) => decimal.TryParse(Value(row, field), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static Dictionary<string, string> Facts(Dictionary<string, object?> row, params string[] names) => names.ToDictionary(n => n, n => Value(row, n) ?? "não informado", StringComparer.Ordinal);
    private static AuditObservation Missing(string rule, string id, string name, string scope, Availability status, string reason) => new(rule, id, name, scope, null, status, "WMI / Storage", new Dictionary<string, string>(), reason);
    private static AuditRule Rule(string id, string category, string name, string method, string evidence, string limitation, string source) => new(id, category, name,
        method, "Windows e provedor WMI correspondente disponível.", "Windows 10/11; campos dependem do firmware/driver.", evidence,
        "Identificar configuração real e limites da coleta; investigar pressão de memória e I/O em sessões medidas.", AuditSupport.Conditional,
        limitation, "Comparar telemetria durante a mesma carga. Benchmarks opcionais exigem confirmação separada; esta leitura não os inicia.", [source]);

    [SupportedOSPlatform("windows")]
    private static (IReadOnlyList<Dictionary<string, object?>> Rows, Availability Status) Read(string className, string scope, CancellationToken ct)
    {
        var rows = new List<Dictionary<string, object?>>();
        try
        {
            ct.ThrowIfCancellationRequested();
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery("SELECT * FROM " + className),
                new System.Management.EnumerationOptions { ReturnImmediately = false, Timeout = TimeSpan.FromSeconds(6) });
            using var results = searcher.Get();
            foreach (ManagementBaseObject item in results)
                using (item)
                {
                    ct.ThrowIfCancellationRequested();
                    if (rows.Count >= 1024) break;
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (PropertyData property in item.Properties) row[property.Name] = property.Value;
                    row["__PATH"] = item.SystemProperties["__PATH"]?.Value;
                    rows.Add(row);
                }
            return (rows, rows.Count > 0 ? Availability.Available : Availability.InsufficientData);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { return (rows, WindowsHardwareDiscovery.Status(error)); }
    }
}
