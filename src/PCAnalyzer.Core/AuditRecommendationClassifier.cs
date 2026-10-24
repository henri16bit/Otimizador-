using System.Globalization;
using System.Text.RegularExpressions;

namespace PCAnalyzer.Core;

public enum AuditFunctionalKind
{
    Adjustable, Informational, DocumentedReference, TestDependent, NoDocumentedEffect, Unsupported, Incomplete
}

/// <summary>Separate evidence for obtaining a value, interpreting it, recommending an action and measuring a benefit.</summary>
public sealed record AuditRecommendation(
    AuditFunctionalKind FunctionalKind, AuditRecommendationKind Kind, string RecommendedValue,
    string ShortReason, string Reason, string PossibleImpact, string Validation, string Risks,
    Confidence? ReadingConfidence, Confidence? InterpretationConfidence, Confidence? RecommendationConfidence,
    Confidence? BenefitConfidence, bool IsImprovement = false, bool IsAdequate = false,
    int Priority = 10, string? TestId = null, string? StatusLabel = null);

/// <summary>Specific, read-only policies. A property or a comparison flag alone is never an optimization opportunity.</summary>
public static partial class AuditRecommendationClassifier
{
    public static AuditRecommendation Classify(AuditAssessment assessment)
    {
        ArgumentNullException.ThrowIfNull(assessment);
        var o = assessment.Observation;
        if (!Readable(o))
        {
            var unsupported = o.Status is Availability.Unsupported or Availability.ToolMissing;
            var reason = Fact(o, "presence") == "absent" ? "A origem não contém valor explícito; o padrão efetivo não foi confirmado." : o.Status switch
            {
                Availability.PermissionRequired => "A fonte negou acesso; o valor atual não foi obtido.",
                Availability.ReadError => "A consulta falhou; o erro completo está na evidência técnica.",
                Availability.Unsupported => "O recurso não é suportado pela fonte disponível neste ambiente.",
                Availability.ToolMissing => "A API ou ferramenta necessária para esta leitura não está disponível.",
                _ => "A fonte não forneceu o valor necessário para avaliar esta configuração."
            };
            return new(unsupported ? AuditFunctionalKind.Unsupported : AuditFunctionalKind.Incomplete,
                AuditRecommendationKind.Unavailable, unsupported ? "Não aplicável neste ambiente" : "Leitura necessária",
                reason, reason, "Não é possível avaliar o impacto sem a leitura.",
                unsupported ? "Confira o suporte do Windows e do driver; uma opção ausente não equivale a desativada." : "Atualize a leitura e confira a fonte antes de considerar uma mudança.",
                "", null, null, null, null, StatusLabel: unsupported ? "Não suportado" : "Leitura incompleta");
        }
        if (assessment.Rule.Support == AuditSupport.Ignored || o.RuleId is "windows.mmcss.gpu" or "windows.mmcss.sfio")
            return Decision(AuditFunctionalKind.NoDocumentedEffect, AuditRecommendationKind.KeepCurrent, "Não alterar para desempenho",
                o.RuleId.EndsWith("gpu", StringComparison.Ordinal) ? "GPU Priority é documentado como não utilizado pelo MMCSS." : "Este campo não tem efeito documentado aplicável nesta auditoria.",
                "A leitura confirma o valor armazenado, mas não sustenta ganho de FPS ou latência ao modificá-lo.",
                "Nenhum benefício atribuído a este campo.", "Investigue medições do jogo e configurações com efeito aplicável, sem usar este campo como indicador de desempenho.",
                confidence: Confidence.High);
        // Virtual/disabled identities are retained, not promoted by an unrelated invalid/comparison fact.
        if (IsInformationalDevice(o) || o.RuleId.StartsWith("network.", StringComparison.Ordinal) &&
            bool.TryParse(Fact(o, "HardwareInterface"), out var physical) && !physical)
            return InformationalDevice();
        return Windows(assessment) ?? HardwareNetwork(assessment) ?? Nvidia(assessment) ?? Unknown();
    }

    public static bool Readable(AuditObservation o) => o.Status == Availability.Available && Fact(o, "presence") != "absent" &&
        (!string.IsNullOrWhiteSpace(o.RawValue) || !string.IsNullOrWhiteSpace(Fact(o, "decimal")) ||
         o.RuleId == "devices.identity" && uint.TryParse(Fact(o, "pnpProblemCode"), out _));

    internal static uint? Number(AuditObservation o)
    {
        if (uint.TryParse(Fact(o, "decimal"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)) return number;
        var value = o.RawValue?.Trim();
        if (value is null) return null;
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = value[2..].Split(' ', '(', ';', ',')[0];
            return uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out number) ? number : null;
        }
        return uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out number) ? number : null;
    }
    private static string? Fact(AuditObservation o, string key) => o.Facts.GetValueOrDefault(key);
    internal static bool IsInformationalDevice(AuditObservation o) => o.RuleId.StartsWith("devices.", StringComparison.Ordinal) &&
        (Fact(o, "pnpProblemCode") == "22" || Fact(o, "busEnumerator")?.Equals("SWD", StringComparison.OrdinalIgnoreCase) == true ||
         o.Name.Contains("virtual", StringComparison.OrdinalIgnoreCase) || o.Name.Contains("Kernel Debug", StringComparison.OrdinalIgnoreCase));
    internal static AuditRecommendation InformationalDevice() =>
        Info("Este dispositivo é virtual ou está desabilitado; isso não demonstra perda de desempenho.",
            "Investigue a identidade somente se o dispositivo for necessário ao cenário observado.");
    private static AuditRecommendation Unknown(string reason = "Não há regra técnica aplicável para recomendar mudança deste valor.") =>
        new(AuditFunctionalKind.Incomplete, AuditRecommendationKind.NoUniversalValue, "Sem recomendação fundamentada",
            reason, reason, "Benefício não avaliado.", "Consulte a documentação específica da propriedade antes de escolher um teste.",
            "", Confidence.High, null, null, null, StatusLabel: "Não avaliado");
    private static AuditRecommendation Info(string reason, string validation) => Decision(AuditFunctionalKind.Informational,
        AuditRecommendationKind.NoUniversalValue, "Não aplicável", reason, reason,
        "Este item descreve o sistema; não é uma proposta de otimização.", validation, confidence: Confidence.High);
    private static AuditRecommendation Keep(string shortReason, string reason, string impact, string validation,
        string risks = "", string? test = null, Confidence confidence = Confidence.Medium,
        AuditFunctionalKind functional = AuditFunctionalKind.Adjustable) => Decision(functional,
            AuditRecommendationKind.KeepCurrent, "Manter", shortReason, reason, impact, validation, risks,
            confidence, adequate: true, test: test);
    private static AuditRecommendation Investigate(string recommendation, string shortReason, string reason, string impact,
        string validation, string risks = "", string? test = null, bool compare = false,
        Confidence confidence = Confidence.Medium, AuditFunctionalKind functional = AuditFunctionalKind.TestDependent,
        int priority = 70) => Decision(functional, compare ? AuditRecommendationKind.Compare : AuditRecommendationKind.NoUniversalValue,
            recommendation, shortReason, reason, impact, validation, risks, confidence,
            improvement: true, priority: priority, test: test);
    private static AuditRecommendation Decision(AuditFunctionalKind functional, AuditRecommendationKind kind, string recommendation,
        string shortReason, string reason, string impact, string validation, string risks = "", Confidence? confidence = Confidence.Medium,
        bool improvement = false, bool adequate = false, int priority = 10, string? test = null) =>
        new(functional, kind, recommendation, shortReason, reason, impact, validation, risks,
            Confidence.High, confidence == Confidence.High ? Confidence.High : Confidence.Medium, confidence, null,
            improvement, adequate, improvement ? priority : adequate ? 20 : 5, test);
    private static bool Invalid(AuditObservation o) => o.Facts.TryGetValue("invalid", out var invalid) && !invalid.Equals("false", StringComparison.OrdinalIgnoreCase);
    private static AuditRecommendation Contract(AuditObservation o) => Investigate("Conferir o valor aceito pela fonte",
        "A leitura não atende ao contrato de tipo ou intervalo desta configuração.",
        Fact(o, "invalidReason") ?? "O coletor identificou um tipo ou valor fora do contrato documentado; isso não prova um problema de desempenho.",
        "Restabelecer uma configuração reconhecida pode evitar comportamento inesperado; ganho não medido.",
        "Confira tipo e valores aceitos na documentação da propriedade e releia o estado efetivo.",
        "Não substitua por um número de tutorial; a correção depende da propriedade e da versão do sistema.",
        confidence: Confidence.Medium, functional: AuditFunctionalKind.DocumentedReference, priority: 80);
}
