namespace PCAnalyzer.Core;

public static class DiagnosticAuditIntegration
{
    /// <summary>Preserve measured diagnosis while giving the combined run one consistent completion state.</summary>
    public static TechnicalResult Merge(TechnicalResult diagnosis, TechnicalResult audit)
    {
        var cancelled = diagnosis.Cancelled || audit.Cancelled;
        var partial = diagnosis.Partial || audit.Partial || audit.Availability != Availability.Available;
        var limits = (diagnosis.Limitations ?? []).Concat(audit.Limitations ?? []).Distinct().ToArray();
        var report = diagnosis.Report;
        if (report is not null)
            report = report with
            {
                Completion = cancelled ? DiagnosticCompletion.Cancelled : partial && report.Completion == DiagnosticCompletion.Completed
                    ? DiagnosticCompletion.Partial : report.Completion,
                Limitations = report.Limitations.Concat(audit.Limitations ?? []).Distinct().ToArray()
            };
        return diagnosis with { Audit = audit.Audit, Partial = partial, Cancelled = cancelled, Limitations = limits, Report = report };
    }
}
