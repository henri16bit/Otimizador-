namespace PCAnalyzer.Core;

/// <summary>Independent read-only providers. A timed-out native call is never queued again while pending.</summary>
public sealed class PerformanceAuditCoordinator
{
    private sealed class Source(IPerformanceAuditProvider provider)
    {
        public IPerformanceAuditProvider Provider { get; } = provider;
        public Task<IReadOnlyList<AuditObservation>>? Pending { get; set; }
        public object Gate { get; } = new();
    }

    private readonly Source[] _sources;
    private readonly TimeSpan _timeout;
    private readonly Action<string, Exception>? _log;

    public PerformanceAuditCoordinator(IEnumerable<IPerformanceAuditProvider> providers,
        TimeSpan? timeout = null, Action<string, Exception>? log = null)
    {
        _sources = providers.Select(p => new Source(p)).ToArray();
        _timeout = timeout ?? TimeSpan.FromSeconds(30);
        if (_timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        _log = log;
        var ids = _sources.SelectMany(s => s.Provider.Rules).Select(r => r.Id).ToArray();
        if (ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ArgumentException("O catálogo possui identificadores de regras duplicados.", nameof(providers));
    }

    public async Task<PerformanceAuditReport> RunAsync(IProgress<TestProgress> progress, CancellationToken ct)
    {
        progress.Report(new("Etapa A · Inventário de configurações", 0, 0,
            $"{_sources.Length} fontes; as consultas terminam ao concluir ou atingir o limite de {_timeout.TotalSeconds:0} s por fonte."));
        var completedSources = 0;
        long completedChecks = 0;
        var progressGate = new object();
        var observations = await Task.WhenAll(_sources.Select(async source =>
        {
            var values = await ReadAsync(source, ct).ConfigureAwait(false);
            // Publish the counters together: independently incremented counters could report
            // the final fraction before a slower callback publishes an earlier value.
            lock (progressGate)
            {
                completedChecks += values.Count;
                completedSources++;
                progress.Report(new("Etapa A · Fonte concluída: " + SourceName(source.Provider),
                    (double)completedSources / _sources.Length, completedChecks,
                    $"{completedSources}/{_sources.Length} fontes encerradas; {completedChecks} verificações com estado registrado. Leituras ausentes não contam como sucesso."));
            }
            return values;
        })).ConfigureAwait(false);
        return PerformanceAuditEngine.Evaluate(_sources.SelectMany(s => s.Provider.Rules).ToArray(),
            observations.SelectMany(o => o).ToArray(), DateTimeOffset.UtcNow);
    }

    private static string SourceName(IPerformanceAuditProvider provider) => provider.GetType().Name switch
    {
        "WindowsConfigurationAuditProvider" => "Windows e processos",
        "NvidiaDriverAuditProvider" => "Perfis NVIDIA",
        "DeviceInterruptAuditProvider" => "Dispositivos e interrupções",
        "NetworkConfigurationAuditProvider" => "Rede",
        "StorageMemoryAuditProvider" => "Memória e armazenamento",
        _ => "Configurações"
    };

    private async Task<IReadOnlyList<AuditObservation>> ReadAsync(Source source, CancellationToken ct)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(_timeout);
        Task<IReadOnlyList<AuditObservation>> pending;
        lock (source.Gate)
        {
            if (source.Pending is { IsCompleted: false })
                return Missing(source, Availability.Collecting, "A consulta anterior ainda está encerrando. Nenhuma nova consulta foi enfileirada.");
            if (ct.IsCancellationRequested)
                return Missing(source, Availability.InsufficientData, "Consulta cancelada antes de iniciar.");
            var token = bounded.Token;
            pending = source.Pending = Task.Run(async () => await source.Provider.ReadAsync(token).ConfigureAwait(false), token);
        }
        try
        {
            var values = await pending.WaitAsync(bounded.Token).ConfigureAwait(false);
            // An empty provider response cannot silently count as successful coverage.
            if (values.Count == 0) return Missing(source, Availability.InsufficientData, "A fonte não retornou valores verificáveis.");
            var missing = source.Provider.Rules.Where(r => !values.Any(o => o.RuleId == r.Id))
                .Select(r => Unavailable(r, Availability.InsufficientData, "Esta verificação não retornou dados nesta coleta."));
            return values.Concat(missing).ToArray();
        }
        catch (OperationCanceledException)
        {
            _ = pending.ContinueWith(t => _log?.Invoke("audit.late", t.Exception!), CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return Missing(source, Availability.InsufficientData, ct.IsCancellationRequested
                ? "Consulta cancelada; resultados das fontes já concluídas foram preservados."
                : "A leitura excedeu o limite de tempo. A fonte permanece indisponível nesta sessão.");
        }
        catch (Exception error)
        {
            _log?.Invoke("audit." + source.Provider.GetType().Name, error);
            return Missing(source, error is UnauthorizedAccessException ? Availability.PermissionRequired : Availability.ReadError,
                error is UnauthorizedAccessException ? "Esta leitura exige permissão adicional; nenhuma elevação foi iniciada automaticamente."
                : "A fonte falhou ao consultar as configurações. O erro técnico foi registrado no log local.");
        }
    }

    private static AuditObservation[] Missing(Source source, Availability status, string reason) =>
        source.Provider.Rules.Select(rule => Unavailable(rule, status, reason)).ToArray();

    private static AuditObservation Unavailable(AuditRule rule, Availability status, string reason) =>
        new(rule.Id, rule.Id, rule.Name, "Sistema", null, status, rule.Method, new Dictionary<string, string>(), reason);
}
