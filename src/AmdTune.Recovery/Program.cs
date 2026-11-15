using System.Diagnostics;
using System.Text.Json;
using AmdTune.Core;
using AmdTune.Infrastructure;

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
    var bridge = new AdlxBridgeGpuDevice(new BridgeProcess(lease.BridgeExecutable));

    // Signal readiness only when lease contents and bridge location have been validated.
    await File.WriteAllTextAsync(leaseFile + ".ready", "ready");
    Log("Guard armed, monitoring pid " + lease.ParentProcessId);
    var monitor = new RecoveryMonitor(bridge,
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
