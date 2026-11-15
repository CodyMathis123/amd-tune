namespace AmdTune.Core;

/// <summary>Human-readable trial interpretation, distinct from successful experiment completion.</summary>
public static class RunReportSummary
{
    public static string Describe(RunPhase phase, bool simulated,
        IReadOnlyList<TrialResult> trials, bool rollbackVerified)
    {
        if (phase != RunPhase.Completed)
            return "Experiment did not complete. No tuning settings approved.";
        if (!rollbackVerified)
            return "Experiment finished but original settings were NOT verified restored. No settings approved.";
        if (simulated)
            return "SIMULATION ONLY: no real GPU stability or performance verified.";
        if (trials.Count == 0)
            return "No completed candidate trials. No settings approved.";
        if (trials.Any(t => t.Verdict == TrialVerdict.Rejected))
            return "One or more candidate trials rejected; original settings restored. No tuning profile applied.";
        if (trials.Any(t => t.Verdict == TrialVerdict.Inconclusive))
            return "Performance INCONCLUSIVE due to benchmark noise or drift; original settings restored. " +
                   "No tuning benefit established and long-term stability not tested.";
        return "Specified GPU checks passed and original settings restored. Long-term gaming stability, " +
               "efficiency gain and practical performance benefit remain unverified.";
    }
}
