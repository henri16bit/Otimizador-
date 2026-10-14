using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PCAnalyzer.Core;

namespace PCAnalyzer.Reports;

public enum ReportFormat { Json, Csv, Html }

public static class ReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string ExportJson(DiagnosticSession session, bool anonymize = true) =>
        JsonSerializer.Serialize(Snapshot(session, anonymize), JsonOptions);

    /// <summary>Lossless CSV representation: one row per JSON leaf, with RFC 6901 property paths.</summary>
    public static string ExportCsv(DiagnosticSession session, bool anonymize = true)
    {
        using var document = JsonDocument.Parse(ExportJson(session, anonymize));
        var output = new StringBuilder();
        AppendRow(output, "path", "type", "value");
        Flatten(document.RootElement, "", output);
        return output.ToString();
    }

    /// <summary>Telemetry-only table for spreadsheets. JSON/ExportCsv retain the complete diagnostic session.</summary>
    public static string ExportMetricCsv(DiagnosticSession session, bool anonymize = true)
    {
        var snapshot = Snapshot(session, anonymize);
        var output = new StringBuilder();
        AppendRow(output, "session_id", "workload", "collection_mode", "configured_interval_seconds", "key", "component", "value", "unit", "source", "method", "timestamp", "sampling_interval_seconds", "status", "kind", "aggregation", "limitation");
        foreach (var sample in snapshot.Samples)
            AppendRow(output, snapshot.Id.ToString("D"), snapshot.Context.Workload, snapshot.Context.CollectionMode,
                Number(snapshot.Context.IntervalSeconds), sample.Key, sample.Component, Number(sample.Value), sample.Unit,
                sample.Source, sample.Method, sample.Timestamp.ToString("O", CultureInfo.InvariantCulture),
                Number(sample.SamplingInterval.TotalSeconds), sample.Status.ToString(), sample.Kind.ToString(),
                sample.Aggregation.ToString(), sample.Limitation ?? "");
        return output.ToString();
    }

    public static string ExportHtml(DiagnosticSession session, bool anonymize = true)
    {
        var snapshot = Snapshot(session, anonymize);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, JsonOptions));
        var output = new StringBuilder("<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\"><title>PC Performance Analyzer — relatório</title><style>body{font:15px system-ui,sans-serif;max-width:1200px;margin:32px auto;padding:0 20px;color:#172033;background:#f8fafc}h1,h2{color:#0c3849}table{border-collapse:collapse;width:100%;background:white;margin-bottom:24px}th,td{border:1px solid #d5dfe9;padding:9px;text-align:left;vertical-align:top;overflow-wrap:anywhere}th{background:#e7f0f5}code{white-space:pre-wrap}details{margin:8px 0 18px}.metadata{color:#435268}.table-wrap{overflow:auto}</style></head><body>");
        output.Append("<h1>PC Performance Analyzer</h1><h2>").Append(Html(snapshot.Label)).Append("</h2><p class=\"metadata\">Sessão ")
            .Append(Html(snapshot.Id.ToString("D"))).Append(" · Início: ").Append(Html(snapshot.StartedAt.ToString("O", CultureInfo.InvariantCulture)))
            .Append(" · Fim: ").Append(Html(snapshot.EndedAt?.ToString("O", CultureInfo.InvariantCulture) ?? "em coleta"))
            .Append("</p><p>Privacidade: ").Append(anonymize ? "identificadores e dados locais redigidos" : "dados locais completos")
            .Append(". Os valores numéricos são os mesmos da sessão registrada. Valores ausentes permanecem ausentes.</p>");
        output.Append("<h2>Contexto da coleta</h2><p>Carga: ").Append(Html(snapshot.Context.Workload))
            .Append(" · Modo: ").Append(Html(snapshot.Context.CollectionMode)).Append(" · Intervalo configurado: ")
            .Append(Html(Number(snapshot.Context.IntervalSeconds))).Append(" s</p><p>").Append(Html(snapshot.Context.Notes)).Append("</p>");
        output.Append("<h2>Achados</h2>");
        if (snapshot.Findings.Count == 0) output.Append("<p>Nenhum achado registrado.</p>");
        foreach (var finding in snapshot.Findings)
        {
            output.Append("<article><h3>").Append(Html(finding.Title)).Append("</h3><p>").Append(Html(finding.Explanation))
                .Append("</p><p>").Append(Html(finding.Category.ToString())).Append(" · ").Append(Html(finding.Severity.ToString()))
                .Append(" · Confiança: ").Append(Html(finding.Confidence.ToString())).Append("</p><p>Recomendação: ")
                .Append(Html(finding.Recommendation)).Append("</p><p>Verificação: ").Append(Html(finding.Verification))
                .Append("</p><p>Reversão: ").Append(Html(finding.Reversal)).Append("</p></article>");
        }
        output.Append("<h2>Diário de alterações da sessão</h2>");
        if (snapshot.Changes is not { Count: > 0 })
            output.Append("<p>Nenhuma alteração associada a esta sessão.</p>");
        else
        {
            output.Append("<div class=\"table-wrap\"><table><thead><tr>");
            foreach (var title in new[] { "Registro", "Tipo", "Componente", "Valor original", "Valor aplicado/solicitado", "Risco", "Estado verificado", "Criado", "Atualizado", "Resultado/erro", "Recuperação" })
                output.Append("<th>").Append(Html(title)).Append("</th>");
            output.Append("</tr></thead><tbody>");
            foreach (var change in snapshot.Changes)
            {
                output.Append("<tr>");
                foreach (var value in new[] { change.Id.ToString("D"), change.Kind, change.Target, change.Before, change.After,
                             change.Risk.ToString(), change.State.ToString(), change.CreatedAt.ToString("O", CultureInfo.InvariantCulture),
                             change.UpdatedAt.ToString("O", CultureInfo.InvariantCulture), change.Error ?? "", change.RecoveryInstructions ?? "" })
                    output.Append("<td>").Append(Html(value)).Append("</td>");
                output.Append("</tr>");
            }
            output.Append("</tbody></table></div><p>O estado é o registro verificado da alteração. Valor solicitado não implica aplicação ou ativação efetiva. Restored indica restauração confirmada; falhas e intenções incompletas permanecem identificadas.</p>");
        }
        output.Append("<h2>Amostras</h2><div class=\"table-wrap\"><table><thead><tr>");
        foreach (var title in new[] { "Métrica", "Componente", "Valor", "Unidade", "Estado", "Fonte", "Método", "Instante", "Intervalo (s)", "Tipo", "Agregação", "Limitação" })
            output.Append("<th>").Append(Html(title)).Append("</th>");
        output.Append("</tr></thead><tbody>");
        foreach (var sample in snapshot.Samples)
        {
            output.Append("<tr>");
            foreach (var value in new[] { sample.Key, sample.Component, Number(sample.Value), sample.Unit, sample.Status.ToString(), sample.Source, sample.Method,
                         sample.Timestamp.ToString("O", CultureInfo.InvariantCulture), Number(sample.SamplingInterval.TotalSeconds), sample.Kind.ToString(), sample.Aggregation.ToString(), sample.Limitation ?? "" })
                output.Append("<td>").Append(Html(value)).Append("</td>");
            output.Append("</tr>");
        }
        output.Append("</tbody></table></div><h2>Registro completo</h2><p>Inclui inventário, capacidades, evidências, alterações associadas e todos os metadados usados nesta exportação.</p><div class=\"table-wrap\"><table><thead><tr><th>Campo</th><th>Tipo</th><th>Valor</th></tr></thead><tbody>");
        AppendHtmlLeaves(document.RootElement, "", output);
        output.Append("</tbody></table></div><p>Uma diferença entre sessões é descritiva. Carga, hardware, intervalo e quantidade de amostras influenciam o resultado; este relatório não demonstra causalidade nem ganho de FPS.</p></body></html>");
        return output.ToString();
    }

    public static Task ExportAsync(DiagnosticSession session, string path, ReportFormat format, bool anonymize = true,
        CancellationToken cancellationToken = default) => Task.Run(
            () => ExportCoreAsync(session, path, format, anonymize, cancellationToken), cancellationToken);

    private static async Task ExportCoreAsync(DiagnosticSession session, string path, ReportFormat format, bool anonymize,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var content = format switch
        {
            ReportFormat.Json => ExportJson(session, anonymize),
            ReportFormat.Csv => ExportCsv(session, anonymize),
            ReportFormat.Html => ExportHtml(session, anonymize),
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, content, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
    }

    private static DiagnosticSession Snapshot(DiagnosticSession session, bool anonymize)
    {
        ArgumentNullException.ThrowIfNull(session);
        return anonymize ? ReportPrivacy.Anonymize(session) : session;
    }
    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? "");
    private static string Number(double? value) => value is null ? "" : value.Value.ToString("R", CultureInfo.InvariantCulture);
    private static string Pointer(string value) => value.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
    private static string LeafValue(JsonElement element) => element.ValueKind == JsonValueKind.String ? element.GetString()! : element.GetRawText();

    private static void Flatten(JsonElement element, string path, StringBuilder output)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToArray();
            if (properties.Length == 0) AppendRow(output, path, "Object", "{}");
            foreach (var property in properties) Flatten(property.Value, path + "/" + Pointer(property.Name), output);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var items = element.EnumerateArray().ToArray();
            if (items.Length == 0) AppendRow(output, path, "Array", "[]");
            for (var index = 0; index < items.Length; index++) Flatten(items[index], path + "/" + index.ToString(CultureInfo.InvariantCulture), output);
        }
        else AppendRow(output, path, element.ValueKind.ToString(), LeafValue(element));
    }

    private static void AppendHtmlLeaves(JsonElement element, string path, StringBuilder output)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var properties = element.EnumerateObject().ToArray();
            if (properties.Length == 0) AppendHtmlLeaf(path, "Object", "{}", output);
            foreach (var property in properties) AppendHtmlLeaves(property.Value, path + "/" + Pointer(property.Name), output);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray()) AppendHtmlLeaves(item, path + "/" + index++, output);
            if (index == 0) AppendHtmlLeaf(path, "Array", "[]", output);
        }
        else AppendHtmlLeaf(path, element.ValueKind.ToString(), LeafValue(element), output);
    }

    private static void AppendHtmlLeaf(string path, string type, string value, StringBuilder output) =>
        output.Append("<tr><td><code>").Append(Html(path)).Append("</code></td><td>").Append(Html(type))
            .Append("</td><td>").Append(Html(value)).Append("</td></tr>");

    private static void AppendRow(StringBuilder output, params string[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            if (index > 0) output.Append(',');
            output.Append('"').Append(values[index].Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        output.Append("\r\n");
    }
}
