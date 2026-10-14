using System.Runtime.InteropServices;
using PCAnalyzer.Core;
using PCAnalyzer.Hardware;

namespace PCAnalyzer.Tests;

public sealed class HardwareTests
{
    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    public void DiskLatencyWithoutCompletedIoIsUnavailableInsteadOfZero(string direction)
    {
        var time = DateTimeOffset.UtcNow;
        var latency = new MetricSample($"disk.0.{direction}.latency.ms", "Disco 0", 0, "ms", "Counter", "Média", time, TimeSpan.FromSeconds(1));
        var rate = latency with { Key = $"disk.0.{direction}.operations_per_second", Value = 0, Unit = "I/O/s" };
        var result = TelemetryQuality.MarkDiskLatencyWithoutIo([latency, rate]);
        Assert.Null(result[0].Value);
        Assert.Equal(Availability.InsufficientData, result[0].Status);
        Assert.Equal(latency.Source, result[0].Source);
        Assert.Equal(latency.Timestamp, result[0].Timestamp);
    }

    [Fact]
    public void DiskLatencyRequiresAvailableRateAndPreservesMeasurementsWhenIoOccurred()
    {
        var time = DateTimeOffset.UtcNow;
        var latency = new MetricSample("disk.1.read.latency.ms", "Disco 1", 0.23, "ms", "Counter", "Média", time, TimeSpan.FromSeconds(2));
        Assert.Equal(Availability.InsufficientData, TelemetryQuality.MarkDiskLatencyWithoutIo([latency])[0].Status);
        var rate = latency with { Key = "disk.1.read.operations_per_second", Value = 32, Unit = "I/O/s" };
        Assert.Equal(latency, TelemetryQuality.MarkDiskLatencyWithoutIo([latency, rate])[0]);
        var unavailable = rate with { Value = null, Status = Availability.ReadError };
        Assert.Equal(Availability.InsufficientData, TelemetryQuality.MarkDiskLatencyWithoutIo([latency, unavailable])[0].Status);
    }

    [Fact]
    public async Task NonWindowsInventoryExplicitlyReportsUnsupportedWithoutInventedComponents()
    {
        if (OperatingSystem.IsWindows()) return; // Windows readings belong to hardware integration validation.
        var inventory = await new WindowsHardwareDiscovery().DiscoverAsync();
        Assert.Empty(inventory.Components);
        Assert.Contains(inventory.Capabilities, item => item.Status == Availability.Unsupported);
    }

    [Fact]
    public async Task NonWindowsCollectorNeverReturnsFictionalMeasurements()
    {
        if (OperatingSystem.IsWindows()) return;
        await using var collector = new WindowsTelemetryCollector();
        var batch = await collector.CollectAsync(TimeSpan.FromSeconds(1));
        Assert.Empty(batch.Processes);
        Assert.NotEmpty(batch.Metrics);
        Assert.All(batch.Metrics, metric =>
        {
            Assert.Null(metric.Value);
            Assert.Equal(Availability.Unsupported, metric.Status);
            Assert.False(string.IsNullOrWhiteSpace(metric.Limitation));
        });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(300001)]
    public async Task CollectionRejectsUnsafeOrNonsensicalCadence(double milliseconds)
    {
        await using var collector = new WindowsTelemetryCollector();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => collector.CollectAsync(TimeSpan.FromMilliseconds(milliseconds)));
    }

    [Fact]
    public async Task PreCancelledRequestsDoNotStartDiscoveryOrCollection()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var collector = new WindowsTelemetryCollector();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => collector.CollectAsync(TimeSpan.FromSeconds(1), cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new WindowsHardwareDiscovery().DiscoverAsync(cancellation.Token));
    }

    [Fact]
    public async Task DisposalIsIdempotentAndPreventsFurtherMeasurements()
    {
        var collector = new WindowsTelemetryCollector();
        await collector.DisposeAsync();
        collector.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => collector.CollectAsync(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void NativeStructuresPreservePublishedWindowsAbiSizes()
    {
        var native = typeof(WindowsHardwareDiscovery).Assembly.GetType("PCAnalyzer.Hardware.NativeWindows", throwOnError: true)!;
        Type Structure(string name) => native.GetNestedType(name, System.Reflection.BindingFlags.NonPublic)!;
        Assert.Equal(220, Marshal.SizeOf(Structure("DisplayMode"))); // DEVMODEW
        Assert.Equal(840, Marshal.SizeOf(Structure("DisplayDevice"))); // DISPLAY_DEVICEW
        Assert.Equal(IntPtr.Size == 8 ? 104 : 56, Marshal.SizeOf(Structure("PerformanceInformation")));
    }
}
