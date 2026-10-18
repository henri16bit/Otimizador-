using PCAnalyzer.Core;

namespace PCAnalyzer.Tests;

public sealed class MemoryAccessBenchmarkTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(100L)]
    public async Task RequiresKnownAvailableMemoryBeforeAllocating(long? available) =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => MemoryAccessBenchmark.RunAsync(available, null, CancellationToken.None));

    [Fact]
    public async Task MeasuresDistinctOperationsWithDefinedByteCountsAndNoDramsClaim()
    {
        var result = await MemoryAccessBenchmark.RunAsync(1024L * 1024 * 1024, null, CancellationToken.None, [1]);
        Assert.False(result.Cancelled);
        Assert.Equal(4, result.Measurements.Count);
        foreach (var measured in result.Measurements)
        {
            Assert.Equal(1, measured.BufferMiB);
            Assert.True(measured.DurationSeconds > 0);
            if (measured.MiBPerSecond is { } rate)
            {
                Assert.Equal(4 * 1024 * 1024, measured.PayloadBytes);
                Assert.Equal(4, measured.Operations);
                Assert.Equal(4 / measured.DurationSeconds, rate, 6);
                Assert.Null(measured.NanosecondsPerAccess);
            }
            else
            {
                Assert.Equal(1_048_576, measured.Operations);
                Assert.Equal(measured.DurationSeconds * 1e9 / measured.Operations, measured.NanosecondsPerAccess!.Value, 6);
            }
        }
        Assert.Contains(result.Limitations, l => l.Contains("Não isola", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancellationPreservesOnlyCompletedStages()
    {
        using var cancellation = new CancellationTokenSource();
        var result = await MemoryAccessBenchmark.RunAsync(1024L * 1024 * 1024,
            new InlineProgress<double>(_ => cancellation.Cancel()), cancellation.Token, [1]);
        Assert.True(result.Cancelled);
        Assert.Single(result.Measurements);
        Assert.Equal("Leitura sequencial", result.Measurements[0].Operation);
    }
}
