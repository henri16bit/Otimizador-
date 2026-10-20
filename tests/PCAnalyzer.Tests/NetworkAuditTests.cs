using System.Text.Json;
using PCAnalyzer.Core;
using PCAnalyzer.Network;

namespace PCAnalyzer.Tests;

public sealed class NetworkAuditTests
{
    private static readonly Guid AdapterId = Guid.Parse("d9016897-a8ca-4275-abaa-5c2a1e439366");

    [Fact]
    public void MismatchedIdentityIsRejectedBeforeDisplayingProperties()
    {
        using var json = JsonDocument.Parse("""{"Identity":{"InterfaceGuid":"7102bd1b-9888-4f80-a590-291695c39253"},"Properties":[{"RegistryKeyword":"*RSS","RegistryValue":["1"]}]}""");
        Assert.Throws<InvalidOperationException>(() => NetworkInspector.ParseAdapter(AdapterId, json.RootElement));
    }

    [Fact]
    public void AvailableIdentitySurvivesPermissionDeniedAndUnsupportedSubqueries()
    {
        using var json = JsonDocument.Parse($$"""
            {"Identity":{"InterfaceGuid":"{{AdapterId:B}}","InterfaceIndex":7,"PnPDeviceID":"PCI\\VEN_8086"},
             "Name":"Ethernet","Description":"Adapter supplied by driver","DriverVersion":"1.2.3",
             "Properties":[],"Rss":null,"Power":null,"Statistics":{"ReceivedBytes":0,"ReceivedPacketErrors":null},
             "Statuses":{"Properties":"PermissionRequired","Rss":"Unsupported","Power":"ReadError"},
             "Limitations":["Properties: leitura não disponível; o motivo técnico está no log de rede."]}
            """);
        var detail = NetworkInspector.ParseAdapter(AdapterId, json.RootElement);
        Assert.Equal(Availability.Available, detail.Status);
        Assert.Equal("1.2.3", detail.DriverVersion);
        Assert.Equal(Availability.PermissionRequired, detail.PropertiesStatus);
        Assert.Equal(Availability.Unsupported, Assert.Single(detail.Rss).Status);
        Assert.Equal(Availability.ReadError, Assert.Single(detail.PowerManagement!).Status);
        Assert.Equal(Availability.Available, detail.Statistics.Single(f => f.Name == "ReceivedBytes").Status);
        Assert.Equal("0", detail.Statistics.Single(f => f.Name == "ReceivedBytes").Value);
        Assert.Equal(Availability.Unsupported, detail.Statistics.Single(f => f.Name == "ReceivedPacketErrors").Status);
        var observations = NetworkConfigurationAuditProvider.FromAdapter(Adapter(), detail);
        Assert.Equal(Availability.PermissionRequired, observations.Single(o => o.RuleId == "network.advanced").Status);
        Assert.DoesNotContain(observations, o => o.RuleId == "network.advanced" && o.RawValue == "0");
    }

    [Fact]
    public void DriverEnumerationsArePreservedWithoutInventingUnsupportedOptions()
    {
        using var json = JsonDocument.Parse($$$"""
            {"Identity":{"InterfaceGuid":"{{{AdapterId:D}}}"},"Properties":[
              {"RegistryKeyword":"*InterruptModeration","DisplayName":" ","RegistryValue":["1"],
               "DisplayValue":"Enabled","ValidRegistryValues":["0","1"],"ValidDisplayValues":["Disabled","Enabled"]},
              {"RegistryKeyword":"VendorSpecific","DisplayName":"Vendor option","RegistryValue":["42"],"ValidRegistryValues":[],"ValidDisplayValues":[]}],
             "Rss":{"Enabled":false,"NumberOfReceiveQueues":null}}
            """);
        var detail = NetworkInspector.ParseAdapter(AdapterId, json.RootElement);
        Assert.Equal(2, detail.AdvancedProperties.Count);
        var moderation = detail.AdvancedProperties[0];
        Assert.Equal("*InterruptModeration", moderation.DisplayName);
        Assert.Equal(["0", "1"], moderation.ValidRegistryValues);
        Assert.DoesNotContain(detail.AdvancedProperties, p => p.RegistryKeyword == "*EEE");
        Assert.Contains("documentação", detail.AdvancedProperties[1].Function);
        var values = NetworkConfigurationAuditProvider.FromAdapter(Adapter(), detail);
        var value = values.Single(o => o.InstanceId.EndsWith(":*InterruptModeration", StringComparison.Ordinal));
        Assert.Equal("1", value.RawValue);
        Assert.Contains("Não há ganho demonstrado", value.Facts["comparison"]);
        Assert.Contains(values, o => o.Name == "Enabled" && o.Status == Availability.Available);
        Assert.Contains(values, o => o.Name == "NumberOfReceiveQueues" && o.Status == Availability.Unsupported);
    }

    [Fact]
    public void UnsupportedPowerCapabilityIsNotPresentedAsAnAvailableSetting()
    {
        using var json = JsonDocument.Parse($$$"""
            {"Identity":{"InterfaceGuid":"{{{AdapterId:D}}}"},"Power":{
              "SelectiveSuspend":"Unsupported","WakeOnMagicPacket":"Enabled","ArpOffload":""}}
            """);
        var detail = NetworkInspector.ParseAdapter(AdapterId, json.RootElement);
        var unsupported = detail.PowerManagement!.Single(f => f.Name == "SelectiveSuspend");
        Assert.Equal(Availability.Unsupported, unsupported.Status);
        Assert.Equal("Unsupported", unsupported.Value);
        Assert.Contains("não é suportada", unsupported.Limitation);
        Assert.Equal(Availability.Available, detail.PowerManagement!.Single(f => f.Name == "WakeOnMagicPacket").Status);
        Assert.Equal(Availability.Unsupported, detail.PowerManagement!.Single(f => f.Name == "ArpOffload").Status);
        Assert.Equal(Availability.Unsupported, NetworkConfigurationAuditProvider.FromAdapter(Adapter(), detail)
            .Single(o => o.RuleId == "network.power" && o.Name == "SelectiveSuspend").Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryAdapterRowCarriesVerifiedPhysicalConnectionContext(bool hardwareInterface)
    {
        using var json = JsonDocument.Parse($$$"""
            {"Identity":{"InterfaceGuid":"{{{AdapterId:D}}}","HardwareInterface":{{{hardwareInterface.ToString().ToLowerInvariant()}}}},
             "Properties":[{"RegistryKeyword":"*RSS","RegistryValue":["1"]}],
             "Rss":{"Enabled":true},"Power":{"WakeOnMagicPacket":"Unsupported"},"Statistics":{"ReceivedBytes":0}}
            """);
        var detail = NetworkInspector.ParseAdapter(AdapterId, json.RootElement);
        var observations = NetworkConfigurationAuditProvider.FromAdapter(Adapter() with { Gateways = ["192.0.2.1"] }, detail);
        Assert.Equal(5, observations.Count);
        Assert.All(observations, observation =>
        {
            Assert.Equal(AdapterId.ToString("D"), observation.Facts["InterfaceGuid"]);
            Assert.Equal(hardwareInterface.ToString(), observation.Facts["HardwareInterface"]);
            Assert.Equal("Up", observation.Facts["AdapterStatus"]);
            Assert.Equal("Ethernet", observation.Facts["AdapterType"]);
            Assert.Equal("true", observation.Facts["HasGateway"]);
        });
    }

    [Fact]
    public void FailedAdapterReadKeepsDiscoveredContextWithoutGuessingPhysicalIdentity()
    {
        var detail = new AdapterDetail(AdapterId, "", "", null, null, null, null, [], [], [], Availability.ToolMissing, ["Missing cmdlet"]);
        var observations = NetworkConfigurationAuditProvider.FromAdapter(Adapter() with { Status = "Down", Gateways = ["0.0.0.0", "::"] }, detail);
        Assert.Equal(5, observations.Count);
        Assert.All(observations, observation =>
        {
            Assert.Equal(Availability.ToolMissing, observation.Status);
            Assert.Equal("não informado", observation.Facts["HardwareInterface"]);
            Assert.Equal("Down", observation.Facts["AdapterStatus"]);
            Assert.Equal("false", observation.Facts["HasGateway"]);
        });
    }

    [Theory]
    [InlineData("{\"Error\":{\"Status\":\"ToolMissing\"}}", Availability.ToolMissing)]
    [InlineData("{\"Error\":{\"Status\":\"Unsupported\"}}", Availability.Unsupported)]
    [InlineData("{\"Error\":{\"Status\":\"PermissionRequired\"}}", Availability.PermissionRequired)]
    [InlineData("{\"Error\":{\"Status\":\"Available\"}}", Availability.ReadError)]
    [InlineData("{\"Error\":{\"Status\":\"NotAState\"}}", Availability.ReadError)]
    [InlineData("[]", Availability.ReadError)]
    [InlineData("invalid json", Availability.ReadError)]
    public void StructuredQueryFailuresKeepTheirStatusWhileUnknownFailuresRemainErrors(string output, Availability expected) =>
        Assert.Equal(expected, NetworkInspector.ReadFailureStatus(output));

    [Theory]
    [InlineData("{\"Tcp\":[]}")]
    [InlineData("{\"Tcp\":[{\"SettingName\":\"Internet\"}]}")]
    public void EmptyTcpProfilesReportMissingEvidence(string output)
    {
        using var json = JsonDocument.Parse(output);
        Assert.Equal(Availability.InsufficientData, Assert.Single(NetworkInspector.ParseTcp(json.RootElement)).Status);
    }

    [Fact]
    public void TcpBlankFieldsAreUnavailableButZeroValuesRemainAvailable()
    {
        using var json = JsonDocument.Parse("""{"Tcp":[{"SettingName":"Internet","MinRtoMs":0,"CongestionProvider":" ","EcnCapability":null}]}""");
        var facts = NetworkInspector.ParseTcp(json.RootElement);
        Assert.Equal(Availability.Available, facts.Single(f => f.Name.EndsWith("MinRtoMs", StringComparison.Ordinal)).Status);
        Assert.Equal("0", facts.Single(f => f.Name.EndsWith("MinRtoMs", StringComparison.Ordinal)).Value);
        Assert.Equal(2, facts.Count(f => f.Status == Availability.Unsupported && f.Value is null));
    }

    [Fact]
    public void ClixmlErrorsAreDecodedWithoutProgressOrXmlInTheMessage()
    {
        const string clixml = """
            #< CLIXML
            <Objs xmlns="http://schemas.microsoft.com/powershell/2004/04"><Obj S="progress"><S>Preparing modules</S></Obj><S S="Error">Access denied_x000D__x000A_Driver query failed</S></Objs>
            """;
        var error = PowerShellErrorText.Read(clixml);
        Assert.Equal("Access denied Driver query failed", error);
        Assert.DoesNotContain("<", error);
        Assert.DoesNotContain("Preparing", error);
    }

    [Theory]
    [InlineData("#< CLIXML\n<Objs><broken>")]
    [InlineData("#< CLIXML\n<!DOCTYPE Objs [<!ENTITY injected SYSTEM 'file:///etc/passwd'>]><Objs>&injected;</Objs>")]
    [InlineData("<Objs><Obj S='progress'><S>Preparing modules</S></Obj></Objs>")]
    public void MalformedOrProgressOnlyClixmlProducesAnActionablePlainMessage(string raw)
    {
        var error = PowerShellErrorText.Read(raw);
        Assert.Contains("log técnico de rede", error);
        Assert.DoesNotContain("<", error);
        Assert.DoesNotContain("root:", error);
    }

    [Fact]
    public void ErrorDisplayIsBoundedAndDoesNotDoubleDecodeLiteralEscapeSequences()
    {
        Assert.Equal("_x0041_", PowerShellErrorText.Read("_x005F_x0041_"));
        Assert.InRange(PowerShellErrorText.Read(new string('x', 5000)).Length, 600, 650);
    }

    private static NetworkAdapter Adapter() => new(AdapterId, "Ethernet", "Adapter supplied by driver", "Ethernet", "Up",
        1_000_000_000, [], [], 0, 0, 0, 0);
}
