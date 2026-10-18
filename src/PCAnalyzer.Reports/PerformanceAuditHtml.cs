using System.Net;
using System.Text;
using PCAnalyzer.Core;

namespace PCAnalyzer.Reports;

internal static class PerformanceAuditHtml
{
    private static string H(string? text) => WebUtility.HtmlEncode(text ?? "");
    internal static string Render(PerformanceAuditReport audit, DiagnosticReport? behavior)
    {
        var html = new StringBuilder("<h2>A. Resumo geral</h2><article><p>");
        html.Append(H(audit.Summary)).Append("</p><p>Configurações lidas em ").Append(H(audit.CapturedAt.ToString("O")))
            .Append(". Catálogo ").Append(H(audit.CatalogVersion)).Append(".</p><p>Estado configurado, estado reconhecido, comportamento e benefício são etapas distintas. Nenhum ajuste foi aplicado.</p></article>");
        var groups = new (string Heading, int Section, string[] Components)[]
        {
            ("B. Otimizações do Windows", 0, ["stability"]),
            ("C. CPU e agendamento", 1, ["cpu"]),
            ("D. GPU NVIDIA", 2, ["gpu", "drivers"]),
            ("E. MSI/IRQ/USB", 3, []),
            ("F. RAM e armazenamento", 4, ["ram", "disks"]),
            ("G. Rede", 5, ["network"])
        };
        var included = new HashSet<(string, string)>();
        foreach (var (heading, section, components) in groups)
        {
            html.Append("<h2>").Append(H(heading)).Append("</h2>");
            var rows = audit.Assessments.Where(a => Section(a.Rule.Id) == section &&
                included.Add((a.Observation.RuleId, a.Observation.InstanceId))).ToArray();
            if (rows.Length == 0) html.Append("<p>Não há leitura de configurações desta área neste snapshot.</p>");
            foreach (var category in rows.GroupBy(a => a.Rule.Category))
            {
                html.Append("<details><summary>").Append(H(category.Key)).Append(" · ")
                    .Append(category.Count(a => a.Observation.Status == Availability.Available)).Append('/').Append(category.Count())
                    .Append(" leituras disponíveis</summary>");
                foreach (var row in category) AppendAssessment(html, row);
                html.Append("</details>");
            }
            AppendBehavior(html, behavior, components);
        }
        var other = audit.Assessments.Where(a => !included.Contains((a.Observation.RuleId, a.Observation.InstanceId))).ToArray();
        if (other.Length > 0)
        {
            html.Append("<details><summary>Outras verificações do catálogo</summary>");
            foreach (var row in other) AppendAssessment(html, row);
            html.Append("</details>");
        }
        html.Append("<h2>H. Latência e DPC/ISR</h2>");
        AppendBehavior(html, behavior, ["latency", "stability"]);
        html.Append("<p>Duração DPC/ISR não mede input lag físico. Uma ocorrência isolada não comprova efeito no jogo.</p><h2>I. Desempenho em jogos</h2>");
        AppendBehavior(html, behavior, ["frames"]);
        html.Append("<h2>J. Recomendações</h2>");
        var recommendations = audit.Assessments.Where(a => a.Classification is AuditClassification.DemonstratedProblem or
            AuditClassification.PossibleProblem or AuditClassification.ComparisonOpportunity or AuditClassification.NoDocumentedEffect)
            .OrderBy(a => a.Classification == AuditClassification.DemonstratedProblem ? 0 : a.Classification == AuditClassification.PossibleProblem ? 1 : 2).ToArray();
        if (recommendations.Length == 0) html.Append("<p>Não há evidência suficiente para recomendar alterações a partir destas leituras.</p>");
        foreach (var row in recommendations) AppendAssessment(html, row);
        html.Append("<h2>K. Ajustes que não precisam ser alterados</h2><p>Ausência de problema demonstrado não significa otimização perfeita. Preserve o estado enquanto não houver uma hipótese e uma comparação justificadas.</p>");
        foreach (var row in audit.Assessments.Where(a => a.Classification == AuditClassification.Adequate))
            html.Append("<p><strong>").Append(H(row.Observation.Name)).Append("</strong>: ").Append(H(row.Summary)).Append("</p>");
        return html.ToString();
    }

    private static int Section(string id)
    {
        bool Starts(params string[] values) => values.Any(v => id.StartsWith(v, StringComparison.Ordinal));
        if (Starts("windows.scheduler.", "windows.ifeo.", "windows.process.", "hardware.cpu.")) return 1;
        if (Starts("nvidia.", "gpu.")) return 2;
        if (Starts("devices.", "windows.mouse.", "windows.input.")) return 3;
        if (Starts("memory.", "storage.", "windows.memory.")) return 4;
        if (Starts("network.")) return 5;
        return 0;
    }

    private static void AppendBehavior(StringBuilder html, DiagnosticReport? behavior, string[] components)
    {
        var matches = behavior?.Components.Where(c => components.Contains(c.Id)).ToArray() ?? [];
        if (matches.Length == 0)
        {
            if (components.Length > 0) html.Append("<p>Comportamento não medido nesta auditoria. Uma leitura de configuração não substitui uma captura.</p>");
            return;
        }
        foreach (var component in matches)
            html.Append("<p><strong>Comportamento observado — ").Append(H(component.Name)).Append(":</strong> ")
                .Append(H(component.Summary)).Append(" A proximidade com uma configuração não estabelece causalidade.</p>");
    }

    private static void AppendAssessment(StringBuilder html, AuditAssessment row)
    {
        html.Append("<article><h3>").Append(H(row.Observation.Name)).Append("</h3><p><strong>").Append(H(Label(row.Classification)))
            .Append("</strong> · ").Append(H(row.Summary)).Append("</p><p>").Append(H(row.Reason))
            .Append("</p><p>Hipótese: ").Append(H(row.PossibleBenefit)).Append("</p><p>Confiança: ").Append(H(row.Confidence.ToString()))
            .Append("</p><p>Riscos e limites: ").Append(H(row.Risks)).Append("</p><p>Como validar: ").Append(H(row.Validation))
            .Append("</p><details><summary>Valor, origem e fundamento técnico</summary><p>").Append(H(row.Observation.Scope))
            .Append(" · ").Append(H(row.Observation.Status.ToString())).Append("</p><pre>").Append(H(row.Observation.RawValue ?? "Sem valor disponível"))
            .Append("</pre><p>").Append(H(row.Observation.Source)).Append("</p><p>").Append(H(row.Observation.Limitation)).Append("</p>");
        foreach (var fact in row.Observation.Facts) html.Append("<p>").Append(H(fact.Key)).Append(": ").Append(H(fact.Value)).Append("</p>");
        foreach (var source in row.Rule.Sources) html.Append("<p>").Append(H(source)).Append("</p>");
        html.Append("</details></article>");
    }

    private static string Label(AuditClassification value) => value switch
    {
        AuditClassification.DemonstratedProblem => "Problema demonstrado", AuditClassification.PossibleProblem => "Possível problema",
        AuditClassification.Adequate => "Adequada nas condições avaliadas", AuditClassification.AppliedUnproven => "Ajuste encontrado; benefício não demonstrado",
        AuditClassification.NoDocumentedEffect => "Sem efeito documentado", AuditClassification.ComparisonOpportunity => "Oportunidade de comparação",
        AuditClassification.Unsupported => "Não suportado", _ => "Dados insuficientes"
    };
}
