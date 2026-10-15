using PCAnalyzer.Core;

namespace PCAnalyzer.Network;

public sealed record NetworkAdapter(Guid InterfaceGuid, string Name, string Description, string Type,
    string Status, long? SpeedBitsPerSecond, IReadOnlyList<string> Addresses, IReadOnlyList<string> Gateways,
    long? ReceivedBytes, long? SentBytes, long? ReceivedErrors, long? SentErrors,
    string Source = "System.Net.NetworkInformation");
public sealed record NetworkSnapshot(DateTimeOffset CapturedAt, IReadOnlyList<NetworkAdapter> Adapters,
    Availability Status, string? Limitation = null);
public sealed record NetworkProperty(string RegistryKeyword, string DisplayName,
    IReadOnlyList<string> RegistryValues, string? DisplayValue, IReadOnlyList<string> ValidRegistryValues,
    IReadOnlyList<string> ValidDisplayValues, string Function, string Consequences,
    string Recommendation, string ExpectedImpact, ChangeRisk Risk = ChangeRisk.Moderate,
    bool AdapterRestartMayBeRequired = true)
{
    public bool CanChange => ValidRegistryValues.Count > 0 && RegistryValues.Count == 1
        && ValidRegistryValues.Contains(RegistryValues[0], StringComparer.Ordinal);
}
public sealed record AdapterDetail(Guid InterfaceGuid, string Name, string Description, string? DriverVersion,
    string? DriverInformation, string? DriverDate, string? LinkSpeed, IReadOnlyList<NetworkProperty> AdvancedProperties,
    IReadOnlyList<HardwareFact> Rss, IReadOnlyList<HardwareFact> Statistics,
    Availability Status, IReadOnlyList<string> Limitations,
    string Source = "PowerShell NetAdapter (Windows inbox)", IReadOnlyList<HardwareFact>? PowerManagement = null);

public interface INetworkPropertyAccess
{
    Task<NetworkProperty> GetPropertyAsync(Guid interfaceGuid, string registryKeyword, CancellationToken cancellationToken = default);
    Task SetPropertyExpectedAsync(Guid interfaceGuid, string registryKeyword, IReadOnlyList<string> registryValues,
        IReadOnlyList<string> expectedCurrentValues, CancellationToken cancellationToken = default);
}

public sealed record PingObservation(int Sequence, DateTimeOffset Timestamp, string Status,
    double? RoundTripMilliseconds, string? Error = null);
public sealed record PingMeasurement(string Host, DateTimeOffset StartedAt, DateTimeOffset EndedAt,
    IReadOnlyList<PingObservation> Observations, int RequestedCount, int SentCount, int ReceivedCount,
    double? LossPercent, double? MinimumRttMs, double? AverageRttMs, double? MaximumRttMs,
    double? P95RttMs, double? JitterMs, bool Cancelled, string Limitation, string? ResolvedAddress = null)
{
    public const string JitterDefinition = "Média de |RTT(i) - RTT(i-1)| entre respostas sucessivas; lacunas interrompem pares. Não é RFC 3550.";
}
