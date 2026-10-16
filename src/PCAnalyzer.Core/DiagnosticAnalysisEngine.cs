namespace PCAnalyzer.Core;

/// <summary>
/// Interprets the captured window, never nominal hardware performance or physical health.
/// A component needs ten distinct observations spanning eight seconds before passive attribution.
/// </summary>
public sealed class DiagnosticAnalysisEngine
{
    public DiagnosticReport Analyze(HardwareInventory inventory, IReadOnlyList<MetricSample> samples,
        IReadOnlyList<ProcessSample> processes, IReadOnlyList<Finding> additionalFindings,
        IReadOnlyList<DiagnosticStage> stages, bool cancelled = false)
    {
        var valid = samples.Where(IsValid).OrderBy(s => s.Timestamp).ToArray();
        var findings = new DiagnosticRuleEngine().Analyze(inventory, valid).Concat(additionalFindings)
            .Where(f => f.Id is not ("ram-platform" or "no-observed-anomaly" or "window" or "general"))
            .DistinctBy(f => f.Id).ToList();
        var components = new List<ComponentAssessment>();

        var cpu = Window(valid.Where(s => s.Key == "cpu.total.percent" && s.Unit == "%" && s.Value is >= 0 and <= 100));
        components.Add(Assess("cpu", HardwareName(inventory, "CPU", "CPU"), cpu,
            findings.Where(f => f.Component.Equals("CPU", StringComparison.OrdinalIgnoreCase)),
            "Não houve carga total de CPU sustentada segundo o critério de triagem nesta janela.",
            "Faltam dez amostras de uso da CPU ao longo de pelo menos oito segundos.",
            ["Uso não demonstra desempenho esperado, temperatura, throttling ou falha física."],
            new("inspect-processes", "Ver processos", "processos", "Confira quais aplicações estavam consumindo CPU.")));

        var memory = MemoryWindow(valid);
        components.Add(Assess("ram", HardwareName(inventory, "RAM", "RAM"), memory,
            findings.Where(f => f.Component.Equals("RAM", StringComparison.OrdinalIgnoreCase)),
            "Não houve pressão de memória pelo critério de memória disponível e commit nesta janela.",
            "Faltam amostras sincronizadas de memória disponível, commit e limite de commit.",
            ["A triagem não testa defeitos físicos, timings ou modo de canais da RAM."],
            new("inspect-processes", "Ver consumo por processo", "processos", "Localize aplicações com maior consumo de memória.")));

        var gpuGroups = valid.Where(s => s.Key.StartsWith("gpu.", StringComparison.Ordinal) &&
            s.Key.EndsWith(".utilization.percent", StringComparison.Ordinal)).GroupBy(s => s.Key[..^".utilization.percent".Length])
            .ToDictionary(g => g.Key, g => g.ToArray());
        var gpuIds = gpuGroups.Keys.Concat(inventory.Components.Where(c => c.Id.StartsWith("gpu.", StringComparison.Ordinal) &&
            int.TryParse(c.Id.AsSpan(4), out _)).Select(c => c.Id)).Distinct().ToArray();
        if (gpuIds.Length == 0)
            components.Add(Inconclusive("gpu", HardwareName(inventory, "GPU", "GPU"),
                "Não foi possível avaliar o uso da GPU.", MissingReasons(samples, "gpu.",
                    "A fonte de utilização da GPU não retornou uma janela válida.")));
        else foreach (var gpuId in gpuIds)
        {
            var gpu = gpuGroups.GetValueOrDefault(gpuId) ?? [];
            var name = inventory.Components.FirstOrDefault(c => c.Id == gpuId)?.Name ?? gpu.FirstOrDefault()?.Component ?? "GPU";
            var window = Window(gpu.Where(s => s.Unit == "%" && s.Value is >= 0 and <= 100));
            components.Add(Assess(gpuId, name, window, findings.Where(f => AppliesTo(f, gpuId, "GPU")),
                "Uso da GPU acompanhado com leituras dentro da faixa válida nesta janela.",
                "A utilização da GPU ainda não tem uma janela suficiente para avaliação.",
                ["Uso alto isolado pode ser esperado em jogos; não identifica gargalo nem defeito.",
                 "Sem correlação com frametimes, clocks e limites do modelo, não há conclusão sobre desempenho ou causa de travamentos."],
                new("analyze-games", "Analisar durante o jogo", "jogos", "Relacione a utilização com os quadros do jogo.")));
        }

        // Win32_DiskDrive IDs are the only inventory IDs explicitly mapped to PhysicalDisk counters.
        // MSFT_PhysicalDisk IDs are not assumed to share the same numbering.
        var disks = inventory.Components.Where(c => c.Id.StartsWith("disk.", StringComparison.Ordinal)).ToArray();
        if (disks.Length == 0)
            components.Add(Inconclusive("storage", "Armazenamento", "Não foi possível associar a telemetria às unidades físicas.",
                ["Informações de outro provedor não substituem o mapeamento da unidade aos contadores de I/O."]));
        foreach (var disk in disks)
        {
            var diskSamples = valid.Where(s => s.Key.StartsWith(disk.Id + ".", StringComparison.Ordinal)).ToArray();
            var ioWindow = BestWindow(diskSamples.Where(s => s.Key.EndsWith(".operations_per_second", StringComparison.Ordinal) &&
                s.Unit == "I/O/s" && s.Value >= 0));
            var hasIo = ioWindow.Any(s => s.Value > 0);
            components.Add(Assess(disk.Id, disk.Name, hasIo ? ioWindow : [],
                findings.Where(f => AppliesTo(f, disk.Id, null)),
                "Operações de I/O observadas nesta unidade; nenhum problema demonstrado pelas evidências disponíveis.",
                hasIo ? "Faltam dez amostras de I/O em pelo menos oito segundos para esta unidade." :
                    "Sem operações suficientes nesta unidade para avaliar seu comportamento durante I/O.",
                ["Esta coleta não mede saúde física, desgaste, temperatura nem velocidade máxima do SSD.",
                 "Latência sem I/O não é tratada como zero; filas e tempos dependem da carga e do modelo."],
                new("inspect-storage", "Ver esta unidade", "armazenamento", "Observe a unidade enquanto a tarefa lenta estiver em execução.")));
        }

        var networkFindings = findings.Where(f => AppliesTo(f, "network", "Rede") || f.Id.StartsWith("ping", StringComparison.Ordinal)).ToArray();
        var networkEvidence = networkFindings.SelectMany(f => f.Evidence).ToArray();
        var networkWindow = BestWindow(valid.Where(s => s.Key.StartsWith("network.", StringComparison.Ordinal) &&
            s.Key.EndsWith(".errors.total", StringComparison.Ordinal) && s.Value >= 0));
        var networkConclusions = networkFindings.Where(f => f.Evidence.Count > 0 &&
            (f.Category is FindingCategory.ObservedProblem or FindingCategory.PossibleProblem ||
             f.Category == FindingCategory.NoAnomaly && f.Id.StartsWith("ping", StringComparison.Ordinal))).ToArray();
        if (networkConclusions.Length > 0)
            components.Add(FromFindings("network", "Rede", networkConclusions,
                networkFindings.Where(f => f.Category == FindingCategory.InsufficientData).Select(f => f.Explanation)
                    .Append("ICMP e contadores locais não comprovam a qualidade da conexão com o servidor de um jogo.").ToArray(),
                new("inspect-network", "Investigar conexão", "rede", "Compare o roteador e o destino afetado.")));
        else
            components.Add(Inconclusive("network", "Rede", "A qualidade da conexão não foi estabelecida nesta análise.",
                networkFindings.Where(f => f.Category == FindingCategory.InsufficientData).Select(f => f.Explanation)
                    .Append("Tráfego e velocidade do link não substituem um teste de resposta até um destino definido.").ToArray(),
                new("inspect-network", "Testar minha conexão", "rede", "Meça resposta e perda até o roteador ou destino escolhido."),
                networkEvidence.Concat(EvidenceFor(networkWindow, "Contador local observado; não mede a Internet")).ToArray()));

        components.Add(Investigation("drivers", "Drivers e interrupções", findings,
            f => f.Component.Contains("driver", StringComparison.OrdinalIgnoreCase) || f.Id.StartsWith("dpc", StringComparison.Ordinal) ||
                 f.Component.Contains("DPC", StringComparison.OrdinalIgnoreCase),
            "Não há captura suficiente para atribuir atrasos a drivers.",
            new("capture-latency", "Investigar atrasos", "drivers", "Uma captura de DPC/ISR permite investigar módulos e recorrência.")));
        components.Add(Investigation("gaming", "Jogos e fluidez", findings,
            f => f.Component.Equals("Jogos", StringComparison.OrdinalIgnoreCase) || f.Id.StartsWith("frame", StringComparison.Ordinal),
            "Sem uma captura de quadros suficiente para avaliar a fluidez do jogo.",
            new("analyze-games", "Analisar durante o jogo", "jogos", "Capture o processo do jogo enquanto o sintoma ocorre.")));
        components.Add(Investigation("stability", "Estabilidade", findings,
            f => f.Component.Contains("estabilidade", StringComparison.OrdinalIgnoreCase) || f.Id.StartsWith("stability", StringComparison.Ordinal),
            "A ausência de falhas nesta janela curta não confirma a estabilidade do computador.",
            new("repeat-general", "Repetir durante o problema", "analisar", "Colete novamente nas condições em que a falha ocorre.")));

        for (var index = 0; index < components.Count; index++)
        {
            var component = components[index];
            if (component.State != DiagnosticAssessmentState.Inconclusive) continue;
            var prefix = component.Id == "ram" ? "memory." : component.Id + ".";
            var sourceFailures = samples.Where(s => s.Status != Availability.Available &&
                (s.Key.StartsWith(prefix, StringComparison.Ordinal) ||
                 component.Id.StartsWith("gpu.", StringComparison.Ordinal) && s.Key == "gpu.telemetry"))
                .Select(s => s.Source + ": " + (s.Limitation ?? s.Status.ToString()));
            components[index] = component with { Limitations = component.Limitations.Concat(sourceFailures).Distinct().ToArray() };
        }

        // Do not promote findings that lack the coverage needed for their component attribution.
        var supported = components.Where(c => c.State != DiagnosticAssessmentState.Inconclusive).ToArray();
        var prioritized = findings.Where(f => f.Category == FindingCategory.InsufficientData || f.Evidence.Count > 0 &&
            (supported.Any(c => c.Evidence.Any(e => f.Evidence.Contains(e))) ||
             f.Category == FindingCategory.NoAnomaly && networkFindings.Contains(f)))
            .OrderByDescending(f => f.Category == FindingCategory.ObservedProblem)
            .ThenByDescending(f => f.Severity).ThenByDescending(f => f.Confidence).ToArray();
        // A relative-clock ETW/frame capture may support a scoped assessment without producing UTC MetricSamples.
        var hasEvidence = valid.Length > 0 || supported.Any(c => c.Evidence.Count > 0);
        var completion = cancelled ? DiagnosticCompletion.Cancelled : !hasEvidence ? DiagnosticCompletion.Failed :
            components.Any(c => c.State == DiagnosticAssessmentState.Inconclusive) || stages.Any(s => s.Status != Availability.Available)
                ? DiagnosticCompletion.Partial : DiagnosticCompletion.Completed;
        var problemCount = components.Count(c => c.State == DiagnosticAssessmentState.Problem);
        var attentionCount = components.Count(c => c.State == DiagnosticAssessmentState.Attention);
        var inconclusiveCount = components.Count(c => c.State == DiagnosticAssessmentState.Inconclusive);
        var summary = completion switch
        {
            DiagnosticCompletion.Cancelled => "Análise cancelada. As evidências já coletadas foram preservadas; áreas sem cobertura continuam inconclusivas.",
            DiagnosticCompletion.Failed => "Não foi possível obter telemetria válida para avaliar o computador. Verifique as fontes e tente novamente.",
            _ when problemCount > 0 => $"{problemCount} área(s) com problema observado, {attentionCount} merecem atenção e {inconclusiveCount} ficaram inconclusivas.",
            _ when attentionCount > 0 => $"{attentionCount} área(s) merecem atenção; {inconclusiveCount} ficaram inconclusivas. Confira as evidências antes de alterar configurações.",
            _ when inconclusiveCount > 0 => $"Nenhum problema demonstrado nas áreas avaliadas; {inconclusiveCount} área(s) precisam de mais dados.",
            _ => "Nenhum problema demonstrado no escopo observado. Isso não certifica saúde física nem desempenho esperado."
        };
        var recommendations = components.OrderBy(c => c.State switch
            { DiagnosticAssessmentState.Problem => 0, DiagnosticAssessmentState.Attention => 1, DiagnosticAssessmentState.Inconclusive => 2, _ => 3 })
            .Where(c => c.State != DiagnosticAssessmentState.Normal).Select(c => c.NextAction).DistinctBy(a => (a.Id, a.Target)).ToArray();
        var limitations = components.SelectMany(c => c.Limitations)
            .Concat(stages.Where(s => s.Status != Availability.Available).Select(s => s.Name + ": " + s.Detail))
            .Concat(processes.Count == 0 ? ["Nenhum processo foi registrado; a carga não pode ser atribuída a uma aplicação."] : Array.Empty<string>())
            .Distinct().ToArray();
        return new(completion, summary, components, prioritized, recommendations, limitations, stages.ToArray());
    }

    private static ComponentAssessment Assess(string id, string name, IReadOnlyList<MetricSample> window,
        IEnumerable<Finding> findings, string normalSummary, string unavailableSummary, IReadOnlyList<string> limitations,
        SuggestedNextAction nextAction)
    {
        if (!Ready(window)) return Inconclusive(id, name, unavailableSummary, limitations, nextAction,
            EvidenceFor(window, "Leitura disponível; cobertura ainda insuficiente"));
        var relevant = findings.Where(f => f.Evidence.Count > 0 && f.Category != FindingCategory.InsufficientData).ToArray();
        if (relevant.Length > 0) return FromFindings(id, name, relevant, limitations, nextAction);
        return new(id, name, DiagnosticAssessmentState.Normal, normalSummary,
            EvidenceFor(window, $"Janela de {window.Count} amostras distintas em {(window[^1].Timestamp - window[0].Timestamp).TotalSeconds:0.#} s"),
            limitations, Confidence.Low, new("compare", "Comparar outra análise", "historico", "Repita com a mesma carga e compare as evidências."));
    }
    private static ComponentAssessment Investigation(string id, string name, IReadOnlyList<Finding> findings,
        Func<Finding, bool> belongs, string missing, SuggestedNextAction action)
    {
        var all = findings.Where(belongs).ToArray();
        var relevant = all.Where(f => f.Evidence.Count > 0 && f.Category != FindingCategory.InsufficientData).ToArray();
        var limitations = all.Where(f => f.Category == FindingCategory.InsufficientData).Select(f => f.Explanation)
            .Append("Inventário ou coleta passiva não substituem a investigação específica.").ToArray();
        return relevant.Length == 0 ? Inconclusive(id, name, missing, limitations, action, all.SelectMany(f => f.Evidence).ToArray()) :
            FromFindings(id, name, relevant, limitations.Append("Associação temporal não demonstra a causa da falha.").ToArray(), action);
    }
    private static ComponentAssessment FromFindings(string id, string name, IReadOnlyList<Finding> findings,
        IReadOnlyList<string> limitations, SuggestedNextAction next)
    {
        var priority = findings.OrderByDescending(f => f.Category == FindingCategory.ObservedProblem)
            .ThenByDescending(f => f.Category == FindingCategory.PossibleProblem).ThenByDescending(f => f.Severity).First();
        var state = priority.Category switch { FindingCategory.ObservedProblem => DiagnosticAssessmentState.Problem,
            FindingCategory.NoAnomaly => DiagnosticAssessmentState.Normal, _ => DiagnosticAssessmentState.Attention };
        return new(id, name, state, priority.Explanation, findings.SelectMany(f => f.Evidence).Distinct().ToArray(),
            limitations, priority.Confidence, next);
    }
    private static ComponentAssessment Inconclusive(string id, string name, string summary, IReadOnlyList<string> limitations,
        SuggestedNextAction? action = null, IReadOnlyList<Evidence>? evidence = null) => new(id, name,
        DiagnosticAssessmentState.Inconclusive, summary, evidence ?? [], limitations, Confidence.Low,
        action ?? new("retry-telemetry", "Verificar coleta", "fontes", "Recupere a fonte de " + name + " e repita a análise."));
    private static bool AppliesTo(Finding finding, string id, string? category) =>
        finding.Evidence.Any(e => e.MetricKey?.StartsWith(id + ".", StringComparison.Ordinal) == true) ||
        finding.Component.Equals(id, StringComparison.OrdinalIgnoreCase) ||
        (category is not null && finding.Component.Equals(category, StringComparison.OrdinalIgnoreCase) &&
         !finding.Evidence.Any(e => e.MetricKey is not null));
    private static string HardwareName(HardwareInventory inventory, string category, string fallback) =>
        inventory.Components.FirstOrDefault(c => c.Category.StartsWith(category, StringComparison.OrdinalIgnoreCase))?.Name ?? fallback;
    private static bool IsValid(MetricSample sample) => sample.Status == Availability.Available && sample.Value is { } value &&
        double.IsFinite(value) && (sample.Unit != "%" || value is >= 0 and <= 100) &&
        (sample.Unit is not ("bytes" or "bytes/s" or "I/O/s" or "ms") || value >= 0);
    private static MetricSample[] Window(IEnumerable<MetricSample> source) => source.OrderBy(s => s.Timestamp).DistinctBy(s => s.Timestamp).ToArray();
    private static MetricSample[] BestWindow(IEnumerable<MetricSample> source) => source.GroupBy(s => s.Key).Select(Window)
        .OrderByDescending(Ready).ThenByDescending(s => s.Length).FirstOrDefault() ?? [];
    private static bool Ready(IReadOnlyList<MetricSample> samples) => samples.Count >= 10 && samples[^1].Timestamp - samples[0].Timestamp >= TimeSpan.FromSeconds(8);
    private static Evidence[] EvidenceFor(IEnumerable<MetricSample> samples, string description) => samples.TakeLast(3)
        .Select(s => new Evidence(description + "; " + s.Method, s.Source, s.Key, s.Value, s.Unit, s.Timestamp)).ToArray();
    private static IReadOnlyList<string> MissingReasons(IReadOnlyList<MetricSample> samples, string prefix, string fallback) =>
        samples.Where(s => s.Key.StartsWith(prefix, StringComparison.Ordinal) && s.Status != Availability.Available)
            .Select(s => s.Limitation ?? s.Status.ToString()).Append(fallback).Distinct().ToArray();
    private static MetricSample[] MemoryWindow(IReadOnlyList<MetricSample> samples)
    {
        var available = samples.Where(s => s.Key == "memory.available.bytes" && s.Unit == "bytes" && s.Value >= 0).ToArray();
        var commit = samples.Where(s => s.Key == "memory.commit.bytes" && s.Unit == "bytes" && s.Value >= 0).ToArray();
        var limit = samples.Where(s => s.Key == "memory.commit.limit.bytes" && s.Unit == "bytes" && s.Value > 0).ToArray();
        return Window(available.Where(a => commit.Any(c => Math.Abs((c.Timestamp - a.Timestamp).TotalSeconds) <= 2) &&
            limit.Any(l => Math.Abs((l.Timestamp - a.Timestamp).TotalSeconds) <= 2)));
    }
}
