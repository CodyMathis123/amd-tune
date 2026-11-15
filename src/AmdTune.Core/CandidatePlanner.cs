namespace AmdTune.Core;

/// <summary>
/// Candidate generation is deterministic and uses live driver ranges.
/// It never assumes a universal GPU voltage or VRAM frequency baseline.
/// </summary>
public static class CandidatePlanner
{
    public static TunePlan ConservativeVoltageSweep(
        DeviceCapabilities capabilities,
        TuningSettings original,
        int candidateCount = 3,
        int deltaPerCandidateMv = 15,
        int repetitions = 3)
    {
        if (!capabilities.CanReadSettings || !capabilities.CanWriteSettings ||
            capabilities.VoltageOffsetMv is null || capabilities.MemoryClockMhz is null)
            throw new SafetyException("Driver capabilities are incomplete; cannot generate candidates.");
        if (candidateCount is < 1 or > 5 || deltaPerCandidateMv is < 1 or > 25)
            throw new SafetyException("Planner parameters outside conservative policy.");
        var selected = new List<TuningSettings>();
        for (int i = 1; i <= candidateCount; i++)
        {
            var target = original.VoltageOffsetMv - i * deltaPerCandidateMv;
            var range = capabilities.VoltageOffsetMv;
            if (target < range.Min) break;
            // Driver granularity is a measured capability, not a guessed 1-mV step.
            var aligned = range.Min + (int)Math.Ceiling((target - range.Min) / (double)range.Step) * range.Step;
            if (aligned >= original.VoltageOffsetMv) continue;
            if (!range.Contains(aligned)) continue;
            var candidate = original with { VoltageOffsetMv = aligned };
            if (!selected.Contains(candidate)) selected.Add(candidate);
        }
        if (selected.Count == 0)
            throw new SafetyException("No conservative candidate fits the live driver voltage range.");
        var plan = new TunePlan(selected, repetitions);
        new TunePolicy(new SafetyEnvelope()).Validate(capabilities, original, plan);
        return plan;
    }
}
