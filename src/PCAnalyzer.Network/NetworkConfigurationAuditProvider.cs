using System.Globalization;
using PCAnalyzer.Core;

namespace PCAnalyzer.Network;

/// <summary>Queries advertised driver properties; no defaults, ICMP or configuration writes.</summary>
public sealed class NetworkConfigurationAuditProvider : IPerformanceAuditProvider
{
    private readonly NetworkInspector _inspector;
    public NetworkConfigurationAuditProvider() : this(new NetworkInspector()) { }
    public NetworkConfigurationAuditProvider(NetworkInspector inspector) => _inspector = inspector;

    public IReadOnlyList<AuditRule> Rules { get; } =
    [
        Rule("network.adapter", "Conexão e driver", "System.Net.NetworkInformation / Get-NetAdapter", "Estado, identidade verificada, driver e velocidade do enlace.",
            "Velocidade negociada não mede ping nem throughput; conexões virtuais não representam necessariamente uma porta física.", "https://learn.microsoft.com/powershell/module/netadapter/get-netadapter"),
        Rule("network.advanced", "Propriedades anunciadas pelo driver", "Get-NetAdapterAdvancedProperty", "RegistryKeyword, valores atuais e enumerações anunciadas.",
            "Moderation, buffers, offloads e EEE envolvem compromissos. A ausência de uma opção não comprova que o mecanismo está desativado.", "https://learn.microsoft.com/powershell/module/netadapter/get-netadapteradvancedproperty"),
        Rule("network.rss", "Distribuição RSS", "Get-NetAdapterRss", "Enabled, filas e processadores reportados pelo driver.",
            "Afinidade configurada não demonstra atividade de interrupções; RSS não implica que todo fluxo usa todos os processadores.", "https://learn.microsoft.com/powershell/module/netadapter/get-netadapterrss"),
        Rule("network.power", "Energia do adaptador", "Get-NetAdapterPowerManagement", "Estados anunciados para suspensão, wake e offload.",
            "Estado configurado não comprova transição de energia durante o jogo.", "https://learn.microsoft.com/powershell/module/netadapter/get-netadapterpowermanagement"),
        Rule("network.statistics", "Contadores da conexão", "Get-NetAdapterStatistics", "Bytes, erros e descartes do adaptador.",
            "Contadores acumulados sem intervalo de comparação não demonstram falha atual nem perda do jogo.", "https://learn.microsoft.com/powershell/module/netadapter/get-netadapterstatistics"),
        Rule("network.tcp", "Perfis TCP", "Get-NetTCPSetting", "Auto-tuning, congestionamento, ECN e campos expostos por perfil.",
            "Perfil disponível não identifica o perfil usado por uma conexão. UDP de jogos não utiliza esses ajustes TCP.", "https://learn.microsoft.com/powershell/module/nettcpip/get-nettcpsetting")
    ];

    public async Task<IReadOnlyList<AuditObservation>> ReadAsync(CancellationToken ct)
    {
        var snapshot = await _inspector.DiscoverAsync(ct).ConfigureAwait(false);
        var observations = new List<AuditObservation>();
        if (snapshot.Status != Availability.Available)
            return Rules.Select(r => Missing(r.Id, r.Name, "Rede", snapshot.Status, snapshot.Limitation ?? "A fonte de rede está indisponível.")).ToArray();
        // Bound the work without guessing an Intel model. Offline Ethernet adapters remain eligible.
        var candidates = snapshot.Adapters.Where(a => a.Type is "Ethernet" or "Wireless80211" or "GigabitEthernet" or "FastEthernetFx" or "FastEthernetT")
            .OrderByDescending(a => a.Status == "Up").ThenBy(a => a.InterfaceGuid).Take(8).ToArray();
        foreach (var adapter in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var detail = await _inspector.InspectAdapterAsync(adapter.InterfaceGuid, ct).ConfigureAwait(false);
            observations.AddRange(FromAdapter(adapter, detail));
        }
        if (candidates.Length == 0)
            foreach (var rule in Rules.Where(r => r.Id != "network.tcp"))
                observations.Add(Missing(rule.Id, rule.Name, "Rede", Availability.InsufficientData, "Nenhuma conexão Ethernet ou Wi-Fi identificada para consulta; adaptadores de túnel não foram tratados como portas físicas."));
        var tcp = await _inspector.InspectTcpAsync(ct).ConfigureAwait(false);
        observations.AddRange(FromFacts("network.tcp", "TCP", "Perfis TCP", tcp, configured: true));
        return observations;
    }

    internal static IReadOnlyList<AuditObservation> FromAdapter(NetworkAdapter adapter, AdapterDetail detail)
    {
        var id = adapter.InterfaceGuid.ToString("D");
        var scope = string.IsNullOrWhiteSpace(adapter.Description) ? adapter.Name : adapter.Description;
        var result = new List<AuditObservation>();
        if (detail.InterfaceGuid != adapter.InterfaceGuid)
            return [Missing("network.adapter", "Conexão e driver", scope, Availability.ReadError, "A consulta retornou outra identidade; os dados foram descartados.")];
        result.Add(new("network.adapter", id, "Conexão e driver", scope,
            $"{adapter.Name}; {adapter.Status}; driver {detail.DriverVersion ?? "não informado"}; enlace {detail.LinkSpeed ?? "não informado"}",
            detail.Status, detail.Source, new Dictionary<string, string>
            {
                ["presence"] = "present", ["configured"] = "false", ["effective"] = "Estado da interface informado pela API: " + adapter.Status,
                ["InterfaceGuid"] = id, ["DriverVersion"] = detail.DriverVersion ?? "não informado",
                ["DriverDate"] = detail.DriverDate ?? "não informado", ["SpeedBitsPerSecond"] = adapter.SpeedBitsPerSecond?.ToString(CultureInfo.InvariantCulture) ?? "não informado"
            }, detail.Limitations.Count == 0 ? "A identidade não comprova qualidade ou latência da conexão." : string.Join(" ", detail.Limitations)));
        if (detail.Status != Availability.Available)
        {
            foreach (var rule in new[] { "network.advanced", "network.rss", "network.power", "network.statistics" })
                result.Add(Missing(rule, "Leitura do driver", scope, detail.Status, string.Join(" ", detail.Limitations), id));
            return WithAdapterContext(result, adapter, detail);
        }
        foreach (var property in detail.AdvancedProperties)
        {
            var facts = new Dictionary<string, string>
            {
                ["presence"] = "present", ["configured"] = "true", ["RegistryKeyword"] = property.RegistryKeyword,
                ["DisplayValue"] = property.DisplayValue ?? "não informado",
                ["AdvertisedValues"] = string.Join(" | ", property.ValidRegistryValues),
                ["AdvertisedLabels"] = string.Join(" | ", property.ValidDisplayValues),
                ["comparison"] = property.Consequences + " Uma comparação externa controlada precisa preservar carga, destino, duração e método. Não há ganho demonstrado neste snapshot."
            };
            result.Add(new("network.advanced", id + ":" + property.RegistryKeyword, property.DisplayName, scope,
                string.Join(", ", property.RegistryValues), property.RegistryValues.Count == 0 ? Availability.InsufficientData : Availability.Available,
                "Get-NetAdapterAdvancedProperty", facts, property.Function + " " + property.ExpectedImpact));
        }
        if (detail.AdvancedProperties.Count == 0)
            result.Add(Missing("network.advanced", "Propriedades do driver", scope,
                detail.PropertiesStatus == Availability.Available ? Availability.InsufficientData : detail.PropertiesStatus,
                "Nenhuma propriedade avançada foi devolvida. Não é possível inferir seus valores.", id));
        result.AddRange(FromFacts("network.rss", id, scope, detail.Rss, true));
        result.AddRange(FromFacts("network.power", id, scope, detail.PowerManagement ?? [], true));
        result.AddRange(FromFacts("network.statistics", id, scope, detail.Statistics, false));
        return WithAdapterContext(result, adapter, detail);
    }

    private static IReadOnlyList<AuditObservation> WithAdapterContext(IEnumerable<AuditObservation> observations,
        NetworkAdapter adapter, AdapterDetail detail)
    {
        // Every row retains its adapter context so the presentation can group physical connections
        // and place virtual interfaces in details without guessing from names or driver keywords.
        var physical = detail.Identity?.FirstOrDefault(f => f.Name == "HardwareInterface" && f.Status == Availability.Available)?.Value;
        var hardwareInterface = bool.TryParse(physical, out var isHardware) ? isHardware.ToString() : "não informado";
        return observations.Select(observation => observation with
        {
            Facts = new Dictionary<string, string>(observation.Facts)
            {
                ["InterfaceGuid"] = adapter.InterfaceGuid.ToString("D"),
                ["HardwareInterface"] = hardwareInterface,
                ["AdapterStatus"] = adapter.Status,
                ["AdapterType"] = adapter.Type,
                ["HasGateway"] = adapter.Gateways.Any(value => System.Net.IPAddress.TryParse(value, out var address) &&
                    !address.Equals(System.Net.IPAddress.Any) && !address.Equals(System.Net.IPAddress.IPv6Any)) ? "true" : "false"
            }
        }).ToArray();
    }

    private static IEnumerable<AuditObservation> FromFacts(string rule, string id, string scope, IReadOnlyList<HardwareFact> facts, bool configured)
    {
        if (facts.Count == 0) return [Missing(rule, "Fonte não disponível", scope, Availability.InsufficientData, "Nenhum campo retornado por esta fonte.", id)];
        return facts.Select(f => new AuditObservation(rule, id + ":" + f.Name, f.Name, scope, f.Value, f.Status, f.Source,
            configured ? new Dictionary<string, string> { ["configured"] = "true" } :
            new Dictionary<string, string> { ["configured"] = "false", ["effective"] = "Contador acumulado reportado pelo driver; não mede variação durante a sessão." }, f.Limitation));
    }

    private static AuditObservation Missing(string rule, string name, string scope, Availability status, string reason, string? id = null) =>
        new(rule, id ?? rule, name, scope, null, status, "NetAdapter / TCP", new Dictionary<string, string>(), reason);

    private static AuditRule Rule(string id, string name, string method, string evidence, string limitation, string source) =>
        new(id, "Rede", name, method, "Windows; provedor NetAdapter/TCP e driver compatível.", "Windows 10/11; campos dependem do driver.", evidence,
            "Investigar estabilidade, distribuição de processamento e compromissos entre CPU, energia e latência; sem promessa de reduzir ping.",
            AuditSupport.Conditional, limitation, "Registrar uma sessão com o mesmo adaptador e comparar erros, RTT e DPC/ISR sob a mesma carga. Mudanças são externas ao aplicativo.", [source]);
}
