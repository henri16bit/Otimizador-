using System.Diagnostics;

namespace PCAnalyzer.Core;

public sealed record DiagnosticOptions(int ObservationSeconds = 30, bool IncludePing = false,
    bool IncludeLatency = false, bool Gaming = false, string PingTarget = "gateway", Guid? AdapterId = null,
    string Workload = "Uso atual do computador", string? PresentMonPath = null, int? ProcessId = null,
    string? WaitForProcessName = null, int WaitForProcessSeconds = 120)
{
    public void Validate()
    {
        if (ObservationSeconds is < 1 or > 120) throw new ArgumentOutOfRangeException(nameof(ObservationSeconds));
        if (WaitForProcessSeconds is < 1 or > 300) throw new ArgumentOutOfRangeException(nameof(WaitForProcessSeconds));
    }
}

/// <summary>Platform boundaries of one analysis. No stress or configuration-changing operation exists here.</summary>
public interface IDiagnosticProbe
{
    HardwareInventory? LatestInventory { get; }
    Task<HardwareInventory> PrepareAsync(DiagnosticOptions options, IProgress<TestProgress> progress, CancellationToken ct);
    Task<TelemetryBatch> CollectAsync(TimeSpan interval, CancellationToken ct);
    Task<TechnicalResult> ReadNetworkAsync(DiagnosticOptions options, bool final, CancellationToken ct);
    Task<TechnicalResult> PingAsync(DiagnosticOptions options, IProgress<TestProgress> progress, CancellationToken ct);
    Task<TechnicalResult> CaptureLatencyAsync(CancellationToken ct);
    Task<TechnicalResult> CaptureFramesAsync(DiagnosticOptions options, CancellationToken ct);
}

/// <summary>Coordinates real sources, keeps partial evidence and builds a single report.</summary>
public sealed class DiagnosticOrchestrator(IDiagnosticProbe probe)
{
    public async Task<TechnicalResult> RunAsync(DiagnosticOptions options, IProgress<TestProgress> progress,
        Action<TelemetryBatch>? publishBatch, CancellationToken ct)
    {
        options.Validate();
        var started = DateTimeOffset.UtcNow;
        var inventory = new HardwareInventory(started, [], []);
        var stages = new List<DiagnosticStage>();
        var pieces = new List<TechnicalResult>();
        var samples = new List<MetricSample>();
        var processes = new List<ProcessSample>();
        var limits = new List<string>();
        var synchronization = new object();
        var cancelled = false;
        var preparationStarted = DateTimeOffset.UtcNow;
        progress.Report(new("Preparando fontes e permissões", Detail: "O registro local já foi preparado. Nenhuma configuração será alterada."));
        try
        {
            inventory = await probe.PrepareAsync(options, progress, ct).ConfigureAwait(false);
            stages.Add(new("preparation", "Preparação", Availability.Available,
                "Fontes identificadas; recursos opcionais serão avaliados separadamente.", preparationStarted, DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            cancelled = true;
            stages.Add(new("preparation", "Preparação", Availability.InsufficientData,
                "Preparação cancelada; nenhuma captura opcional foi iniciada.", preparationStarted, DateTimeOffset.UtcNow));
        }
        catch (Exception error)
        {
            stages.Add(new("preparation", "Preparação", Availability.ReadError, error.Message, preparationStarted, DateTimeOffset.UtcNow));
            limits.Add("Identificação incompleta. As fontes básicas independentes ainda serão consultadas.");
        }

        async Task StageAsync(string id, string name, Func<Task<TechnicalResult>> action)
        {
            var stageStart = DateTimeOffset.UtcNow;
            TechnicalResult result;
            try { result = await action().ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            { result = new(name, ["Etapa cancelada; nenhuma conclusão inferida."], Availability: Availability.InsufficientData, Cancelled: true); }
            catch (Exception error)
            { result = new(name, [error.Message], Availability: error is UnauthorizedAccessException ? Availability.PermissionRequired : Availability.ReadError); }
            lock (synchronization)
            {
                pieces.Add(result);
                stages.Add(new(id, name, result.Cancelled || result.Partial ? Availability.InsufficientData : result.Availability,
                    string.Join(" ", result.Limitations?.Take(2) ?? result.Lines.Take(2)), stageStart, DateTimeOffset.UtcNow));
            }
        }

        if (!cancelled)
        {
            var network = StageAsync("network-start", "Identificação da conexão", () => probe.ReadNetworkAsync(options, false, ct));
            var jobs = new List<Task> { network };
            if (options.IncludeLatency) jobs.Add(StageAsync("latency", "Drivers e interrupções", () => probe.CaptureLatencyAsync(ct)));
            else lock (synchronization) stages.Add(new("latency", "Drivers e interrupções", Availability.InsufficientData,
                "Captura aprofundada não selecionada. Ative a opção de drivers para solicitar UAC e capturar 30 s.", started, started));
            if (options.IncludePing) jobs.Add(StageAsync("ping", "Resposta da conexão", () => probe.PingAsync(options, new InlineProgress<TestProgress>(_ => { }), ct)));
            if (options.Gaming) jobs.Add(StageAsync("frames", "Quadros do jogo", () => probe.CaptureFramesAsync(options, ct)));
            var observationStart = DateTimeOffset.UtcNow;
            var clock = Stopwatch.StartNew();
            var failures = new List<string>();
            try
            {
                do
                {
                    ct.ThrowIfCancellationRequested();
                    progress.Report(new("Observando o computador", Math.Min(.8, .8 * clock.Elapsed.TotalSeconds / options.ObservationSeconds),
                        Detail: $"{Math.Min(options.ObservationSeconds, (int)clock.Elapsed.TotalSeconds)} de {options.ObservationSeconds} s · CPU, RAM, GPU, discos e processos disponíveis."));
                    using var sampleCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    Task<TelemetryBatch>? pendingCollection = null;
                    try
                    {
                        // A stalled native source does not create an unbounded queue of retries.
                        pendingCollection = probe.CollectAsync(TimeSpan.FromSeconds(1), sampleCancellation.Token);
                        var batch = await pendingCollection
                            .WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                        samples.AddRange(batch.Metrics); processes.AddRange(batch.Processes);
                        if (samples.Count > 60_000) { samples.RemoveRange(0, samples.Count - 60_000); limits.Add("Telemetria limitada às últimas 60.000 amostras."); }
                        if (processes.Count > 10_000) { processes.RemoveRange(0, processes.Count - 10_000); limits.Add("Processos limitados aos últimos 10.000 registros."); }
                        publishBatch?.Invoke(batch);
                    }
                    catch (TimeoutException)
                    { sampleCancellation.Cancel(); failures.Add("Uma chamada de coleta excedeu cinco segundos; nenhuma chamada adicional foi enfileirada."); break; }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception error) { failures.Add(error.Message); }
                    finally
                    {
                        // WaitAsync bounds the wait, not the native operation. Observe a late fault without
                        // retaining the session or waiting for a source that ignores cancellation.
                        if (pendingCollection is not null)
                            _ = pendingCollection.ContinueWith(static task => _ = task.Exception,
                                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default);
                    }
                    var remaining = TimeSpan.FromSeconds(options.ObservationSeconds) - clock.Elapsed;
                    if (remaining > TimeSpan.Zero) await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                } while (clock.Elapsed.TotalSeconds < options.ObservationSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { cancelled = true; }
            var hasMeasurements = samples.Any(s => s.Status == Availability.Available && s.Value is { } v && double.IsFinite(v));
            lock (synchronization) stages.Add(new("observation", "Observação do sistema",
                failures.Count == 0 && hasMeasurements && !cancelled ? Availability.Available : Availability.InsufficientData,
                failures.Count > 0 ? string.Join("; ", failures.Distinct()) : cancelled ?
                    "Observação cancelada; as amostras já obtidas foram preservadas e a cobertura restante é desconhecida." :
                    hasMeasurements ? "Amostras reais preservadas com fonte, intervalo e horário." : "Nenhuma fonte retornou medidas válidas.", observationStart, DateTimeOffset.UtcNow));
            progress.Report(new("Concluindo as capturas selecionadas", Detail: "Aguardando as fontes e liberando os recursos. Você pode cancelar."));
            await Task.WhenAll(jobs).ConfigureAwait(false);
            if (!ct.IsCancellationRequested)
                await StageAsync("network", "Comportamento da conexão", () => probe.ReadNetworkAsync(options, true, ct)).ConfigureAwait(false);
        }

        cancelled |= ct.IsCancellationRequested || pieces.Any(p => p.Cancelled);
        progress.Report(new("Interpretando evidências e preparando relatório"));
        inventory = probe.LatestInventory ?? inventory;
        samples.AddRange(pieces.SelectMany(p => p.Metrics ?? []));
        var additional = pieces.SelectMany(p => p.Findings ?? []).Concat(TemporalDiagnosticAnalyzer.Analyze(samples)).DistinctBy(f => f.Id).ToArray();
        var report = new DiagnosticAnalysisEngine().Analyze(inventory, samples, processes, additional,
            stages.OrderBy(s => s.StartedAt).ToArray(), cancelled);
        report = report with { Limitations = report.Limitations.Concat(limits).Concat(pieces.SelectMany(p => p.Limitations ?? [])).Distinct().ToArray() };
        var title = options.Gaming ? "Análise de desempenho em jogos" : "Análise do computador";
        var session = new DiagnosticSession(Guid.NewGuid(), title, started, DateTimeOffset.UtcNow,
            new(options.Workload, "Diagnóstico coordenado", 1,
                $"Observação solicitada: {options.ObservationSeconds} s; ICMP: {options.IncludePing}; ETW: {options.IncludeLatency}; frames: {options.Gaming}."),
            inventory, samples.ToArray(), report.Findings, Processes: processes.ToArray());
        var sourceIdentity = string.Join("|", pieces.Select(p => p.ComparisonKey).Where(k => !string.IsNullOrEmpty(k))
            .Distinct().OrderBy(k => k, StringComparer.Ordinal));
        return new(title, new[] { report.Summary }.Concat(stages.Select(s => $"{s.Name}: {s.Detail}")).ToArray(),
            samples, pieces.SelectMany(p => p.Series ?? []).ToArray(), pieces.SelectMany(p => p.Indicators ?? []).ToArray(),
            report.Findings, report.Limitations,
            report.Completion == DiagnosticCompletion.Failed ? Availability.ReadError : Availability.Available,
            cancelled, session, $"analysis:v2:{options.ObservationSeconds}:{options.IncludePing}:{options.IncludeLatency}:{options.Gaming}:{options.PingTarget}:{options.AdapterId}:{options.ProcessId}:{options.WaitForProcessName}:{sourceIdentity}",
            report, report.Completion == DiagnosticCompletion.Partial);
    }
}
