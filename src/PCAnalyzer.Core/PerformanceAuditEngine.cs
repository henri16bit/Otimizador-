using System.Globalization;

namespace PCAnalyzer.Core;

/// <summary>Interprets observations without converting configuration presence into measured benefit.</summary>
public sealed class PerformanceAuditEngine
{
    public static PerformanceAuditReport Evaluate(IReadOnlyList<AuditRule> rules,
        IReadOnlyList<AuditObservation> observations, DateTimeOffset capturedAt)
    {
        var catalog = rules.GroupBy(x => x.Id, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.First(), StringComparer.Ordinal);
        var assessments = new List<AuditAssessment>();
        foreach (var observation in observations.DistinctBy(x => (x.RuleId, x.InstanceId)))
        {
            var rule = catalog.GetValueOrDefault(observation.RuleId) ?? new AuditRule(observation.RuleId,
                "Outras áreas", observation.Name, observation.Source, "Regra catalogada", "Não verificada",
                "Fonte técnica e critério de avaliação", "Não determinado", AuditSupport.NotVerifiable,
                "Observação sem regra técnica registrada.", "Consultar fonte e metodologia antes de interpretar.", []);
            assessments.Add(Interpret(rule, observation));
        }
        var available = assessments.Count(x => x.Observation.Status == Availability.Available);
        var attention = assessments.Count(x => x.Classification is AuditClassification.DemonstratedProblem or AuditClassification.PossibleProblem);
        var limits = observations.Where(x => !string.IsNullOrWhiteSpace(x.Limitation)).Select(x => x.Limitation!)
            .Append("Snapshot somente de leitura. Configuração encontrada, uso efetivo e ganho de desempenho são evidências diferentes.")
            .Distinct(StringComparer.Ordinal).ToArray();
        return new(capturedAt, $"{available} de {assessments.Count} observações disponíveis; {attention} merecem investigação. Nenhuma configuração foi alterada.",
            assessments, limits);
    }

    private static AuditAssessment Interpret(AuditRule rule, AuditObservation observation)
    {
        AuditAssessment Result(AuditClassification classification, string summary, string reason, Confidence confidence = Confidence.Medium) =>
            new(observation, rule, classification, summary, reason, rule.ImpactHypothesis, confidence,
                "Nenhuma alteração é executada. " + rule.Limitations, rule.Validation);
        if (observation.Status != Availability.Available)
            return Result(observation.Status is Availability.Unsupported or Availability.ToolMissing ? AuditClassification.Unsupported : AuditClassification.InsufficientData,
                "Leitura não concluída", observation.Limitation ?? "A fonte não forneceu dados suficientes.", Confidence.Low);
        if (observation.Facts.GetValueOrDefault("presence") == "absent")
            return Result(AuditClassification.InsufficientData, "Sem valor explícito nesta origem",
                "A ausência pode representar o comportamento padrão. Esta leitura não confirma o estado efetivo nem indica um problema.", Confidence.High);
        if (rule.Support is AuditSupport.Ignored)
            return Result(AuditClassification.NoDocumentedEffect, "Campo sem efeito documentado neste contexto", rule.Limitations, Confidence.High);
        // High overrides the numeric task priority. A wrong registry type is still a contract error;
        // an out-of-range DWORD must not imply that the ignored number became the thread priority.
        if (rule.Id == "windows.mmcss.priority" && observation.Facts.GetValueOrDefault("registryType") == "DWord" &&
            observation.Facts.GetValueOrDefault("SchedulingCategory")?.Equals("High", StringComparison.OrdinalIgnoreCase) == true)
            return Result(AuditClassification.AppliedUnproven, "A categoria High usa prioridade de tarefa 2",
                "MMCSS trata Priority como 2 para Scheduling Category = High, independentemente do número armazenado. " +
                "Esta interpretação do contrato não comprova o valor de prioridade atual de uma thread nem sua adesão à tarefa. " +
                observation.Facts.GetValueOrDefault("invalidReason", ""), Confidence.High);
        if (observation.Facts.TryGetValue("invalid", out var invalid) && !string.Equals(invalid, "false", StringComparison.OrdinalIgnoreCase))
            return Result(AuditClassification.PossibleProblem, "Valor fora do contrato documentado",
                observation.Facts.GetValueOrDefault("invalidReason") ?? invalid);

        // These are documented semantics, not a score or universal recommendation.
        if (rule.Id == "windows.mmcss.responsiveness" && Numeric(observation) is { } response)
        {
            if (response == 100)
                return Result(AuditClassification.PossibleProblem, "MMCSS configurado para ser desativado",
                    "SystemResponsiveness = 100 desativa MMCSS segundo a documentação. Isso não demonstra a causa de um problema no jogo; falta observar clientes MMCSS.", Confidence.High);
            var interpreted = response < 10 || response > 100 ? 20 : response / 10 * 10;
            return Result(AuditClassification.AppliedUnproven, $"Reserva para tarefas de baixa prioridade: {interpreted}%",
                $"Valor armazenado: {response}. É a parcela garantida a tarefas de baixa prioridade no mecanismo MMCSS, não uma reserva exclusiva para o jogo. " +
                "A documentação arredonda para múltiplos de 10 e usa 20 para valores abaixo de 10 ou acima de 100. Não foi observada a associação das threads do jogo ao MMCSS.", Confidence.High);
        }
        if (rule.Id == "windows.mmcss.affinity" && Numeric(observation) is 0 or uint.MaxValue)
            return Result(AuditClassification.AppliedUnproven, "Este valor MMCSS não impõe afinidade",
                "A documentação define 0 e 0xFFFFFFFF como ausência de afinidade para a tarefa MMCSS. Isso não exclui restrições por processo, thread ou CPU Sets e não descreve a afinidade de interrupções.", Confidence.High);
        if (rule.Id == "windows.process.scheduling" && observation.Facts.GetValueOrDefault("PriorityClass") == "RealTime")
            return Result(AuditClassification.PossibleProblem, "Processo observado em prioridade de tempo real",
                "O Windows alerta que threads de tempo real podem impedir trabalho importante do sistema. O snapshot confirma a classe do processo; não demonstra contenção ou input lag.", Confidence.High);
        if (rule.Id == "windows.process.scheduling" && observation.Facts.GetValueOrDefault("PriorityClass") == "Normal")
            return Result(AuditClassification.Adequate, "Classe Normal observada; sem alerta de prioridade neste item",
                "A API reportou a classe Normal. Esta verificação não encontrou prioridade de tempo real; não avalia prioridades individuais das threads, afinidade, contenção ou desempenho.", Confidence.High);
        if (rule.Id == "storage.physical")
        {
            var health = observation.Facts.GetValueOrDefault("HealthStatus");
            if (health is "1" or "2")
                return Result(AuditClassification.PossibleProblem, "O provedor Storage reportou um alerta de saúde",
                    $"HealthStatus = {health} ({(health == "1" ? "Warning" : "Unhealthy")}). Investigar o dispositivo e seu diagnóstico do fabricante. Isso não comprova falha física nem a causa de stuttering ou perda de desempenho.", Confidence.High);
            if (health == "0")
                return Result(AuditClassification.Adequate, "O provedor Storage reporta estado saudável",
                    "HealthStatus = 0 (Healthy). Não há alerta neste campo; isso não certifica ausência de falhas, desgaste, latência elevada ou configuração inadequada do disco.", Confidence.High);
            return Result(AuditClassification.InsufficientData, "Saúde do disco não determinada",
                "O provedor não retornou um HealthStatus reconhecido como Healthy, Warning ou Unhealthy. A identidade do disco foi preservada; estado desconhecido não equivale a saudável.", Confidence.Low);
        }
        if (rule.Id == "devices.identity" && uint.TryParse(observation.Facts.GetValueOrDefault("pnpProblemCode"),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out var problemCode))
            return problemCode == 0
                ? Result(AuditClassification.Adequate, "Nenhum código de problema PnP informado",
                    "O Configuration Manager reportou código 0 para o dispositivo. Isso não avalia qualidade do driver, DPC/ISR, latência ou adequação das suas configurações.", Confidence.High)
                : Result(AuditClassification.PossibleProblem, "O Windows informou um código de problema PnP",
                    $"Código reportado: {problemCode}. Verificar o dispositivo no Gerenciador de Dispositivos; um código de problema não demonstra que ele causa a perda de desempenho do jogo.", Confidence.High);
        if (rule.Id == "windows.ifeo.cpu" && observation.Facts.GetValueOrDefault("matchingProcesses") == "0")
            return Result(AuditClassification.AppliedUnproven, "Regra persistente sem processo correspondente nesta captura",
                "A entrada IFEO existe, mas nenhum executável com esse nome foi observado. Isso não prova que o programa foi removido nem que a regra foi utilizada.", Confidence.High);
        if (rule.Id == "windows.ifeo.cpu" && observation.Facts.TryGetValue("observedProcessClasses", out var classes) && !string.IsNullOrWhiteSpace(classes))
            return Result(AuditClassification.AppliedUnproven, "Regra persistente e processo observado; vínculo não confirmado",
                "Classes observadas separadamente: " + classes + ". A correspondência usa o nome do executável, não sua identidade por caminho. " +
                "A classe atual não comprova que o IFEO foi aplicado e não foi inferida do número armazenado.", Confidence.High);
        if (rule.Id.StartsWith("windows.bcd.", StringComparison.Ordinal))
            return Result(AuditClassification.ComparisonOpportunity, "Opção de inicialização explícita encontrada",
                "A Microsoft destina estas opções de temporização a depuração. O valor do BCD não comprova ganho, fonte QPC efetiva ou redução de input lag; a configuração pode depender de uma reinicialização.", Confidence.High);
        if (observation.Facts.TryGetValue("comparison", out var comparison))
            return Result(AuditClassification.ComparisonOpportunity, "Comparação controlada pode esclarecer o efeito", comparison);
        if (rule.Support is AuditSupport.NotVerifiable or AuditSupport.Experimental)
            return Result(AuditClassification.AppliedUnproven, "Valor encontrado; efeito não verificado",
                "A leitura registra o valor bruto. O contrato ou uso efetivo não foi confirmado para este Windows/driver. " + rule.Limitations, Confidence.Low);
        if (observation.Facts.GetValueOrDefault("configured") == "false" && observation.Facts.ContainsKey("effective"))
            return Result(AuditClassification.InsufficientData, "Estado lido; adequação ainda não determinada",
                observation.Facts["effective"] + " Não há critério suficiente neste item para classificar o desempenho ou a configuração como adequados. " + rule.Limitations);
        return Result(AuditClassification.AppliedUnproven, "Configuração encontrada; benefício não demonstrado",
            "A fonte forneceu a configuração. Reconhecimento, aplicação ao processo e impacto precisam de evidências próprias. " + rule.Limitations);
    }

    private static uint? Numeric(AuditObservation observation) => uint.TryParse(observation.Facts.GetValueOrDefault("decimal"),
        NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
}
