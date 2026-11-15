namespace AmdTune.Core;

/// <summary>Conservative non-bypassable policy; driver ranges are additional constraints.</summary>
public sealed class TunePolicy(SafetyEnvelope envelope)
{
    public void Validate(DeviceCapabilities caps, TuningSettings original, TunePlan plan)
    {
        if (!caps.CanReadSettings || !caps.CanWriteSettings || caps.VoltageOffsetMv is null ||
            caps.MemoryClockMhz is null)
            throw new SafetyException("Readback, write support, and driver ranges are required.");
        if (plan.Candidates is null || plan.Candidates.Count is < 1 or > 12 ||
            plan.Candidates.Count > envelope.MaxCandidates)
            throw new SafetyException("Candidate count is outside the safety envelope.");
        if (plan.Repetitions < 3 || plan.Repetitions > envelope.MaxRepetitions)
            throw new SafetyException("Use between 3 and 9 repetitions.");
        if (plan.MaxHotspotC < 40 || plan.MaxHotspotC > envelope.AbsoluteHotspotLimitC)
            throw new SafetyException("Requested temperature guard is outside the safety envelope.");

        foreach (var settings in plan.Candidates)
        {
            if (!caps.VoltageOffsetMv.Contains(settings.VoltageOffsetMv))
                throw new SafetyException($"Driver does not allow voltage offset {settings.VoltageOffsetMv} mV.");
            if (!caps.MemoryClockMhz.Contains(settings.MemoryClockMhz))
                throw new SafetyException($"Driver does not allow memory frequency {settings.MemoryClockMhz} MHz.");
            if (settings.VoltageOffsetMv < original.VoltageOffsetMv - envelope.MaxUndervoltDeltaMv ||
                settings.VoltageOffsetMv > original.VoltageOffsetMv)
                throw new SafetyException("Candidate voltage exceeds conservative relative bounds.");
            if (settings.MemoryClockMhz > original.MemoryClockMhz + envelope.MaxMemoryClockIncreaseMhz ||
                settings.MemoryClockMhz < original.MemoryClockMhz)
                throw new SafetyException("Candidate VRAM exceeds conservative relative bounds.");
            if (!caps.CanSetMemoryTiming && settings.MemoryTiming != original.MemoryTiming)
                throw new SafetyException("Memory timing changes are unsupported on this device.");
        }
    }

    public static void RequireReadback(TuningSettings requested, TuningSettings observed)
    {
        if (requested != observed)
            throw new HardwareException($"Readback mismatch. Requested {requested}; observed {observed}.");
    }

    public static void RequireTelemetry(GpuTelemetry telemetry, double maxHotspotC)
    {
        if (telemetry.HotspotC is null || telemetry.HotspotC <= 0)
            throw new SafetyException("Hotspot telemetry unavailable. Fail closed.");
        if (telemetry.HotspotC >= maxHotspotC)
            throw new SafetyException($"Hotspot {telemetry.HotspotC:F1} C reached limit {maxHotspotC:F1} C.");
        if (DateTimeOffset.UtcNow - telemetry.At > TimeSpan.FromSeconds(15))
            throw new SafetyException("Telemetry is stale. Fail closed.");
    }

    public static TrialVerdict Decide(IReadOnlyList<WorkloadMeasurement> measurements)
    {
        if (measurements.Count == 0 || measurements.Any(m => m.Score <= 0 || m.CheckedElements <= 0 || !m.ExecutedOnGpu))
            return TrialVerdict.Inconclusive;
        if (measurements.Any(m => !m.Correct || m.Mismatches > 0))
            return TrialVerdict.Rejected;
        return TrialVerdict.Accepted;
    }
}
