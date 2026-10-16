namespace PCAnalyzer.Core;

public enum DiagnosticAssessmentState { Normal, Attention, Problem, Inconclusive }
public enum DiagnosticCompletion { Completed, Partial, Failed, Cancelled }

public sealed record SuggestedNextAction(string Id, string Label, string Target, string Reason);
public sealed record ComponentAssessment(string Id, string Name, DiagnosticAssessmentState State, string Summary,
    IReadOnlyList<Evidence> Evidence, IReadOnlyList<string> Limitations, Confidence Confidence,
    SuggestedNextAction NextAction);
public sealed record DiagnosticStage(string Id, string Name, Availability Status, string Detail,
    DateTimeOffset StartedAt, DateTimeOffset EndedAt);
public sealed record DiagnosticReport(DiagnosticCompletion Completion, string Summary,
    IReadOnlyList<ComponentAssessment> Components, IReadOnlyList<Finding> Findings,
    IReadOnlyList<SuggestedNextAction> Recommendations, IReadOnlyList<string> Limitations,
    IReadOnlyList<DiagnosticStage> Stages);
