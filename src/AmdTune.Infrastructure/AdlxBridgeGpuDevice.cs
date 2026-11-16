using AmdTune.Core;

namespace AmdTune.Infrastructure;

/// <summary>
/// Windows ADLX access is deliberately limited to a separately built, auditable C++ helper.
/// No third-party binary is downloaded or executed.
/// </summary>
public sealed class AdlxBridgeGpuDevice(BridgeProcess bridge) : IGpuDevice, IStreamingGpuTelemetryDevice
{
    private sealed record WriteResult(bool Ok, string? Error);

    public Task<DeviceCapabilities> DiscoverAsync(CancellationToken ct)
        => bridge.InvokeAsync<DeviceCapabilities>(ct, "discover");

    public Task<TuningSettings> ReadSettingsAsync(CancellationToken ct)
        => bridge.InvokeAsync<TuningSettings>(ct, "read");

    public Task<GpuTelemetry> ReadTelemetryAsync(CancellationToken ct)
        => bridge.InvokeAsync<GpuTelemetry>(ct, "telemetry");

    public IAsyncEnumerable<GpuTelemetry> StreamTelemetryAsync(TimeSpan interval, CancellationToken ct)
        => bridge.StreamTelemetryAsync(interval, ct);

    public async Task ApplyAsync(TuningSettings settings, CancellationToken ct)
    {
        var result = await bridge.InvokeAsync<WriteResult>(ct,
            "apply",
            settings.VoltageOffsetMv.ToString(System.Globalization.CultureInfo.InvariantCulture),
            settings.MemoryClockMhz.ToString(System.Globalization.CultureInfo.InvariantCulture),
            settings.MemoryTiming.ToString());
        if (!result.Ok) throw new HardwareException("Driver rejected settings: " + result.Error);
        // Readback is still required by the orchestrator, not trusted to the bridge alone.
    }
}
