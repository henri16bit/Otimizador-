using System.Globalization;
using PCAnalyzer.Core;

namespace PCAnalyzer.Diagnostics;

public enum LatencyConsultationClassification
{
    NoneDemonstrated, Investigate, GroundedAnomaly, Inconclusive, InvalidOrInsufficient
}

/// <summary>A scoped interpretation of measured kernel durations, never a measurement of physical input lag.</summary>
public sealed record LatencyConsultation(LatencyConsultationClassification Classification, string ClassificationLabel,
    string Summary, string NextStep, Confidence Confidence, string Coverage, IReadOnlyList<Finding> Findings,
    IReadOnlyList<string> Details, IReadOnlyList<LatencyDeviceRelation> DeviceRelations, bool Partial);

public static class LatencyInterpretation
{
    // This is a descriptive recurrence marker, not a universal latency defect threshold.
    private const double RecurrenceMarkerMicroseconds = 1000;

    public static IReadOnlyList<Finding> Analyze(EtwCaptureResult capture) => Consult(capture).Findings;

    public static LatencyConsultation Consult(EtwCaptureResult capture, string? context = null, PerformanceAuditReport? audit = null)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var summaries = LatencyResultPresentation.Summarize(capture);
        var validEvents = (capture.Timeline ?? []).Where(ValidEvent).ToArray();
        var timed = validEvents.Where(e => ValidPosition(e.TraceRelativeMilliseconds, capture.Duration)).ToArray();
        var quality = QualityIssues(capture, summaries, validEvents, timed).ToArray();
        var partial = IsPartial(capture) || quality.Length > 0;
        var knownContext = !string.IsNullOrWhiteSpace(context);
        var coverage = DescribeCoverage(capture);
        var recurrence = RecurringFindings(capture, timed, coverage, partial).ToArray();
        var validCount = summaries.Where(s => s.AnalyzedCount.HasValue).Sum(s => (decimal)s.AnalyzedCount!.Value);
        var first = timed.Length > 0 ? timed.Min(e => e.TraceRelativeMilliseconds!.Value) : (double?)null;
        var last = timed.Length > 0 ? timed.Max(e => e.TraceRelativeMilliseconds!.Value) : (double?)null;
        var occupiedWindows = timed.Select(e => (long)(e.TraceRelativeMilliseconds!.Value / 1000)).Distinct().Count();
        var span = last - first;
        // These are transparent evidence gates for this application's quick observation. They do not
        // establish that 30 seconds, 100 events or any duration value can certify gaming performance.
        var observationAdequate = capture.Duration.TotalSeconds >= 30 && knownContext && !partial &&
            summaries.All(s => s.AnalyzedCount >= 100 && s.P99Microseconds.HasValue) &&
            timed.Length == validEvents.Length && span >= capture.Duration.TotalMilliseconds * .5 &&
            occupiedWindows >= Math.Min(10, Math.Ceiling(capture.Duration.TotalSeconds / 2));
        var hasValidEvidence = validCount > 0 || validEvents.Length > 0;
        var invalid = !hasValidEvidence || capture.Duration <= TimeSpan.Zero || !CountsStructurallyValid(capture.EventCounts);
        var classification = invalid ? LatencyConsultationClassification.InvalidOrInsufficient :
            partial ? LatencyConsultationClassification.Inconclusive :
            recurrence.Length > 0 ? LatencyConsultationClassification.Investigate :
            observationAdequate && summaries.All(s => s.MaximumMicroseconds < RecurrenceMarkerMicroseconds)
                ? LatencyConsultationClassification.NoneDemonstrated : LatencyConsultationClassification.Inconclusive;
        // GroundedAnomaly is intentionally reserved: there is no machine/workload baseline or
        // independently corroborated performance fault in this capture contract alone.
        var label = classification switch
        {
            LatencyConsultationClassification.NoneDemonstrated => "Sem anomalia relevante demonstrada na janela",
            LatencyConsultationClassification.Investigate => "Padrão que merece investigação",
            LatencyConsultationClassification.GroundedAnomaly => "Anomalia identificada por critério fundamentado",
            LatencyConsultationClassification.InvalidOrInsufficient => "Captura inválida ou insuficiente",
            _ => "Inconclusivo"
        };
        var durations = string.Join("; ", summaries.Select(s => s.MaximumMicroseconds is { } maximum
            ? $"máximo {s.Kind} de {Number(maximum)} µs" : $"{s.Kind} sem duração disponível"));
        var measured = $"Janela registrada de {Number(Math.Max(0, capture.Duration.TotalSeconds))} s: {durations}.";
        var summary = classification switch
        {
            LatencyConsultationClassification.InvalidOrInsufficient =>
                "Não há evidência DPC/ISR válida e suficiente para interpretar esta captura. Ausência de eventos não demonstra baixa latência.",
            LatencyConsultationClassification.NoneDemonstrated => measured +
                " Com distribuições disponíveis e eventos espalhados na janela, os maiores eventos ficaram abaixo de 1 ms e não surgiu o padrão de recorrência investigado. Isso não comprova ausência de problemas no jogo nem baixo input lag físico.",
            LatencyConsultationClassification.Investigate => measured +
                $" Foram observados {recurrence.Length} padrão(ões) de eventos de pelo menos 1 ms recorrentes em instantes distintos. É uma triagem contextual, não um limite universal de defeito nem prova de culpa do driver.",
            _ when partial => measured +
                " A coleta tem lacunas ou inconsistências; os números descrevem somente as evidências preservadas. Não é possível concluir sobre toda a janela.",
            _ when capture.Duration.TotalSeconds < 30 => measured +
                " A observação é curta para a triagem proposta. Um máximo pequeno, isoladamente, não certifica responsividade nem ausência de problemas no jogo.",
            _ when !knownContext => measured +
                " O contexto de uso não foi informado; não é possível relacionar a distribuição ao sintoma investigado.",
            _ => measured +
                " A amostra, a distribuição temporal ou a recorrência não sustentam uma conclusão abrangente. Picos isolados não demonstram defeito e não medem input lag físico."
        };
        var next = invalid ? "Confira permissões e a fonte ETW; repita uma captura de 30 s durante uma carga conhecida antes de interpretar o resultado." :
            partial ? "Repita a captura sem perdas ou truncamento, conferindo o orçamento e o limite de eventos; mantenha a mesma carga e preserve o trace." :
            capture.Duration.TotalSeconds < 30 ? "Use 30 s como observação rápida com contexto explícito. Para sintomas intermitentes, escolha 60 ou 120 s durante uma sessão representativa; duração maior não garante qualidade." :
            !knownContext ? "Identifique a carga e o sintoma desta captura. Se investigar travadas no jogo, repita durante uma sessão representativa e compare os instantes com frametimes." :
            classification == LatencyConsultationClassification.Investigate ? "Confira os módulos e CPUs associados, repita durante o sintoma e compare os eventos com frametimes sincronizados. Não altere MSI ou afinidade com base apenas nesta associação." :
            "Se o sintoma persistir, capture durante a sessão representativa e compare eventos com frametimes; escolha 60 ou 120 s para ampliar a observação de ocorrências intermitentes.";
        var confidence = classification is LatencyConsultationClassification.NoneDemonstrated or LatencyConsultationClassification.Investigate
            ? Confidence.Medium : Confidence.Low;
        var relations = LatencyDeviceCorrelation.Analyze(capture, audit);
        var details = new List<string>
        {
            "Contexto informado: " + (knownContext ? context!.Trim() : "não informado"),
            $"Início registrado: {(capture.TraceStartUtc ?? capture.StartedAt):O}; duração registrada: {Number(capture.Duration.TotalSeconds)} s.",
            first is { } start && last is { } end
                ? $"Eventos válidos com posição conhecida: {timed.Length}; primeiro={Number(start)} ms; último={Number(end)} ms; extensão observada={Number(end - start)} ms; {occupiedWindows} janela(s) de um segundo com eventos. Intervalos sem eventos não comprovam cobertura contínua."
                : "Cobertura temporal indisponível: não há posições válidas dos eventos na janela.",
            "Critério de recorrência: ao menos três eventos do mesmo módulo/tipo de pelo menos 1.000 µs, distribuídos em ao menos duas janelas de um segundo. É um marcador de triagem, não um limiar universal de anomalia.",
            "Uma conclusão limitada de ausência de padrão exige contexto informado, janela de ao menos 30 s, 100 eventos por tipo para p99, posições espalhadas por metade da janela e pelo menos dez janelas de um segundo, sem perdas ou inconsistências. Esses critérios de cobertura não certificam desempenho ideal.",
            "Anomalia fundamentada não é emitida somente a partir de máximos/percentis; exige referência aplicável ou evidência independente que este contrato não fornece.",
            "Não foi estabelecida correlação temporal com utilização de CPU, frametimes ou entrada física. DPC, ISR e input lag são grandezas diferentes."
        };
        details.AddRange(LatencyResultPresentation.Details(capture));
        details.AddRange(quality.Select(q => "Limitação de qualidade: " + q));
        foreach (var group in capture.CpuSummaries.Where(ValidAggregate).OrderBy(s => s.Cpu).ThenBy(s => s.Kind))
            details.Add($"Índice CPU ETW {group.Cpu}, {group.Kind}: {group.Count} eventos válidos; máximo={Number(group.MaximumMicroseconds)} µs; média={Number(group.TotalMicroseconds / group.Count)} µs. Não identifica grupo de processadores nem conflito de afinidade.");
        foreach (var relation in relations)
        {
            details.Add($"{relation.Module}: {relation.DeviceName}. {relation.Observation}");
            details.AddRange(relation.TechnicalDetails);
        }
        var findingCategory = classification switch
        {
            LatencyConsultationClassification.NoneDemonstrated => FindingCategory.NoAnomaly,
            LatencyConsultationClassification.Investigate => FindingCategory.PossibleProblem,
            LatencyConsultationClassification.GroundedAnomaly => FindingCategory.ObservedProblem,
            _ => FindingCategory.InsufficientData
        };
        var findings = new List<Finding>
        {
            new("dpc-interpretation", "DPC/ISR", label, summary, findingCategory,
                findingCategory is FindingCategory.PossibleProblem or FindingCategory.ObservedProblem ? Severity.Warning : Severity.Information,
                confidence, [new(coverage, "Kernel ETW / TraceEvent"), new(measured, "Agregados por CPU e timeline retida")],
                next, "Compare a mesma carga e o alcance da amostra; confirme relógios antes de associar eventos a frametimes.",
                "Nenhuma configuração foi alterada.", Context: knownContext ? context!.Trim() : "Contexto não informado",
                ExpectedImpact: "Benefício não medido; somente interpretação de durações registradas.")
        };
        findings.AddRange(recurrence);
        if (partial)
            findings.Add(new("dpc-partial", "DPC/ISR", "Cobertura DPC/ISR parcial", coverage + " " + string.Join(" ", quality),
                FindingCategory.InsufficientData, Severity.Information, Confidence.Low, [new(coverage, "Kernel ETW")],
                next, "Eventos perdidos ou rejeitados não são reconstruídos por suposição.", "Nenhuma configuração foi alterada."));
        return new(classification, label, summary, next, confidence, coverage, findings, details, relations, partial);
    }

    public static string DescribeCoverage(EtwCaptureResult capture)
    {
        var counts = capture.EventCounts;
        var detail = counts is null
            ? $"Total ETW: {capture.EventsSeen}; contagens separadas indisponíveis neste resultado antigo."
            : $"Total ETW: {counts.TotalEtwEvents}; DPC (inclui timer/threaded): {counts.DpcEvents}; ISR: {counts.IsrEvents}; auxiliares: {counts.AuxiliaryEvents}; " +
                $"rejeitados por CPU/duração inválida: {counts.RejectedLatencyEvents}; analisados: {counts.AnalyzedLatencyEvents}; sem módulo conhecido: {counts.UnmappedLatencyEvents}; timeline retida: {counts.TimelineEventsRetained}.";
        return detail + $" Perdidos pela fonte: {capture.EventsLost}; timeline truncada: {(capture.TimelineTruncated ? "sim" : "não")}; " +
            $"limite atingido: {(capture.LimitReached ? "sim" : "não")}; cancelado: {(capture.Cancelled ? "sim" : "não")}; estado: {capture.Status}.";
    }

    private static IEnumerable<Finding> RecurringFindings(EtwCaptureResult capture, LatencyEvent[] timed, string coverage, bool partial)
    {
        var candidates = timed.Where(e => !string.IsNullOrWhiteSpace(e.Module) && e.DurationMicroseconds >= RecurrenceMarkerMicroseconds)
            .GroupBy(e => (Module: e.Module!.Replace('/', '\\').ToUpperInvariant(), e.Kind));
        foreach (var group in candidates)
        {
            var events = group.ToArray();
            if (events.Length < 3 || events.Select(e => (long)(e.TraceRelativeMilliseconds!.Value / 1000)).Distinct().Count() < 2) continue;
            var name = group.Key.Module[(group.Key.Module.LastIndexOf('\\') + 1)..];
            yield return new("dpc-recurring:" + group.Key.Module + ":" + group.Key.Kind, "DPC/ISR", "Durações recorrentes para investigar · " + name,
                $"Foram retidos {events.Length} eventos {group.Key.Kind} de pelo menos 1.000 µs em duas ou mais janelas de um segundo. Esse é um critério de triagem, não um limite de defeito nem prova de input lag. {coverage}",
                FindingCategory.PossibleProblem, Severity.Warning, Confidence.Low,
                events.OrderByDescending(e => e.DurationMicroseconds).Take(3).Select(e => new Evidence(
                    $"{e.Kind}; CPU {e.Cpu}; módulo {e.Module}; t={e.TraceRelativeMilliseconds:0.###} ms", "Kernel ETW / TraceEvent",
                    "dpc.cpu." + e.Cpu + "." + e.Kind.ToLowerInvariant() + ".duration.us", e.DurationMicroseconds, "µs", e.EndedAtUtc)).ToArray(),
                "Verifique a versão e o comportamento do driver durante a carga antes de mudar configurações. Repita a captura.",
                partial ? "A cobertura é parcial; recorrência descreve somente eventos retidos." : "Compare distribuição e recorrência sob a mesma carga, confirmando o contexto do módulo.",
                "Nenhuma configuração foi alterada.");
        }
    }

    private static IEnumerable<string> QualityIssues(EtwCaptureResult capture, IReadOnlyList<LatencyKindResult> summaries,
        IReadOnlyList<LatencyEvent> valid, IReadOnlyList<LatencyEvent> timed)
    {
        if (capture.EventCounts is null) yield return "Contagens separadas da origem não estão disponíveis para confirmar completude.";
        if (!CountsStructurallyValid(capture.EventCounts)) yield return "Contagens da origem são inconsistentes ou negativas.";
        if (capture.CpuSummaries.Any(s => !ValidAggregate(s))) yield return "Há agregados inválidos; somente grupos válidos foram utilizados.";
        if (capture.CpuSummaries.GroupBy(s => (s.Cpu, s.Kind)).Any(g => g.Count() > 1))
            yield return "Há grupos CPU/tipo duplicados; os totais não confirmam uma população única de eventos.";
        if ((capture.Timeline?.Count ?? 0) != valid.Count) yield return "A timeline contém eventos com tipo, CPU ou duração inválida.";
        if (valid.Count != timed.Count) yield return "Há eventos sem posição temporal válida dentro da duração registrada.";
        if (capture.EventsLost < 0 || capture.EventsSeen < 0) yield return "Contagens de eventos/perdas são negativas.";
        if (capture.EventCounts is { } counts)
        {
            if (counts.TotalEtwEvents != capture.EventsSeen) yield return "Total ETW diverge da contagem da origem.";
            if (summaries.Sum(s => (decimal)(s.AnalyzedCount ?? 0)) != counts.AnalyzedLatencyEvents)
                yield return "Agregados por CPU não cobrem a quantidade de eventos analisados pela origem.";
            if (counts.TimelineEventsRetained != valid.Count) yield return "A quantidade retida não coincide com os eventos válidos disponíveis.";
            if (!capture.TimelineTruncated && counts.AnalyzedLatencyEvents != valid.Count)
                yield return "A timeline não cobre todos os eventos analisados e a lacuna não foi marcada como truncamento.";
            if (counts.RejectedLatencyEvents == 0 &&
                (summaries.Single(s => s.Kind == "DPC").AnalyzedCount.GetValueOrDefault() != counts.DpcEvents ||
                 summaries.Single(s => s.Kind == "ISR").AnalyzedCount.GetValueOrDefault() != counts.IsrEvents))
                yield return "As contagens DPC/ISR divergem entre a origem e os agregados por tipo.";
        }
        if (!capture.TimelineTruncated)
        {
            var retainedGroups = valid.GroupBy(e => (e.Cpu, e.Kind)).ToDictionary(g => g.Key, g => g.ToArray());
            foreach (var aggregate in capture.CpuSummaries.Where(ValidAggregate))
            {
                if (!retainedGroups.TryGetValue((aggregate.Cpu, aggregate.Kind), out var events) || aggregate.Count != events.LongLength)
                {
                    yield return $"A quantidade de eventos da CPU {aggregate.Cpu}/{aggregate.Kind} diverge entre timeline completa e agregados.";
                    continue;
                }
                var total = events.Sum(e => e.DurationMicroseconds);
                if (!double.IsFinite(total) || Math.Abs(total - aggregate.TotalMicroseconds) > Math.Max(1, aggregate.TotalMicroseconds) * 1e-9)
                    yield return $"A soma das durações da CPU {aggregate.Cpu}/{aggregate.Kind} diverge entre timeline completa e agregados.";
            }
        }
        foreach (var summary in summaries)
        {
            var events = valid.Where(e => summary.Kind == "ISR" ? e.Kind == "ISR" : e.Kind is "DPC" or "TimerDPC" or "ThreadedDPC").ToArray();
            if (events.Length == 0 || summary.MaximumMicroseconds is not { } maximum) continue;
            var retainedMaximum = events.Max(e => e.DurationMicroseconds);
            if (retainedMaximum > maximum + Math.Max(1, maximum) * 1e-9)
                yield return $"O máximo {summary.Kind} retido excede o máximo dos agregados; a distribuição é inconsistente.";
            if (!capture.TimelineTruncated && summary.AnalyzedCount == events.Length &&
                Math.Abs(retainedMaximum - maximum) > Math.Max(1, maximum) * 1e-9)
                yield return $"O máximo {summary.Kind} não coincide entre timeline completa e agregados.";
        }
    }

    private static bool CountsStructurallyValid(EtwEventCounts? counts) => counts is null ||
        counts.TotalEtwEvents >= 0 && counts.DpcEvents >= 0 && counts.IsrEvents >= 0 && counts.AuxiliaryEvents >= 0 &&
        counts.RejectedLatencyEvents >= 0 && counts.AnalyzedLatencyEvents >= 0 && counts.UnmappedLatencyEvents >= 0 && counts.TimelineEventsRetained >= 0 &&
        (decimal)counts.DpcEvents + counts.IsrEvents + counts.AuxiliaryEvents == counts.TotalEtwEvents &&
        (decimal)counts.AnalyzedLatencyEvents + counts.RejectedLatencyEvents == (decimal)counts.DpcEvents + counts.IsrEvents &&
        counts.UnmappedLatencyEvents <= counts.AnalyzedLatencyEvents && counts.TimelineEventsRetained <= counts.AnalyzedLatencyEvents;
    private static bool IsPartial(EtwCaptureResult capture) => capture.EventsLost > 0 || capture.TimelineTruncated ||
        capture.LimitReached || capture.Cancelled || capture.Status != Availability.Available || capture.EventCounts?.RejectedLatencyEvents > 0;
    private static bool ValidKind(string kind) => kind is "DPC" or "TimerDPC" or "ThreadedDPC" or "ISR";
    private static bool ValidEvent(LatencyEvent e) => ValidKind(e.Kind) && e.Cpu is >= 0 and <= 65_535 &&
        double.IsFinite(e.DurationMicroseconds) && e.DurationMicroseconds >= 0;
    private static bool ValidAggregate(LatencySummary s) => LatencyResultPresentation.IsValidAggregate(s);
    private static bool ValidPosition(double? value, TimeSpan duration) => value is { } t && double.IsFinite(t) && t >= 0 &&
        t < TimeSpan.MaxValue.TotalMilliseconds && t <= duration.TotalMilliseconds;
    private static string Number(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);
}
