using AmdTune.Core;

namespace AmdTune.Infrastructure;

/// <summary>A transparent fake: never opens a driver, and never reports real GPU performance.</summary>
public sealed class SimulatedGpuDevice : IGpuDevice
{
    private readonly object _gate = new();
    private TuningSettings _settings = new(0, 2500, MemoryTimingMode.Default);
    public bool RejectWrites { get; set; }
    public bool CorruptReadback { get; set; }
    public double HotspotC { get; set; } = 49;
    public TuningSettings Current { get { lock (_gate) return _settings; } }

    public Task<DeviceCapabilities> DiscoverAsync(CancellationToken ct)
        => Task.FromResult(new DeviceCapabilities("simulation:0", "SIMULATED Radeon (NO HARDWARE ACCESS)",
            "Simulation", true, true, true, new IntRange(-150, 0),
            new IntRange(2000, 3000, 5), true,
            ["Demonstration data only. All settings and benchmark scores are synthetic."]));

    public Task<TuningSettings> ReadSettingsAsync(CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(CorruptReadback
                ? _settings with { VoltageOffsetMv = _settings.VoltageOffsetMv + 1 } : _settings);
        }
    }

    public Task<GpuTelemetry> ReadTelemetryAsync(CancellationToken ct)
        => Task.FromResult(new GpuTelemetry(DateTimeOffset.UtcNow, HotspotC, 235,
            2800 + -Current.VoltageOffsetMv, Current.MemoryClockMhz));

    public Task ApplyAsync(TuningSettings settings, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (RejectWrites) throw new HardwareException("Simulated driver write failure");
        lock (_gate) _settings = settings;
        return Task.CompletedTask;
    }
}
