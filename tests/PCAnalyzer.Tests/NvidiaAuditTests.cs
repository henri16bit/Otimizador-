using System.Runtime.InteropServices;
using PCAnalyzer.Core;
using PCAnalyzer.Hardware;

namespace PCAnalyzer.Tests;

public sealed class NvidiaAuditTests
{
    private const string GamePath = @"C:\Games\VALORANT\VALORANT-Win64-Shipping.exe";
    private static readonly uint PowerId = NvidiaAuditSettings.All.Single(x => x.Key == "power").Id;

    [Fact]
    public async Task ReadsRealPathAssociationAndPreservesInheritedUserValueWithoutCallingItLocalOrEffective()
    {
        var native = new FakeDrs();
        native.Applications[GamePath] = App(3, "VALORANT");
        native.Settings[(3, PowerId)] = new(PowerId, 0, 1, false, true, 1, 5);
        var provider = Provider(native, new NvDrsTarget(GamePath, [41, 42], true));
        var rows = await provider.ReadAsync(default);
        Assert.Contains(GamePath, native.Lookups);
        Assert.DoesNotContain("VALORANT-Win64-Shipping.exe", native.Lookups);
        var association = Assert.Single(rows, x => x.RuleId == "nvidia.applications" && x.Facts.ContainsKey("profile"));
        Assert.Equal("41, 42", association.Facts["runningProcessIds"]);
        Assert.Equal("true", association.Facts["observedProcess"]);
        var power = Assert.Single(rows, x => x.RuleId == "nvidia.drs.power" && x.Scope.StartsWith("Aplicativo:"));
        Assert.Equal("Herdado do perfil global", power.Facts["origin"]);
        Assert.Equal("false", power.Facts["explicitInProfile"]);
        Assert.Equal("true", power.Facts["configured"]);
        Assert.Equal(GamePath, power.Facts["executable"]);
        Assert.Equal("VALORANT-Win64-Shipping.exe", power.Facts["executableName"]);
        Assert.Equal("true", power.Facts["observedProcess"]);
        Assert.Equal("application", power.Facts["profileScope"]);
        Assert.Equal("Não identificada", power.Facts["GraphicsApi"]);
        Assert.Equal(Availability.InsufficientData.ToString(), power.Facts["GraphicsApiStatus"]);
        Assert.False(power.Facts.ContainsKey("effective"));
        Assert.Contains("Energia otimizada", power.Facts["predefinedValue"]);
        Assert.True(native.Disposed);
    }

    [Fact]
    public async Task GlobalDifferenceDoesNotBecomeAnOptimizationWithoutApplicableEvidence()
    {
        var native = new FakeDrs();
        native.Applications[GamePath] = App(3, "VALORANT");
        native.Settings[(1, PowerId)] = new(PowerId, 0, 0, true, true, 5, 5);
        native.Settings[(3, PowerId)] = new(PowerId, 0, 0, false, true, 1, 5);
        var provider = Provider(native, new NvDrsTarget(GamePath, [7], true));
        var report = PerformanceAuditEngine.Evaluate(provider.Rules, await provider.ReadAsync(default), DateTimeOffset.UtcNow);
        var assessment = Assert.Single(report.Assessments, x => x.Rule.Id == "nvidia.drs.power" && x.Observation.Scope.StartsWith("Aplicativo:"));
        Assert.NotEqual(AuditClassification.ComparisonOpportunity, assessment.Classification);
        Assert.Equal("true", assessment.Observation.Facts["explicitInProfile"]);
        Assert.Equal("true", assessment.Observation.Facts["differsFromGlobal"]);
        Assert.Contains("falta uma captura", assessment.Observation.Facts["comparison"]);
        var recommendation = AuditRecommendationClassifier.Classify(assessment);
        Assert.Equal(AuditRecommendationKind.KeepCurrent, recommendation.Kind);
        Assert.False(recommendation.IsImprovement);
        Assert.Null(recommendation.BenefitConfidence);
        Assert.DoesNotContain(report.Assessments, x => x.Classification == AuditClassification.DemonstratedProblem);
    }

    [Fact]
    public async Task NameOnlyCandidateDoesNotClaimGameIsInstalledRunningOrUsingProfile()
    {
        var native = new FakeDrs();
        native.Applications["VALORANT-Win64-Shipping.exe"] = App(3, "VALORANT") with { FileInFolder = "game.dll", Flags = 2, CommandLine = "-game" };
        var rows = await Provider(native).ReadAsync(default);
        var candidate = Assert.Single(rows, x => x.Facts.ContainsKey("profile") && x.RuleId == "nvidia.applications");
        Assert.Equal("false", candidate.Facts["observedProcess"]);
        Assert.Contains("nenhum processo", candidate.Limitation);
        Assert.Contains("critérios adicionais", candidate.Limitation);
        Assert.Equal("game.dll", candidate.Facts["fileInFolder"]);
        Assert.Equal("-game", candidate.Facts["commandLine"]);
        var power = Assert.Single(rows, x => x.RuleId == "nvidia.drs.power" && x.Scope.StartsWith("Aplicativo:"));
        Assert.Equal("false", power.Facts["observedProcess"]);
        Assert.Equal("game.dll", power.Facts["fileInFolder"]);
        Assert.Equal("-game", power.Facts["commandLine"]);
        Assert.False(power.Facts.ContainsKey("effective"));
    }

    [Fact]
    public async Task DesktopProcessAndGameCandidateKeepSeparateProfilesWithoutInventingGraphicsApi()
    {
        const string desktop = @"C:\Windows\explorer.exe";
        var native = new FakeDrs();
        native.Applications[desktop] = App(3, "Desktop");
        native.Applications["VALORANT-Win64-Shipping.exe"] = App(4, "VALORANT");
        var rows = await Provider(native, new NvDrsTarget(desktop, [78], true)).ReadAsync(default);
        var desktopSetting = Assert.Single(rows, x => x.RuleId == "nvidia.drs.power" && x.Facts.GetValueOrDefault("executable") == desktop);
        var gameSetting = Assert.Single(rows, x => x.RuleId == "nvidia.drs.power" && x.Facts.GetValueOrDefault("executable") == "VALORANT-Win64-Shipping.exe");
        Assert.Equal("Desktop", desktopSetting.Facts["profile"]);
        Assert.Equal("78", desktopSetting.Facts["runningProcessIds"]);
        Assert.Equal("false", gameSetting.Facts["observedProcess"]);
        Assert.Equal("", gameSetting.Facts["runningProcessIds"]);
        Assert.All(rows.Where(x => x.RuleId.StartsWith("nvidia.drs.") && x.Status == Availability.Available), row =>
        {
            Assert.Equal("Não identificada", row.Facts["GraphicsApi"]);
            Assert.DoesNotContain("SelectedProcess", row.Facts.Keys);
        });
        var global = Assert.Single(rows, x => x.RuleId == "nvidia.drs.power" && x.Scope == "Perfil global");
        Assert.Equal("global", global.Facts["profileScope"]);
        Assert.DoesNotContain("executable", global.Facts.Keys);
    }

    [Fact]
    public async Task AbsentGameAndPrivateOptionsRemainUnknownWhileGlobalReadsWork()
    {
        var native = new FakeDrs();
        var provider = Provider(native);
        var rows = await provider.ReadAsync(default);
        var missing = Assert.Single(rows, x => x.RuleId == "nvidia.applications" && x.Facts.GetValueOrDefault("presence") == "absent");
        Assert.Null(missing.RawValue);
        Assert.Contains(rows, x => x.RuleId == "nvidia.drs.power" && x.Scope == "Perfil global" && x.Status == Availability.Available);
        Assert.All(rows.Where(x => x.RuleId is "nvidia.low-latency" or "nvidia.reflex"), x =>
        { Assert.Equal(Availability.InsufficientData, x.Status); Assert.Null(x.RawValue); });
        Assert.All(native.ReadIds, id => Assert.Contains(NvidiaAuditSettings.All, x => x.Id == id));
    }

    [Theory]
    [InlineData(-160, Availability.Unsupported)]
    [InlineData(-137, Availability.PermissionRequired)]
    [InlineData(-1, Availability.ReadError)]
    public async Task IndividualNativeFailureDoesNotDiscardOtherSettings(int status, Availability expected)
    {
        var native = new FakeDrs { FailingSetting = PowerId, FailureStatus = status };
        var rows = await Provider(native).ReadAsync(default);
        var power = Assert.Single(rows, x => x.RuleId == "nvidia.drs.power" && x.Scope == "Perfil global");
        Assert.Equal(expected, power.Status);
        Assert.Null(power.RawValue);
        Assert.Equal(status.ToString(), power.Facts["nvapiStatus"]);
        Assert.Contains(rows, x => x.RuleId == "nvidia.drs.frame-limit" && x.Status == Availability.Available);
    }

    [Fact]
    public async Task AmbiguousNameIsNotResolvedArbitrarily()
    {
        var native = new FakeDrs { FindError = -182 };
        var rows = await Provider(native, new NvDrsTarget("game.exe", [77], false)).ReadAsync(default);
        Assert.Contains(rows, x => x.Name == "Consulta de game.exe" && x.Status == Availability.InsufficientData && x.Limitation!.Contains("mais de uma"));
        Assert.DoesNotContain(rows, x => x.RuleId.StartsWith("nvidia.drs.") && x.Scope.StartsWith("Aplicativo:"));
    }

    [Fact]
    public async Task UnsupportedTypeIsNeverDecodedAsDwordSetting()
    {
        var native = new FakeDrs();
        native.Settings[(1, PowerId)] = new(PowerId, 1, 0, true, true, 1, 5);
        var rows = await Provider(native).ReadAsync(default);
        var row = Assert.Single(rows, x => x.RuleId == "nvidia.drs.power" && x.Scope == "Perfil global");
        Assert.Equal(Availability.ReadError, row.Status);
        Assert.Null(row.RawValue);
        Assert.DoesNotContain("decimal", row.Facts.Keys);
    }

    [Fact]
    public async Task MissingDriverEmitsCoverageWithoutInventingDefaults()
    {
        var provider = new NvidiaDriverAuditProvider(() => throw new DllNotFoundException("Driver ausente"), () => new([], 0, 0, false));
        var rows = await provider.ReadAsync(default);
        Assert.Equal(provider.Rules.Count, rows.Count);
        Assert.All(rows, x => { Assert.Null(x.RawValue); Assert.NotEqual(Availability.Available, x.Status); });
    }

    [Fact]
    public async Task CancellationStopsNextNativeReadAndDisposesSnapshot()
    {
        using var cancel = new CancellationTokenSource();
        var native = new FakeDrs { AfterRead = cancel.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Provider(native).ReadAsync(cancel.Token));
        Assert.Single(native.ReadIds);
        Assert.True(native.Disposed);
    }

    [Fact]
    public void NativeLayoutsMatchPublicPackedHeaderAndDecoderUsesDistinctUnions()
    {
        Assert.Equal(12320, Marshal.SizeOf<SettingV1>());
        Assert.Equal(NvapiDrsReader.SettingBytes, Marshal.SizeOf<SettingV1>());
        Assert.Equal(NvapiDrsReader.SettingPredefinedOffset, Marshal.OffsetOf<SettingV1>(nameof(SettingV1.Predefined)).ToInt32());
        Assert.Equal(NvapiDrsReader.SettingCurrentOffset, Marshal.OffsetOf<SettingV1>(nameof(SettingV1.Current)).ToInt32());
        Assert.Equal(NvapiDrsReader.ProfileBytes, Marshal.SizeOf<ProfileV1>());
        Assert.Equal(NvapiDrsReader.ApplicationBytes(1), Marshal.SizeOf<ApplicationV1>());
        Assert.Equal(NvapiDrsReader.ApplicationBytes(2), Marshal.SizeOf<ApplicationV2>());
        Assert.Equal(NvapiDrsReader.ApplicationBytes(3), Marshal.SizeOf<ApplicationV3>());
        Assert.Equal(NvapiDrsReader.ApplicationBytes(4), Marshal.SizeOf<ApplicationV4>());
        Assert.Equal(16396, Marshal.OffsetOf<ApplicationV4>(nameof(ApplicationV4.CommandLine)).ToInt32());
        var memory = Marshal.AllocHGlobal(NvapiDrsReader.SettingBytes);
        try
        {
            var data = new SettingV1 { Version = (1 << 16) | 12320, Name = new ushort[2048], Id = PowerId,
                Type = 0, Location = 1, CurrentPredefined = 0, PredefinedValid = 1,
                Predefined = new() { Value = 5 }, Current = new() { Value = 1 } };
            Marshal.StructureToPtr(data, memory, false);
            var actual = NvapiDrsReader.DecodeSetting(memory, PowerId);
            Assert.Equal(new(PowerId, 0, 1, false, true, 1, 5), actual);
            Assert.Throws<InvalidDataException>(() => NvapiDrsReader.DecodeSetting(memory, 123));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    [Fact]
    public void NativeUnicodeReadIsBoundedEvenWithoutTerminator()
    {
        var memory = Marshal.AllocHGlobal(NvapiDrsReader.UnicodeBytes);
        try
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(new string('X', 2048));
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            Assert.Equal(2048, NvapiDrsReader.Unicode(memory, 0).Length);
            Marshal.WriteInt16(memory, 4, 0);
            Assert.Equal("XX", NvapiDrsReader.Unicode(memory, 0));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    private static NvidiaDriverAuditProvider Provider(FakeDrs native, params NvDrsTarget[] targets) =>
        new(() => native, () => new(targets, targets.Sum(x => x.ProcessIds.Count), 0, false));
    private static NvDrsApplication App(nint handle, string name) => new(new(handle, name, true), "game.exe", true, "", 0, "", 4);

    private sealed class FakeDrs : INvapiDrsReader
    {
        public Dictionary<string, NvDrsApplication> Applications { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(nint, uint), NvDrsSetting> Settings { get; } = [];
        public List<string> Lookups { get; } = [];
        public List<uint> ReadIds { get; } = [];
        public uint? FailingSetting { get; init; }
        public int FailureStatus { get; init; }
        public int? FindError { get; init; }
        public Action? AfterRead { get; init; }
        public bool Disposed { get; private set; }
        public NvDrsProfile GetGlobalProfile() => new(1, "Global", true);
        public NvDrsProfile GetBaseProfile() => new(2, "Base", true);
        public NvDrsApplication FindApplication(string executable)
        {
            Lookups.Add(executable);
            if (FindError is { } error) throw new NvDrsException("FindApplication", error);
            return Applications.TryGetValue(executable, out var app) ? app : throw new NvDrsException("FindApplication", -166);
        }
        public NvDrsSetting ReadSetting(nint profile, uint settingId)
        {
            ReadIds.Add(settingId);
            AfterRead?.Invoke();
            if (settingId == FailingSetting) throw new NvDrsException("GetSetting", FailureStatus);
            return Settings.GetValueOrDefault((profile, settingId)) ?? new(settingId, 0, 0, true, true, 0, 0);
        }
        public void Dispose() => Disposed = true;
    }

    // Independent header-shaped declarations exercise packing and array widths, not the decoder's offsets.
    [StructLayout(LayoutKind.Explicit, Size = 4100, Pack = 4)] private struct SettingUnion { [FieldOffset(0)] public uint Value; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct SettingV1
    {
        public uint Version;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2048)] public ushort[] Name;
        public uint Id, Type, Location, CurrentPredefined, PredefinedValid;
        public SettingUnion Predefined, Current;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProfileV1
    {
        public uint Version;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2048)] public ushort[] Name;
        public uint GpuSupport, Predefined, Applications, Settings;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ApplicationV1
    {
        public uint Version, Predefined;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2048)] public ushort[] Name;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2048)] public ushort[] FriendlyName;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2048)] public ushort[] Launcher;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ApplicationV2
    {
        public ApplicationV1 V1;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2048)] public ushort[] FileInFolder;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ApplicationV3 { public ApplicationV2 V2; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct ApplicationV4
    {
        public ApplicationV3 V3;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2048)] public ushort[] CommandLine;
    }
}
