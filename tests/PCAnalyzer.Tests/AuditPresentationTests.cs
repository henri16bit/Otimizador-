using PCAnalyzer.Core;

namespace PCAnalyzer.Tests;

public sealed class AuditPresentationTests
{
    [Fact]
    public void KnownVirtualNetworkSettingsStayInformationalWithoutLosingEvidence()
    {
        var observation = Observation("network.advanced", "virtual-network", "Interrupt Moderation", "Disabled", new()
            { ["HardwareInterface"] = "False" });
        var row = Assert.Single(AuditPresentationBuilder.Build(Evaluate(observation)));
        Assert.False(row.IsImprovement);
        Assert.Null(row.TestId);
        Assert.Equal("Informativo", row.StatusLabel);
        Assert.Contains(row.TechnicalDetails, detail => detail.Contains("HardwareInterface: False", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledPnpDeviceIsInformationalEvenWhenItsDriverServiceIsEmpty()
    {
        var observation = Observation("devices.identity", "disabled", "Controlador de áudio", "", new()
            { ["pnpProblemCode"] = "22", ["class"] = "MEDIA" });
        var report = Evaluate(observation);
        Assert.Equal(AuditClassification.AppliedUnproven, Assert.Single(report.Assessments).Classification);
        var row = Assert.Single(AuditPresentationBuilder.Build(report));
        Assert.Equal("Desabilitado no Windows", row.CurrentValue);
        Assert.Equal("Informativo", row.StatusLabel);
        Assert.Equal(AuditRecommendationKind.NoUniversalValue, row.RecommendationKind);
        Assert.False(row.IsImprovement);
        Assert.False(row.IsAdequate);
        Assert.Null(row.TestId);
        Assert.Contains(row.TechnicalDetails, detail => detail.Contains("pnpProblemCode: 22", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Adaptador virtual", "PCI")]
    [InlineData("Microsoft Kernel Debug Network Adapter", "ROOT")]
    [InlineData("Dispositivo de software", "SWD")]
    public void VirtualDeviceDoesNotBecomeAPerformancePriority(string name, string bus)
    {
        var identity = Observation("devices.identity", "virtual", name, "driver", new()
            { ["pnpProblemCode"] = "10", ["busEnumerator"] = bus });
        var invalidMsi = Observation("devices.interrupt.msi", "virtual", name, "9", new()
            { ["decimal"] = "9", ["invalid"] = "Valor fora do contrato" });
        var report = Evaluate(identity, invalidMsi);
        Assert.DoesNotContain(report.Assessments, assessment => assessment.Classification is
            AuditClassification.PossibleProblem or AuditClassification.ComparisonOpportunity);
        var row = Assert.Single(AuditPresentationBuilder.Build(report));
        Assert.False(row.IsImprovement);
        Assert.Equal("neutral", row.Tone);
        Assert.Null(row.TestId);
        Assert.Equal(2, row.Evidence.Count);
        Assert.Contains(row.TechnicalDetails, detail => detail.Contains("Valor fora do contrato", StringComparison.Ordinal));
    }

    [Fact]
    public void IgnoredMmcssValueHasNoInventedGpuPriorityOrSuggestedBenchmark()
    {
        var observation = Observation("windows.mmcss.gpu", "gpu", "GPU Priority", "0x00000008 (8 decimal)", new()
            { ["decimal"] = "8", ["invalid"] = "not relevant for ignored field" });
        var rule = Rule(observation.RuleId) with { Support = AuditSupport.Ignored };
        var row = Assert.Single(AuditPresentationBuilder.Build(PerformanceAuditEngine.Evaluate([rule], [observation], DateTimeOffset.UtcNow)));
        Assert.Equal("8", row.CurrentValue);
        Assert.Equal("Não alterar para desempenho", row.RecommendedValue);
        Assert.Equal("Sem efeito documentado", row.StatusLabel);
        Assert.False(row.IsImprovement);
        Assert.False(row.IsAdequate);
        Assert.Null(row.TestId);
    }

    [Theory]
    [InlineData("network.advanced", "Disabled")]
    [InlineData("nvidia.power", "Prefer maximum performance")]
    public void ObservedValuesDoNotBecomeUniversalRecommendations(string ruleId, string value)
    {
        var row = Assert.Single(AuditPresentationBuilder.Build(Evaluate(Observation(ruleId, "setting", "Configuração", value))));
        Assert.Equal(AuditRecommendationKind.NoUniversalValue, row.RecommendationKind);
        Assert.Equal("Sem recomendação fundamentada", row.RecommendedValue);
        Assert.False(row.IsImprovement);
        Assert.True(row.IsUnassessed);
        Assert.Null(row.TestId);
    }

    [Fact]
    public void AProviderComparisonFlagDoesNotCreateAnOpportunityWithoutASpecificPolicy()
    {
        var observation = Observation("network.advanced", "nic:moderation", "Interrupt Moderation", "Enabled", new()
            { ["comparison"] = "Comparar a variação de RTT sob a mesma carga." });
        var row = Assert.Single(AuditPresentationBuilder.Build(Evaluate(observation)));
        Assert.Equal("Ativado", row.CurrentValue);
        Assert.Equal(AuditRecommendationKind.NoUniversalValue, row.RecommendationKind);
        Assert.False(row.IsComparison);
        Assert.False(row.IsImprovement);
        Assert.False(row.IsAdequate);
        Assert.Null(row.TestId);
        Assert.Equal("Sem recomendação fundamentada", row.RecommendedValue);
    }

    [Theory]
    [InlineData(Availability.ReadError)]
    [InlineData(Availability.PermissionRequired)]
    [InlineData(Availability.Unsupported)]
    public void UnavailableReadingDoesNotExposeStaleValuesOrSuggestAChange(Availability state)
    {
        var observation = Observation("network.advanced", "nic:setting", "Interrupt Moderation", "Enabled") with
            { Status = state, Limitation = "<Objs><S>System.InvalidOperationException: secret</S></Objs>" };
        var row = Assert.Single(AuditPresentationBuilder.Build(Evaluate(observation)));
        Assert.Equal("Não identificado", row.CurrentValue);
        Assert.Equal(state == Availability.Unsupported ? "Não aplicável neste ambiente" : "Leitura necessária", row.RecommendedValue);
        Assert.Equal(AuditRecommendationKind.Unavailable, row.RecommendationKind);
        Assert.True(row.IsUnassessed);
        Assert.False(row.IsImprovement);
        Assert.Equal("Dados insuficientes", row.ConfidenceLabel);
        Assert.Null(row.TestId);
        Assert.DoesNotContain("<Objs>", row.Reason);
        Assert.Contains(row.TechnicalDetails, detail => detail.Contains("<Objs>", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingRegistryValueDoesNotTurnIntoARecommendedDefault()
    {
        var row = Assert.Single(AuditPresentationBuilder.Build(Evaluate(Observation("windows.mmcss.responsiveness", "absent",
            "SystemResponsiveness", null, new() { ["presence"] = "absent" }))));
        Assert.Equal("Sem valor explícito", row.CurrentValue);
        Assert.Equal("Leitura necessária", row.RecommendedValue);
        Assert.False(row.IsImprovement);
        Assert.True(row.IsUnassessed);
    }

    [Theory]
    [InlineData("381b4222-f694-41f0-9685-ff5bb260df2e")]
    [InlineData(@"C:\Users\Pessoa\secret.txt")]
    [InlineData(@"HKLM\SYSTEM\CurrentControlSet\Enum")]
    [InlineData(@"PCI\VEN_8086&DEV_15FA\1")]
    [InlineData("<Obj><MS><S N=\"Message\">PowerShell output</S></MS></Obj>")]
    [InlineData("#<CLIXML\r\n<Objs><S>error</S></Objs>")]
    [InlineData("System.InvalidOperationException: failure at Example.Read()")]
    public void TechnicalEvidenceIsPreservedButExcludedFromSimpleFields(string technical)
    {
        var observation = Observation("custom.setting", "technical", technical, technical) with { Scope = technical };
        var assessment = Assert.Single(Evaluate(observation).Assessments) with { Reason = technical, Risks = technical };
        var row = Assert.Single(AuditPresentationBuilder.Build(Report(assessment)));
        foreach (var field in new[] { row.Name, row.Description, row.CurrentValue, row.RecommendedValue, row.StatusLabel,
            row.Reason, row.Risks, row.Validation, row.PossibleImpact, row.Subcategory })
            Assert.DoesNotContain(technical, field);
        Assert.Same(observation, Assert.Single(row.Evidence).Observation);
        Assert.Contains(row.TechnicalDetails, detail => detail.Contains(technical, StringComparison.Ordinal));
    }

    [Fact]
    public void EquivalentRegistryViewsCollapseWithoutLosingSourcesAndDifferentValuesStaySeparate()
    {
        const string path = @"HKLM\SOFTWARE\Fixture :: Scheduling Category";
        var first = Observation("windows.mmcss.category", path + " [Registry32]", "Scheduling Category", "High", new()
            { ["registryView"] = "32-bit", ["evidence"] = path + " [Registry32]", ["registryType"] = "String" });
        var second = first with { InstanceId = path + " [Registry64]", Source = "64-bit source",
            Facts = new Dictionary<string, string> { ["registryView"] = "64-bit", ["evidence"] = path + " [Registry64]", ["registryType"] = "String" } };
        var row = Assert.Single(AuditPresentationBuilder.Build(Evaluate(first, second)));
        Assert.Equal(2, row.Evidence.Count);
        Assert.Equal(2, row.TechnicalDetails.Count(detail => detail.Contains("Origem:", StringComparison.Ordinal)));
        Assert.Contains(row.TechnicalDetails, detail => detail.Contains("[Registry32]", StringComparison.Ordinal));
        Assert.Contains(row.TechnicalDetails, detail => detail.Contains("[Registry64]", StringComparison.Ordinal));
        Assert.Equal(2, AuditPresentationBuilder.Build(Evaluate(first, second with { RawValue = "Medium" })).Count);
    }

    [Fact]
    public void EachProcessKeepsItsIdentityAndObservedPriorityWhileRealtimeRemainsActionable()
    {
        AuditObservation Process(string id, string name, string priority) => Observation("windows.process.scheduling", id, name, priority,
            new() { ["PriorityClass"] = priority, ["ExecutableName"] = name, ["PID"] = id[4..], ["AffinityMask"] = "0xF" });
        var rows = AuditPresentationBuilder.Build(Evaluate(Process("pid:1", "one.exe", "Normal"), Process("pid:2", "two.exe", "Normal"),
            Process("pid:3", "VALORANT-Win64-Shipping.exe", "Normal"), Process("pid:4", "audio.exe", "RealTime")));
        Assert.Equal(4, rows.Count);
        Assert.All(rows, row => Assert.Single(row.Evidence));
        Assert.Contains(rows, row => row.Name.Contains("one.exe", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Name.Contains("two.exe", StringComparison.Ordinal));
        Assert.All(rows, row => Assert.Contains("0xF", row.CurrentValue));
        Assert.Equal("games", Assert.Single(rows, row => row.Evidence.Any(e => e.Observation.InstanceId == "pid:3")).CategoryId);
        var realtime = Assert.Single(rows, row => row.IsImprovement);
        Assert.Contains("tempo real", realtime.CurrentValue);
        Assert.Equal("Rever o uso de prioridade Tempo real", realtime.RecommendedValue);
        Assert.Equal(realtime, rows[0]);
        Assert.Equal(4, rows.Sum(row => row.Evidence.Count));
    }

    [Fact]
    public void DeviceGroupingDecodesAffinityWithoutClaimingEffectiveMsiOrAGoodDistribution()
    {
        var observations = new[]
        {
            Observation("devices.identity", "device", "Controlador USB", "usbxhci", new() { ["pnpProblemCode"] = "0", ["class"] = "USB" }),
            Observation("devices.interrupt.msi", "device", "Controlador USB — MSISupported", "0x00000001", new() { ["decimal"] = "1" }),
            Observation("devices.interrupt.affinity", "device", "Controlador USB — AssignmentSetOverride", "0000000000000003", new()
                { ["logicalBits"] = "0, 1", ["groupContext"] = "0", ["maskPolicyApplies"] = "Aplicação efetiva não verificada" })
        };
        var row = Assert.Single(AuditPresentationBuilder.Build(Evaluate(observations)));
        Assert.Equal("devices", row.CategoryId);
        Assert.Equal("USB e periféricos", row.Subcategory);
        Assert.Contains("MSI permitido na configuração", row.CurrentValue);
        Assert.Contains("CPUs 0, 1", row.CurrentValue);
        Assert.DoesNotContain("0000000000000003", row.CurrentValue);
        Assert.False(row.IsAdequate);
        Assert.False(row.IsImprovement);
        Assert.Equal(3, row.Evidence.Count);
    }

    [Fact]
    public void MemorySpeedIsObservedDataAndDoesNotAssumeAdvertisedXmpOrPlatformFault()
    {
        var observation = Observation("memory.modules", "dimm:0", "Módulo RAM", "fixture", new()
            { ["Capacity"] = "8589934592", ["ConfiguredTransferRateMTs"] = "2933" });
        var row = Assert.Single(AuditPresentationBuilder.Build(Evaluate(observation)));
        Assert.Equal("8 GiB · 2933 MT/s configurados", row.CurrentValue);
        Assert.False(row.IsImprovement);
        Assert.Equal(AuditRecommendationKind.KeepCurrent, row.RecommendationKind);
        Assert.Equal("memory-access", row.TestId);
        Assert.Contains("CPU/placa", row.Validation);
        Assert.DoesNotContain("B460", row.Reason);
    }

    [Fact]
    public void IdenticalIfeoPropertyNamesRetainTheirApplicationContext()
    {
        var rows = AuditPresentationBuilder.Build(Evaluate(
            Observation("windows.ifeo.cpu", "ifeo:one", "CpuPriorityClass", "3", new() { ["executable"] = "one.exe" }),
            Observation("windows.ifeo.cpu", "ifeo:two", "CpuPriorityClass", "3", new() { ["executable"] = "two.exe" })));
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.Name == "CpuPriorityClass · one.exe");
        Assert.Contains(rows, row => row.Name == "CpuPriorityClass · two.exe");
        Assert.DoesNotContain(rows, row => row.IsImprovement);
    }

    [Fact]
    public void NvidiaInheritedValueAndPowerSchemeUseFriendlyNamesWithoutDiscardingOriginals()
    {
        var nvidia = Observation("nvidia.power", "global:power", "Power Management Mode", "Prefer maximum performance (0x00000001; 1)",
            new() { ["decimal"] = "1", ["origin"] = "Herdado do perfil global" }) with { Scope = "Perfil global" };
        var power = Observation("windows.power.policy", "power:active", "Plano de energia ativo", "381b4222-f694-41f0-9685-ff5bb260df2e",
            new() { ["ActiveScheme"] = "381b4222-f694-41f0-9685-ff5bb260df2e" });
        var rows = AuditPresentationBuilder.Build(Evaluate(nvidia, power));
        var profile = Assert.Single(rows, row => row.CategoryId == "nvidia");
        Assert.Equal("Prefer maximum performance", profile.CurrentValue);
        Assert.Contains("valor herdado", profile.Description);
        Assert.Contains(profile.TechnicalDetails, detail => detail.Contains("0x00000001", StringComparison.Ordinal));
        Assert.Equal("Equilibrado", Assert.Single(rows, row => row.CategoryId == "windows").CurrentValue);
        Assert.DoesNotContain(rows, row => row.IsImprovement);
    }

    [Fact]
    public void FilterFlagsSeparateAdequateComparisonConcernAndUnassessedWithoutClaimingGain()
    {
        var rows = AuditPresentationBuilder.Build(Evaluate(
            Observation("windows.process.scheduling", "normal", "Normal", "Normal", new() { ["PriorityClass"] = "Normal" }),
            Observation("windows.process.scheduling", "realtime", "Realtime", "RealTime", new() { ["PriorityClass"] = "RealTime" }),
            Observation("windows.bcd.clock", "bcd", "useplatformclock", "Yes"),
            Observation("windows.scheduler.separation", "separation", "Win32PrioritySeparation", "42"),
            Observation("custom.unknown", "unknown", "Propriedade desconhecida", "Enabled")));
        Assert.Single(rows, row => row.IsImprovement);
        Assert.DoesNotContain(rows, row => row.IsComparison);
        Assert.Equal(3, rows.Count(row => row.IsAdequate));
        Assert.Single(rows, row => row.IsUnassessed);
        Assert.All(rows, row => Assert.NotEqual(AuditRecommendationKind.SpecificValue, row.RecommendationKind));
        Assert.DoesNotContain(rows, row => row.IsAdequate && row.IsImprovement);
    }

    private static AuditObservation Observation(string rule, string instance, string name, string? value,
        Dictionary<string, string>? facts = null) => new(rule, instance, name, "Sistema", value,
            Availability.Available, "Fonte de teste", facts ?? []);
    private static AuditRule Rule(string id) => new(id, "Fixture", "Configuração", "Leitura", "Fonte disponível", "Fonte documentada",
        "Leitura observada", "Depende da carga", AuditSupport.Documented, "Sem medição causal", "Comparar sessões", ["https://example.test/reference"]);
    private static PerformanceAuditReport Evaluate(params AuditObservation[] observations) => PerformanceAuditEngine.Evaluate(
        observations.Select(observation => Rule(observation.RuleId)).ToArray(), observations, DateTimeOffset.UtcNow);
    private static PerformanceAuditReport Report(params AuditAssessment[] assessments) => new(DateTimeOffset.UtcNow, "Fixture", assessments, []);
}
