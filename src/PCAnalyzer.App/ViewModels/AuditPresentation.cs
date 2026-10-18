using PCAnalyzer.Core;

namespace PCAnalyzer.App.ViewModels;

/// <summary>Labels and grouping only; conclusions stay in the audited evidence model.</summary>
public sealed record AuditAssessmentRow(AuditAssessment Assessment)
{
    public string Name => Assessment.Observation.Name;
    public string Summary => Assessment.Summary;
    public string Reason => Assessment.Reason;
    public string PossibleBenefit => Assessment.PossibleBenefit;
    public Confidence Confidence => Assessment.Confidence;
    public string Risks => Assessment.Risks;
    public string Validation => Assessment.Validation;
    public string RawValue => Assessment.Observation.RawValue ?? "Valor não disponível";
    public string Scope => Assessment.Observation.Scope;
    public string Source => Assessment.Observation.Source;
    public string Limitation => Assessment.Observation.Limitation ?? Assessment.Rule.Limitations;
    public IEnumerable<string> Facts => Assessment.Observation.Facts.Select(f => f.Key + ": " + f.Value);
    public IEnumerable<string> Sources => Assessment.Rule.Sources;
    public string ClassificationLabel => Assessment.Classification switch
    {
        AuditClassification.DemonstratedProblem => "Problema demonstrado",
        AuditClassification.PossibleProblem => "Possível problema",
        AuditClassification.Adequate => "Adequada nas condições avaliadas",
        AuditClassification.AppliedUnproven => "Aplicado · benefício não demonstrado",
        AuditClassification.NoDocumentedEffect => "Sem efeito documentado",
        AuditClassification.ComparisonOpportunity => "Oportunidade de comparação",
        AuditClassification.Unsupported => "Não suportado pelo método disponível",
        _ => "Dados insuficientes"
    };
    public string SupportLabel => Assessment.Rule.Support switch
    {
        AuditSupport.Documented => "Mecanismo documentado", AuditSupport.Conditional => "Depende do contexto",
        AuditSupport.Experimental => "Experimental", AuditSupport.Obsolete => "Obsoleto",
        AuditSupport.Ignored => "Ignorado neste contexto", AuditSupport.NoDemonstratedBenefit => "Benefício não demonstrado",
        AuditSupport.PotentiallyHarmful => "Risco potencial", _ => "Não verificável pelo método disponível"
    };
}

public sealed record AuditCategoryRow(string Name, IReadOnlyList<AuditAssessmentRow> Assessments)
{
    public string Coverage => $"{Assessments.Count(a => a.Assessment.Observation.Status == Availability.Available)} de {Assessments.Count} leituras disponíveis";
    public string Summary
    {
        get
        {
            var attention = Assessments.Count(a => a.Assessment.Classification is AuditClassification.DemonstratedProblem or AuditClassification.PossibleProblem);
            var unproven = Assessments.Count(a => a.Assessment.Classification == AuditClassification.AppliedUnproven);
            var missing = Assessments.Count(a => a.Assessment.Classification is AuditClassification.InsufficientData or AuditClassification.Unsupported);
            return $"{attention} para investigar · {unproven} ajustes sem benefício demonstrado · {missing} sem conclusão";
        }
    }
}
