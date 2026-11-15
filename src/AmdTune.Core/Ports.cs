namespace AmdTune.Core;

/// <summary>All hardware-affecting operations must go through this interface.</summary>
public interface IGpuDevice
{
    Task<DeviceCapabilities> DiscoverAsync(CancellationToken cancellationToken);
    Task<TuningSettings> ReadSettingsAsync(CancellationToken cancellationToken);
    Task<GpuTelemetry> ReadTelemetryAsync(CancellationToken cancellationToken);
    Task ApplyAsync(TuningSettings settings, CancellationToken cancellationToken);
}

/// <summary>A workload must report whether it actually executed on the GPU.</summary>
public interface IWorkloadRunner
{
    Task<WorkloadMeasurement> RunAsync(WorkloadKind kind, int seed, CancellationToken cancellationToken);
}

/// <summary>
/// Optional real-hardware measurement wrapper. Its telemetry snapshots are
/// asynchronous, not synchronized to the GPU command queue.
/// </summary>
public interface IWorkloadTelemetryRecorder
{
    Task<WorkloadMeasurement> CaptureAsync(
        Func<CancellationToken, Task<WorkloadMeasurement>> workload,
        double maxHotspotC,
        CancellationToken cancellationToken);
}

/// <summary>
/// Optional read-only long-lived native monitoring stream. Does not change GPU tuning.
/// Implementations must surface telemetry errors and obey cancellation.
/// </summary>
public interface IStreamingGpuTelemetryDevice
{
    IAsyncEnumerable<GpuTelemetry> StreamTelemetryAsync(
        TimeSpan interval, CancellationToken cancellationToken);
}

/// <summary>Untrusted workloads are never allowed to persist a tune.</summary>
public interface IAuditStore
{
    Task SaveAsync(RunReport report, CancellationToken cancellationToken);
    Task AppendEventAsync(Guid sessionId, AuditEvent item, CancellationToken cancellationToken);
}

/// <summary>Required for hardware writes. Must be a separate process able to restore a snapshot.</summary>
public interface IRollbackGuard : IAsyncDisposable
{
    bool IsIndependent { get; }
    Task ArmAsync(TuningSettings baseline, CancellationToken cancellationToken);
    Task PulseAsync(CancellationToken cancellationToken);
    Task DisarmAsync(CancellationToken cancellationToken);
}

public sealed class SafetyException(string message) : Exception(message);
public sealed class HardwareException(string message) : Exception(message);
