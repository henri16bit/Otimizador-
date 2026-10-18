using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Win32;
using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

internal sealed record DeviceRegistryValue(bool Present, object? Value, RegistryValueKind? Kind,
    Availability Status = Availability.Available, string? Detail = null);

/// <summary>Read-only inventory: enumerated present PnP nodes, targeted registry values and allocated IRQs.</summary>
public sealed class DeviceInterruptAuditProvider : IPerformanceAuditProvider
{
    private const string AffinitySource = "https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/interrupt-affinity-and-priority";
    private const string MsiSource = "https://learn.microsoft.com/en-us/windows-hardware/drivers/kernel/enabling-message-signaled-interrupts-in-the-registry";
    private const string ResourceSource = "https://learn.microsoft.com/en-us/windows/win32/api/cfgmgr32/ns-cfgmgr32-irq_des_64";
    private const string TopologySource = "https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-getlogicalprocessorinformationex";
    private const string PciSource = "https://github.com/microsoft/win32metadata/blob/main/generation/WinSDK/RecompiledIdlHeaders/shared/pciprop.h";
    private const string UsbSource = "https://learn.microsoft.com/en-us/windows-hardware/drivers/usbcon/usb-device-tree-viewer";

    public IReadOnlyList<AuditRule> Rules { get; } =
    [
        Rule("hardware.cpu.topology", "CPU e agendamento", "Núcleos e processadores lógicos", "GetLogicalProcessorInformationEx / RelationProcessorCore", TopologySource,
            "Topologia não mede saturação, frequência ou contenção; pares SMT vêm da máscara do núcleo, não da numeração adjacente."),
        Rule("devices.identity", "MSI/IRQ/USB", "Dispositivo e driver presentes", "SetupAPI + Configuration Manager + chave de classe enumerada", "https://learn.microsoft.com/en-us/windows/win32/api/setupapi/nf-setupapi-setupdigetdeviceregistrypropertyw",
            "Um dispositivo presente não comprova desempenho adequado ou driver ideal."),
        Rule("devices.interrupt.capability", "MSI/IRQ/USB", "Capacidade de interrupção PCI", "CM_Get_DevNode_Property / DEVPKEY_PciDevice_InterruptSupport", PciSource,
            "Capacidade reportada pelo barramento não identifica o modo efetivamente selecionado pelo driver."),
        Rule("devices.interrupt.msi", "MSI/IRQ/USB", "Configuração MSI persistente", "Registro do dispositivo enumerado / MSISupported", MsiSource,
            "MSISupported registra configuração; não comprova MSI/MSI-X ativo, menor latência ou benefício."),
        Rule("devices.interrupt.limit", "MSI/IRQ/USB", "Limite configurado de mensagens", "Registro do dispositivo enumerado / MessageNumberLimit", MsiSource,
            "O limite configurado não é o número de mensagens alocadas ou utilizadas; MSI e MSI-X têm contratos distintos."),
        Rule("devices.interrupt.policy", "MSI/IRQ/USB", "Política persistente de afinidade", "Registro do dispositivo enumerado / DevicePolicy", AffinitySource,
            "A política persistente não comprova afinidade em uso ou distribuição de DPC/ISR."),
        Rule("devices.interrupt.affinity", "MSI/IRQ/USB", "Máscara persistente de afinidade", "Registro / AssignmentSetOverride little-endian; topologia nativa", AffinitySource,
            "A máscara só escolhe processadores com DevicePolicy = 4. Em múltiplos grupos, a origem não identifica sozinha o grupo; compartilhar CPU não comprova conflito."),
        Rule("devices.interrupt.allocated", "MSI/IRQ/USB", "Recursos IRQ atribuídos", "CM_Get_First_Log_Conf ALLOC_LOG_CONF + IRQ_DES_64", ResourceSource,
            "Recursos alocados não medem a distribuição real de eventos. Número IRQ, flags de disparo e capacidade MSI não bastam para afirmar modo MSI/MSI-X efetivo ou conflito."),
        Rule("devices.usb.topology", "MSI/IRQ/USB", "Relação USB/HID com pais e controladores", "CM_Get_Parent + identidade PnP dos dispositivos presentes", UsbSource,
            "A árvore PnP não informa velocidade negociada, número físico da porta, polling rate, entrega de eventos, suspensão efetiva ou input lag."),
        Rule("devices.usb.polling", "MSI/IRQ/USB", "Polling e intervalos HID", "Não medidos nesta auditoria passiva", UsbSource,
            "Medir intervalos exige captura dedicada e interação autorizada; nenhuma taxa é inferida da identificação ou velocidade do barramento.")
    ];

    public Task<IReadOnlyList<AuditObservation>> ReadAsync(CancellationToken ct) => Task.Run<IReadOnlyList<AuditObservation>>(() =>
    {
        if (OperatingSystem.IsWindows()) return ReadWindows(ct);
        return Rules.Select(rule => new AuditObservation(rule.Id, "platform", rule.Name, "Windows", null,
            Availability.Unsupported, rule.Method, new Dictionary<string, string>(), "Esta leitura requer Windows.")).ToArray();
    }, ct);

    [SupportedOSPlatform("windows")]
    private IReadOnlyList<AuditObservation> ReadWindows(CancellationToken ct)
    {
        var observations = new List<AuditObservation>();
        IReadOnlyList<CpuCore> cores = [];
        try { cores = CpuTopologyReader.Read(); observations.Add(TopologyObservation(cores)); }
        catch (Exception error) when (error is not OperationCanceledException)
        { observations.Add(Failure("hardware.cpu.topology", "cpu", "Topologia CPU", error)); }
        IReadOnlyList<AuditPnpDevice> devices;
        try { devices = DeviceAuditNative.Read(ct); }
        catch (Exception error) when (error is not OperationCanceledException)
        { observations.Add(Failure("devices.identity", "enumeration", "Dispositivos presentes", error)); return observations; }
        using var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Registry32);
        var byId = devices.ToDictionary(device => device.InstanceId, StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            ct.ThrowIfCancellationRequested();
            var root = @"SYSTEM\CurrentControlSet\Enum\" + device.InstanceId;
            var values = new Dictionary<string, DeviceRegistryValue>(StringComparer.Ordinal)
            {
                ["MSISupported"] = ReadRegistry(registry, root + @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties", "MSISupported"),
                ["MessageNumberLimit"] = ReadRegistry(registry, root + @"\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties", "MessageNumberLimit"),
                ["DevicePolicy"] = ReadRegistry(registry, root + @"\Device Parameters\Interrupt Management\Affinity Policy", "DevicePolicy"),
                ["AssignmentSetOverride"] = ReadRegistry(registry, root + @"\Device Parameters\Interrupt Management\Affinity Policy", "AssignmentSetOverride")
            };
            var driver = new Dictionary<string, string>();
            // SPDRP_DRIVER gives a relative class key, not an executable path or an arbitrary registry scan.
            if (ValidDriverKey(device.DriverKey))
                foreach (var field in new[] { "DriverVersion", "ProviderName", "DriverDesc", "InfPath" })
                {
                    var read = ReadRegistry(registry, @"SYSTEM\CurrentControlSet\Control\Class\" + device.DriverKey, field);
                    driver[field] = read.Status != Availability.Available ? read.Detail ?? read.Status.ToString() :
                        read.Present ? Raw(read) : "Não exposto";
                }
            observations.Add(IdentityObservation(device, driver));
            var isPci = device.InstanceId.StartsWith("PCI\\", StringComparison.OrdinalIgnoreCase);
            // PCI capability and interrupt registry audit apply to the PCI controllers/devices, not every HID child.
            if (isPci || values.Values.Any(value => value.Present || value.Status != Availability.Available))
            {
                if (isPci)
                    observations.Add(CapabilityObservation(device, DeviceAuditNative.ReadPciProperty(device.DevInst, 14),
                        DeviceAuditNative.ReadPciProperty(device.DevInst, 15)));
                observations.AddRange(ConfigurationObservations(device, values, cores, IntPtr.Size));
                observations.Add(AllocatedObservation(device, DeviceAuditNative.ReadInterrupts(device.DevInst, ct), cores));
            }
            if (IsUsbRelated(device)) observations.Add(UsbObservation(device, byId));
        }
        if (devices.Any(IsUsbRelated)) observations.Add(new("devices.usb.polling", "hid", "Polling e entrega de eventos HID", "USB/HID", null,
            Availability.Unsupported, "Nenhuma captura de entrada foi iniciada", new Dictionary<string, string>(),
            "Polling configurado e intervalos observados não são expostos por esta leitura. O aplicativo não instalou filtros e não inferiu polling pela velocidade USB."));
        return observations;
    }

    internal static AuditObservation TopologyObservation(IReadOnlyList<CpuCore> cores)
    {
        var facts = ObservedFacts();
        facts["physicalCoreCount"] = cores.Count.ToString(CultureInfo.InvariantCulture);
        facts["logicalProcessorCount"] = cores.Sum(core => core.LogicalProcessors.Count).ToString(CultureInfo.InvariantCulture);
        facts["processorGroupCount"] = cores.Select(core => core.Group).Distinct().Count().ToString(CultureInfo.InvariantCulture);
        facts["smtCoreCount"] = cores.Count(core => core.Smt).ToString(CultureInfo.InvariantCulture);
        facts["coreMap"] = string.Join("; ", cores.Select(core => $"núcleo {core.Index}: grupo {core.Group}, CPUs {string.Join(", ", core.LogicalProcessors)}, SMT={core.Smt}, eficiência={core.EfficiencyClass}"));
        facts["effective"] = "Topologia de núcleos e grupos reportada pelo Windows nesta captura.";
        return new("hardware.cpu.topology", "cpu", "Topologia CPU", "Sistema", $"{cores.Count} núcleos físicos; {facts["logicalProcessorCount"]} processadores lógicos; {facts["processorGroupCount"]} grupo(s)",
            cores.Count > 0 ? Availability.Available : Availability.InsufficientData, "GetLogicalProcessorInformationEx(RelationProcessorCore)", facts,
            cores.Count > 0 ? null : "Nenhum núcleo foi retornado.");
    }

    internal static AuditObservation IdentityObservation(AuditPnpDevice device, IReadOnlyDictionary<string, string> driver)
    {
        var facts = ObservedFacts();
        facts["hardwareIds"] = device.HardwareIds; facts["class"] = device.Class; facts["classGuid"] = device.ClassGuid;
        facts["service"] = device.Service; facts["busEnumerator"] = device.Enumerator; facts["location"] = device.Location;
        facts["parentId"] = device.ParentId;
        if (device.Status is { } status) facts["pnpStatus"] = $"0x{status:X8}";
        if (device.Problem is { } problem) facts["pnpProblemCode"] = problem.ToString(CultureInfo.InvariantCulture);
        foreach (var field in driver) facts[field.Key] = field.Value;
        facts["effective"] = "Dispositivo presente na enumeração PnP; serviço e identidade consultados nesta captura.";
        return new("devices.identity", device.InstanceId, device.Name, device.Class, device.Service, Availability.Available,
            "SetupAPI DIGCF_PRESENT + Configuration Manager + chave de classe do driver", facts,
            device.Limitations.Count > 0 ? string.Join(" ", device.Limitations) : null);
    }

    internal static AuditObservation CapabilityObservation(AuditPnpDevice device, DevicePropertyRead support, DevicePropertyRead maximum)
    {
        var facts = new Dictionary<string, string>();
        if (support.Value is { } flags)
        {
            facts["presence"] = "present"; facts["configured"] = "false";
            facts["capabilityMask"] = $"0x{flags:X8}";
            facts["lineBasedCapable"] = ((flags & 1) != 0).ToString(); facts["msiCapable"] = ((flags & 2) != 0).ToString(); facts["msixCapable"] = ((flags & 4) != 0).ToString();
        }
        if (maximum.Value is { } count) facts["hardwareMessageMaximum"] = count.ToString(CultureInfo.InvariantCulture);
        facts["messageMaximumStatus"] = maximum.Status.ToString();
        if (maximum.Detail is not null) facts["messageMaximumDetail"] = maximum.Detail;
        return new("devices.interrupt.capability", device.InstanceId, device.Name + " — capacidade PCI", device.Class,
            support.Value is { } value ? string.Join(", ", new[] { (value & 1) != 0 ? "line-based" : null, (value & 2) != 0 ? "MSI" : null, (value & 4) != 0 ? "MSI-X" : null }.OfType<string>()) : null,
            support.Status, "DEVPKEY_PciDevice_InterruptSupport (14) / InterruptMessageMaximum (15)", facts,
            support.Detail ?? "Capacidade do hardware reportada pelo barramento; o modo efetivo não foi identificado.");
    }

    internal static IReadOnlyList<AuditObservation> ConfigurationObservations(AuditPnpDevice device,
        IReadOnlyDictionary<string, DeviceRegistryValue> values, IReadOnlyList<CpuCore> cores, int pointerSize)
    {
        var result = new List<AuditObservation>();
        var ruleIds = new Dictionary<string, string>
        {
            ["MSISupported"] = "devices.interrupt.msi", ["MessageNumberLimit"] = "devices.interrupt.limit",
            ["DevicePolicy"] = "devices.interrupt.policy", ["AssignmentSetOverride"] = "devices.interrupt.affinity"
        };
        foreach (var (field, ruleId) in ruleIds)
        {
            var value = values[field];
            var facts = new Dictionary<string, string>();
            var limitation = value.Detail;
            if (value.Status == Availability.Available)
            {
                facts["presence"] = value.Present ? "present" : "absent";
                facts["configured"] = value.Present ? "true" : "false";
                if (value.Present)
                {
                    facts["registryType"] = value.Kind?.ToString() ?? "Desconhecido";
                    if (field == "AssignmentSetOverride")
                    {
                        try
                        {
                            var mask = DecodeAffinity(value, pointerSize);
                            facts["maskHex"] = $"0x{mask:X}";
                            facts["logicalBits"] = string.Join(", ", InterruptAffinityDecoder.Processors(mask));
                            var groups = cores.Select(core => core.Group).Distinct().ToArray();
                            if (groups.Length == 1)
                            {
                                facts["groupContext"] = groups[0].ToString(CultureInfo.InvariantCulture);
                                facts["topologyMapping"] = MapMask(mask, groups[0], cores);
                                var activeMask = cores.Aggregate(0UL, (all, core) => all | core.Mask);
                                if ((mask & ~activeMask) != 0) facts["invalid"] = "A máscara persistente inclui processadores não presentes na topologia atual.";
                            }
                            else facts["groupContext"] = "Indeterminado: a máscara persistente não contém número de grupo.";
                            if (mask == 0) facts["invalid"] = "Máscara persistente vazia; nenhum processador foi selecionado.";
                            var policy = values["DevicePolicy"];
                            facts["maskPolicyApplies"] = Dword(policy) == 4 ? "Política 4 configurada; aplicação efetiva não verificada." :
                                "DevicePolicy = 4 não foi confirmado; não interpretar esta máscara como seleção em uso.";
                            limitation = "Bits little-endian decodificados; grupos e pares SMT só são associados quando a topologia identifica o contexto. Nenhuma distribuição de eventos foi medida.";
                        }
                        catch (InvalidDataException error) { facts["invalid"] = error.Message; }
                    }
                    else if (Dword(value) is { } number)
                    {
                        facts["decimal"] = number.ToString(CultureInfo.InvariantCulture); facts["hex"] = $"0x{number:X8}";
                        if (field == "MSISupported" && number > 1) facts["invalid"] = "MSISupported deve ser REG_DWORD 0 ou 1.";
                        if (field == "MessageNumberLimit" && number == 0) facts["invalid"] = "MessageNumberLimit não descreve um limite positivo de mensagens.";
                        if (field == "DevicePolicy")
                        {
                            facts["policyDescription"] = InterruptAffinityDecoder.Policy(number);
                            if (number > 5) facts["invalid"] = "DevicePolicy está fora das políticas públicas 0 a 5 para configuração de dispositivos.";
                        }
                    }
                    else facts["invalid"] = field + " não foi armazenado como REG_DWORD.";
                }
            }
            result.Add(new(ruleId, device.InstanceId, device.Name + " — " + field, device.Class,
                value.Present ? Raw(value) : null, value.Status,
                @"HKLM\SYSTEM\CurrentControlSet\Enum\" + device.InstanceId + @"\Device Parameters\Interrupt Management\" +
                (field is "MSISupported" or "MessageNumberLimit" ? "MessageSignaledInterruptProperties\\" : "Affinity Policy\\") + field,
                facts, limitation));
        }
        return result;
    }

    internal static AuditObservation AllocatedObservation(AuditPnpDevice device, InterruptResourceRead read, IReadOnlyList<CpuCore> cores)
    {
        var facts = new Dictionary<string, string>();
        if (read.Resources.Count > 0)
        {
            facts["presence"] = "present"; facts["configured"] = "false";
            facts["allocatedCount"] = read.Resources.Count.ToString(CultureInfo.InvariantCulture);
            facts["allocatedResources"] = string.Join("; ", read.Resources.Select(resource =>
                $"IRQ {resource.Number}; grupo {resource.Group}; máscara 0x{resource.Affinity:X}; CPUs [{string.Join(", ", InterruptAffinityDecoder.Processors(resource.Affinity))}]; flags 0x{resource.Flags:X4}; {MapMask(resource.Affinity, resource.Group, cores)}"));
            facts["effective"] = "Recursos IRQ retornados pela configuração ALLOC_LOG_CONF do dispositivo nesta captura. Não representa uma medição de eventos.";
        }
        return new("devices.interrupt.allocated", device.InstanceId, device.Name + " — recursos atribuídos", device.Class,
            read.Resources.Count > 0 ? string.Join(", ", read.Resources.Select(resource => $"IRQ {resource.Number}, grupo {resource.Group}")) : null,
            read.Status, "Configuration Manager / ALLOC_LOG_CONF / ResType_IRQ / IRQ_DES_64", facts,
            read.Detail ?? "Não foi determinado o modo MSI/MSI-X efetivo. Recursos compartilhados não são classificados como conflito; a distribuição real requer ETW.");
    }

    internal static AuditObservation UsbObservation(AuditPnpDevice device, IReadOnlyDictionary<string, AuditPnpDevice> byId)
    {
        var chain = new List<string> { device.Name };
        var ids = new List<string> { device.InstanceId };
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { device.InstanceId };
        var current = device; string? limitation = null;
        while (current.ParentId.Length > 0)
        {
            if (!visited.Add(current.ParentId)) { limitation = "Ciclo na relação PnP retornada; árvore parcial."; break; }
            ids.Add(current.ParentId);
            if (!byId.TryGetValue(current.ParentId, out var parent)) { limitation = "Um ancestral não está entre os dispositivos relevantes enumerados; árvore parcial."; break; }
            chain.Add(parent.Name); current = parent;
            if (chain.Count >= 64) { limitation = "Limite de profundidade atingido; árvore parcial."; break; }
        }
        var facts = ObservedFacts(); facts["parentId"] = device.ParentId;
        facts["parentChain"] = string.Join(" ← ", chain); facts["parentInstanceIds"] = string.Join(" ← ", ids);
        facts["effective"] = "Relações pai/filho observadas pelo Configuration Manager nesta captura.";
        return new("devices.usb.topology", device.InstanceId, device.Name + " — conexão USB/HID", device.Class, string.Join(" ← ", chain),
            Availability.Available, "CM_Get_Parent + identidades PnP presentes", facts,
            limitation ?? "Relação lógica PnP; porta física, velocidade USB, polling e suspensão efetiva não foram medidos.");
    }

    [UnconditionalSuppressMessage("Interoperability", "CA1416", Justification = "Pure data decoding: RegistryValueKind constants do not access the Windows registry.")]
    internal static ulong DecodeAffinity(DeviceRegistryValue value, int pointerSize)
    {
        if (pointerSize is not (4 or 8)) throw new ArgumentOutOfRangeException(nameof(pointerSize));
        if (value.Kind == RegistryValueKind.Binary && value.Value is byte[] bytes) return InterruptAffinityDecoder.DecodeBinary(bytes, pointerSize);
        if (Dword(value) is { } dword) return dword;
        if (pointerSize == 8 && value.Kind == RegistryValueKind.QWord && value.Value is long qword) return unchecked((ulong)qword);
        throw new InvalidDataException("AssignmentSetOverride deve ser REG_BINARY, REG_DWORD ou REG_QWORD compatível com a largura da plataforma.");
    }

    internal static string MapMask(ulong mask, ushort group, IReadOnlyList<CpuCore> cores)
    {
        if (cores.Count == 0) return "Topologia indisponível";
        return string.Join(", ", InterruptAffinityDecoder.Processors(mask).Select(cpu =>
        {
            var core = cores.FirstOrDefault(candidate => candidate.Group == group && (candidate.Mask & (1UL << cpu)) != 0);
            return core is null ? $"CPU {cpu}: núcleo não identificado no grupo {group}" :
                $"CPU {cpu}: núcleo {core.Index}" + (core.Smt ? $" (SMT: {string.Join("/", core.LogicalProcessors)})" : "");
        }));
    }

    [SupportedOSPlatform("windows")]
    internal static DeviceRegistryValue ReadRegistry(RegistryKey registry, string path, string name)
    {
        try
        {
            using var key = registry.OpenSubKey(path, writable: false);
            if (key is null || !key.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase)) return new(false, null, null);
            return new(true, key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), key.GetValueKind(name));
        }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException)
        { return new(false, null, null, Availability.PermissionRequired, "Leitura do Registro negada: " + error.Message); }
        catch (Exception error) when (error is IOException or ArgumentException)
        { return new(false, null, null, Availability.ReadError, "Falha na leitura do Registro: " + error.Message); }
    }

    private static bool ValidDriverKey(string path)
    {
        var parts = path.Split('\\');
        return parts.Length == 2 && Guid.TryParse(parts[0], out _) && parts[1].Length == 4 && parts[1].All(char.IsAsciiDigit);
    }
    private static bool IsUsbRelated(AuditPnpDevice device) => device.InstanceId.StartsWith("USB\\", StringComparison.OrdinalIgnoreCase) ||
        device.InstanceId.StartsWith("HID\\", StringComparison.OrdinalIgnoreCase) || device.Class.Equals("USB", StringComparison.OrdinalIgnoreCase);
    [UnconditionalSuppressMessage("Interoperability", "CA1416", Justification = "Pure data decoding: RegistryValueKind constants do not access the Windows registry.")]
    private static uint? Dword(DeviceRegistryValue value) => value.Status == Availability.Available && value.Present &&
        value.Kind == RegistryValueKind.DWord && value.Value is int number ? unchecked((uint)number) : null;
    [UnconditionalSuppressMessage("Interoperability", "CA1416", Justification = "Pure data formatting: RegistryValueKind constants do not access the Windows registry.")]
    private static string Raw(DeviceRegistryValue value) => value.Value switch
    {
        byte[] bytes => Convert.ToHexString(bytes),
        int number when value.Kind == RegistryValueKind.DWord => $"{unchecked((uint)number).ToString(CultureInfo.InvariantCulture)} (0x{unchecked((uint)number):X8})",
        long number when value.Kind == RegistryValueKind.QWord => $"{unchecked((ulong)number).ToString(CultureInfo.InvariantCulture)} (0x{unchecked((ulong)number):X16})",
        string[] values => string.Join("; ", values),
        _ => Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? ""
    };
    private static Dictionary<string, string> ObservedFacts() => new() { ["presence"] = "present", ["configured"] = "false" };
    private AuditObservation Failure(string rule, string instance, string name, Exception error) => new(rule, instance, name, "Sistema", null,
        error is UnauthorizedAccessException or SecurityException ? Availability.PermissionRequired : error is PlatformNotSupportedException ? Availability.Unsupported : Availability.ReadError,
        Rules.First(entry => entry.Id == rule).Method, new Dictionary<string, string>(), error.Message);
    private static AuditRule Rule(string id, string category, string name, string method, string source, string limitation) =>
        new(id, category, name, method, "Windows; dispositivo presente; leitura permitida pela origem", "Windows 10/11 x64; propriedades dependem do barramento e driver",
            "Valor bruto, tipo, identidade do dispositivo, fonte e estado da leitura", "Investigar políticas persistentes e recursos atribuídos sem prometer menor latência",
            id == "devices.usb.polling" ? AuditSupport.NotVerifiable : AuditSupport.Conditional, limitation,
            "Conferir identidade e recursos no Gerenciador de Dispositivos; para efeito, repetir captura ETW autorizada durante a mesma carga. Nenhuma alteração pelo aplicativo.", [source]);
}
