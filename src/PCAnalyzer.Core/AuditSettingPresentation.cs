using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PCAnalyzer.Core;

public enum AuditRecommendationKind { SpecificValue, KeepCurrent, Compare, NoUniversalValue, Unavailable }

/// <summary>A read-only, compact projection. Original evidence remains available for inspection/export.</summary>
public sealed record AuditSettingPresentation(
    string Key, string CategoryId, string Subcategory, string Name, string Description,
    string CurrentValue, string RecommendedValue, string StatusLabel, string Tone,
    AuditRecommendationKind RecommendationKind, bool IsImprovement, bool IsComparison,
    bool IsAdequate, bool IsUnassessed, int Priority, string Reason, string PossibleImpact,
    string ConfidenceLabel, string Validation, string Risks, IReadOnlyList<AuditAssessment> Evidence,
    string? TestId, IReadOnlyList<string> TechnicalDetails);

/// <summary>
/// Presentation policy never treats an enum, registry presence, or missing measurement as an ideal value.
/// No collector calls or configuration writes are performed here.
/// </summary>
public static class AuditPresentationBuilder
{
    private static readonly Regex Technical = new(
        @"(?:[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}|[A-Za-z]:[\\/]|\\\\|HK(?:LM|CU|CR|U)\\|HKEY_|\b(?:PCI|USB|HID|SWD|ROOT|ACPI)\\|#<CLIXML|<[/!?]?[A-Za-z][^>]*>|&lt;[/!?]?[A-Za-z]|\b(?:System\.[A-Za-z.]+Exception|at [A-Za-z.]+\())",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex RegistryView = new(@"\s*\[Registry(?:32|64)\]", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex HexSuffix = new(@"\s*\(0x[0-9a-f]+(?:;\s*\d+)?\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex HexOnly = new(@"^(?:0x)?[0-9a-f]{12,}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static IReadOnlyList<AuditSettingPresentation> Build(PerformanceAuditReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var rows = new List<AuditSettingPresentation>();
        var devices = report.Assessments.Where(a => IsDevice(a) && a.Rule.Id != "devices.usb.polling").ToArray();
        foreach (var group in devices.GroupBy(a => a.Observation.InstanceId, StringComparer.OrdinalIgnoreCase))
            rows.Add(CreateDevice(group.ToArray()));
        foreach (var group in report.Assessments.Except(devices).GroupBy(GroupKey, StringComparer.Ordinal))
            rows.Add(Create(group.ToArray()));
        return rows.OrderByDescending(r => r.Priority).ThenBy(r => r.CategoryId, StringComparer.Ordinal)
            .ThenBy(r => r.Subcategory, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Key, StringComparer.Ordinal).ToArray();
    }

    private static string GroupKey(AuditAssessment a)
    {
        var o = a.Observation;
        // Aggregate snapshots, not independently configurable values. Every source row is retained in Evidence.
        if (o.RuleId == "windows.process.scheduling" && !IsGame(o) && Fact(o, "PriorityClass") != "RealTime")
            return $"process:{o.Status}:{a.Classification}:{Fact(o, "PriorityClass")}";
        if (o.RuleId is "windows.services.state" or "windows.tasks.entries" or "windows.startup.entries")
            return $"overview:{o.RuleId}:{o.Status}:{a.Classification}";
        if (o.RuleId is "network.statistics" or "storage.reliability")
            return $"counters:{o.RuleId}:{o.Scope}:{o.InstanceId[..Math.Max(0, o.InstanceId.LastIndexOf(':'))]}";
        if (o.Facts.ContainsKey("registryView"))
        {
            var facts = string.Join("\n", o.Facts.Where(f => f.Key is not "registryView" and not "evidence")
                .OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key.Length}:{f.Key}{f.Value.Length}:{f.Value}"));
            return $"registry:{o.RuleId}:{RegistryView.Replace(o.InstanceId, "")}:{o.Scope}:{o.Name}:{o.Status}:{o.RawValue}:{facts}:{a.Classification}";
        }
        return $"item:{o.RuleId}:{o.InstanceId}";
    }

    private static AuditSettingPresentation Create(IReadOnlyList<AuditAssessment> evidence)
    {
        var a = evidence.OrderByDescending(Severity).First();
        var o = a.Observation;
        var category = Category(o);
        var description = Description(o);
        var name = Name(o);
        var value = CurrentValue(o);
        var aggregate = evidence.Count > 1 && (o.RuleId is "windows.process.scheduling" or "windows.services.state" or
            "windows.tasks.entries" or "windows.startup.entries" or "network.statistics" or "storage.reliability");
        if (aggregate)
        {
            name = o.RuleId switch
            {
                "windows.process.scheduling" => "Processos · " + PriorityName(Fact(o, "PriorityClass")),
                "windows.services.state" => "Serviços do Windows",
                "windows.tasks.entries" => "Tarefas agendadas",
                "windows.startup.entries" => "Aplicativos de inicialização",
                "network.statistics" => "Contadores da conexão",
                _ => "Telemetria do SSD"
            };
            var available = evidence.Count(e => IsReadable(e.Observation));
            value = available > 0 ? $"{available} leituras disponíveis" : "Não identificado";
            description = o.RuleId is "network.statistics" or "storage.reliability"
                ? Plain(o.Scope, description, 90) : description;
            if (o.RuleId == "storage.reliability" && evidence.FirstOrDefault(e => e.Observation.Name == "Temperature" && IsReadable(e.Observation)) is { } temperature)
                value = CurrentValue(temperature.Observation) + " · temperatura informada";
        }
        return Finish(evidence, a, category, Subcategory(o), name, description, value,
            informational: o.RuleId.StartsWith("network.", StringComparison.Ordinal) &&
                bool.TryParse(Fact(o, "HardwareInterface"), out var hardware) && !hardware);
    }

    private static AuditSettingPresentation CreateDevice(IReadOnlyList<AuditAssessment> evidence)
    {
        var identity = evidence.FirstOrDefault(a => a.Rule.Id == "devices.identity");
        var representative = evidence.OrderByDescending(Severity).First();
        var o = identity?.Observation ?? representative.Observation;
        var available = evidence.Where(e => IsReadable(e.Observation)).ToArray();
        var msi = available.FirstOrDefault(e => e.Rule.Id == "devices.interrupt.msi");
        var affinity = available.FirstOrDefault(e => e.Rule.Id == "devices.interrupt.affinity");
        var parts = new List<string>();
        if (Fact(o, "pnpProblemCode") == "22") parts.Add("Desabilitado no Windows");
        if (msi is not null) parts.Add(CurrentValue(msi.Observation));
        if (affinity is not null) parts.Add(CurrentValue(affinity.Observation));
        if (parts.Count == 0)
        {
            if (available.Length == 0) parts.Add("Não identificado");
            else if (Fact(o, "pnpProblemCode") == "0") parts.Add("Sem alerta PnP · modo de interrupção não confirmado");
            else parts.Add("Leitura disponível · modo efetivo não confirmado");
        }
        // A healthy PnP identity alone cannot certify all interrupt/affinity policies as adequate.
        if (representative.Classification == AuditClassification.Adequate && evidence.Count > 1)
            representative = representative with { Classification = AuditClassification.AppliedUnproven };
        var name = Plain(o.Name.Split(" — ", StringSplitOptions.None)[0], "Dispositivo", 85);
        return Finish(evidence, representative, "devices", DeviceGroup(o), name,
            "Interrupções, afinidade e estado do dispositivo.", Plain(string.Join(" · ", parts), "Consulte os detalhes", 100),
            informational: IsLowRelevanceDevice(o));
    }

    private static AuditSettingPresentation Finish(IReadOnlyList<AuditAssessment> evidence, AuditAssessment a,
        string category, string subcategory, string name, string description, string current, bool informational = false)
    {
        var available = evidence.Any(e => IsReadable(e.Observation));
        var ignored = a.Rule.Support == AuditSupport.Ignored || a.Classification == AuditClassification.NoDocumentedEffect;
        var comparison = available && !informational && !ignored && a.Classification == AuditClassification.ComparisonOpportunity;
        var problem = available && !informational && !ignored && a.Classification == AuditClassification.DemonstratedProblem;
        var possible = available && !informational && !ignored && a.Classification == AuditClassification.PossibleProblem;
        var adequate = available && !informational && !ignored && a.Classification == AuditClassification.Adequate;
        var unassessed = !available || (!ignored && !informational && a.Classification is AuditClassification.InsufficientData or AuditClassification.Unsupported or AuditClassification.AppliedUnproven);
        // SpecificValue is intentionally not emitted: no current rule establishes a universal, compatible replacement.
        var kind = !available ? AuditRecommendationKind.Unavailable : comparison ? AuditRecommendationKind.Compare :
            adequate || ignored || informational ? AuditRecommendationKind.KeepCurrent : AuditRecommendationKind.NoUniversalValue;
        var recommended = kind switch
        {
            AuditRecommendationKind.Unavailable => "Não determinado",
            AuditRecommendationKind.KeepCurrent => "Manter",
            AuditRecommendationKind.Compare => "Vale comparar",
            _ => possible || problem ? "Investigar antes de alterar" : "Depende de medição"
        };
        var status = !available ? "Não avaliado" : informational ? "Informativo" : ignored ? "Sem efeito documentado" :
            problem ? "Problema demonstrado" : possible ? "Investigar" : comparison ? "Comparação opcional" : adequate ? "Sem alerta neste item" : "Efeito não verificado";
        var tone = problem ? "problem" : possible || comparison ? "attention" : adequate ? "success" : "neutral";
        var priority = problem ? 100 : possible ? 80 : comparison ? 50 : adequate ? 20 : ignored || informational ? 5 : 10;
        var reason = !available ? AvailabilityReason(a.Observation) : informational
            ? "Dispositivos virtuais ou desabilitados não demonstram perda de desempenho. Investigue apenas se este dispositivo for necessário ao cenário avaliado."
            : Plain(a.Reason, "O valor foi preservado; consulte a evidência técnica para interpretar esta leitura.", 420);
        var test = available && !ignored && !informational ? CompatibleTest(a, comparison || possible || problem) : null;
        var validation = Validation(category, test);
        var impact = ignored || informational || !available ? "Nenhum impacto no desempenho demonstrado." : Impact(category, a.Observation.RuleId);
        var key = a.Observation.RuleId + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join("\n", evidence.Select(e => e.Observation.InstanceId).Order(StringComparer.Ordinal)))))[..16];
        return new(key, category, subcategory, name, description, current, recommended, status, tone, kind,
            problem || possible || comparison, comparison, adequate, unassessed, priority, reason, impact,
            !available ? "Insuficiente" : a.Confidence switch { Confidence.High => "Alta", Confidence.Medium => "Média", _ => "Baixa" },
            validation, Plain(a.Risks, "Mudanças externas podem afetar estabilidade; consulte os limites da leitura.", 260),
            evidence, test, evidence.Select(TechnicalDetail).ToArray());
    }

    private static bool IsReadable(AuditObservation o) => o.Status == Availability.Available &&
        Fact(o, "presence") != "absent" && (!string.IsNullOrWhiteSpace(o.RawValue) ||
        o.RuleId == "devices.identity" && uint.TryParse(Fact(o, "pnpProblemCode"), NumberStyles.None,
            CultureInfo.InvariantCulture, out _));
    private static bool IsDevice(AuditAssessment a) => a.Rule.Id.StartsWith("devices.", StringComparison.Ordinal);
    private static string? Fact(AuditObservation o, string key) => o.Facts.GetValueOrDefault(key);
    private static int Severity(AuditAssessment a) => a.Classification switch
    {
        AuditClassification.DemonstratedProblem => 100, AuditClassification.PossibleProblem => 80,
        AuditClassification.ComparisonOpportunity => 50, AuditClassification.Adequate => 20,
        AuditClassification.NoDocumentedEffect => 5, _ => 10
    };

    private static string Category(AuditObservation o)
    {
        if (IsGame(o)) return "games";
        if (o.RuleId.StartsWith("nvidia.", StringComparison.Ordinal)) return "nvidia";
        if (o.RuleId.StartsWith("network.", StringComparison.Ordinal)) return "network";
        if (o.RuleId.StartsWith("devices.", StringComparison.Ordinal) || o.RuleId.StartsWith("windows.mouse.", StringComparison.Ordinal) || o.RuleId == "windows.input.queue") return "devices";
        if (o.RuleId.StartsWith("hardware.", StringComparison.Ordinal) || o.RuleId.StartsWith("memory.", StringComparison.Ordinal) || o.RuleId.StartsWith("storage.", StringComparison.Ordinal)) return "hardware";
        return "windows";
    }
    private static bool IsGame(AuditObservation o) =>
        new[] { Fact(o, "ExecutableName"), Fact(o, "executable"), o.Scope, o.Name, o.InstanceId }
            .Any(value => value?.Contains("VALORANT", StringComparison.OrdinalIgnoreCase) == true);

    private static string Subcategory(AuditObservation o) => o.RuleId switch
    {
        var id when id.StartsWith("nvidia.", StringComparison.Ordinal) => NvidiaScope(o),
        var id when id.StartsWith("windows.mmcss.", StringComparison.Ordinal) => "MMCSS",
        var id when id.StartsWith("windows.ifeo.", StringComparison.Ordinal) => "Prioridades",
        "windows.scheduler.separation" => "Agendamento",
        "windows.process.scheduling" => "Processos",
        var id when id.StartsWith("windows.power.", StringComparison.Ordinal) => "Energia",
        "windows.services.state" => "Serviços",
        "windows.startup.entries" or "windows.tasks.entries" => "Inicialização e tarefas",
        var id when id.StartsWith("windows.timers.", StringComparison.Ordinal) || id.StartsWith("windows.bcd.", StringComparison.Ordinal) => "Temporizadores",
        var id when id.StartsWith("windows.gaming.", StringComparison.Ordinal) || id.StartsWith("windows.graphics.", StringComparison.Ordinal) => "Gráficos do Windows",
        var id when id.StartsWith("windows.mouse.", StringComparison.Ordinal) || id == "windows.input.queue" => "USB e periféricos",
        "windows.memory.policy" => "Memória e paginação",
        "network.adapter" => "Conexão",
        "network.rss" => "Distribuição RSS",
        "network.power" => "Energia",
        "network.statistics" => "Contadores",
        "network.tcp" => "Perfis TCP",
        "network.advanced" => "Configurações do driver",
        var id when id.StartsWith("hardware.cpu.", StringComparison.Ordinal) => "CPU",
        var id when id.StartsWith("memory.", StringComparison.Ordinal) => "RAM",
        var id when id.StartsWith("storage.", StringComparison.Ordinal) => "Armazenamento",
        var id when id.StartsWith("devices.", StringComparison.Ordinal) => DeviceGroup(o),
        _ => "Sistema"
    };
    private static string NvidiaScope(AuditObservation o)
    {
        if (IsGame(o)) return "Perfil do VALORANT";
        var scope = o.Scope;
        if (scope.Contains("global", StringComparison.OrdinalIgnoreCase)) return "Perfil global";
        if (scope.Contains("base", StringComparison.OrdinalIgnoreCase)) return "Perfil base";
        if (o.RuleId is "nvidia.low-latency" or "nvidia.reflex") return "Latência";
        return Plain(scope, "Perfis de aplicativos", 70);
    }
    private static string DeviceGroup(AuditObservation o)
    {
        var text = (Fact(o, "class") + " " + o.Scope + " " + o.Name).ToLowerInvariant();
        if (text.Contains("display") || text.Contains("nvidia") || text.Contains("radeon")) return "GPU";
        if (text.Contains("net") || text.Contains("ethernet") || text.Contains("wi-fi")) return "Rede";
        if (text.Contains("usb") || text.Contains("hid") || text.Contains("mouse") || text.Contains("keyboard")) return "USB e periféricos";
        if (text.Contains("audio") || text.Contains("áudio") || text.Contains("media")) return "Áudio";
        if (text.Contains("nvme") || text.Contains("scsi") || text.Contains("storage") || text.Contains("disk")) return "Armazenamento";
        return "Outros controladores";
    }
    private static bool IsLowRelevanceDevice(AuditObservation o) => Fact(o, "pnpProblemCode") == "22" ||
        o.Name.Contains("virtual", StringComparison.OrdinalIgnoreCase) || o.Name.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase) ||
        Fact(o, "busEnumerator")?.Equals("SWD", StringComparison.OrdinalIgnoreCase) == true;

    private static string Name(AuditObservation o)
    {
        var baseName = o.RuleId switch
        {
            "storage.physical" => Plain(o.Scope, "Disco físico", 72),
            "memory.modules" => "RAM · " + Plain(o.Scope, "Módulo", 55),
            "windows.scheduler.separation" => "Win32PrioritySeparation",
            "windows.mmcss.gpu" => "MMCSS · GPU Priority",
            "windows.mmcss.sfio" => "MMCSS · SFIO Priority",
            _ => Plain(o.Name, "Configuração auditada", 85)
        };
        if (o.RuleId.StartsWith("windows.mmcss.", StringComparison.Ordinal) && o.Scope is not "Sistema" and not "Registro" && !string.IsNullOrWhiteSpace(o.Scope))
            baseName += " · " + Plain(o.Scope, "Perfil", 35);
        if (o.RuleId.StartsWith("windows.ifeo.", StringComparison.Ordinal))
            baseName += " · " + Plain(Fact(o, "executable") ?? o.Scope, "Aplicativo", 55);
        return Plain(baseName, "Configuração auditada", 100);
    }

    private static string Description(AuditObservation o) => o.RuleId switch
    {
        "windows.scheduler.separation" => "Política de agendamento do Windows.",
        "windows.mmcss.responsiveness" => "Reserva do mecanismo multimídia do Windows.",
        var id when id.StartsWith("windows.mmcss.", StringComparison.Ordinal) => "Preferência da tarefa multimídia; uso pelo jogo não confirmado.",
        var id when id.StartsWith("windows.ifeo.", StringComparison.Ordinal) => "Preferência persistente por executável.",
        "windows.process.scheduling" => "Classe e afinidade observadas nesta captura.",
        var id when id.StartsWith("windows.power.", StringComparison.Ordinal) => "Política configurada; consumo e clocks exigem medição.",
        "windows.services.state" => "Estado dos serviços; atividade não comprova consumo.",
        "windows.tasks.entries" => "Tarefas registradas; nenhuma tarefa foi executada.",
        "windows.startup.entries" => "Entradas persistentes; execução não confirmada.",
        var id when id.StartsWith("windows.gaming.", StringComparison.Ordinal) || id.StartsWith("windows.graphics.", StringComparison.Ordinal) => "Preferência gráfica do Windows; efeito no jogo não medido.",
        var id when id.StartsWith("windows.timers.", StringComparison.Ordinal) || id.StartsWith("windows.bcd.", StringComparison.Ordinal) => "Temporização do Windows; não representa input lag.",
        var id when id.StartsWith("windows.mouse.", StringComparison.Ordinal) || id == "windows.input.queue" => "Preferência de entrada; não mede polling nem clique para fóton.",
        "windows.memory.policy" => "Configuração de memória e paginação.",
        var id when id.StartsWith("nvidia.", StringComparison.Ordinal) => NvidiaScope(o) + (Fact(o, "origin")?.StartsWith("Herdado", StringComparison.Ordinal) == true ? " · valor herdado." : " · preferência do driver."),
        "network.adapter" => "Estado e driver da conexão; enlace não é latência.",
        "network.advanced" => Plain(o.Scope, "Opção anunciada pelo driver da conexão.", 90),
        "network.rss" => "Distribuição configurada do processamento de rede.",
        "network.power" => "Preferência de energia da conexão.",
        "network.statistics" => "Totais acumulados; não representam perda no jogo.",
        "network.tcp" => "Perfil TCP; não se aplica ao tráfego UDP do jogo.",
        "hardware.cpu.topology" => "Núcleos e processadores lógicos identificados pelo Windows.",
        "memory.modules" => "Capacidade e velocidade informadas pelo firmware.",
        "storage.physical" => "Saúde informada pelo provedor do Windows.",
        "storage.reliability" => "Contadores do SSD; não são um benchmark da sessão.",
        "storage.location" => "Associação da unidade com Windows e paginação.",
        _ => "Leitura de configuração; nenhuma alteração automática."
    };

    private static string CurrentValue(AuditObservation o)
    {
        if (!IsReadable(o)) return Fact(o, "presence") == "absent" ? "Sem valor explícito" : "Não identificado";
        var number = Fact(o, "decimal");
        if (o.RuleId == "windows.power.policy" && Fact(o, "ActiveScheme") is { } scheme)
            return scheme.ToLowerInvariant() switch
            {
                "381b4222-f694-41f0-9685-ff5bb260df2e" => "Equilibrado",
                "a1841308-3541-4fab-bc81-f71556f20b4a" => "Economia de energia",
                "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c" => "Alto desempenho",
                _ => "Plano personalizado"
            };
        if (o.RuleId == "windows.power.policy" && Fact(o, "unit") is { } unit) return Plain(o.RawValue + " " + unit, "Índice configurado", 90);
        if (o.RuleId == "windows.process.scheduling") return "Prioridade " + PriorityName(Fact(o, "PriorityClass"));
        if (o.RuleId == "devices.interrupt.msi") return number switch { "1" => "MSI permitido na configuração", "0" => "MSI não habilitado na configuração", _ => "Configuração MSI não interpretada" };
        if (o.RuleId == "devices.interrupt.affinity" && Fact(o, "logicalBits") is { } bits)
            return LogicalProcessors(bits, Fact(o, "groupContext"));
        if (o.RuleId == "devices.interrupt.policy" && Fact(o, "policyDescription") is { } policy) return Plain(policy, "Política configurada", 90);
        if (o.RuleId == "storage.physical") return Fact(o, "HealthStatus") switch { "0" => "Saudável segundo o Windows", "1" => "Alerta de saúde", "2" => "Estado não saudável", _ => "Saúde não determinada" };
        if (o.RuleId == "memory.modules")
        {
            var capacity = double.TryParse(Fact(o, "Capacity"), NumberStyles.Number, CultureInfo.InvariantCulture, out var bytes) && bytes > 0
                ? (bytes / 1073741824d).ToString("0.#", CultureInfo.InvariantCulture) + " GiB" : "Capacidade não identificada";
            return capacity + (uint.TryParse(Fact(o, "ConfiguredTransferRateMTs"), out var speed) && speed > 0 ? $" · {speed} MT/s configurados" : "");
        }
        if (o.RuleId == "network.advanced" && Fact(o, "DisplayValue") is { } display && display != "não informado") return Friendly(display);
        if (o.RuleId.StartsWith("nvidia.", StringComparison.Ordinal)) return Plain(HexSuffix.Replace(o.RawValue!, ""), "Valor disponível nos detalhes", 90);
        if (o.RuleId is "windows.mouse.curves" or "windows.startup.entries" || Fact(o, "registryType") == "Binary") return "Configurado · detalhes disponíveis";
        if (number is not null) return Plain(number, "Valor numérico disponível", 90);
        var raw = o.RawValue!;
        if (HexOnly.IsMatch(raw)) return "Valor disponível nos detalhes";
        return Friendly(raw);
    }

    private static string LogicalProcessors(string bits, string? group)
    {
        var processors = bits.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value is >= 0 and < 64 ? (int?)value : null).ToArray();
        if (processors.Length == 0 || processors.Any(p => p is null)) return "Afinidade não interpretada";
        var list = processors.Select(p => p!.Value).Distinct().Order().ToArray();
        var label = list.Length > 8 ? $"{list.Length} processadores lógicos configurados" : "CPUs " + string.Join(", ", list);
        return label + (int.TryParse(group, out var groupNumber) && groupNumber >= 0 ? $" · grupo {groupNumber}" : " · grupo não confirmado") + " · configuração";
    }
    private static string PriorityName(string? value) => value switch
    {
        "Normal" => "normal", "RealTime" => "tempo real", "High" => "alta", "AboveNormal" => "acima do normal",
        "BelowNormal" => "abaixo do normal", "Idle" => "ociosa", _ => "não identificada"
    };
    private static string Friendly(string value) => Plain(value.Trim() switch
    {
        "True" or "true" or "Enabled" or "Enable" => "Ativado", "False" or "false" or "Disabled" or "Disable" => "Desativado",
        "NotSupported" => "Não suportado", "NotConfigured" => "Sem configuração explícita", _ => value
    }, "Valor disponível nos detalhes", 90);
    private static string Plain(string? value, string fallback, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || Technical.IsMatch(value)) return fallback;
        var text = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return text.Length <= maximum ? text : text[..(maximum - 1)].TrimEnd() + "…";
    }
    private static string AvailabilityReason(AuditObservation o) => Fact(o, "presence") == "absent"
        ? "Esta origem não contém um valor explícito. Isso pode representar o padrão; o estado efetivo não foi confirmado."
        : o.Status switch
        {
            Availability.PermissionRequired => "A origem não permitiu a leitura. Nenhum valor foi inferido.",
            Availability.Unsupported or Availability.ToolMissing => "A fonte necessária não está disponível neste ambiente. Nenhum valor foi inferido.",
            Availability.ReadError => "A leitura falhou. O erro completo está nos detalhes técnicos.",
            _ => "Os dados não permitem determinar este valor. Uma nova leitura ou medição pode complementar a análise."
        };
    private static string? CompatibleTest(AuditAssessment a, bool hypothesis)
    {
        var id = a.Rule.Id;
        if (id == "memory.modules") return "memory-access";
        if (id is "storage.physical" or "storage.reliability" && hypothesis) return "disk-read";
        if (id is "network.advanced" or "network.rss" or "network.adapter" && hypothesis) return "ping";
        if (id.StartsWith("devices.interrupt.", StringComparison.Ordinal) && hypothesis) return "dpc";
        if ((id.StartsWith("nvidia.", StringComparison.Ordinal) || id.StartsWith("windows.gaming.", StringComparison.Ordinal)) && hypothesis) return "frames";
        return null;
    }
    private static string Validation(string category, string? test) => test switch
    {
        "ping" => "Meça RTT e jitter no mesmo destino. ICMP não comprova perda no jogo nem isola o efeito desta configuração.",
        "dpc" => "Capture DPC/ISR durante a mesma carga. A captura mede execução; não comprova o modo MSI nem input lag físico.",
        "frames" => "Selecione o processo e capture frames com PresentMon. Compare a mesma cena; o perfil lido não comprova efeito na renderização.",
        "memory-access" => "Compare acesso e transferência em buffers virtuais. O teste inclui CPU e cache; não isola a latência DRAM.",
        "disk-read" => "Escolha um arquivo na unidade avaliada. A leitura inclui cache e não substitui diagnóstico de saúde do fabricante.",
        _ => category == "network" ? "Compare a mesma conexão e carga. Contadores TCP não representam tráfego UDP do jogo."
            : "Confirme a relevância para sua carga e compare sessões equivalentes. Eventuais mudanças são externas ao aplicativo."
    };
    private static string Impact(string category, string rule) => category switch
    {
        "network" => "CPU, estabilidade e variação de latência da rede; efeito ainda não medido.",
        "devices" => "Distribuição de processamento e estabilidade; efeito ainda não medido.",
        "nvidia" or "games" => "Frametime, qualidade visual e uso de GPU; efeito depende do jogo.",
        "hardware" => rule.StartsWith("storage.", StringComparison.Ordinal) ? "Atividade e estabilidade da unidade; desempenho exige medição." : "CPU, cache e memória; desempenho exige medição.",
        _ => "Agendamento e comportamento do sistema; benefício não demonstrado por este valor."
    };
    private static string TechnicalDetail(AuditAssessment a)
    {
        var o = a.Observation;
        return $"{o.Name}\nRegra: {a.Rule.Id}\nIdentidade: {o.InstanceId}\nEscopo: {o.Scope}\nEstado: {o.Status}\n" +
            $"Valor original: {o.RawValue ?? "não retornado"}\nOrigem: {o.Source}\n" +
            string.Join("\n", o.Facts.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => $"{f.Key}: {f.Value}")) +
            $"\nInterpretação: {a.Summary}\nJustificativa: {a.Reason}\nMétodo: {a.Rule.Method}\nPré-requisitos: {a.Rule.Prerequisites}" +
            $"\nCompatibilidade: {a.Rule.Compatibility}\nEvidência necessária: {a.Rule.RequiredEvidence}\nLimites: {o.Limitation} {a.Rule.Limitations}" +
            $"\nRiscos: {a.Risks}\nValidação: {a.Validation}\nFontes: {string.Join("\n", a.Rule.Sources)}";
    }
}
