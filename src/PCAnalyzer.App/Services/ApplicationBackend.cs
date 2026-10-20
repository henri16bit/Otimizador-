using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using PCAnalyzer.Core;
using PCAnalyzer.Diagnostics;
using PCAnalyzer.Gaming;
using PCAnalyzer.Hardware;
using PCAnalyzer.Network;
using PCAnalyzer.Optimization;
using PCAnalyzer.Reports;
using PCAnalyzer.Storage;

namespace PCAnalyzer.App.Services;

public sealed partial class ApplicationBackend : IApplicationBackend
{
    private readonly WindowsHardwareDiscovery _discovery = new();
    private readonly WindowsTelemetryCollector _collector = new();
    private readonly NetworkInspector _network = new();
    private readonly SQLiteHistoryStore _history;
    private readonly JsonFileLogger _logger;
    private readonly ReversibleChangeEngine _changes;
    private readonly DiagnosticRuleEngine _rules = new();
    private readonly SemaphoreSlim _telemetryGate = new(1, 1);
    public string DataDirectory { get; }

    public ApplicationBackend()
    {
        DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCAnalyzer");
        _history = new SQLiteHistoryStore(Path.Combine(DataDirectory, "history.db"));
        _logger = new JsonFileLogger(Path.Combine(DataDirectory, "logs"));
        _changes = new ReversibleChangeEngine(_history);
    }

    public async Task<HardwareInventory> InitializeAsync(CancellationToken ct)
    {
        // Uma falha de histórico não deve impedir a descoberta passiva de hardware.
        try { await _history.InitializeAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { LogError("history.initialize", ex); }
        var inventory = await _discovery.DiscoverAsync(ct);
        var capabilities = inventory.Capabilities.ToList();
        capabilities.Add(new("Auxiliar privilegiado", File.Exists(WorkerPath) ? Availability.Available : Availability.ToolMissing,
            "Usado apenas para captura manual de DPC/ISR com UAC. Não altera configurações do Windows."));
        capabilities.Add(new("PresentMon", Availability.ToolMissing,
            "Captura opcional: selecione o executável oficial ao iniciar. Importação CSV disponível sem a ferramenta."));
        _lastInventory = inventory with { Capabilities = capabilities };
        return _lastInventory;
    }
    public async Task<TelemetryBatch> CollectAsync(TimeSpan interval, CancellationToken ct)
    {
        await _telemetryGate.WaitAsync(ct);
        try { return await _collector.CollectAsync(interval, ct); }
        finally { _telemetryGate.Release(); }
    }
    public IReadOnlyList<Finding> Analyze(HardwareInventory inventory, IReadOnlyList<MetricSample> samples) => _rules.Analyze(inventory, samples);
    public async Task SaveSessionAsync(DiagnosticSession session, CancellationToken ct) =>
        await _history.SaveSessionAsync(await WithChangesAsync(session, ct), ct);
    public Task<IReadOnlyList<SessionSummary>> ListSessionsAsync(CancellationToken ct) => _history.ListSessionsAsync(100, ct);
    public Task<DiagnosticSession?> LoadSessionAsync(Guid id, CancellationToken ct) => _history.LoadSessionAsync(id, ct);
    public Task SaveTestAsync(TestSession session, CancellationToken ct = default) => _history.SaveTestAsync(session, ct);
    public Task<TestSession?> LoadTestAsync(Guid id, CancellationToken ct = default) => _history.LoadTestAsync(id, ct);
    public Task<IReadOnlyList<TestRunSummary>> ListTestsAsync(int limit = 100, CancellationToken ct = default) => _history.ListTestsAsync(limit, ct);
    public async Task ExportAsync(DiagnosticSession session, string path, string format, bool anonymize, CancellationToken ct) =>
        await ReportExporter.ExportAsync(await WithChangesAsync(session, ct), path, Enum.Parse<ReportFormat>(format, true), anonymize, ct);
    private async Task<DiagnosticSession> WithChangesAsync(DiagnosticSession session, CancellationToken ct)
    {
        if (session.Changes is not null) return session;
        var changes = await _history.ListChangesAsync(ct);
        return session with { Changes = changes.Where(c => c.CreatedAt >= session.StartedAt && c.CreatedAt <= (session.EndedAt ?? DateTimeOffset.UtcNow)
            || c.UpdatedAt >= session.StartedAt && c.UpdatedAt <= (session.EndedAt ?? DateTimeOffset.UtcNow)).ToArray() };
    }

    public async Task<TechnicalResult> CompareAsync(Guid before, Guid after, CancellationToken ct)
    {
        var a = await _history.LoadSessionAsync(before, ct) ?? throw new InvalidOperationException("Sessão anterior não encontrada.");
        var b = await _history.LoadSessionAsync(after, ct) ?? throw new InvalidOperationException("Sessão posterior não encontrada.");
        var result = await Task.Run(() => SessionComparer.Compare(a, b), ct);
        var lines = new List<string> { result.Interpretation,
            "Condições documentadas equivalentes: " + (result.ConditionsComparable ? "sim; diferenças ainda descritivas" : "não") };
        lines.AddRange(result.Warnings.Select(w => "Aviso: " + w));
        lines.AddRange(result.Metrics.Select(m => $"{m.Key}: n={m.BeforeSampleCount}/{m.AfterSampleCount}; média {N(m.BeforeMean)} → {N(m.AfterMean)} {m.Unit}; Δ={N(m.MeanDelta)}; p95 {N(m.BeforeP95)} → {N(m.AfterP95)}; {m.Limitation}"));
        return new("Performance Comparison", lines);
    }

    public async Task<TechnicalResult> PingAsync(string host, Guid? adapterId, IProgress<TestProgress> progress, CancellationToken ct)
    {
        if (host.Trim().Equals("gateway", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = await _network.DiscoverAsync(ct);
            host = GatewaySelection.Select(snapshot, adapterId);
        }
        progress.Report(new("Resolvendo destino e iniciando ICMP"));
        var result = await new ManualPingService().RunAsync(host, 20, 1500, ct, new InlineProgress<PingObservation>(o =>
            progress.Report(new($"Pacote {o.Sequence} de 20", o.Sequence / 20d, o.Sequence,
                o.RoundTripMilliseconds is { } rtt ? $"Última resposta: {rtt:0.###} ms" : "Sem resposta válida: " + o.Status))));
        await SaveArtifactAsync("ping", result, CancellationToken.None);
        var lines = new List<string> {
            $"Destino: {result.Host}; endereço resolvido: {result.ResolvedAddress ?? "indisponível"}. Rota escolhida pelo Windows.",
            $"Pacotes concluídos: {result.SentCount}; respostas: {result.ReceivedCount}; ausência de resposta ICMP: {N(result.LossPercent)}%",
            $"RTT min/média/max: {N(result.MinimumRttMs)}/{N(result.AverageRttMs)}/{N(result.MaximumRttMs)} ms; p95: {N(result.P95RttMs)} ms",
            $"Jitter: {N(result.JitterMs)} ms. {PingMeasurement.JitterDefinition}", result.Limitation,
            $"Cancelado: {result.Cancelled}; resultados locais preservados." };
        lines.AddRange(result.Observations.Select(o => $"#{o.Sequence} {o.Timestamp:O}: {o.Status}, RTT={N(o.RoundTripMilliseconds)} ms {o.Error}"));
        var metrics = result.Observations.Select(o => new MetricSample("network.ping.rtt.ms", "Rede", o.RoundTripMilliseconds,
            "ms", "System.Net.NetworkInformation.Ping", "ICMP Echo; RTT do Windows, resolução em ms", o.Timestamp,
            TimeSpan.Zero, o.RoundTripMilliseconds.HasValue ? Availability.Available : Availability.ReadError,
            Limitation: result.Limitation)).ToArray();
        return new("Teste manual de rede", lines, metrics,
            [new("Resposta do destino (ICMP)", "ms", result.Observations.Select(o => new RelativePoint((o.Timestamp - result.StartedAt).TotalMilliseconds, o.RoundTripMilliseconds)).ToArray(),
                "Milissegundos desde o início do teste", "System.Net.NetworkInformation.Ping")],
            [new("network.ping.rtt.ms", "RTT médio", result.AverageRttMs, "ms", "Ida e volta ao destino selecionado"),
             new("network.ping.jitter.ms", "Variação (jitter)", result.JitterMs, "ms", PingMeasurement.JitterDefinition),
             new("network.ping.loss.percent", "Sem resposta ICMP", result.LossPercent, "%", "Não comprova perda dentro do jogo"),
             new("network.ping.responses", "Respostas recebidas", result.ReceivedCount, "pacotes", $"{result.SentCount} pacotes concluídos")],
            Findings: PingInterpretation.Analyze(result), Limitations: [result.Limitation],
            Availability: result.SentCount == 0 ? Availability.ReadError : Availability.Available, Cancelled: result.Cancelled,
            ComparisonKey: result.ResolvedAddress is { } ip ? "icmp:" + ip + ":20:32bytes:1500ms" : null);
    }

    public Task<NetworkSnapshot> DiscoverNetworkAsync(CancellationToken ct) => _network.DiscoverAsync(ct);
    public Task<AdapterDetail> InspectNetworkAsync(Guid id, CancellationToken ct) => _network.InspectAdapterAsync(id, ct);
    public IReadOnlyList<NetworkSettingView> NetworkSettingsFor(AdapterDetail detail) =>
        detail.AdvancedProperties.Select(property => new NetworkSettingView(detail.InterfaceGuid.ToString("D"), detail.Name,
            property.RegistryKeyword, string.IsNullOrWhiteSpace(property.DisplayName) ? property.RegistryKeyword : property.DisplayName,
            NetworkPropertyOperation.SerializeValues(property.RegistryValues),
            [],
            $"{property.Function}\n{property.Consequences}\n{property.Recommendation}\n{property.ExpectedImpact}\nDriver: {detail.DriverVersion ?? "indisponível"}. Consulta somente leitura. O valor persistido não comprova efeito ou benefício.",
            property.DisplayValue ?? string.Join(", ", property.RegistryValues),
            "Somente leitura · configuração informada pelo driver")
        ).ToArray();

    public Task<IReadOnlyList<SelectionItem>> ListPowerPlansAsync(CancellationToken ct) => Task.Run<IReadOnlyList<SelectionItem>>(() =>
        PowerPlans.List().Select(p => new SelectionItem(p.Id.ToString("D"), p.Name + (p.IsActive ? " (ativo)" : ""))).ToArray(), ct);
    public async Task<string> PreviewPowerPlanAsync(string id, CancellationToken ct)
    {
        var preview = await _changes.PreviewAsync(new PowerPlanOperation(), Guid.Parse(id).ToString("D"), ct);
        return Describe(preview) + "\nHipótese de comparação, sem melhoria comprovada.\nUm plano diferente pode aumentar consumo, temperatura ou ruído. Esta versão não modifica o plano.";
    }
    public Task<ChangeRecord> ApplyPowerPlanAsync(string id, CancellationToken ct) =>
        Task.FromException<ChangeRecord>(AuditOnlyPolicy.ModificationNotSupported());
    public async Task<string> PreviewNetworkChangeAsync(NetworkSettingView setting, string value, CancellationToken ct)
    {
        var preview = await _changes.PreviewAsync(NetworkOperation(setting), value, ct);
        return $"Adaptador: {setting.AdapterName}\nPropriedade: {setting.Name}\nAtual exibido: {setting.CurrentDisplayValue}\n" + Describe(preview) +
            "\nHipótese de comparação. Benefício exige medição; nenhuma configuração será alterada.\n" + setting.Explanation;
    }
    public Task<ChangeRecord> ApplyNetworkChangeAsync(NetworkSettingView setting, string value, CancellationToken ct) =>
        Task.FromException<ChangeRecord>(AuditOnlyPolicy.ModificationNotSupported());
    public Task<IReadOnlyList<ChangeRecord>> ListChangesAsync(CancellationToken ct) => _history.ListChangesAsync(ct);
    public Task<ChangeRecord> RestoreChangeAsync(Guid id, CancellationToken ct) =>
        Task.FromException<ChangeRecord>(AuditOnlyPolicy.ModificationNotSupported());

    public async Task<TechnicalResult> ImportFramesAsync(string path, PresentMonParseOptions options, CancellationToken ct)
    {
        var result = await new PresentMonCsvParser().ImportFileAsync(path, options, ct);
        await SaveArtifactAsync("frames", result, ct);
        return DescribeFrames(result);
    }
    public async Task<TechnicalResult> CaptureFramesAsync(string executablePath, int processId, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(DataDirectory, "captures"));
        var csv = Path.Combine(DataDirectory, "captures", "frames-" + Guid.NewGuid().ToString("N") + ".csv");
        var result = await new PresentMonCaptureService().CaptureAsync(
            new(executablePath, processId, TimeSpan.FromSeconds(30), csv), ct);
        if (result.Import is not null)
        {
            await SaveArtifactAsync("frames", result.Import, CancellationToken.None);
            var description = DescribeFrames(result.Import);
            return description with { Lines = new[] { result.Detail, "CSV real preservado localmente. Nenhuma garantia de compatibilidade com Vanguard." }.Concat(description.Lines).ToArray(),
                Availability = result.Status, Cancelled = result.Cancelled, Partial = !result.Import.Complete };
        }
        return new("Captura de jogo sem resultado válido", [result.Status + ": " + result.Detail, result.Tool?.Detail ?? "Ferramenta não validada",
            "Não existe captura válida para apresentar."], Availability: result.Status, Limitations: [result.Detail], Cancelled: result.Cancelled);
    }
    private static TechnicalResult DescribeFrames(PresentMonImportResult result)
    {
        var lines = new List<string> { $"Registros: {result.RecordsRead}; inválidos: {result.InvalidRecords}; filtrados: {result.FilteredRecords}; completo: {result.Complete}",
            "Cada PID, swapchain, tipo de frame e segmento é analisado separadamente; tempos relativos não são automaticamente correlacionados a telemetria UTC.",
            "FPS = 1000/média de frametimes válidos. Low = 1000/média dos ceil(n×fração) frametimes mais lentos (1% e 0,1%). Percentis R-7." };
        lines.AddRange(result.Warnings);
        foreach (var stream in result.Streams)
        {
            var s = stream.ApplicationStatistics; var d = stream.DisplayedStatistics;
            lines.Add($"{stream.Identity.Application} PID={stream.Identity.ProcessId}, swapchain={stream.Identity.SwapChainAddress}, segmento={stream.Identity.Segment}, tipo={stream.Identity.FrameType ?? "não informado"}");
            lines.Add($"Aplicação: n={s.SampleCount}, FPS={N(s.AverageFps)}, frametime médio={N(s.MeanFrameTimeMilliseconds)} ms; p95/p99/p99,9={N(s.P95FrameTimeMilliseconds)}/{N(s.P99FrameTimeMilliseconds)}/{N(s.P999FrameTimeMilliseconds)} ms; lows={N(s.OnePercentLowFps)}/{N(s.PointOnePercentLowFps)} FPS");
            lines.Add($"Apresentação: n={d.SampleCount}, FPS={N(d.AverageFps)}, p99={N(d.P99FrameTimeMilliseconds)} ms; descartados={stream.DroppedFrameCount}; estado de apresentação desconhecido={stream.UnknownDisplayStateCount}");
            lines.AddRange(stream.Limitations); lines.AddRange(s.Limitations); lines.AddRange(d.Limitations);
        }
        var series = result.Streams.Take(16).Select(stream => new RelativeSeries(
            $"{stream.Identity.Application} PID {stream.Identity.ProcessId} / {stream.Identity.SwapChainAddress} / segmento {stream.Identity.Segment}",
            "ms", stream.Frames.TakeLast(10_000).Select(f => new RelativePoint(f.TimestampMilliseconds, f.FrameTimeMilliseconds)).ToArray(),
            stream.TimestampBasis, stream.DurationSource + "; frames de aplicação, incluindo descartados quando marcados pelo CSV; últimas 10.000 amostras para visualização")).ToArray();
        var metrics = result.Streams.SelectMany(stream => stream.Frames.TakeLast(10_000).Where(f => FrameInterpretation.UtcTimestamp(stream, f).HasValue)
            .Select(f => new MetricSample($"frames.{stream.Identity.ProcessId}.{stream.Identity.SwapChainAddress}.{stream.Identity.Segment}.frametime.ms", "Jogos", f.FrameTimeMilliseconds,
                "ms", "PresentMon CSV", stream.DurationSource, FrameInterpretation.UtcTimestamp(stream, f)!.Value, TimeSpan.Zero,
                f.FrameTimeMilliseconds.HasValue ? Availability.Available : Availability.InsufficientData, Limitation: stream.TimestampBasis))).Take(30_000).ToArray();
        var indicators = result.Streams.Take(16).SelectMany(stream =>
        {
            var s = stream.ApplicationStatistics;
            var id = $"{stream.Identity.Application}.{stream.Identity.ProcessId}.{stream.Identity.SwapChainAddress}.{stream.Identity.Segment}";
            return new ResultIndicator[] {
                new("frames." + id + ".fps", "FPS médio · " + stream.Identity.Application, s.AverageFps, "FPS", "Derivado: 1000 / média dos frametimes válidos; aplicação"),
                new("frames." + id + ".p99.ms", "Frametime p99", s.P99FrameTimeMilliseconds, "ms", $"PID {stream.Identity.ProcessId} · swapchain {stream.Identity.SwapChainAddress} · segmento {stream.Identity.Segment}"),
                new("frames." + id + ".low1.fps", "1% low", s.OnePercentLowFps, "FPS", "1000 / média dos 1% intervalos mais lentos; não é percentil de FPS") };
        }).ToArray();
        return new("Análise de captura PresentMon", lines, metrics, series, indicators, FrameInterpretation.Analyze(result),
            result.Warnings.Concat(["Streams permanecem separadas. Até 16 streams aparecem nos indicadores e gráficos; o texto inclui todas. Até 30.000 intervalos UTC participam da correlação; o JSON bruto preserva a captura retida completa.",
                metrics.Length > 0 ? "UTC veio da coluna de data/hora com offset explicitamente informado." : "Sem âncora UTC conhecida: correlação temporal indisponível; o horário da importação não será usado como horário dos frames."]).ToArray(),
            result.ErrorDetail is not null ? Availability.ReadError : result.Streams.Any(s => s.ApplicationStatistics.SampleCount > 0) ? Availability.Available : Availability.InsufficientData,
            Cancelled: result.Cancelled,
            ComparisonKey: "frames:" + string.Join("|", result.Streams.Select(s => s.Identity.Application + ":" + s.DurationSource + ":" + s.Identity.FrameType).Distinct().Order()), Partial: !result.Complete);
    }
    public async Task<TechnicalResult> CaptureDpcAsync(CancellationToken ct)
    {
        var response = await RunPrivilegedAsync(new(PrivilegedAction.CaptureDpc, DateTimeOffset.UtcNow), ct);
        var result = JsonSerializer.Deserialize<EtwCaptureResult>(response.PayloadJson ?? "null")
            ?? throw new InvalidOperationException("O auxiliar não retornou um resultado ETW válido.");
        await SaveArtifactAsync("dpc-isr", result, CancellationToken.None);
        return DescribeEtw(result);
    }
    public async Task<TechnicalResult> ImportEtlAsync(string path, CancellationToken ct)
    {
        var result = await new EtwTraceService().ImportAsync(path, ct);
        await SaveArtifactAsync("dpc-isr", result, ct);
        return DescribeEtw(result);
    }
    private static TechnicalResult DescribeEtw(EtwCaptureResult result)
    {
        var lines = new List<string> { result.Status + ": " + result.Detail,
            $"Duração={result.Duration.TotalSeconds:F2} s; eventos vistos={result.EventsSeen}; perdidos={result.EventsLost}; cancelado={result.Cancelled}; limite={result.LimitReached}",
            "DPC/ISR mede execução no kernel; não mede input lag físico e picos isolados não comprovam defeito." };
        lines.AddRange(LatencyResultPresentation.Details(result));
        foreach (var group in result.CpuSummaries.Concat(result.ModuleSummaries))
            lines.Add($"CPU={group.Cpu}; {group.Kind}; módulo={group.Module ?? "não atribuído"}; n={group.Count}; média={group.MeanMicroseconds:F3} µs; máximo={group.MaximumMicroseconds:F3} µs; p95/p99={N(group.P95Microseconds)}/{N(group.P99Microseconds)} µs; n percentis={group.PercentileSampleCount}");
        if (result.Timeline is { } timeline)
        {
            lines.Add($"Linha do tempo retida: {timeline.Count} eventos; truncada={result.TimelineTruncated}; base={result.TimelineTimeBasis}");
            lines.AddRange(timeline.Take(100).Select(e => $"t={e.TraceRelativeMilliseconds:F3} ms; {e.Kind}; CPU={e.Cpu}; duração={e.DurationMicroseconds:F3} µs; módulo={e.Module ?? "não atribuído"}; UTC={e.EndedAtUtc:O}"));
            if (timeline.Count > 100) lines.Add("A tela resume os primeiros 100 eventos; resultado JSON local preserva a linha do tempo retida completa.");
        }
        lines.AddRange(result.Limitations);
        var metrics = result.Timeline?.Where(e => e.EndedAtUtc.HasValue).TakeLast(2000).Select(e => new MetricSample(
            "dpc.cpu." + e.Cpu + "." + e.Kind.ToLowerInvariant() + ".duration.us", "DPC/ISR", e.DurationMicroseconds,
            "µs", "Kernel ETW / TraceEvent", "ElapsedTimeMSec × 1000; instante final do evento",
            e.EndedAtUtc!.Value, TimeSpan.Zero, Limitation: "Evento individual; não representa input lag. " + result.TimelineTimeBasis)).ToArray();
        var series = (result.Timeline ?? []).Where(e => e.TraceRelativeMilliseconds.HasValue).GroupBy(e => e.Kind)
            .Select(group => new RelativeSeries(group.Key + " · duração por evento", "µs",
                group.TakeLast(10_000).Select(e => new RelativePoint(e.TraceRelativeMilliseconds!.Value, e.DurationMicroseconds)).ToArray(),
                result.TimelineTimeBasis, "Kernel ETW / TraceEvent; ordem temporal original, eventos individuais")).ToArray();
        return new("DPC/ISR — captura real", lines, metrics, series, LatencyResultPresentation.Indicators(result),
            LatencyInterpretation.Analyze(result), result.Limitations, result.Status, result.Cancelled, ComparisonKey: "kernel-etw:dpc-isr",
            Partial: result.EventsLost > 0 || result.LimitReached || result.TimelineTruncated || result.EventCounts?.RejectedLatencyEvents > 0);
    }

    public async Task<TechnicalResult> RunLabAsync(string test, string? path, IProgress<double> progress, CancellationToken ct)
    {
        if (test == "memory-access")
        {
            var memory = await MemoryAccessBenchmark.RunAsync(await Task.Run(WindowsTelemetryCollector.ReadAvailablePhysicalMemoryBytes, ct), progress, ct);
            await SaveArtifactAsync("memory-access", memory, CancellationToken.None);
            return new("Acesso e transferência de memória", memory.Measurements.Select(m =>
                $"{m.BufferMiB} MiB · {m.Operation}: {N(m.MiBPerSecond)} MiB/s; {N(m.NanosecondsPerAccess)} ns/acesso; {m.DurationSeconds:0.######} s.").ToArray(),
                Indicators: memory.Measurements.Select(m => new ResultIndicator($"memory-access.{m.BufferMiB}.{m.Operation}",
                    $"{m.Operation} · {m.BufferMiB} MiB", m.MiBPerSecond ?? m.NanosecondsPerAccess,
                    m.MiBPerSecond.HasValue ? "MiB/s" : "ns/acesso", "Inclui CPU, cache, TLB e memória virtual; não isola DRAM.")).ToArray(),
                Limitations: memory.Limitations, Cancelled: memory.Cancelled, Partial: memory.Cancelled,
                ComparisonKey: "memory-access:v1:1-16-64MiB:4passes:1048576hops");
        }
        var result = test.ToLowerInvariant() switch
        {
            "cpu" or "cpu-single" => await ControlledTests.CpuAsync(progress, ct),
            "ram" => await ControlledTests.MemoryAsync(progress, ct,
                await Task.Run(WindowsTelemetryCollector.ReadAvailablePhysicalMemoryBytes, ct)),
            "leitura" or "disk" or "nvme" or "disk-read" => await ControlledTests.ReadAsync(path ?? throw new ArgumentException("Selecione um arquivo existente."), progress, ct),
            _ => throw new ArgumentException("Teste não implementado. GPU/stress/gravação não são iniciados.")
        };
        await SaveArtifactAsync("lab", result, ct);
        return new("Teste concluído — " + result.Name, new[] { $"Duração={result.Duration.TotalSeconds:F3} s; operações={result.Operations}; bytes={result.Bytes}; taxa={N(result.ThroughputMiBPerSecond)} MiB/s" }.Concat(result.Limitations).ToArray(),
            Indicators: [new("lab.duration.seconds", "Duração medida", result.Duration.TotalSeconds, "s", "Tempo real da carga; preparação não incluída"),
                new("lab.operations", "Operações concluídas", result.Operations, "operações", result.Name),
                new("lab.bytes", "Bytes processados", test == "cpu" ? null : result.Bytes, "bytes", "Somente a carga de memória/leitura usa este contador"),
                new("lab.throughput", "Taxa derivada", result.ThroughputMiBPerSecond, "MiB/s", "Bytes / duração; inclui influência de CPU e cache")], Limitations: result.Limitations,
            ComparisonKey: "lab:" + test + ":" + (path is null ? "fixed-workload" : Path.GetFullPath(path) + ":" + result.Bytes));
    }
    private NetworkPropertyOperation NetworkOperation(NetworkSettingView setting) =>
        new(_network, Guid.Parse(setting.AdapterId), setting.Keyword);
    private static string Describe(ChangePreview preview) =>
        $"Tipo: {preview.Kind}\nComponente: {preview.Target}\nOriginal: {preview.Before}\nAlternativa para comparação externa: {preview.After}\nRisco: {preview.Risk}\nSomente consulta: nenhuma mudança será executada.\n{preview.RecoveryInstructions}";
    private Task SaveArtifactAsync<T>(string prefix, T value, CancellationToken ct) => Task.Run(async () =>
    {
        var directory = Path.Combine(DataDirectory, "results"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, prefix + "-" + Guid.NewGuid().ToString("N") + ".json");
        var temporary = path + ".tmp";
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(output, value, new JsonSerializerOptions { WriteIndented = true }, ct);
                await output.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }, ct);
    private static string N(double? value) => value.HasValue ? value.Value.ToString("F3", CultureInfo.CurrentCulture) : "indisponível";
    public void LogError(string operation, Exception exception) => _logger.TryLog(LogLevel.Error, operation, "Falha na operação", exception);

    private static string WorkerPath => Path.Combine(AppContext.BaseDirectory, "worker", "PCAnalyzer.Worker.exe");
    private static async Task<PrivilegedResponse> RunPrivilegedAsync(PrivilegedRequest request, CancellationToken ct)
    {
        AuditOnlyPolicy.ValidatePrivilegedAction(request.Action);
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || !File.Exists(WorkerPath))
            throw new InvalidOperationException("Auxiliar Windows ausente. Execute a publicação completa, incluindo a pasta worker.");
        var id = Guid.NewGuid();
        Directory.CreateDirectory(PrivilegedProtocol.RequestDirectory);
        var requestPath = PrivilegedProtocol.RequestPath(id);
        var responsePath = PrivilegedProtocol.ResponsePath(id);
        await File.WriteAllTextAsync(requestPath, JsonSerializer.Serialize(request), ct);
        try
        {
            var info = new ProcessStartInfo(WorkerPath) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
            info.ArgumentList.Add("--request"); info.ArgumentList.Add(id.ToString("N"));
            using var process = Process.Start(info) ?? throw new InvalidOperationException("O Windows não iniciou o auxiliar.");
            using var registration = ct.Register(() =>
            {
                try { File.WriteAllText(PrivilegedProtocol.CancellationPath(id), "cancel"); }
                catch (IOException) { }
            });
            await process.WaitForExitAsync(CancellationToken.None);
            if (!File.Exists(responsePath)) throw new InvalidOperationException("O auxiliar encerrou sem resposta. Consulte o log técnico da captura antes de repetir.");
            var response = JsonSerializer.Deserialize<PrivilegedResponse>(await File.ReadAllTextAsync(responsePath))
                ?? throw new InvalidOperationException("Resposta inválida do auxiliar.");
            if (!response.Success) throw new InvalidOperationException(response.Detail);
            return response;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("Elevação UAC recusada; a captura não foi iniciada.", ex, ct); }
        finally
        {
            foreach (var path in new[] { requestPath, responsePath, PrivilegedProtocol.CancellationPath(id) })
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    public async ValueTask DisposeAsync() { await _collector.DisposeAsync(); _telemetryGate.Dispose(); }
}
