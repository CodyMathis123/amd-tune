using AmdTune.Core;

namespace AmdTune.Infrastructure;

public enum RecoveryOutcome { Disarmed, Restored, Failed }

/// <summary>
/// Independent watchdog decision loop with injectable clocks, liveness, storage and GPU.
/// Can be exercised in CI without hardware, driver crashes or OS process termination.
/// </summary>
public sealed class RecoveryMonitor(
    IGpuDevice device,
    Func<bool> parentAlive,
    Func<bool> isDisarmed,
    Func<TimeSpan> heartbeatAge,
    Func<TimeSpan, CancellationToken, Task> pause,
    Action<string> log)
{
    public async Task<RecoveryOutcome> RunAsync(TuningSettings baseline, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (isDisarmed())
            {
                log("Guard disarmed after session confirmed original settings.");
                return RecoveryOutcome.Disarmed;
            }

            var live = parentAlive();
            var stale = heartbeatAge() > TimeSpan.FromSeconds(20);
            if (!live || stale)
            {
                log($"Recovery triggered. parentAlive={live}, stale={stale}");
                for (var attempt = 1; attempt <= 5; attempt++)
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        await device.ApplyAsync(baseline, timeout.Token);
                        TunePolicy.RequireReadback(baseline,
                            await device.ReadSettingsAsync(timeout.Token));
                        log("RECOVERY VERIFIED: original driver settings restored and read back.");
                        return RecoveryOutcome.Restored;
                    }
                    catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        log($"Recovery attempt {attempt} failed: {error.Message}");
                        if (attempt < 5) await pause(TimeSpan.FromSeconds(1), cancellationToken);
                    }
                }
                log("RECOVERY FAILED. Manually reset AMD Adrenalin tuning to defaults.");
                return RecoveryOutcome.Failed;
            }
            await pause(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
    }
}
