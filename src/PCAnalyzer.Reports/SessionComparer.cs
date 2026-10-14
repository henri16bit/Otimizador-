using PCAnalyzer.Core;

namespace PCAnalyzer.Reports;

public sealed record MetricComparison(string Key, string Component, string Unit,
    int BeforeSampleCount, int AfterSampleCount, double? BeforeMean, double? AfterMean,
    double? MeanDelta, double? MeanDeltaPercent, double? BeforeP95, double? AfterP95,
    double? P95Delta, double? P95DeltaPercent, bool Comparable, string? Limitation);

public sealed record SessionComparison(Guid BeforeId, Guid AfterId, bool ConditionsComparable,
    IReadOnlyList<string> Warnings, IReadOnlyList<MetricComparison> Metrics,
    string Interpretation = "Diferenças descritivas entre sessões; não demonstram causalidade, ganho de FPS ou benefício de uma alteração.");

/// <summary>Compares matching measurement semantics and warns when the experiment conditions differ.</summary>
public static class SessionComparer
{
    private static readonly HashSet<string> PhysicalFacts = new(StringComparer.OrdinalIgnoreCase)
    {
        "Manufacturer", "Model", "Product", "PartNumber", "Capacity", "Size", "TotalPhysicalMemory",
        "NumberOfCores", "NumberOfLogicalProcessors", "Architecture", "BusType", "MediaType", "DataWidth",
        "TotalWidth", "SMBIOSMemoryType", "DeviceLocator", "SocketDesignation", "VRAM total bytes", "UUID",
        "PNPDeviceID", "ProcessorId", "Modelo EDID", "Fabricante EDID"
    };
    private static readonly HashSet<string> ConfigurationFacts = new(StringComparer.OrdinalIgnoreCase)
    {
        "DriverVersion", "DriverDate", "DriverProviderName", "FirmwareVersion", "FirmwareRevision",
        "SMBIOSBIOSVersion", "SMBIOSMajorVersion", "SMBIOSMinorVersion", "BuildNumber", "OSArchitecture",
        "ConfiguredClockSpeed", "Plano ativo GUID", "Firmware", "Resolução atual", "Frequência atual Hz", "Principal"
    };

    public static SessionComparison Compare(DiagnosticSession before, DiagnosticSession after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var warnings = new List<string>();
        if (!Equivalent(before.Context.Workload, after.Context.Workload))
            warnings.Add("A carga de trabalho difere entre as sessões.");
        if (!Equivalent(before.Context.CollectionMode, after.Context.CollectionMode))
            warnings.Add("O modo de coleta difere entre as sessões.");
        if (!SameInterval(before.Context.IntervalSeconds, after.Context.IntervalSeconds))
            warnings.Add("O intervalo configurado de amostragem difere ou é inválido.");
        var firstFingerprint = before.Context.HardwareFingerprint;
        var secondFingerprint = after.Context.HardwareFingerprint;
        if (!string.IsNullOrWhiteSpace(firstFingerprint) && !string.IsNullOrWhiteSpace(secondFingerprint))
        {
            if (!string.Equals(firstFingerprint, secondFingerprint, StringComparison.Ordinal))
                warnings.Add("A identificação do hardware difere entre as sessões.");
        }
        else
        {
            var firstInventory = HardwareSignature(before.Inventory);
            var secondInventory = HardwareSignature(after.Inventory);
            if (firstInventory.Length == 0 || secondInventory.Length == 0)
                warnings.Add("Sem identificação de hardware suficiente para confirmar condições equivalentes.");
            else if (!firstInventory.SequenceEqual(secondInventory, StringComparer.Ordinal))
                warnings.Add("O inventário de hardware difere entre as sessões.");
        }
        if (!ConfigurationSignature(before.Inventory).SequenceEqual(ConfigurationSignature(after.Inventory), StringComparer.Ordinal))
            warnings.Add("A configuração reportada (driver, firmware, Windows, RAM, vídeo ou energia) difere; isso não implica troca de hardware.");
        if (before.EndedAt is null || after.EndedAt is null)
            warnings.Add("Uma das sessões ainda estava em coleta ao ser comparada.");
        var conditionsComparable = warnings.Count == 0;

        var firstGroups = before.Samples.GroupBy(Identity).ToDictionary(group => group.Key, group => group.ToArray());
        var secondGroups = after.Samples.GroupBy(Identity).ToDictionary(group => group.Key, group => group.ToArray());
        var keys = firstGroups.Keys.Union(secondGroups.Keys).OrderBy(key => key.Key, StringComparer.Ordinal)
            .ThenBy(key => key.Component, StringComparer.Ordinal).ThenBy(key => key.Unit, StringComparer.Ordinal);
        var metrics = new List<MetricComparison>();
        foreach (var key in keys)
        {
            var first = firstGroups.GetValueOrDefault(key) ?? [];
            var second = secondGroups.GetValueOrDefault(key) ?? [];
            var firstValid = Valid(first);
            var secondValid = Valid(second);
            var limitations = new List<string>();
            var comparable = firstValid.Length > 0 && secondValid.Length > 0;
            if (firstValid.Length == 0 || secondValid.Length == 0)
                limitations.Add("Medição ausente ou indisponível em uma das sessões; nenhuma diferença foi calculada.");
            if (firstValid.Length < 10 || secondValid.Length < 10)
                limitations.Add("Menos de 10 amostras válidas em uma das sessões; percentis têm baixa estabilidade.");
            if (!Metadata(first).SequenceEqual(Metadata(second), StringComparer.Ordinal))
            {
                comparable = false;
                limitations.Add("Fonte, método, tipo de medição ou agregação diferem; nenhuma diferença foi calculada.");
            }
            var firstIntervals = first.Where(IsValid).Select(sample => sample.SamplingInterval.TotalSeconds).ToArray();
            var secondIntervals = second.Where(IsValid).Select(sample => sample.SamplingInterval.TotalSeconds).ToArray();
            if (!EquivalentCadence(firstIntervals, secondIntervals))
            {
                conditionsComparable = false;
                limitations.Add("Os intervalos efetivos das amostras diferem.");
            }
            if (first.Concat(second).Any(sample => IsValid(sample) && sample.Aggregation == Aggregation.Cumulative))
            {
                comparable = false;
                limitations.Add("Totais acumulados dependem da duração e do ponto inicial do contador; compare uma taxa derivada ou incremento na mesma janela, não a diferença de médias desses totais.");
            }
            if (firstValid.Length != secondValid.Length)
                limitations.Add("As sessões possuem quantidades diferentes de amostras válidas.");
            var firstMean = Statistics.Mean(firstValid);
            var secondMean = Statistics.Mean(secondValid);
            var firstP95 = Statistics.Percentile(firstValid, .95);
            var secondP95 = Statistics.Percentile(secondValid, .95);
            var meanDelta = comparable ? Delta(firstMean, secondMean) : null;
            var p95Delta = comparable ? Delta(firstP95, secondP95) : null;
            if (limitations.Count > 0) warnings.Add($"{key.Key} ({key.Unit}): {string.Join(" ", limitations)}");
            metrics.Add(new(key.Key, key.Component, key.Unit, firstValid.Length, secondValid.Length,
                firstMean, secondMean, meanDelta, comparable ? PercentDelta(firstMean, secondMean) : null,
                firstP95, secondP95, p95Delta, comparable ? PercentDelta(firstP95, secondP95) : null,
                comparable, limitations.Count == 0 ? null : string.Join(" ", limitations)));
        }
        foreach (var group in keys.GroupBy(key => (key.Key, key.Component)).Where(group => group.Select(key => key.Unit).Distinct().Count() > 1))
            warnings.Add($"{group.Key.Key}: unidades diferentes; os resultados foram mantidos separados.");
        if (metrics.Count == 0) warnings.Add("As sessões não contêm amostras para comparação.");
        return new(before.Id, after.Id, conditionsComparable && metrics.Count > 0, warnings, metrics);
    }

    private static (string Key, string Component, string Unit) Identity(MetricSample sample) => (sample.Key, sample.Component, sample.Unit);
    private static bool IsValid(MetricSample sample) => sample.Status == Availability.Available && sample.Value is { } value && double.IsFinite(value);
    private static double[] Valid(IEnumerable<MetricSample> samples) => samples.Where(IsValid).Select(sample => sample.Value!.Value).ToArray();
    private static string[] Metadata(IEnumerable<MetricSample> samples) => samples.Where(IsValid)
        .Select(sample => $"{sample.Source}\u001f{sample.Method}\u001f{sample.Kind}\u001f{sample.Aggregation}")
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static bool Equivalent(string first, string second) => string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
    private static bool EquivalentCadence(double[] first, double[] second)
    {
        if (first.Length == 0 || second.Length == 0) return first.Length == second.Length;
        if (first.Concat(second).Any(interval => interval < 0 || !double.IsFinite(interval))) return false;
        // Variações normais do scheduler não tornam duas coletas de 1 s incompatíveis.
        // Quantis também detectam misturas de perfis sem comparar cada tick do relógio.
        foreach (var quantile in new[] { .25, .5, .95 })
        {
            var a = Statistics.Percentile(first, quantile)!.Value;
            var b = Statistics.Percentile(second, quantile)!.Value;
            if ((a == 0) != (b == 0) || Math.Abs(a - b) > Math.Max(.05, Math.Max(a, b) * .1)) return false;
        }
        return true;
    }
    private static bool SameInterval(double first, double second) => first > 0 && second > 0 && double.IsFinite(first)
        && double.IsFinite(second) && Math.Abs(first - second) <= Math.Max(first, second) * 1e-6;
    private static double? Delta(double? first, double? second) => first is { } a && second is { } b && double.IsFinite(b - a) ? b - a : null;
    private static double? PercentDelta(double? first, double? second) => first is { } a && second is { } b && a != 0
        && double.IsFinite((b - a) / Math.Abs(a) * 100) ? (b - a) / Math.Abs(a) * 100 : null;
    private static string[] HardwareSignature(HardwareInventory inventory) => inventory.Components
        .Where(component => component.Id is not ("system" or "bios" or "firmware" or "power"))
        .Select(component => $"{component.Category}\u001f{component.Id}\u001f{component.Name}\u001f" +
            string.Join("\u001e", component.Facts.Where(fact => fact.Status == Availability.Available && fact.Value is not null && PhysicalFacts.Contains(fact.Name))
                .OrderBy(fact => fact.Name, StringComparer.Ordinal).Select(fact => fact.Name + "=" + fact.Value)))
        .Order(StringComparer.Ordinal).ToArray();
    private static string[] ConfigurationSignature(HardwareInventory inventory) => inventory.Components
        .SelectMany(component => component.Facts.Where(fact => fact.Status == Availability.Available && fact.Value is not null &&
                (ConfigurationFacts.Contains(fact.Name) || fact.Name.Equals("Version", StringComparison.OrdinalIgnoreCase) && (component.Id is "system" or "bios")))
            .Select(fact => component.Id + "\u001f" + fact.Name + "=" + fact.Value))
        .Order(StringComparer.Ordinal).ToArray();
}
