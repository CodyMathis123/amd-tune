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
    [Fact]
    public async Task Stopping_during_an_inflight_telemetry_read_is_normal_not_a_hardware_fault()
    {
        var gpu = new BlockingTelemetryDevice();
        using var run = new CancellationTokenSource();
        await using var sampler = new WorkloadTelemetrySampler(
            gpu, 90, run, TimeSpan.FromMilliseconds(10));
        sampler.Start();
        await gpu.Entered.WaitAsync(TimeSpan.FromSeconds(3));
        var evidence = await sampler.StopAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, evidence.SampleCount);
        Assert.Equal(0, evidence.ActiveSamples);
        Assert.False(run.IsCancellationRequested);
    }

    [Fact]
    public async Task Recorder_can_finish_a_correct_workload_while_a_read_is_in_flight()
    {
        var gpu = new BlockingTelemetryDevice();
        var recorder = new WorkloadTelemetryRecorder(gpu);
        var measurement = await recorder.CaptureAsync(
            async ct =>
            {
                await gpu.Entered.WaitAsync(ct);
                return new WorkloadMeasurement(
                    WorkloadKind.Compute, true, true, 1024, 0, 512,
                    "test", 300, "in-flight cancellation regression");
            },
            90, CancellationToken.None);
        Assert.True(measurement.Correct);
        Assert.NotNull(measurement.TelemetryEvidence);
        Assert.Equal(0, measurement.TelemetryEvidence.SampleCount);
    }

    [Fact]
    public async Task Telemetry_driver_error_is_never_suppressed_as_a_normal_stop()
    {
        var gpu = new FailingTelemetryDevice();
        using var run = new CancellationTokenSource();
        await using var sampler = new WorkloadTelemetrySampler(gpu, 90, run);
        sampler.Start();
        await gpu.Entered.WaitAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(25);
        var error = await Assert.ThrowsAsync<SafetyException>(sampler.StopAsync);
        Assert.Contains("ADLX diagnostic failure", error.Message);
        Assert.True(run.IsCancellationRequested);
    }

    private sealed class BlockingTelemetryDevice : IGpuDevice
    {
        private readonly SimulatedGpuDevice _stub = new();
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public Task<DeviceCapabilities> DiscoverAsync(CancellationToken ct) => _stub.DiscoverAsync(ct);
        public Task<TuningSettings> ReadSettingsAsync(CancellationToken ct) => _stub.ReadSettingsAsync(ct);
        public Task ApplyAsync(TuningSettings settings, CancellationToken ct)
            => throw new SafetyException("Mock must not write tuning settings.");
        public async Task<GpuTelemetry> ReadTelemetryAsync(CancellationToken ct)
        {
            _entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new GpuTelemetry(DateTimeOffset.UtcNow, 40, 100, 2000, 1000, 90);
        }
    }

    private sealed class FailingTelemetryDevice : IGpuDevice
    {
        private readonly SimulatedGpuDevice _stub = new();
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public Task<DeviceCapabilities> DiscoverAsync(CancellationToken ct) => _stub.DiscoverAsync(ct);
        public Task<TuningSettings> ReadSettingsAsync(CancellationToken ct) => _stub.ReadSettingsAsync(ct);
        public Task ApplyAsync(TuningSettings settings, CancellationToken ct)
            => throw new SafetyException("Mock must not write tuning settings.");
        public Task<GpuTelemetry> ReadTelemetryAsync(CancellationToken ct)
        {
            _entered.TrySetResult(true);
            return Task.FromException<GpuTelemetry>(new HardwareException("ADLX diagnostic failure"));
        }
    }

    [Fact]
    public async Task Streaming_sampler_uses_one_native_session_instead_of_snapshot_processes()
    {
        var gpu = new FakeStreamingDevice(false);
        using var work = new CancellationTokenSource();
        await using var sampler = new WorkloadTelemetrySampler(gpu, 90, work);
        sampler.Start();
        await gpu.FiveSamples.WaitAsync(TimeSpan.FromSeconds(3));
        var evidence = await sampler.StopAsync();
        Assert.InRange(evidence.SampleCount, 5, 256);
        Assert.InRange(evidence.ActiveSamples, 5, 256);
        Assert.Equal(1, gpu.StreamStarts);
        Assert.Equal(0, gpu.SnapshotRequests);
        Assert.Equal(TimeSpan.FromMilliseconds(125), gpu.RequestedInterval);
        Assert.False(work.IsCancellationRequested);
        Assert.True(evidence.HasUsefulLoadEvidence);
    }

    [Fact]
    public async Task Broken_native_telemetry_stream_fails_closed_and_cancels_workload()
    {
        var gpu = new FakeStreamingDevice(true);
        using var work = new CancellationTokenSource();
        await using var sampler = new WorkloadTelemetrySampler(gpu, 90, work);
        sampler.Start();
        for (var i = 0; i < 100 && !work.IsCancellationRequested; i++)
            await Task.Delay(10);
        Assert.True(work.IsCancellationRequested);
        var ex = await Assert.ThrowsAsync<SafetyException>(() => sampler.StopAsync());
        Assert.Contains("Injected native stream failure", ex.Message);
        Assert.Equal(0, gpu.SnapshotRequests);
    }

    private sealed class FakeStreamingDevice(bool fail) : IGpuDevice, IStreamingGpuTelemetryDevice
    {
        private readonly SimulatedGpuDevice _simulation = new();
        private readonly TaskCompletionSource<bool> _five =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task FiveSamples => _five.Task;
        public int SnapshotRequests { get; private set; }
        public int StreamStarts { get; private set; }
        public TimeSpan RequestedInterval { get; private set; }
        public Task<DeviceCapabilities> DiscoverAsync(CancellationToken ct)
            => _simulation.DiscoverAsync(ct);
        public Task<TuningSettings> ReadSettingsAsync(CancellationToken ct)
            => _simulation.ReadSettingsAsync(ct);
        public Task ApplyAsync(TuningSettings settings, CancellationToken ct)
            => throw new SafetyException("Streaming fake must never write GPU settings.");
        public Task<GpuTelemetry> ReadTelemetryAsync(CancellationToken ct)
        {
            SnapshotRequests++;
            return Task.FromException<GpuTelemetry>(
                new HardwareException("Per-reading snapshot path should not be used."));
        }
        public async IAsyncEnumerable<GpuTelemetry> StreamTelemetryAsync(
            TimeSpan interval,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            StreamStarts++;
            RequestedInterval = interval;
            for (var i = 0; !ct.IsCancellationRequested; i++)
            {
                if (fail && i == 2)
                    throw new HardwareException("Injected native stream failure");
                if (i >= 4) _five.TrySetResult(true);
                yield return new GpuTelemetry(DateTimeOffset.UtcNow, 42, 185, 2200, 1800, 95);
                await Task.Delay(5, ct);
            }
        }
    }

    [Fact]
    public async Task Repeated_ADLX_source_timestamps_cannot_be_counted_as_independent_loaded_samples()
    {
        var gpu = new TimestampedStreamingDevice(usage: 64, repeated: true);
        using var run = new CancellationTokenSource();
        await using var sampler = new WorkloadTelemetrySampler(
            gpu, 90, run, TimeSpan.FromMilliseconds(125));
        sampler.Start();
        await gpu.FiveSamples.WaitAsync(TimeSpan.FromSeconds(3));
        var result = await sampler.StopAsync();
        Assert.True(result.SampleCount >= 5);
        Assert.Equal(1, result.ActiveSamples);
        Assert.False(result.HasUsefulLoadEvidence);
        Assert.NotNull(result.Diagnostics);
        Assert.True(result.Diagnostics.NativeMetricTimestampsAvailable);
        Assert.Equal(1, result.Diagnostics.DistinctMetricSamples);
        Assert.Equal(result.SampleCount - 1, result.Diagnostics.RepeatedMetricSamples);
        Assert.Equal(64, result.Diagnostics.MaximumUtilizationPercent);
    }

    [Fact]
    public async Task Low_reported_usage_is_diagnostic_not_proof_the_GPU_was_idle()
    {
        var gpu = new TimestampedStreamingDevice(usage: 31, repeated: false);
        using var run = new CancellationTokenSource();
        await using var sampler = new WorkloadTelemetrySampler(
            gpu, 90, run, TimeSpan.FromMilliseconds(125));
        sampler.Start();
        await gpu.FiveSamples.WaitAsync(TimeSpan.FromSeconds(3));
        var evidence = await sampler.StopAsync();
        Assert.True(evidence.SampleCount >= 5);
        Assert.Equal(0, evidence.ActiveSamples);
        Assert.False(evidence.HasUsefulLoadEvidence);
        Assert.NotNull(evidence.Diagnostics);
        Assert.InRange(evidence.Diagnostics.DistinctMetricSamples, 5, 256);
        Assert.Equal(31, evidence.Diagnostics.MinimumUtilizationPercent);
        Assert.Equal(31, evidence.Diagnostics.MaximumUtilizationPercent);
        Assert.True(evidence.Diagnostics.PositiveClockSamples >= 5);
        Assert.True(evidence.Diagnostics.PositiveUtilizationSamples >= 5);
    }

    private sealed class TimestampedStreamingDevice(double usage, bool repeated)
        : IGpuDevice, IStreamingGpuTelemetryDevice
    {
        private readonly SimulatedGpuDevice _simulation = new();
        private readonly TaskCompletionSource<bool> _five =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly long _sourceTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        public Task FiveSamples => _five.Task;
        public Task<DeviceCapabilities> DiscoverAsync(CancellationToken ct)
            => _simulation.DiscoverAsync(ct);
        public Task<TuningSettings> ReadSettingsAsync(CancellationToken ct)
            => _simulation.ReadSettingsAsync(ct);
        public Task ApplyAsync(TuningSettings s, CancellationToken ct)
            => throw new SafetyException("Read-only timestamp fixture must never apply tuning.");
        public Task<GpuTelemetry> ReadTelemetryAsync(CancellationToken ct)
            => throw new InvalidOperationException("Expected stream-based sample");
        public async IAsyncEnumerable<GpuTelemetry> StreamTelemetryAsync(
            TimeSpan interval,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            for (var i = 0; !ct.IsCancellationRequested; i++)
            {
                if (i >= 4) _five.TrySetResult(true);
                yield return new GpuTelemetry(
                    DateTimeOffset.UtcNow, 45, 90, 1900, 1500,
                    usage, _sourceTime + (repeated ? 0 : i * 125L));
                await Task.Delay(6, ct);
            }
        }
    }

    [Fact]
    public void Monitoring_comparison_interleaves_fast_and_slow_sampling_with_time_balancing()
    {
        Assert.Equal(new[] { 125, 500, 500, 125 }, MonitoringComparison.IntervalsForRound(0));
        Assert.Equal(new[] { 500, 125, 125, 500 }, MonitoringComparison.IntervalsForRound(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MonitoringComparison.IntervalsForRound(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => MonitoringComparison.IntervalsForRound(-1));
    }

    [Fact]
    public void Monitoring_comparison_rejects_missing_or_incorrect_GPU_data()
    {
        var complete = MonitoringTestObservations();
        Assert.Equal(3, MonitoringComparison.Summarize(complete).Count);
        Assert.Throws<ArgumentException>(() => MonitoringComparison.Summarize(complete.Skip(1).ToArray()));
        var corrupt = complete.ToArray();
        corrupt[0] = corrupt[0] with
        {
            Measurement = corrupt[0].Measurement with { Correct = false, Mismatches = 1 }
        };
        Assert.Throws<ArgumentException>(() => MonitoringComparison.Summarize(corrupt));
    }

    [Fact]
    public void Monitoring_comparison_reports_observed_score_difference_not_a_tuning_verdict()
    {
        var observations = MonitoringTestObservations();
        var result = MonitoringComparison.Summarize(observations);
        Assert.All(result, summary =>
        {
            Assert.Equal(4, summary.Fast.N);
            Assert.Equal(4, summary.Slow.N);
            Assert.Equal(10, summary.SlowVsFastPercent, 7);
            Assert.Equal(0, summary.FastDistinctSourceSamples);
            Assert.Equal(0, summary.SlowDistinctSourceSamples);
        });
    }

    private static MonitoringObservation[] MonitoringTestObservations()
    {
        var items = new List<MonitoringObservation>();
        foreach (var kind in Enum.GetValues<WorkloadKind>())
            for (var round = 0; round < MonitoringComparison.Rounds; round++)
            {
                var sequence = MonitoringComparison.IntervalsForRound(round);
                for (var i = 0; i < sequence.Count; i++)
                {
                    var ms = sequence[i];
                    items.Add(new MonitoringObservation(
                        kind, round, i, ms,
                        new WorkloadMeasurement(kind, true, true, 1024, 0,
                            ms == MonitoringComparison.FastIntervalMs ? 100 : 110,
                            "test", 750, "monitor crossover")));
                }
            }
        return items.ToArray();
    }

}
