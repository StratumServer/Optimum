using System;
using Vintagestory.API.Client;

namespace Optimum;

internal sealed class EntityLightBatchBuffer
{
    internal IOptimumEntityLightSampler[] Samplers = Array.Empty<IOptimumEntityLightSampler>();
    internal int[] SampleIndexes = Array.Empty<int>();
    internal int[] X = Array.Empty<int>();
    internal int[] Y = Array.Empty<int>();
    internal int[] Z = Array.Empty<int>();
    internal int[] LocalIndices = Array.Empty<int>();
    internal int[] Order = Array.Empty<int>();
    internal long[] ChunkKeys = Array.Empty<long>();
    internal ushort[] PackedLights = Array.Empty<ushort>();
    internal byte[] Saturations = Array.Empty<byte>();
    internal int BatchId;
    internal bool FailureLogged;

    internal int NextBatchId()
    {
        BatchId++;
        if (BatchId == 0)
        {
            BatchId = 1;
        }
        return BatchId;
    }

    internal void EnsureCapacity(int capacity)
    {
        if (Samplers.Length >= capacity)
        {
            return;
        }

        int newLength = Math.Max(capacity, Samplers.Length == 0 ? 16 : Samplers.Length * 2);
        Array.Resize(ref Samplers, newLength);
        Array.Resize(ref SampleIndexes, newLength);
        Array.Resize(ref X, newLength);
        Array.Resize(ref Y, newLength);
        Array.Resize(ref Z, newLength);
        Array.Resize(ref LocalIndices, newLength);
        Array.Resize(ref Order, newLength);
        Array.Resize(ref ChunkKeys, newLength);
        Array.Resize(ref PackedLights, newLength);
        Array.Resize(ref Saturations, newLength);
    }

    internal void ClearSamplers(int count)
    {
        Array.Clear(Samplers, 0, count);
    }
}
