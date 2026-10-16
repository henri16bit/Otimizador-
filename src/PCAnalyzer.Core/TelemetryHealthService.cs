namespace PCAnalyzer.Core;

public enum TelemetrySourceState { Healthy, Waiting, Stale, Unavailable, Paused, Error }
public sealed record TelemetrySourceHealth(string Id, string Name, TelemetrySourceState State, string Summary,
    DateTimeOffset? LastSampleAt, SuggestedNextAction NextAction);

/// <summary>Reports whether a source is delivering current observations, never the health of the hardware.</summary>
public sealed class TelemetryHealthService
{
    public IReadOnlyList<TelemetrySourceHealth> Evaluate(IReadOnlyList<MetricSample> samples,
        IReadOnlyList<ProcessSample> processes, DateTimeOffset now, TimeSpan expectedInterval, bool paused = false,
        DateTimeOffset? collectionStartedAt = null, DateTimeOffset? lastBatchAt = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(processes);
        if (expectedInterval <= TimeSpan.Zero || expectedInterval > TimeSpan.FromMinutes(5))
            throw new ArgumentOutOfRangeException(nameof(expectedInterval));
        var freshness = TimeSpan.FromSeconds(Math.Max(5, expectedInterval.TotalSeconds * 3));
        var warmingUp = collectionStartedAt is null || now - collectionStartedAt <= freshness;
        return
        [
            Assess("cpu", "CPU", samples.Where(s => s.Key == "cpu.total.percent"),
                s => s.Unit == "%" && s.Value is >= 0 and <= 100),
            Assess("ram", "RAM", samples.Where(s => s.Key == "memory.available.bytes" || s.Key == "memory.telemetry"),
                s => s.Key == "memory.available.bytes" && s.Unit == "bytes" && s.Value >= 0),
            Assess("gpu", "GPU", samples.Where(s => s.Key == "gpu.telemetry" ||
                s.Key.StartsWith("gpu.", StringComparison.Ordinal) && s.Key.EndsWith(".utilization.percent", StringComparison.Ordinal)),
                s => s.Unit == "%" && s.Value is >= 0 and <= 100),
            Assess("network", "Rede", samples.Where(s => s.Key.StartsWith("network.", StringComparison.Ordinal) &&
                (s.Key.EndsWith(".bytes_per_second", StringComparison.Ordinal) || s.Key.EndsWith(".telemetry", StringComparison.Ordinal))),
                s => s.Unit == "bytes/s" && s.Value >= 0),
            Assess("processes", "Processos", samples.Where(s => s.Key == "process.telemetry").Concat(processes.Select(p =>
                new MetricSample("process.observation", "Processos", p.WorkingSetBytes, "bytes", "Process", "Leitura do processo",
                    p.Timestamp, expectedInterval, p.WorkingSetBytes is >= 0 ? Availability.Available : Availability.InsufficientData,
                    Limitation: p.Limitation))), s => s.Key == "process.observation" && s.Value >= 0)
        ];

        TelemetrySourceHealth Assess(string id, string name, IEnumerable<MetricSample> source, Func<MetricSample, bool> usable)
        {
            var observations = source.OrderByDescending(s => s.Timestamp)
                .ThenBy(s => s.Status == Availability.Collecting ? 2 : s.Status == Availability.Available ? 1 : 0).ToArray();
            var valid = observations.FirstOrDefault(s => s.Status == Availability.Available && s.Value is { } value &&
                double.IsFinite(value) && usable(s) && s.Timestamp <= now.AddSeconds(1));
            var latest = observations.FirstOrDefault();
            var last = valid?.Timestamp;
            var retry = new SuggestedNextAction("retry-telemetry", "Verificar coleta", "fontes", "Consulte a limitação da fonte de " + name + " e repita a leitura.");
            if (paused) return new(id, name, TelemetrySourceState.Paused,
                "Coleta pausada. Os valores anteriores não representam uma nova leitura.", last,
                new("resume-telemetry", "Retomar coleta", "fontes", "Retome a coleta para atualizar as leituras."));
            if (valid is not null && now - valid.Timestamp <= freshness &&
                (latest is null || latest.Status == Availability.Available && usable(latest) &&
                 latest.Value is { } latestValue && double.IsFinite(latestValue) && latest.Timestamp <= valid.Timestamp ||
                 latest.Status == Availability.Collecting))
                return new(id, name, TelemetrySourceState.Healthy,
                    "Leitura recente recebida. Isso confirma a fonte, não a saúde do componente.", last, retry);
            if (latest is not null && now - latest.Timestamp <= freshness)
            {
                if (latest.Timestamp > now.AddSeconds(1)) return new(id, name, TelemetrySourceState.Error,
                    "A fonte informou uma data futura; a leitura não foi tratada como atual.", last, retry);
                if (latest.Status == Availability.ReadError) return new(id, name, TelemetrySourceState.Error,
                    latest.Limitation ?? "A leitura da fonte falhou. Os valores anteriores não foram reutilizados como atuais.", last, retry);
                if (latest.Status is Availability.Unsupported or Availability.PermissionRequired or Availability.ToolMissing)
                    return new(id, name, TelemetrySourceState.Unavailable,
                        latest.Limitation ?? "A fonte não está disponível nesta configuração.", last, retry);
                if (latest.Status == Availability.Collecting && warmingUp) return new(id, name, TelemetrySourceState.Waiting,
                    latest.Limitation ?? "Aguardando as primeiras leituras necessárias para calcular a métrica.", last, retry);
                if (latest.Status == Availability.Collecting) return new(id, name, TelemetrySourceState.Stale,
                    "A fonte continua aguardando uma leitura além do intervalo esperado. " + latest.Limitation, last, retry);
                return new(id, name, TelemetrySourceState.Unavailable,
                    latest.Limitation ?? "A fonte respondeu sem uma medição válida; nenhum zero foi preenchido.", last, retry);
            }
            if (latest is not null || lastBatchAt is { } batch && now - batch > freshness || !warmingUp)
                return new(id, name, TelemetrySourceState.Stale,
                    "Nenhuma leitura recente desta fonte. Confira o coletor antes de interpretar os valores anteriores.", last, retry);
            if (lastBatchAt is not null) return new(id, name, TelemetrySourceState.Unavailable,
                "O lote recente não trouxe dados desta fonte; a ausência não significa valor zero.", last, retry);
            return new(id, name, TelemetrySourceState.Waiting, "Aguardando o primeiro lote de telemetria.", last, retry);
        }
    }
}
