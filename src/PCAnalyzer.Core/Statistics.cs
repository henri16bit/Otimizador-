namespace PCAnalyzer.Core;

public static class Statistics
{
    // R-7: interpolação linear em (n-1)*p; mesma convenção de muitos pacotes estatísticos.
    public static double? Percentile(IEnumerable<double> values, double probability)
    {
        if (probability is < 0 or > 1 || !double.IsFinite(probability))
            throw new ArgumentOutOfRangeException(nameof(probability));
        var sorted = values.Where(double.IsFinite).Order().ToArray();
        if (sorted.Length == 0) return null;
        var position = (sorted.Length - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = Math.Min(lower + 1, sorted.Length - 1);
        var weight = position - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    public static double? Mean(IEnumerable<double> values)
    {
        var finite = values.Where(double.IsFinite).ToArray();
        if (finite.Length == 0) return null;
        var scale = finite.Max(Math.Abs);
        if (scale == 0) return 0;
        return Math.Clamp(finite.Sum(value => value / scale) / finite.Length, -1, 1) * scale;
    }

    public static double? MeanAbsoluteSuccessiveDifference(IEnumerable<double?> values)
    {
        double? previous = null;
        var differences = new List<double>();
        foreach (var value in values)
        {
            if (previous.HasValue && value.HasValue && double.IsFinite(value.Value) && double.IsFinite(previous.Value))
                differences.Add(Math.Abs(value.Value - previous.Value));
            previous = value is { } v && double.IsFinite(v) ? v : null;
        }
        return Mean(differences);
    }
}
