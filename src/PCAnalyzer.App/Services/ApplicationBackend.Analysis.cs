using System.Diagnostics;
using System.IO;
using PCAnalyzer.Core;
using PCAnalyzer.Gaming;
using PCAnalyzer.Network;

namespace PCAnalyzer.App.Services;

public sealed partial class ApplicationBackend
{
    private volatile HardwareInventory? _lastInventory;
    public Task ResetTelemetryAsync(CancellationToken ct) => _collector.ResetAsync(ct);
    public Task<PresentMonToolInspection> InspectPresentMonAsync(string path, CancellationToken ct) => new PresentMonCaptureService().InspectAsync(path, ct);
    public async Task<TechnicalResult> AnalyzePcAsync(DiagnosticOptions options, IProgress<TestProgress> progress,
        Action<TelemetryBatch> publishBatch, CancellationToken ct)
    {
        var result = await new DiagnosticOrchestrator(new AnalysisProbe(this)).RunAsync(options, progress, publishBatch, ct);
        if (result.Session is { } session)
        {
            try { result = result with { Session = await WithChangesAsync(session, CancellationToken.None) }; }
            catch (Exception error)
            {
                LogError("analysis.change-context", error);
                const string limitation = "O diário de alterações não pôde ser lido; a comparação não conhece todas as mudanças.";
                result = result with
                {
                    Limitations = (result.Limitations ?? []).Append(limitation).ToArray(),
                    Report = result.Report is { } report
                        ? report with { Limitations = report.Limitations.Append(limitation).ToArray() } : null
                };
            }
        }
        return result;
    }

    private sealed class AnalysisProbe(ApplicationBackend backend) : IDiagnosticProbe
    {
        private sealed record NetworkBaseline(NetworkSnapshot Snapshot, NetworkAdapter? Adapter);
        private readonly object _networkGate = new();
        private Task<NetworkBaseline>? _networkBaseline;
        private AdapterDetail[] _details = [];
        private int? _gamePid;
        private string? _gameUnavailableReason;
        private PresentMonToolInspection? _tool;
        public HardwareInventory? LatestInventory => backend._lastInventory;
        public async Task<HardwareInventory> PrepareAsync(DiagnosticOptions options, IProgress<TestProgress> progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (options.Gaming)
            {
                progress.Report(new("Verificando a integração de jogos", Detail: "Assinatura, origem e compatibilidade do PresentMon."));
                _tool = await backend.InspectPresentMonAsync(options.PresentMonPath ?? "", ct);
                _gamePid = options.ProcessId;
                if (_tool.Status == Availability.Available && _gamePid is null && !string.IsNullOrWhiteSpace(options.WaitForProcessName))
                {
                    var name = options.WaitForProcessName.Trim();
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                    if (name.Length is 0 or > 128 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        throw new ArgumentException("Informe apenas o nome do processo do jogo, sem pasta ou comando.");
                    var clock = Stopwatch.StartNew();
                    while (clock.Elapsed.TotalSeconds < options.WaitForProcessSeconds)
                    {
                        ct.ThrowIfCancellationRequested();
                        var candidates = Process.GetProcessesByName(name);
                        try
                        {
                            if (candidates.Length == 1) { _gamePid = candidates[0].Id; break; }
                            if (candidates.Length > 1) throw new InvalidOperationException("Há mais de um processo com esse nome. Escolha o PID na lista e repita.");
                        }
                        finally { foreach (var candidate in candidates) candidate.Dispose(); }
                        progress.Report(new("Aguardando você abrir o jogo", Detail: $"{name} · limite de {options.WaitForProcessSeconds} s; clique em Cancelar para encerrar a espera."));
                        await Task.Delay(1000, ct);
                    }
                    if (_gamePid is null)
                        _gameUnavailableReason = $"O jogo '{name}' não apareceu em {options.WaitForProcessSeconds} s. Abra o jogo e repita, ou confira o nome do processo. Nenhuma captura de quadros foi iniciada.";
                }
            }
            return backend._lastInventory ?? new(DateTimeOffset.UtcNow, [],
                [new("Inventário", Availability.Collecting, "A identificação ainda está em andamento; CPU/RAM e outras fontes independentes continuam disponíveis.")]);
        }
        public Task<TelemetryBatch> CollectAsync(TimeSpan interval, CancellationToken ct) => backend.CollectAsync(interval, ct);
        private Task<NetworkBaseline> InitialNetworkAsync(DiagnosticOptions options, CancellationToken ct)
        {
            // Network inspection and ICMP begin in parallel. Both must keep the same
            // initial choice even if the adapter list or default gateway changes later.
            lock (_networkGate) return _networkBaseline ??= DiscoverInitialNetworkAsync(options, ct);
        }
        private async Task<NetworkBaseline> DiscoverInitialNetworkAsync(DiagnosticOptions options, CancellationToken ct)
        {
            var snapshot = await backend.DiscoverNetworkAsync(ct);
            return new(snapshot, NetworkDiagnosticAnalyzer.SelectActive(snapshot, options.AdapterId));
        }
        public async Task<TechnicalResult> ReadNetworkAsync(DiagnosticOptions options, bool final, CancellationToken ct)
        {
            var initial = await InitialNetworkAsync(options, ct);
            if (!final)
            {
                var selected = initial.Adapter;
                if (selected is not null) _details = [await backend.InspectNetworkAsync(selected.InterfaceGuid, ct)];
                return new("Conexão identificada", [selected is null ? "Nenhuma conexão ativa utilizável." : selected.Name + " · " + selected.Description],
                    Availability: selected is null && initial.Snapshot.Status == Availability.Available
                        ? Availability.InsufficientData : initial.Snapshot.Status,
                    Limitations: initial.Snapshot.Limitation is { } reason ? [reason] : [],
                    ComparisonKey: "network:" + selected?.InterfaceGuid);
            }
            var after = await backend.DiscoverNetworkAsync(ct);
            var findings = NetworkDiagnosticAnalyzer.Analyze(initial.Snapshot, after, _details,
                options.AdapterId ?? initial.Adapter?.InterfaceGuid);
            return new("Análise da conexão", findings.Select(f => f.Explanation).ToArray(), Findings: findings,
                Limitations: _details.SelectMany(d => d.Limitations).ToArray(), Availability: after.Status,
                ComparisonKey: "network:" + initial.Adapter?.InterfaceGuid);
        }
        public async Task<TechnicalResult> PingAsync(DiagnosticOptions options, IProgress<TestProgress> progress, CancellationToken ct)
        {
            var initial = await InitialNetworkAsync(options, ct);
            var destination = options.PingTarget;
            if (destination.Trim().Equals("gateway", StringComparison.OrdinalIgnoreCase))
            {
                if (initial.Adapter is null)
                    return new("Gateway não identificado", ["A leitura inicial não identificou a conexão escolhida ativa. O teste não mudou para outra interface."],
                        Availability: initial.Snapshot.Status == Availability.Available ? Availability.InsufficientData : initial.Snapshot.Status,
                        Limitations: initial.Snapshot.Limitation is { } reason ? [reason] : []);
                try { destination = GatewaySelection.Select(initial.Snapshot, initial.Adapter.InterfaceGuid); }
                catch (InvalidOperationException error)
                { return new("Gateway não identificado", [error.Message], Availability: Availability.InsufficientData); }
            }
            var result = await backend.PingAsync(destination, options.AdapterId ?? initial.Adapter?.InterfaceGuid, progress, ct);
            return initial.Adapter is not { } adapter ? result : result with
            {
                Lines = new[] { $"Conexão de referência no início: {adapter.Name} · {adapter.InterfaceGuid:D}. O ICMP segue a rota escolhida pelo Windows, que pode mudar durante o teste." }
                    .Concat(result.Lines).ToArray()
            };
        }
        public Task<TechnicalResult> CaptureLatencyAsync(CancellationToken ct) => backend.CaptureDpcAsync(ct);
        public Task<TechnicalResult> CaptureFramesAsync(DiagnosticOptions options, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (_tool?.Status != Availability.Available) return Task.FromResult(new TechnicalResult("Integração de jogos indisponível",
                [_tool?.Detail ?? "Configure o PresentMon para capturar quadros."], Availability: _tool?.Status ?? Availability.ToolMissing));
            if (_gamePid is not { } pid) return Task.FromResult(new TechnicalResult("Jogo não identificado",
                [_gameUnavailableReason ?? "Escolha um processo em execução ou informe o nome para aguardar sua abertura."], Availability: Availability.InsufficientData));
            return backend.CaptureFramesAsync(options.PresentMonPath!, pid, ct);
        }
    }
}
