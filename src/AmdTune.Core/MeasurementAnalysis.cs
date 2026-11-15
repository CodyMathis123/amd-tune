namespace AmdTune.Core;

public sealed record DescriptiveStats(int N, double Mean, double Median, double StandardDeviation, double CoefficientOfVariationPercent);

public static class MeasurementAnalysis
{
    public static DescriptiveStats Describe(IEnumerable<double> observations)
    {
        var x = observations.ToArray();
        if (x.Length == 0 || x.Any(v => !double.IsFinite(v)))
            throw new ArgumentException("Require at least one finite observation.", nameof(observations));
        var mean = x.Average();
        var ordered = x.Order().ToArray();
        var median = ordered.Length % 2 == 1
            ? ordered[ordered.Length / 2]
            : (ordered[ordered.Length / 2 - 1] + ordered[ordered.Length / 2]) / 2;
        var standardDeviation = x.Length > 1
            ? Math.Sqrt(x.Sum(v => (v - mean) * (v - mean)) / (x.Length - 1))
            : 0;
        var cv = mean == 0 ? 0 : Math.Abs(standardDeviation / mean) * 100;
        return new(x.Length, mean, median, standardDeviation, cv);
    }

    /// <summary>Paired baseline-candidate-baseline comparisons reduce thermal-drift bias.</summary>
    public static double RelativeChangePercent(double before, double candidate, double after)
    {
        if (before <= 0 || after <= 0 || candidate <= 0)
            throw new ArgumentOutOfRangeException(nameof(candidate), "Measurements must be positive.");
        var reference = (before + after) / 2;
        return 100 * (candidate - reference) / reference;
    }
}
