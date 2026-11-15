using AmdTune.Core;
using AmdTune.Infrastructure;
using Xunit;

namespace AmdTune.Tests;

public class ExperimentTests
{
    private sealed class Store : IAuditStore
    {
        public RunReport? Saved { get; private set; }
        public Task AppendEventAsync(Guid id, AuditEvent item, CancellationToken ct) => Task.CompletedTask;
        public Task SaveAsync(RunReport report, CancellationToken ct)
        {
            Saved = report;
            return Task.CompletedTask;
        }
    }

    private static (TuningEngine Engine, SimulatedGpuDevice Gpu, Store Store) Setup()
    {
        var gpu = new SimulatedGpuDevice();
        var store = new Store();
        var engine = new TuningEngine(gpu, new SimulatedWorkloadRunner(gpu), store,
            new SimulationRollbackGuard(), new TunePolicy(new SafetyEnvelope()));
        return (engine, gpu, store);
    }

    [Fact]
    public async Task Successful_simulated_session_restores_original_settings()
    {
        var (engine, gpu, store) = Setup();
        var original = gpu.Current;
        var report = await engine.RunAsync(TunePlan.Conservative(original));
        Assert.Equal(RunPhase.Completed, report.Phase);
        Assert.True(report.RollbackVerified);
        Assert.Equal(original, gpu.Current);
        Assert.Equal(3, report.Trials.Count);
        Assert.All(report.Trials, t => Assert.Equal(TrialVerdict.Inconclusive, t.Verdict));
        Assert.NotNull(store.Saved);
    }

    [Fact]
    public async Task Bad_temperature_aborts_before_applying_candidates()
    {
        var (engine, gpu, _) = Setup();
        var initial = gpu.Current;
        gpu.HotspotC = 96;
        var report = await engine.RunAsync(TunePlan.Conservative(initial));
        Assert.Equal(RunPhase.Failed, report.Phase);
        Assert.Equal(initial, gpu.Current);
        Assert.Empty(report.Trials);
    }

    [Fact]
    public async Task Driver_write_failure_is_reported_and_never_approved()
    {
        var (engine, gpu, _) = Setup();
        gpu.RejectWrites = true;
        var report = await engine.RunAsync(TunePlan.Conservative(gpu.Current));
        Assert.Equal(RunPhase.Failed, report.Phase);
        Assert.False(report.RollbackVerified);
        Assert.Empty(report.Trials);
    }

    [Fact]
    public async Task Cancellation_is_followed_by_verified_restore()
    {
        var (engine, gpu, _) = Setup();
        using var cancel = new CancellationTokenSource();
        var report = await engine.RunAsync(TunePlan.Conservative(gpu.Current),
            p => { if (p.Phase == RunPhase.Benchmarking) cancel.Cancel(); },
            cancel.Token);
        Assert.Equal(RunPhase.Cancelled, report.Phase);
        Assert.True(report.RollbackVerified);
        Assert.Equal(0, gpu.Current.VoltageOffsetMv);
    }

    [Fact]
    public async Task Audit_is_stored_as_parseable_versioned_json()
    {
        var gpu = new SimulatedGpuDevice();
        var path = Path.Combine(Path.GetTempPath(), "AmdTuneTest_" + Guid.NewGuid());
        try
        {
            var engine = new TuningEngine(gpu, new SimulatedWorkloadRunner(gpu), new JsonAuditStore(path),
                new SimulationRollbackGuard(), new TunePolicy(new SafetyEnvelope()));
            var report = await engine.RunAsync(new TunePlan([gpu.Current with { VoltageOffsetMv = -15 }]));
            var file = Path.Combine(path, report.Id.ToString("N") + ".json");
            Assert.True(File.Exists(file));
            using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(file));
            Assert.Equal(1, doc.RootElement.GetProperty("SchemaVersion").GetInt32());
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task Corrupt_gpu_result_aborts_and_restores_original_settings()
    {
        var gpu = new SimulatedGpuDevice();
        var store = new Store();
        var runner = new SimulatedWorkloadRunner(gpu) { InjectCorruption = true };
        var engine = new TuningEngine(gpu, runner, store,
            new SimulationRollbackGuard(), new TunePolicy(new SafetyEnvelope()));
        var original = gpu.Current;

        var report = await engine.RunAsync(new TunePlan([original with { VoltageOffsetMv = -15 }]));
        Assert.Equal(RunPhase.Failed, report.Phase);
        Assert.True(report.RollbackVerified);
        Assert.Contains("correctness", report.Error ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Equal(original, gpu.Current);
    }

    [Fact]
    public async Task Journal_contains_original_candidate_and_rollback_events()
    {
        var gpu = new SimulatedGpuDevice();
        var path = Path.Combine(Path.GetTempPath(), "AmdTuneJournal_" + Guid.NewGuid());
        try
        {
            var engine = new TuningEngine(gpu, new SimulatedWorkloadRunner(gpu), new JsonAuditStore(path),
                new SimulationRollbackGuard(), new TunePolicy(new SafetyEnvelope()));
            var report = await engine.RunAsync(new TunePlan([gpu.Current with { VoltageOffsetMv = -15 }]));
            var eventPath = Path.Combine(path, report.Id.ToString("N") + ".events.jsonl");
            Assert.True(File.Exists(eventPath));
            var lines = await File.ReadAllLinesAsync(eventPath);
            Assert.Contains(lines, l => l.Contains("\"Kind\":\"candidate\"", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("\"Kind\":\"final-rollback\"", StringComparison.Ordinal));
            Assert.True(report.RollbackVerified);
        }
        finally { if (Directory.Exists(path)) Directory.Delete(path, true); }
    }

    [Fact]
    public async Task Independent_recovery_monitor_restores_baseline_when_parent_dies()
    {
        var gpu = new SimulatedGpuDevice();
        var baseline = gpu.Current;
        await gpu.ApplyAsync(baseline with { VoltageOffsetMv = -15 }, default);
        var logs = new List<string>();
        var monitor = new RecoveryMonitor(gpu, () => false, () => false,
            () => TimeSpan.Zero, (delay, ct) => Task.CompletedTask, logs.Add);
        var outcome = await monitor.RunAsync(baseline, default);
        Assert.Equal(RecoveryOutcome.Restored, outcome);
        Assert.Equal(baseline, gpu.Current);
        Assert.Contains(logs, line => line.Contains("RECOVERY VERIFIED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Independent_recovery_monitor_fails_closed_after_write_rejections()
    {
        var gpu = new SimulatedGpuDevice { RejectWrites = true };
        var logs = new List<string>();
        var monitor = new RecoveryMonitor(gpu, () => false, () => false,
            () => TimeSpan.Zero, (delay, ct) => Task.CompletedTask, logs.Add);
        var outcome = await monitor.RunAsync(gpu.Current, default);
        Assert.Equal(RecoveryOutcome.Failed, outcome);
        Assert.Equal(5, logs.Count(line => line.StartsWith("Recovery attempt", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Disarmed_guard_does_not_write_to_gpu()
    {
        var gpu = new SimulatedGpuDevice { RejectWrites = true };
        var monitor = new RecoveryMonitor(gpu, () => true, () => true,
            () => TimeSpan.Zero, (delay, ct) => Task.CompletedTask, _ => { });
        var outcome = await monitor.RunAsync(gpu.Current, default);
        Assert.Equal(RecoveryOutcome.Disarmed, outcome);
    }
    [Fact]
    public async Task Independent_heartbeat_runs_while_primary_workload_waits()
    {
        int ticks = 0;
        await using var heartbeat = new RecoveryHeartbeat(
            ct => { Interlocked.Increment(ref ticks); return Task.CompletedTask; },
            TimeSpan.FromMilliseconds(15));
        heartbeat.Start();
        await Task.Delay(110);
        heartbeat.ThrowIfFailed();
        Assert.True(Volatile.Read(ref ticks) >= 3, "Background heartbeats should run without foreground progress.");
    }

    [Fact]
    public async Task Failed_background_heartbeat_is_reported_to_engine()
    {
        await using var heartbeat = new RecoveryHeartbeat(
            ct => throw new IOException("Injected heartbeat failure"),
            TimeSpan.FromMilliseconds(10));
        heartbeat.Start();
        await Task.Delay(100);
        var result = Assert.Throws<InvalidOperationException>(() => heartbeat.ThrowIfFailed());
        Assert.IsType<IOException>(result.InnerException);
    }

    [Fact]
    public async Task Failure_before_candidate_write_does_not_issue_unnecessary_driver_write()
    {
        var gpu = new CountingGpu { HotspotC = 98 };
        var baseline = await gpu.ReadSettingsAsync(default);
        var store = new Store();
        var simulated = new SimulatedGpuDevice();
        var engine = new TuningEngine(gpu, new SimulatedWorkloadRunner(simulated), store,
            new SimulationRollbackGuard(), new TunePolicy(new SafetyEnvelope()));

        var result = await engine.RunAsync(new TunePlan([baseline with { VoltageOffsetMv = -15 }]));
        Assert.Equal(RunPhase.Failed, result.Phase);
        Assert.True(result.RollbackVerified);
        Assert.Equal(0, gpu.ApplyCalls);
    }

    private sealed class CountingGpu : IGpuDevice
    {
        private readonly SimulatedGpuDevice _inner = new();
        public double HotspotC { get; init; } = 48;
        public int ApplyCalls { get; private set; }
        public Task<DeviceCapabilities> DiscoverAsync(CancellationToken ct) => _inner.DiscoverAsync(ct);
        public Task<TuningSettings> ReadSettingsAsync(CancellationToken ct) => _inner.ReadSettingsAsync(ct);
        public Task<GpuTelemetry> ReadTelemetryAsync(CancellationToken ct)
            => Task.FromResult(new GpuTelemetry(DateTimeOffset.UtcNow, HotspotC, 200, 2800, 2500));
        public Task ApplyAsync(TuningSettings settings, CancellationToken ct)
        {
            ApplyCalls++;
            return _inner.ApplyAsync(settings, ct);
        }
    }


}
