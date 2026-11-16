namespace AmdTune.Core;

/// <summary>
/// An evidence gate, NOT a statement that a tuning profile is stable.
/// Measurements from a single A/B/A session cannot establish reproducibility.
/// </summary>
public sealed record WorkloadQuality(
    WorkloadKind Kind,
    int RepetitionsPerPhase,
    double MinimumHostTimedMilliseconds,
    double MaximumCoefficientOfVariationPercent,
    double StockReferenceDriftPercent,
    int ActiveTelemetrySamples,
    bool ComparisonUsable,
    IReadOnlyList<string> Limitations);

public sealed record TrialEvidence(
    bool AllGpuResultsCorrect,
    bool HardwareRollbackTested,
    bool SustainedStabilityDemonstrated,
    bool SuitableForAutomaticTuning,
    double CandidateMeasuredSeconds,
    IReadOnlyList<WorkloadQuality> Workloads,
    IReadOnlyList<string> RemainingRequirements);

public static class EvidenceAssessment
{
    // These are *quality control targets*, not claims of statistical significance.
    // No confidence intervals are emitted for sequential, correlated A/B/A runs.
    public static TrialEvidence Evaluate(
        IReadOnlyList<WorkloadMeasurement> before,
        IReadOnlyList<WorkloadMeasurement> candidate,
        IReadOnlyList<WorkloadMeasurement> after,
        IReadOnlyList<KindComparison> comparisons)
    {
        var quality = new List<WorkloadQuality>();
        foreach (var comparison in comparisons)
        {
            var a = before.Where(x => x.Kind == comparison.Kind).ToArray();
            var b = candidate.Where(x => x.Kind == comparison.Kind).ToArray();
            var c = after.Where(x => x.Kind == comparison.Kind).ToArray();
            var measurements = a.Concat(b).Concat(c).ToArray();
            var limits = new List<string>();
            if (a.Length < 7 || b.Length < 7 || c.Length < 7)
                limits.Add("Fewer than 7 samples in each A/B/A phase.");
            var minDuration = measurements.Length == 0 ? 0 : measurements.Min(x => x.ElapsedMilliseconds);
            if (minDuration < 250)
                limits.Add("Host-timed workload windows shorter than 250 ms; launch/transfer overhead may dominate.");
            var maxCv = Math.Max(comparison.Before.CoefficientOfVariationPercent,
                Math.Max(comparison.Candidate.CoefficientOfVariationPercent,
                         comparison.After.CoefficientOfVariationPercent));
            if (maxCv > 5)
                limits.Add($"Within-phase variation {maxCv:F1}% exceeds 5% quality target.");
            if (Math.Abs(comparison.ReferenceDriftPercent) > 3)
                limits.Add($"Stock-to-stock drift {comparison.ReferenceDriftPercent:F1}% exceeds 3% quality target.");
            var loaded = measurements.Sum(x => x.TelemetryEvidence?.ActiveSamples ?? 0);
            if (a.All(x => x.TelemetryEvidence?.HasUsefulLoadEvidence != true) ||
                b.All(x => x.TelemetryEvidence?.HasUsefulLoadEvidence != true) ||
                c.All(x => x.TelemetryEvidence?.HasUsefulLoadEvidence != true))
                limits.Add("Insufficient synchronized loaded-activity ADLX evidence across A/B/A phases.");
            quality.Add(new WorkloadQuality(
                comparison.Kind, Math.Min(a.Length, Math.Min(b.Length, c.Length)),
                minDuration, maxCv, comparison.ReferenceDriftPercent, loaded,
                limits.Count == 0, limits));
        }

        var correct = candidate.Count > 0 &&
            candidate.All(x => x.ExecutedOnGpu && x.Correct && x.Mismatches == 0);
        var remaining = new List<string>();
        if (!correct) remaining.Add("Real GPU output correctness checks have not all passed.");
        if (quality.Any(q => !q.ComparisonUsable))
            remaining.Add("Benchmark duration, variation, stock drift, or loaded telemetry quality is insufficient.");
        remaining.Add("GPU command-queue timestamp timing is not integrated; host-side timing is only a proxy.");
        remaining.Add("Multiple independent cold/warm sessions and sustained gaming tests have not been validated.");
        remaining.Add("Crash-termination rollback on physical hardware has not been validated.");

        return new TrialEvidence(
            correct,
            HardwareRollbackTested: false,
            SustainedStabilityDemonstrated: false,
            SuitableForAutomaticTuning: false,
            CandidateMeasuredSeconds: candidate.Sum(x => x.ElapsedMilliseconds) / 1000,
            quality,
            remaining);
    }
}
