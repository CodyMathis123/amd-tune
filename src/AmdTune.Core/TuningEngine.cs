namespace AmdTune.Core;

/// <summary>
/// Runs a non-persistent, baseline-restored tuning experiment. A verdict of Accepted means
/// only that the specified workloads passed, never that gaming stability is guaranteed.
/// </summary>
public sealed class TuningEngine(
    IGpuDevice device,
    IWorkloadRunner workload,
    IAuditStore audit,
    IRollbackGuard guard,
    TunePolicy policy)
{
    private readonly SemaphoreSlim _exclusive = new(1, 1);

    public async Task<RunReport> RunAsync(
        TunePlan plan,
        Action<RunProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _exclusive.WaitAsync(0, cancellationToken))
            throw new SafetyException("Another tuning session is already running.");

        var started = DateTimeOffset.UtcNow;
        var trialResults = new List<TrialResult>();
        var phase = RunPhase.Discovering;
        TuningSettings? original = null;
        DeviceCapabilities? capabilities = null;
        var rollbackVerified = false;
        string? error = null;
        var guardArmed = false;

        void Emit(RunPhase state, string message) =>
            progress?.Invoke(new RunProgress(state, message, trialResults.Count,
                plan.Candidates?.Count ?? 0, DateTimeOffset.UtcNow));

        try
        {
            Emit(RunPhase.Discovering, "Discovering device capabilities and current driver state");
            capabilities = await device.DiscoverAsync(cancellationToken);
            original = await device.ReadSettingsAsync(cancellationToken);
            policy.Validate(capabilities, original, plan);
            if (!capabilities.IsSimulation && !guard.IsIndependent)
                throw new SafetyException("Live writes require an independent recovery process.");

            Emit(RunPhase.Baseline, "Checking original settings and temperature");
            TunePolicy.RequireTelemetry(await device.ReadTelemetryAsync(cancellationToken), plan.MaxHotspotC);

            // Arm before the first write; restore even on cancellation, failed validation or exceptions.
            await guard.ArmAsync(original, cancellationToken);
            guardArmed = true;

            var first = true;
            foreach (var candidate in plan.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Emit(RunPhase.Applying, $"Applying candidate {trialResults.Count + 1} of {plan.Candidates.Count}");
                await guard.PulseAsync(cancellationToken);
                TunePolicy.RequireReadback(original,
                    await device.ReadSettingsAsync(cancellationToken));

                await device.ApplyAsync(candidate, cancellationToken);
                Emit(RunPhase.Verifying, "Verifying actual driver settings");
                TunePolicy.RequireReadback(candidate,
                    await device.ReadSettingsAsync(cancellationToken));

                var readings = new List<WorkloadMeasurement>();
                for (var repetition = 0; repetition < plan.Repetitions; repetition++)
                {
                    foreach (var kind in Enum.GetValues<WorkloadKind>())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await guard.PulseAsync(cancellationToken);
                        TunePolicy.RequireTelemetry(
                            await device.ReadTelemetryAsync(cancellationToken), plan.MaxHotspotC);
                        Emit(RunPhase.Benchmarking, $"Running {kind} pass {repetition + 1}/{plan.Repetitions}");
                        var result = await workload.RunAsync(kind, plan.Seed + repetition, cancellationToken);
                        readings.Add(result);
                        if (!result.Correct || result.Mismatches != 0)
                            throw new SafetyException($"Correctness check failed on {kind}: {result.Detail}");
                    }
                }

                var reading = await device.ReadTelemetryAsync(cancellationToken);
                TunePolicy.RequireTelemetry(reading, plan.MaxHotspotC);
                var verdict = TunePolicy.Decide(readings);
                trialResults.Add(new TrialResult(candidate, verdict,
                    verdict == TrialVerdict.Accepted ? "Specified workloads passed; long-term stability unproven."
                    : "Result inconclusive; not safe to persist.", readings, reading, DateTimeOffset.UtcNow));

                Emit(RunPhase.RollingBack, "Restoring original driver settings");
                // No candidate is left applied across trials.
                await RestoreAndVerify(original);
                first = false;
            }

            phase = RunPhase.Completed;
            Emit(phase, "Experiments complete; original settings restored");
        }
        catch (OperationCanceledException)
        {
            phase = RunPhase.Cancelled;
            error = "Cancelled by operator; attempting rollback.";
            Emit(phase, error);
        }
        catch (Exception ex)
        {
            phase = RunPhase.Failed;
            error = ex.GetType().Name + ": " + ex.Message;
            Emit(phase, error);
        }
        finally
        {
            if (original is not null)
            {
                try
                {
                    Emit(RunPhase.RollingBack, "Final rollback verification");
                    using var restoreTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                    await device.ApplyAsync(original, restoreTimeout.Token);
                    TunePolicy.RequireReadback(original,
                        await device.ReadSettingsAsync(restoreTimeout.Token));
                    rollbackVerified = true;
                }
                catch (Exception restoreFailure)
                {
                    phase = RunPhase.Failed;
                    error = (error is null ? "" : error + " | ") +
                        "ROLLBACK NOT VERIFIED: " + restoreFailure.Message;
                    // Do not disarm an independent watchdog if rollback failed.
                }
            }
            if (guardArmed && rollbackVerified)
            {
                try { await guard.DisarmAsync(CancellationToken.None); }
                catch (Exception ex)
                {
                    phase = RunPhase.Failed;
                    error = "Recovery guard could not be disarmed: " + ex.Message;
                }
            }
            await guard.DisposeAsync();
            _exclusive.Release();
        }

        var report = new RunReport(Guid.NewGuid(),
            capabilities?.Name ?? "Unavailable",
            capabilities?.Backend ?? "Unavailable",
            capabilities?.IsSimulation ?? false,
            started, DateTimeOffset.UtcNow, phase,
            original ?? new TuningSettings(0, 0, MemoryTimingMode.Default),
            trialResults, phase == RunPhase.Completed
                ? "Only tested workloads passed. No long-term stability claim."
                : "No settings approved.",
            rollbackVerified, error);
        try { await audit.SaveAsync(report, CancellationToken.None); }
        catch (Exception ex) { throw new IOException("Could not persist audit report.", ex); }
        return report;

        async Task RestoreAndVerify(TuningSettings baseline)
        {
            using var restoreTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await device.ApplyAsync(baseline, restoreTimeout.Token);
            TunePolicy.RequireReadback(baseline, await device.ReadSettingsAsync(restoreTimeout.Token));
        }
    }
}
