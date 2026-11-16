using System.Diagnostics;
using System.Text.Json;
using AmdTune.Core;
using AmdTune.Infrastructure;

// Runtime self-check never accesses ADLX, opens a lease, or writes GPU settings.
if (args.Length == 1 && args[0] == "--self-check")
{
    Console.WriteLine("RECOVERY_RUNTIME_OK");
    return 0;
}

// A standalone process: parent process crashes must not prevent rollback.
// Operates only on an explicitly written local lease. Does NOT apply tuning at startup.
if (args.Length != 2 || args[0] != "--lease")
{
    Console.Error.WriteLine("Usage: AmdTune.Recovery.exe --lease <absolute-path>");
    return 2;
}
var leaseFile = Path.GetFullPath(args[1]);
var logFile = leaseFile + ".recovery.log";

void Log(string message)
{
    File.AppendAllText(logFile, DateTimeOffset.UtcNow.ToString("O") + " " + message + Environment.NewLine);
}

try
{
    var lease = JsonSerializer.Deserialize<RecoveryLease>(await File.ReadAllTextAsync(leaseFile))
        ?? throw new InvalidDataException("Malformed recovery lease.");
    if (!Path.IsPathFullyQualified(lease.BridgeExecutable) || !File.Exists(lease.BridgeExecutable))
        throw new InvalidDataException("Recovery lease has no usable bridge.");
    // Diagnostic leases use the real watchdog lifecycle but prohibit AMD tuning
    // operations, even if the parent exits during a no-write smoke test.
    IGpuDevice device = lease.DiagnosticOnly
        ? new DiagnosticNoWriteGpuDevice()
        : new AdlxBridgeGpuDevice(new BridgeProcess(lease.BridgeExecutable));

    // Signal readiness only when lease contents and bridge location have been validated.
    await File.WriteAllTextAsync(leaseFile + ".ready", "ready");
    Log("Guard armed, monitoring pid " + lease.ParentProcessId + (lease.DiagnosticOnly ? " (DIAGNOSTIC ONLY, no GPU writes)" : ""));
    var monitor = new RecoveryMonitor(device,
        () => IsProcessAlive(lease.ParentProcessId),
        () => File.Exists(leaseFile + ".done"),
        () => DateTime.UtcNow - File.GetLastWriteTimeUtc(leaseFile),
        (delay, ct) => Task.Delay(delay, ct),
        Log);
    var outcome = await monitor.RunAsync(lease.Baseline, CancellationToken.None);
    return outcome == RecoveryOutcome.Failed ? 3 : 0;
}
catch (Exception e)
{
    Log("FATAL GUARD ERROR: " + e);
    return 4;
}

static bool IsProcessAlive(int pid)
{
    try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
    catch (ArgumentException) { return false; }
}

file sealed class DiagnosticNoWriteGpuDevice : IGpuDevice
{
    public Task<DeviceCapabilities> DiscoverAsync(CancellationToken cancellationToken)
        => throw new SafetyException("Diagnostic watchdog forbids AMD device discovery.");
    public Task<TuningSettings> ReadSettingsAsync(CancellationToken cancellationToken)
        => throw new SafetyException("Diagnostic watchdog forbids AMD device readback.");
    public Task<GpuTelemetry> ReadTelemetryAsync(CancellationToken cancellationToken)
        => throw new SafetyException("Diagnostic watchdog forbids AMD telemetry.");
    public Task ApplyAsync(TuningSettings settings, CancellationToken cancellationToken)
        => throw new SafetyException("Diagnostic watchdog forbids hardware writes.");
}
