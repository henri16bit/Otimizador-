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

public sealed class ApplicationBackend : IApplicationBackend
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
            "Usado apenas em ações manuais com UAC. A interface roda como usuário normal."));
        capabilities.Add(new("PresentMon", Availability.ToolMissing,
            "Captura opcional: selecione o executável oficial ao iniciar. Importação CSV disponível sem a ferramenta."));
        return inventory with { Capabilities = capabilities };
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

    public async Task<TechnicalResult> PingAsync(string host, CancellationToken ct)
    {
        if (host.Trim().Equals("gateway", StringComparison.OrdinalIgnoreCase))
        {
            var snapshot = await _network.DiscoverAsync(ct);
            host = snapshot.Adapters.Where(a => a.Status == "Up").SelectMany(a => a.Gateways).FirstOrDefault()
                ?? throw new InvalidOperationException("Nenhum gateway foi identificado no adaptador ativo.");
        }
        var result = await new ManualPingService().RunAsync(host, 20, 1500, ct);
        await SaveArtifactAsync("ping", result, CancellationToken.None);
        var lines = new List<string> {
            $"Pacotes concluídos: {result.SentCount}; respostas: {result.ReceivedCount}; ausência de resposta ICMP: {N(result.LossPercent)}%",
            $"RTT min/média/max: {N(result.MinimumRttMs)}/{N(result.AverageRttMs)}/{N(result.MaximumRttMs)} ms; p95: {N(result.P95RttMs)} ms",
            $"Jitter: {N(result.JitterMs)} ms. {PingMeasurement.JitterDefinition}", result.Limitation,
            $"Cancelado: {result.Cancelled}; resultados locais preservados." };
        lines.AddRange(result.Observations.Select(o => $"#{o.Sequence} {o.Timestamp:O}: {o.Status}, RTT={N(o.RoundTripMilliseconds)} ms {o.Error}"));
        var metrics = result.Observations.Select(o => new MetricSample("network.ping.rtt.ms", "Rede", o.RoundTripMilliseconds,
            "ms", "System.Net.NetworkInformation.Ping", "ICMP Echo; RTT do Windows, resolução em ms", o.Timestamp,
            TimeSpan.Zero, o.RoundTripMilliseconds.HasValue ? Availability.Available : Availability.ReadError,
            Limitation: result.Limitation)).ToArray();
        return new("Teste manual de rede", lines, metrics);
    }

    public async Task<IReadOnlyList<NetworkSettingView>> ListNetworkSettingsAsync(CancellationToken ct)
    {
        var snapshot = await _network.DiscoverAsync(ct);
        var result = new List<NetworkSettingView>();
        foreach (var adapter in snapshot.Adapters)
        {
            ct.ThrowIfCancellationRequested();
            var detail = await _network.InspectAdapterAsync(adapter.InterfaceGuid, ct);
            result.Add(new(adapter.InterfaceGuid.ToString("D"), adapter.Name, "", "Identificação / RSS / estatísticas", "", [],
                $"{detail.Description}\nInterfaceGuid: {detail.InterfaceGuid:D}\nDriver: {detail.DriverInformation}, versão {detail.DriverVersion}, data {detail.DriverDate}\nLink: {detail.LinkSpeed ?? adapter.SpeedBitsPerSecond?.ToString() ?? "indisponível"}\n" +
                string.Join("\n", detail.Rss.Concat(detail.Statistics).Select(f => f.Name + ": " + (f.Value ?? "indisponível") + " [" + f.Source + "] " + f.Limitation)) +
                "\n" + string.Join("\n", detail.Limitations)));
            foreach (var property in detail.AdvancedProperties)
            {
                var values = property.CanChange ? property.ValidRegistryValues.Select((v, i) => new SelectionItem(
                    NetworkPropertyOperation.SerializeValue(v), i < property.ValidDisplayValues.Count ? property.ValidDisplayValues[i] + " [" + v + "]" : v)).ToArray() : [];
                result.Add(new(adapter.InterfaceGuid.ToString("D"), adapter.Name, property.RegistryKeyword, property.DisplayName,
                    NetworkPropertyOperation.SerializeValues(property.RegistryValues), values,
                    $"{property.Function}\n{property.Consequences}\n{property.Recommendation}\n{property.ExpectedImpact}\nDriver: {detail.DriverVersion ?? "indisponível"}. -NoRestart: gravar não comprova ativação; pode exigir reinício manual."));
            }
            if (detail.AdvancedProperties.Count == 0)
                result.Add(new(adapter.InterfaceGuid.ToString("D"), adapter.Name, "", "Propriedades indisponíveis", "", [],
                    string.Join("; ", detail.Limitations)));
        }
        return result;
    }

    public Task<IReadOnlyList<SelectionItem>> ListPowerPlansAsync(CancellationToken ct) => Task.Run<IReadOnlyList<SelectionItem>>(() =>
        PowerPlans.List().Select(p => new SelectionItem(p.Id.ToString("D"), p.Name + (p.IsActive ? " (ativo)" : ""))).ToArray(), ct);
    public async Task<string> PreviewPowerPlanAsync(string id, CancellationToken ct) =>
        Describe(await _changes.PreviewAsync(new PowerPlanOperation(), Guid.Parse(id).ToString("D"), ct)) +
        "\nUm plano diferente pode aumentar consumo, temperatura ou ruído. Benefício em jogos não é garantido; compare antes/depois.";
    public Task<ChangeRecord> ApplyPowerPlanAsync(string id, CancellationToken ct) => _changes.ApplyAsync(
        new ElevatedOperation(new PowerPlanOperation(), (value, original) => new(PrivilegedAction.SetPowerPlan,
            DateTimeOffset.UtcNow, Guid.Parse(value), Value: value, ExpectedOriginal: original)), Guid.Parse(id).ToString("D"), ct);
    public async Task<string> PreviewNetworkChangeAsync(NetworkSettingView setting, string value, CancellationToken ct) =>
        Describe(await _changes.PreviewAsync(NetworkOperation(setting), value, ct)) + "\n" + setting.Explanation;
    public Task<ChangeRecord> ApplyNetworkChangeAsync(NetworkSettingView setting, string value, CancellationToken ct) =>
        _changes.ApplyAsync(ElevateNetwork(NetworkOperation(setting)), value, ct);
    public Task<IReadOnlyList<ChangeRecord>> ListChangesAsync(CancellationToken ct) => _history.ListChangesAsync(ct);
    public async Task<ChangeRecord> RestoreChangeAsync(Guid id, CancellationToken ct)
    {
        var change = await _history.LoadChangeAsync(id, ct) ?? throw new InvalidOperationException("Registro de alteração não encontrado.");
        IReversibleOperation operation;
        if (change.Kind == "active-power-plan")
            operation = new ElevatedOperation(new PowerPlanOperation(), (value, original) => new(PrivilegedAction.SetPowerPlan,
                DateTimeOffset.UtcNow, Guid.Parse(value), Value: value, ExpectedOriginal: original));
        else if (change.Kind == "network-advanced-property")
        {
            var parts = change.Target.Split(':', 2);
            if (parts.Length != 2) throw new InvalidOperationException("Destino de rede inválido no histórico.");
            operation = ElevateNetwork(new NetworkPropertyOperation(_network, Guid.Parse(parts[0]), parts[1]));
        }
        else throw new InvalidOperationException("O tipo de alteração não possui restauração implementada.");
        return await _changes.RestoreAsync(id, operation, ct);
    }

    public async Task<TechnicalResult> ImportFramesAsync(string path, CancellationToken ct)
    {
        var result = await new PresentMonCsvParser().ImportFileAsync(path, cancellationToken: ct);
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
            return description with { Lines = new[] { result.Detail, "CSV real preservado localmente. Nenhuma garantia de compatibilidade com Vanguard." }.Concat(description.Lines).ToArray() };
        }
        return new("Game Session Analyzer", [result.Status + ": " + result.Detail, result.Tool?.Detail ?? "Ferramenta não validada",
            "Não existe captura válida para apresentar. Nenhum dado foi inventado."]);
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
        var series = result.Streams.Select(stream => new RelativeSeries(
            $"{stream.Identity.Application} PID {stream.Identity.ProcessId} / {stream.Identity.SwapChainAddress} / segmento {stream.Identity.Segment}",
            "ms", stream.Frames.TakeLast(10_000).Select(f => new RelativePoint(f.TimestampMilliseconds, f.FrameTimeMilliseconds)).ToArray(),
            stream.TimestampBasis, stream.DurationSource + "; frames de aplicação, incluindo descartados quando marcados pelo CSV; últimas 10.000 amostras para visualização")).ToArray();
        return new("Análise de captura PresentMon", lines, Series: series);
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
        return new("DPC/ISR — captura real", lines, metrics);
    }

    public async Task<TechnicalResult> RunLabAsync(string test, string? path, IProgress<double> progress, CancellationToken ct)
    {
        var result = test.ToLowerInvariant() switch
        {
            "cpu" or "cpu-single" => await ControlledTests.CpuAsync(progress, ct),
            "ram" => await ControlledTests.MemoryAsync(progress, ct,
                await Task.Run(WindowsTelemetryCollector.ReadAvailablePhysicalMemoryBytes, ct)),
            "leitura" or "disk" or "nvme" or "disk-read" => await ControlledTests.ReadAsync(path ?? throw new ArgumentException("Selecione um arquivo existente."), progress, ct),
            _ => throw new ArgumentException("Teste não implementado. GPU/stress/gravação não são iniciados.")
        };
        await SaveArtifactAsync("lab", result, ct);
        return new("Test Lab — " + result.Name, new[] { $"Duração={result.Duration.TotalSeconds:F3} s; operações={result.Operations}; bytes={result.Bytes}; taxa={N(result.ThroughputMiBPerSecond)} MiB/s" }.Concat(result.Limitations).ToArray());
    }
    private NetworkPropertyOperation NetworkOperation(NetworkSettingView setting) =>
        new(_network, Guid.Parse(setting.AdapterId), setting.Keyword);
    private static IReversibleOperation ElevateNetwork(NetworkPropertyOperation operation) =>
        new ElevatedOperation(operation, (value, original) => new(PrivilegedAction.SetAdapterProperty,
            DateTimeOffset.UtcNow, operation.InterfaceGuid, operation.RegistryKeyword, value, original));
    private static string Describe(ChangePreview preview) =>
        $"Tipo: {preview.Kind}\nComponente: {preview.Target}\nOriginal: {preview.Before}\nDestino: {preview.After}\nRisco: {preview.Risk}\nBackup: será persistido antes da escrita.\nRestauração: {preview.RecoveryInstructions}";
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
            // Não matar operações de configuração durante a escrita: aguardar confirmação real.
            using var registration = request.Action == PrivilegedAction.CaptureDpc
                ? ct.Register(() => { try { File.WriteAllText(PrivilegedProtocol.CancellationPath(id), "cancel"); } catch (IOException) { } })
                : default;
            await process.WaitForExitAsync(CancellationToken.None);
            if (!File.Exists(responsePath)) throw new InvalidOperationException("O auxiliar encerrou sem resposta. Releia o estado antes de repetir; o backup está no histórico.");
            var response = JsonSerializer.Deserialize<PrivilegedResponse>(await File.ReadAllTextAsync(responsePath))
                ?? throw new InvalidOperationException("Resposta inválida do auxiliar.");
            if (!response.Success) throw new InvalidOperationException(response.Detail);
            return response;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { throw new OperationCanceledException("Elevação UAC recusada; verifique o histórico da operação.", ex, ct); }
        finally
        {
            foreach (var path in new[] { requestPath, responsePath, PrivilegedProtocol.CancellationPath(id) })
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    private sealed class ElevatedOperation(IReversibleOperation inner, Func<string, string, PrivilegedRequest> request) : IExpectedValueOperation
    {
        public string Kind => inner.Kind;
        public string Target => inner.Target;
        public ChangeRisk Risk => inner.Risk;
        public string RecoveryInstructions => inner.RecoveryInstructions;
        public Task<string> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct);
        public Task ValidateAsync(string value, CancellationToken ct) => inner.ValidateAsync(value, ct);
        public Task WriteAsync(string value, CancellationToken ct) =>
            throw new InvalidOperationException("Operações elevadas exigem o valor esperado do backup preparado.");
        public async Task WriteExpectedAsync(string value, string expectedCurrent, CancellationToken ct) =>
            await RunPrivilegedAsync(request(value, expectedCurrent), ct);
    }
    public async ValueTask DisposeAsync() { await _collector.DisposeAsync(); _telemetryGate.Dispose(); }
}
