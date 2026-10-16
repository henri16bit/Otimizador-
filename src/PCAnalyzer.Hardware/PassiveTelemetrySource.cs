using System.Diagnostics;
using PCAnalyzer.Core;

namespace PCAnalyzer.Hardware;

internal interface IPassiveTelemetrySource : IDisposable
{
    IReadOnlyList<MetricSample> Read(CancellationToken cancellationToken);
}

/// <summary>
/// A native provider may ignore cancellation. Keep exactly one operation alive and never wait for it
/// on the basic collection path. Reset invalidates its result; disposal happens only after it exits.
/// All public methods are called under the collector's gate.
/// </summary>
internal sealed class PassiveTelemetrySource : IDisposable
{
    private readonly Func<IPassiveTelemetrySource> _factory;
    private readonly string _key, _component, _source;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _slowAfter;
    private IPassiveTelemetrySource? _reader;
    private Task<ReadResult>? _pending;
    private CancellationTokenSource? _cancellation;
    private IReadOnlyList<MetricSample> _last = [];
    private long _generation, _started;
    private int _failures;
    private DateTimeOffset _retryAt = DateTimeOffset.MaxValue;
    private DateTimeOffset _refreshAt = DateTimeOffset.MaxValue;
    private bool _reopen = true, _disposed;
    private sealed record ReadResult(long Generation, IReadOnlyList<MetricSample> Samples);

    public PassiveTelemetrySource(string key, string component, string source, Func<IPassiveTelemetrySource> factory,
        TimeProvider? clock = null, TimeSpan? slowAfter = null)
    {
        _key = key; _component = component; _source = source; _factory = factory;
        _clock = clock ?? TimeProvider.System; _slowAfter = slowAfter ?? TimeSpan.FromSeconds(3);
    }

    public IReadOnlyList<MetricSample> Poll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var now = _clock.GetUtcNow();
        IReadOnlyList<MetricSample>? completed = null;
        if (_pending is { IsCompleted: true })
        {
            var result = _pending.GetAwaiter().GetResult(); // Read always catches provider failures.
            _pending = null;
            _cancellation?.Dispose(); _cancellation = null;
            if (result.Generation == _generation)
            {
                completed = _last = result.Samples;
                var failures = result.Samples.Any(s => s.Status is Availability.ReadError or Availability.PermissionRequired
                    or Availability.ToolMissing or Availability.Unsupported or Availability.InsufficientData);
                if (failures && !result.Samples.Any(s => s.Status is Availability.Available or Availability.Collecting))
                {
                    _retryAt = now.AddSeconds(Math.Min(300, 15 * Math.Pow(2, Math.Min(_failures++, 5))));
                }
                else { _failures = 0; _retryAt = DateTimeOffset.MaxValue; }
                // Retry missing counters occasionally without pausing counters that still work.
                if (failures && _refreshAt == DateTimeOffset.MaxValue) _refreshAt = now.AddMinutes(5);
            }
        }
        if (_pending is null && now < _retryAt && _retryAt != DateTimeOffset.MaxValue)
            return completed ?? _last.Select(s => s with { Value = null, Timestamp = now,
                Limitation = s.Limitation + " Nova tentativa automática após " + _retryAt.ToString("HH:mm:ss 'UTC'") + "." }).ToArray();
        if (_pending is null)
        {
            if (now >= _retryAt || now >= _refreshAt)
            { _reopen = true; _retryAt = DateTimeOffset.MaxValue; _refreshAt = DateTimeOffset.MaxValue; }
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            var generation = _generation;
            var reopen = _reopen; _reopen = false;
            _started = _clock.GetTimestamp();
            _pending = Task.Run(() => Read(generation, reopen, token));
        }
        if (completed is not null) return completed;
        var elapsed = _clock.GetElapsedTime(_started);
        var delayed = elapsed >= _slowAfter;
        var reason = delayed
            ? "A fonte demorou para responder. A coleta básica continua; aguardando esta leitura, sem iniciar chamadas simultâneas."
            : "Leitura da fonte em segundo plano; aguardando a primeira resposta ou a próxima amostra.";
        var status = delayed ? Availability.ReadError : Availability.Collecting;
        // Do not restamp old values or count the same observation multiple times in the session.
        return _last.Count == 0 ? [MetricSample.Missing(_key, _component, "", _source, now, status, reason)] :
            _last.Select(s => MetricSample.Missing(s.Key, s.Component, s.Unit, s.Source, now, status, reason)).ToArray();
    }

    public void Reset()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _generation++; _last = []; _failures = 0; _retryAt = DateTimeOffset.MaxValue;
        _refreshAt = DateTimeOffset.MaxValue; _reopen = true;
        _cancellation?.Cancel();
    }

    private ReadResult Read(long generation, bool reopen, CancellationToken token)
    {
        try
        {
            if (reopen) { _reader?.Dispose(); _reader = null; }
            token.ThrowIfCancellationRequested();
            _reader ??= _factory();
            token.ThrowIfCancellationRequested();
            return new(generation, _reader.Read(token));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(generation, []); }
        catch (Exception error)
        {
            return new(generation, [MetricSample.Missing(_key, _component, "", _source, _clock.GetUtcNow(),
                WindowsHardwareDiscovery.Status(error), error.Message)]);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++; _cancellation?.Cancel();
        var pending = _pending;
        // Native shutdown can itself stall. Never invoke it on the collector/UI caller, even when
        // the last read already completed; also never unload a library while a read still uses it.
        if (pending is null) _ = Task.Run(Release);
        else _ = pending.ContinueWith(_ => Release(), CancellationToken.None,
            TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void Release()
    {
        try { _reader?.Dispose(); } catch { /* A failed provider cannot block application shutdown. */ }
        _reader = null; _cancellation?.Dispose(); _cancellation = null;
    }
}
