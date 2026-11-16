namespace AmdTune.Core;

/// <summary>
/// Passive monitoring of an external graphics or VRAM stress application.
/// Does not launch, control or terminate the external program and never
/// applies GPU tuning settings. ADLX observations are asynchronous.
/// </summary>
public sealed record ExternalLoadMinute(
    int Minute,
    int Samples,
    double? MeanGpuUtilizationPercent,
    double? MeanBoardPowerW,
    double? PeakBoardPowerW,
    double? MeanHotspotC,
    double? PeakHotspotC,
    double? MeanCoreClockMhz);

public sealed record ExternalLoadSummary(
    int Samples,
    int SourceTimestampDistinctSamples,
    int DistinctMetricValueStates,
    double ObservedSeconds,
    double? MeanGpuUtilizationPercent,
    double? PeakGpuUtilizationPercent,
    double? MeanBoardPowerW,
    double? PeakBoardPowerW,
    double? MeanHotspotC,
    double? PeakHotspotC,
    double? MeanCoreClockMhz,
    double? PeakCoreClockMhz,
    double? HighUtilizationSampleFraction,
    double? LaterMinuteVsEarlyMinutePowerChangePercent,
    IReadOnlyList<ExternalLoadMinute> Minutes,
    IReadOnlyList<string> Limitations);

public static class ExternalLoadAnalysis
{
    public const int MinimumDurationSeconds = 30;
    public const int MaximumDurationSeconds = 900;
    public const int RecommendedDurationSeconds = 300;
    public const int MaxRetainedSamples = 16_384;

    public static ExternalLoadSummary Analyze(
        IReadOnlyList<GpuTelemetry> rawSamples, DateTimeOffset started, DateTimeOffset finished)
    {
        if (finished < started)
            throw new ArgumentException("Monitoring interval must be positive.");
        var samples = rawSamples.Where(s => s.At >= started && s.At <= finished)
            .OrderBy(s => s.At).ToArray();
        if (samples.Length == 0)
            throw new ArgumentException("Monitoring returned no valid sensor samples.");

        static double? Mean(IEnumerable<double?> values)
        {
            var a = values.Where(v => v.HasValue && double.IsFinite(v.Value))
                .Select(v => v!.Value).ToArray();
            return a.Length == 0 ? null : a.Average();
        }
        static double? Peak(IEnumerable<double?> values)
        {
            var a = values.Where(v => v.HasValue && double.IsFinite(v.Value))
                .Select(v => v!.Value).ToArray();
            return a.Length == 0 ? null : a.Max();
        }
        static double? Power(IEnumerable<GpuTelemetry> readings) =>
            Mean(readings.Where(x => x.BoardPowerW is >= 0).Select(x => x.BoardPowerW));

        var data = samples.Select(s => (
            s.HotspotC, s.BoardPowerW, s.CoreClockMhz, s.MemoryClockMhz, s.UtilizationPercent))
            .Distinct().Count();
        var hasAllTimestamps = samples.All(s => s.MetricTimestampMs is > 0);
        var distinct = hasAllTimestamps
            ? samples.Select(s => s.MetricTimestampMs!.Value).Distinct().Count()
            : 0;
        var bins = samples.GroupBy(s => (int)Math.Floor((s.At - started).TotalSeconds / 60))
            .OrderBy(g => g.Key)
            .Select(g => new ExternalLoadMinute(
                g.Key, g.Count(),
                Mean(g.Select(s => s.UtilizationPercent)),
                Power(g),
                Peak(g.Select(s => s.BoardPowerW)),
                Mean(g.Select(s => s.HotspotC)),
                Peak(g.Select(s => s.HotspotC)),
                Mean(g.Select(s => (double?)s.CoreClockMhz))))
            .ToArray();

        // Compare the first and last *full minute* only. A partial final
        // minute or any unobserved minute must not be treated as a plateau.
        double? change = null;
        var firstMinute = samples.Where(s => s.At < started.AddSeconds(60)).ToArray();
        var lastMinute = samples.Where(s =>
            s.At >= finished.AddSeconds(-60) && s.At <= finished).ToArray();
        var firstPower = Power(firstMinute);
        var lastPower = Power(lastMinute);
        if ((finished - started).TotalSeconds >= 120 &&
            firstMinute.Length >= 3 && lastMinute.Length >= 3 &&
            firstPower is > 0 && lastPower.HasValue)
            change = 100 * (lastPower.Value / firstPower.Value - 1);

        return new ExternalLoadSummary(
            samples.Length, distinct, data,
            (finished - started).TotalSeconds,
            Mean(samples.Select(s => s.UtilizationPercent)),
            Peak(samples.Select(s => s.UtilizationPercent)),
            Power(samples),
            Peak(samples.Select(s => s.BoardPowerW)),
            Mean(samples.Select(s => s.HotspotC)),
            Peak(samples.Select(s => s.HotspotC)),
            Mean(samples.Select(s => (double?)s.CoreClockMhz)),
            Peak(samples.Select(s => (double?)s.CoreClockMhz)),
            (double)samples.Count(s => s.UtilizationPercent is >= 80 and <= 100) / samples.Length,
            change,
            bins,
            [
                "Passive ADLX observations only: does not validate external stress-tool completion, frame rate, rendered output or VRAM error counts.",
                "The monitor does not start, stop, throttle or terminate the external stress application. A guard failure stops monitoring ONLY.",
                "ADLX metrics may be averaged, rounded or lagged; source timestamps and value changes do not establish independent per-sample hardware measurements.",
                "Read-only monitoring cannot establish stability at untested tuning profiles, GPU-queue execution time or GPU performance-per-watt."
            ]);
    }
}
