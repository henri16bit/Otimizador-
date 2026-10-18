using PCAnalyzer.Core;
using PCAnalyzer.Hardware;

namespace PCAnalyzer.Tests;

public sealed class StorageAuditTests
{
    [Fact]
    public void DdrConfiguredRateIsReportedWithoutFabricatingSpdXmpOrDualChannel()
    {
        var rows = new[] { Row(("DeviceLocator", "DIMM_A2"), ("PartNumber", "DDR4 module"), ("Capacity", 8_589_934_592L),
            ("ConfiguredClockSpeed", 2933), ("Speed", 3600), ("SMBIOSMemoryType", 26)) };
        var module = Assert.Single(StorageMemoryAuditProvider.ReadMemory(rows, Availability.Available));
        Assert.Contains("2933 MT/s", module.RawValue);
        Assert.Equal("2933", module.Facts["ConfiguredTransferRateMTs"]);
        Assert.Equal("3600", module.Facts["Speed"]);
        Assert.Equal("Não lido", module.Facts["SPD"]);
        Assert.Equal("Não verificados", module.Facts["Channels"]);
        Assert.Equal("Não verificado", module.Facts["XMP"]);
        Assert.Equal("Não expostos", module.Facts["Timings"]);
        Assert.DoesNotContain("defeito", module.RawValue);
    }

    [Theory]
    [InlineData(0, 26)]
    [InlineData(2933, 0)]
    public void MissingRateOrUnknownMemoryTypeNeverInventsTransferRates(int configured, int type)
    {
        var module = Assert.Single(StorageMemoryAuditProvider.ReadMemory(
            [Row(("Capacity", 1024), ("ConfiguredClockSpeed", configured), ("SMBIOSMemoryType", type))], Availability.Available));
        Assert.Equal("Não verificada", module.Facts["ConfiguredTransferRateMTs"]);
        Assert.DoesNotContain("MT/s configurados", module.RawValue);
    }

    [Fact]
    public void TwoDisksKeepTheirOwnModelFirmwareHealthAndStableIdentity()
    {
        var first = Disk("0", "nvme-first", "Kingston test model", "FW1");
        var second = Disk("1", "nvme-second", "XPG test model", "FW2");
        second["HealthStatus"] = 2;
        var results = StorageMemoryAuditProvider.ReadDisks([first, second], Availability.Available);
        Assert.Contains("FW1", results[0].RawValue);
        Assert.DoesNotContain("FW2", results[0].RawValue);
        Assert.Contains("Unhealthy", results[1].RawValue);
        Assert.Equal("2", results[1].Facts["HealthStatus"]);
        Assert.Equal(results[0].InstanceId, StorageMemoryAuditProvider.ReadDisks([second, first], Availability.Available)[1].InstanceId);
    }

    [Fact]
    public void ReliabilityCountersAreMatchedByAssociationAndDeviceIdNeverModelOrPosition()
    {
        var first = Disk("0", "first", "Same model", "FW");
        var second = Disk("1", "second", "Same model", "FW");
        var results = StorageMemoryAuditProvider.ReadReliability([first, second],
            [Counter("1", 61), Counter("0", 37)], [Link("1"), Link("0")], Availability.Available);
        Assert.Equal("37 °C", results.Single(o => o.InstanceId == "physical:first:Temperature").RawValue);
        Assert.Equal("61 °C", results.Single(o => o.InstanceId == "physical:second:Temperature").RawValue);
        Assert.All(results.Where(o => o.Name == "ReadErrorsTotal"), o =>
        {
            Assert.Equal("0 eventos acumulados", o.RawValue);
            Assert.Equal(Availability.Available, o.Status);
        });
    }

    [Theory]
    [InlineData("missing-link")]
    [InlineData("duplicate-link")]
    [InlineData("duplicate-counter")]
    [InlineData("wrong-device")]
    [InlineData("missing-device")]
    [InlineData("shared-counter")]
    public void AmbiguousOrMismatchedReliabilityCannotBecomeAnotherDisksTemperature(string fault)
    {
        var disk = Disk("0", "first", "Model", "FW");
        var counter = Counter("0", 40);
        var links = new List<Dictionary<string, object?>> { Link("0") };
        var counters = new List<Dictionary<string, object?>> { counter };
        switch (fault)
        {
            case "missing-link": links.Clear(); break;
            case "duplicate-link": links.Add(Link("0")); break;
            case "duplicate-counter": counters.Add(Counter("0", 90)); break;
            case "wrong-device": counter["DeviceId"] = "1"; break;
            case "missing-device": disk.Remove("DeviceId"); counter.Remove("DeviceId"); break;
            case "shared-counter": links.Add(Row(("PhysicalDisk", "physical-other"), ("StorageReliabilityCounter", "counter-0"))); break;
        }
        var observation = Assert.Single(StorageMemoryAuditProvider.ReadReliability([disk], counters, links, Availability.Available));
        Assert.Equal(Availability.InsufficientData, observation.Status);
        Assert.Null(observation.RawValue);
    }

    [Fact]
    public void ZeroTemperatureIsUnavailableWhileZeroErrorCountRemainsValid()
    {
        var results = StorageMemoryAuditProvider.ReadReliability([Disk("0", "first", "Model", "FW")],
            [Counter("0", 0)], [Link("0")], Availability.Available);
        var temperature = results.Single(o => o.Name == "Temperature");
        Assert.Equal(Availability.InsufficientData, temperature.Status);
        Assert.Null(temperature.RawValue);
        Assert.Equal("0", temperature.Facts["RawValue"]);
        Assert.Equal(Availability.Available, results.Single(o => o.Name == "ReadErrorsTotal").Status);
        Assert.Equal(Availability.InsufficientData, results.Single(o => o.Name == "Wear").Status);
    }

    [Fact]
    public void VolumeMappingSeparatesWindowsAndPagefilesAndReportsUnavailableSources()
    {
        var physical = new[] { Disk("0", "FIRST", "Model A", "FW"), Disk("1", "SECOND", "Model B", "FW") };
        var logical = new[] { Row(("UniqueId", "first"), ("Number", 0)), Row(("UniqueId", "second"), ("Number", 1)) };
        var partitions = new[] { Row(("DiskNumber", 0), ("AccessPaths", new[] { "C:\\" })), Row(("DiskNumber", 1), ("AccessPaths", new[] { "D:\\" })) };
        var results = StorageMemoryAuditProvider.ReadLocations(physical, logical, partitions, [Row(("SystemDrive", "C:"))],
            [Row(("Name", "D:\\pagefile.sys"))], Availability.Available);
        Assert.Equal("Identificado", results[0].Facts["WindowsVolume"]);
        Assert.Equal("Não identificados nesta unidade", results[0].Facts["Pagefiles"]);
        Assert.Equal("Não identificado nesta unidade", results[1].Facts["WindowsVolume"]);
        Assert.Equal("D:\\pagefile.sys", results[1].Facts["Pagefiles"]);
        var unavailable = StorageMemoryAuditProvider.ReadLocations(physical, logical, partitions, [], [], Availability.Available,
            Availability.ReadError, Availability.PermissionRequired);
        Assert.All(unavailable, o => Assert.Equal("Consulta indisponível", o.Facts["Pagefiles"]));
    }

    [Fact]
    public void DuplicatePhysicalUniqueIdsAreRejectedCaseInsensitively()
    {
        var results = StorageMemoryAuditProvider.ReadLocations(
            [Disk("0", "DEVICE", "First", "FW"), Disk("1", "device", "Second", "FW")],
            [Row(("UniqueId", "DEVICE"), ("Number", 0))], [Row(("DiskNumber", 0), ("AccessPaths", new[] { "C:\\" }))],
            [Row(("SystemDrive", "C:"))], [], Availability.Available);
        Assert.All(results, o => { Assert.Equal(Availability.InsufficientData, o.Status); Assert.Null(o.RawValue); });
    }

    private static Dictionary<string, object?> Disk(string id, string unique, string model, string firmware) =>
        Row(("DeviceId", id), ("UniqueId", unique), ("Model", model), ("FirmwareVersion", firmware),
            ("HealthStatus", 0), ("BusType", 17), ("__PATH", "physical-" + id));
    private static Dictionary<string, object?> Counter(string id, int temperature) =>
        Row(("DeviceId", id), ("Temperature", temperature), ("ReadErrorsTotal", 0), ("__PATH", "counter-" + id));
    private static Dictionary<string, object?> Link(string id) =>
        Row(("PhysicalDisk", "physical-" + id), ("StorageReliabilityCounter", "counter-" + id));
    private static Dictionary<string, object?> Row(params (string Key, object? Value)[] fields) =>
        fields.ToDictionary(f => f.Key, f => f.Value, StringComparer.OrdinalIgnoreCase);
}
