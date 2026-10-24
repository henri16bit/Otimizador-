using PCAnalyzer.Core;

namespace PCAnalyzer.Tests;

public sealed class RecommendationClassifierTests
{
    [Theory]
    [InlineData("unknown.setting")]
    [InlineData("windows.unknown")]
    [InlineData("network.advanced")]
    [InlineData("nvidia.drs.unknown")]
    public void PropertyPresenceAndProviderComparisonTextAreNotARecommendation(string rule)
    {
        var assessment = Assess(rule, "1", new()
            { ["comparison"] = "Comparar com outro valor", ["RegistryKeyword"] = "UnknownProperty", ["invalid"] = "true" });
        var policy = AuditRecommendationClassifier.Classify(assessment);
        Assert.Equal(AuditClassification.InsufficientData, assessment.Classification);
        Assert.Equal("Sem recomendação fundamentada", policy.RecommendedValue);
        Assert.False(policy.IsImprovement);
        Assert.Null(policy.RecommendationConfidence);
        Assert.Null(policy.BenefitConfidence);
        Assert.Null(policy.TestId);
    }

    [Fact]
    public void EthernetTypeIsDecodedAndRemainsInformationalDespiteGenericProviderFlags()
    {
        var assessment = Assess("network.advanced", "6", new()
        {
            ["RegistryKeyword"] = "*IfType", ["RegistryValue"] = "6", ["comparison"] = "Compare", ["invalid"] = "true"
        });
        var row = Present(assessment);
        Assert.Contains("6", row.CurrentValue);
        Assert.Contains("Ethernet", row.CurrentValue);
        Assert.Equal(AuditFunctionalKind.Informational, row.FunctionalKind);
        Assert.Equal("Não aplicável", row.RecommendedValue);
        Assert.False(row.IsImprovement);
        Assert.False(row.IsComparison);
        Assert.Null(row.TestId);
        Assert.Equal(AuditClassification.AppliedUnproven, assessment.Classification);
    }

    [Theory]
    [InlineData("0x0000002A (42 decimal)", "42", "0x2A (42)")]
    [InlineData("0x00000026 (38 decimal)", "38", "0x26 (38)")]
    public void SchedulerDisplaysTheObservedNumberAndJustifiesKeepingWithoutAUniversalReplacement(string raw, string number, string display)
    {
        var assessment = Assess("windows.scheduler.separation", raw, new() { ["decimal"] = number });
        var row = Present(assessment);
        Assert.Equal(display, row.CurrentValue);
        Assert.Equal(AuditRecommendationKind.KeepCurrent, row.RecommendationKind);
        Assert.Equal("Manter", row.RecommendedValue);
        Assert.True(row.IsAdequate);
        Assert.False(row.IsImprovement);
        Assert.Contains("não existe superioridade universal", row.Reason);
    }

    [Theory]
    [InlineData("Intel Core i7-10700K", "B460M", true)]
    [InlineData("Intel Core i7-10700K", "Z490", false)]
    [InlineData("Intel Core i5-10400", "B460M", false)]
    [InlineData("", "", false)]
    public void MemoryPlatformExplanationRequiresTheActuallyIdentifiedCpuAndBoard(string cpu, string board, bool platformKnown)
    {
        var policy = AuditRecommendationClassifier.Classify(Assess("memory.modules", "Memory module", new()
        {
            ["ConfiguredTransferRateMTs"] = "2933", ["CpuName"] = cpu, ["BaseboardProduct"] = board
        }));
        Assert.False(policy.IsImprovement);
        Assert.Equal(AuditRecommendationKind.KeepCurrent, policy.Kind);
        Assert.Equal(platformKnown, policy.ShortReason.Contains("i7-10700K e B460", StringComparison.Ordinal));
        Assert.DoesNotContain("3600", policy.RecommendedValue);
        Assert.Null(policy.BenefitConfidence);
    }

    [Fact]
    public void MemoryIdentityAloneDoesNotInventAConfiguredTransferRate()
    {
        var policy = AuditRecommendationClassifier.Classify(Assess("memory.modules", "Memory module", new()
            { ["Capacity"] = "8589934592", ["CpuName"] = "Intel Core i7-10700K", ["BaseboardProduct"] = "B460M" }));
        Assert.Equal(AuditFunctionalKind.Incomplete, policy.FunctionalKind);
        Assert.Equal("Sem recomendação fundamentada", policy.RecommendedValue);
        Assert.DoesNotContain("2933", policy.ShortReason);
        Assert.False(policy.IsImprovement);
    }

    [Theory]
    [InlineData("windows.mmcss.gpu")]
    [InlineData("windows.mmcss.sfio")]
    public void IgnoredMmcssFieldsNeverBecomeImprovementsEvenWithInvalidAndComparisonFlags(string rule)
    {
        var assessment = Assess(rule, "8", new() { ["invalid"] = "true", ["comparison"] = "Compare" });
        var policy = AuditRecommendationClassifier.Classify(assessment);
        Assert.Equal(AuditClassification.NoDocumentedEffect, assessment.Classification);
        Assert.Equal(AuditFunctionalKind.NoDocumentedEffect, policy.FunctionalKind);
        Assert.False(policy.IsImprovement);
        Assert.False(policy.IsAdequate);
        Assert.Null(policy.TestId);
        Assert.Null(policy.BenefitConfidence);
    }

    [Theory]
    [InlineData("9")]
    [InlineData("4294967295")]
    public void MmcssHighOverridesAnOutOfRangeDwordWithoutInventingEffectiveThreadPriority(string number)
    {
        var assessment = Assess("windows.mmcss.priority", number, new()
        {
            ["decimal"] = number, ["registryType"] = "DWord", ["SchedulingCategory"] = "High", ["invalid"] = "true"
        });
        var policy = AuditRecommendationClassifier.Classify(assessment);
        Assert.Equal(AuditClassification.AppliedUnproven, assessment.Classification);
        Assert.Equal(AuditRecommendationKind.KeepCurrent, policy.Kind);
        Assert.False(policy.IsImprovement);
        Assert.Contains("interpretada como 2", policy.ShortReason);
        Assert.Contains("não comprova o valor de prioridade atual de uma thread", assessment.Reason);
    }

    [Fact]
    public void MmcssHighDoesNotHideAnInvalidRegistryType()
    {
        var assessment = Assess("windows.mmcss.priority", "8", new()
        {
            ["registryType"] = "String", ["SchedulingCategory"] = "High", ["invalid"] = "true", ["invalidReason"] = "Requer REG_DWORD"
        });
        Assert.Equal(AuditClassification.PossibleProblem, assessment.Classification);
        var policy = AuditRecommendationClassifier.Classify(assessment);
        Assert.True(policy.IsImprovement);
        Assert.Contains("REG_DWORD", policy.Reason);
    }

    [Theory]
    [InlineData(Availability.ReadError)]
    [InlineData(Availability.PermissionRequired)]
    [InlineData(Availability.Unsupported)]
    [InlineData(Availability.ToolMissing)]
    public void FailedReadCannotPromoteAStaleValueOrItsComparisonHypothesis(Availability status)
    {
        var assessment = Assess("windows.process.scheduling", "RealTime", new()
            { ["PriorityClass"] = "RealTime", ["comparison"] = "Compare" }, status);
        var policy = AuditRecommendationClassifier.Classify(assessment);
        Assert.Equal(AuditRecommendationKind.Unavailable, policy.Kind);
        Assert.False(policy.IsImprovement);
        Assert.Null(policy.ReadingConfidence);
        Assert.Null(policy.RecommendationConfidence);
        Assert.Null(policy.BenefitConfidence);
        Assert.Null(policy.TestId);
        Assert.Equal("Não identificado", Present(assessment).CurrentValue);
    }

    [Theory]
    [InlineData("devices.identity", "22", "PCI")]
    [InlineData("devices.identity", "10", "SWD")]
    public void DisabledAndVirtualDevicesDoNotBecomeTopImprovements(string rule, string problem, string bus)
    {
        var assessment = Assess(rule, "driver", new() { ["pnpProblemCode"] = problem, ["busEnumerator"] = bus });
        Assert.False(AuditRecommendationClassifier.Classify(assessment).IsImprovement);
        Assert.Equal(AuditClassification.AppliedUnproven, assessment.Classification);
    }

    [Fact]
    public void ModerationNeedsDriverAlternativesAndStillDoesNotPromiseAnImprovement()
    {
        var facts = new Dictionary<string, string> { ["RegistryKeyword"] = "*InterruptModeration" };
        var incomplete = AuditRecommendationClassifier.Classify(Assess("network.advanced", "Enabled", facts));
        Assert.Equal("Sem recomendação fundamentada", incomplete.RecommendedValue);
        facts["AdvertisedValues"] = "0 | 1";
        var known = AuditRecommendationClassifier.Classify(Assess("network.advanced", "Enabled", facts));
        Assert.Equal(AuditFunctionalKind.TestDependent, known.FunctionalKind);
        Assert.Equal(AuditRecommendationKind.KeepCurrent, known.Kind);
        Assert.False(known.IsImprovement);
        Assert.Contains("interrupções", known.ShortReason);
        Assert.Equal("ping", known.TestId);
        Assert.True(TestCatalog.Get(known.TestId!).Implemented);
    }

    [Fact]
    public void NvidiaProfileDifferenceAndInheritanceDoNotProveMaximumPowerIsBetter()
    {
        var assessment = Assess("nvidia.drs.power", "Prefer maximum performance", new()
        {
            ["profile"] = "VALORANT", ["application"] = "VALORANT-Win64-Shipping.exe", ["origin"] = "Herdado do perfil global",
            ["differsFromGlobal"] = "true", ["comparison"] = "Comparar perfil global e aplicação"
        });
        var policy = AuditRecommendationClassifier.Classify(assessment);
        Assert.Equal(AuditClassification.Adequate, assessment.Classification);
        Assert.Equal("Manter", policy.RecommendedValue);
        Assert.Contains("não é superior para todos", policy.Reason);
        Assert.Contains("herança", policy.Reason);
        Assert.Contains("temperatura", policy.Validation);
        Assert.False(policy.IsImprovement);
        Assert.Null(policy.BenefitConfidence);
        Assert.Equal("VALORANT-Win64-Shipping.exe", assessment.Observation.Facts["application"]);
    }

    [Theory]
    [InlineData("nvidia.drs.opengl-threads", "Direct3D 12")]
    [InlineData("nvidia.drs.opengl-triple-buffer", "Vulkan")]
    [InlineData("nvidia.drs.prerender", "DX12")]
    public void GraphicsOptionsCannotBeRecommendedForAnInapplicableApi(string rule, string api)
    {
        var policy = AuditRecommendationClassifier.Classify(Assess(rule, "1", new() { ["GraphicsApi"] = api }));
        Assert.Equal(AuditFunctionalKind.Informational, policy.FunctionalKind);
        Assert.Equal("Não aplicável", policy.RecommendedValue);
        Assert.False(policy.IsImprovement);
        Assert.Null(policy.TestId);
    }

    [Fact]
    public void SpecificReasonsAndConfidenceDescribeTheRecommendationInsteadOfInventingMeasuredBenefit()
    {
        var policies = new[]
        {
            AuditRecommendationClassifier.Classify(Assess("windows.scheduler.separation", "42")),
            AuditRecommendationClassifier.Classify(Assess("windows.input.queue", "100")),
            AuditRecommendationClassifier.Classify(Assess("windows.mouse.effective", "10")),
            AuditRecommendationClassifier.Classify(Assess("nvidia.drs.shader-cache", "Enabled"))
        };
        Assert.Equal(policies.Length, policies.Select(policy => policy.ShortReason).Distinct().Count());
        Assert.All(policies, policy =>
        {
            Assert.Equal(Confidence.High, policy.ReadingConfidence);
            Assert.Equal(Confidence.Medium, policy.RecommendationConfidence);
            Assert.Null(policy.BenefitConfidence);
            Assert.Equal("Manter", policy.RecommendedValue);
        });
    }

    [Fact]
    public void ClassifyingAnObservationPreservesTheOriginalEvidenceAndDoesNotMutateFacts()
    {
        var facts = new Dictionary<string, string> { ["PriorityClass"] = "Normal", ["ExecutableName"] = "real-game.exe", ["PID"] = "150" };
        var before = facts.ToArray();
        var assessment = Assess("windows.process.scheduling", "Normal", facts);
        var policy = AuditRecommendationClassifier.Classify(assessment);
        Assert.Same(facts, assessment.Observation.Facts);
        Assert.Equal(before, facts.ToArray());
        Assert.Equal("Normal", assessment.Observation.RawValue);
        Assert.False(policy.IsImprovement);
        Assert.Contains("executável e PID", policy.Reason);
    }

    private static AuditAssessment Assess(string ruleId, string? value, Dictionary<string, string>? facts = null,
        Availability availability = Availability.Available)
    {
        var observation = new AuditObservation(ruleId, ruleId + ":fixture", ruleId, "Sistema", value,
            availability, "Fonte da leitura", facts ?? []);
        var rule = new AuditRule(ruleId, "Fixture", ruleId, "Consulta", "Leitura disponível", "Contrato aplicável",
            "Valor observado", "Não medido", AuditSupport.Documented, "Sem teste causal", "Verificar contexto", []);
        return Assert.Single(PerformanceAuditEngine.Evaluate([rule], [observation], DateTimeOffset.UtcNow).Assessments);
    }

    private static AuditSettingPresentation Present(AuditAssessment assessment) =>
        Assert.Single(AuditPresentationBuilder.Build(new(DateTimeOffset.UtcNow, "Fixture", [assessment], [])));
}
