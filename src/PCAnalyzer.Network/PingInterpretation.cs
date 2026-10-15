using PCAnalyzer.Core;

namespace PCAnalyzer.Network;

public static class PingInterpretation
{
    public static IReadOnlyList<Finding> Analyze(PingMeasurement result)
    {
        var complete = result.SentCount >= 10 && !result.Cancelled;
        if (!complete)
            return [new("ping-insufficient", "Rede", "Cobertura insuficiente do teste de rede",
                "São necessários pelo menos dez pacotes concluídos e um teste sem cancelamento para esta triagem.",
                FindingCategory.InsufficientData, Severity.Information, Confidence.Low,
                [new($"{result.SentCount} pacotes concluídos", "ICMP", Value: result.SentCount, Unit: "pacotes", Timestamp: result.EndedAt)],
                "Confirme o destino e repita o teste.", "Compare o mesmo destino, conexão e carga.", "Nenhuma configuração foi alterada.")];
        var noReply = result.SentCount - result.ReceivedCount;
        return [new("ping-observation", "Rede", noReply > 0 ? "Houve pacotes sem resposta ICMP" : "Todos os pacotes concluídos receberam resposta",
            noReply > 0 ? "A ausência de resposta foi observada neste destino. Filtros, baixa prioridade ou limitação ICMP podem explicar o resultado; isso não demonstra perda no jogo."
                : "Este destino respondeu durante a janela. Isso não valida outros destinos, protocolos ou momentos.",
            noReply > 0 ? FindingCategory.PossibleProblem : FindingCategory.NoAnomaly,
            noReply > 0 ? Severity.Warning : Severity.Information, Confidence.Low,
            [new("Pacotes sem resposta entre os concluídos", "System.Net.NetworkInformation.Ping", "network.ping.loss.percent", result.LossPercent, "%", result.EndedAt),
             new(PingMeasurement.JitterDefinition, "System.Net.NetworkInformation.Ping", "network.ping.jitter.ms", result.JitterMs, "ms", result.EndedAt)],
            noReply > 0 ? "Repita para o gateway e depois para o destino desejado. Verifique cabo/link e carga antes de alterar o driver." : "Se o sintoma persistir, teste durante a ocorrência e compare o mesmo destino.",
            "Repita mantendo destino e carga. Não desative RSS/offloads em conjunto.", "Nenhuma configuração foi alterada.")];
    }
}
