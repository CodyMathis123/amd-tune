namespace AmdTune.Core;

/// <summary>
/// A bounded, read-only measurement study. Each block tests each workload
/// once at each of three ADLX polling rates, with a balanced ordering.
/// This never writes GPU settings or approves an overclock.
/// </summary>
public sealed record StudyObservation(
    int Block,
    int Position,
    WorkloadKind Kind,
    int PollIntervalMs,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    WorkloadMeasurement Measurement);

public sealed record StudyConditionSummary(
    WorkloadKind Kind,
    int PollIntervalMs,
    int TotalMeasurements,
    int ScoredMeasurements,
    DescriptiveStats? Throughput,
    double? LaterHalfVsEarlyHalfPercent,
    double TotalHostTimedSeconds,
    int TotalRawAdlxSamples,
    int DistinctInWindowSamples,
    int ActiveInWindowSamples,
    double? PeakObservedHotspotC,
    double? MeanLoadedBoardPowerW);

public sealed record StudyPairSummary(
    WorkloadKind Kind,
    int ComparisonIntervalMs,
    int ReferenceIntervalMs,
    int PairedBlocks,
    DescriptiveStats? PairedPercentDifferences);

public sealed record StudySummary(
    int TotalBlocks,
    int DiscardedWarmupBlocks,
    int TotalMeasurements,
    double TotalHostTimedSeconds,
    int TotalRawAdlxSamples,
    int TotalDistinctInWindowSamples,
    int TotalActiveInWindowSamples,
    double? PeakObservedHotspotC,
    IReadOnlyList<StudyConditionSummary> Conditions,
    IReadOnlyList<StudyPairSummary> PairedComparisons,
    IReadOnlyList<string> Limitations);

public static class ReadOnlyStudy
{
    public const int DefaultSeconds = 300;
    public const int MinimumSeconds = 120;
    public const int MaximumSeconds = 900;
    public const int MaxBlocks = 96;
    public static readonly IReadOnlyList<int> IntervalsMs = [125, 250, 500];

    private static readonly int[][] Rotations =
    [
        [125, 250, 500],
        [250, 500, 125],
        [500, 125, 250],
        [500, 250, 125],
        [250, 125, 500],
        [125, 500, 250]
    ];

    /// <summary>Round-robin all nine test conditions in a rotating order.</summary>
    public static IReadOnlyList<(WorkloadKind Kind, int IntervalMs)> Schedule(int block)
    {
        if (block < 0 || block >= MaxBlocks)
            throw new ArgumentOutOfRangeException(nameof(block));
        var order = Rotations[block % Rotations.Length];
        var result = new List<(WorkloadKind Kind, int IntervalMs)>(9);
        for (int k = 0; k < 3; k++)
        {
            var kind = (WorkloadKind)((block + k) % 3);
            foreach (var ms in order)
                result.Add((kind, ms));
        }
        return result;
    }

    public static StudySummary Summarize(IReadOnlyList<StudyObservation> observations)
    {
        if (observations.Count == 0 || observations.Count % 9 != 0)
            throw new ArgumentException("Study must contain complete 9-measurement blocks.");
        var blocks = observations.Count / 9;
        if (blocks is < 2 or > MaxBlocks)
            throw new ArgumentException("At least two complete bounded study blocks are required.");

        for (int b = 0; b < blocks; b++)
        {
            var expected = Schedule(b);
            var items = observations.Where(o => o.Block == b).ToArray();
            if (items.Length != expected.Count)
                throw new ArgumentException("Study block is incomplete.");
            for (int position = 0; position < expected.Count; position++)
            {
                var o = items.SingleOrDefault(x => x.Position == position);
                if (o is null || o.Kind != expected[position].Kind ||
                    o.PollIntervalMs != expected[position].IntervalMs ||
                    o.FinishedAt < o.StartedAt ||
                    !o.Measurement.ExecutedOnGpu || !o.Measurement.Correct ||
                    o.Measurement.Mismatches != 0 || o.Measurement.Score <= 0 ||
                    !double.IsFinite(o.Measurement.Score))
                    throw new ArgumentException("Study schedule, timestamps or GPU correctness invalid.");
            }
        }

        // First entire block is unscored warmup for each kind + interval.
        // Retain it in raw output for analysis and correctness evidence.
        var scored = observations.Where(o => o.Block > 0).ToArray();
        var conditions = new List<StudyConditionSummary>();
        var paired = new List<StudyPairSummary>();
        static double? MeanOrNull(IEnumerable<double?> values)
        {
            var x = values.Where(v => v.HasValue && double.IsFinite(v.Value))
                .Select(v => v!.Value).ToArray();
            return x.Length == 0 ? null : x.Average();
        }
        foreach (var kind in Enum.GetValues<WorkloadKind>())
        {
            foreach (var interval in IntervalsMs)
            {
                var all = observations.Where(x => x.Kind == kind &&
                    x.PollIntervalMs == interval).ToArray();
                var values = scored.Where(x => x.Kind == kind &&
                    x.PollIntervalMs == interval).OrderBy(x => x.Block).ToArray();
                var stats = values.Length >= 1
                    ? MeasurementAnalysis.Describe(values.Select(x => x.Measurement.Score))
                    : null;
                double? drift = null;
                if (values.Length >= 4)
                {
                    var middle = values.Length / 2;
                    var early = values.Take(middle).Average(x => x.Measurement.Score);
                    var later = values.Skip(middle).Average(x => x.Measurement.Score);
                    drift = 100.0 * (later / early - 1.0);
                }
                conditions.Add(new StudyConditionSummary(
                    kind,
                    interval,
                    all.Length,
                    values.Length,
                    stats,
                    drift,
                    values.Sum(x => x.Measurement.ElapsedMilliseconds) / 1000.0,
                    values.Sum(x => x.Measurement.TelemetryEvidence?.SampleCount ?? 0),
                    values.Sum(x => x.Measurement.TelemetryEvidence?.Diagnostics?.DistinctSamplesWithinHostTimedWindow ?? 0),
                    values.Sum(x => x.Measurement.TelemetryEvidence?.ActiveSamples ?? 0),
                    all.Select(x => x.Measurement.TelemetryEvidence?.PeakHotspotC)
                       .Where(x => x is > 0).Select(x => x!.Value)
                       .DefaultIfEmpty(double.NaN).Max() is var hot && double.IsFinite(hot) ? hot : null,
                    MeanOrNull(values.Where(x => x.Measurement.TelemetryEvidence?.HasUsefulLoadEvidence == true)
                        .Select(x => x.Measurement.TelemetryEvidence?.MeanActiveBoardPowerW))));
            }

            foreach (var (tested, reference) in new[] { (250, 125), (500, 125), (500, 250) })
            {
                var diffs = new List<double>();
                for (int b = 1; b < blocks; b++)
                {
                    var relevant = scored.Where(x => x.Block == b && x.Kind == kind).ToArray();
                    var a = relevant.Single(x => x.PollIntervalMs == reference).Measurement.Score;
                    var v = relevant.Single(x => x.PollIntervalMs == tested).Measurement.Score;
                    diffs.Add(100.0 * (v / a - 1.0));
                }
                paired.Add(new StudyPairSummary(kind, tested, reference, diffs.Count,
                    diffs.Count > 0 ? MeasurementAnalysis.Describe(diffs) : null));
            }
        }

        var limitations = new List<string>
        {
            "Repeated GPU correctness checks with CPU-verification gaps: not uninterrupted gaming or thermal stress.",
            "Synthetic host-side dispatch/readback time, not calibrated GPU command-queue timestamp time.",
            "ADLX metrics sampled asynchronously and may be averaged/delayed; in-window host observations are not device-time synchronized.",
            "Observations from one session may be correlated; no independent-session confidence interval or proof of polling interference.",
            "GPU tuning settings were not modified; no automatic tuning candidates, stability approval or performance-per-watt verdict."
        };
        return new StudySummary(
            blocks,
            1,
            observations.Count,
            observations.Sum(x => x.Measurement.ElapsedMilliseconds) / 1000,
            observations.Sum(x => x.Measurement.TelemetryEvidence?.SampleCount ?? 0),
            observations.Sum(x => x.Measurement.TelemetryEvidence?.Diagnostics?.DistinctSamplesWithinHostTimedWindow ?? 0),
            observations.Sum(x => x.Measurement.TelemetryEvidence?.ActiveSamples ?? 0),
            observations.Select(x => x.Measurement.TelemetryEvidence?.PeakHotspotC)
                .Where(x => x is > 0).Select(x => x!.Value)
                .DefaultIfEmpty(double.NaN).Max() is var peak && double.IsFinite(peak) ? peak : null,
            conditions,
            paired,
            limitations);
    }
}
