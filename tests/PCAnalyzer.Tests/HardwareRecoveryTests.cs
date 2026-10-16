using System.Diagnostics;
using PCAnalyzer.Core;
using PCAnalyzer.Hardware;

namespace PCAnalyzer.Tests;

public sealed class HardwareRecoveryTests
{
    [Fact]
    public async Task SlowProviderKeepsOneReadAndResetAndDisposalDoNotWaitForIt()
    {
        using var released = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = new TestClock(); var reads = 0;
        var source = new PassiveTelemetrySource("optional", "Fonte", "Teste", () => new Reader(_ =>
        {
            Interlocked.Increment(ref reads); entered.TrySetResult(); released.Wait();
            return [Sample(99)];
        }, () => disposed.TrySetResult()), clock);
        Assert.Equal(Availability.Collecting, Assert.Single(source.Poll()).Status);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(4));
        for (var index = 0; index < 50; index++) Assert.Equal(Availability.ReadError, Assert.Single(source.Poll()).Status);
        Assert.Equal(1, Volatile.Read(ref reads));
        source.Reset();
        Assert.Null(Assert.Single(source.Poll()).Value);
        Assert.Equal(1, Volatile.Read(ref reads));
        source.Dispose();
        Assert.False(disposed.Task.IsCompleted);
        released.Set();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ResetDiscardsLateResultAndReopensOnlyAfterOldProviderExits()
    {
        using var released = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creates = 0;
        using var source = new PassiveTelemetrySource("optional", "Fonte", "Teste", () =>
        {
            var generation = Interlocked.Increment(ref creates);
            return new Reader(_ =>
            {
                if (generation == 1) { entered.TrySetResult(); released.Wait(); }
                return [Sample(generation)];
            });
        });
        source.Poll(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        source.Reset(); released.Set();
        var result = await Until(() =>
        {
            var sample = Assert.Single(source.Poll());
            Assert.NotEqual(1d, sample.Value);
            return sample.Value == 2 ? sample : null;
        });
        Assert.Equal(2d, result.Value);
        Assert.Equal(2, Volatile.Read(ref creates));
    }

    [Fact]
    public async Task CompletedProviderWithSlowNativeShutdownDoesNotBlockDispose()
    {
        using var releaseShutdown = new ManualResetEventSlim();
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shuttingDown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdownFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shutdowns = 0;
        var source = new PassiveTelemetrySource("optional", "Fonte", "Teste", () => new Reader(_ =>
        {
            read.TrySetResult(); return [Sample(1)];
        }, () =>
        {
            Interlocked.Increment(ref shutdowns); shuttingDown.TrySetResult(); releaseShutdown.Wait();
            shutdownFinished.TrySetResult();
        }));
        try
        {
            source.Poll();
            await read.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Leave the completed read queued for Poll, as happens between collection intervals.
            await Task.Delay(50);
            await Task.Run(source.Dispose).WaitAsync(TimeSpan.FromSeconds(2));
            await shuttingDown.Task.WaitAsync(TimeSpan.FromSeconds(5));
            source.Dispose();
            Assert.False(shutdownFinished.Task.IsCompleted);
            Assert.Equal(1, Volatile.Read(ref shutdowns));
        }
        finally { releaseShutdown.Set(); source.Dispose(); }
        await shutdownFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FailedProviderBacksOffAndRecoversWithANewInstance()
    {
        var clock = new TestClock(); var creates = 0;
        using var source = new PassiveTelemetrySource("optional", "Fonte", "Teste", () =>
        {
            if (Interlocked.Increment(ref creates) == 1) throw new UnauthorizedAccessException("provider denied");
            return new Reader(_ => [Sample(7)]);
        }, clock);
        source.Poll();
        await Until(() => { var sample = Assert.Single(source.Poll()); return sample.Status == Availability.PermissionRequired ? sample : null; });
        for (var index = 0; index < 10; index++) Assert.Null(Assert.Single(source.Poll()).Value);
        clock.Advance(TimeSpan.FromSeconds(14)); source.Poll();
        Assert.Equal(1, Volatile.Read(ref creates));
        clock.Advance(TimeSpan.FromSeconds(1));
        var result = await Until(() => { var sample = Assert.Single(source.Poll()); return sample.Value == 7 ? sample : null; });
        Assert.Equal(Availability.Available, result.Status);
        Assert.Equal(2, Volatile.Read(ref creates));
    }

    [Fact]
    public async Task PartialProviderFailureKeepsWorkingMetricsFlowing()
    {
        var reads = 0;
        using var source = new PassiveTelemetrySource("optional", "Fonte", "Teste", () => new Reader(_ =>
            [Sample(Interlocked.Increment(ref reads)), MetricSample.Missing("unsupported", "Fonte", "", "Teste", DateTimeOffset.UtcNow,
                Availability.Unsupported, "Sensor não exposto")]));
        source.Poll();
        await Until(() => source.Poll().FirstOrDefault(sample => sample.Key == "value" && sample.Value >= 3));
        Assert.True(Volatile.Read(ref reads) >= 3);
    }

    [Fact]
    public void ProcessCpuDeniedStillPreservesNamePidAndMemory()
    {
        var state = new ProcessTelemetryState(); state.BeginBatch();
        var sample = state.Read(42, () => "game", () => throw new UnauthorizedAccessException(),
            () => throw new UnauthorizedAccessException(), () => 123456, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), 8, out var partial);
        state.EndBatch();
        Assert.True(partial); Assert.Equal("game", sample.Name); Assert.Equal(42, sample.ProcessId);
        Assert.Equal(123456, sample.WorkingSetBytes); Assert.Null(sample.CpuPercent);
        Assert.Contains("parcial", sample.Limitation);
    }

    [Fact]
    public void ProcessPidReuseAndMissingIdentityNeverShareCpuBaselines()
    {
        var state = new ProcessTelemetryState(); var ticks = Stopwatch.GetTimestamp();
        ProcessSample Read(long start, double cpu, bool noStart = false)
        {
            state.BeginBatch(); ticks += Stopwatch.Frequency;
            var result = state.Read(42, () => "game", () => noStart ? throw new UnauthorizedAccessException() : start,
                () => TimeSpan.FromSeconds(cpu), () => 1000, DateTimeOffset.UtcNow, ticks, 2, out _);
            state.EndBatch(); return result;
        }
        Assert.Null(Read(10, 1).CpuPercent);
        Assert.Equal(25d, Read(10, 1.5).CpuPercent);
        Assert.Null(Read(20, 99).CpuPercent); // Same PID, different process creation.
        Assert.Equal(25d, Read(20, 99.5).CpuPercent);
        Assert.Null(Read(20, 100, noStart: true).CpuPercent);
        Assert.Null(Read(20, 100.5).CpuPercent); // Unknown identity invalidates the previous baseline.
        state.Reset(); Assert.Null(Read(20, 101).CpuPercent);
    }

    [Fact]
    public void GpuEngineAggregationSumsProcessSharesButNotParallelEngines()
    {
        var result = GpuEngineAggregation.Aggregate([
            ("pid_1_luid_0x00000000_0x0000AABB_phys_0_eng_0_engtype_3D", 35),
            ("pid_2_luid_0x00000000_0x0000AABB_phys_0_eng_0_engtype_3D", 25),
            ("pid_1_luid_0x00000000_0x0000AABB_phys_0_eng_1_engtype_Copy", 50),
            ("pid_1_luid_0x00000000_0x0000CCDD_phys_0_eng_0_engtype_3D", 120),
            ("pid_3_luid_0x00000000_0x0000AABB_phys_0_eng_0_engtype_3D", double.NaN),
            ("unknown", 100)]);
        Assert.Equal(2, result.Count);
        Assert.Equal(60, result["luid_0x00000000_0x0000aabb_phys_0"]);
        Assert.Equal(100, result["luid_0x00000000_0x0000ccdd_phys_0"]);
    }

    [Fact]
    public async Task CollectorResetHonorsCancellationAndLifetime()
    {
        var collector = new WindowsTelemetryCollector();
        await collector.ResetAsync();
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.ResetAsync(cancelled.Token));
        await collector.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => collector.ResetAsync());
    }

    private static MetricSample Sample(double value) => new("value", "Fonte", value, "%", "Teste", "Observação", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(1));
    private static async Task<T> Until<T>(Func<T?> read) where T : class
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (true) { if (read() is { } result) return result; await Task.Delay(5, deadline.Token); }
    }
    private sealed class Reader(Func<CancellationToken, IReadOnlyList<MetricSample>> read, Action? dispose = null) : IPassiveTelemetrySource
    {
        public IReadOnlyList<MetricSample> Read(CancellationToken token) => read(token);
        public void Dispose() => dispose?.Invoke();
    }
    private sealed class TestClock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(_milliseconds);
        public override long GetTimestamp() => _milliseconds;
        public void Advance(TimeSpan elapsed) => _milliseconds += (long)elapsed.TotalMilliseconds;
    }
}
