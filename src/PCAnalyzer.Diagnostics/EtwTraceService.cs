using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;
using Microsoft.Diagnostics.Tracing.Session;
using PCAnalyzer.Core;

namespace PCAnalyzer.Diagnostics;

/// <summary>Manual Windows ETW capture. Does not touch an existing/global tracing session.</summary>
public sealed class EtwTraceService
{
    public async Task<EtwCaptureResult> CaptureAsync(EtwCaptureOptions options, CancellationToken cancellationToken = default)
    {
        options.Validate();
        var started = DateTimeOffset.UtcNow;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10))
            return Missing(Availability.Unsupported, "Captura DPC/ISR ETW requer Windows 10 ou 11.", started);
        if (TraceEventSession.IsElevated() != true)
            return Missing(Availability.PermissionRequired, "Inicie uma captura manual pelo auxiliar elevado autorizado; o restante do aplicativo funciona sem administrador.", started);
        cancellationToken.ThrowIfCancellationRequested();
        var quantumKiB = Math.Max(64, Environment.ProcessorCount);
        // TraceEvent 3.2.8 calculates MaximumBuffers = MinimumBuffers*5/4 + 10.
        // Reserve room for that growth while retaining per-processor buffering.
        var minimumMiB = (int)Math.Floor((options.BufferBudgetMiB * 1024.0 - 10 * quantumKiB) / 1.25 / 1024);
        var minimumBuffers = minimumMiB * 1024 / quantumKiB;
        if (minimumMiB < 1 || minimumBuffers < 2 * Environment.ProcessorCount)
            return Missing(Availability.Unsupported, "O número de CPUs exige mais buffers ETW que o orçamento permitido. Use importação de um ETL existente.", started);
        var sessionName = "PCAnalyzer-DpcIsr-" + Guid.NewGuid().ToString("N");
        var timer = Stopwatch.StartNew();
        var aggregate = new LatencyAggregator();
        var state = new ParseState(options.MaximumEvents);
        TraceEventSession? session = null;
        ETWTraceEventSource? source = null;
        Task? processing = null;
        var errors = new List<string>();
        var lost = 0;
        var cancelled = false;
        DateTimeOffset? traceStartUtc = null;
        var ownsSession = false;
        try
        {
            session = new TraceEventSession(sessionName,
                TraceEventSessionOptions.Create | TraceEventSessionOptions.NoRestartOnCreate)
            {
                // Stop() in TraceEvent resets a global profile interval even without Profile enabled.
                // Explicit native cleanup below stops only this logger and preserves that setting.
                StopOnDispose = false,
                BufferQuantumKB = quantumKiB,
                BufferSizeMB = minimumMiB
            };
            // Source's getter starts a regular logger if it is not running yet; kernel enable
            // must happen first or TraceEvent rejects it as a repeated provider activation.
            ownsSession = true;
            session.EnableKernelProvider(KernelTraceEventParser.Keywords.DeferedProcedureCalls |
                KernelTraceEventParser.Keywords.Interrupt | KernelTraceEventParser.Keywords.ImageLoad);
            source = session.Source;
            traceStartUtc = new DateTimeOffset(source.SessionStartTime.ToUniversalTime());
            Subscribe(source, aggregate, state);
            processing = Task.Run(() => source.Process(), CancellationToken.None);
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var duration = Task.Delay(options.Duration, waitCancellation.Token);
            await Task.WhenAny(processing, duration).ConfigureAwait(false);
            cancelled = cancellationToken.IsCancellationRequested;
            waitCancellation.Cancel();
            try { lost = session.EventsLost; } catch (Exception e) when (e is COMException or InvalidOperationException) { errors.Add("Contagem de eventos perdidos indisponível: " + e.Message); }
            lost = Math.Max(lost, OwnedEtwControl.Stop(sessionName));
            ownsSession = false;
            // StopProcessing also handles consumer/event-limit cancellation.
            source.StopProcessing();
            await processing.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or COMException or InvalidOperationException or NotSupportedException or TimeoutException or Win32Exception)
        {
            // NoRestartOnCreate must also prevent cleanup of an unexpected pre-existing logger.
            if (e.HResult == unchecked((int)0x800700B7)) ownsSession = false;
            errors.Add(e.Message);
        }
        finally
        {
            // Stop only the random logger owned by this invocation, including partial activation.
            if (ownsSession)
            {
                try { lost = Math.Max(lost, OwnedEtwControl.Stop(sessionName)); }
                catch (Exception e) { errors.Add("Falha ao encerrar sessão própria " + sessionName + ": " + e.Message); }
            }
            try { session?.Dispose(); } catch (Exception e) { errors.Add("Falha ao fechar handles da sessão própria: " + e.Message); }
            try { source?.Dispose(); } catch (Exception e) { errors.Add("Falha ao fechar consumidor ETW: " + e.Message); }
            if (processing is not null && !processing.IsCompleted)
                _ = processing.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        }
        timer.Stop();
        var data = aggregate.Snapshot();
        var timeline = aggregate.TimelineSnapshot();
        var limitations = data.Limitations.ToList();
        if (lost > 0) limitations.Add($"ETW perdeu {lost} eventos: contagens, durações e atribuição de módulos podem estar incompletas.");
        if (state.LimitReached) limitations.Add("Captura interrompida pelo limite de eventos; resultado parcial.");
        if (cancelled) limitations.Add("Captura cancelada manualmente; resultado parcial.");
        limitations.Add($"Orçamento solicitado de buffers ETW: {options.BufferBudgetMiB} MiB; buffers e contadores limitados, sem retenção de eventos brutos. Não representa um limite para toda a memória do processo.");
        limitations.AddRange(errors);
        var status = errors.Count > 0 ? Availability.ReadError : data.Cpu.Count > 0 ? Availability.Available : Availability.InsufficientData;
        return new(status, errors.Count > 0 ? "A captura retornou dados parciais ou falhou: " + errors[0] :
            data.Cpu.Count == 0 ? "Nenhum evento DPC/ISR válido recebido; ausência de dados não prova baixa latência." : "DPC/ISR obtidos diretamente de eventos do kernel ETW.",
            started, timer.Elapsed, sessionName, state.EventsSeen, lost, cancelled, state.LimitReached, data.Cpu, data.Modules, limitations,
            timeline.Events, timeline.Truncated, TraceStartUtc: traceStartUtc, TimeBuckets: timeline.Buckets,
            EventCounts: aggregate.EventCounts(state.EventsSeen));
    }

    public async Task<EtwCaptureResult> ImportAsync(string etlPath, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        if (!OperatingSystem.IsWindows()) return Missing(Availability.Unsupported, "O decodificador ETL da biblioteca TraceEvent requer Windows.", started);
        var path = Path.GetFullPath(etlPath);
        if (!File.Exists(path)) return Missing(Availability.ReadError, "Arquivo ETL não encontrado.", started);
        if (!string.Equals(Path.GetExtension(path), ".etl", StringComparison.OrdinalIgnoreCase))
            return Missing(Availability.ReadError, "Selecione um arquivo ETL real.", started);
        if (new FileInfo(path).Length > 512L * 1024 * 1024)
            return Missing(Availability.ReadError, "Importação limitada a ETLs de 512 MiB; exporte uma seleção menor no WPR/WPA.", started);
        return await Task.Run(() => Import(path, cancellationToken), CancellationToken.None).ConfigureAwait(false);
    }

    private static EtwCaptureResult Import(string path, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var aggregate = new LatencyAggregator();
        var state = new ParseState(2_000_000);
        var errors = new List<string>();
        var lost = 0;
        DateTimeOffset? traceStartUtc = null;
        try
        {
            using var source = new ETWTraceEventSource(path);
            traceStartUtc = new DateTimeOffset(source.SessionStartTime.ToUniversalTime());
            Subscribe(source, aggregate, state);
            using var registration = cancellationToken.Register(source.StopProcessing);
            try { source.Process(); }
            finally { lost = source.EventsLost; }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException or InvalidOperationException or ArgumentException)
        {
            errors.Add("A decodificação do ETL foi interrompida: " + e.Message);
        }
        var data = aggregate.Snapshot();
        var timeline = aggregate.TimelineSnapshot();
        var limitations = data.Limitations.ToList();
        if (lost > 0) limitations.Add($"ETL informa {lost} eventos perdidos; resumo incompleto.");
        if (state.LimitReached) limitations.Add("Importação atingiu 2.000.000 de eventos: resumo parcial.");
        if (cancellationToken.IsCancellationRequested) limitations.Add("Importação cancelada: resumo parcial.");
        limitations.AddRange(errors);
        return new(errors.Count > 0 ? Availability.ReadError : data.Cpu.Count == 0 ? Availability.InsufficientData : Availability.Available,
            errors.Count > 0 ? "ETL lido parcialmente; os eventos válidos anteriores ao erro foram preservados. " + errors[0] :
            data.Cpu.Count == 0 ? "O ETL não contém eventos DPC/ISR válidos." : "ETL existente importado; arquivo original preservado.",
            traceStartUtc ?? started, TimeSpan.FromMilliseconds(Math.Max(0, state.LastTimeMilliseconds)),
            null, state.EventsSeen, lost, cancellationToken.IsCancellationRequested, state.LimitReached,
            data.Cpu, data.Modules, limitations, timeline.Events, timeline.Truncated,
            TraceStartUtc: traceStartUtc, TimeBuckets: timeline.Buckets,
            EventCounts: aggregate.EventCounts(state.EventsSeen));
    }

    private static void Subscribe(ETWTraceEventSource source, LatencyAggregator aggregate, ParseState state)
    {
        source.AllEvents += data =>
        {
            state.EventsSeen++;
            var relativeMilliseconds = data.TimeStampRelativeMSec;
            if (double.IsFinite(relativeMilliseconds) && relativeMilliseconds >= 0 && relativeMilliseconds < TimeSpan.MaxValue.TotalMilliseconds)
                state.LastTimeMilliseconds = Math.Max(state.LastTimeMilliseconds, relativeMilliseconds);
            if (state.EventsSeen >= state.MaximumEvents) { state.LimitReached = true; source.StopProcessing(); }
        };
        source.Kernel.ImageLoadGroup += data => aggregate.ObserveKernelImage(data.ProcessID, data.ImageBase, data.ImageSize, data.FileName);
        source.Kernel.ImageUnloadGroup += data => aggregate.ForgetKernelImage(data.ProcessID, data.ImageBase);
        source.Kernel.PerfInfoDPC += data => Dpc("DPC", data);
        source.Kernel.PerfInfoTimerDPC += data => Dpc("TimerDPC", data);
        source.Kernel.PerfInfoThreadedDPC += data => Dpc("ThreadedDPC", data);
        source.Kernel.PerfInfoISR += data => aggregate.ObserveLatency("ISR", data.ProcessorNumber, data.Routine, data.ElapsedTimeMSec * 1000,
            data.TimeStampRelativeMSec, new DateTimeOffset(data.TimeStamp.ToUniversalTime()), RawClockTicks(data));
        void Dpc(string kind, DPCTraceData data) => aggregate.ObserveLatency(kind, data.ProcessorNumber, data.Routine, data.ElapsedTimeMSec * 1000,
            data.TimeStampRelativeMSec, new DateTimeOffset(data.TimeStamp.ToUniversalTime()), RawClockTicks(data));
    }

    private static long RawClockTicks(TraceEvent data)
    {
        // TraceEvent marks this API discouraged rather than removed: retain the original clock
        // only as provenance. All duration calculations and plotting use TimeStampRelativeMSec.
#pragma warning disable CS0618
        return data.TimeStampQPC;
#pragma warning restore CS0618
    }

    private static EtwCaptureResult Missing(Availability status, string detail, DateTimeOffset started) =>
        new(status, detail, started, TimeSpan.Zero, null, 0, 0, false, false, [], [], [detail]);
    private sealed class ParseState(long maximumEvents)
    {
        public long MaximumEvents { get; } = maximumEvents;
        public long EventsSeen;
        public double LastTimeMilliseconds;
        public bool LimitReached;
    }
}
