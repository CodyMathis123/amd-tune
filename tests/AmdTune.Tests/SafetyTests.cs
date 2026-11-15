using AmdTune.Core;
using AmdTune.Infrastructure;
using Xunit;

namespace AmdTune.Tests;

public class SafetyTests
{
    private static readonly TunePolicy Policy = new(new SafetyEnvelope());

    [Fact]
    public async Task Discovery_exposes_simulation_unambiguously()
    {
        var caps = await new SimulatedGpuDevice().DiscoverAsync(default);
        Assert.True(caps.IsSimulation);
        Assert.Contains("SIMULATED", caps.Name);
    }

    [Theory]
    [InlineData(-80, 2500)]
    [InlineData(-15, 2700)]
    [InlineData(3, 2500)]
    [InlineData(-15, 1500)]
    public async Task Rejects_settings_outside_conservative_envelope(int uv, int vram)
    {
        var gpu = new SimulatedGpuDevice();
        var caps = await gpu.DiscoverAsync(default);
        var original = await gpu.ReadSettingsAsync(default);
        var plan = new TunePlan([new TuningSettings(uv, vram, MemoryTimingMode.Default)]);
        Assert.Throws<SafetyException>(() => Policy.Validate(caps, original, plan));
    }

    [Fact]
    public async Task Requires_at_least_three_repetitions()
    {
        var gpu = new SimulatedGpuDevice();
        var caps = await gpu.DiscoverAsync(default);
        var original = await gpu.ReadSettingsAsync(default);
        Assert.Throws<SafetyException>(() => Policy.Validate(caps, original,
            new TunePlan([original], 1)));
    }

    [Fact]
    public void Fails_closed_when_telemetry_missing_or_stale()
    {
        Assert.Throws<SafetyException>(() => TunePolicy.RequireTelemetry(
            new GpuTelemetry(DateTimeOffset.UtcNow, null, 200, 2800, 2500), 90));
        Assert.Throws<SafetyException>(() => TunePolicy.RequireTelemetry(
            new GpuTelemetry(DateTimeOffset.UtcNow.AddMinutes(-1), 40, 200, 2800, 2500), 90));
    }

    [Fact]
    public void Detects_driver_readback_mismatch()
    {
        Assert.Throws<HardwareException>(() => TunePolicy.RequireReadback(
            new TuningSettings(-20, 2500, MemoryTimingMode.Default),
            new TuningSettings(-19, 2500, MemoryTimingMode.Default)));
    }

    [Fact]
    public void Simulation_never_counts_as_hardware_validation()
    {
        var m = new WorkloadMeasurement(WorkloadKind.Compute, false, true, 100, 0, 100,
            "synthetic", 0, "fake");
        Assert.Equal(TrialVerdict.Inconclusive, TunePolicy.Decide([m]));
    }

    [Fact]
    public void Correctness_error_always_rejects_trial()
    {
        var m = new WorkloadMeasurement(WorkloadKind.Memory, true, false, 1024, 1, 100,
            "verified elements/s", 10, "mismatch");
        Assert.Equal(TrialVerdict.Rejected, TunePolicy.Decide([m]));
    }

    [Fact]
    public void Statistics_are_sample_based_and_drift_adjusted()
    {
        var stats = MeasurementAnalysis.Describe([10d, 12d, 14d]);
        Assert.Equal(12, stats.Mean);
        Assert.Equal(2, stats.StandardDeviation, 8);
        Assert.Equal(0, MeasurementAnalysis.RelativeChangePercent(10, 11, 12), 8);
    }
}
