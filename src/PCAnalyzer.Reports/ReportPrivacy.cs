using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PCAnalyzer.Core;

namespace PCAnalyzer.Reports;

/// <summary>Produces an independent export snapshot; numerical measurements and their metadata are not rounded.</summary>
public static class ReportPrivacy
{
    private const string Redacted = "[redigido]";

    public static DiagnosticSession Anonymize(DiagnosticSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var identifiers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddIdentifier(Environment.MachineName);
        AddIdentifier(Environment.UserName);
        foreach (var component in session.Inventory.Components)
        {
            AddIdentifier(component.Id);
            foreach (var fact in component.Facts)
                if (IsPrivateFact(fact.Name) && fact.Value is not null) AddIdentifier(fact.Value);
        }
        if (session.Context.HardwareFingerprint is not null) AddIdentifier(session.Context.HardwareFingerprint);

        var known = identifiers.Count == 0 ? null : new Regex(
            string.Join("|", identifiers.OrderByDescending(value => value.Length).Select(Regex.Escape)),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

        string Clean(string? text)
        {
            if (text is null) return "";
            var clean = known?.Replace(text, Redacted) ?? text;
            return ScrubText(clean);
        }
        string? CleanNullable(string? text) => text is null ? null : Clean(text);

        var inventory = session.Inventory with
        {
            Components = session.Inventory.Components.Select(component => component with
            {
                Id = "componente-" + Fingerprint(component.Id),
                Category = Clean(component.Category),
                Name = Clean(component.Name),
                Facts = component.Facts.Select(fact => fact with
                {
                    Name = Clean(fact.Name),
                    Value = fact.Value is null ? null : IsPrivateFact(fact.Name) ? Redacted : Clean(fact.Value),
                    Source = Clean(fact.Source),
                    Limitation = CleanNullable(fact.Limitation)
                }).ToArray()
            }).ToArray(),
            Capabilities = session.Inventory.Capabilities.Select(capability => capability with
            {
                Name = Clean(capability.Name), Detail = Clean(capability.Detail)
            }).ToArray()
        };
        return session with
        {
            Id = Pseudonym(session.Id),
            Label = Clean(session.Label),
            Context = session.Context with
            {
                Workload = Clean(session.Context.Workload),
                CollectionMode = Clean(session.Context.CollectionMode),
                Notes = Clean(session.Context.Notes),
                HardwareFingerprint = null
            },
            Inventory = inventory,
            Samples = session.Samples.Select(sample => sample with
            {
                Key = Clean(sample.Key), Component = Clean(sample.Component), Unit = Clean(sample.Unit),
                Source = Clean(sample.Source), Method = Clean(sample.Method), Limitation = CleanNullable(sample.Limitation)
            }).ToArray(),
            Findings = session.Findings.Select(finding => finding with
            {
                Id = Clean(finding.Id), Component = Clean(finding.Component), Title = Clean(finding.Title),
                Explanation = Clean(finding.Explanation), Recommendation = Clean(finding.Recommendation),
                Verification = Clean(finding.Verification), Reversal = Clean(finding.Reversal),
                Context = Clean(finding.Context), ExpectedImpact = Clean(finding.ExpectedImpact),
                Requirements = Clean(finding.Requirements),
                Evidence = finding.Evidence.Select(evidence => evidence with
                {
                    Description = Clean(evidence.Description), Source = Clean(evidence.Source),
                    MetricKey = CleanNullable(evidence.MetricKey), Unit = CleanNullable(evidence.Unit)
                }).ToArray()
            }).ToArray(),
            Changes = session.Changes?.Select(change => change with
            {
                Id = Pseudonym(change.Id), Kind = Clean(change.Kind), Target = Clean(change.Target),
                Before = CleanConfiguration(change.Before, Clean), After = CleanConfiguration(change.After, Clean),
                Error = CleanNullable(change.Error), RecoveryInstructions = CleanNullable(change.RecoveryInstructions)
            }).ToArray()
        };

        void AddIdentifier(string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && value.Length >= 3) identifiers.Add(value);
        }
    }

    private static bool IsPrivateFact(string name)
    {
        var unaccented = new string(name.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark).ToArray());
        var normalized = Regex.Replace(unaccented, "[^a-z0-9]", "", RegexOptions.IgnoreCase).ToLowerInvariant();
        return normalized.Contains("serial", StringComparison.Ordinal) || normalized.Contains("uuid", StringComparison.Ordinal)
            || normalized.Contains("guid", StringComparison.Ordinal) || normalized.Contains("hostname", StringComparison.Ordinal)
            || normalized.Contains("computername", StringComparison.Ordinal) || normalized.Contains("machinename", StringComparison.Ordinal)
            || normalized.Contains("username", StringComparison.Ordinal) || normalized.Contains("address", StringComparison.Ordinal)
            || normalized.Contains("endereco", StringComparison.Ordinal) || normalized.Contains("caminho", StringComparison.Ordinal)
            || normalized.Contains("path", StringComparison.Ordinal) || normalized.Contains("mount", StringComparison.Ordinal)
            || normalized.Contains("volumeid", StringComparison.Ordinal) || normalized.Contains("volumelabel", StringComparison.Ordinal)
            || normalized.Contains("deviceid", StringComparison.Ordinal) || normalized.Contains("pnp", StringComparison.Ordinal)
            || normalized.Contains("identifier", StringComparison.Ordinal) || normalized.Contains("identificador", StringComparison.Ordinal)
            || normalized.Contains("identifying", StringComparison.Ordinal) || normalized.Contains("systemname", StringComparison.Ordinal)
            || normalized.Contains("computador", StringComparison.Ordinal) || normalized.Contains("usuario", StringComparison.Ordinal)
            || normalized.Contains("numerodeserie", StringComparison.Ordinal) || normalized.Contains("fqdn", StringComparison.Ordinal)
            || normalized.EndsWith("id", StringComparison.Ordinal)
            || normalized is "id" or "mac" or "ip" or "host" or "computer" or "machine" or "user";
    }

    private static string Fingerprint(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
    private static Guid Pseudonym(Guid id) => new(SHA256.HashData(id.ToByteArray()).AsSpan(0, 16));

    private static string CleanConfiguration(string text, Func<string, string> clean)
    {
        // Driver values are often JSON string arrays. Decode before scrubbing escaped paths or addresses.
        // Keep untouched formatting and all numeric driver values; redact the text of sensitive values only.
        try
        {
            var node = JsonNode.Parse(text);
            var changed = false;
            var sanitized = Visit(node);
            return changed ? sanitized?.ToJsonString() ?? "null" : text;

            JsonNode? Visit(JsonNode? current)
            {
                if (current is JsonArray array) return new JsonArray(array.Select(Visit).ToArray());
                if (current is JsonObject map)
                {
                    var result = new JsonObject();
                    foreach (var pair in map)
                    {
                        var key = clean(pair.Key);
                        changed |= key != pair.Key;
                        var uniqueKey = key;
                        var suffix = 1;
                        while (result.ContainsKey(uniqueKey)) uniqueKey = key + "_" + suffix++;
                        var privateKey = IsPrivateFact(pair.Key) || Regex.IsMatch(pair.Key,
                            @"(?i)^(password|passwd|pwd|secret|token|api[_-]?key|authorization|cookie)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
                        if (privateKey && pair.Value is not null)
                        {
                            changed = true;
                            result.Add(uniqueKey, JsonValue.Create(Redacted));
                        }
                        else result.Add(uniqueKey, Visit(pair.Value));
                    }
                    return result;
                }
                if (current is JsonValue value && value.TryGetValue<string>(out var original))
                {
                    if (double.TryParse(original, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number))
                        return current.DeepClone();
                    var sanitizedText = clean(original);
                    changed |= sanitizedText != original;
                    return JsonValue.Create(sanitizedText);
                }
                return current?.DeepClone();
            }
        }
        catch (JsonException) { return clean(text); }
    }

    private static string ScrubText(string text)
    {
        const RegexOptions options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        var timeout = TimeSpan.FromSeconds(1);
        text = Regex.Replace(text, @"\b(?:https?|ftp)://[^\s<>""']+", "[URL redigida]", options, timeout);
        text = Regex.Replace(text, @"(?:[A-Za-z]:\\|\\\\)[^\r\n""<>|]+", "[caminho redigido]", options, timeout);
        text = Regex.Replace(text, @"(?<![\w:])/(?:[^\s/]+/)+[^\s]*", "[caminho redigido]", options, timeout);
        text = Regex.Replace(text, @"\b[A-Za-z]:\\?\b", "[unidade redigida]", options, timeout);
        text = Regex.Replace(text, @"\b(?:\d{1,3}\.){3}\d{1,3}\b", "[IP redigido]", options, timeout);
        text = Regex.Replace(text, @"(?<![\w:])(?:[0-9a-f]{0,4}:){2,}[0-9a-f:.%]+(?!\w)", "[IP redigido]", options, timeout);
        text = Regex.Replace(text, @"\b(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}\b|\b(?:[0-9a-f]{4}\.){2}[0-9a-f]{4}\b", "[MAC redigido]", options, timeout);
        text = Regex.Replace(text, @"\b[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\b", "[identificador redigido]", options, timeout);
        text = Regex.Replace(text, @"\b[^\s@]+@[^\s@]+\.[^\s@]+\b", "[email redigido]", options, timeout);
        text = Regex.Replace(text, @"\b(?:localhost|(?:[a-z0-9-]+\.)+(?:local|lan|internal|corp|com|net|org|io))\b", "[host redigido]", options, timeout);
        text = Regex.Replace(text, @"\b(password|passwd|pwd|secret|token|api[_-]?key|authorization|cookie|host(?:name)?|computer(?:name)?|user(?:name)?|serial(?:number)?)\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|\S+)", "$1=" + Redacted, options, timeout);
        text = Regex.Replace(text, @"\bBearer\s+[A-Za-z0-9._~+/-]+=*|\b(?:gh[pousr]_[A-Za-z0-9]{20,}|sk-[A-Za-z0-9_-]{16,})\b", "[credencial redigida]", options, timeout);
        return text;
    }
}
