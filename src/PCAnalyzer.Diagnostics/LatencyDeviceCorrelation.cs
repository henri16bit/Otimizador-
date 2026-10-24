using System.Globalization;
using PCAnalyzer.Core;

namespace PCAnalyzer.Diagnostics;

/// <summary>An association of observed identities, never proof that a device caused a latency event.</summary>
public sealed record LatencyDeviceRelation(string Module, string? DeviceId, string DeviceName,
    bool ExactAssociation, string Observation, string NextStep, IReadOnlyList<string> TechnicalDetails);

/// <summary>Joins ETW kernel image evidence to the existing audit; performs no additional system reads.</summary>
public static class LatencyDeviceCorrelation
{
    private static readonly string[] ImageKeys = ["driverImagePath", "driverBinaryPath", "serviceImagePath"];

    public static IReadOnlyList<LatencyDeviceRelation> Analyze(EtwCaptureResult capture, PerformanceAuditReport? audit)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var identities = (audit?.Assessments ?? []).Select(a => a.Observation)
            .Where(o => o.RuleId == "devices.identity" && o.Status == Availability.Available)
            .GroupBy(o => o.InstanceId, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToArray();
        var events = (capture.Timeline ?? []).Where(ValidEvent).ToArray();
        var modules = capture.ModuleSummaries.Where(s => ValidKind(s.Kind) && s.Count > 0 &&
                double.IsFinite(s.TotalMicroseconds) && s.TotalMicroseconds >= 0 &&
                double.IsFinite(s.MaximumMicroseconds) && s.MaximumMicroseconds >= 0)
            .Select(s => s.Module).Concat(events.Select(e => e.Module))
            .Where(m => !string.IsNullOrWhiteSpace(m)).Select(m => NormalizePath(m!))
            .Where(m => m.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
        var result = new List<LatencyDeviceRelation>();
        foreach (var modulePath in modules)
        {
            var module = FileName(modulePath);
            var candidates = identities.Select(o => (Observation: o, Match: Match(modulePath, o)))
                .Where(c => c.Match != MatchKind.None).ToArray();
            var device = candidates.Length == 1 ? candidates[0].Observation : null;
            // A matching service or filename alone is a candidate, not an exact device identity.
            var exact = device is not null && candidates[0].Match == MatchKind.FullPath;
            var observed = events.Where(e => NormalizePath(e.Module ?? "").Equals(modulePath, StringComparison.OrdinalIgnoreCase)).ToArray();
            var cpuGroups = observed.GroupBy(e => e.Cpu).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).ToArray();
            var activity = observed.Length == 0
                ? "O módulo consta nos agregados; a distribuição por CPU não está disponível na timeline."
                : $"Foram retidos {observed.Length} eventos deste módulo em {cpuGroups.Length} índice(s) de CPU do ETW.";
            var association = candidates.Length switch
            {
                0 => "Nenhum dispositivo pôde ser associado pelas identidades lidas.",
                > 1 => $"Há {candidates.Length} dispositivos candidatos; o módulo não permite distinguir qual produziu a atividade.",
                _ when exact => "O caminho do módulo coincide com o binário registrado para um dispositivo no snapshot.",
                _ when candidates[0].Match == MatchKind.FileName => "O nome do binário coincide com o de um dispositivo; o caminho completo não foi confirmado.",
                _ => "O nome do módulo coincide com o serviço de um dispositivo; o binário desse serviço não foi confirmado."
            };
            var details = new List<string>
            {
                "Módulo ETW: " + modulePath,
                association,
                SnapshotContext(capture, audit),
                "O índice de CPU do ETW não contém identidade de grupo de processadores neste modelo. Não é comparado automaticamente com bits de afinidade.",
                "A configuração MSI/MSI-X persistente e os recursos IRQ não demonstram o modo efetivo durante os eventos. CPU compartilhada não é conflito.",
                "Não há série de utilização de CPU nem frametimes sincronizados nesta associação; concentração de eventos não comprova coincidência com carga alta ou stuttering."
            };
            foreach (var candidate in candidates)
            {
                var o = candidate.Observation;
                details.Add($"Candidato: {o.Name}; instância={o.InstanceId}; serviço={Fact(o, "service") ?? "não identificado"}; associação={candidate.Match}.");
                foreach (var key in ImageKeys)
                    if (Fact(o, key) is { Length: > 0 } image) details.Add(key + "=" + image);
                foreach (var setting in (audit?.Assessments ?? []).Select(a => a.Observation).Where(s =>
                    s.InstanceId.Equals(o.InstanceId, StringComparison.OrdinalIgnoreCase) &&
                    s.RuleId is "devices.interrupt.msi" or "devices.interrupt.capability" or "devices.interrupt.affinity" or "devices.interrupt.policy" or "devices.interrupt.allocated"))
                {
                    details.Add($"{setting.RuleId}: {setting.RawValue ?? "não lido"}; estado={setting.Status}; origem={setting.Source}.");
                    foreach (var fact in setting.Facts.Where(f => f.Key is "allocatedResources" or "maskHex" or "logicalBits" or "groupContext" or "maskPolicyApplies" or "policyDescription"))
                        details.Add(fact.Key + "=" + fact.Value);
                }
            }
            foreach (var cpuKind in observed.GroupBy(e => (e.Cpu, e.Kind)).OrderBy(g => g.Key.Cpu).ThenBy(g => g.Key.Kind))
            {
                var timed = cpuKind.Where(e => e.TraceRelativeMilliseconds is { } t && double.IsFinite(t) && t >= 0 &&
                    t < TimeSpan.MaxValue.TotalMilliseconds && t <= capture.Duration.TotalMilliseconds).ToArray();
                var windows = timed.Select(e => (long)(e.TraceRelativeMilliseconds!.Value / 1000)).Distinct().Count();
                details.Add($"Índice CPU ETW {cpuKind.Key.Cpu}, {cpuKind.Key.Kind}: {cpuKind.Count()} eventos retidos; " +
                    $"máximo={cpuKind.Max(e => e.DurationMicroseconds).ToString("0.###", CultureInfo.InvariantCulture)} µs; " +
                    $"posição temporal válida em {timed.Length} eventos / {windows} janela(s) de um segundo.");
            }
            if (capture.TimelineTruncated || capture.EventsLost > 0 || capture.LimitReached || capture.Cancelled || capture.Status != Availability.Available)
                details.Add("Coleta parcial: distribuição e associação descrevem somente evidências preservadas; eventos perdidos não foram reconstruídos.");
            result.Add(new(module, device?.InstanceId,
                device is not null ? device.Name : candidates.Length > 1 ? "Dispositivo ambíguo" : "Dispositivo não identificado",
                exact, association + " " + activity + " Isso não demonstra que a política do dispositivo causa atrasos.",
                candidates.Length == 0 ? "Preserve o trace e identifique o binário/serviço do módulo antes de relacioná-lo a um dispositivo." :
                candidates.Length > 1 ? "Confirme o dispositivo e a versão do binário; capture durante o sintoma antes de investigar uma política de interrupção." :
                "Se houver travamentos, repita durante o sintoma e compare os eventos com frametimes; não escolha núcleos ou MSI por esta associação isolada.", details));
        }
        return result;
    }

    private static MatchKind Match(string module, AuditObservation device)
    {
        var images = ImageKeys.Select(k => Fact(device, k)).Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => NormalizePath(v!)).Where(v => FileName(v).EndsWith(".sys", StringComparison.OrdinalIgnoreCase) ||
                FileName(v).EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || FileName(v).EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (images.Any(image => HasPath(image) && HasPath(module) && image.Equals(module, StringComparison.OrdinalIgnoreCase)))
            return MatchKind.FullPath;
        if (images.Any(image => FileName(image).Equals(FileName(module), StringComparison.OrdinalIgnoreCase) && (!HasPath(image) || !HasPath(module))))
            return MatchKind.FileName;
        // Known contradictory paths/names must not be overridden by a similar service name.
        if (images.Length > 0) return MatchKind.None;
        var service = Fact(device, "service")?.Trim();
        var name = FileName(module);
        return !string.IsNullOrWhiteSpace(service) && name.EndsWith(".sys", StringComparison.OrdinalIgnoreCase) &&
            name[..^4].Equals(service, StringComparison.OrdinalIgnoreCase) ? MatchKind.ServiceName : MatchKind.None;
    }

    private static string SnapshotContext(EtwCaptureResult capture, PerformanceAuditReport? audit)
    {
        if (audit is null) return "Snapshot de dispositivos indisponível; não foi inferido um inventário do trace.";
        var traceStart = capture.TraceStartUtc ?? capture.StartedAt;
        var duration = capture.Duration >= TimeSpan.Zero ? capture.Duration : TimeSpan.Zero;
        var age = audit.CapturedAt - traceStart;
        var simultaneous = age >= TimeSpan.Zero && age <= duration && capture.SessionName is not null;
        return $"Snapshot de dispositivos: {audit.CapturedAt:O}; início ETW: {traceStart:O}. " +
            (simultaneous ? "Snapshot lido na janela, mas não comprova estado efetivo do hardware em cada evento." :
                "Snapshot separado ou captura histórica/importada; não comprova a configuração vigente durante os eventos.");
    }

    private static bool ValidEvent(LatencyEvent e) => ValidKind(e.Kind) && e.Cpu is >= 0 and <= 65_535 &&
        double.IsFinite(e.DurationMicroseconds) && e.DurationMicroseconds >= 0;
    private static bool ValidKind(string kind) => kind is "DPC" or "TimerDPC" or "ThreadedDPC" or "ISR";
    private static bool HasPath(string value) => value.Contains('\\');
    private static string FileName(string value) => value[(value.LastIndexOf('\\') + 1)..];
    private static string NormalizePath(string value)
    {
        var result = value.Trim().Trim('"').Replace('/', '\\');
        return result.StartsWith(@"\??\", StringComparison.Ordinal) ? result[4..] : result;
    }
    private static string? Fact(AuditObservation o, string key) => o.Facts.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    private enum MatchKind { None, ServiceName, FileName, FullPath }
}
