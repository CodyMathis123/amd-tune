using System.Security.Cryptography;
using AmdTune.Core;

namespace AmdTune.Infrastructure;

/// <summary>
/// Read-only supervisor for FurMark 2. Owns and kills the FurMark process
/// on timeout, operator cancellation, sensor failure or thermal violation.
/// Does NOT apply or restore tuning changes: tuning-write orchestration
/// must remain gated on independently validated crash recovery.
/// </summary>
public sealed class FurMarkSupervisor(IGpuDevice device)
{
    public async Task<SupervisedFurMarkResult> RunAsync(
        FurMarkPlan plan, CancellationToken operatorCancellation)
    {
        plan.Validate();
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("FurMark supervision requires Windows.");
        var exe = Path.GetFullPath(plan.ExecutablePath);
        if (!File.Exists(exe)) throw new FileNotFoundException("FurMark 2 executable not found.", exe);
        var caps = await device.DiscoverAsync(operatorCancellation);
        if (caps.IsSimulation || !caps.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
            throw new SafetyException("Supervised FurMark test requires the real discovered Radeon GPU.");
        if (!caps.CanReadSettings)
            throw new SafetyException("Cannot read back GPU tuning settings.");
        var settings = await device.ReadSettingsAsync(operatorCancellation);
        var initialTelemetry = await device.ReadTelemetryAsync(operatorCancellation);
        TunePolicy.RequireTelemetry(initialTelemetry, FurMarkPlan.MaxHotspotC);
        string sha256;
        await using (var file = File.OpenRead(exe))
            sha256 = Convert.ToHexString(await SHA256.HashDataAsync(file, operatorCancellation));

        var id = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow;
        var final = (TuningSettings?)null;
        OwnedProcessResult? process = null;
        WorkloadTelemetryEvidence? evidence = null;
        ExternalLoadSummary? summary = null;
        string? failure = null;

        using var monitorCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(operatorCancellation);
        await using var sampler = new WorkloadTelemetrySampler(
            device, FurMarkPlan.MaxHotspotC, monitorCancellation,
            TimeSpan.FromMilliseconds(125), ExternalLoadAnalysis.MaxRetainedSamples);
        try
        {
            sampler.Start();
            // FurMark CLI's --max-time is the requested run duration. This
            // independent deadline also covers startup and shutdown delays.
            process = await OwnedProcessRunner.RunAsync(
                exe, plan.Arguments(), TimeSpan.FromSeconds(plan.DurationSeconds + 30),
                monitorCancellation.Token);
            if (process.TimedOut || process.Aborted)
                failure = process.FailureReason ?? "FurMark supervision aborted.";
            else if (process.ExitCode != 0)
                failure = $"FurMark exited with nonzero status {process.ExitCode}.";
            else if (process.WallSeconds < plan.DurationSeconds - 5)
                failure = "FurMark exited earlier than the configured load duration; possible launcher handoff, failure or premature termination.";
        }
        catch (Exception error)
        {
            failure = "FurMark launch or supervision failed: " + error.Message;
        }
        finally
        {
            try
            {
                evidence = await sampler.StopAsync();
                if (evidence.SampleCount < 3)
                    throw new SafetyException("Too few independent ADLX sensor readings to assess stress workload.");
                summary = ExternalLoadAnalysis.Analyze(evidence.Samples, evidence.StartedAt, evidence.FinishedAt);
            }
            catch (Exception error)
            {
                failure = "ADLX telemetry safety/collection failure: " + error.Message +
                          (failure is null ? "" : "; other error: " + failure);
            }

            try
            {
                final = await device.ReadSettingsAsync(CancellationToken.None);
                if (settings != final)
                    failure = "GPU tuning settings changed during the read-only run; " +
                              $"initial={settings}, final={final}; " + failure;
            }
            catch (Exception error)
            {
                failure = "Cannot verify original GPU tuning settings after FurMark: " +
                          error.Message + "; " + failure;
            }
        }

        var completed = failure is null && process is { Aborted: false, ExitCode: 0 }
                        && summary is not null && final == settings;
        string classification = completed
            ? summary!.HighUtilizationSampleFraction is >= 0.5
                ? "Monitored high graphics load; GPU correctness and long-term stability NOT established."
                : "External process completed but GPU load was low or unclear; not a high-load stability result."
            : "Test incomplete or failed; no stability conclusion.";
        return new SupervisedFurMarkResult(
            id, start, DateTimeOffset.UtcNow, plan, settings, final,
            completed, failure, process, evidence, summary,
            sha256, classification,
            [
                "No driver tuning writes or automatic overclocking are performed.",
                "FurMark CLI exit 0 does not establish GPU rendering correctness, VRAM error freedom or game stability.",
                "GPU targeting cannot be proven solely by the FurMark process exit code; verify the FurMark window identifies the RX 9070 XT.",
                "Host-observed ADLX samples may represent rounded/averaged or delayed values. No precise synchronized FPS or power-per-frame estimate.",
                "For gaming FPS, 1% lows and frame times, capture a repeatable scene with a dedicated PresentMon ETW trace; do not infer these from ADLX GPU usage."
            ]);
    }
}
