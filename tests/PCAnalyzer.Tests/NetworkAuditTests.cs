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
