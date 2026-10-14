using PCAnalyzer.App.ViewModels;
using PCAnalyzer.Core;

namespace PCAnalyzer.Tests;

public sealed class PresentationTests
{
    private static readonly DateTimeOffset Time = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
    private static MetricSample Sample(string key, double? value, string unit, int second = 0, Availability status = Availability.Available, string component = "CPU") =>
        new(key, component, value, unit, "Fixture", "Teste de apresentação", Time.AddSeconds(second), TimeSpan.FromSeconds(1), status);

    [Fact]
    public void DisplayConvertsBinaryBytesAndDecimalLinkSpeedWithoutChangingStoredSamples()
    {
        var memory = Sample("memory.total.bytes", 16L * 1024 * 1024 * 1024, "bytes");
        Assert.Equal(new DisplayReading("16", "GiB"), MetricPresentation.Format(memory));
        Assert.Equal(16L * 1024 * 1024 * 1024, memory.Value);
        Assert.Equal("bytes", memory.Unit);
        Assert.Equal(new DisplayReading("1", "MiB/s"), MetricPresentation.Format(Sample("disk.0.read.bytes_per_second", 1024 * 1024, "bytes/s")));
        Assert.Equal(new DisplayReading("1000", "Mbit/s"), MetricPresentation.Format(Sample("network.a.link.bits_per_second", 1_000_000_000, "bits/s")));
    }

    [Theory]
    [InlineData(Availability.Unsupported)]
    [InlineData(Availability.Collecting)]
    [InlineData(Availability.ReadError)]
    public void UnavailableReadingNeverLooksLikeAValidZero(Availability status)
    {
        Assert.Equal("—", MetricPresentation.Format(Sample("cpu.total.percent", 0, "%", status: status)).Value);
        Assert.False(MetricPresentation.Overview([Sample("cpu.total.percent", 0, "%", status: status)], null)[0].HasPercentage);
    }

    [Fact]
    public void RamCardUsesSynchronizedPhysicalMemoryAndDoesNotUseCommit()
    {
        var cards = MetricPresentation.Overview([
            Sample("memory.total.bytes", 16L * 1024 * 1024 * 1024, "bytes"),
            Sample("memory.available.bytes", 4L * 1024 * 1024 * 1024, "bytes"),
            Sample("memory.commit.bytes", 100L * 1024 * 1024 * 1024, "bytes")], null);
        Assert.Equal(75, cards[1].Percentage);
        Assert.True(cards[1].HasPercentage);
        Assert.Contains("12 GiB", cards[1].Detail);
    }

    [Theory]
    [InlineData(3, 4, "bytes")]
    [InlineData(0, 20, "bytes")]
    [InlineData(0, -1, "bytes")]
    [InlineData(0, 4, "MiB")]
    public void RamCardDoesNotDeriveUsageFromInvalidOrUnpairedReadings(int second, double freeGib, string unit)
    {
        var card = MetricPresentation.Overview([
            Sample("memory.total.bytes", 16L * 1024 * 1024 * 1024, "bytes"),
            Sample("memory.available.bytes", freeGib * 1024 * 1024 * 1024, unit, second)], null)[1];
        Assert.Equal("—", card.Value);
        Assert.False(card.HasPercentage);
    }

    [Fact]
    public void NetworkCardIdentifiesOneMeasuredAdapterInsteadOfSummingPossiblyDuplicatedTraffic()
    {
        var card = MetricPresentation.Overview([
            Sample("network.eth.receive.bytes_per_second", 512 * 1024, "bytes/s", component: "Ethernet"),
            Sample("network.vpn.receive.bytes_per_second", 256 * 1024, "bytes/s", component: "VPN")], null)[3];
        Assert.Equal("512", card.Value);
        Assert.Equal("KiB/s", card.Unit);
        Assert.Equal("Ethernet", card.Detail);
    }

    [Fact]
    public void GpuInventoryAndMemoryActivityDoNotBecomeInventedGpuUsage()
    {
        var card = MetricPresentation.Overview([Sample("gpu.0.memory.activity.percent", 60, "%", component: "GPU")],
            new(Time, [new("gpu.0", "GPU", "Placa do teste", [])], []))[2];
        Assert.Equal("—", card.Value);
        Assert.False(card.HasPercentage);
    }

    [Fact]
    public void TinyValuesRemainNonzeroAndLargeValuesRemainBoundedForDisplay()
    {
        Assert.Contains("E", MetricPresentation.Format(.0000001, "ms").Value);
        Assert.Equal("—", MetricPresentation.Format(double.NaN, "ms").Value);
        Assert.InRange(MetricPresentation.Format(double.MaxValue, "bytes").Value.Length, 1, 16);
    }

    [Fact]
    public void ChartAxisUsesOneConsistentScaleAcrossItsRange()
    {
        var scale = MetricPresentation.Scale("bytes", 16L * 1024 * 1024 * 1024);
        Assert.Equal("GiB", scale.Unit);
        Assert.Equal(1024 * 1024 * 1024d, scale.Factor);
        Assert.Equal(4, 4L * 1024 * 1024 * 1024 / scale.Factor);
    }
}
