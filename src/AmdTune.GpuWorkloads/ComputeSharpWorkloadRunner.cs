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
public sealed class ComputeSharpWorkloadRunner(IGpuDevice device) : IWorkloadRunner
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
        var sw = Stopwatch.StartNew();
        using ReadWriteBuffer<int> buffer = gpu.AllocateReadWriteBuffer(input);
        gpu.For(count, new CheckKernel(buffer, rounds));
        buffer.CopyTo(input);
        sw.Stop();
        cancellationToken.ThrowIfCancellationRequested();

        long mismatches = 0;
        for (var i = 0; i < input.Length; i++)
        {
            var expected = unchecked((i * 37 + seed) & 0x000FFFFF);
            for (var j = 0; j < rounds; j++)
                expected = ((expected ^ (i + j * 17)) + 29) & 0x3FFFFFFF;
            if (input[i] != expected) mismatches++;
        }

        var duration = Math.Max(sw.Elapsed.TotalMilliseconds, 0.001);
        return new WorkloadMeasurement(
            kind, true, mismatches == 0, count, mismatches,
            Math.Round(count / (duration / 1000.0), 2), "verified elements/s (includes transfer)",
            duration, $"DX12 adapter: {gpu.Name}; {count:N0} elements; {rounds} integer rounds; all elements checked; no ECC counter inferred.");
    }

    public static string? ExtractModel(string name)
    {
        var match = Regex.Match(name, @"\bRX\s*(\d{4})\s*(XT|GRE)?\b", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups[1].Value + match.Groups[2].Value).ToUpperInvariant() : null;
    }
}

[ThreadGroupSize(DefaultThreadGroupSizes.X)]
[GeneratedComputeShaderDescriptor]
[SupportedOSPlatform("windows6.2")]
internal readonly partial struct CheckKernel(ReadWriteBuffer<int> buffer, int rounds) : IComputeShader
{
    public void Execute()
    {
        int i = ThreadIds.X;
        int value = buffer[i];
        for (int j = 0; j < rounds; j++)
            value = ((value ^ (i + j * 17)) + 29) & 0x3FFFFFFF;
        buffer[i] = value;
    }
}
