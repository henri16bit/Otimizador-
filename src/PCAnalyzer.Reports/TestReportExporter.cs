using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCAnalyzer.Core;

namespace PCAnalyzer.Reports;

public static class TestReportExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string ExportJson(TestSession run, bool anonymize = true) => JsonSerializer.Serialize(anonymize ? Redact(run) : run, Options);
    public static string ExportHtml(TestSession run, bool anonymize = true)
    {
        var safe = anonymize ? Redact(run) : run;
        static string H(string? text) => WebUtility.HtmlEncode(text ?? "");
        var html = new StringBuilder("<!doctype html><html lang=\"pt-BR\"><meta charset=\"utf-8\"><title>PC Analyzer — resultado</title><style>body{font:16px system-ui;background:#101827;color:#e2e8f0;max-width:1080px;margin:32px auto;padding:20px}article,details{background:#192337;padding:18px;margin:12px 0;border-radius:8px}td,th{padding:10px;text-align:left;border-bottom:1px solid #334155}pre{white-space:pre-wrap;overflow-wrap:anywhere}h2{color:#93c5fd}</style><h1>");
        html.Append(H(safe.Name)).Append("</h1><p>").Append(H(safe.Status.ToString())).Append(" · ").Append(H(safe.StartedAt.ToString("O"))).Append("</p><p>").Append(H(safe.Error)).Append("</p>");
        html.Append("<h2>Indicadores</h2><table><tr><th>Métrica</th><th>Valor</th><th>Interpretação</th></tr>");
        foreach (var indicator in safe.Result?.Indicators ?? [])
            html.Append("<tr><td>").Append(H(indicator.Name)).Append("</td><td>").Append(H(indicator.Value?.ToString("0.###") ?? "—")).Append(' ').Append(H(indicator.Unit)).Append("</td><td>").Append(H(indicator.Detail)).Append("</td></tr>");
        html.Append("</table><h2>Evidências e recomendações</h2>");
        foreach (var finding in safe.Result?.Findings ?? [])
        {
            html.Append("<article><h3>").Append(H(finding.Title)).Append("</h3><p>").Append(H(finding.Category + " · confiança " + finding.Confidence)).Append("</p><p>").Append(H(finding.Explanation)).Append("</p><p>").Append(H(finding.Recommendation)).Append("</p>");
            foreach (var evidence in finding.Evidence) html.Append("<p>").Append(H($"{evidence.Description} · {evidence.Value:0.###} {evidence.Unit} · {evidence.Source} · {evidence.Timestamp:O}")).Append("</p>");
            html.Append("</article>");
        }
        html.Append("<h2>Limitações</h2>");
        foreach (var limit in safe.Result?.Limitations ?? []) html.Append("<p>").Append(H(limit)).Append("</p>");
        html.Append("<details><summary>Resultado técnico</summary><pre>").Append(H(string.Join("\n", safe.Result?.Lines ?? []))).Append("</pre></details>");
        html.Append("<details><summary>Registro completo, séries e contexto</summary><pre>").Append(H(JsonSerializer.Serialize(safe, Options))).Append("</pre></details></html>");
        return html.ToString();
    }
    public static Task ExportAsync(TestSession run, string path, bool anonymize, CancellationToken ct) => Task.Run(async () =>
    {
        var full = Path.GetFullPath(path); var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var text = Path.GetExtension(full).Equals(".json", StringComparison.OrdinalIgnoreCase) ? ExportJson(run, anonymize) : ExportHtml(run, anonymize);
        try { await File.WriteAllTextAsync(temporary, text, Encoding.UTF8, ct); ct.ThrowIfCancellationRequested(); File.Move(temporary, full, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, ct);

    private static TestSession Redact(TestSession run)
    {
        var context = run.Result?.Session ?? new DiagnosticSession(run.Id, run.Name, run.StartedAt, run.EndedAt,
            new("", "", 1), new(run.StartedAt, [], []), [], []);
        var clean = ReportPrivacy.CreateTextRedactor(context);
        var safe = ReportPrivacy.Anonymize(context);
        var result = run.Result;
        if (result is not null)
        {
            var metrics = ReportPrivacy.Anonymize(context with { Samples = result.Metrics ?? [], Findings = result.Findings ?? [] });
            result = result with
            {
                Title = clean(result.Title), Lines = result.Lines.Select(clean).ToArray(), Metrics = metrics.Samples, Findings = metrics.Findings,
                ComparisonKey = result.ComparisonKey is null ? null : clean(result.ComparisonKey),
                Session = result.Session is null ? null : safe,
                Limitations = result.Limitations?.Select(clean).ToArray(),
                Indicators = result.Indicators?.Select(i => i with { Id = clean(i.Id), Name = clean(i.Name), Unit = clean(i.Unit), Detail = clean(i.Detail) }).ToArray(),
                Series = result.Series?.Select(s => s with { Name = clean(s.Name), Unit = clean(s.Unit), Source = clean(s.Source), TimeBasis = clean(s.TimeBasis) }).ToArray()
            };
        }
        return run with { Id = safe.Id, Name = clean(run.Name), Error = run.Error is null ? null : clean(run.Error),
            PersistenceError = run.PersistenceError is null ? null : clean(run.PersistenceError), Result = result,
            Progress = run.Progress with { Stage = clean(run.Progress.Stage), Detail = run.Progress.Detail is null ? null : clean(run.Progress.Detail) } };
    }
}
