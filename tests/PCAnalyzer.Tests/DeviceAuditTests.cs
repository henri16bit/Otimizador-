using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Win32;
using PCAnalyzer.Core;
using PCAnalyzer.Hardware;

namespace PCAnalyzer.Tests;

[UnconditionalSuppressMessage("Interoperability", "CA1416", Justification = "Fixtures use RegistryValueKind constants as data; no Windows API or registry operation is called.")]
public sealed class DeviceAuditTests
{
    private static readonly AuditPnpDevice Device = new(@"PCI\VEN_8086&DEV_15FA\1", "Ethernet", "Net",
        "", "e1dexpress", @"PCI\VEN_8086&DEV_15FA", "PCI", "PCI bus 0", "", 0, 0, []);

    [Theory]
    [InlineData(RegistryValueKind.String, @"\SystemRoot\System32\drivers\different-binary.sys")]
    [InlineData(RegistryValueKind.ExpandString, @"%SystemRoot%\System32\drivers\different-binary.sys")]
    public void DeviceBinaryIdentityComesFromActualServiceImagePath(RegistryValueKind kind, string path)
    {
        var facts = DeviceInterruptAuditProvider.DriverImageFacts(Device.Service, new(true, path, kind));
        var identity = DeviceInterruptAuditProvider.IdentityObservation(Device, facts);
        Assert.Equal(path, identity.Facts["driverImagePath"]);
        Assert.Equal(Availability.Available.ToString(), identity.Facts["driverImagePathStatus"]);
        Assert.Equal(kind.ToString(), identity.Facts["driverImagePathRegistryType"]);
        Assert.Equal(@"HKLM\SYSTEM\CurrentControlSet\Services\e1dexpress\ImagePath", identity.Facts["driverImagePathSource"]);
        Assert.DoesNotContain("e1dexpress.sys", identity.Facts["driverImagePath"]);
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("denied")]
    [InlineData("wrong-type")]
    [InlineData("empty")]
    [InlineData("invalid-service")]
    public void UnreadableOrInvalidServiceImagePathNeverBecomesAConventionalDriverName(string scenario)
    {
        var value = scenario switch
        {
            "absent" => new DeviceRegistryValue(false, null, null),
            "denied" => new(false, null, null, Availability.PermissionRequired, "Acesso negado"),
            "wrong-type" => new(true, 1, RegistryValueKind.DWord),
            "empty" => new(true, "  ", RegistryValueKind.String),
            _ => new(true, @"C:\drivers\e1dexpress.sys", RegistryValueKind.String)
        };
        var facts = DeviceInterruptAuditProvider.DriverImageFacts(scenario == "invalid-service" ? @"bad\other" : Device.Service, value);
        Assert.False(facts.ContainsKey("driverImagePath"));
        if (scenario == "denied") Assert.Equal(Availability.PermissionRequired.ToString(), facts["driverImagePathStatus"]);
        if (scenario == "absent") Assert.Equal("absent", facts["driverImagePathPresence"]);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void NativeCoreRecordsPreserveGroupsAndNonAdjacentSmtPairs(int pointerSize)
    {
        var bytes = CoreRecord(pointerSize, 0, 0b0101, true)
            .Concat(CoreRecord(pointerSize, 0, 0b1010, true))
            .Concat(CoreRecord(pointerSize, 1, 1, false)).ToArray();
        var cores = CpuTopologyReader.Parse(bytes, pointerSize);
        Assert.Equal(3, cores.Count);
        Assert.Equal(new[] { 0, 2 }, cores[0].LogicalProcessors);
        Assert.Equal(new[] { 1, 3 }, cores[1].LogicalProcessors);
        Assert.Equal((ushort)1, cores[2].Group);
        Assert.False(cores[2].Smt);
        var row = DeviceInterruptAuditProvider.TopologyObservation(cores);
        Assert.Equal("3", row.Facts["physicalCoreCount"]);
        Assert.Equal("5", row.Facts["logicalProcessorCount"]);
        Assert.Equal("2", row.Facts["processorGroupCount"]);
        Assert.Contains("SMT: 0/2", DeviceInterruptAuditProvider.MapMask(1, 0, cores));
        Assert.Contains("núcleo 2", DeviceInterruptAuditProvider.MapMask(1, 1, cores));
    }

    [Fact]
    public void TopologyRetainsHighAffinityBitAndRejectsRepeatedCpuWithinGroup()
    {
        var high = CoreRecord(8, 0, 1UL << 63, false);
        Assert.Equal(new[] { 63 }, Assert.Single(CpuTopologyReader.Parse(high, 8)).LogicalProcessors);
        var repeated = high.Concat(high).ToArray();
        Assert.Throws<InvalidDataException>(() => CpuTopologyReader.Parse(repeated, 8));
    }

    [Theory]
    [InlineData("header")]
    [InlineData("size")]
    [InlineData("relationship")]
    [InlineData("group-count")]
    [InlineData("empty-mask")]
    public void MalformedNativeTopologyIsNeverInvented(string error)
    {
        var bytes = CoreRecord(8, 0, 1, false);
        switch (error)
        {
            case "header": bytes = bytes[..7]; break;
            case "size": BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), uint.MaxValue); break;
            case "relationship": BinaryPrimitives.WriteUInt32LittleEndian(bytes, 4); break;
            case "group-count": BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(30), 2); break;
            case "empty-mask": BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), 0); break;
        }
        Assert.Throws<InvalidDataException>(() => CpuTopologyReader.Parse(bytes, 8));
    }

    [Fact]
    public void RegistryBinaryAffinityIsLittleEndianAndBoundedByNativeWidth()
    {
        Assert.Equal(0x0102UL, InterruptAffinityDecoder.DecodeBinary(new byte[] { 2, 1 }, 8));
        Assert.Equal(new[] { 1, 8 }, InterruptAffinityDecoder.Processors(0x0102));
        Assert.Equal(1UL << 63, InterruptAffinityDecoder.DecodeBinary(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0x80 }, 8));
        Assert.Throws<InvalidDataException>(() => InterruptAffinityDecoder.DecodeBinary(new byte[8], 4));
        Assert.Throws<InvalidDataException>(() => InterruptAffinityDecoder.DecodeBinary([], 8));
        Assert.Throws<InvalidDataException>(() => InterruptAffinityDecoder.DecodeBinary(new byte[9], 8));
    }

    [Fact]
    public void DwordAndQwordAffinityPreserveUnsignedHighBitsWithoutParsingStrings()
    {
        Assert.Equal(1UL << 31, DeviceInterruptAuditProvider.DecodeAffinity(new(true, int.MinValue, RegistryValueKind.DWord), 8));
        Assert.Equal(1UL << 63, DeviceInterruptAuditProvider.DecodeAffinity(new(true, long.MinValue, RegistryValueKind.QWord), 8));
        Assert.Throws<InvalidDataException>(() => DeviceInterruptAuditProvider.DecodeAffinity(new(true, long.MinValue, RegistryValueKind.QWord), 4));
        Assert.Throws<InvalidDataException>(() => DeviceInterruptAuditProvider.DecodeAffinity(new(true, "0x03", RegistryValueKind.String), 8));
    }

    [Fact]
    public void PersistentMaskNeedsPolicyFourAndDoesNotInferGroupOrAppliedAffinity()
    {
        var cores = new[] { new CpuCore(0, 0, 3, true, 0), new CpuCore(1, 1, 3, true, 0) };
        var values = RegistryValues();
        values["AssignmentSetOverride"] = new(true, new byte[] { 3 }, RegistryValueKind.Binary);
        var row = Assert.Single(DeviceInterruptAuditProvider.ConfigurationObservations(Device, values, cores, 8),
            x => x.RuleId == "devices.interrupt.affinity");
        Assert.Equal("0, 1", row.Facts["logicalBits"]);
        Assert.StartsWith("Indeterminado", row.Facts["groupContext"]);
        Assert.Contains("não foi confirmado", row.Facts["maskPolicyApplies"]);
        Assert.False(row.Facts.ContainsKey("topologyMapping"));
        Assert.False(row.Facts.ContainsKey("effective"));
        values["DevicePolicy"] = new(true, 4, RegistryValueKind.DWord);
        row = Assert.Single(DeviceInterruptAuditProvider.ConfigurationObservations(Device, values, cores, 8),
            x => x.RuleId == "devices.interrupt.affinity");
        Assert.Contains("aplicação efetiva não verificada", row.Facts["maskPolicyApplies"]);
    }

    [Fact]
    public void MaskOutsideSingleGroupTopologyIsFlaggedButSharedCpuIsNotAConflict()
    {
        var cores = new[] { new CpuCore(0, 0, 5, true, 0), new CpuCore(1, 0, 10, true, 0) };
        var values = RegistryValues();
        values["DevicePolicy"] = new(true, 4, RegistryValueKind.DWord);
        values["AssignmentSetOverride"] = new(true, new byte[] { 16 }, RegistryValueKind.Binary);
        var row = Assert.Single(DeviceInterruptAuditProvider.ConfigurationObservations(Device, values, cores, 8),
            x => x.RuleId == "devices.interrupt.affinity");
        Assert.Contains("não presentes", row.Facts["invalid"]);
        values["AssignmentSetOverride"] = new(true, new byte[] { 1 }, RegistryValueKind.Binary);
        var rows = DeviceInterruptAuditProvider.ConfigurationObservations(Device, values, cores, 8)
            .Concat(DeviceInterruptAuditProvider.ConfigurationObservations(Device with { InstanceId = "other" }, values, cores, 8)).ToArray();
        var report = PerformanceAuditEngine.Evaluate(new DeviceInterruptAuditProvider().Rules, rows, DateTimeOffset.UtcNow);
        Assert.DoesNotContain(report.Assessments, x => x.Classification is AuditClassification.DemonstratedProblem or AuditClassification.PossibleProblem);
    }

    [Fact]
    public void MissingRegistryValuesRemainDistinctFromPermissionFailureAndWrongType()
    {
        var values = RegistryValues();
        values["MessageNumberLimit"] = new(false, null, null, Availability.PermissionRequired, "Leitura negada");
        values["DevicePolicy"] = new(true, "4", RegistryValueKind.String);
        var rows = DeviceInterruptAuditProvider.ConfigurationObservations(Device, values, [], 8);
        var absent = Assert.Single(rows, x => x.RuleId == "devices.interrupt.msi");
        Assert.Equal("absent", absent.Facts["presence"]);
        Assert.Null(absent.RawValue);
        var denied = Assert.Single(rows, x => x.RuleId == "devices.interrupt.limit");
        Assert.Equal(Availability.PermissionRequired, denied.Status);
        Assert.False(denied.Facts.ContainsKey("presence"));
        var wrong = Assert.Single(rows, x => x.RuleId == "devices.interrupt.policy");
        Assert.Contains("REG_DWORD", wrong.Facts["invalid"]);
    }

    [Fact]
    public void MsiRegistryCapabilityAndAllocatedIrqRemainSeparateEvidence()
    {
        var values = RegistryValues();
        values["MSISupported"] = new(true, 1, RegistryValueKind.DWord);
        var msi = Assert.Single(DeviceInterruptAuditProvider.ConfigurationObservations(Device, values, [], 8),
            x => x.RuleId == "devices.interrupt.msi");
        var capability = DeviceInterruptAuditProvider.CapabilityObservation(Device, new(7, Availability.Available), new(32, Availability.Available));
        var allocated = DeviceInterruptAuditProvider.AllocatedObservation(Device,
            new([new(45, 1, 4, 3)], Availability.Available), [new(0, 1, 4, false, 0)]);
        Assert.False(msi.Facts.ContainsKey("effective"));
        Assert.False(capability.Facts.ContainsKey("effective"));
        Assert.Equal("True", capability.Facts["msiCapable"]);
        Assert.Equal("32", capability.Facts["hardwareMessageMaximum"]);
        Assert.Contains("grupo 1", allocated.Facts["allocatedResources"]);
        Assert.Contains("não foi determinado", allocated.Limitation!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("MSI ativo", allocated.Facts["effective"]);
    }

    [Fact]
    public void AllocatedIrqNativeHeaderKeepsGroupSeparateFromFlagsAndRejectsTruncatedRanges()
    {
        var bytes = new byte[36]; // IRQ_DES_64 plus one IRQ_RANGE.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 12);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(10), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), 213);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), 1UL << 63);
        Assert.Equal(new(213, 7, 1UL << 63, 3), InterruptResourceDecoder.Parse(bytes));
        Assert.Throws<InvalidDataException>(() => InterruptResourceDecoder.Parse(bytes.AsSpan(0, 23)));
        Assert.Throws<InvalidDataException>(() => InterruptResourceDecoder.Parse(bytes.AsSpan(0, 24)));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 16);
        Assert.Throws<InvalidDataException>(() => InterruptResourceDecoder.Parse(bytes));
    }

    [Fact]
    public void PartialAllocatedReadPreservesResourcesAndItsError()
    {
        var row = DeviceInterruptAuditProvider.AllocatedObservation(Device,
            new([new(25, 0, 3, 1)], Availability.ReadError, "A consulta seguinte falhou"), []);
        Assert.Equal(Availability.ReadError, row.Status);
        Assert.Equal("1", row.Facts["allocatedCount"]);
        Assert.Equal("A consulta seguinte falhou", row.Limitation);
    }

    [Fact]
    public void UsbTreePreservesRealParentsAndBoundsCyclesWithoutInferringPolling()
    {
        var controller = Device with { InstanceId = "controller", Name = "Controlador USB", Class = "USB", ParentId = "" };
        var hub = Device with { InstanceId = @"USB\hub", Name = "Hub USB", ParentId = controller.InstanceId };
        var mouse = Device with { InstanceId = @"HID\mouse", Name = "Mouse", ParentId = hub.InstanceId };
        var devices = new[] { controller, hub, mouse }.ToDictionary(x => x.InstanceId, StringComparer.OrdinalIgnoreCase);
        var row = DeviceInterruptAuditProvider.UsbObservation(mouse, devices);
        Assert.Equal("Mouse ← Hub USB ← Controlador USB", row.RawValue);
        Assert.Contains("polling", row.Limitation);
        devices[hub.InstanceId] = hub with { ParentId = mouse.InstanceId };
        Assert.Contains("Ciclo", DeviceInterruptAuditProvider.UsbObservation(mouse, devices).Limitation);
        devices.Remove(hub.InstanceId);
        Assert.Contains("árvore parcial", DeviceInterruptAuditProvider.UsbObservation(mouse, devices).Limitation);
    }

    private static Dictionary<string, DeviceRegistryValue> RegistryValues() => new()
    {
        ["MSISupported"] = new(false, null, null), ["MessageNumberLimit"] = new(false, null, null),
        ["DevicePolicy"] = new(false, null, null), ["AssignmentSetOverride"] = new(false, null, null)
    };

    private static byte[] CoreRecord(int pointerSize, ushort group, ulong mask, bool smt)
    {
        var bytes = new byte[32 + pointerSize + 8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), checked((uint)bytes.Length));
        bytes[8] = smt ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(30), 1);
        if (pointerSize == 8) BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(32), mask);
        else BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(32), checked((uint)mask));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(32 + pointerSize), group);
        return bytes;
    }
}
