using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;

namespace Vintagestory.API.Config;

/// <summary>
/// Structure-of-Arrays (SoA) layout for dense chunk frustum culling.
/// Maintained at the source (mesh insertion, removal, and compaction time) to ensure
/// the per-frame culling hot path performs zero heap allocations, zero pointer dereferencing,
/// and vectorizes directly across contiguous 64-element-aligned memory buffers.
/// </summary>
public sealed class PoolCullData
{
    public const int CapacityStep = 64;
    public const float FreeSlotSentinelRadius = -1e30f;
    private const float InvSqrt3 = 1f / 1.7320508f;

    public int Capacity { get; private set; }
    public float[] CenterX;
    public float[] CenterY;
    public float[] CenterZ;
    public float[] RadiusX;
    public float[] RadiusY;
    public float[] RadiusZ;
    public int[] IndexStartBytes;
    public int[] IndexCounts;
    public ushort[] CandidateIndices;

    public PoolCullData(int maxParts)
    {
        Capacity = 0;
        CenterX = Array.Empty<float>();
        CenterY = Array.Empty<float>();
        CenterZ = Array.Empty<float>();
        RadiusX = Array.Empty<float>();
        RadiusY = Array.Empty<float>();
        RadiusZ = Array.Empty<float>();
        IndexStartBytes = Array.Empty<int>();
        IndexCounts = Array.Empty<int>();
        CandidateIndices = Array.Empty<ushort>();

        EnsureCapacity(maxParts > 0 ? maxParts : CapacityStep);
    }

    public void EnsureCapacity(int required)
    {
        int target = (required + CapacityStep - 1) & ~(CapacityStep - 1);
        if (target < CapacityStep) target = CapacityStep;
        if (target <= Capacity) return;

        int oldCapacity = Capacity;
        Capacity = target;

        Array.Resize(ref CenterX, target);
        Array.Resize(ref CenterY, target);
        Array.Resize(ref CenterZ, target);
        Array.Resize(ref RadiusX, target);
        Array.Resize(ref RadiusY, target);
        Array.Resize(ref RadiusZ, target);
        Array.Resize(ref IndexStartBytes, target);
        Array.Resize(ref IndexCounts, target);
        Array.Resize(ref CandidateIndices, target);

        // Pre-fill newly allocated slots with sentinel negative radius at the source
        for (int i = oldCapacity; i < target; i++)
        {
            RadiusX[i] = FreeSlotSentinelRadius;
            RadiusY[i] = FreeSlotSentinelRadius;
            RadiusZ[i] = FreeSlotSentinelRadius;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetSlot(int index, float x, float y, float z, float rx, float ry, float rz, int indexStartByte, int indexCount)
    {
        CenterX[index] = x;
        CenterY[index] = y;
        CenterZ[index] = z;
        RadiusX[index] = rx;
        RadiusY[index] = ry;
        RadiusZ[index] = rz;
        IndexStartBytes[index] = indexStartByte;
        IndexCounts[index] = indexCount;
    }

    public void InsertSlot(int index, int currentCount, float x, float y, float z, float rx, float ry, float rz, int indexStartByte, int indexCount)
    {
        EnsureCapacity(currentCount + 1);
        int moveCount = currentCount - index;
        if (moveCount > 0)
        {
            Array.Copy(CenterX, index, CenterX, index + 1, moveCount);
            Array.Copy(CenterY, index, CenterY, index + 1, moveCount);
            Array.Copy(CenterZ, index, CenterZ, index + 1, moveCount);
            Array.Copy(RadiusX, index, RadiusX, index + 1, moveCount);
            Array.Copy(RadiusY, index, RadiusY, index + 1, moveCount);
            Array.Copy(RadiusZ, index, RadiusZ, index + 1, moveCount);
            Array.Copy(IndexStartBytes, index, IndexStartBytes, index + 1, moveCount);
            Array.Copy(IndexCounts, index, IndexCounts, index + 1, moveCount);
        }
        SetSlot(index, x, y, z, rx, ry, rz, indexStartByte, indexCount);
    }

    public void RemoveSlot(int index, int newCount)
    {
        int moveCount = newCount - index;
        if (moveCount > 0)
        {
            Array.Copy(CenterX, index + 1, CenterX, index, moveCount);
            Array.Copy(CenterY, index + 1, CenterY, index, moveCount);
            Array.Copy(CenterZ, index + 1, CenterZ, index, moveCount);
            Array.Copy(RadiusX, index + 1, RadiusX, index, moveCount);
            Array.Copy(RadiusY, index + 1, RadiusY, index, moveCount);
            Array.Copy(RadiusZ, index + 1, RadiusZ, index, moveCount);
            Array.Copy(IndexStartBytes, index + 1, IndexStartBytes, index, moveCount);
            Array.Copy(IndexCounts, index + 1, IndexCounts, index, moveCount);
        }
        RadiusX[newCount] = FreeSlotSentinelRadius;
        RadiusY[newCount] = FreeSlotSentinelRadius;
        RadiusZ[newCount] = FreeSlotSentinelRadius;
        IndexCounts[newCount] = 0;
    }

    public void Clear()
    {
        for (int i = 0; i < Capacity; i++)
        {
            RadiusX[i] = FreeSlotSentinelRadius;
            RadiusY[i] = FreeSlotSentinelRadius;
            RadiusZ[i] = FreeSlotSentinelRadius;
            IndexCounts[i] = 0;
        }
    }

    /// <summary>
    /// Evaluates bounding planes in dense SIMD batches across Structure-of-Arrays buffers.
    /// Populates CandidateIndices with surviving slot indices and returns candidate count.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int CullPlanesSimd(Plane[] planes, int planeCount, int count)
    {
        if (count <= 0 || planeCount <= 0) return 0;

        if (Vector256.IsHardwareAccelerated && OptimumConfig.EffectiveSimdCulling && count >= 8)
        {
            return CullPlanesAvx2(planes, planeCount, count);
        }
        if (Vector128.IsHardwareAccelerated && OptimumConfig.EffectiveSimdCulling && count >= 4)
        {
            return CullPlanesVector128(planes, planeCount, count);
        }
        return CullPlanesScalar(planes, planeCount, count);
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int CullPlanesAvx2(Plane[] planes, int planeCount, int count)
    {
        int candidateCount = 0;

        Span<Vector256<float>> pNx = stackalloc Vector256<float>[6];
        Span<Vector256<float>> pNy = stackalloc Vector256<float>[6];
        Span<Vector256<float>> pNz = stackalloc Vector256<float>[6];
        Span<Vector256<float>> pD  = stackalloc Vector256<float>[6];
        Span<Vector256<float>> pAnx = stackalloc Vector256<float>[6];
        Span<Vector256<float>> pAny = stackalloc Vector256<float>[6];
        Span<Vector256<float>> pAnz = stackalloc Vector256<float>[6];

        for (int p = 0; p < planeCount; p++)
        {
            float nx = (float)planes[p].normalX;
            float ny = (float)planes[p].normalY;
            float nz = (float)planes[p].normalZ;
            float d  = (float)planes[p].D;

            pNx[p] = Vector256.Create(nx);
            pNy[p] = Vector256.Create(ny);
            pNz[p] = Vector256.Create(nz);
            pD[p]  = Vector256.Create(d);

            pAnx[p] = Vector256.Create(MathF.Abs(nx) * InvSqrt3);
            pAny[p] = Vector256.Create(MathF.Abs(ny) * InvSqrt3);
            pAnz[p] = Vector256.Create(MathF.Abs(nz) * InvSqrt3);
        }

        int vectorBatches = count / 8;
        int vectorEnd = vectorBatches * 8;

        for (int i = 0; i < vectorEnd; i += 8)
        {
            var cx = Vector256.LoadUnsafe(ref CenterX[i]);
            var cy = Vector256.LoadUnsafe(ref CenterY[i]);
            var cz = Vector256.LoadUnsafe(ref CenterZ[i]);
            var rx = Vector256.LoadUnsafe(ref RadiusX[i]);
            var ry = Vector256.LoadUnsafe(ref RadiusY[i]);
            var rz = Vector256.LoadUnsafe(ref RadiusZ[i]);

            uint activeMask = 0xFF;

            for (int p = 0; p < planeCount; p++)
            {
                Vector256<float> dist = Fma.IsSupported
                    ? Fma.MultiplyAdd(pNx[p], cx, Fma.MultiplyAdd(pNy[p], cy, Fma.MultiplyAdd(pNz[p], cz, pD[p])))
                    : pNx[p] * cx + pNy[p] * cy + pNz[p] * cz + pD[p];

                Vector256<float> extent = Fma.IsSupported
                    ? Fma.MultiplyAdd(pAnx[p], rx, Fma.MultiplyAdd(pAny[p], ry, pAnz[p] * rz))
                    : pAnx[p] * rx + pAny[p] * ry + pAnz[p] * rz;

                Vector256<float> val = dist + extent;
                Vector256<float> inside = Vector256.GreaterThanOrEqual(val, Vector256<float>.Zero);
                activeMask &= (uint)inside.ExtractMostSignificantBits();

                if (activeMask == 0) break;
            }

            while (activeMask != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(activeMask);
                CandidateIndices[candidateCount++] = (ushort)(i + bit);
                activeMask &= activeMask - 1;
            }
        }

        for (int i = vectorEnd; i < count; i++)
        {
            if (RadiusX[i] < 0) continue;
            float cx = CenterX[i], cy = CenterY[i], cz = CenterZ[i];
            float rx = RadiusX[i], ry = RadiusY[i], rz = RadiusZ[i];

            bool outside = false;
            for (int p = 0; p < planeCount; p++)
            {
                float testX = cx + (planes[p].normalX > 0 ? 1 : -1) * rx * InvSqrt3;
                float testY = cy + (planes[p].normalY > 0 ? 1 : -1) * ry * InvSqrt3;
                float testZ = cz + (planes[p].normalZ > 0 ? 1 : -1) * rz * InvSqrt3;

                if (testX * (float)planes[p].normalX + testY * (float)planes[p].normalY + testZ * (float)planes[p].normalZ + (float)planes[p].D < 0)
                {
                    outside = true;
                    break;
                }
            }

            if (!outside)
            {
                CandidateIndices[candidateCount++] = (ushort)i;
            }
        }

        return candidateCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int CullPlanesVector128(Plane[] planes, int planeCount, int count)
    {
        int candidateCount = 0;

        Span<Vector128<float>> pNx = stackalloc Vector128<float>[6];
        Span<Vector128<float>> pNy = stackalloc Vector128<float>[6];
        Span<Vector128<float>> pNz = stackalloc Vector128<float>[6];
        Span<Vector128<float>> pD  = stackalloc Vector128<float>[6];
        Span<Vector128<float>> pAnx = stackalloc Vector128<float>[6];
        Span<Vector128<float>> pAny = stackalloc Vector128<float>[6];
        Span<Vector128<float>> pAnz = stackalloc Vector128<float>[6];

        for (int p = 0; p < planeCount; p++)
        {
            float nx = (float)planes[p].normalX;
            float ny = (float)planes[p].normalY;
            float nz = (float)planes[p].normalZ;
            float d  = (float)planes[p].D;

            pNx[p] = Vector128.Create(nx);
            pNy[p] = Vector128.Create(ny);
            pNz[p] = Vector128.Create(nz);
            pD[p]  = Vector128.Create(d);

            pAnx[p] = Vector128.Create(MathF.Abs(nx) * InvSqrt3);
            pAny[p] = Vector128.Create(MathF.Abs(ny) * InvSqrt3);
            pAnz[p] = Vector128.Create(MathF.Abs(nz) * InvSqrt3);
        }

        int vectorBatches = count / 4;
        int vectorEnd = vectorBatches * 4;

        for (int i = 0; i < vectorEnd; i += 4)
        {
            var cx = Vector128.LoadUnsafe(ref CenterX[i]);
            var cy = Vector128.LoadUnsafe(ref CenterY[i]);
            var cz = Vector128.LoadUnsafe(ref CenterZ[i]);
            var rx = Vector128.LoadUnsafe(ref RadiusX[i]);
            var ry = Vector128.LoadUnsafe(ref RadiusY[i]);
            var rz = Vector128.LoadUnsafe(ref RadiusZ[i]);

            uint activeMask = 0x0F;

            for (int p = 0; p < planeCount; p++)
            {
                Vector128<float> dist = pNx[p] * cx + pNy[p] * cy + pNz[p] * cz + pD[p];
                Vector128<float> extent = pAnx[p] * rx + pAny[p] * ry + pAnz[p] * rz;

                Vector128<float> val = dist + extent;
                Vector128<float> inside = Vector128.GreaterThanOrEqual(val, Vector128<float>.Zero);
                activeMask &= (uint)inside.ExtractMostSignificantBits();

                if (activeMask == 0) break;
            }

            while (activeMask != 0)
            {
                int bit = System.Numerics.BitOperations.TrailingZeroCount(activeMask);
                CandidateIndices[candidateCount++] = (ushort)(i + bit);
                activeMask &= activeMask - 1;
            }
        }

        for (int i = vectorEnd; i < count; i++)
        {
            if (RadiusX[i] < 0) continue;
            float cx = CenterX[i], cy = CenterY[i], cz = CenterZ[i];
            float rx = RadiusX[i], ry = RadiusY[i], rz = RadiusZ[i];

            bool outside = false;
            for (int p = 0; p < planeCount; p++)
            {
                float testX = cx + (planes[p].normalX > 0 ? 1 : -1) * rx * InvSqrt3;
                float testY = cy + (planes[p].normalY > 0 ? 1 : -1) * ry * InvSqrt3;
                float testZ = cz + (planes[p].normalZ > 0 ? 1 : -1) * rz * InvSqrt3;

                if (testX * (float)planes[p].normalX + testY * (float)planes[p].normalY + testZ * (float)planes[p].normalZ + (float)planes[p].D < 0)
                {
                    outside = true;
                    break;
                }
            }

            if (!outside)
            {
                CandidateIndices[candidateCount++] = (ushort)i;
            }
        }

        return candidateCount;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    public int CullPlanesScalar(Plane[] planes, int planeCount, int count)
    {
        int candidateCount = 0;

        for (int i = 0; i < count; i++)
        {
            if (RadiusX[i] < 0) continue;
            float cx = CenterX[i], cy = CenterY[i], cz = CenterZ[i];
            float rx = RadiusX[i], ry = RadiusY[i], rz = RadiusZ[i];

            bool outside = false;
            for (int p = 0; p < planeCount; p++)
            {
                float testX = cx + (planes[p].normalX > 0 ? 1 : -1) * rx * InvSqrt3;
                float testY = cy + (planes[p].normalY > 0 ? 1 : -1) * ry * InvSqrt3;
                float testZ = cz + (planes[p].normalZ > 0 ? 1 : -1) * rz * InvSqrt3;

                if (testX * (float)planes[p].normalX + testY * (float)planes[p].normalY + testZ * (float)planes[p].normalZ + (float)planes[p].D < 0)
                {
                    outside = true;
                    break;
                }
            }

            if (!outside)
            {
                CandidateIndices[candidateCount++] = (ushort)i;
            }
        }

        return candidateCount;
    }
}
