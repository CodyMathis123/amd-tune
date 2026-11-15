using AmdTune.Core;

namespace AmdTune.Infrastructure;

/// <summary>
/// Concurrent ADLX safety and evidence sampling while a workload runs.
/// Sampling may include CPU-side verification; never label it GPU-only.
/// Any failed or unsafe reading requests workload cancellation and causes
/// a session failure, so the normal rollback path remains armed.
/// </summary>
public sealed class WorkloadTelemetrySampler(
    IGpuDevice device,
    double hotspotLimitC,
    CancellationTokenSource workloadCancellation,
    TimeSpan? sampleInterval = null) : IAsyncDisposable
{
    // The native stream avoids expensive per-reading process startup, so
    // use a faster cadence only when that streaming path is supported.
    private readonly TimeSpan _interval = sampleInterval ??
        (device is IStreamingGpuTelemetryDevice
            ? TimeSpan.FromMilliseconds(125)
            : TimeSpan.FromMilliseconds(250));
    private readonly CancellationTokenSource _stop = CancellationTokenSource.CreateLinkedTokenSource(workloadCancellation.Token);
    private readonly List<GpuTelemetry> _samples = [];
    private Task? _worker;
    private Exception? _error;
    private DateTimeOffset _started;

    public void Start()
    {
        if (_worker is not null) throw new InvalidOperationException("Sampler already started.");
        if (_interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sampleInterval));
        _started = DateTimeOffset.UtcNow;
        _worker = Task.Run(PollAsync);
    }

    private async Task PollAsync()
    {
        try
        {
            if (device is IStreamingGpuTelemetryDevice streaming)
            {
                await foreach (var reading in streaming.StreamTelemetryAsync(_interval, _stop.Token))
                    ValidateAndStore(reading);
                // Unexpected clean EOF while sampling is an unsafe loss
                // of live thermal protection; caller-stop is handled below.
                if (!_stop.IsCancellationRequested)
                    throw new HardwareException("Native telemetry stream ended unexpectedly.");
            }
            else
            {
                while (!_stop.IsCancellationRequested)
                {
                    var reading = await device.ReadTelemetryAsync(_stop.Token);
                    ValidateAndStore(reading);
                    await Task.Delay(_interval, _stop.Token);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            _error = error;
            // The benchmark may not observe cancellation until a bounded
            // dispatch returns; the independent watchdog remains armed.
            try { workloadCancellation.Cancel(); }
            catch (ObjectDisposedException) { }
        }
    }

    private void ValidateAndStore(GpuTelemetry reading)
    {
        TunePolicy.RequireTelemetry(reading, hotspotLimitC);
        if (_samples.Count < 256) _samples.Add(reading);
    }

    public async Task<WorkloadTelemetryEvidence> StopAsync()
    {
        if (_worker is null) throw new InvalidOperationException("Sampler was never started.");
        _stop.Cancel();
        await _worker;
        if (_error is not null)
            throw new SafetyException("GPU telemetry sampling failed or exceeded a thermal guard: " + _error.Message);
        var end = DateTimeOffset.UtcNow;
        var samples = _samples.ToArray();
        var active = samples.Where(s => s.UtilizationPercent is >= 50 and <= 100 &&
                                         s.CoreClockMhz is > 0).ToArray();
        static double? MeanOrNull(IEnumerable<double> values)
        {
            var v = values.Where(double.IsFinite).ToArray();
            return v.Length == 0 ? null : v.Average();
        }
        return new WorkloadTelemetryEvidence(
            _started, end, samples.Length, active.Length,
            samples.Where(s => s.HotspotC is > 0).Select(s => s.HotspotC!.Value)
                .DefaultIfEmpty(double.NaN).Max() is var hot && double.IsFinite(hot) ? hot : null,
            samples.Where(s => s.BoardPowerW is > 0).Select(s => s.BoardPowerW!.Value)
                .DefaultIfEmpty(double.NaN).Max() is var p && double.IsFinite(p) ? p : null,
            MeanOrNull(active.Where(s => s.BoardPowerW is > 0).Select(s => s.BoardPowerW!.Value)),
            MeanOrNull(active.Select(s => s.UtilizationPercent!.Value)),
            MeanOrNull(active.Select(s => (double)s.CoreClockMhz!.Value)),
            samples);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_worker is not null) await _worker;
        _stop.Dispose();
    }
}
