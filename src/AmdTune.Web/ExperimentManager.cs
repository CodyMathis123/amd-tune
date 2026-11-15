using AmdTune.Core;

namespace AmdTune.Web;

public sealed record ExperimentSnapshot(bool Running, RunProgress? Progress, RunReport? Report, string? LastError);

/// <summary>One explicitly started experiment at a time; never auto-runs on startup.</summary>
public sealed class ExperimentManager(IGpuDevice device, TuningEngine engine, bool canWrite)
{
    private readonly object _gate = new();
    private CancellationTokenSource? _cancel;
    private ExperimentSnapshot _snapshot = new(false, null, null, null);
    public ExperimentSnapshot Snapshot { get { lock (_gate) return _snapshot; } }

    public Task<bool> StartAsync(IReadOnlyList<TuningSettings> candidates, int repetitions)
    {
        if (!canWrite) throw new SafetyException("Hardware writes are disabled. Start with --enable-hardware-writes only after completing the safety checklist.");
        if (candidates is null || candidates.Count == 0) throw new SafetyException("At least one candidate required.");
        lock (_gate)
        {
            if (_snapshot.Running) return Task.FromResult(false);
            _cancel = new CancellationTokenSource();
            _snapshot = new ExperimentSnapshot(true, null, null, null);
        }
        var token = _cancel!.Token;
        // Use a dedicated task so the web request stays responsive. The engine itself is
        // responsible for rollback on failures/cancellation; a separate process guards live writes.
        _ = Task.Run(async () =>
        {
            try
            {
                var report = await engine.RunAsync(new TunePlan(candidates, repetitions),
                    p => { lock (_gate) _snapshot = _snapshot with { Progress = p }; }, token);
                lock (_gate) _snapshot = new(false, _snapshot.Progress, report, report.Error);
            }
            catch (Exception ex)
            {
                lock (_gate) _snapshot = _snapshot with
                {
                    Running = false,
                    LastError = ex.ToString()
                };
            }
            finally
            {
                lock (_gate)
                {
                    _cancel?.Dispose();
                    _cancel = null;
                }
            }
        });
        return Task.FromResult(true);
    }

    public bool Cancel()
    {
        lock (_gate)
        {
            if (!_snapshot.Running || _cancel is null) return false;
            _cancel.Cancel();
            return true;
        }
    }
}
