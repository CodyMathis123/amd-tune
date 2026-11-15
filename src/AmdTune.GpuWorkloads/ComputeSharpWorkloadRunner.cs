using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using AmdTune.Core;
using ComputeSharp;

namespace AmdTune.GpuWorkloads;

/// <summary>
/// Deterministic integer correctness across entire buffers using ComputeSharp/DX12.
/// Score is end-to-end throughput INCLUDING transfer time, never claimed GDDR6 bandwidth.
/// </summary>
public sealed class ComputeSharpWorkloadRunner(IGpuDevice device, bool extended = false) : IWorkloadRunner
{
    public async Task<WorkloadMeasurement> RunAsync(WorkloadKind kind, int seed, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(6, 2))
            throw new PlatformNotSupportedException("The DX12 workloads require Windows.");
        var caps = await device.DiscoverAsync(cancellationToken);
        var gpu = GraphicsDevice.GetDefault();
        if (!gpu.IsHardwareAccelerated)
            throw new HardwareException("ComputeSharp selected software WARP. Refusing to report GPU results.");
        var intendedModel = ExtractModel(caps.Name);
        var actualModel = ExtractModel(gpu.Name);
        if (intendedModel is null || actualModel is null || intendedModel != actualModel)
            throw new HardwareException($"GPU adapter identity uncertain: ADLX={caps.Name}, DX12={gpu.Name}. Refusing to benchmark another GPU.");

        // Memory tests must exceed 64MB Infinity Cache. Avoid excessive allocation on small GPUs.
        var count = kind switch
        {
            WorkloadKind.Compute => 1_048_576,
            WorkloadKind.Memory => 33_554_432, // 128 MiB integer buffer
            WorkloadKind.Mixed => 4_194_304,
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var rounds = kind switch
        {
            WorkloadKind.Compute => 64,
            WorkloadKind.Memory => 1,
            _ => 16
        };
        var input = new int[count];
        for (var i = 0; i < count; i++)
            input[i] = unchecked((i * 37 + seed) & 0x000FFFFF);

        cancellationToken.ThrowIfCancellationRequested();
        var profile = GpuExecutionProfile.For(kind, extended);
        profile.Validate();
        // Preserve the original quick-profile timing (allocation + dispatch +
        // readback) so older single-pass reports remain roughly comparable.
        // Extended benchmarks reset this timer after allocating and warming up.
        var sw = Stopwatch.StartNew();
        using ReadWriteBuffer<int> buffer = gpu.AllocateReadWriteBuffer(input);
        var dispatches = GpuDispatchPlanner.Plan(count);

        void DispatchPass()
        {
            // A 128 MiB buffer is processed in 32 bounded dispatches. Repeating
            // passes does NOT increase the size of any individual GPU dispatch.
            foreach (var slice in dispatches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                gpu.For(slice.Length, new CheckKernel(buffer, rounds, slice.Offset));
            }
        }

        // Untimed GPU warmup raises device clock state without inflating scores.
        // Warmup modifies the buffer: CPU verification includes ALL passes.
        for (var pass = 0; pass < profile.UntimedWarmupPasses; pass++)
            DispatchPass();

        if (extended) sw.Restart();
        for (var pass = 0; pass < profile.MeasuredPasses; pass++)
            DispatchPass();
        buffer.CopyTo(input);
        sw.Stop();
        cancellationToken.ThrowIfCancellationRequested();

        // Check every output element after all warmup and timed passes.
        // Intermediate states are not checked individually, so this is
        // final-state correctness evidence, not an ECC error counter.
        long mismatches = 0;
        for (var i = 0; i < input.Length; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            var expected = unchecked((i * 37 + seed) & 0x000FFFFF);
            for (var pass = 0; pass < profile.TotalPasses; pass++)
                for (var j = 0; j < rounds; j++)
                    expected = ((expected ^ (i + j * 17)) + 29) & 0x3FFFFFFF;
            if (input[i] != expected) mismatches++;
        }

        var duration = Math.Max(sw.Elapsed.TotalMilliseconds, 0.001);
        var processedElements = checked((long)count * profile.MeasuredPasses);
        return new WorkloadMeasurement(
            kind, true, mismatches == 0, count, mismatches,
            Math.Round(processedElements / (duration / 1000.0), 2),
            extended
                ? "processed elements/s (dispatch + readback; excludes warmup and CPU verification)"
                : "processed elements/s (allocation + dispatch + readback; excludes CPU verification)",
            duration, $"DX12 adapter: {gpu.Name}; {count:N0} outputs validated; " +
                $"{dispatches.Count} bounded dispatches/pass; warmup passes={profile.UntimedWarmupPasses}, " +
                $"measured passes={profile.MeasuredPasses}, total passes={profile.TotalPasses}; " +
                $"{rounds} integer rounds/pass; every final output checked; no ECC counter inferred.");
    }

    public static string? ExtractModel(string name)
    {
        var match = Regex.Match(name, @"\bRX\s*(\d{4})\s*(XT|GRE)?\b", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups[1].Value + match.Groups[2].Value).ToUpperInvariant() : null;
    }
}

[ThreadGroupSize(GpuDispatchPlanner.ThreadsPerGroup, 1, 1)]
[GeneratedComputeShaderDescriptor]
[SupportedOSPlatform("windows6.2")]
internal readonly partial struct CheckKernel(ReadWriteBuffer<int> buffer, int rounds, int baseOffset) : IComputeShader
{
    public void Execute()
    {
        int i = ThreadIds.X + baseOffset;
        int value = buffer[i];
        for (int j = 0; j < rounds; j++)
            value = ((value ^ (i + j * 17)) + 29) & 0x3FFFFFFF;
        buffer[i] = value;
    }
}
