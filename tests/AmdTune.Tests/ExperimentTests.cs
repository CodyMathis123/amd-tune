using AmdTune.Core;
using AmdTune.Infrastructure;
using Xunit;

namespace AmdTune.Tests;

public class ExperimentTests
{
    private sealed class Store : IAuditStore
    {
        public RunReport? Saved { get; private set; }
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
}
