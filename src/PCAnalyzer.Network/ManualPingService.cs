using System.Net;
using System.Net.NetworkInformation;
using PCAnalyzer.Core;

namespace PCAnalyzer.Network;

/// <summary>Bounded, manual ICMP test. Constructing the service starts no network activity.</summary>
public sealed class ManualPingService
{
    public async Task<PingMeasurement> RunAsync(string host, int count = 10, int timeoutMs = 1500,
        CancellationToken cancellationToken = default)
    {
        ValidateHost(host);
        if (count is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(count), "Escolha 1 a 100 pacotes.");
        if (timeoutMs is < 100 or > 5000) throw new ArgumentOutOfRangeException(nameof(timeoutMs), "Timeout: 100 a 5000 ms.");
        host = host.Trim();
        var started = DateTimeOffset.UtcNow;
        var observations = new List<PingObservation>(count);
        var cancelled = false;
        IPAddress destination;
        try
        {
            if (!IPAddress.TryParse(host, out destination!))
            {
                using var dnsDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                dnsDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                var addresses = await Dns.GetHostAddressesAsync(host, dnsDeadline.Token);
                destination = addresses.FirstOrDefault(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?? addresses.FirstOrDefault() ?? throw new InvalidOperationException("DNS não retornou endereço IP.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Summarize(host, started, count, observations, true);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("A resolução DNS excedeu cinco segundos; nenhum pacote ICMP foi enviado.");
        }
        // DNS failures are surfaced without being counted as packet loss: no ICMP packet was sent.
        using var ping = new Ping();
        for (var i = 0; i < count; i++)
        {
            if (cancellationToken.IsCancellationRequested) { cancelled = true; break; }
            var timestamp = DateTimeOffset.UtcNow;
            try
            {
                var response = await ping.SendPingAsync(destination, TimeSpan.FromMilliseconds(timeoutMs),
                    new byte[32], new PingOptions(), cancellationToken);
                observations.Add(new(i + 1, timestamp, response.Status.ToString(),
                    response.Status == IPStatus.Success ? response.RoundtripTime : null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The in-flight probe has no completed result; it is excluded from the denominator.
                cancelled = true;
                break;
            }
            catch (PingException ex)
            {
                observations.Add(new(i + 1, timestamp, "LocalError", null, ex.InnerException?.Message ?? ex.Message));
            }
            if (i + 1 < count)
            {
                try { await Task.Delay(250, cancellationToken); }
                catch (OperationCanceledException) { cancelled = true; break; }
            }
        }
        return Summarize(host, started, count, observations, cancelled);
    }

    public static PingMeasurement Summarize(string host, DateTimeOffset started, int requestedCount,
        IReadOnlyList<PingObservation> observations, bool cancelled = false)
    {
        var rtts = observations.Where(x => x.Status == nameof(IPStatus.Success) &&
                x.RoundTripMilliseconds is { } value && double.IsFinite(value) && value >= 0)
            .Select(x => x.RoundTripMilliseconds!.Value).ToArray();
        // A local send failure does not prove that a packet reached the network.
        var sent = observations.Count(x => x.Status != "LocalError");
        var timeline = observations.Select(x => x.Status == nameof(IPStatus.Success) &&
            x.RoundTripMilliseconds is { } value && double.IsFinite(value) && value >= 0 ? x.RoundTripMilliseconds : null);
        return new(host, started, DateTimeOffset.UtcNow, observations.ToArray(), requestedCount, sent, rtts.Length,
            sent == 0 ? null : (sent - rtts.Length) * 100.0 / sent,
            rtts.Length == 0 ? null : rtts.Min(), Statistics.Mean(rtts), rtts.Length == 0 ? null : rtts.Max(),
            Statistics.Percentile(rtts, .95), Statistics.MeanAbsoluteSuccessiveDifference(timeline), cancelled,
            "ICMP mede somente a resposta do destino escolhido. Rate limiting, filtros e baixa prioridade de ICMP " +
            "podem causar ausência de resposta; isso não comprova perda no jogo nem mede latência de servidor VALORANT. " +
            "Erros locais e pacotes cancelados sem resultado não entram na perda. " + PingMeasurement.JitterDefinition);
    }

    public static void ValidateHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        host = host.Trim();
        if (host.Length > 253 || host.Any(char.IsWhiteSpace) ||
            (!IPAddress.TryParse(host, out _) && Uri.CheckHostName(host) != UriHostNameType.Dns))
            throw new ArgumentException("Informe apenas um IP ou nome DNS, sem URL, porta ou comando.", nameof(host));
    }
}
