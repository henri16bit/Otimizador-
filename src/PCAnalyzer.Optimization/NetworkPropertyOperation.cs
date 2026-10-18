using System.Text.Json;
using PCAnalyzer.Core;
using PCAnalyzer.Network;

namespace PCAnalyzer.Optimization;

public sealed class NetworkPropertyOperation : IExpectedValueOperation
{
    private readonly INetworkPropertyAccess _inspector;
    public Guid InterfaceGuid { get; }
    public string RegistryKeyword { get; }
    public string Kind => "network-advanced-property";
    public string Target => InterfaceGuid.ToString("D") + ":" + RegistryKeyword;
    public ChangeRisk Risk => ChangeRisk.Moderate;
    public string RecoveryInstructions =>
        "O histórico preserva os valores originais para consulta. Esta versão não altera nem restaura o adaptador. " +
        "Um valor persistido não comprova que a configuração entrou em vigor ou melhorou o desempenho.";

    public NetworkPropertyOperation(INetworkPropertyAccess inspector, Guid interfaceGuid, string registryKeyword)
    {
        ArgumentNullException.ThrowIfNull(inspector);
        if (interfaceGuid == Guid.Empty) throw new ArgumentException("InterfaceGuid vazio.", nameof(interfaceGuid));
        NetworkInspector.ValidateKeyword(registryKeyword);
        _inspector = inspector;
        InterfaceGuid = interfaceGuid;
        RegistryKeyword = registryKeyword;
    }
    public async Task<string> ReadAsync(CancellationToken cancellationToken) =>
        SerializeValues((await _inspector.GetPropertyAsync(InterfaceGuid, RegistryKeyword, cancellationToken)).RegistryValues);

    public async Task ValidateAsync(string value, CancellationToken cancellationToken)
    {
        var values = ParseValues(value);
        var property = await _inspector.GetPropertyAsync(InterfaceGuid, RegistryKeyword, cancellationToken);
        if (!property.CanChange || !property.ValidRegistryValues.Contains(values[0], StringComparer.Ordinal))
            throw new ArgumentException("O valor não é escalar/suportado pelo driver atual. Nenhuma alteração será feita.", nameof(value));
    }
    public Task WriteAsync(string value, CancellationToken cancellationToken) =>
        Task.FromException(AuditOnlyPolicy.ModificationNotSupported());
    public Task WriteExpectedAsync(string value, string expectedCurrent, CancellationToken cancellationToken) =>
        Task.FromException(AuditOnlyPolicy.ModificationNotSupported());
    public static string SerializeValues(IEnumerable<string> values) => JsonSerializer.Serialize(values.ToArray());
    public static string SerializeValue(string value) => SerializeValues([value]);
    public static string[] ParseValues(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (json.Length > 1024) throw new ArgumentException("Valor de propriedade excedeu o limite.", nameof(json));
        string[]? values;
        try { values = JsonSerializer.Deserialize<string[]>(json); }
        catch (JsonException ex) { throw new ArgumentException("O valor deve ser um array JSON de strings.", nameof(json), ex); }
        if (values is not { Length: 1 } || values[0] is null || values[0].Length > 128)
            throw new ArgumentException("A alteração deve conter exatamente um valor de driver.", nameof(json));
        if (!string.Equals(SerializeValues(values), json, StringComparison.Ordinal))
            throw new ArgumentException("O valor deve usar a serialização canônica SerializeValue/SerializeValues.", nameof(json));
        return values;
    }
}
