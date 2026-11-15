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
    while (!File.Exists(leaseFile + ".done"))
    {
        var parentAlive = IsProcessAlive(lease.ParentProcessId);
        var stale = DateTime.UtcNow - File.GetLastWriteTimeUtc(leaseFile) > TimeSpan.FromSeconds(20);
        if (!parentAlive || stale)
        {
            Log($"Recovery triggered. parentAlive={parentAlive}, stale={stale}");
            var recovered = false;
            for (var retry = 1; retry <= 5; retry++)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await bridge.ApplyAsync(lease.Baseline, timeout.Token);
                    var actual = await bridge.ReadSettingsAsync(timeout.Token);
                    TunePolicy.RequireReadback(lease.Baseline, actual);
                    Log("RECOVERY VERIFIED: restored initial settings.");
                    recovered = true;
                    break;
                }
                catch (Exception error)
                {
                    Log("Recovery attempt " + retry + " failed: " + error.Message);
                    await Task.Delay(1000);
                }
            }
            if (!recovered)
            {
                Log("RECOVERY FAILED. MANUALLY RESET AMD ADRENALIN TUNING TO DEFAULT.");
                return 3;
            }
            return 0;
        }
        await Task.Delay(500);
    }
    Log("Guard disarmed after parent verified rollback.");
    return 0;
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
