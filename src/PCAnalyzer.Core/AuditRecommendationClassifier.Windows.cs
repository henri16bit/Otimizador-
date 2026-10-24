namespace PCAnalyzer.Core;

public static partial class AuditRecommendationClassifier
{
    private static AuditRecommendation? Windows(AuditAssessment a)
    {
        var o = a.Observation;
        if (!o.RuleId.StartsWith("windows.", StringComparison.Ordinal)) return null;
        if (o.RuleId == "windows.mmcss.priority" && Fact(o, "registryType") == "DWord" &&
            Fact(o, "SchedulingCategory")?.Equals("High", StringComparison.OrdinalIgnoreCase) == true)
            return MmcssPriority(o, Number(o));
        var number = Number(o);
        var policy = o.RuleId switch
        {
            "windows.version" => Info("A versão identifica quais contratos do Windows podem ser aplicáveis.", "Confirme edição e build antes de seguir instruções específicas de outra versão."),
            "windows.scheduler.separation" => number is null ? Unknown("O valor de Win32PrioritySeparation não pôde ser interpretado como DWORD.") :
                Keep("Outra política de agendamento não demonstrou menor latência neste computador.",
                    "Win32PrioritySeparation controla aspectos de quantum e prioridade do primeiro plano. O valor lido não descreve a prioridade efetiva das threads; não existe superioridade universal de 0x26 sobre 0x2A.",
                    "A distribuição de tempo de CPU pode mudar; benefício em frametime não foi medido.",
                    "Se houver sintoma reproduzível, compare frametimes e atividade das threads sob a mesma carga antes de mudar a política.", test: "frames"),
            "windows.mmcss.responsiveness" => Responsiveness(o, number),
            "windows.mmcss.priority" => MmcssPriority(o, number),
            "windows.mmcss.category" => Keep("A categoria só alcança threads que usam esta tarefa MMCSS.",
                "High, Medium e Low definem classes do MMCSS. A existência do perfil não demonstra que as threads do jogo aderiram a ele.",
                "Pode afetar threads participantes; não altera toda a CPU ou GPU.", "Identifique a adesão das threads ao MMCSS antes de comparar outra categoria."),
            "windows.mmcss.affinity" => Keep(number is 0 or uint.MaxValue ? "Este valor não impõe afinidade segundo o contrato MMCSS." : "Afinidade da tarefa não comprova restrição das threads do jogo.",
                "O MMCSS interpreta a máscara para threads associadas à tarefa. Valores 0 e 0xFFFFFFFF não impõem afinidade; a máscara não determina onde DPC/ISR executam.",
                "Uma restrição pode reduzir opções do agendador; não há benefício medido aqui.", "Verifique threads participantes e utilização dos processadores antes de comparar uma máscara.", risks: "Fixar poucos processadores pode limitar paralelismo."),
            "windows.mmcss.clock" => Keep("Clock Rate é uma dica; não garante resolução de timer ou latência de entrada.",
                "A garantia antiga de granularidade foi removida desde Windows 7. Este campo não mede a resolução atualmente solicitada pelo jogo.",
                "Nenhuma redução de input lag pode ser atribuída a esta leitura.", "Meça a carga e os timers do processo quando houver um sintoma; não use Clock Rate como benchmark."),
            "windows.mmcss.background" => Keep("A política de segundo plano depende do cliente MMCSS que usa a tarefa.",
                "Background Only e BackgroundPriority não demonstram que o jogo está em segundo plano ou que foi priorizado pelo MMCSS.",
                "O efeito possível está restrito às threads participantes.", "Confirme o cliente e o estado das threads antes de alterar a política."),
            "windows.mmcss.throttling" => Decision(AuditFunctionalKind.NoDocumentedEffect, AuditRecommendationKind.NoUniversalValue,
                "Sem recomendação fundamentada", "A leitura de NetworkThrottlingIndex não comprova limitação ativa de rede.",
                "O catálogo não confirmou um contrato público aplicável deste valor no Windows auditado. Nem presença nem 0xFFFFFFFF comprovam ganho no tráfego do jogo.",
                "Benefício de FPS, RTT ou jitter não demonstrado.", "Investigue RTT, perdas e carga de CPU observados antes de associar sintomas a este DWORD.", confidence: null),
            "windows.ifeo.cpu" => Ifeo(o, "classe de prioridade de CPU"),
            "windows.ifeo.io" => Ifeo(o, "prioridade de I/O"),
            "windows.ifeo.page" => Ifeo(o, "prioridade de páginas"),
            "windows.process.scheduling" => ProcessScheduling(o),
            "windows.services.state" => Info("Estado e inicialização de um serviço não medem seu consumo.", "Observe CPU, I/O e dependências do serviço durante o sintoma; não o desative só porque está ativo."),
            "windows.startup.entries" => Info("A entrada de inicialização não comprova execução ou consumo nesta sessão.", "Confirme o processo iniciado e meça seu custo no cenário observado antes de mudar a inicialização."),
            "windows.tasks.entries" => Info("Uma tarefa registrada não demonstra execução ou interferência no jogo.", "Compare o horário de execução da tarefa com o sintoma e com o processo realmente ativo."),
            "windows.power.policy" => Power(o),
            "windows.power.throttling" => Keep("O DWORD não confirma o estado de Power Throttling do processo.",
                "A preferência persistente é distinta do estado efetivo PROCESS_POWER_THROTTLING_STATE. Nenhum processo foi demonstrado limitado por esta leitura.",
                "Consumo e desempenho podem depender de carga, firmware e política efetiva.", "Verifique o estado do processo e clocks sob carga antes de comparar a preferência."),
            "windows.timers.qpc" => Info("A frequência QPC não identifica HPET/TSC nem mede input lag.", "Use QPC como base temporal; investigue temporização do processo quando houver evidência de atraso."),
            "windows.timers.capabilities" => Info("O intervalo suportado não é a resolução atualmente usada pelo jogo.", "Uma investigação de timers precisa identificar as solicitações e a versão do Windows; não escolha o mínimo apenas pela capacidade."),
            "windows.timers.current" => Unknown("O estado efetivo de timers por processo não foi estabelecido por esta origem."),
            "windows.mouse.effective" => Keep("O parâmetro do ponteiro não determina o comportamento de jogos com Raw Input.",
                "A API leu velocidade ou aceleração do ponteiro do Windows. Um jogo pode consumir entrada bruta e ignorar essa transformação.",
                "Pode mudar o movimento do cursor; não é medição de polling ou clique para fóton.", "Confira o modo de entrada do jogo e sua sensação de movimento; use medição apropriada para latência física."),
            "windows.mouse.settings" => Keep("A preferência persistente de mouse pode não afetar o Raw Input do jogo.",
                "A leitura descreve o ajuste do ponteiro. O valor efetivo da API e a forma de consumo pelo jogo são evidências separadas.",
                "Velocidade/aceleração podem mudar no desktop, sem ganho de latência demonstrado.", "Compare com os parâmetros atuais da API e confirme se o jogo usa Raw Input."),
            "windows.mouse.curves" => Info("Os bytes da curva descrevem aceleração; não são frequência USB.", "Confira a política do ponteiro e o modo de entrada do aplicativo antes de interpretar a curva."),
            "windows.input.queue" => Keep("Capacidade de fila não demonstra ocupação, perdas ou atraso do dispositivo.",
                "A leitura indica configuração de buffering do driver. Reduzir o tamanho não prova menor latência e pode reduzir tolerância a rajadas.",
                "O compromisso é capacidade de buffering; não há medição de polling nesta leitura.", "Investigue perdas ou atrasos reproduzíveis do dispositivo antes de comparar capacidades.", risks: "Filas menores podem perder eventos em condições de sobrecarga."),
            "windows.memory.policy" => Keep("A política persistente não demonstra pressão de RAM ou paginação problemática.",
                "Commit, RAM disponível e I/O durante a carga são necessários para avaliar esta política. Uma chave personalizada não é, por si só, um defeito.",
                "Disponibilidade de memória e estabilidade dependem da carga; ganho não demonstrado.", "Observe commit, RAM disponível e paginação sob o mesmo jogo antes de considerar mudanças.", risks: "Restringir paginação pode provocar falha de alocação."),
            "windows.gaming.gamemode" => Keep("A preferência não comprova os recursos de Modo de Jogo efetivos no processo.",
                "O Registro preserva a preferência do usuário. A ativação efetiva e seu benefício dependem do Windows e da carga concorrente.",
                "Pode alterar a gestão de recursos quando aplicável; FPS não foi comparado.", "Compare sessões do mesmo jogo sob carga equivalente, verificando o estado efetivo."),
            "windows.gaming.hags" => Keep("HwSchMode não confirma suporte nem estado ativo do agendador da GPU.",
                "São necessários suporte WDDM, driver e estado efetivo; o DWORD isolado não estabelece se o agendamento acelerado está em uso.",
                "Frametime e estabilidade podem variar por driver; não existe ganho universal demonstrado.", "Confirme suporte/ativação no Windows e compare capturas do mesmo jogo e driver.", test: "frames"),
            "windows.gaming.fullscreen" => Keep("A flag persistente não identifica o modo de apresentação usado pelo jogo.",
                "O jogo pode usar caminhos de apresentação diferentes. É preciso observar o executável e os frames antes de atribuir efeito às flags.",
                "Pode afetar apresentação e compatibilidade; benefício não foi medido.", "Capture frametimes e modo de apresentação do executável correto.", test: "frames"),
            "windows.gaming.graphics" => Keep("A preferência de GPU não comprova qual adaptador renderizou o jogo.",
                "O ajuste persistente identifica uma preferência por aplicativo, distinta do adaptador efetivamente utilizado.",
                "Uma GPU diferente pode mudar desempenho; essa leitura não comprova troca ou gargalo.", "Confirme a GPU ativa do processo e a carga do adaptador antes de recomendar uma preferência."),
            "windows.graphics.overlay" => Unknown("OverlayTestMode não confirma estado de MPO nem benefício no modo de apresentação observado."),
            _ when o.RuleId.StartsWith("windows.bcd.", StringComparison.Ordinal) => Keep("Esta opção de inicialização é voltada a diagnóstico, não a otimização universal.",
                "A leitura não confirma a fonte de temporização efetiva, reinicialização após a mudança ou ganho no jogo.",
                "Pode modificar temporização e compatibilidade; não há redução de input lag demonstrada.", "Registre o valor e investigue um problema reproduzível com as orientações oficiais antes de mudar opções de boot.", risks: "Alterações externas no BCD podem prejudicar inicialização e temporização."),
            _ => Unknown()
        };
        var numericContract = o.RuleId is "windows.scheduler.separation" or "windows.mmcss.responsiveness" or "windows.mmcss.priority";
        return Invalid(o) && (numericContract || policy.FunctionalKind is not (AuditFunctionalKind.Incomplete or
            AuditFunctionalKind.Informational or AuditFunctionalKind.NoDocumentedEffect)) ? Contract(o) : policy;
    }

    private static AuditRecommendation Responsiveness(AuditObservation o, uint? value)
    {
        if (value is null) return Unknown("SystemResponsiveness não foi interpretado como porcentagem DWORD.");
        if (value == 100) return Investigate("Conferir se desativar MMCSS é intencional",
            "SystemResponsiveness em 100 desativa o MMCSS segundo o contrato documentado.",
            "O valor armazenado tem um efeito documentado específico. Sem identificar os clientes MMCSS ativos, isso ainda não demonstra perda de desempenho do jogo.",
            "Restabelecer uso do MMCSS pode importar para clientes participantes; não há ganho medido.",
            "Identifique aplicativos e threads que dependem de MMCSS e confirme a intenção da política antes de qualquer alteração externa.",
            functional: AuditFunctionalKind.DocumentedReference, priority: 75);
        var effective = value < 10 || value > 100 ? 20 : value.Value / 10 * 10;
        return Keep($"O contrato MMCSS interpreta este valor como reserva de {effective}%.",
            $"SystemResponsiveness é arredondado para baixo em múltiplos de 10; valores abaixo de 10 ou acima de 100 são tratados como 20. O efeito documentado aqui é {effective}%, sem demonstrar adesão das threads do jogo.",
            "A reserva alcança o mecanismo MMCSS; não representa porcentagem de CPU perdida pelo jogo.",
            "Verifique clientes MMCSS e carga concorrente antes de comparar uma reserva diferente.",
            functional: AuditFunctionalKind.DocumentedReference);
    }
    private static AuditRecommendation MmcssPriority(AuditObservation o, uint? number)
    {
        var high = Fact(o, "registryType") == "DWord" &&
            Fact(o, "SchedulingCategory")?.Equals("High", StringComparison.OrdinalIgnoreCase) == true;
        if (!high && number is (< 1 or > 8)) return Contract(o);
        if (number is null) return Unknown("A prioridade MMCSS não foi interpretada no intervalo documentado de 1 a 8.");
        return Keep(high ? "Na categoria High, a prioridade MMCSS é interpretada como 2." : "A prioridade só se aplica às threads participantes desta tarefa MMCSS.",
            high ? "O contrato de Scheduling Category High interpreta Priority como 2, independentemente do DWORD armazenado. Isso não equivale à classe de prioridade do processo." :
                "O valor está na faixa documentada de 1 a 8. Sem associação das threads ao perfil, não se comprova uma prioridade efetiva no jogo.",
            "Pode alterar escalonamento de threads associadas; benefício em FPS não medido.",
            "Confirme a adesão à tarefa e observe as threads sob carga antes de comparar prioridades.", functional: AuditFunctionalKind.DocumentedReference);
    }
    private static AuditRecommendation Ifeo(AuditObservation o, string target) => Keep(
        "A preferência IFEO não substitui a leitura do estado efetivo do executável.",
        $"Este valor é uma preferência persistente de {target}. A correspondência pelo nome do executável não demonstra identidade por caminho nem que o Windows aplicou a política.",
        "Pode influenciar recursos do processo quando reconhecida; desempenho e estado efetivo não demonstrados pelo DWORD.",
        "Confira executável, PID, instante de criação e estado observado no mesmo processo; não compare com PCAnalyzer ou Explorer como se fossem o jogo.",
        risks: "Prioridades forçadas podem reduzir responsividade e interferir em outros processos.");
    private static AuditRecommendation ProcessScheduling(AuditObservation o)
    {
        var priority = Fact(o, "PriorityClass");
        if (priority?.Equals("RealTime", StringComparison.OrdinalIgnoreCase) == true)
            return Investigate("Rever o uso de prioridade Tempo real",
                "O processo observado usa Tempo real, que pode impedir o trabalho de threads críticas do sistema.",
                "A classe foi lida no processo identificado. Ela merece revisão por risco de responsividade, sem comprovar que causou uma travada específica.",
                "Evitar starvation pode melhorar estabilidade; redução de latência não foi medida.",
                "Confirme o executável e a necessidade técnica dessa classe; investigue travadas sob carga antes de definir uma política externa.",
                risks: "Tempo real pode impedir processamento de entrada, rede e tarefas essenciais.", functional: AuditFunctionalKind.DocumentedReference, priority: 90);
        if (string.IsNullOrWhiteSpace(priority)) return Unknown("A classe efetiva deste processo não foi obtida; não é possível recomendar outra prioridade.");
        return Keep("A classe observada não sustenta elevar a prioridade ou restringir a afinidade.",
            "Prioridade e máscara pertencem ao processo identificado por executável e PID. Compartilhar processadores não demonstra conflito; threads podem ter políticas próprias.",
            "Uma mudança pode redistribuir tempo de CPU, mas também limitar paralelismo; benefício não medido.",
            "Durante o sintoma, compare utilização por thread e frametime do mesmo executável e instante de criação.",
            risks: "Não force Alta/Tempo real nem núcleos específicos sem evidência do cenário.");
    }
    private static AuditRecommendation Power(AuditObservation o)
    {
        if (Fact(o, "ActiveScheme") is not null) return Keep("O plano ativo, sozinho, não comprova limitação de desempenho.",
            "O Windows identificou o plano em uso. Clocks efetivos, consumo, temperatura e limites de firmware precisam ser observados durante a carga.",
            "Outro plano pode aumentar consumo sem melhorar frametime.", "Compare clocks e frametime sob carga equivalente antes de trocar o plano.");
        var label = o.Name.ToLowerInvariant();
        var parking = label.Contains("parking") || label.Contains("estacion");
        return Keep(parking ? "O índice de Core Parking não prova que núcleos ficaram indisponíveis ao jogo." : "O índice de energia não comprova limitação efetiva durante a carga.",
            "O valor pertence ao plano e à alimentação AC/DC indicados na evidência. Firmware e driver podem limitar ou reinterpretar sua aplicação.",
            parking ? "O estacionamento pode afetar consumo e disponibilidade de núcleos; benefício não medido." : "A política pode alterar consumo e frequência; não há perda de FPS demonstrada pela leitura.",
            "Observe frequência efetiva, carga e temperatura no cenário problemático antes de comparar esta política.");
    }
}
