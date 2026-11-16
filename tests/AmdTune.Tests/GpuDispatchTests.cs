using AmdTune.Core;
using AmdTune.GpuWorkloads;
using Xunit;

namespace AmdTune.Tests;

/// <summary>Run on CI without a GPU; covers the actual failure observed on RX 9070 XT.</summary>
public sealed class GpuDispatchTests
{
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1_048_576, 1)]
    [InlineData(1_048_577, 2)]
    [InlineData(4_194_304, 4)]
    [InlineData(33_554_432, 32)]
    [InlineData(33_554_433, 33)]
    public void Plan_never_exceeds_DX12_group_limit_and_covers_entire_buffer(int size, int expectedBatches)
    {
        var slices = GpuDispatchPlanner.Plan(size);
        Assert.Equal(expectedBatches, slices.Count);
        long coverage = 0;
        foreach (var slice in slices)
        {
            Assert.Equal((int)coverage, slice.Offset);
            Assert.InRange(slice.Length, 1, GpuDispatchPlanner.MaxElementsPerDispatch);
            var groups = (slice.Length + GpuDispatchPlanner.ThreadsPerGroup - 1)
                / GpuDispatchPlanner.ThreadsPerGroup;
            Assert.InRange(groups, 1, GpuDispatchPlanner.MaxGroupsX);
            coverage += slice.Length;
        }
        Assert.Equal(size, coverage);
    }

    [Fact]
    public void Dispatch_plan_rejects_zero_and_negative_lengths()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => GpuDispatchPlanner.Plan(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => GpuDispatchPlanner.Plan(-1));
    }

    [Fact]
    public void Exact_global_indexes_are_preserved_across_multiple_slices()
    {
        var slices = GpuDispatchPlanner.Plan(2_100_000);
        var distinct = new HashSet<int>();
        foreach (var slice in slices)
        {
            // Sample every transition and offset, which caught the original indexing issue.
            Assert.True(distinct.Add(slice.Offset));
            Assert.True(distinct.Add(slice.Offset + slice.Length - 1));
        }
        Assert.Equal(slices.Count * 2, distinct.Count);
        Assert.Equal(0, slices[0].Offset);
        Assert.Equal(2_099_999, slices[^1].Offset + slices[^1].Length - 1);
    }
    [Theory]
    [InlineData(WorkloadKind.Compute, 2, 16)]
    [InlineData(WorkloadKind.Memory, 2, 12)]
    [InlineData(WorkloadKind.Mixed, 2, 16)]
    public void Extended_profile_has_bounded_timed_and_warmup_passes(
        WorkloadKind kind, int warmup, int measured)
    {
        var profile = GpuExecutionProfile.For(kind, true);
        profile.Validate();
        Assert.Equal(warmup, profile.UntimedWarmupPasses);
        Assert.Equal(measured, profile.MeasuredPasses);
        Assert.InRange(profile.TotalPasses, 1, 32);
    }

    [Theory]
    [InlineData(WorkloadKind.Compute)]
    [InlineData(WorkloadKind.Memory)]
    [InlineData(WorkloadKind.Mixed)]
    public void Quick_profile_remains_single_pass_for_fast_correctness(WorkloadKind kind)
    {
        var profile = GpuExecutionProfile.For(kind, false);
        profile.Validate();
        Assert.Equal(0, profile.UntimedWarmupPasses);
        Assert.Equal(1, profile.MeasuredPasses);
    }

}
