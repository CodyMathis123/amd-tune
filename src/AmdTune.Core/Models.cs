namespace AmdTune.Core;

public enum MemoryTimingMode { Default, Fast }
public enum WorkloadKind { Compute, Memory, Mixed }
public enum RunPhase { Idle, Discovering, Baseline, Applying, Verifying, Benchmarking, RollingBack, Completed, Failed, Cancelled }
public enum TrialVerdict { Accepted, Rejected, Inconclusive }

public sealed record IntRange(int Min, int Max, int Step = 1)
{
    public bool Contains(int value) => Step > 0 && value >= Min && value <= Max && (value - Min) % Step == 0;
}

public sealed record DeviceCapabilities(
    string DeviceId,
    string Name,
    string Backend,
    bool IsSimulation,
    bool CanReadSettings,
    bool CanWriteSettings,
    IntRange? VoltageOffsetMv,
    IntRange? MemoryClockMhz,
    bool CanSetMemoryTiming,
    IReadOnlyList<string> Warnings);

public sealed record TuningSettings(int VoltageOffsetMv, int MemoryClockMhz, MemoryTimingMode MemoryTiming);
public sealed record GpuTelemetry(
    DateTimeOffset At,
    double? HotspotC,
    double? BoardPowerW,
    int? CoreClockMhz,
    int? MemoryClockMhz,
    double? UtilizationPercent = null,
    long? MetricTimestampMs = null);

public sealed record WorkloadMeasurement(
    WorkloadKind Kind,
    bool ExecutedOnGpu,
    bool Correct,
    long CheckedElements,
    long Mismatches,
    double Score,
    string ScoreUnit,
    double ElapsedMilliseconds,
    string Detail,
    WorkloadTelemetryEvidence? TelemetryEvidence = null,
    HostTimedWindow? HostTimedWindow = null);

/// <summary>
/// Host clock timestamps surrounding GPU dispatch submission and buffer readback;
/// this window is NOT calibrated GPU execution time or device command timestamps.
/// </summary>
public sealed record HostTimedWindow(DateTimeOffset StartedAt, DateTimeOffset FinishedAt)
{
    public bool Contains(DateTimeOffset capturedAt) =>
        capturedAt >= StartedAt && capturedAt <= FinishedAt;
}


/// <summary>
/// ADLX snapshots around a workload. Sampling is asynchronous and NOT aligned
/// with the GPU's execution timeline; missing active-load samples remain unknown.
/// </summary>
public sealed record WorkloadTelemetryEvidence(
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int SampleCount,
    int ActiveSamples,
    double? PeakHotspotC,
    double? MaxBoardPowerW,
    double? MeanActiveBoardPowerW,
    double? MeanActiveUtilizationPercent,
    double? MeanActiveCoreClockMhz,
    IReadOnlyList<GpuTelemetry> Samples,
    TelemetrySampleDiagnostics? Diagnostics = null,
    HostTimedWindow? HostTimedWindow = null)
{
    public bool HasUsefulLoadEvidence => ActiveSamples >= 3 &&
        MeanActiveBoardPowerW is > 0 && MeanActiveCoreClockMhz is > 0;
}

/// <summary>
/// Diagnostics for actual AMD metric publication cadence and observed utilization.
/// Re-reading the same ADLX timestamp is not an independent sensor sample.
/// </summary>
public sealed record TelemetrySampleDiagnostics(
    int RawSamples,
    int DistinctMetricSamples,
    int RepeatedMetricSamples,
    bool NativeMetricTimestampsAvailable,
    double? MinimumUtilizationPercent,
    double? MaximumUtilizationPercent,
    double? MeanUtilizationPercent,
    double? MeanObservedMetricIntervalMs,
    int PositiveClockSamples,
    int PositiveUtilizationSamples,
    int SamplesWithinHostTimedWindow = 0,
    int DistinctSamplesWithinHostTimedWindow = 0,
    bool HasHostTimedWindow = false);

public sealed record TrialResult(
    TuningSettings Settings,
    TrialVerdict Verdict,
    string Reason,
    IReadOnlyList<WorkloadMeasurement> Measurements,
    GpuTelemetry? Telemetry,
    DateTimeOffset At,
    IReadOnlyList<KindComparison>? Comparisons = null,
    TrialEvidence? Evidence = null);

public sealed record AuditEvent(DateTimeOffset At, string Kind, string Detail, TuningSettings? Settings = null);

public sealed record RunProgress(
    RunPhase Phase,
    string Message,
    int CompletedTrials,
    int TotalTrials,
    DateTimeOffset At);

public sealed record RunReport(
    Guid Id,
    string DeviceName,
    string Backend,
    bool Simulated,
    DateTimeOffset Started,
    DateTimeOffset Finished,
    RunPhase Phase,
    TuningSettings OriginalSettings,
    IReadOnlyList<TrialResult> Trials,
    string Verdict,
    bool RollbackVerified,
    string? Error);

public sealed record TunePlan(
    IReadOnlyList<TuningSettings> Candidates,
    int Repetitions = 3,
    double MaxHotspotC = 90,
    int Seed = 49191)
{
    public static TunePlan Conservative(TuningSettings original)
        => new(new[]
        {
            original with { VoltageOffsetMv = original.VoltageOffsetMv - 15 },
            original with { VoltageOffsetMv = original.VoltageOffsetMv - 30 },
            original with { VoltageOffsetMv = original.VoltageOffsetMv - 45 }
        });
}

public sealed record SafetyEnvelope(int MaxUndervoltDeltaMv = 75, int MaxMemoryClockIncreaseMhz = 150, int MaxCandidates = 12, int MaxRepetitions = 9, double AbsoluteHotspotLimitC = 94);
