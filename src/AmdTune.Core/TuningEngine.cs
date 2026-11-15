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
        var sessionId = Guid.NewGuid();
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
            await audit.AppendEventAsync(sessionId, new AuditEvent(DateTimeOffset.UtcNow,
                "baseline", "Original settings observed before any write.", original), cancellationToken);
            policy.Validate(capabilities, original, plan);
            if (!capabilities.IsSimulation && !guard.IsIndependent)
                throw new SafetyException("Live writes require an independent recovery process.");

            Emit(RunPhase.Baseline, "Checking original settings and temperature");
            TunePolicy.RequireTelemetry(await device.ReadTelemetryAsync(cancellationToken), plan.MaxHotspotC);

            // Arm before the first write; restore even on cancellation, failed validation or exceptions.
            await guard.ArmAsync(original, cancellationToken);
            guardArmed = true;
            await audit.AppendEventAsync(sessionId, new AuditEvent(DateTimeOffset.UtcNow,
                "recovery", "Independent guard armed; baseline recoverable.", original), cancellationToken);

            foreach (var candidate in plan.Candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await guard.PulseAsync(cancellationToken);
                TunePolicy.RequireReadback(original, await device.ReadSettingsAsync(cancellationToken));

                // A/B/A minimizes false wins from the GPU heating up or system load drifting.
                Emit(RunPhase.Baseline, "A: measuring current stock configuration");
                var before = await ExecutePasses(cancellationToken);

                Emit(RunPhase.Applying, $"B: applying candidate {trialResults.Count + 1}/{plan.Candidates.Count}");
                await audit.AppendEventAsync(sessionId, new AuditEvent(DateTimeOffset.UtcNow,
                    "candidate", "Attempting candidate write. Recovery guard must be alive.", candidate), cancellationToken);
                await device.ApplyAsync(candidate, cancellationToken);
                Emit(RunPhase.Verifying, "Verifying requested values against driver readback");
                TunePolicy.RequireReadback(candidate, await device.ReadSettingsAsync(cancellationToken));
                await audit.AppendEventAsync(sessionId, new AuditEvent(DateTimeOffset.UtcNow,
                    "readback", "Requested settings confirmed by ADLX.", candidate), cancellationToken);
                var candidateResults = await ExecutePasses(cancellationToken);
                var reading = await device.ReadTelemetryAsync(cancellationToken);
                TunePolicy.RequireTelemetry(reading, plan.MaxHotspotC);

                Emit(RunPhase.RollingBack, "Restoring original settings before reference repeat");
                await RestoreAndVerify(original);
                await audit.AppendEventAsync(sessionId, new AuditEvent(DateTimeOffset.UtcNow,
                    "rollback", "Original driver state restored and read back.", original), cancellationToken);
                Emit(RunPhase.Baseline, "A: repeating stock reference to quantify drift");
                var after = await ExecutePasses(cancellationToken);
                var comparisons = MeasurementAnalysis.Compare(before, candidateResults, after);
                var verdict = TunePolicy.Decide(candidateResults);
                var reason = verdict == TrialVerdict.Accepted
                    ? "All verified GPU workloads passed; long-term gaming stability not established."
                    : "Result is synthetic, incorrect or otherwise inconclusive.";
                if (verdict == TrialVerdict.Accepted)
                {
                    if (comparisons.Any(x => Math.Abs(x.ReferenceDriftPercent) > 5 ||
                            x.Before.CoefficientOfVariationPercent > 5 ||
                            x.Candidate.CoefficientOfVariationPercent > 5 ||
                            x.After.CoefficientOfVariationPercent > 5))
                    {
                        verdict = TrialVerdict.Inconclusive;
                        reason = "Excessive reference drift or benchmark noise. Do not infer performance gain.";
                    }
                    else if (comparisons.Any(x => x.PercentChange < -3))
                    {
                        verdict = TrialVerdict.Rejected;
                        reason = "Measured performance regression; cause unknown (not proven ECC).";
                    }
                }
                trialResults.Add(new TrialResult(candidate, verdict, reason,
                    candidateResults, reading, DateTimeOffset.UtcNow, comparisons));
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
            if (original is not null && guardArmed)
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
                try
                {
                    await audit.AppendEventAsync(sessionId, new AuditEvent(DateTimeOffset.UtcNow,
                        "final-rollback", "Original settings independently verified after session.", original!), CancellationToken.None);
                }
                catch (Exception journalError)
                {
                    phase = RunPhase.Failed;
                    error = (error is null ? "" : error + " | ") + "Audit journal failed: " + journalError.Message;
                }
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

        var report = new RunReport(sessionId,
            capabilities?.Name ?? "Unavailable",
            capabilities?.Backend ?? "Unavailable",
            capabilities?.IsSimulation ?? false,
            started, DateTimeOffset.UtcNow, phase,
            original ?? new TuningSettings(0, 0, MemoryTimingMode.Default),
            trialResults, phase == RunPhase.Completed
                ? (capabilities?.IsSimulation == true
                    ? "SIMULATION ONLY: no real GPU stability or performance verified."
                    : "Only specified workloads passed. No long-term stability claim.")
                : "No settings approved.",
            rollbackVerified, error);
        try { await audit.SaveAsync(report, CancellationToken.None); }
        catch (Exception ex) { throw new IOException("Could not persist audit report.", ex); }
        return report;

        async Task<IReadOnlyList<WorkloadMeasurement>> ExecutePasses(CancellationToken ct)
        {
            var measurements = new List<WorkloadMeasurement>();
            for (var repetition = 0; repetition < plan.Repetitions; repetition++)
            {
                foreach (var kind in Enum.GetValues<WorkloadKind>())
                {
                    ct.ThrowIfCancellationRequested();
                    await guard.PulseAsync(ct);
                    TunePolicy.RequireTelemetry(await device.ReadTelemetryAsync(ct), plan.MaxHotspotC);
                    Emit(RunPhase.Benchmarking, $"{kind} pass {repetition + 1}/{plan.Repetitions}");
                    var value = await workload.RunAsync(kind, plan.Seed + repetition, ct);
                    measurements.Add(value);
                    if (!value.Correct || value.Mismatches > 0)
                        throw new SafetyException($"GPU correctness failed for {kind}: {value.Detail}");
                    if (capabilities is { IsSimulation: false } && !value.ExecutedOnGpu)
                        throw new SafetyException("Hardware experiment received a non-GPU workload result.");
                }
            }
            return measurements;
        }

        async Task RestoreAndVerify(TuningSettings baseline)
        {
            using var restoreTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await device.ApplyAsync(baseline, restoreTimeout.Token);
            TunePolicy.RequireReadback(baseline, await device.ReadSettingsAsync(restoreTimeout.Token));
        }
    }
}
