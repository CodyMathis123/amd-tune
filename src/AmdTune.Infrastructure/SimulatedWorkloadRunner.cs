using System.Diagnostics;
using AmdTune.Core;

namespace AmdTune.Infrastructure;

/// <summary>Deterministic CPU-side model; not a benchmark. Built for safety and orchestration tests.</summary>
public sealed class SimulatedWorkloadRunner(SimulatedGpuDevice device) : IWorkloadRunner
{
    public bool InjectCorruption { get; set; }
    public WorkloadKind? FailOnlyKind { get; set; }

    public Task<WorkloadMeasurement> RunAsync(WorkloadKind kind, int seed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var s = device.Current;
        var factor = 1 + (-s.VoltageOffsetMv * 0.0008) + (s.MemoryClockMhz - 2500) * 0.0001;
        var random = new Random(HashCode.Combine(kind, seed, 101));
        // HashCode.Combine is process-seeded; reproducibility requires a fixed arithmetic seed instead.
        random = new Random(unchecked(seed * 397 + (int)kind * 7919));
        var jitter = (random.NextDouble() - 0.5) * 0.01;
        var corrupt = InjectCorruption && (FailOnlyKind is null || FailOnlyKind == kind);
        var score = (kind == WorkloadKind.Memory ? 500 : kind == WorkloadKind.Mixed ? 150 : 100)
                    * factor * (1 + jitter);
        return Task.FromResult(new WorkloadMeasurement(kind, false, !corrupt, 131072,
            corrupt ? 10 : 0, Math.Round(score, 3), "synthetic units", 0,
            "SIMULATED: computed in C#, not on a GPU; not evidence of hardware stability."));
    }
}
