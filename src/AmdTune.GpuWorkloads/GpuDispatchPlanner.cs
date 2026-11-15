namespace AmdTune.GpuWorkloads;

/// <summary>A contiguous region of the GPU buffer with a separate 1D dispatch.</summary>
public readonly record struct DispatchSlice(int Offset, int Length);

/// <summary>Portable DX12 dispatch planning with no hardware dependencies.</summary>
public static class GpuDispatchPlanner
{
    public const int ThreadsPerGroup = 64;
    public const int MaxGroupsX = 65_535;
    public const int MaxElementsPerDispatch = 1_048_576; // 16,384 groups at 64 threads/group

    public static IReadOnlyList<DispatchSlice> Plan(int elements)
    {
        if (elements <= 0) throw new ArgumentOutOfRangeException(nameof(elements));
        if ((MaxElementsPerDispatch + ThreadsPerGroup - 1L) / ThreadsPerGroup > MaxGroupsX)
            throw new InvalidOperationException("Dispatch configuration exceeds the portable DX12 limit.");
        var output = new List<DispatchSlice>((int)(((long)elements + MaxElementsPerDispatch - 1) / MaxElementsPerDispatch));
        for (int offset = 0; offset < elements;)
        {
            int length = Math.Min(MaxElementsPerDispatch, elements - offset);
            output.Add(new DispatchSlice(offset, length));
            offset += length;
        }
        return output;
    }
}
