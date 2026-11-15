using AmdTune.Core;
using AmdTune.GpuWorkloads;
using AmdTune.Infrastructure;
using Xunit;

namespace AmdTune.Tests;

public class BenchmarkEvidenceTests
{
    [Fact]
    public void Evidence_never_unlocks_automatic_tuning_for_short_noisy_results()
    {
        var before = Samples(3, 5, 100);
        var candidate = Samples(3, 5, 104);
        var after = Samples(3, 5, 98);
        var comparisons = MeasurementAnalysis.Compare(before, candidate, after);
        var evidence = EvidenceAssessment.Evaluate(before, candidate, after, comparisons);
        Assert.True(evidence.AllGpuResultsCorrect);
        Assert.False(evidence.SustainedStabilityDemonstrated);
        Assert.False(evidence.HardwareRollbackTested);
        Assert.False(evidence.SuitableForAutomaticTuning);
        Assert.All(evidence.Workloads, q => Assert.False(q.ComparisonUsable));
        Assert.All(evidence.Workloads, q => Assert.Contains(q.Limitations, x => x.Contains("250 ms")));
    }

    [Fact]
    public void Corruption_is_never_considered_stability_success()
    {
        var b = Samples(7, 750, 100);
        var x = Samples(7, 750, 104).Select(m => m.Kind == WorkloadKind.Memory
            ? m with { Correct = false, Mismatches = 1 } : m).ToArray();
        var a = Samples(7, 750, 100);
        var evidence = EvidenceAssessment.Evaluate(b, x, a,
            MeasurementAnalysis.Compare(b, x, a));
        Assert.False(evidence.AllGpuResultsCorrect);
        Assert.False(evidence.SuitableForAutomaticTuning);
    }

    [Fact]
    public void Even_clean_measurements_do_not_replace_crash_restore_or_independent_session_validation()
    {
        var loaded = new WorkloadTelemetryEvidence(
            DateTimeOffset.UtcNow.AddSeconds(-2), DateTimeOffset.UtcNow,
            5, 5, 60, 200, 175, 90, 2300, []);
        var b = Samples(7, 750, 100).Select(m => m with { TelemetryEvidence = loaded }).ToArray();
        var c = Samples(7, 750, 101).Select(m => m with { TelemetryEvidence = loaded }).ToArray();
        var a = Samples(7, 750, 100).Select(m => m with { TelemetryEvidence = loaded }).ToArray();
        var evidence = EvidenceAssessment.Evaluate(b, c, a,
            MeasurementAnalysis.Compare(b, c, a));
        Assert.All(evidence.Workloads, q => Assert.True(q.ComparisonUsable));
        Assert.False(evidence.SuitableForAutomaticTuning);
        Assert.False(evidence.HardwareRollbackTested);
        Assert.False(evidence.SustainedStabilityDemonstrated);
    }

    [Fact]
    public async Task Concurrent_gpu_telemetry_records_samples_and_separates_loaded_evidence()
    {
        var gpu = new InstrumentedDevice(new GpuTelemetry(DateTimeOffset.UtcNow, 45, 200, 2300, 1500, 95));
        using var run = new CancellationTokenSource();
        await using var sample = new WorkloadTelemetrySampler(gpu, 90, run, TimeSpan.FromMilliseconds(15));
        sample.Start();
        await Task.Delay(110);
        var report = await sample.StopAsync();
        Assert.True(report.SampleCount >= 2);
        Assert.True(report.ActiveSamples >= 2);
        Assert.Equal(200, report.MeanActiveBoardPowerW);
        Assert.Equal(45, report.PeakHotspotC);
    }

    [Fact]
    public async Task Over_temperature_during_sampling_requests_workload_cancellation()
    {
        var gpu = new InstrumentedDevice(new GpuTelemetry(DateTimeOffset.UtcNow, 95, 200, 2300, 1500, 95));
        using var run = new CancellationTokenSource();
        await using var sample = new WorkloadTelemetrySampler(gpu, 90, run, TimeSpan.FromMilliseconds(15));
        sample.Start();
        for (var i = 0; i < 50 && !run.IsCancellationRequested; i++)
            await Task.Delay(10);
        Assert.True(run.IsCancellationRequested);
        var error = await Assert.ThrowsAsync<SafetyException>(() => sample.StopAsync());
        Assert.Contains("thermal", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Sustained_profile_dispatches_remain_bounded()
    {
        foreach (var count in new[] { 1_048_576, 33_554_432, 4_194_304 })
        {
            var slices = GpuDispatchPlanner.Plan(count);
            var maxPasses = SustainedGpuBenchmarkRunner.MaxDispatchesPerWindow / slices.Count;
            Assert.InRange(maxPasses, 1, 16_384);
            Assert.True(maxPasses * slices.Count <= SustainedGpuBenchmarkRunner.MaxDispatchesPerWindow);
            Assert.InRange(SustainedGpuBenchmarkRunner.TargetWindowMilliseconds, 500, 1500);
        }
    }

    private static WorkloadMeasurement[] Samples(int perKind, double elapsed, double baseScore)
        => Enum.GetValues<WorkloadKind>().SelectMany(kind =>
            Enumerable.Range(0, perKind).Select(i =>
                new WorkloadMeasurement(kind, true, true, 1_048_576, 0,
                    baseScore + i * 0.05,
                    "host submitted", elapsed, "test")))
            .ToArray();

    private sealed class InstrumentedDevice(GpuTelemetry snapshot) : IGpuDevice
    {
        private readonly SimulatedGpuDevice _stub = new();
        public Task<DeviceCapabilities> DiscoverAsync(CancellationToken ct) => _stub.DiscoverAsync(ct);
        public Task<TuningSettings> ReadSettingsAsync(CancellationToken ct) => _stub.ReadSettingsAsync(ct);
        public Task ApplyAsync(TuningSettings settings, CancellationToken ct)
            => throw new SafetyException("Test prohibits writes.");
        public Task<GpuTelemetry> ReadTelemetryAsync(CancellationToken ct)
            => Task.FromResult(snapshot with { At = DateTimeOffset.UtcNow });
    }
}
