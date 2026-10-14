using System.Globalization;

namespace PCAnalyzer.Core;

public sealed class DiagnosticRuleEngine
{
    public IReadOnlyList<Finding> Analyze(HardwareInventory inventory, IReadOnlyList<MetricSample> samples)
    {
        var valid = samples.Where(s => s.Status == Availability.Available && s.Value.HasValue &&
            double.IsFinite(s.Value.Value)).ToArray();
        var findings = new List<Finding>();
        var groups = valid.GroupBy(s => s.Key).ToDictionary(g => g.Key, g => g.OrderBy(s => s.Timestamp).ToArray());
        if (groups.Count == 0)
            return [Insufficient("general", "Nenhuma medição válida", "Aguarde a coleta ou verifique as permissões e fontes disponíveis.")];

        foreach (var (key, values) in groups)
        {
            var recent = values.DistinctBy(s => s.Timestamp).TakeLast(30).ToArray();
            // Critérios de triagem de carga, nunca limites universais de defeito de hardware.
            if (recent.Length < 10 || recent[^1].Timestamp - recent[0].Timestamp < TimeSpan.FromSeconds(8)) continue;
            if (IsCpuUtilization(key) && recent.All(s => s.Unit == "%") &&
                recent.Count(v => v.Value >= 90) >= Math.Ceiling(recent.Length * .8))
            {
                findings.Add(new Finding("cpu-load:" + key, "CPU", "Carga de CPU sustentada nesta janela",
                    key == "cpu.total.percent"
                        ? "A utilização reportada permaneceu elevada. Isso descreve a carga observada; não prova perda de FPS."
                        : "Um processador lógico permaneceu ocupado. A média global baixa não exclui uma thread crítica; migração de threads exige ETW para atribuição.",
                    FindingCategory.PossibleProblem, Severity.Warning, Confidence.Medium,
                    EvidenceFor(recent, "Utilização sustentada; critério de triagem ≥90% em ≥80% das amostras"),
                    "Observe os processos e repita durante a carga de interesse, correlacionando com frametimes.",
                    "Capture novamente com a mesma carga e intervalo; use ETW de CPU para investigar threads.",
                    "Nenhuma configuração foi alterada."));
            }
        }
        var memoryWindowReady = false;
        if (groups.TryGetValue("memory.available.bytes", out var available) &&
            groups.TryGetValue("memory.commit.bytes", out var commit) &&
            groups.TryGetValue("memory.commit.limit.bytes", out var limit))
        {
            var a = available.TakeLast(30).ToArray();
            var commitWindow = commit.TakeLast(30).ToArray();
            var limitWindow = limit.TakeLast(30).ToArray();
            var paired = a.Select(sample =>
            {
                var c = commitWindow.MinBy(c => Math.Abs((c.Timestamp - sample.Timestamp).TotalSeconds));
                var l = limitWindow.MinBy(l => Math.Abs((l.Timestamp - sample.Timestamp).TotalSeconds));
                return (Available: sample, Commit: c, Limit: l);
            }).Where(p => p.Commit is not null && p.Limit is not null && p.Available.Unit == "bytes" &&
                p.Commit.Unit == "bytes" && p.Limit.Unit == "bytes" && p.Available.Value >= 0 && p.Commit.Value >= 0 &&
                p.Limit.Value > 0 && Math.Abs((p.Commit.Timestamp - p.Available.Timestamp).TotalSeconds) <= 2 &&
                Math.Abs((p.Limit.Timestamp - p.Available.Timestamp).TotalSeconds) <= 2)
                .DistinctBy(p => p.Available.Timestamp).ToArray();
            memoryWindowReady = paired.Length >= 10 && paired[^1].Available.Timestamp - paired[0].Available.Timestamp >= TimeSpan.FromSeconds(8);
            if (memoryWindowReady && paired.Count(p => p.Available.Value < 512 * 1024 * 1024 &&
                p.Commit!.Value / p.Limit!.Value > .9) >= Math.Ceiling(paired.Length * .8))
            {
                findings.Add(new Finding("memory-pressure", "RAM", "Pouca memória disponível e commit elevado",
                    "As duas condições foram observadas repetidamente. Paginação ou stuttering dependem de evidências adicionais de I/O e hard faults; faults totais também incluem soft faults.",
                    FindingCategory.ObservedProblem, Severity.Warning, Confidence.High,
                    EvidenceFor(paired.Select(p => p.Available), "Memória disponível <512 MiB; triagem, não prova de defeito físico").Concat(
                        EvidenceFor(paired.Select(p => p.Commit!), "Commit sincronizado; comparar com limite")).ToArray(),
                    "Identifique aplicações com maior consumo e reproduza a carga. Preserve o arquivo de paginação.",
                    "Compare memória disponível, commit e I/O na mesma carga após uma mudança individual.",
                    "Reabra a aplicação encerrada, se necessário; nenhuma mudança automática."));
            }
        }
        // Não inferir defeito de DIMM/XMP ou dual-channel a partir da velocidade reportada.
        if (inventory.Components.Any(c => c.Category.Equals("RAM", StringComparison.OrdinalIgnoreCase) ||
            c.Category.Equals("Memory", StringComparison.OrdinalIgnoreCase)))
            findings.Add(new Finding("ram-platform", "RAM", "Velocidade configurada exige contexto da plataforma",
                "SPD e velocidade configurada são informações diferentes. Dois módulos não comprovam dual-channel. Uma limitação de chipset, como B460 com i7-10700K a 2933 MT/s, não constitui defeito.",
                FindingCategory.InsufficientData, Severity.Information, Confidence.High,
                inventory.Components.Where(c => c.Category is "RAM" or "Memory").SelectMany(c => c.Facts)
                    .Where(f => f.Name.Contains("speed", StringComparison.OrdinalIgnoreCase) || f.Name.Contains("veloc", StringComparison.OrdinalIgnoreCase))
                    .Select(f => new Evidence(f.Name + ": " + (f.Value ?? "Indisponível"), f.Source)).ToArray(),
                "Verifique SKU, documentação da placa-mãe e configuração efetiva antes de qualquer ajuste.",
                "Confirme a configuração em fonte capaz de ler timings e modo de canais.", "Não há alteração proposta."));
        var cpuWindowReady = groups.Any(g => IsCpuUtilization(g.Key) &&
            g.Value.Where(s => s.Unit == "%").DistinctBy(s => s.Timestamp).TakeLast(30).ToArray() is { Length: >= 10 } window &&
            window[^1].Timestamp - window[0].Timestamp >= TimeSpan.FromSeconds(8));
        if (!cpuWindowReady || !memoryWindowReady)
            findings.Add(Insufficient("window", "Cobertura insuficiente para concluir a triagem",
                $"CPU: {(cpuWindowReady ? "janela disponível" : "faltam amostras de utilização")}; RAM: {(memoryWindowReady ? "janela sincronizada disponível" : "faltam amostras sincronizadas de disponível, commit e limite")}. Dados de outras métricas não substituem essas fontes."));
        else if (!findings.Any(f => f.Severity != Severity.Information))
            findings.Add(new Finding("no-observed-anomaly", "Sistema", "Sem motivo demonstrado para modificar configurações",
                "As regras de triagem não identificaram carga persistente relevante nesta janela. Isso não valida desempenho esperado do modelo de hardware nem exclui DPC, throttling, falhas físicas ou problemas fora da coleta.",
                FindingCategory.NoAnomaly, Severity.Information, Confidence.Low,
                [new Evidence($"{valid.Length} medições válidas; {groups.Count} séries", "Coletores disponíveis")],
                "Mantenha a configuração estável e colete durante o cenário que apresenta o problema.",
                "Reproduza a carga e compare sessões com condições equivalentes.", "Nenhuma alteração."));
        return findings;
    }

    private static bool IsCpuUtilization(string key)
    {
        if (key == "cpu.total.percent") return true;
        const string prefix = "cpu.logical.";
        const string suffix = ".percent";
        return key.Length > prefix.Length + suffix.Length && key.StartsWith(prefix, StringComparison.Ordinal) &&
            key.EndsWith(suffix, StringComparison.Ordinal) &&
            int.TryParse(key.AsSpan(prefix.Length, key.Length - prefix.Length - suffix.Length), NumberStyles.None,
                CultureInfo.InvariantCulture, out _);
    }

    private static Finding Insufficient(string id, string title, string explanation) => new(id, "Sistema", title,
        explanation, FindingCategory.InsufficientData, Severity.Information, Confidence.Low, [],
        "Colete dados reais antes de recomendar alterações.", "Verificar fonte, disponibilidade e janela de coleta.", "Nenhuma alteração.");
    private static Evidence[] EvidenceFor(IEnumerable<MetricSample> samples, string description) => samples.TakeLast(3)
        .Select(s => new Evidence(description, s.Source, s.Key, s.Value, s.Unit, s.Timestamp)).ToArray();
}
