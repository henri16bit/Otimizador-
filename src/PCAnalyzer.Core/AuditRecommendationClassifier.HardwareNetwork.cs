using System.Globalization;

namespace PCAnalyzer.Core;

public static partial class AuditRecommendationClassifier
{
    private static AuditRecommendation? HardwareNetwork(AuditAssessment a)
    {
        var o = a.Observation;
        if (o.RuleId.StartsWith("network.", StringComparison.Ordinal)) return Network(o);
        if (o.RuleId.StartsWith("devices.", StringComparison.Ordinal)) return Device(o);
        if (o.RuleId == "memory.modules")
        {
            var rate = Fact(o, "ConfiguredTransferRateMTs");
            if (!uint.TryParse(rate, NumberStyles.None, CultureInfo.InvariantCulture, out var transfer) || transfer == 0)
                return Unknown("A taxa configurada do módulo não foi obtida; a identidade ou capacidade não substitui a leitura de MT/s.");
            var cpu = Fact(o, "CpuName") ?? Fact(o, "CPUName") ?? "";
            var board = (Fact(o, "BaseboardProduct") ?? "") + " " + (Fact(o, "Chipset") ?? "");
            var matched = cpu.Contains("10700K", StringComparison.OrdinalIgnoreCase) && board.Contains("B460", StringComparison.OrdinalIgnoreCase);
            if (matched && transfer == 2933)
                return Keep("2933 MT/s é compatível com o limite conhecido de i7-10700K e B460.",
                    "CPU e placa identificadas correspondem à plataforma que limita DDR4 a 2933 MT/s nessa combinação. A taxa nominal do módulo não demonstra frequência suportada pela plataforma.",
                    "Não há problema de frequência demonstrado; acesso à memória também depende de caches, timings e carga.",
                    "Confira especificações da placa/CPU. Para investigar acesso à memória, use o teste manual sob condições equivalentes.",
                    test: "memory-access", confidence: Confidence.High, functional: AuditFunctionalKind.DocumentedReference) with { RecommendedValue = "Manter dentro da plataforma" };
            return Keep("A taxa configurada deve ser comparada à especificação da CPU e da placa.",
                "O SMBIOS informa configuração do módulo, sem verificar XMP, timings, canais ou limite da plataforma. Diferença para a taxa nominal não demonstra defeito.",
                "Acesso real à memória depende de CPU, caches, controlador e timings; MT/s isolado não determina latência.",
                "Confirme a especificação da combinação CPU/placa e repita o teste manual de memória se houver sintoma.", test: "memory-access");
        }
        if (o.RuleId.StartsWith("hardware.cpu.", StringComparison.Ordinal))
            return Info("A topologia descreve os processadores disponíveis; não é uma recomendação de afinidade.", "Observe distribuição de carga por thread antes de restringir processadores.");
        if (o.RuleId == "storage.physical")
        {
            var health = Fact(o, "HealthStatus");
            if (health is "1" or "2")
                return Investigate("Verificar o alerta de saúde da unidade", "O provedor de armazenamento reportou um estado de saúde que requer atenção.",
                    "O alerta veio da unidade identificada pelo provedor. Ele merece confirmação com a ferramenta do fabricante; não prova que o SSD causou uma travada no jogo.",
                    "Investigar a saúde pode proteger dados e disponibilidade; velocidade ou ganho de FPS não foram demonstrados.",
                    "Preserve seus dados e confirme o diagnóstico na ferramenta oficial do fabricante antes de executar carga no disco.",
                    functional: AuditFunctionalKind.DocumentedReference, priority: 95);
            return Info("Identidade e estado do SSD não demonstram seu desempenho durante o jogo.", "Consulte saúde e sensores da unidade correta; compare leituras de arquivos apenas quando houver objetivo específico.");
        }
        if (o.RuleId == "storage.reliability")
            return Info("Este contador ou sensor descreve a unidade; uma leitura isolada não demonstra regressão.", "Compare a evolução e os limites publicados pelo fabricante da unidade; temperatura e contadores não têm limite universal neste catálogo.");
        if (o.RuleId.StartsWith("storage.", StringComparison.Ordinal))
            return Info("A associação de volumes identifica onde estão os dados; não mede o custo de acesso.", "Investigue a unidade que contém o arquivo ou jogo realmente utilizado, sem somar discos distintos.");
        return null;
    }

    private static AuditRecommendation Network(AuditObservation o)
    {
        if (o.RuleId == "network.adapter") return Info("Identidade, enlace e driver descrevem a conexão selecionada.", "Confirme adaptador, rota e destino antes de interpretar RTT ou comparar propriedades.");
        if (o.RuleId == "network.statistics") return Info("Contadores acumulados não indicam quando ocorreram erros ou descartes.", "Colete diferenças no mesmo adaptador e intervalo da carga; um contador antigo não comprova perda durante o jogo.");
        if (o.RuleId == "network.advanced")
        {
            var key = (Fact(o, "RegistryKeyword") ?? o.Name).Trim();
            if (key.Equals("*IfType", StringComparison.OrdinalIgnoreCase) || key.Equals("IfType", StringComparison.OrdinalIgnoreCase))
                return Info(Number(o) == 6 ? "O tipo 6 identifica uma interface Ethernet; não é um ajuste de desempenho." :
                    "O tipo de interface descreve o adaptador; não é uma otimização.", "Use esse campo para identificar a interface, sem escolher outro tipo para reduzir latência.");
            if (new[] { "*MediaType", "*PhysicalMediaType", "*NdisDeviceType", "DriverVersion", "BusType" }.Contains(key, StringComparer.OrdinalIgnoreCase))
                return Info("Esta propriedade descreve a interface ou o driver; não configura uma otimização.", "Use a identidade para selecionar a documentação correta do adaptador.");
            var known = key.TrimStart('*').ToLowerInvariant();
            var advertised = Fact(o, "AdvertisedValues");
            var mutable = advertised is { Length: > 0 } && advertised.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1;
            if (known is "interruptmoderation" or "interruptmoderationrate" || known.Contains("interruptmoderation", StringComparison.Ordinal))
            {
                if (Invalid(o)) return Contract(o);
                if (!mutable) return Unknown("O driver não anunciou alternativas suficientes para recomendar moderação de interrupções.");
                return Keep("A moderação troca frequência de interrupções por trabalho de CPU e processamento de pacotes.",
                    "O valor atual foi lido e o driver anunciou alternativas. Sem RTT/jitter, utilização de CPU e DPC/ISR sob tráfego representativo, não há evidência para recomendar desativá-lo.",
                    "Menos agrupamento pode antecipar pacotes e aumentar interrupções; o resultado depende da carga.",
                    "Se houver jitter reproduzível, compare RTT, perdas e CPU com o mesmo destino e tráfego; teste apenas alternativas anunciadas pelo driver.",
                    risks: "Menor moderação pode aumentar carga de CPU e não melhorar a latência do jogo.", test: "ping", functional: AuditFunctionalKind.TestDependent);
            }
            if (known is "rss" or "numrssqueues") return Rss();
            if (known.Contains("jumbo", StringComparison.Ordinal)) return Keep("MTU maior exige compatibilidade de todo o caminho; não reduz RTT automaticamente.",
                "O tamanho de quadro anunciado pelo driver não confirma suporte da rede inteira nem tráfego do jogo que se beneficie dele.",
                "Pode reduzir overhead em transferências compatíveis; incompatibilidade pode causar perdas.",
                "Confirme MTU de cada trecho e aplicação antes de comparar um valor suportado.", risks: "Quadros incompatíveis podem interromper comunicação.");
            if (known.Contains("energy", StringComparison.Ordinal) || known.Contains("eee", StringComparison.Ordinal) || known.Contains("green", StringComparison.Ordinal))
                return Keep("A preferência de economia não comprova transições de energia durante a partida.",
                    "Estado anunciado pelo driver e transições efetivas do enlace são evidências diferentes. Não foi observada uma interrupção atribuída à economia de energia.",
                    "Pode alterar consumo e tempo de retomada; efeito em jitter não medido.", "Compare eventos de enlace e RTT sob o mesmo tráfego antes de desativar economia.", test: "ping");
            if (known.Contains("checksum", StringComparison.Ordinal) || known.Contains("lso", StringComparison.Ordinal) || known.Contains("rsc", StringComparison.Ordinal))
                return Keep("O offload trata trabalho de rede; o efeito depende do protocolo e do driver.",
                    "Offloads podem reduzir trabalho da CPU, mas a propriedade isolada não demonstra uso efetivo ou uma falha. Offload TCP não descreve automaticamente UDP do jogo.",
                    "Pode alterar CPU e vazão quando aplicável; não há ganho de RTT demonstrado.", "Identifique protocolo e sintoma antes de comparar offload no mesmo adaptador e carga.", test: "ping");
            return Unknown("O driver expõe esta propriedade, mas o catálogo não possui uma regra técnica específica para recomendá-la.");
        }
        if (o.RuleId == "network.rss") return Rss();
        if (o.RuleId == "network.power") return Keep("A política de energia não demonstra suspensão indevida da conexão.",
            "O driver anunciou uma configuração de suspensão, wake ou offload. Wake não mede latência de pacotes durante o uso ativo.",
            "Pode alterar consumo e retomada; ganho de latência não demonstrado.", "Investigue quedas e transições de enlace durante o sintoma antes de comparar políticas.", test: "ping");
        if (o.RuleId == "network.tcp") return Keep("O perfil TCP não descreve automaticamente o tráfego UDP do jogo.",
            "A preferência pertence ao perfil TCP identificado. Não foi comprovada a seleção desse perfil pela conexão do jogo nem um gargalo causado pelo valor.",
            "Pode afetar transporte TCP, vazão ou controle de congestionamento; não mede input lag.", "Identifique a conexão e o protocolo antes de comparar a mesma carga e destino.", test: "ping");
        return Unknown();
    }
    private static AuditRecommendation Rss() => Keep("RSS distribui recepção; compartilhar CPUs não demonstra conflito.",
        "Filas, processadores anunciados e tráfego efetivo são informações distintas. Não há evidência para desligar RSS ou fixar núcleos pelo snapshot.",
        "Pode distribuir trabalho de rede e reduzir saturação; nenhuma diferença de RTT foi medida.",
        "Observe CPU, DPC/ISR e tráfego no adaptador durante o sintoma antes de comparar filas ou afinidade.", test: "dpc");

    private static AuditRecommendation Device(AuditObservation o)
    {
        if (Invalid(o) && o.RuleId is "devices.interrupt.msi" or "devices.interrupt.capability" or
            "devices.interrupt.affinity" or "devices.interrupt.policy") return Contract(o);
        if (o.RuleId == "devices.identity")
        {
            var code = Fact(o, "pnpProblemCode");
            if (uint.TryParse(code, out var value) && value is not 0 and not 22)
                return Investigate("Verificar o erro informado pelo Windows", "O dispositivo possui código de problema PnP que merece confirmação.",
                    "O código pertence ao dispositivo identificado. Ele não determina sozinho impacto em FPS; confirme se o dispositivo é necessário ao cenário.",
                    "Restabelecer disponibilidade pode resolver uma falha funcional; ganho de latência não medido.",
                    "Confira o código e o driver no Gerenciador de Dispositivos, sem tratar dispositivos distintos como um mesmo controlador.",
                    functional: AuditFunctionalKind.DocumentedReference, priority: 85);
            return Info("A identidade PnP não comprova o modo de interrupção efetivo ou desempenho.", "Use o dispositivo e o driver identificados para relacionar uma captura; ausência de código PnP não certifica latência.");
        }
        if (o.RuleId is "devices.interrupt.msi" or "devices.interrupt.capability")
            return Keep("Preferência e capacidade MSI não confirmam o modo utilizado nesta captura.",
                "MSI/MSI-X dependem de dispositivo, driver e alocação efetiva de recursos. Um valor de Registro não prova ativação nem benefício.",
                "O mecanismo de entrega de interrupções pode mudar; ganho em latência não foi demonstrado.",
                "Relacione o driver a eventos DPC/ISR e aos recursos efetivamente alocados antes de propor uma mudança.",
                risks: "Forçar modo não suportado pode impedir o funcionamento do dispositivo.", test: "dpc");
        if (o.RuleId is "devices.interrupt.affinity" or "devices.interrupt.policy")
            return Keep("A política não demonstra que CPUs compartilhadas estejam em conflito.",
                "Máscara e política configuradas não descrevem toda a distribuição efetiva. Grupos de processadores e decisão do driver precisam ser considerados.",
                "Uma restrição pode concentrar trabalho; não há núcleo ideal universal.",
                "Capture DPC/ISR por CPU lógica durante a carga e confirme a identidade do driver antes de comparar uma política.",
                risks: "Restringir CPUs pode concentrar interrupções e reduzir responsividade.", test: "dpc");
        if (o.RuleId == "devices.usb.polling") return Unknown("Esta origem não mede o polling efetivo dos dispositivos USB.");
        return Info("A alocação ou topologia observada descreve o dispositivo, sem demonstrar um conflito.",
            "Correlacione driver, eventos por CPU e contexto de carga; compartilhar IRQ ou processador não prova perda de desempenho.");
    }
}
