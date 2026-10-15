using System.Net;
using System.Net.Sockets;

namespace PCAnalyzer.Network;

public static class GatewaySelection
{
    public static string Select(NetworkSnapshot snapshot, Guid? adapterId)
    {
        var adapters = snapshot.Adapters.Where(a => a.Status == "Up" && (adapterId is null || a.InterfaceGuid == adapterId))
            .Select(a => (Adapter: a, Gateways: a.Gateways.Where(g => IPAddress.TryParse(g, out var ip) &&
                !ip.Equals(IPAddress.Any) && !ip.Equals(IPAddress.IPv6Any)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()))
            .Where(a => a.Gateways.Length > 0).ToArray();
        if (adapters.Length == 0) throw new InvalidOperationException("Não há gateway no adaptador escolhido. Confira a conexão ou informe um IP/nome DNS no destino.");
        if (adapterId is null && adapters.Length > 1)
            throw new InvalidOperationException("Há mais de uma conexão com gateway. Em Rede, escolha o adaptador ou informe um destino explícito antes do teste.");
        return adapters[0].Gateways.FirstOrDefault(g => IPAddress.Parse(g).AddressFamily == AddressFamily.InterNetwork)
            ?? adapters[0].Gateways[0];
    }
}
