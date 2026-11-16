namespace AmdTune.Core;

/// <summary>
/// Read-only ABBA crossover of two telemetry request intervals.
/// This is a repeatability diagnostic, NOT a performance-tuning optimizer.
/// </summary>
public sealed record MonitoringObservation(
    WorkloadKind Kind,
    int Round,
    int Position,
    int RequestedIntervalMs,
    WorkloadMeasurement Measurement);

public sealed record MonitoringKindSummary(
    WorkloadKind Kind,
    DescriptiveStats Fast,
    DescriptiveStats Slow,
    double SlowVsFastPercent,
    int FastDistinctSourceSamples,
    int SlowDistinctSourceSamples,
    double? FastObservedMeanUtilizationPercent,
    double? SlowObservedMeanUtilizationPercent);

public static class MonitoringComparison
{
    public const int FastIntervalMs = 125;
    public const int SlowIntervalMs = 500;
    public const int Rounds = 2;
    public const int RunsPerIntervalAndKind = 4;

    public static IReadOnlyList<int> IntervalsForRound(int round)
    {
        if (round < 0 || round >= Rounds)
            throw new ArgumentOutOfRangeException(nameof(round));
        // ABBA followed by BAAB balances time-order effects and keeps a
        // stable tuning profile for each paired measurement.
        return round % 2 == 0
            ? [FastIntervalMs, SlowIntervalMs, SlowIntervalMs, FastIntervalMs]
            : [SlowIntervalMs, FastIntervalMs, FastIntervalMs, SlowIntervalMs];
    }

    public static IReadOnlyList<MonitoringKindSummary> Summarize(
        IReadOnlyList<MonitoringObservation> observations)
    {
        var summaries = new List<MonitoringKindSummary>();
        foreach (var kind in Enum.GetValues<WorkloadKind>())
        {
            var set = observations.Where(o => o.Kind == kind).ToArray();
            if (set.Length != Rounds * 4 ||
                set.Select(o => (o.Round, o.Position)).Distinct().Count() != set.Length)
                throw new ArgumentException("Monitoring comparison is missing workloads or contains duplicate positions.");
            foreach (var round in Enumerable.Range(0, Rounds))
                foreach (var (interval, position) in IntervalsForRound(round).Select((ms, i) => (ms, i)))
                    if (!set.Any(o => o.Round == round && o.Position == position &&
                                      o.RequestedIntervalMs == interval))
                        throw new ArgumentException("Monitoring comparison schedule does not match ABBA/BAAB.");

            var fast = set.Where(o => o.RequestedIntervalMs == FastIntervalMs)
                .Select(o => o.Measurement).ToArray();
            var slow = set.Where(o => o.RequestedIntervalMs == SlowIntervalMs)
                .Select(o => o.Measurement).ToArray();
            if (fast.Length != RunsPerIntervalAndKind || slow.Length != RunsPerIntervalAndKind ||
                fast.Concat(slow).Any(m => !m.ExecutedOnGpu || !m.Correct ||
                    m.Mismatches != 0 || m.Score <= 0 || !double.IsFinite(m.Score)))
                throw new ArgumentException("Monitoring comparison requires complete correct GPU results.");

            var fastStats = MeasurementAnalysis.Describe(fast.Select(m => m.Score));
            var slowStats = MeasurementAnalysis.Describe(slow.Select(m => m.Score));
            static double? MeanUtil(IEnumerable<WorkloadMeasurement> m)
            {
                var readings = m.Select(x => x.TelemetryEvidence?.Diagnostics?.MeanUtilizationPercent)
                    .Where(x => x.HasValue).Select(x => x!.Value).ToArray();
                return readings.Length == 0 ? null : readings.Average();
            }
            summaries.Add(new MonitoringKindSummary(
                kind, fastStats, slowStats,
                (slowStats.Mean / fastStats.Mean - 1) * 100,
                fast.Sum(m => m.TelemetryEvidence?.Diagnostics?.DistinctMetricSamples ?? 0),
                slow.Sum(m => m.TelemetryEvidence?.Diagnostics?.DistinctMetricSamples ?? 0),
                MeanUtil(fast), MeanUtil(slow)));
        }
        return summaries;
    }
}
