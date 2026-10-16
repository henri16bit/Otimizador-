using System.Globalization;
using System.Net;
using PCAnalyzer.Core;

namespace PCAnalyzer.Network;

/// <summary>Interprets observed adapter state. It neither changes settings nor infers Internet/game health from a link.</summary>
public static class NetworkDiagnosticAnalyzer
{
    public static NetworkAdapter? SelectActive(NetworkSnapshot snapshot, Guid? selectedAdapterId = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status != Availability.Available) return null;
        var active = snapshot.Adapters.Where(a => IsUp(a) && !IsLoopback(a));
        if (selectedAdapterId is { } id) return active.FirstOrDefault(a => a.InterfaceGuid == id);
        return active.OrderByDescending(HasGateway).ThenByDescending(IsEthernet)
            .ThenBy(a => a.InterfaceGuid).FirstOrDefault();
    }

    public static IReadOnlyList<Finding> Analyze(NetworkSnapshot before, NetworkSnapshot? after,
        IReadOnlyList<AdapterDetail> details, Guid? selectedAdapterId = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(details);
        var findings = new List<Finding>();
        var snapshot = after ?? before;
        // Keep the initial automatic choice across this observation window. If it disappears,
        // reporting another active interface would quietly change the subject of the analysis.
        var observedAdapterId = selectedAdapterId ?? SelectActive(before)?.InterfaceGuid;
        var adapter = SelectActive(snapshot, observedAdapterId);
        AddCoverage(findings, before, after);

        var inventoryEvidence = snapshot.Adapters.Select(a => new Evidence(
            $"{a.Name} · {a.Description} · tipo {a.Type} · estado {a.Status} · ID {a.InterfaceGuid:D}",
            a.Source, Timestamp: snapshot.CapturedAt)).ToArray();
        if (adapter is null)
        {
            findings.Add(Inconclusive("network.connection", observedAdapterId is null
                    ? "Nenhuma conexão ativa identificada" : "Conexão escolhida indisponível",
                snapshot.Status != Availability.Available
                    ? "A leitura das interfaces não foi concluída; não é possível afirmar o estado da conexão."
                    : observedAdapterId is null
                        ? "As interfaces observadas não incluem uma conexão ativa utilizável. Uma interface desligada, por si só, não prova um defeito."
                        : "O adaptador escolhido no início não está ativo ou não está presente nesta leitura. A análise não mudou para outra conexão.",
                inventoryEvidence, "Conecte a rede desejada e use Rede → Ler conexão. Se necessário, escolha outro adaptador."));
            return findings;
        }

        var active = snapshot.Adapters.Where(a => IsUp(a) && !IsLoopback(a)).ToArray();
        var selection = selectedAdapterId is null
            ? "Escolha automática: primeiro uma conexão com gateway, depois o tipo Ethernet; empates usam o ID da interface."
            : "Esta é a conexão escolhida pelo usuário.";
        findings.Add(Observation(adapter, "connection", "Conexão ativa identificada",
            $"{adapter.Name} · {adapter.Description}. O Windows informou estado ativo nesta leitura. " +
            selection + (active.Length > 1
                ? $" Há {active.Length} conexões ativas, todas listadas nas evidências. A rota de um destino pode usar outra interface."
                : " Esse estado não confirma acesso à Internet nem estabilidade durante todo o período."),
            inventoryEvidence, active.Length > 1
                ? "Confira a conexão desejada em Rede. Para testar um destino, confirme também a rota usada pelo Windows."
                : "Se há falha de acesso, teste primeiro o gateway e depois o destino afetado."));

        if (adapter.SpeedBitsPerSecond is > 0)
            findings.Add(Observation(adapter, "link", "Velocidade do link informada",
                $"O enlace informa {FormatSpeed(adapter.SpeedBitsPerSecond.Value)}. Esse valor é a velocidade negociada da interface, não a velocidade medida de download ou do jogo.",
                [new("Velocidade informada pelo Windows", adapter.Source, $"network.{adapter.InterfaceGuid:N}.link.bits_per_second",
                    adapter.SpeedBitsPerSecond, "bit/s", snapshot.CapturedAt)],
                "Compare com a capacidade do adaptador, da porta e do cabo somente se a velocidade estiver abaixo do esperado."));
        else
            findings.Add(Inconclusive(Id(adapter, "link"), "Velocidade do link indisponível",
                "A interface está ativa, mas não informou uma velocidade positiva nesta leitura.",
                [new($"{adapter.Name}: velocidade não informada", adapter.Source, Timestamp: snapshot.CapturedAt)],
                "Abra Rede → Ler conexão novamente e confira o estado do link nas configurações do Windows."));

        if (!HasGateway(adapter))
            findings.Add(Inconclusive(Id(adapter, "gateway"), "Gateway não informado nesta conexão",
                "Uma conexão local pode funcionar sem gateway. Esta leitura não identifica uma saída para outros destinos por esta interface.",
                [new($"Gateways informados: {(adapter.Gateways.Count == 0 ? "nenhum" : string.Join(", ", adapter.Gateways))}",
                    adapter.Source, Timestamp: snapshot.CapturedAt)],
                "Confira o endereço IP e o gateway no Windows. Para uma rede local sem gateway, informe o destino do teste manualmente."));

        AddErrors(findings, adapter, before, after);
        AddDetails(findings, adapter, details.FirstOrDefault(d => d.InterfaceGuid == adapter.InterfaceGuid));
        return findings;
    }

    private static void AddCoverage(List<Finding> findings, NetworkSnapshot before, NetworkSnapshot? after)
    {
        var snapshots = after is null ? new[] { before } : new[] { before, after };
        var incomplete = snapshots.Where(s => s.Status != Availability.Available || !string.IsNullOrWhiteSpace(s.Limitation)).ToArray();
        if (incomplete.Length == 0) return;
        findings.Add(Inconclusive("network.coverage", "Leitura de rede incompleta",
            "Parte dos dados de rede não foi obtida. Os demais achados descrevem apenas os campos que foram realmente lidos.",
            incomplete.Select(s => new Evidence($"Estado {s.Status}. {s.Limitation}", "Descoberta das interfaces de rede",
                Timestamp: s.CapturedAt)).ToArray(),
            incomplete.Any(s => s.Status == Availability.PermissionRequired)
                ? "Abra Rede → Ler conexão com a permissão indicada e repita a análise."
                : "Abra Rede → Ler conexão e repita a análise após confirmar que o adaptador está disponível."));
    }

    private static void AddErrors(List<Finding> findings, NetworkAdapter selected, NetworkSnapshot before, NetworkSnapshot? after)
    {
        var first = before.Adapters.FirstOrDefault(a => a.InterfaceGuid == selected.InterfaceGuid);
        var last = after?.Adapters.FirstOrDefault(a => a.InterfaceGuid == selected.InterfaceGuid);
        var evidence = new List<Evidence>();
        if (first is not null) AddCounterEvidence(evidence, first, before.CapturedAt, "Início");
        if (last is not null && after is not null) AddCounterEvidence(evidence, last, after.CapturedAt, "Fim");
        string? missing = after is null ? "Há apenas uma leitura. Contadores acumulados não mostram quando ocorreram os erros."
            : before.Status != Availability.Available || after.Status != Availability.Available ? "Uma das leituras das interfaces não foi concluída."
            : after.CapturedAt <= before.CapturedAt ? "As leituras não formam uma janela temporal positiva."
            : first is null || last is null ? "O mesmo adaptador não está presente nas duas leituras."
            : !IsUp(first) || !IsUp(last) ? "O adaptador não estava ativo nas duas leituras."
            : first.Source != last.Source || first.Type != last.Type || first.Description != last.Description
                ? "A identificação ou a fonte do adaptador mudou entre as leituras."
            : CounterReset(first, last) ? "Um contador diminuiu: pode ter ocorrido reinício da interface, troca de driver ou reinicialização dos contadores."
            : null;
        if (missing is not null)
        {
            findings.Add(Inconclusive(Id(selected, "errors"), "Erros da rede: comparação inconclusiva", missing,
                evidence, "Repita a análise com a mesma conexão ativa, sem reiniciar o adaptador, enquanto usa a rede."));
            return;
        }

        var received = Delta(first!.ReceivedErrors, last!.ReceivedErrors);
        var sent = Delta(first.SentErrors, last.SentErrors);
        var seconds = (after!.CapturedAt - before.CapturedAt).TotalSeconds;
        var window = seconds.ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) + " s";
        if (received is > 0 || sent is > 0)
        {
            var changes = new List<string>();
            if (received is { } rx) changes.Add($"recepção: +{rx}");
            if (sent is { } tx) changes.Add($"envio: +{tx}");
            findings.Add(new(Id(selected, "errors"), "Rede", "Novos erros registrados no adaptador",
                $"O contador cresceu durante {window}: {string.Join("; ", changes)}. Isso confirma novos registros da interface, mas não identifica a causa nem demonstra perda no jogo ou defeito do driver.",
                FindingCategory.ObservedProblem, Severity.Warning, Confidence.High, evidence,
                "Repita durante o sintoma. Confira cabo, porta e estado do link; depois teste gateway e destino antes de considerar alterações.",
                "Compare novamente as diferenças dos contadores na mesma conexão e carga.", "Nenhuma configuração foi alterada.",
                Context: "Diferença entre duas leituras de contadores cumulativos", ExpectedImpact: "Diagnóstico; benefício não quantificado"));
        }
        else if (received is not null && sent is not null)
            findings.Add(Observation(selected, "errors", "Sem novos erros na janela",
                $"Os contadores de recepção e envio não cresceram entre as duas leituras, separadas por {window}. Os totais anteriores não foram tratados como falha atual. Isso não exclui perdas fora do adaptador nem problemas fora desta janela.",
                evidence, "Se o sintoma continuar, repita enquanto usa a rede e teste o destino afetado."));

        if (received is null || sent is null)
            findings.Add(Inconclusive(Id(selected, "errors.coverage"), "Contadores de erros incompletos",
                $"Não foi possível comparar {(received is null && sent is null ? "recepção e envio" : received is null ? "recepção" : "envio")}. Um valor ausente ou negativo não foi convertido em zero.",
                evidence, "Abra Rede → Ler conexão e repita. Se o driver continuar sem expor o contador, use um teste de destino como evidência adicional."));
    }

    private static void AddDetails(List<Finding> findings, NetworkAdapter adapter, AdapterDetail? detail)
    {
        if (detail is null || detail.Status != Availability.Available)
        {
            var permission = detail?.Status == Availability.PermissionRequired;
            findings.Add(Inconclusive(Id(adapter, "details"), permission ? "Detalhes da rede exigem permissão" : "Detalhes do adaptador indisponíveis",
                "Driver, RSS e propriedades avançadas não foram lidos. A ausência desses dados não indica que os recursos estejam desligados.",
                detail is null ? [] : [new($"Estado {detail.Status}. {string.Join("; ", detail.Limitations)}", detail.Source)],
                permission ? "Abra Rede → Ler conexão com a permissão indicada para consultar os detalhes."
                    : "Abra Rede → Ler conexão para consultar o driver e as propriedades deste adaptador."));
            return;
        }

        if (!string.IsNullOrWhiteSpace(detail.DriverVersion))
            findings.Add(Observation(adapter, "driver", "Driver identificado",
                $"Versão {detail.DriverVersion}. " + (string.IsNullOrWhiteSpace(detail.DriverDate) ? "" : $"Data informada: {detail.DriverDate}. ") +
                "Versão e data identificam o driver; isoladamente, não demonstram defeito nem necessidade de atualização.",
                [new($"{detail.Description} · versão {detail.DriverVersion} · {detail.DriverInformation}", detail.Source)],
                "Se houver falhas repetidas, compare esta versão com a documentação do fabricante para o seu modelo."));
        else
            findings.Add(Inconclusive(Id(adapter, "driver"), "Versão do driver indisponível",
                "O Windows não retornou a versão do driver nesta consulta.",
                [new("DriverVersion ausente", detail.Source)],
                "Abra Rede → Ler conexão novamente ou consulte a versão no Gerenciador de Dispositivos."));

        var propertyEvidence = new List<Evidence>();
        var missing = new List<string>();
        AddProperty("RSS", ["*RSS"], "Distribui a recepção entre processadores compatíveis.", detail,
            propertyEvidence, missing, detail.Rss.Where(f => f.Name.Equals("Enabled", StringComparison.OrdinalIgnoreCase)).ToArray());
        AddProperty("EEE", ["*EEE", "EEE", "EEELinkAdvertisement"], "Permite economia de energia no enlace quando os dois lados suportam o recurso.",
            detail, propertyEvidence, missing);
        AddProperty("Moderação de interrupções", ["*InterruptModeration", "InterruptModerationRate"],
            "Agrupa interrupções para reduzir trabalho da CPU; o efeito depende da carga e do driver.", detail, propertyEvidence, missing);
        AddProperty("Filas RSS", ["*NumRssQueues"], "Indica as filas de recepção divulgadas pelo driver; mais filas não garantem menor latência.",
            detail, propertyEvidence, missing,
            detail.Rss.Where(f => f.Name.Equals("NumberOfReceiveQueues", StringComparison.OrdinalIgnoreCase)).ToArray());
        AddProperty("Buffers de recepção", ["*ReceiveBuffers", "ReceiveBuffers"],
            "Reserva buffers para receber pacotes. A quantidade adequada depende da carga e do driver.", detail, propertyEvidence, missing);
        AddProperty("Buffers de envio", ["*TransmitBuffers", "TransmitBuffers"],
            "Reserva buffers para enviar pacotes. A quantidade adequada depende da carga e do driver.", detail, propertyEvidence, missing);
        AddProperty("Offloads", ["*LsoV2IPv4", "*LsoV2IPv6", "*IPChecksumOffloadIPv4", "*TCPChecksumOffloadIPv4",
                "*TCPChecksumOffloadIPv6", "*UDPChecksumOffloadIPv4", "*UDPChecksumOffloadIPv6"],
            "Transfere segmentação ou verificação de pacotes para o adaptador, conforme a propriedade identificada.",
            detail, propertyEvidence, missing);
        AddProperty("Controle de fluxo", ["*FlowControl"],
            "Configura quadros PAUSE Ethernet para controlar fluxo entre os dois lados do enlace.", detail, propertyEvidence, missing);
        AddProperty("Gerenciamento de energia", ["*WakeOnMagicPacket", "*WakeOnPattern", "*PMARPOffload", "*PMNSOffload"],
            "Descreve funções de economia de energia e ativação do adaptador. Suporte e efeito variam conforme o modelo e o estado do computador.",
            detail, propertyEvidence, missing, detail.PowerManagement);
        if (propertyEvidence.Count > 0)
            findings.Add(Observation(adapter, "properties", "Configurações de rede identificadas",
                "As evidências mostram o estado divulgado pelo driver e a função de cada recurso. Estar ligado ou desligado não comprova um problema; não há recomendação automática de mudança.",
                propertyEvidence, "Preserve os valores atuais. Se houver evidência repetida de falha, avalie uma alteração por vez com comparação antes e depois."));
        if (missing.Count > 0 || detail.Limitations.Count > 0)
            findings.Add(Inconclusive(Id(adapter, "properties.coverage"), "Parte dos detalhes não foi informada",
                (missing.Count == 0 ? "" : $"Sem estado verificável para: {string.Join(", ", missing)}. ") +
                "O recurso pode não ser exposto pelo driver ou a consulta pode exigir permissão; isso não significa que esteja desligado.",
                detail.Limitations.Select(l => new Evidence(l, detail.Source)).ToArray(),
                "Abra Rede → Ler conexão e consulte a limitação exibida. Confira o suporte do modelo na documentação do fabricante."));
    }

    private static void AddProperty(string label, string[] keywords, string function, AdapterDetail detail,
        List<Evidence> evidence, List<string> missing, IReadOnlyList<HardwareFact>? additional = null)
    {
        var properties = detail.AdvancedProperties.Where(p => keywords.Contains(p.RegistryKeyword, StringComparer.OrdinalIgnoreCase)).ToArray();
        var read = false;
        foreach (var property in properties)
        {
            var value = !string.IsNullOrWhiteSpace(property.DisplayValue) ? property.DisplayValue
                : property.RegistryValues.Count > 0 && property.RegistryValues.All(v => !string.IsNullOrWhiteSpace(v))
                    ? "valor técnico " + string.Join(", ", property.RegistryValues) : null;
            if (value is null) continue;
            evidence.Add(new($"{label}: {value}. {function} Identificador: {property.RegistryKeyword}.", detail.Source));
            read = true;
        }
        foreach (var fact in additional ?? [])
        {
            if (fact.Status != Availability.Available || string.IsNullOrWhiteSpace(fact.Value)) continue;
            evidence.Add(new($"{label} · {fact.Name}: {fact.Value}. {function}", fact.Source));
            read = true;
        }
        if (!read) missing.Add(label);
    }

    private static void AddCounterEvidence(List<Evidence> evidence, NetworkAdapter adapter, DateTimeOffset at, string stage)
    {
        evidence.Add(new($"{stage} · erros de recepção" + (adapter.ReceivedErrors is null ? " indisponíveis" : " acumulados"),
            adapter.Source, $"network.{adapter.InterfaceGuid:N}.received.errors", adapter.ReceivedErrors, "erros", at));
        evidence.Add(new($"{stage} · erros de envio" + (adapter.SentErrors is null ? " indisponíveis" : " acumulados"),
            adapter.Source, $"network.{adapter.InterfaceGuid:N}.sent.errors", adapter.SentErrors, "erros", at));
        if (adapter.ReceivedBytes is not null)
            evidence.Add(new($"{stage} · bytes recebidos acumulados", adapter.Source,
                $"network.{adapter.InterfaceGuid:N}.received.bytes", adapter.ReceivedBytes, "bytes", at));
        if (adapter.SentBytes is not null)
            evidence.Add(new($"{stage} · bytes enviados acumulados", adapter.Source,
                $"network.{adapter.InterfaceGuid:N}.sent.bytes", adapter.SentBytes, "bytes", at));
    }

    private static Finding Observation(NetworkAdapter adapter, string suffix, string title, string explanation,
        IReadOnlyList<Evidence> evidence, string recommendation) => new(Id(adapter, suffix), "Rede", title, explanation,
            FindingCategory.NoAnomaly, Severity.Information, Confidence.High, evidence, recommendation,
            "Repita a leitura na mesma interface durante o sintoma.", "Nenhuma configuração foi alterada.",
            Context: "Leitura do estado informado pelo Windows/driver", ExpectedImpact: "Identificação e contexto; benefício não quantificado");
    private static Finding Inconclusive(string id, string title, string explanation, IReadOnlyList<Evidence> evidence,
        string recommendation) => new(id, "Rede", title, explanation, FindingCategory.InsufficientData,
            Severity.Information, Confidence.Low, evidence, recommendation,
            "Obtenha a fonte ausente e repita a análise na mesma conexão.", "Nenhuma configuração foi alterada.");
    private static string Id(NetworkAdapter adapter, string suffix) => $"network.{adapter.InterfaceGuid:N}.{suffix}";
    private static bool IsUp(NetworkAdapter adapter) => adapter.Status.Equals("Up", StringComparison.OrdinalIgnoreCase);
    private static bool IsLoopback(NetworkAdapter adapter) => adapter.Type.Equals("Loopback", StringComparison.OrdinalIgnoreCase);
    private static bool IsEthernet(NetworkAdapter adapter) => adapter.Type is "Ethernet" or "Ethernet3Megabit" or "FastEthernetT" or "FastEthernetFx" or "GigabitEthernet";
    private static bool HasGateway(NetworkAdapter adapter) => adapter.Gateways.Any(g => IPAddress.TryParse(g, out var ip)
        && !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any) && !IPAddress.IsLoopback(ip));
    private static bool CounterReset(NetworkAdapter before, NetworkAdapter after) =>
        Decreased(before.ReceivedErrors, after.ReceivedErrors) || Decreased(before.SentErrors, after.SentErrors)
        || Decreased(before.ReceivedBytes, after.ReceivedBytes) || Decreased(before.SentBytes, after.SentBytes);
    private static bool Decreased(long? before, long? after) => before is >= 0 && after is >= 0 && after < before;
    private static long? Delta(long? before, long? after) => before is >= 0 && after >= before ? after.Value - before.Value : null;
    private static string FormatSpeed(long bitsPerSecond) => bitsPerSecond >= 1_000_000_000
        ? (bitsPerSecond / 1_000_000_000d).ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) + " Gbit/s"
        : (bitsPerSecond / 1_000_000d).ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) + " Mbit/s";
}
