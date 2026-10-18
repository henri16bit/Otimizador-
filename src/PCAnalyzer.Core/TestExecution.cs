namespace PCAnalyzer.Core;

public enum TestStatus { NotStarted, Preparing, Running, Completed, Failed, Cancelled, Partial }
public sealed record RelativePoint(double TimeMilliseconds, double? Value);
public sealed record RelativeSeries(string Name, string Unit, IReadOnlyList<RelativePoint> Points, string TimeBasis, string Source);
public sealed record ResultIndicator(string Id, string Name, double? Value, string Unit, string Detail);
public sealed record TechnicalResult(string Title, IReadOnlyList<string> Lines, IReadOnlyList<MetricSample>? Metrics = null,
    IReadOnlyList<RelativeSeries>? Series = null, IReadOnlyList<ResultIndicator>? Indicators = null,
    IReadOnlyList<Finding>? Findings = null, IReadOnlyList<string>? Limitations = null,
    Availability Availability = Availability.Available, bool Cancelled = false, DiagnosticSession? Session = null,
    string? ComparisonKey = null, DiagnosticReport? Report = null, bool Partial = false,
    PerformanceAuditReport? Audit = null);
public sealed record TestProgress(string Stage, double? Fraction = null, long? CompletedUnits = null,
    string? Detail = null);
public sealed record TestSession(Guid Id, string TestId, string Name, TestStatus Status, DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt, TestProgress Progress, TechnicalResult? Result = null, string? Error = null,
    string? PersistenceError = null)
{
    public bool IsActive => Status is TestStatus.Preparing or TestStatus.Running;
}
public sealed record TestRunSummary(Guid Id, string TestId, string Name, TestStatus Status,
    DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? Summary);

public interface ITestHistoryStore
{
    Task SaveTestAsync(TestSession session, CancellationToken ct = default);
    Task<TestSession?> LoadTestAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<TestRunSummary>> ListTestsAsync(int limit = 100, CancellationToken ct = default);
}

/// <summary>A single immutable execution stream. UI subscribers marshal notifications to their dispatcher.</summary>
public sealed class TestExecutionService(ITestHistoryStore history, Action<string, Exception>? log = null)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private TestSession? _current;
    private bool _running;
    public event Action<TestSession>? Changed;
    public TestSession? Current { get { lock (_gate) return _current; } }
    public bool IsRunning { get { lock (_gate) return _running; } }
    public void Cancel() { lock (_gate) _cancellation?.Cancel(); }

    // Call only after the application's per-user instance lock is acquired.
    public async Task RecoverInterruptedAsync(CancellationToken ct = default)
    {
        if (IsRunning) throw new InvalidOperationException("Não recupere registros enquanto um teste estiver ativo.");
        foreach (var summary in await history.ListTestsAsync(1000, ct).ConfigureAwait(false))
        {
            if (summary.Status is not (TestStatus.Preparing or TestStatus.Running)) continue;
            var run = await history.LoadTestAsync(summary.Id, ct).ConfigureAwait(false);
            if (run is not { IsActive: true }) continue;
            await history.SaveTestAsync(run with { Status = TestStatus.Failed, EndedAt = null,
                Progress = new("Execução anterior sem encerramento registrado"),
                Error = "O aplicativo foi encerrado antes de confirmar o término. A duração final e o resultado são desconhecidos; repita o diagnóstico manualmente." }, ct).ConfigureAwait(false);
        }
    }

    public async Task<TestSession> ExecuteAsync(TestDefinition definition,
        Func<IProgress<TestProgress>, CancellationToken, Task<TechnicalResult>> run, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (!definition.Implemented) throw new InvalidOperationException(definition.Limitations);
        CancellationTokenSource cancellation;
        lock (_gate)
        {
            if (_running) throw new InvalidOperationException("Aguarde ou cancele o diagnóstico em andamento.");
            _running = true;
            _cancellation = cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _current = new(Guid.NewGuid(), definition.Id, definition.Name, TestStatus.Preparing,
                DateTimeOffset.UtcNow, null, new("Preparando recursos e registro local"));
        }
        try
        {
            Publish();
            // Initial persistence failure prevents a test from starting without a recoverable execution record.
            await history.SaveTestAsync(Current!, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            Change(s => s with { Status = TestStatus.Running, Progress = new("Executando diagnóstico") });
            await history.SaveTestAsync(Current!, cancellation.Token).ConfigureAwait(false);
            var id = Current!.Id;
            var progress = new InlineProgress<TestProgress>(value =>
            {
                lock (_gate)
                {
                    if (_current?.Id != id || _current.Status != TestStatus.Running) return;
                    var fraction = value.Fraction is { } f && double.IsFinite(f) && f is >= 0 and <= 1 ? f : (double?)null;
                    _current = _current with { Progress = value with { Fraction = fraction } };
                }
                Publish();
            });
            var result = await run(progress, cancellation.Token).ConfigureAwait(false);
            var status = result.Cancelled || cancellation.IsCancellationRequested ? TestStatus.Cancelled
                : result.Availability is Availability.ReadError or Availability.PermissionRequired or Availability.ToolMissing or Availability.Unsupported
                    ? TestStatus.Failed : result.Partial ? TestStatus.Partial : TestStatus.Completed;
            if (status == TestStatus.Cancelled)
                result = result with { Cancelled = true, Report = result.Report is { } report
                    ? report with { Completion = DiagnosticCompletion.Cancelled } : null };
            Change(s => s with { Status = status, EndedAt = DateTimeOffset.UtcNow, Result = result,
                Progress = new(status is TestStatus.Completed or TestStatus.Partial ? "Concluído; salvando resultado" : status == TestStatus.Cancelled ? "Cancelado; preservando resultados parciais" : "Falha reportada pela fonte"),
                Error = status == TestStatus.Failed ? result.Lines.FirstOrDefault() : null });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            Change(s => s with { Status = TestStatus.Cancelled, EndedAt = DateTimeOffset.UtcNow,
                Progress = new("Cancelado; recursos liberados"), Error = "Operação interrompida por solicitação. Nenhuma conclusão foi inferida." });
        }
        catch (Exception error)
        {
            log?.Invoke("test." + definition.Id, error);
            Change(s => s with { Status = TestStatus.Failed, EndedAt = DateTimeOffset.UtcNow,
                Progress = new("Falha na execução"), Error = $"{definition.Name}: {error.Message} [{error.GetType().Name}, 0x{error.HResult:X8}]" });
        }
        finally
        {
            try
            {
                var final = Current! with { Progress = Current!.Progress with { Stage = Current!.Status switch
                    { TestStatus.Completed => "Resultado salvo no histórico", TestStatus.Partial => "Análise parcial salva no histórico", TestStatus.Cancelled => "Cancelamento registrado no histórico", _ => "Falha registrada no histórico" } } };
                await history.SaveTestAsync(final, CancellationToken.None).ConfigureAwait(false);
                Change(_ => final);
            }
            catch (Exception error)
            {
                log?.Invoke("test.persist", error);
                Change(s => s with { PersistenceError = "Não foi possível salvar o registro. O resultado permanece nesta sessão; exporte antes de sair. " + error.GetType().Name,
                    Progress = s.Progress with { Stage = "Registro disponível somente nesta sessão" } });
            }
            lock (_gate) { _cancellation = null; _running = false; }
            cancellation.Dispose();
        }
        return Current!;
    }
    private void Change(Func<TestSession, TestSession> update)
    {
        lock (_gate) _current = update(_current!);
        Publish();
    }
    private void Publish() { var snapshot = Current; if (snapshot is not null) Changed?.Invoke(snapshot); }
}

/// <summary>Unlike Progress&lt;T&gt;, delivers measured progress synchronously without a captured UI context.</summary>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
