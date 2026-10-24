using Microsoft.Win32;
using System.Diagnostics.CodeAnalysis;
using PCAnalyzer.Core;
using PCAnalyzer.Hardware;

namespace PCAnalyzer.Tests;

[UnconditionalSuppressMessage("Interoperability", "CA1416", Justification = "RegistryValueKind fixture constants are decoded as data; native provider execution is platform-guarded.")]
public sealed class WindowsAuditTests
{
    [Fact]
    public void ProcessIdentityKeepsAnalyzerAndForegroundSeparateFromTheSelectedGame()
    {
        var created = new DateTimeOffset(2026, 10, 23, 10, 0, 0, TimeSpan.Zero);
        var analyzer = WindowsConfigurationAuditProvider.ProcessIdentityFacts(10, 10, 20, "PCAnalyzer", @"C:\Tools\PCAnalyzer.exe", created);
        var desktop = WindowsConfigurationAuditProvider.ProcessIdentityFacts(20, 10, 20, "explorer", @"C:\Windows\explorer.exe", created);
        var game = WindowsConfigurationAuditProvider.ProcessIdentityFacts(30, 10, 20, "VALORANT-Win64-Shipping",
            @"C:\Games\VALORANT-Win64-Shipping.exe", created.AddSeconds(1));
        Assert.Equal("True", analyzer["IsAnalyzerProcess"]);
        Assert.Equal("True", desktop["foreground"]);
        Assert.Equal("explorer.exe", desktop["ExecutableName"]);
        Assert.Equal("VALORANT-Win64-Shipping.exe", game["ExecutableName"]);
        Assert.Equal("False", game["foreground"]);
        Assert.NotEqual(game["ProcessStartedAtUtc"], desktop["ProcessStartedAtUtc"]);
        Assert.All(new[] { analyzer, desktop, game }, facts =>
        {
            Assert.DoesNotContain("SelectedProcess", facts.Keys);
            Assert.DoesNotContain("IsSelectedProcess", facts.Keys);
            Assert.DoesNotContain("PriorityClass", facts.Keys);
            Assert.DoesNotContain("AffinityMask", facts.Keys);
        });
    }

    [Fact]
    public void UnavailableProcessPathAndCreationRemainUnavailableInsteadOfUsingAnalyzerIdentity()
    {
        var partial = WindowsConfigurationAuditProvider.ProcessIdentityFacts(41, 10, 20, "game", null, null);
        Assert.Equal("game.exe", partial["ExecutableName"]);
        Assert.Contains("caminho não confirmado", partial["ExecutableIdentitySource"]);
        Assert.DoesNotContain("ExecutablePath", partial.Keys);
        Assert.DoesNotContain("ProcessStartedAtUtc", partial.Keys);
        var missing = WindowsConfigurationAuditProvider.ProcessIdentityFacts(42, 10, 20, null, null, null);
        Assert.DoesNotContain("ExecutableName", missing.Keys);
        Assert.Equal("42", missing["PID"]);
    }

    [Theory]
    [InlineData(0x26, "0x00000026 (38 decimal)", "38")]
    [InlineData(0x2A, "0x0000002A (42 decimal)", "42")]
    [InlineData(-1, "0xFFFFFFFF (4294967295 decimal)", "4294967295")]
    public void DwordIsUnsignedAndKeepsHexadecimalSeparateFromDecimal(int value, string raw, string number)
    {
        var row = Registry("windows.scheduler.separation", "Win32PrioritySeparation", value);
        Assert.Equal(raw, row.RawValue);
        Assert.Equal(number, row.Facts["decimal"]);
        Assert.StartsWith(row.Facts["hexadecimal"], raw);
        Assert.Equal("DWord", row.Facts["registryType"]);
        Assert.Equal(AuditClassification.AppliedUnproven, Interpret(row).Classification);
    }

    [Fact]
    public void NonDwordFormatsPreserveBytesAndDoNotInterpretNumbersInsideStrings()
    {
        Assert.Equal("0xFFFFFFFFFFFFFFFF (18446744073709551615 decimal)",
            WindowsConfigurationAuditProvider.FormatRegistryValue(-1L, RegistryValueKind.QWord));
        Assert.Equal("00FF1026", WindowsConfigurationAuditProvider.FormatRegistryValue(new byte[] { 0, 255, 16, 38 }, RegistryValueKind.Binary));
        Assert.Equal(@"%TEMP%\first | D:\pagefile.sys", WindowsConfigurationAuditProvider.FormatRegistryValue(
            new[] { @"%TEMP%\first", @"D:\pagefile.sys" }, RegistryValueKind.MultiString));
        var row = Registry("windows.mmcss.responsiveness", "SystemResponsiveness", "0x26", RegistryValueKind.String);
        Assert.Equal("0x26", row.RawValue);
        Assert.False(row.Facts.ContainsKey("decimal"));
        Assert.Equal(AuditClassification.PossibleProblem, Interpret(row).Classification);
        Assert.DoesNotContain("Reserva", Interpret(row).Summary);
    }

    [Theory]
    [InlineData("windows.mmcss.responsiveness", "SystemResponsiveness")]
    [InlineData("windows.mmcss.gpu", "GPU Priority")]
    [InlineData("windows.gaming.hags", "HwSchMode")]
    public void MissingRegistryValueDoesNotBecomeZeroDefaultOrProblem(string rule, string name)
    {
        var row = WindowsConfigurationAuditProvider.RegistryObservation(rule, "HKLM\\fixture", name, "Sistema",
            null, RegistryValueKind.Unknown, false);
        Assert.Null(row.RawValue);
        Assert.Equal("absent", row.Facts["presence"]);
        Assert.Equal("false", row.Facts["configured"]);
        Assert.False(row.Facts.ContainsKey("decimal"));
        Assert.Equal(AuditClassification.InsufficientData, Interpret(row).Classification);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(9, 20)]
    [InlineData(10, 10)]
    [InlineData(19, 10)]
    [InlineData(27, 20)]
    [InlineData(99, 90)]
    [InlineData(101, 20)]
    [InlineData(-1, 20)]
    public void ResponsivenessUsesDocumentedRoundingAndClampingWithoutPromisingBenefit(int stored, int interpreted)
    {
        var assessment = Interpret(Registry("windows.mmcss.responsiveness", "SystemResponsiveness", stored));
        Assert.Equal(AuditClassification.AppliedUnproven, assessment.Classification);
        Assert.Contains(interpreted + "%", assessment.Summary);
        Assert.Contains("tarefas de baixa prioridade", assessment.Reason);
        Assert.Contains("Não foi observada", assessment.Reason);
    }

    [Fact]
    public void Responsiveness100FlagsDisabledMmcssWithoutClaimingGameCausality()
    {
        var assessment = Interpret(Registry("windows.mmcss.responsiveness", "SystemResponsiveness", 100));
        Assert.Equal(AuditClassification.PossibleProblem, assessment.Classification);
        Assert.Contains("desativado", assessment.Summary);
        Assert.Contains("não demonstra a causa", assessment.Reason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(9)]
    public void HighCategoryIgnoresStoredNumericPriorityButDoesNotClaimActualThreadPriority(int stored)
    {
        var row = Registry("windows.mmcss.priority", "Priority", stored, extra: new Dictionary<string, string> { ["SchedulingCategory"] = "High" });
        var assessment = Interpret(row);
        Assert.Equal(AuditClassification.AppliedUnproven, assessment.Classification);
        Assert.Contains("prioridade de tarefa 2", assessment.Summary);
        Assert.Contains("não comprova o valor de prioridade atual de uma thread", assessment.Reason);
        Assert.Equal(stored.ToString(), row.Facts["decimal"]);
    }

    [Fact]
    public void HighCategoryDoesNotHideWrongRegistryType()
    {
        var row = Registry("windows.mmcss.priority", "Priority", "8", RegistryValueKind.String,
            new Dictionary<string, string> { ["SchedulingCategory"] = "High" });
        var assessment = Interpret(row);
        Assert.Equal(AuditClassification.PossibleProblem, assessment.Classification);
        Assert.Contains("REG_DWORD", assessment.Reason);
    }

    [Theory]
    [InlineData("Priority", "windows.mmcss.priority", 0)]
    [InlineData("Priority", "windows.mmcss.priority", 9)]
    [InlineData("BackgroundPriority", "windows.mmcss.background", 9)]
    public void DocumentedPriorityRangeIsCheckedForOrdinaryTasks(string name, string rule, int value)
    {
        var row = Registry(rule, name, value, extra: new Dictionary<string, string> { ["SchedulingCategory"] = "Medium" });
        Assert.Equal(AuditClassification.PossibleProblem, Interpret(row).Classification);
        Assert.Contains("1 a 8", Interpret(row).Reason);
    }

    [Theory]
    [InlineData("windows.mmcss.category", "Scheduling Category", "Ultra")]
    [InlineData("windows.mmcss.background", "Background Only", "1")]
    public void DocumentedStringFieldsDoNotAcceptPopularNumericSubstitutes(string rule, string name, string value)
    {
        var row = Registry(rule, name, value, RegistryValueKind.String);
        Assert.Equal(AuditClassification.PossibleProblem, Interpret(row).Classification);
        Assert.Equal("true", row.Facts["invalid"]);
    }

    [Theory]
    [InlineData("windows.mmcss.gpu", "GPU Priority", 8)]
    [InlineData("windows.mmcss.sfio", "SFIO Priority", "High")]
    public void IgnoredMmcssFieldsNeverBecomeEffectiveGpuOrIoPriority(string rule, string name, object value)
    {
        var row = Registry(rule, name, value, value is int ? RegistryValueKind.DWord : RegistryValueKind.String);
        var assessment = Interpret(row);
        Assert.Equal(AuditClassification.NoDocumentedEffect, assessment.Classification);
        Assert.False(row.Facts.ContainsKey("effective"));
        Assert.Contains("não é utilizado", assessment.Reason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void UnrestrictedMmcssMaskDoesNotClaimUnrestrictedInterruptOrProcessAffinity(int mask)
    {
        var assessment = Interpret(Registry("windows.mmcss.affinity", "Affinity", mask));
        Assert.Equal(AuditClassification.AppliedUnproven, assessment.Classification);
        Assert.Contains("não impõe afinidade", assessment.Summary);
        Assert.Contains("não exclui restrições", assessment.Reason);
        Assert.Contains("não descreve a afinidade de interrupções", assessment.Reason);
    }

    [Fact]
    public void PersistentIfeoAndObservedClassRemainSeparateEvenWhenExecutableNameMatches()
    {
        var row = Registry("windows.ifeo.cpu", "CpuPriorityClass", 3, extra: new Dictionary<string, string>
        {
            ["executable"] = "real-game.exe", ["matchingProcesses"] = "1", ["observedProcessClasses"] = "PID 150: Normal"
        });
        var assessment = Interpret(row);
        Assert.Equal(AuditClassification.AppliedUnproven, assessment.Classification);
        Assert.Contains("PID 150: Normal", assessment.Reason);
        Assert.Contains("não comprova que o IFEO foi aplicado", assessment.Reason);
        Assert.False(row.Facts.ContainsKey("effective"));

        var absent = Registry("windows.ifeo.cpu", "CpuPriorityClass", 3, extra: new Dictionary<string, string> { ["matchingProcesses"] = "0" });
        Assert.Contains("sem processo correspondente", Interpret(absent).Summary);
        Assert.Contains("não prova que o programa foi removido", Interpret(absent).Reason);
    }

    [Fact]
    public void OnlyObservedRealtimeClassRaisesSchedulingConcern()
    {
        var row = new AuditObservation("windows.process.scheduling", "pid:150", "real-game", "Processo", "RealTime",
            Availability.Available, "Process.PriorityClass", new Dictionary<string, string>
            { ["configured"] = "false", ["PriorityClass"] = "RealTime", ["effective"] = "Classe: RealTime" });
        Assert.Equal(AuditClassification.PossibleProblem, Interpret(row).Classification);
        Assert.Contains("não demonstra contenção", Interpret(row).Reason);
        var ordinary = row with { RawValue = "Normal", Facts = new Dictionary<string, string>
            { ["configured"] = "false", ["PriorityClass"] = "Normal", ["effective"] = "Classe: Normal" } };
        Assert.Equal(AuditClassification.Adequate, Interpret(ordinary).Classification);
        Assert.Equal(AuditClassification.InsufficientData, Interpret(row with { Status = Availability.PermissionRequired, Limitation = "Sem acesso ao processo" }).Classification);
    }

    [Fact]
    public void BcdParserRequiresExactIdentifierAndNeverInventsDefaultForMissingEntry()
    {
        const string text = "Carregador de inicialização do Windows\r\nuseplatformclockoverride Sim\r\n  useplatformclock    Não\r\n TscSyncPolicy   Enhanced\r\n";
        Assert.Equal("Não", WindowsConfigurationAuditProvider.ParseBcdValue(text, "useplatformclock"));
        Assert.Equal("Enhanced", WindowsConfigurationAuditProvider.ParseBcdValue(text, "tscsyncpolicy"));
        Assert.Null(WindowsConfigurationAuditProvider.ParseBcdValue(text, "disabledynamictick"));
        var row = new AuditObservation("windows.bcd.useplatformclock", "bcd:useplatformclock", "useplatformclock", "Boot", "Não",
            Availability.Available, "bcdedit /enum {current}", new Dictionary<string, string> { ["presence"] = "present", ["configured"] = "true" });
        Assert.Equal(AuditClassification.Adequate, Interpret(row).Classification);
        Assert.Contains("não confirma a fonte de temporização efetiva", Interpret(row).Reason);
        Assert.Equal(AuditRecommendationKind.KeepCurrent, AuditRecommendationClassifier.Classify(Interpret(row)).Kind);
    }

    [Fact]
    public async Task NonWindowsProviderExposesUnsupportedCoverageWithoutCallingWindowsApis()
    {
        if (OperatingSystem.IsWindows()) return; // Native validation is performed by PCAnalyzer.Verify on Windows.
        var provider = new WindowsConfigurationAuditProvider();
        var rows = await provider.ReadAsync(default);
        Assert.Equal(provider.Rules.Count, rows.Count);
        Assert.All(rows, row => { Assert.Null(row.RawValue); Assert.Equal(Availability.Unsupported, row.Status); });
    }

    private static AuditObservation Registry(string rule, string name, object value, RegistryValueKind kind = RegistryValueKind.DWord,
        IReadOnlyDictionary<string, string>? extra = null) =>
        WindowsConfigurationAuditProvider.RegistryObservation(rule, "HKLM\\fixture :: " + name, name, "Fixture", value, kind, true, extra);
    private static AuditAssessment Interpret(AuditObservation row) =>
        Assert.Single(PerformanceAuditEngine.Evaluate(WindowsAuditCatalog.Rules, [row], DateTimeOffset.UtcNow).Assessments);
}
