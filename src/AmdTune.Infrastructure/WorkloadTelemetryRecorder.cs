using AmdTune.Core;

namespace AmdTune.Infrastructure;

/// <summary>Connects concurrent thermal sampling to the tuning engine.</summary>
public sealed class WorkloadTelemetryRecorder(IGpuDevice device, TimeSpan? readOnlySampleInterval = null) : IWorkloadTelemetryRecorder
{
    public async Task<WorkloadMeasurement> CaptureAsync(
        Func<CancellationToken, Task<WorkloadMeasurement>> workload,
        double maxHotspotC,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var sampler = new WorkloadTelemetrySampler(device, maxHotspotC, linked, readOnlySampleInterval);
        sampler.Start();
        WorkloadMeasurement result;
        try
        {
            result = await workload(linked.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Preserve a sensor failure as a safety failure where possible.
            await sampler.StopAsync();
            throw;
        }
        var evidence = await sampler.StopAsync();
        return result with { TelemetryEvidence = evidence };
    }
}
