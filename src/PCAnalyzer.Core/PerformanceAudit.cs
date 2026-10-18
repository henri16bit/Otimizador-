namespace PCAnalyzer.Core;

public enum AuditSupport { Documented, Conditional, Experimental, Obsolete, Ignored, NoDemonstratedBenefit, PotentiallyHarmful, NotVerifiable }
public enum AuditClassification { DemonstratedProblem, PossibleProblem, Adequate, AppliedUnproven, NoDocumentedEffect, ComparisonOpportunity, InsufficientData, Unsupported }

public sealed record AuditRule(string Id, string Category, string Name, string Method,
    string Prerequisites, string Compatibility, string RequiredEvidence, string ImpactHypothesis,
    AuditSupport Support, string Limitations, string Validation, IReadOnlyList<string> Sources);

/// <summary>Observed values only. Presence does not establish recognition, effective use or benefit.</summary>
public sealed record AuditObservation(string RuleId, string InstanceId, string Name, string Scope,
    string? RawValue, Availability Status, string Source, IReadOnlyDictionary<string, string> Facts,
    string? Limitation = null);

public sealed record AuditAssessment(AuditObservation Observation, AuditRule Rule,
    AuditClassification Classification, string Summary, string Reason, string PossibleBenefit,
    Confidence Confidence, string Risks, string Validation);

public sealed record PerformanceAuditReport(DateTimeOffset CapturedAt, string Summary,
    IReadOnlyList<AuditAssessment> Assessments, IReadOnlyList<string> Limitations,
    string CatalogVersion = "1");

public interface IPerformanceAuditProvider
{
    IReadOnlyList<AuditRule> Rules { get; }
    Task<IReadOnlyList<AuditObservation>> ReadAsync(CancellationToken ct);
}
