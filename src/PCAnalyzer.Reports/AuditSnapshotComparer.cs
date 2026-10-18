using PCAnalyzer.Core;

namespace PCAnalyzer.Reports;

public static class AuditSnapshotComparer
{
    public static IReadOnlyList<string> Describe(PerformanceAuditReport before, PerformanceAuditReport after)
    {
        var lines = new List<string>
        {
            "Snapshots de configurações: diferenças observadas, sem alterações executadas pelo aplicativo.",
            "Uma diferença não comprova reconhecimento, uso pelo jogo ou benefício. Compare a mesma carga em sessões repetidas após qualquer mudança externa."
        };
        if (before.CatalogVersion != after.CatalogVersion)
            lines.Add("As versões do catálogo diferem; cobertura e interpretação podem ter mudado.");
        var a = before.Assessments.GroupBy(x => (x.Observation.RuleId, x.Observation.InstanceId)).ToDictionary(g => g.Key, g => g.First());
        var b = after.Assessments.GroupBy(x => (x.Observation.RuleId, x.Observation.InstanceId)).ToDictionary(g => g.Key, g => g.First());
        var changes = 0;
        var unavailable = 0;
        foreach (var key in a.Keys.Union(b.Keys))
        {
            if (!a.TryGetValue(key, out var first) || !b.TryGetValue(key, out var second))
            {
                unavailable++;
                continue; // Enumeration coverage is not proof of installation/removal or a changed value.
            }
            var x = first.Observation;
            var y = second.Observation;
            if (x.Status != Availability.Available || y.Status != Availability.Available)
            {
                unavailable++;
                continue;
            }
            if (x.RuleId == "windows.process.scheduling" &&
                (string.IsNullOrWhiteSpace(x.Facts.GetValueOrDefault("ProcessStartedAtUtc")) ||
                 string.IsNullOrWhiteSpace(x.Facts.GetValueOrDefault("ExecutableName")) ||
                 x.Facts.GetValueOrDefault("ProcessStartedAtUtc") != y.Facts.GetValueOrDefault("ProcessStartedAtUtc") ||
                 x.Facts.GetValueOrDefault("ExecutableName") != y.Facts.GetValueOrDefault("ExecutableName")))
            {
                unavailable++;
                continue; // A reused PID or inaccessible creation time cannot identify the same process.
            }
            var facts = x.Facts.Keys.Union(y.Facts.Keys).Where(k => k is not ("evidence" or "foreground" or "ProcessStartedAtUtc"))
                .Where(k => x.Facts.GetValueOrDefault(k) != y.Facts.GetValueOrDefault(k)).Order(StringComparer.Ordinal).ToArray();
            if (x.RawValue == y.RawValue && facts.Length == 0) continue;
            changes++;
            lines.Add($"{y.Name} · {y.Scope}: {Value(x)} → {Value(y)}. Origem: {y.Source}.");
            foreach (var field in facts)
                lines.Add($"  {field}: {x.Facts.GetValueOrDefault(field) ?? "não informado"} → {y.Facts.GetValueOrDefault(field) ?? "não informado"}.");
        }
        lines.Add($"{changes} diferenças de valores; {unavailable} itens sem leitura equivalente nos dois snapshots (inclui identidade de processo não confirmada).");
        if (changes == 0) lines.Add("Nenhuma diferença verificável nos valores comuns. Configurações fora da cobertura podem ter mudado.");
        return lines;
    }

    private static string Value(AuditObservation observation) => observation.Facts.GetValueOrDefault("presence") == "absent"
        ? "sem valor explícito nesta origem" : observation.RawValue ?? "sem valor bruto";
}
