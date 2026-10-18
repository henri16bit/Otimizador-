using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32;
using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

/// <summary>Catalog-directed, read-only configuration snapshot. Never requests elevation or changes policy.</summary>
public sealed class WindowsConfigurationAuditProvider : IPerformanceAuditProvider
{
    public IReadOnlyList<AuditRule> Rules => WindowsAuditCatalog.Rules;
    private const string SystemProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
    private const int MaxEntries = 256;

    public Task<IReadOnlyList<AuditObservation>> ReadAsync(CancellationToken ct) => Task.Run<IReadOnlyList<AuditObservation>>(() =>
        OperatingSystem.IsWindows() ? ReadWindows(ct) : Rules.Select(rule => Missing(rule.Id, rule.Id, "Sistema", Availability.Unsupported,
            "Esta leitura exige APIs do Windows; nenhum valor foi simulado.")).ToArray(), ct);

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<AuditObservation> ReadWindows(CancellationToken ct)
    {
        var result = new List<AuditObservation>();
        ReadProcesses(result, ct);
        ReadRegistry(result, ct);
        ReadPower(result, ct);
        ReadMouseAndTimers(result);
        ReadServices(result, ct);
        ReadTasks(result, ct);
        ReadBcd(result, ct);
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static void ReadRegistry(List<AuditObservation> output, CancellationToken ct)
    {
        void Read(RegistryHive hive, string key, string name, string rule, string? scope = null,
            IReadOnlyDictionary<string, string>? extra = null) => output.Add(ReadValue(hive, key, name, rule, scope, extra));
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            foreach (var name in new[] { "ProductName", "CurrentBuildNumber", "DisplayVersion", "UBR" })
                output.Add(ReadValue(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion", name, "windows.version", "Windows", view: view));
        Read(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", "windows.scheduler.separation");
        Read(RegistryHive.LocalMachine, SystemProfile, "SystemResponsiveness", "windows.mmcss.responsiveness");
        Read(RegistryHive.LocalMachine, SystemProfile, "NetworkThrottlingIndex", "windows.mmcss.throttling");
        var profiles = Subkeys(RegistryHive.LocalMachine, SystemProfile + @"\Tasks", "windows.mmcss.category", output);
        foreach (var profile in profiles)
        {
            ct.ThrowIfCancellationRequested();
            var key = SystemProfile + @"\Tasks\" + profile;
            var category = ReadValue(RegistryHive.LocalMachine, key, "Scheduling Category", "windows.mmcss.category", profile);
            output.Add(category);
            var facts = new Dictionary<string, string> { ["SchedulingCategory"] = category.RawValue ?? "Não disponível" };
            foreach (var (name, rule) in new[] { ("Priority", "priority"), ("GPU Priority", "gpu"), ("SFIO Priority", "sfio"),
                         ("Clock Rate", "clock"), ("Affinity", "affinity"), ("Background Only", "background"), ("BackgroundPriority", "background") })
                Read(RegistryHive.LocalMachine, key, name, "windows.mmcss." + rule, profile, facts);
        }
        foreach (var executable in Subkeys(RegistryHive.LocalMachine, Ifeo, "windows.ifeo.cpu", output))
        {
            ct.ThrowIfCancellationRequested();
            var key = Ifeo + "\\" + executable + @"\PerfOptions";
            if (!KeyExists(RegistryHive.LocalMachine, key, "windows.ifeo.cpu", output)) continue;
            var matches = output.Where(o => o.RuleId == "windows.process.scheduling" &&
                o.Facts.GetValueOrDefault("ExecutableName")?.Equals(executable, StringComparison.OrdinalIgnoreCase) == true).ToArray();
            var facts = new Dictionary<string, string>
            {
                ["executable"] = executable, ["matchingProcesses"] = matches.Length.ToString(CultureInfo.InvariantCulture),
                ["observedProcessClasses"] = string.Join("; ", matches.Select(m => $"PID {m.Facts.GetValueOrDefault("PID")}: {m.Facts.GetValueOrDefault("PriorityClass", "indisponível")}")),
                ["matchingMethod"] = "Nome do executável no snapshot; não comprova identidade por caminho nem aplicação de IFEO."
            };
            Read(RegistryHive.LocalMachine, key, "CpuPriorityClass", "windows.ifeo.cpu", executable, facts);
            Read(RegistryHive.LocalMachine, key, "IoPriority", "windows.ifeo.io", executable, facts);
            Read(RegistryHive.LocalMachine, key, "PagePriority", "windows.ifeo.page", executable, facts);
        }
        foreach (var name in new[] { "AutoGameModeEnabled", "AllowAutoGameMode" })
            Read(RegistryHive.CurrentUser, @"Software\Microsoft\GameBar", name, "windows.gaming.gamemode");
        Read(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", "HwSchMode", "windows.gaming.hags");
        foreach (var name in new[] { "GameDVR_FSEBehaviorMode", "GameDVR_HonorUserFSEBehaviorMode", "GameDVR_DXGIHonorFSEWindowsCompatible", "GameDVR_EFSEFeatureFlags" })
            Read(RegistryHive.CurrentUser, @"System\GameConfigStore", name, "windows.gaming.fullscreen");
        ReadValues(RegistryHive.CurrentUser, @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers", "windows.gaming.fullscreen", output, ct);
        ReadValues(RegistryHive.CurrentUser, @"Software\Microsoft\DirectX\UserGpuPreferences", "windows.gaming.graphics", output, ct);
        foreach (var name in new[] { "MouseSpeed", "MouseThreshold1", "MouseThreshold2", "MouseSensitivity" })
            Read(RegistryHive.CurrentUser, @"Control Panel\Mouse", name, "windows.mouse.settings");
        foreach (var name in new[] { "SmoothMouseXCurve", "SmoothMouseYCurve" })
            Read(RegistryHive.CurrentUser, @"Control Panel\Mouse", name, "windows.mouse.curves");
        Read(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Mouclass\Parameters", "MouseDataQueueSize", "windows.input.queue");
        Read(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Kbdclass\Parameters", "KeyboardDataQueueSize", "windows.input.queue");
        foreach (var name in new[] { "PagingFiles", "DisablePagingExecutive", "LargeSystemCache", "ClearPageFileAtShutdown" })
            Read(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", name, "windows.memory.policy");
        // Additional commonly changed policies are retained as unverified configuration, not asserted effective.
        Read(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", "windows.power.throttling");
        Read(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", "windows.graphics.overlay");
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var suffix in new[] { "Run", "RunOnce" })
                ReadValues(hive, @"SOFTWARE\Microsoft\Windows\CurrentVersion\" + suffix, "windows.startup.entries", output, ct);
            foreach (var suffix in new[] { "Run", "Run32", "StartupFolder" })
                ReadValues(hive, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\" + suffix, "windows.startup.entries", output, ct);
        }
    }

    [SupportedOSPlatform("windows")]
    private static AuditObservation ReadValue(RegistryHive hive, string path, string name, string rule, string? scope = null,
        IReadOnlyDictionary<string, string>? extra = null, RegistryView view = RegistryView.Registry64)
    {
        var source = HiveName(hive) + "\\" + path + " :: " + name + " [" + view + "]";
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(path, writable: false);
            var present = key?.GetValueNames().Contains(name, StringComparer.OrdinalIgnoreCase) == true;
            var value = present ? key!.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) : null;
            var kind = present ? key!.GetValueKind(name) : RegistryValueKind.Unknown;
            var observation = RegistryObservation(rule, source, name, scope ?? (hive == RegistryHive.CurrentUser ? "Usuário atual" : "Sistema"), value, kind, present, extra);
            var facts = new Dictionary<string, string>(observation.Facts) { ["registryView"] = view == RegistryView.Registry32 ? "32-bit" : "64-bit" };
            return observation with { Facts = facts };
        }
        catch (Exception error) when (ReadFailure(error)) { return Failure(rule, source, scope ?? "Registro", error); }
    }

    [UnconditionalSuppressMessage("Interoperability", "CA1416", Justification = "Pure data decoding: RegistryValueKind constants do not access the Windows registry.")]
    internal static AuditObservation RegistryObservation(string rule, string source, string name, string scope, object? value,
        RegistryValueKind kind, bool present, IReadOnlyDictionary<string, string>? extra = null)
    {
        var facts = extra is null ? new Dictionary<string, string>() : new Dictionary<string, string>(extra);
        facts["presence"] = present ? "present" : "absent";
        facts["configured"] = present ? "true" : "false";
        facts["registryType"] = kind.ToString();
        facts["registryView"] = "64-bit";
        facts["OSVersion"] = Environment.OSVersion.Version.ToString();
        facts["evidence"] = source;
        string? raw = null;
        if (present)
        {
            raw = FormatRegistryValue(value, kind);
            if (kind == RegistryValueKind.DWord && value is int signed)
            {
                var number = unchecked((uint)signed);
                facts["decimal"] = number.ToString(CultureInfo.InvariantCulture);
                facts["hexadecimal"] = "0x" + number.ToString("X8", CultureInfo.InvariantCulture);
            }
            string? invalid = null;
            if (rule.StartsWith("windows.mmcss.", StringComparison.Ordinal) && rule != "windows.mmcss.throttling")
            {
                var expectedString = name is "Scheduling Category" or "SFIO Priority" or "Background Only";
                if (kind != (expectedString ? RegistryValueKind.String : RegistryValueKind.DWord))
                    invalid = $"{name} exige {(expectedString ? "REG_SZ" : "REG_DWORD")} no contrato MMCSS; o tipo lido foi {kind}.";
                else if (name is "Priority" or "BackgroundPriority" && uint.TryParse(facts.GetValueOrDefault("decimal"), out var priority) && priority is < 1 or > 8)
                    invalid = "A faixa documentada desta prioridade MMCSS é 1 a 8. A leitura não comprova o comportamento de fallback do serviço.";
                else if (name == "Scheduling Category" && raw is not null && !new[] { "High", "Medium", "Low" }.Contains(raw, StringComparer.OrdinalIgnoreCase))
                    invalid = "Scheduling Category documenta High, Medium ou Low; o valor lido não pertence a esse conjunto.";
                else if (name == "Background Only" && raw is not null && !new[] { "True", "False" }.Contains(raw, StringComparer.OrdinalIgnoreCase))
                    invalid = "Background Only documenta True ou False; o valor lido não pertence a esse conjunto.";
            }
            if (invalid is not null) { facts["invalid"] = "true"; facts["invalidReason"] = invalid; }
        }
        return new(rule, source, name, scope, raw, Availability.Available, source, facts,
            present ? "Configuração persistente; não comprova aplicação ao processo, comportamento observado ou benefício."
                : "Valor não encontrado na visão de Registro consultada. O padrão efetivo não foi inferido.");
    }

    internal static string FormatRegistryValue(object? value, RegistryValueKind kind) => value switch
    {
        int number when kind == RegistryValueKind.DWord => $"0x{unchecked((uint)number):X8} ({unchecked((uint)number).ToString(CultureInfo.InvariantCulture)} decimal)",
        long number when kind == RegistryValueKind.QWord => $"0x{unchecked((ulong)number):X16} ({unchecked((ulong)number).ToString(CultureInfo.InvariantCulture)} decimal)",
        byte[] bytes => Convert.ToHexString(bytes),
        string[] values => string.Join(" | ", values),
        null => "(sem conteúdo)",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "(sem conteúdo)"
    };

    [SupportedOSPlatform("windows")]
    private static string[] Subkeys(RegistryHive hive, string path, string rule, List<AuditObservation> output)
    {
        var source = HiveName(hive) + "\\" + path;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path, false);
            if (key is null) { output.Add(RegistryObservation(rule, source, "Chave", "Sistema", null, RegistryValueKind.Unknown, false)); return []; }
            var names = key.GetSubKeyNames().Order(StringComparer.OrdinalIgnoreCase).ToArray();
            if (names.Length > MaxEntries) output.Add(Missing(rule, source + ":limit", "Registro", Availability.InsufficientData,
                $"Escopo limitado a {MaxEntries} de {names.Length} subchaves nesta origem."));
            return names.Take(MaxEntries).ToArray();
        }
        catch (Exception error) when (ReadFailure(error)) { output.Add(Failure(rule, source, "Registro", error)); return []; }
    }

    [SupportedOSPlatform("windows")]
    private static bool KeyExists(RegistryHive hive, string path, string rule, List<AuditObservation> output)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path, false);
            return key is not null;
        }
        catch (Exception error) when (ReadFailure(error)) { output.Add(Failure(rule, HiveName(hive) + "\\" + path, "Registro", error)); return false; }
    }

    [SupportedOSPlatform("windows")]
    private static void ReadValues(RegistryHive hive, string path, string rule, List<AuditObservation> output, CancellationToken ct)
    {
        var source = HiveName(hive) + "\\" + path;
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(path, false);
            var names = key?.GetValueNames().Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
            if (names.Length == 0) output.Add(RegistryObservation(rule, source, "Entradas", "Registro", null, RegistryValueKind.Unknown, false));
            foreach (var name in names.Take(MaxEntries)) { ct.ThrowIfCancellationRequested(); output.Add(ReadValue(hive, path, name, rule)); }
            if (names.Length > MaxEntries) output.Add(Missing(rule, source + ":limit", "Registro", Availability.InsufficientData, $"Somente {MaxEntries} de {names.Length} entradas foram lidas."));
        }
        catch (Exception error) when (ReadFailure(error)) { output.Add(Failure(rule, source, "Registro", error)); }
    }

    [SupportedOSPlatform("windows")]
    private static void ReadProcesses(List<AuditObservation> output, CancellationToken ct)
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var foregroundPid);
        var processes = Process.GetProcesses();
        try
        {
            foreach (var process in processes.OrderBy(p => p.Id == Environment.ProcessId ? 0 : 1).ThenBy(p => p.Id).Take(1024))
            {
                ct.ThrowIfCancellationRequested();
                var facts = new Dictionary<string, string> { ["PID"] = process.Id.ToString(CultureInfo.InvariantCulture),
                    ["foreground"] = (process.Id == foregroundPid).ToString(), ["configured"] = "false", ["presence"] = "present" };
                var missing = new List<string>();
                var name = "PID " + process.Id;
                try { name = process.ProcessName; facts["ExecutableName"] = name + ".exe"; } catch (Exception error) when (ReadFailure(error)) { missing.Add("Nome indisponível."); }
                try { facts["ProcessStartedAtUtc"] = process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture); }
                catch (Exception error) when (ReadFailure(error)) { missing.Add("Horário de criação indisponível; este PID não será comparado entre snapshots."); }
                try { facts["PriorityClass"] = process.PriorityClass.ToString(); } catch (Exception error) when (ReadFailure(error)) { missing.Add("Classe de prioridade indisponível: " + error.GetType().Name); }
                try { facts["AffinityMask"] = "0x" + unchecked((ulong)process.ProcessorAffinity.ToInt64()).ToString("X", CultureInfo.InvariantCulture); }
                catch (Exception error) when (ReadFailure(error)) { missing.Add("Afinidade indisponível: " + error.GetType().Name); }
                var observed = string.Join("; ", new[] { facts.TryGetValue("PriorityClass", out var priority) ? "Classe: " + priority : null,
                    facts.TryGetValue("AffinityMask", out var affinity) ? "Afinidade: " + affinity : null }.Where(x => x is not null));
                if (observed.Length > 0) facts["effective"] = observed;
                facts["evidence"] = $"Process PID {process.Id}; snapshot {DateTimeOffset.UtcNow:O}";
                output.Add(new("windows.process.scheduling", "pid:" + process.Id, name, "Processo", observed.Length > 0 ? observed : null,
                    observed.Length > 0 ? Availability.Available : Availability.PermissionRequired, "Process.PriorityClass / ProcessorAffinity; GetForegroundWindow", facts,
                    string.Join(" ", missing.Append("Afinidade é máscara reportada pela API; grupos, CPU Sets, políticas por thread e utilização não foram inferidos. Nenhuma memória do processo foi lida."))));
            }
            if (processes.Length > 1024) output.Add(Missing("windows.process.scheduling", "process:limit", "Processos", Availability.InsufficientData, "Snapshot limitado aos primeiros 1024 PIDs."));
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    [SupportedOSPlatform("windows")]
    private static void ReadPower(List<AuditObservation> output, CancellationToken ct)
    {
        if (!NativeWindows.TryGetPowerScheme(out var scheme))
        { output.Add(Missing("windows.power.policy", "power:active", "Sistema", Availability.ReadError, "PowerGetActiveScheme não retornou o plano ativo.")); return; }
        output.Add(Observed("windows.power.policy", "power:active", "Plano de energia ativo", scheme.ToString(), "PowerGetActiveScheme",
            new() { ["ActiveScheme"] = scheme.ToString(), ["effective"] = "Plano ativo reportado pelo Windows: " + scheme }));
        foreach (var setting in PowerSettings)
        {
            ct.ThrowIfCancellationRequested();
            var group = Guid.Parse(setting.Group);
            var id = Guid.Parse(setting.Id);
            foreach (var ac in new[] { true, false })
            {
                var status = ac ? PowerReadACValueIndex(0, ref scheme, ref group, ref id, out var value)
                    : PowerReadDCValueIndex(0, ref scheme, ref group, ref id, out value);
                var instance = $"power:{scheme}:{id}:{(ac ? "AC" : "DC")}";
                if (status != 0) { output.Add(Missing("windows.power.policy", instance, "Plano ativo", status == 5 ? Availability.PermissionRequired : Availability.Unsupported,
                    $"{setting.Name}: índice {(ac ? "AC" : "DC")} não disponível; código Win32 {status}.")); continue; }
                output.Add(Observed("windows.power.policy", instance, setting.Name + (ac ? " — tomada" : " — bateria"), value.ToString(CultureInfo.InvariantCulture),
                    ac ? "PowerReadACValueIndex" : "PowerReadDCValueIndex", new() { ["configured"] = "true", ["scheme"] = scheme.ToString(),
                        ["subgroup"] = group.ToString(), ["setting"] = id.ToString(), ["powerSource"] = ac ? "AC" : "DC", ["unit"] = setting.Unit },
                    "Índice configurado do plano ativo; não comprova o estado físico da CPU, PCIe ou USB. AC/DC são mostrados separadamente."));
            }
        }
    }

    private static readonly (string Name, string Group, string Id, string Unit)[] PowerSettings =
    [
        ("Estado mínimo do processador", "54533251-82be-4824-96c1-47b60b740d00", "893dee8e-2bef-41e0-89c6-b55d0929964c", "%"),
        ("Estado máximo do processador", "54533251-82be-4824-96c1-47b60b740d00", "bc5038f7-23e0-4960-96da-33abaf5935ec", "%"),
        ("Mínimo de núcleos sem parking", "54533251-82be-4824-96c1-47b60b740d00", "0cc5b647-c1df-4637-891a-dec35c318583", "%"),
        ("Política de boost", "54533251-82be-4824-96c1-47b60b740d00", "be337238-0d82-4146-a960-4f3749d470c7", "índice"),
        ("Preferência energia/desempenho", "54533251-82be-4824-96c1-47b60b740d00", "36687f9e-e3a5-4dbf-b1dc-15eb381c6863", "índice"),
        ("Economia do link PCI Express", "501a4d13-42af-4429-9fd1-a8218c268e20", "ee12f906-d277-404b-b6da-e5fa1a576df5", "índice"),
        ("Suspensão seletiva USB", "2a737441-1930-4402-8d77-b2bebba308a3", "48e6b7a6-50f5-4782-a5d4-53bb8f07e226", "índice")
    ];

    [SupportedOSPlatform("windows")]
    private static void ReadMouseAndTimers(List<AuditObservation> output)
    {
        var mouse = new int[3];
        if (SystemParametersInfo(0x0003, 0, mouse, 0))
            output.Add(Observed("windows.mouse.effective", "mouse:thresholds", "Aceleração atual do ponteiro", $"Limiares {mouse[0]} / {mouse[1]}; aceleração {mouse[2]}",
                "SystemParametersInfoW SPI_GETMOUSE", new() { ["effective"] = $"Threshold1={mouse[0]}; Threshold2={mouse[1]}; Acceleration={mouse[2]}" }));
        else output.Add(Missing("windows.mouse.effective", "mouse:thresholds", "Usuário atual", Availability.ReadError, "SPI_GETMOUSE falhou: " + Marshal.GetLastWin32Error()));
        var speed = new int[1];
        if (SystemParametersInfo(0x0070, 0, speed, 0))
            output.Add(Observed("windows.mouse.effective", "mouse:speed", "Velocidade atual do ponteiro", speed[0].ToString(CultureInfo.InvariantCulture),
                "SystemParametersInfoW SPI_GETMOUSESPEED", new() { ["effective"] = "Velocidade do ponteiro: " + speed[0] + " (faixa documentada 1–20)" }));
        else output.Add(Missing("windows.mouse.effective", "mouse:speed", "Usuário atual", Availability.ReadError, "SPI_GETMOUSESPEED falhou: " + Marshal.GetLastWin32Error()));
        output.Add(Observed("windows.timers.qpc", "timer:qpc", "Frequência QPC", Stopwatch.Frequency.ToString(CultureInfo.InvariantCulture) + " Hz", "Stopwatch.Frequency / QueryPerformanceFrequency",
            new() { ["effective"] = $"Contador de alta resolução: {Stopwatch.IsHighResolution}; frequência {Stopwatch.Frequency} Hz." }));
        var status = timeGetDevCaps(out var caps, (uint)Marshal.SizeOf<TimerCaps>());
        if (status == 0) output.Add(Observed("windows.timers.capabilities", "timer:capabilities", "Resoluções multimídia suportadas", $"{caps.Minimum} a {caps.Maximum} ms", "timeGetDevCaps",
            new() { ["effective"] = $"Capacidade reportada: mínimo {caps.Minimum} ms, máximo {caps.Maximum} ms. Não é a resolução em uso." }));
        else output.Add(Missing("windows.timers.capabilities", "timer:capabilities", "Sistema", Availability.ReadError, "timeGetDevCaps retornou " + status));
        output.Add(Missing("windows.timers.current", "timer:current", "Processos", Availability.InsufficientData,
            "Não houve captura de solicitações timeBeginPeriod por processo; resolução efetiva e fonte física QPC não foram inferidas."));
    }

    [SupportedOSPlatform("windows")]
    private static void ReadServices(List<AuditObservation> output, CancellationToken ct)
    {
        const string query = "SELECT Name,DisplayName,State,StartMode,ProcessId FROM Win32_Service WHERE Name='MMCSS' OR Name='SysMain' OR Name='WSearch' OR Name='Audiosrv' OR Name='AudioEndpointBuilder' OR Name='BITS' OR Name='Spooler' OR Name='DiagTrack'";
        try
        {
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"), new ObjectQuery(query),
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(4), ReturnImmediately = false });
            using var rows = searcher.Get();
            foreach (ManagementObject row in rows)
            {
                using (row)
                {
                    ct.ThrowIfCancellationRequested();
                    var facts = new Dictionary<string, string>();
                    foreach (var field in new[] { "Name", "State", "StartMode", "ProcessId" }) facts[field] = Convert.ToString(row[field], CultureInfo.InvariantCulture) ?? "Não informado";
                    facts["effective"] = $"Estado reportado: {facts["State"]}; inicialização configurada: {facts["StartMode"]}; PID: {facts["ProcessId"]}.";
                    output.Add(Observed("windows.services.state", "service:" + facts["Name"], Convert.ToString(row["DisplayName"], CultureInfo.InvariantCulture) ?? facts["Name"],
                        facts["effective"], "WMI Win32_Service", facts, "Seleção explícita de serviços; snapshot não mede consumo nem justifica desativação. Dependências não foram avaliadas."));
                }
            }
        }
        catch (Exception error) when (ReadFailure(error)) { output.Add(Failure("windows.services.state", "Win32_Service", "Serviços", error)); }
    }

    [SupportedOSPlatform("windows")]
    private static void ReadTasks(List<AuditObservation> output, CancellationToken ct)
    {
        object? serviceObject = null;
        var folders = new Queue<object>();
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service");
            if (type is null) { output.Add(Missing("windows.tasks.entries", "tasks:service", "Sistema", Availability.Unsupported, "Task Scheduler COM não disponível.")); return; }
            serviceObject = Activator.CreateInstance(type);
            dynamic service = serviceObject!;
            service.Connect();
            folders.Enqueue((object)service.GetFolder("\\"));
            var count = 0;
            var visited = 0;
            while (folders.Count > 0 && count < MaxEntries && visited++ < 128)
            {
                ct.ThrowIfCancellationRequested();
                dynamic folder = folders.Dequeue();
                object? taskCollection = null;
                object? childCollection = null;
                try
                {
                    taskCollection = folder.GetTasks(1); // TASK_ENUM_HIDDEN; enumeration only.
                    dynamic tasks = taskCollection;
                    for (var i = 1; i <= (int)tasks.Count && count < MaxEntries; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        dynamic task = tasks[i];
                        try
                        {
                            var path = (string)task.Path;
                            var state = (int)task.State;
                            var enabled = (bool)task.Enabled;
                            var facts = new Dictionary<string, string> { ["configured"] = "true", ["State"] = state.ToString(CultureInfo.InvariantCulture),
                                ["Enabled"] = enabled.ToString(), ["LastTaskResult"] = ((int)task.LastTaskResult).ToString(CultureInfo.InvariantCulture),
                                ["LastRunTime"] = ((DateTime)task.LastRunTime).ToString("O", CultureInfo.InvariantCulture),
                                ["NextRunTime"] = ((DateTime)task.NextRunTime).ToString("O", CultureInfo.InvariantCulture) };
                            output.Add(Observed("windows.tasks.entries", "task:" + path, (string)task.Name, $"Habilitada: {enabled}; estado: {state}", "Task Scheduler COM IRegisteredTask", facts,
                                "Não foi iniciada nem alterada. Estado e último resultado não demonstram carga ou efeito durante jogos."));
                            count++;
                        }
                        finally { ReleaseCom((object)task); }
                    }
                    childCollection = folder.GetFolders(0);
                    dynamic children = childCollection;
                    for (var i = 1; i <= (int)children.Count && folders.Count + visited < 128; i++) folders.Enqueue((object)children[i]);
                }
                catch (Exception error) when (ReadFailure(error)) { output.Add(Failure("windows.tasks.entries", "Task Scheduler folder " + visited, "Tarefas", error)); }
                finally { ReleaseCom(taskCollection); ReleaseCom(childCollection); ReleaseCom((object)folder); }
            }
            if (count >= MaxEntries || visited >= 128 || folders.Count > 0)
                output.Add(Missing("windows.tasks.entries", "tasks:limit", "Sistema", Availability.InsufficientData, "Enumeração limitada a 256 tarefas e 128 pastas. Outras tarefas podem existir."));
            else if (count == 0) output.Add(Missing("windows.tasks.entries", "tasks:none", "Sistema", Availability.InsufficientData, "Nenhuma tarefa pôde ser enumerada."));
        }
        catch (Exception error) when (ReadFailure(error)) { output.Add(Failure("windows.tasks.entries", "Task Scheduler", "Tarefas", error)); }
        finally { foreach (var folder in folders) ReleaseCom(folder); ReleaseCom(serviceObject); }
    }

    [SupportedOSPlatform("windows")]
    private static void ReadBcd(List<AuditObservation> output, CancellationToken ct)
    {
        const string source = "bcdedit /enum {current}";
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        {
            foreach (var name in BcdNames) output.Add(Missing("windows.bcd." + name, "bcd:" + name, "Inicialização", Availability.PermissionRequired,
                "A leitura BCD exige uma sessão já elevada. Nenhuma elevação foi solicitada automaticamente."));
            return;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var process = new Process { StartInfo = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "bcdedit.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.StartInfo.ArgumentList.Add("/enum"); process.StartInfo.ArgumentList.Add("{current}");
            if (!process.Start()) throw new InvalidOperationException("bcdedit não iniciou.");
            try
            {
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
                var text = stdout.GetAwaiter().GetResult();
                _ = stderr.GetAwaiter().GetResult();
                if (process.ExitCode != 0) throw new Win32Exception(process.ExitCode, "A leitura BCD retornou erro.");
                foreach (var name in BcdNames)
                {
                    var value = ParseBcdValue(text, name);
                    var facts = new Dictionary<string, string> { ["presence"] = value is null ? "absent" : "present", ["configured"] = value is null ? "false" : "true", ["evidence"] = source };
                    output.Add(new("windows.bcd." + name, "bcd:" + name, name, "Entrada de boot atual", value, Availability.Available, source, facts,
                        "O BCD não confirma qual fonte de clock está em uso nem se houve reinicialização após alterações externas."));
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { foreach (var name in BcdNames) output.Add(Missing("windows.bcd." + name, "bcd:" + name, "Inicialização", Availability.ReadError, "A leitura BCD excedeu cinco segundos.")); }
            finally { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        }
        catch (Exception error) when (ReadFailure(error)) { foreach (var name in BcdNames) output.Add(Failure("windows.bcd." + name, source + ":" + name, "Inicialização", error)); }
    }

    private static readonly string[] BcdNames = ["useplatformclock", "useplatformtick", "disabledynamictick", "tscsyncpolicy"];
    internal static string? ParseBcdValue(string text, string name)
    {
        foreach (var line in text.Split('\n'))
        {
            var split = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length == 2 && split[0].Equals(name, StringComparison.OrdinalIgnoreCase)) return split[1].Trim();
        }
        return null;
    }

    private static AuditObservation Observed(string rule, string instance, string name, string raw, string source,
        Dictionary<string, string> facts, string? limitation = null)
    {
        facts.TryAdd("presence", "present"); facts.TryAdd("configured", "false"); facts.TryAdd("evidence", source);
        return new(rule, instance, name, "Sistema", raw, Availability.Available, source, facts, limitation);
    }
    private static AuditObservation Missing(string rule, string instance, string scope, Availability status, string limitation) =>
        new(rule, instance, WindowsAuditCatalog.Rules.First(r => r.Id == rule).Name, scope, null, status,
            WindowsAuditCatalog.Rules.First(r => r.Id == rule).Method, new Dictionary<string, string>(), limitation);
    private static AuditObservation Failure(string rule, string instance, string scope, Exception error) =>
        Missing(rule, instance, scope, error is UnauthorizedAccessException or SecurityException || error is Win32Exception { NativeErrorCode: 5 }
            ? Availability.PermissionRequired : Availability.ReadError,
            $"Leitura indisponível ({error.GetType().Name}, 0x{error.HResult:X8}). Permissões, serviço indisponível ou processo encerrado podem impedir esta consulta.");
    private static bool ReadFailure(Exception error) => error is UnauthorizedAccessException or SecurityException or IOException or
        InvalidOperationException or ArgumentException or Win32Exception or ManagementException or COMException or NotSupportedException;
    private static string HiveName(RegistryHive hive) => hive == RegistryHive.CurrentUser ? "HKCU" : "HKLM";
    [SupportedOSPlatform("windows")]
    private static void ReleaseCom(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }

    [StructLayout(LayoutKind.Sequential)] private struct TimerCaps { public uint Minimum, Maximum; }
    [DllImport("winmm.dll")] private static extern uint timeGetDevCaps(out TimerCaps caps, uint size);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SystemParametersInfo(uint action, uint parameter, [Out] int[] values, uint flags);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("powrprof.dll")] private static extern uint PowerReadACValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(nint root, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
}
