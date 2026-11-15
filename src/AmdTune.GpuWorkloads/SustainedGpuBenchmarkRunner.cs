using System.Diagnostics;
using System.Runtime.Versioning;
using AmdTune.Core;
using ComputeSharp;

namespace AmdTune.GpuWorkloads;

/// <summary>
/// Longer host-timed GPU throughput window, with deterministic full-buffer
/// verification and sticky evidence of incorrect *intermediate* GPU writes.
/// This is NOT GPU timestamp timing, GDDR6 bandwidth or a gaming stability test.
/// </summary>
public sealed class SustainedGpuBenchmarkRunner(IGpuDevice device) : IWorkloadRunner
{
    public const int TargetWindowMilliseconds = 750;
    public const int MaxDispatchesPerWindow = 16_384;

    public async Task<WorkloadMeasurement> RunAsync(
        WorkloadKind kind, int seed, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            throw new PlatformNotSupportedException("Sustained DX12 workloads require Windows.");
        var caps = await device.DiscoverAsync(cancellationToken);
        var gpu = GraphicsDevice.GetDefault();
        if (!gpu.IsHardwareAccelerated ||
            ComputeSharpWorkloadRunner.ExtractModel(caps.Name) is not { } intended ||
            ComputeSharpWorkloadRunner.ExtractModel(gpu.Name) != intended)
            throw new HardwareException("ADLX and DX12 GPU identities do not match; refusing to benchmark.");

        var count = kind switch
        {
            WorkloadKind.Compute => 1_048_576,
            WorkloadKind.Memory => 33_554_432,
            WorkloadKind.Mixed => 4_194_304,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var rounds = kind switch
        {
            WorkloadKind.Compute => 64,
            WorkloadKind.Memory => 1,
            _ => 16
        };

        // Independent CPU oracle: all buffer elements start as their expected
        // post-shader value. Each GPU pass compares the previous pass's output
        // to this expectation and sets a sticky error flag before re-writing.
        // The CPU checks both the sticky flags and final outputs.
        var expected = new int[count];
        for (int i = 0; i < count; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var v = unchecked((i * 37 + seed) & 0x000FFFFF);
            for (int j = 0; j < rounds; j++)
                v = ((v ^ (i + j * 17)) + 29) & 0x3FFFFFFF;
            expected[i] = v;
        }

        var dispatches = GpuDispatchPlanner.Plan(count);
        int maxPasses = MaxDispatchesPerWindow / dispatches.Count;
        if (maxPasses < 1) throw new HardwareException("GPU workload cannot fit dispatch safety limit.");
        var sticky = new int[count];
        using ReadWriteBuffer<int> data = gpu.AllocateReadWriteBuffer(expected);
        using ReadWriteBuffer<int> errors = gpu.AllocateReadWriteBuffer(sticky);

        void DispatchPass()
        {
            foreach (var slice in dispatches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                gpu.For(slice.Length, new RepeatAndCheckKernel(
                    data, errors, seed, rounds, slice.Offset));
            }
        }

        // Warm up GPU work without contributing to the measured throughput.
        DispatchPass();
        DispatchPass();

        // Capture only the timed dispatch/readback phase, excluding adapter
        // discovery, CPU reference setup, buffer creation, warmup and CPU oracle.
        var hostWindowStart = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        int measuredPasses = 0;
        // Respect maximum dispatch count even on very fast GPUs.
        while (stopwatch.ElapsedMilliseconds < TargetWindowMilliseconds &&
               measuredPasses < maxPasses)
        {
            DispatchPass();
            measuredPasses++;
        }
        if (measuredPasses == 0)
        {
            DispatchPass();
            measuredPasses = 1;
        }
        var resultData = new int[count];
        data.CopyTo(resultData);
        errors.CopyTo(sticky);
        stopwatch.Stop();
        var hostWindowEnd = DateTimeOffset.UtcNow;
        cancellationToken.ThrowIfCancellationRequested();
        long mismatches = 0;
        for (int i = 0; i < count; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var v = unchecked((i * 37 + seed) & 0x000FFFFF);
            for (int j = 0; j < rounds; j++)
                v = ((v ^ (i + j * 17)) + 29) & 0x3FFFFFFF;
            if (resultData[i] != v || sticky[i] != 0) mismatches++;
        }
        var elapsedMs = Math.Max(stopwatch.Elapsed.TotalMilliseconds, 0.001);
        var elementsProcessed = (long)count * measuredPasses;
        return new WorkloadMeasurement(
            kind, true, mismatches == 0, count, mismatches,
            Math.Round(elementsProcessed / (elapsedMs / 1000.0), 2),
            "synthetic passes elements/s (host dispatch + GPU readback, not VRAM bandwidth)",
            elapsedMs,
            $"DX12: {gpu.Name}; sustained target={TargetWindowMilliseconds}ms; " +
            $"measured passes={measuredPasses}; bounded dispatches/pass={dispatches.Count}; " +
            $"sticky intermediate-error flags + all {count:N0} final elements CPU-checked; " +
            "warmup=2 passes; not GPU timestamp timing or long-term stability.",
            HostTimedWindow: new HostTimedWindow(hostWindowStart, hostWindowEnd));
    }
}

[ThreadGroupSize(GpuDispatchPlanner.ThreadsPerGroup, 1, 1)]
[GeneratedComputeShaderDescriptor]
[SupportedOSPlatform("windows6.2")]
internal readonly partial struct RepeatAndCheckKernel(
    ReadWriteBuffer<int> data,
    ReadWriteBuffer<int> errors,
    int seed,
    int rounds,
    int baseOffset) : IComputeShader
{
    public void Execute()
    {
        int i = ThreadIds.X + baseOffset;
        int v = ((i * 37 + seed) & 0x000FFFFF);
        for (int j = 0; j < rounds; j++)
            v = ((v ^ (i + j * 17)) + 29) & 0x3FFFFFFF;
        if (data[i] != v)
            errors[i] = 1;
        data[i] = v;
    }
}
