using PCAnalyzer.Core;

namespace PCAnalyzer.Tests;

public class CoreTests
{
    [Fact] public void PercentileUsesDocumentedInterpolation()
    {
        Assert.Equal(28.5, Statistics.Percentile([0, 10, 20, 30], .95)!.Value, precision: 10);
        Assert.Equal(0, Statistics.Percentile([30, 20, 0, 10], 0));
        Assert.Equal(30, Statistics.Percentile([30, 20, 0, 10], 1));
    }
    [Fact] public void MissingSamplesAreNeverZero()
    {
        Assert.Null(Statistics.Percentile([], .5));
        Assert.Null(Statistics.Mean([double.NaN, double.PositiveInfinity]));
        Assert.Null(MetricSample.Missing("gpu.temperature", "GPU", "°C", "NVML", DateTimeOffset.UtcNow,
            Availability.Unsupported, "Sem sensor").Value);
    }

    [Fact] public void FiniteExtremeValuesDoNotOverflowStatistics()
    {
        Assert.Equal(1e308, Statistics.Mean([1e308, 1e308]));
        Assert.Equal(0, Statistics.Mean([-1e308, 1e308]));
        Assert.Equal(0, Statistics.Percentile([-1e308, 1e308], .5));
    }
    [Fact] public void JitterDoesNotBridgeLostReplies()
    {
        Assert.Equal(3, Statistics.MeanAbsoluteSuccessiveDifference([10, 14, null, 100, 102]));
        Assert.Null(Statistics.MeanAbsoluteSuccessiveDifference([10, null, 20]));
    }
    [Theory] [InlineData(-.1)] [InlineData(1.1)]
    public void PercentileRejectsInvalidProbability(double p) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Statistics.Percentile([1], p));

    private static readonly HardwareInventory EmptyInventory = new(DateTimeOffset.UtcNow, [], []);
    private static MetricSample Sample(string key, double value, int second, string unit = "%") =>
        new(key, "CPU", value, unit, "Teste unitário: dados sintéticos", "Fixture", DateTimeOffset.UnixEpoch.AddSeconds(second),
            TimeSpan.FromSeconds(1));

    [Fact] public void LowGlobalCpuDoesNotExcludeLogicalSaturation()
    {
        var data = Enumerable.Range(0, 15).SelectMany(i => new[]
            { Sample("cpu.total.percent", 8, i), Sample("cpu.logical.0.percent", 99, i) }).ToArray();
        var findings = new DiagnosticRuleEngine().Analyze(EmptyInventory, data);
        Assert.Contains(findings, f => f.Id == "cpu-load:cpu.logical.0.percent");
        Assert.DoesNotContain(findings, f => f.Id == "cpu-load:cpu.total.percent");
    }
    [Fact] public void IsolatedPeakDoesNotBecomePersistentWarning()
    {
        var data = Enumerable.Range(0, 15).Select(i => Sample("cpu.total.percent", i == 4 ? 100 : 10, i)).ToArray();
        Assert.DoesNotContain(new DiagnosticRuleEngine().Analyze(EmptyInventory, data), f => f.Severity == Severity.Warning);
    }
    [Fact] public void MissingSourcesDoNotProveHealthySystem()
    {
        var f = new DiagnosticRuleEngine().Analyze(EmptyInventory, []);
        Assert.All(f, v => Assert.Equal(FindingCategory.InsufficientData, v.Category));
    }
    [Fact] public void SustainedMemoryPressureRequiresBothConditions()
    {
        var data = Enumerable.Range(0, 15).SelectMany(i => new[] {
            Sample("memory.available.bytes", 100 * 1048576, i, "bytes"),
            Sample("memory.commit.bytes", 950, i, "bytes"),
            Sample("memory.commit.limit.bytes", 1000, i, "bytes") }).ToArray();
        Assert.Contains(new DiagnosticRuleEngine().Analyze(EmptyInventory, data), f => f.Id == "memory-pressure");
        var noCommit = data.Where(s => s.Key != "memory.commit.bytes").ToArray();
        Assert.DoesNotContain(new DiagnosticRuleEngine().Analyze(EmptyInventory, noCommit), f => f.Id == "memory-pressure");
    }
    [Fact] public void FrequencyAloneDoesNotGenerateThrottlingOrXmpWarning()
    {
        var inventory = new HardwareInventory(DateTimeOffset.UtcNow,
            [new("dimm0", "RAM", "DDR4", [new("Configured speed", "2933 MT/s", "WMI")])], []);
        var findings = new DiagnosticRuleEngine().Analyze(inventory,
            Enumerable.Range(0, 15).Select(i => Sample("cpu.frequency.mhz", 2900, i, "MHz")).ToArray());
        Assert.DoesNotContain(findings, f => f.Severity == Severity.Critical || f.Severity == Severity.Warning);
        Assert.Contains(findings, f => f.Id == "ram-platform");
    }
    [Fact] public async Task ControlledReadPreservesFileAndReportsActualBytes()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".bin");
        var bytes = Enumerable.Range(0, 131072).Select(i => (byte)i).ToArray();
        try
        {
            await File.WriteAllBytesAsync(path, bytes);
            var result = await ControlledTests.ReadAsync(path, null, CancellationToken.None);
            Assert.Equal(bytes.Length, result.Bytes);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        }
        finally { File.Delete(path); }
    }
    [Fact] public async Task CancelledCpuTestDoesNotStartWork()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ControlledTests.CpuAsync(null, cts.Token));
    }

    [Fact] public void UnrelatedTelemetryDoesNotEstablishHealthyCpuAndMemory()
    {
        var samples = Enumerable.Range(0, 20).Select(i => Sample("collector.duration.ms", 5, i, "ms")).ToArray();
        var findings = new DiagnosticRuleEngine().Analyze(EmptyInventory, samples);
        Assert.Contains(findings, f => f.Id == "window" && f.Category == FindingCategory.InsufficientData);
        Assert.DoesNotContain(findings, f => f.Category == FindingCategory.NoAnomaly);
    }

    [Fact] public void MemoryConditionsFromDifferentPeriodsAreNotConcurrentPressure()
    {
        var data = Enumerable.Range(0, 15).SelectMany(i => new[] {
            Sample("memory.available.bytes", 100 * 1048576, i, "bytes"),
            Sample("memory.commit.bytes", 950, 100 + i, "bytes"),
            Sample("memory.commit.limit.bytes", 1000, 100 + i, "bytes") }).ToArray();
        var findings = new DiagnosticRuleEngine().Analyze(EmptyInventory, data);
        Assert.DoesNotContain(findings, f => f.Id == "memory-pressure");
        Assert.Contains(findings, f => f.Id == "window");
    }

    [Fact] public void MemoryConditionsMustCoexistInEachObservation()
    {
        var data = Enumerable.Range(0, 30).SelectMany(i => new[] {
            Sample("memory.available.bytes", i < 24 ? 100 * 1048576 : 2000 * 1048576, i, "bytes"),
            Sample("memory.commit.bytes", i >= 6 ? 950 : 100, i, "bytes"),
            Sample("memory.commit.limit.bytes", 1000, i, "bytes") }).ToArray();
        Assert.DoesNotContain(new DiagnosticRuleEngine().Analyze(EmptyInventory, data), f => f.Id == "memory-pressure");
    }

    [Theory] [InlineData("cpu.logical.percent")] [InlineData("cpu.logical.0.frequency.mhz")]
    [InlineData("cpu.logical.+1.percent")]
    public void CpuRuleIgnoresUnrecognizedMetricNames(string key)
    {
        var data = Enumerable.Range(0, 15).Select(i => Sample(key, 100, i)).ToArray();
        Assert.DoesNotContain(new DiagnosticRuleEngine().Analyze(EmptyInventory, data), f => f.Severity == Severity.Warning);
    }

    [Fact] public void SynchronizedCpuAndMemoryProvideScopedNoAnomalyFinding()
    {
        var data = Enumerable.Range(0, 15).SelectMany(i => new[] {
            Sample("cpu.total.percent", 10, i),
            Sample("memory.available.bytes", 2000 * 1048576, i, "bytes"),
            Sample("memory.commit.bytes", 500, i, "bytes"),
            Sample("memory.commit.limit.bytes", 1000, i, "bytes") }).ToArray();
        Assert.Contains(new DiagnosticRuleEngine().Analyze(EmptyInventory, data), f => f.Category == FindingCategory.NoAnomaly);
    }

    [Fact] public void DuplicateTimestampsDoNotCreateSustainedCpuEvidence()
    {
        var samples = Enumerable.Range(0, 30).Select(i => Sample("cpu.total.percent", 99, i % 2 * 10)).ToArray();
        var findings = new DiagnosticRuleEngine().Analyze(EmptyInventory, samples);
        Assert.DoesNotContain(findings, f => f.Id.StartsWith("cpu-load:", StringComparison.Ordinal));
        Assert.DoesNotContain(findings, f => f.Category == FindingCategory.NoAnomaly);
    }

    [Fact] public async Task MemoryLabRefusesInsufficientPhysicalAvailability()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ControlledTests.MemoryAsync(null, CancellationToken.None, 64 * 1048576));
    }
}
