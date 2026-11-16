using AmdTune.Core;

namespace AmdTune.GpuWorkloads;

/// <summary>
/// Fixed and auditable workload intensity, not a claim of sustained game stability.
/// The extended profile avoids a single long-running shader dispatch (TDR risk).
/// </summary>
public readonly record struct GpuExecutionProfile(int UntimedWarmupPasses, int MeasuredPasses)
{
    public int TotalPasses => checked(UntimedWarmupPasses + MeasuredPasses);

    public static GpuExecutionProfile For(WorkloadKind kind, bool extended)
        => !extended ? new(0, 1) : kind switch
        {
            WorkloadKind.Compute => new(2, 16),
            WorkloadKind.Memory => new(2, 12),
            WorkloadKind.Mixed => new(2, 16),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

    public void Validate()
    {
        if (UntimedWarmupPasses < 0 || MeasuredPasses < 1 || TotalPasses > 32)
            throw new ArgumentOutOfRangeException(nameof(MeasuredPasses),
                "Workload passes must be bounded between 1 and 32.");
    }
}
