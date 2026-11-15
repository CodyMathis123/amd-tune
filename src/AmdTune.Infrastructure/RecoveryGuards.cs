using System.Diagnostics;
using System.Text.Json;
using AmdTune.Core;

namespace AmdTune.Infrastructure;

public sealed class SimulationRollbackGuard : IRollbackGuard
{
    public bool IsIndependent => false;
    public Task ArmAsync(TuningSettings baseline, CancellationToken ct) => Task.CompletedTask;
    public Task PulseAsync(CancellationToken ct) => Task.CompletedTask;
    public Task DisarmAsync(CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed record RecoveryLease(
    int ParentProcessId, string BridgeExecutable, TuningSettings Baseline, DateTimeOffset StartedAt);

/// <summary>
/// Coordinates an independent recovery executable. The guard survives parent process crashes.
/// Fail closed if the guard is missing or does not acknowledge its lease.
/// </summary>
public sealed class ExternalRecoveryGuard(string directory, string recoveryExecutable, BridgeProcess bridge) : IRollbackGuard
{
    private string? _lease;
    private Process? _guard;
    public bool IsIndependent => File.Exists(recoveryExecutable) && File.Exists(bridge.PathOnDisk);

    public async Task ArmAsync(TuningSettings baseline, CancellationToken ct)
    {
        if (_lease is not null) throw new SafetyException("Recovery guard is already armed.");
        if (!IsIndependent) throw new SafetyException("Both the ADLX bridge and independent recovery executable are required.");
        Directory.CreateDirectory(directory);
        var name = Guid.NewGuid().ToString("N");
        _lease = Path.Combine(directory, name + ".json");
        var lease = new RecoveryLease(Environment.ProcessId, bridge.PathOnDisk, baseline, DateTimeOffset.UtcNow);
        await WriteAtomically(_lease, JsonSerializer.Serialize(lease), ct);
        var info = new ProcessStartInfo(recoveryExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("--lease");
        info.ArgumentList.Add(_lease);
        _guard = Process.Start(info) ?? throw new SafetyException("Cannot start recovery process.");
        var ready = _lease + ".ready";
        for (var i = 0; i < 40 && !_guard.HasExited && !File.Exists(ready); i++)
            await Task.Delay(100, ct);
        if (!File.Exists(ready) || _guard.HasExited)
            throw new SafetyException("Recovery process did not acknowledge readiness.");
    }

    public Task PulseAsync(CancellationToken ct)
    {
        if (_lease is null || _guard is null || _guard.HasExited)
            throw new SafetyException("Recovery process is no longer alive.");
        // The guard uses the lease's mtime as heartbeat.
        return WriteAtomically(_lease, File.ReadAllText(_lease), ct);
    }

    public async Task DisarmAsync(CancellationToken ct)
    {
        if (_lease is null) return;
        await File.WriteAllTextAsync(_lease + ".done", "verified-restored", ct);
        if (_guard is not null)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await _guard.WaitForExitAsync(timeout.Token);
        }
    }

    public ValueTask DisposeAsync()
    {
        _guard?.Dispose(); // Never kill guard on failure; it must outlive this process.
        return ValueTask.CompletedTask;
    }

    private static async Task WriteAtomically(string path, string contents, CancellationToken ct)
    {
        var tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(tmp, contents, ct);
            File.Move(tmp, path, true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
