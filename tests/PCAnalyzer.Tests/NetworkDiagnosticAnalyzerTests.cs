using PCAnalyzer.Core;
using PCAnalyzer.Network;

namespace PCAnalyzer.Tests;

public sealed class NetworkDiagnosticAnalyzerTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid FirstId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondId = new("00000000-0000-0000-0000-000000000002");

    [Fact]
    public void ActiveSelectionUsesStateGatewayAndTypeWithoutModelAssumptions()
    {
        var disconnectedIntel = Adapter() with { Description = "Intel Ethernet Connection I219-V", Status = "Down" };
        var ethernet = Adapter(SecondId) with { Description = "Another vendor", Gateways = [] };
        var wifi = Adapter(Guid.NewGuid()) with { Type = "Wireless80211" };
        var loopback = Adapter(Guid.NewGuid()) with { Type = "Loopback" };
        var snapshot = Snapshot(disconnectedIntel, ethernet, wifi, loopback);
        Assert.Equal(wifi.InterfaceGuid, NetworkDiagnosticAnalyzer.SelectActive(snapshot)?.InterfaceGuid);
        Assert.Equal(ethernet.InterfaceGuid, NetworkDiagnosticAnalyzer.SelectActive(snapshot, ethernet.InterfaceGuid)?.InterfaceGuid);
        Assert.Null(NetworkDiagnosticAnalyzer.SelectActive(snapshot, disconnectedIntel.InterfaceGuid));
        Assert.Null(NetworkDiagnosticAnalyzer.SelectActive(snapshot, Guid.NewGuid()));
        Assert.Null(NetworkDiagnosticAnalyzer.SelectActive(snapshot with { Status = Availability.ReadError }));
        Assert.Equal(ethernet.InterfaceGuid, NetworkDiagnosticAnalyzer.SelectActive(Snapshot(wifi, ethernet with { Gateways = ["192.0.2.1"] }))?.InterfaceGuid);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("::")]
    [InlineData("127.0.0.1")]
    [InlineData("::1")]
    [InlineData("not-an-address")]
    public void UnusableGatewayDoesNotWinAutomaticSelection(string unusableGateway)
    {
        var noGateway = Adapter() with { Gateways = [unusableGateway] };
        var usable = Adapter(SecondId) with { Type = "Wireless80211", Gateways = ["2001:db8::1"] };
        Assert.Equal(usable.InterfaceGuid, NetworkDiagnosticAnalyzer.SelectActive(Snapshot(noGateway, usable))?.InterfaceGuid);
        var result = NetworkDiagnosticAnalyzer.Analyze(Snapshot(noGateway), null, []);
        Assert.Contains(result, f => f.Id.EndsWith(".gateway", StringComparison.Ordinal) && f.Category == FindingCategory.InsufficientData);
        Assert.DoesNotContain(result, IsProblem);
    }

    [Fact]
    public void MultipleActiveAdaptersAreVisibleAndChosenInterfaceDoesNotChangeMidWindow()
    {
        var first = Adapter(); var second = Adapter(SecondId);
        var before = Snapshot(first, second);
        var finding = Assert.Single(NetworkDiagnosticAnalyzer.Analyze(before, null, []),
            f => f.Id.EndsWith(".connection", StringComparison.Ordinal));
        Assert.Equal(2, finding.Evidence.Count);
        Assert.Contains("2 conexões ativas", finding.Explanation);
        Assert.Contains("rota", finding.Explanation);

        var after = Snapshot(first with { Status = "Down" }, second) with { CapturedAt = Start.AddSeconds(30) };
        var result = NetworkDiagnosticAnalyzer.Analyze(before, after, []);
        Assert.Contains(result, f => f.Id == "network.connection" && f.Category == FindingCategory.InsufficientData);
        Assert.DoesNotContain(result, f => f.Id.StartsWith($"network.{SecondId:N}.", StringComparison.Ordinal));
        Assert.DoesNotContain(result, IsProblem);
    }

    [Fact]
    public void AutomaticSelectionIsDeterministicAndStaysOnTheInitialAdapterWhenPriorityChanges()
    {
        var first = Adapter() with { Gateways = [] };
        var second = Adapter(SecondId) with { Gateways = [] };
        var before = Snapshot(second, first);
        Assert.Equal(FirstId, NetworkDiagnosticAnalyzer.SelectActive(before)?.InterfaceGuid);
        Assert.Equal(FirstId, NetworkDiagnosticAnalyzer.SelectActive(Snapshot(first, second))?.InterfaceGuid);

        var after = Snapshot(second with { Gateways = ["192.0.2.1"], ReceivedErrors = 100 },
            first with { ReceivedErrors = 2 }) with { CapturedAt = Start.AddSeconds(30) };
        Assert.Equal(SecondId, NetworkDiagnosticAnalyzer.SelectActive(after)?.InterfaceGuid);
        var result = NetworkDiagnosticAnalyzer.Analyze(before, after, []);
        var errors = Assert.Single(result, IsProblem);
        Assert.Equal($"network.{FirstId:N}.errors", errors.Id);
        Assert.Contains("recepção: +2", errors.Explanation);
        Assert.DoesNotContain(result, f => f.Id.StartsWith($"network.{SecondId:N}.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AFailedPollNeverUsesItsAdapterValuesAsSuccessfulEvidence(bool initialPollFailed)
    {
        var before = Snapshot(Adapter()) with
        {
            Status = initialPollFailed ? Availability.ReadError : Availability.Available,
            Limitation = initialPollFailed ? "Initial polling failed" : null
        };
        var after = Snapshot(Adapter() with { ReceivedErrors = 10 }) with
        {
            CapturedAt = Start.AddSeconds(30),
            Status = initialPollFailed ? Availability.Available : Availability.ReadError,
            Limitation = initialPollFailed ? null : "Final polling failed"
        };
        var result = NetworkDiagnosticAnalyzer.Analyze(before, after, []);
        Assert.Contains(result, f => f.Id == "network.coverage" && f.Category == FindingCategory.InsufficientData);
        Assert.DoesNotContain(result, IsProblem);
        Assert.DoesNotContain(result, f => f.Id.EndsWith(".errors", StringComparison.Ordinal)
            && f.Category == FindingCategory.NoAnomaly);
        if (!initialPollFailed)
            Assert.DoesNotContain(result, f => f.Category == FindingCategory.NoAnomaly);
    }

    [Fact]
    public void HighHistoricalErrorTotalsDoNotBecomeCurrentProblems()
    {
        var adapter = Adapter() with { ReceivedErrors = 90_000, SentErrors = 120_000 };
        var before = Snapshot(adapter);
        var after = before with { CapturedAt = Start.AddSeconds(30) };
        var result = NetworkDiagnosticAnalyzer.Analyze(before, after, []);
        var errors = Assert.Single(result, f => f.Id.EndsWith(".errors", StringComparison.Ordinal));
        Assert.Equal(FindingCategory.NoAnomaly, errors.Category);
        Assert.Contains("30 s", errors.Explanation);
        Assert.Contains("totais anteriores", errors.Explanation);
        Assert.DoesNotContain(result, IsProblem);
        var singleRead = NetworkDiagnosticAnalyzer.Analyze(before, null, []);
        Assert.Contains(singleRead, f => f.Id.EndsWith(".errors", StringComparison.Ordinal) && f.Category == FindingCategory.InsufficientData);
        Assert.DoesNotContain(singleRead, IsProblem);
    }

    [Fact]
    public void NewErrorsAreAttributedToTheObservedWindowWithOriginalEvidence()
    {
        var adapter = Adapter() with { ReceivedErrors = 90_000, SentErrors = 120_000 };
        var before = Snapshot(adapter);
        var after = Snapshot(adapter with { ReceivedErrors = 90_003, SentErrors = 120_001, ReceivedBytes = 2000 })
            with { CapturedAt = Start.AddSeconds(30) };
        var finding = Assert.Single(NetworkDiagnosticAnalyzer.Analyze(before, after, []), IsProblem);
        Assert.Equal(FindingCategory.ObservedProblem, finding.Category);
        Assert.Contains("recepção: +3", finding.Explanation);
        Assert.Contains("envio: +1", finding.Explanation);
        Assert.Contains("30 s", finding.Explanation);
        Assert.Contains("não identifica a causa", finding.Explanation);
        Assert.Contains(finding.Evidence, e => e.Value == 90_000 && e.Timestamp == Start);
        Assert.Contains(finding.Evidence, e => e.Value == 90_003 && e.Timestamp == after.CapturedAt);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CounterResetsInvalidateTheWholeWindow(bool resetErrorCounter)
    {
        var adapter = Adapter() with { ReceivedErrors = 20, SentErrors = 30 };
        var changed = adapter with { SentErrors = 31 };
        changed = resetErrorCounter ? changed with { ReceivedErrors = 0 } : changed with { ReceivedBytes = 0 };
        var result = NetworkDiagnosticAnalyzer.Analyze(Snapshot(adapter),
            Snapshot(changed) with { CapturedAt = Start.AddSeconds(30) }, []);
        var errors = Assert.Single(result, f => f.Id.EndsWith(".errors", StringComparison.Ordinal));
        Assert.Equal(FindingCategory.InsufficientData, errors.Category);
        Assert.Contains("contador diminuiu", errors.Explanation);
        Assert.DoesNotContain(result, IsProblem);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1L)]
    public void MissingOrInvalidErrorCounterNeverBecomesZero(long? unavailable)
    {
        var adapter = Adapter() with { ReceivedErrors = unavailable };
        var before = Snapshot(adapter);
        var result = NetworkDiagnosticAnalyzer.Analyze(before, before with { CapturedAt = Start.AddSeconds(30) }, []);
        Assert.DoesNotContain(result, f => f.Id.EndsWith(".errors", StringComparison.Ordinal) && f.Category == FindingCategory.NoAnomaly);
        Assert.Contains(result, f => f.Id.EndsWith(".errors.coverage", StringComparison.Ordinal) && f.Category == FindingCategory.InsufficientData);
        Assert.DoesNotContain(result, IsProblem);
    }

    [Fact]
    public void OneUnavailableCounterDoesNotHideAnObservedIncreaseInTheOther()
    {
        var adapter = Adapter() with { ReceivedErrors = null, SentErrors = 5 };
        var result = NetworkDiagnosticAnalyzer.Analyze(Snapshot(adapter),
            Snapshot(adapter with { SentErrors = 7 }) with { CapturedAt = Start.AddSeconds(30) }, []);
        Assert.Contains(result, f => f.Category == FindingCategory.ObservedProblem && f.Explanation.Contains("envio: +2", StringComparison.Ordinal));
        Assert.Contains(result, f => f.Id.EndsWith(".errors.coverage", StringComparison.Ordinal) && f.Category == FindingCategory.InsufficientData);
    }

    [Fact]
    public void InvalidTimeOrChangedIdentityCannotProduceAnErrorAttribution()
    {
        var before = Snapshot(Adapter());
        var increase = Adapter() with { ReceivedErrors = 2 };
        NetworkSnapshot[] invalidEnds = [
            Snapshot(increase),
            Snapshot(increase) with { CapturedAt = Start.AddSeconds(-1) },
            Snapshot(increase with { Description = "Different device" }) with { CapturedAt = Start.AddSeconds(30) },
            Snapshot(increase with { Source = "Different provider" }) with { CapturedAt = Start.AddSeconds(30) },
            Snapshot(increase) with { CapturedAt = Start.AddSeconds(30), Status = Availability.ReadError }
        ];
        foreach (var after in invalidEnds)
        {
            var result = NetworkDiagnosticAnalyzer.Analyze(before, after, []);
            Assert.DoesNotContain(result, IsProblem);
            Assert.Contains(result, f => f.Category == FindingCategory.InsufficientData);
        }
    }

    [Fact]
    public void AdapterDetailsExplainSupportedPropertiesWithoutAutomaticTweaks()
    {
        var adapter = Adapter();
        var detail = Detail(adapter) with
        {
            AdvancedProperties = [Property("*RSS", "Enabled"), Property("*EEE", "Enabled"),
                Property("*InterruptModeration", "Disabled"), Property("*ReceiveBuffers", "512"),
                Property("*TransmitBuffers", "512"), Property("*LsoV2IPv4", "Enabled"), Property("*FlowControl", "Disabled")],
            Rss = [new("NumberOfReceiveQueues", "2", "Get-NetAdapterRss")],
            PowerManagement = [new("AllowComputerToTurnOffDevice", "Enabled", "Get-NetAdapterPowerManagement")]
        };
        var result = NetworkDiagnosticAnalyzer.Analyze(Snapshot(adapter), null, [detail]);
        var properties = Assert.Single(result, f => f.Id.EndsWith(".properties", StringComparison.Ordinal));
        Assert.Equal(FindingCategory.NoAnomaly, properties.Category);
        foreach (var name in new[] { "RSS", "EEE", "Moderação", "Buffers de recepção", "Buffers de envio", "Offloads", "Controle de fluxo", "Gerenciamento de energia" })
            Assert.Contains(properties.Evidence, e => e.Description.Contains(name, StringComparison.Ordinal));
        Assert.Contains("Preserve os valores atuais", properties.Recommendation);
        Assert.DoesNotContain(result, IsProblem);
        Assert.DoesNotContain(result, f => f.Id.EndsWith(".properties.coverage", StringComparison.Ordinal));
        Assert.Contains(result, f => f.Id.EndsWith(".driver", StringComparison.Ordinal) && f.Explanation.Contains("isoladamente", StringComparison.Ordinal));
    }

    [Fact]
    public void MissingPropertyValuesAndDeniedDetailsRemainExplicitlyUnavailable()
    {
        var adapter = Adapter();
        var detail = Detail(adapter) with
        {
            AdvancedProperties = [Property("*RSS", "") with { RegistryValues = [] }],
            Rss = [new("Enabled", null, "Get-NetAdapterRss", Availability.PermissionRequired, "Access denied")]
        };
        var result = NetworkDiagnosticAnalyzer.Analyze(Snapshot(adapter), null, [detail]);
        Assert.DoesNotContain(result, f => f.Id.EndsWith(".properties", StringComparison.Ordinal));
        Assert.Contains(result, f => f.Id.EndsWith(".properties.coverage", StringComparison.Ordinal) && f.Explanation.Contains("RSS", StringComparison.Ordinal));
        var denied = NetworkDiagnosticAnalyzer.Analyze(Snapshot(adapter), null,
            [detail with { Status = Availability.PermissionRequired, Limitations = ["Access denied"] }]);
        Assert.Contains(denied, f => f.Id.EndsWith(".details", StringComparison.Ordinal) && f.Title.Contains("permissão", StringComparison.Ordinal));
        Assert.DoesNotContain(denied, f => f.Id.EndsWith(".driver", StringComparison.Ordinal));
        var anotherAdapter = NetworkDiagnosticAnalyzer.Analyze(Snapshot(adapter), null, [detail with { InterfaceGuid = SecondId }]);
        Assert.Contains(anotherAdapter, f => f.Id.EndsWith(".details", StringComparison.Ordinal) && f.Category == FindingCategory.InsufficientData);
    }

    private static bool IsProblem(Finding finding) => finding.Category is FindingCategory.ObservedProblem or FindingCategory.PossibleProblem;
    private static NetworkAdapter Adapter(Guid? id = null) => new(id ?? FirstId, "Ethernet", "Fixture adapter", "Ethernet", "Up",
        1_000_000_000, ["192.0.2.10"], ["192.0.2.1"], 1000, 1000, 0, 0);
    private static NetworkSnapshot Snapshot(params NetworkAdapter[] adapters) => new(Start, adapters, Availability.Available);
    private static AdapterDetail Detail(NetworkAdapter adapter) => new(adapter.InterfaceGuid, adapter.Name, adapter.Description,
        "1.2.3", "Fixture driver", "2025-01-01", "1 Gbps", [], [], [], Availability.Available, []);
    private static NetworkProperty Property(string keyword, string displayValue) => new(keyword, keyword, [displayValue], displayValue,
        [], [], "Driver property", "Not evaluated", "Preserve", "Not quantified");
}
