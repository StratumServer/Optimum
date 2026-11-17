using System;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Vintagestory.API.Config;

/// <summary>
/// Issue #607: Groups quad geometry by cardinal face normal into 7 contiguous buckets inside
/// chunk <see cref="MeshData"/> (<c>[Down, North, East, South, West, Up, Unaligned]</c>) so CPU
/// range culling omits backfacing cardinal buckets relative to the active camera before multi-draw
/// or indirect submission without reordering shared SSBO quad indices.
/// </summary>
public static class OptimumChunkFaceBuckets
{
    public const int BucketCount = 7;

    // Physical bucket order minimizes MultiDraw span splits above terrain by placing
    // the four horizontal normals adjacent in clockwise order (N,E,S,W) and placing Up
    // directly before Unaligned so upper terrain faces coalesce with unaligned geometry.
    public const int SlotDown = 0;
    public const int SlotNorth = 1;
    public const int SlotEast = 2;
    public const int SlotSouth = 3;
    public const int SlotWest = 4;
    public const int SlotUp = 5;
    public const int SlotUnaligned = 6;

    private const int DimensionYOffset = 32768;
    private const double WorldBoundsPadding = 0.05;
    private const float PlaneTolerance = 1e-3f;
    private const float CrossAxisToleranceRatio = 1e-4f;
    private const float MinAreaThreshold = 1e-6f;

    private static readonly int NormalNorthFlags = BlockFacing.NORTH.NormalPackedFlags;
    private static readonly int NormalEastFlags = BlockFacing.EAST.NormalPackedFlags;
    private static readonly int NormalSouthFlags = BlockFacing.SOUTH.NormalPackedFlags;
    private static readonly int NormalWestFlags = BlockFacing.WEST.NormalPackedFlags;
    private static readonly int NormalUpFlags = BlockFacing.UP.NormalPackedFlags;
    private static readonly int NormalDownFlags = BlockFacing.DOWN.NormalPackedFlags;

    public sealed class MeshBucketDescriptor
    {
        public readonly float LocalMinX;
        public readonly float LocalMinY;
        public readonly float LocalMinZ;
        public readonly float LocalMaxX;
        public readonly float LocalMaxY;
        public readonly float LocalMaxZ;
        public readonly int[] IndexOffsets;
        public readonly int[] IndexCounts;

        public MeshBucketDescriptor(
            float localMinX, float localMinY, float localMinZ,
            float localMaxX, float localMaxY, float localMaxZ,
            int[] indexOffsets, int[] indexCounts)
        {
            LocalMinX = localMinX;
            LocalMinY = localMinY;
            LocalMinZ = localMinZ;
            LocalMaxX = localMaxX;
            LocalMaxY = localMaxY;
            LocalMaxZ = localMaxZ;
            IndexOffsets = indexOffsets;
            IndexCounts = indexCounts;
        }
    }

    public sealed class LocationBucketData
    {
        public readonly double MinX;
        public readonly double MinY;
        public readonly double MinZ;
        public readonly double MaxX;
        public readonly double MaxY;
        public readonly double MaxZ;
        public readonly int[] AbsStartBytes;
        public readonly int[] IndexCounts;
        public readonly int TotalIndices;

        public LocationBucketData(
            double minX, double minY, double minZ,
            double maxX, double maxY, double maxZ,
            int[] absStartBytes, int[] indexCounts, int totalIndices)
        {
            MinX = minX;
            MinY = minY;
            MinZ = minZ;
            MaxX = maxX;
            MaxY = maxY;
            MaxZ = maxZ;
            AbsStartBytes = absStartBytes;
            IndexCounts = indexCounts;
            TotalIndices = totalIndices;
        }
    }

    private static readonly ConditionalWeakTable<MeshData, MeshBucketDescriptor> PendingDescriptors = new();
    private static readonly ConditionalWeakTable<ModelDataPoolLocation, LocationBucketData> LocationBuckets = new();

    [ThreadStatic] private static bool t_opaquePassActive;
    [ThreadStatic] private static double t_camX;
    [ThreadStatic] private static double t_camY;
    [ThreadStatic] private static double t_camZ;

    [ThreadStatic] private static byte[]? t_quadSlots;
    [ThreadStatic] private static float[]? t_xyzScratch;
    [ThreadStatic] private static float[]? t_uvScratch;
    [ThreadStatic] private static byte[]? t_rgbaScratch;
    [ThreadStatic] private static int[]? t_flagsScratch;
    [ThreadStatic] private static int[]? t_customIntsScratch;
    [ThreadStatic] private static short[]? t_customShortsScratch;
    [ThreadStatic] private static float[]? t_customFloatsScratch;
    [ThreadStatic] private static byte[]? t_customBytesScratch;

    public static bool IsSupportedRenderPass(EnumChunkRenderPass pass)
    {
        return pass == EnumChunkRenderPass.Opaque || pass == EnumChunkRenderPass.TopSoil;
    }

    public static bool IsOpaqueFaceCullActive =>
        t_opaquePassActive && OptimumConfig.ChunkFaceBucketCullEnabled;

    public static void BeginOpaquePass(Vec3d? camEyePos, bool warpActive)
    {
        if (OptimumConfig.ChunkFaceBucketCullEnabled &&
            !warpActive &&
            camEyePos != null &&
            double.IsFinite(camEyePos.X) &&
            double.IsFinite(camEyePos.Y) &&
            double.IsFinite(camEyePos.Z))
        {
            t_camX = camEyePos.X;
            t_camY = camEyePos.Y;
            t_camZ = camEyePos.Z;
            t_opaquePassActive = true;
        }
        else
        {
            t_opaquePassActive = false;
        }
    }

    public static void BeginOpaquePass(double camX, double camY, double camZ, bool warpActive)
    {
        if (OptimumConfig.ChunkFaceBucketCullEnabled &&
            !warpActive &&
            double.IsFinite(camX) &&
            double.IsFinite(camY) &&
            double.IsFinite(camZ))
        {
            t_camX = camX;
            t_camY = camY;
            t_camZ = camZ;
            t_opaquePassActive = true;
        }
        else
        {
            t_opaquePassActive = false;
        }
    }

    public static void EndOpaquePass()
    {
        t_opaquePassActive = false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool TryGetActiveCullCamera(int poolDimensionId, out double camX, out double camY, out double camZ)
    {
        if (!IsOpaqueFaceCullActive || poolDimensionId == Dimensions.MiniDimensions)
        {
            camX = 0d;
            camY = 0d;
            camZ = 0d;
            return false;
        }

        camX = t_camX;
        camY = t_camY;
        camZ = t_camZ;
        return true;
    }

    /// <summary>
    /// Buckets pure quad chunk geometry by cardinal normal and records local bounds + slot ranges
    /// for <see cref="BindLocation"/>. Leaves non-quad or incompatible custom-buffer meshes untouched.
    /// </summary>
    public static void BucketAndAttach(MeshData? mesh, EnumChunkRenderPass pass)
    {
        if (!OptimumConfig.ChunkFaceBucketCullEnabled || !IsSupportedRenderPass(pass) || mesh == null)
        {
            return;
        }

        int vertCount = mesh.VerticesCount;
        int idxCount = mesh.IndicesCount;
        if (vertCount < 4 || (vertCount & 3) != 0)
        {
            return;
        }

        int quadCount = vertCount >> 2;
        if (idxCount != quadCount * 6)
        {
            return;
        }

        // Preserve optional per-vertex arrays only when populated.
        if ((mesh.Normals != null && mesh.NormalsCount > 0) ||
            (mesh.TextureIndices != null && mesh.TextureIndicesCount > 0))
        {
            return;
        }

        float[]? xyz = mesh.xyz;
        float[]? uv = mesh.Uv;
        byte[]? rgba = mesh.Rgba;
        int[]? flags = mesh.Flags;
        int[]? indices = mesh.Indices;
        if (xyz == null || uv == null || rgba == null || flags == null || indices == null ||
            xyz.Length < vertCount * 3 || uv.Length < vertCount * 2 ||
            rgba.Length < vertCount * 4 || flags.Length < vertCount || indices.Length < idxCount)
        {
            return;
        }

        int intStride = (mesh.CustomInts != null && mesh.CustomInts.Count > 0) ? (mesh.CustomInts.Count / vertCount) : 0;
        int shortStride = (mesh.CustomShorts != null && mesh.CustomShorts.Count > 0) ? (mesh.CustomShorts.Count / vertCount) : 0;
        int floatStride = (mesh.CustomFloats != null && mesh.CustomFloats.Count > 0) ? (mesh.CustomFloats.Count / vertCount) : 0;
        int byteStride = (mesh.CustomBytes != null && mesh.CustomBytes.Count > 0) ? (mesh.CustomBytes.Count / vertCount) : 0;

        if ((mesh.CustomInts != null && mesh.CustomInts.Count > 0 && mesh.CustomInts.Count != vertCount * intStride) ||
            (mesh.CustomShorts != null && mesh.CustomShorts.Count > 0 && mesh.CustomShorts.Count != vertCount * shortStride) ||
            (mesh.CustomFloats != null && mesh.CustomFloats.Count > 0 && mesh.CustomFloats.Count != vertCount * floatStride) ||
            (mesh.CustomBytes != null && mesh.CustomBytes.Count > 0 && mesh.CustomBytes.Count != vertCount * byteStride))
        {
            return;
        }

        byte[] quadSlots = EnsureQuadSlotsScratch(quadCount);
        Span<int> counts = stackalloc int[BucketCount];
        counts.Clear();

        float minX = float.PositiveInfinity, minY = float.PositiveInfinity, minZ = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity, maxZ = float.NegativeInfinity;

        int prevSlot = -1;
        int runTransitions = 0;

        for (int q = 0; q < quadCount; q++)
        {
            int v0 = q << 2;
            int ii = q * 6;
            if (indices[ii] != v0 || indices[ii + 1] != v0 + 1 || indices[ii + 2] != v0 + 2 ||
                indices[ii + 3] != v0 || indices[ii + 4] != v0 + 2 || indices[ii + 5] != v0 + 3)
            {
                return;
            }

            int b0 = v0 * 3;
            for (int v = 0; v < 4; v++)
            {
                int vi = b0 + v * 3;
                float vx = xyz[vi];
                float vy = xyz[vi + 1];
                float vz = xyz[vi + 2];
                if (!float.IsFinite(vx) || !float.IsFinite(vy) || !float.IsFinite(vz))
                {
                    return;
                }
                if (vx < minX) minX = vx;
                if (vx > maxX) maxX = vx;
                if (vy < minY) minY = vy;
                if (vy > maxY) maxY = vy;
                if (vz < minZ) minZ = vz;
                if (vz > maxZ) maxZ = vz;
            }

            int slot = ClassifyQuadSlot(xyz, flags, v0);
            quadSlots[q] = (byte)slot;
            counts[slot]++;

            if (slot != prevSlot)
            {
                runTransitions++;
                prevSlot = slot;
            }
        }

        // Compute bucket index ranges in physical slot order [0..6].
        var slotIndexOffsets = new int[BucketCount];
        var slotIndexCounts = new int[BucketCount];
        var writeVertexCursor = new int[BucketCount];
        int runningQuads = 0;
        int nonEmptySlots = 0;
        bool alreadyInOrder = runTransitions <= BucketCount;

        int lastNonEmptySlot = -1;
        for (int s = 0; s < BucketCount; s++)
        {
            int c = counts[s];
            slotIndexOffsets[s] = runningQuads * 6;
            slotIndexCounts[s] = c * 6;
            writeVertexCursor[s] = runningQuads << 2;
            runningQuads += c;
            if (c > 0)
            {
                nonEmptySlots++;
                if (s < lastNonEmptySlot)
                {
                    alreadyInOrder = false;
                }
                lastNonEmptySlot = s;
            }
        }

        if (alreadyInOrder)
        {
            // Check strictly monotonic non-decreasing slot assignment when transitions == nonEmptySlots.
            alreadyInOrder = (runTransitions == nonEmptySlots);
        }

        if (!alreadyInOrder && nonEmptySlots > 1)
        {
            ReorderQuadsIntoBuckets(
                mesh,
                quadCount,
                vertCount,
                quadSlots,
                writeVertexCursor,
                intStride,
                shortStride,
                floatStride,
                byteStride);
        }

        PendingDescriptors.AddOrUpdate(
            mesh,
            new MeshBucketDescriptor(minX, minY, minZ, maxX, maxY, maxZ, slotIndexOffsets, slotIndexCounts));
    }

    /// <summary>
    /// Attaches world-space bounds and absolute pool byte offsets to <paramref name="location"/>
    /// once <see cref="MeshDataPool.TryAdd"/> succeeds, then drops the transient <see cref="MeshData"/> entry.
    /// </summary>
    public static void BindLocation(ModelDataPoolLocation? location, MeshData? mesh, Vec3i? modelOrigin, int dimension)
    {
        if (mesh == null)
        {
            return;
        }

        if (!PendingDescriptors.TryGetValue(mesh, out MeshBucketDescriptor? desc))
        {
            return;
        }

        PendingDescriptors.Remove(mesh);

        if (location == null || modelOrigin == null || dimension == Dimensions.MiniDimensions)
        {
            return;
        }

        int totalIndices = location.IndicesEnd - location.IndicesStart;
        if (totalIndices <= 0)
        {
            return;
        }

        var absStartBytes = new int[BucketCount];
        var indexCounts = new int[BucketCount];
        for (int s = 0; s < BucketCount; s++)
        {
            absStartBytes[s] = (location.IndicesStart + desc.IndexOffsets[s]) * 4;
            indexCounts[s] = desc.IndexCounts[s];
        }

        double dimYOffset = (double)dimension * DimensionYOffset;
        var locData = new LocationBucketData(
            modelOrigin.X + desc.LocalMinX - WorldBoundsPadding,
            modelOrigin.Y + dimYOffset + desc.LocalMinY - WorldBoundsPadding,
            modelOrigin.Z + desc.LocalMinZ - WorldBoundsPadding,
            modelOrigin.X + desc.LocalMaxX + WorldBoundsPadding,
            modelOrigin.Y + dimYOffset + desc.LocalMaxY + WorldBoundsPadding,
            modelOrigin.Z + desc.LocalMaxZ + WorldBoundsPadding,
            absStartBytes,
            indexCounts,
            totalIndices);

        LocationBuckets.AddOrUpdate(location, locData);
    }

    public static bool TryGetLocationBuckets(ModelDataPoolLocation location, out LocationBucketData? data)
    {
        return LocationBuckets.TryGetValue(location, out data);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ComputeKeepMask(LocationBucketData loc, double camX, double camY, double camZ)
    {
        int mask = 1 << SlotUnaligned;
        if (camY < loc.MaxY) mask |= 1 << SlotDown;
        if (camZ < loc.MaxZ) mask |= 1 << SlotNorth;
        if (camX > loc.MinX) mask |= 1 << SlotEast;
        if (camZ > loc.MinZ) mask |= 1 << SlotSouth;
        if (camX < loc.MaxX) mask |= 1 << SlotWest;
        if (camY > loc.MinY) mask |= 1 << SlotUp;
        return mask;
    }

    /// <summary>
    /// Emits 0..4 coalesced directional spans for <paramref name="location"/> into
    /// <paramref name="pool"/>, dynamically growing <c>indicesStartsByte</c> and <c>indicesSizes</c>
    /// if split chunks push group count past initial <c>MaxPartsPerPool</c>.
    /// </summary>
    public static void EmitLocationSpans(
        MeshDataPool pool,
        ModelDataPoolLocation location,
        bool bucketCullActive,
        double camX,
        double camY,
        double camZ,
        ref int groupsCount,
        ref int renderedTris,
        ref long keptTrisAcc,
        ref long culledTrisAcc)
    {
        int fullSize = location.IndicesEnd - location.IndicesStart;
        if (fullSize <= 0)
        {
            return;
        }

        if (!bucketCullActive || !LocationBuckets.TryGetValue(location, out LocationBucketData? loc))
        {
            EnsurePoolSpanCapacity(pool, groupsCount + 1);
            pool.indicesStartsByte[groupsCount * 2] = location.IndicesStart * 4;
            pool.indicesSizes[groupsCount] = fullSize;
            groupsCount++;
            renderedTris += fullSize / 3;
            return;
        }

        EnsurePoolSpanCapacity(pool, groupsCount + 4);
        int[] startsByte = pool.indicesStartsByte;
        int[] sizes = pool.indicesSizes;
        int keepMask = ComputeKeepMask(loc, camX, camY, camZ);
        int[] absStarts = loc.AbsStartBytes;
        int[] bCounts = loc.IndexCounts;

        int runStartByte = 0;
        int runCount = 0;
        int keptIndices = 0;

        for (int s = 0; s < BucketCount; s++)
        {
            int c = bCounts[s];
            if (c == 0)
            {
                continue;
            }

            if ((keepMask & (1 << s)) != 0)
            {
                keptIndices += c;
                if (runCount == 0)
                {
                    runStartByte = absStarts[s];
                    runCount = c;
                }
                else
                {
                    runCount += c;
                }
            }
            else if (runCount > 0)
            {
                startsByte[groupsCount * 2] = runStartByte;
                sizes[groupsCount] = runCount;
                groupsCount++;
                runCount = 0;
            }
        }

        if (runCount > 0)
        {
            startsByte[groupsCount * 2] = runStartByte;
            sizes[groupsCount] = runCount;
            groupsCount++;
        }

        int keptTris = keptIndices / 3;
        int culledTris = Math.Max(0, (loc.TotalIndices - keptIndices) / 3);
        renderedTris += keptTris;
        keptTrisAcc += keptTris;
        culledTrisAcc += culledTris;
    }

    public static void EnsurePoolSpanCapacity(MeshDataPool pool, int neededSpans)
    {
        if (pool.indicesSizes != null && pool.indicesSizes.Length >= neededSpans &&
            pool.indicesStartsByte != null && pool.indicesStartsByte.Length >= neededSpans * 2)
        {
            return;
        }

        int curCap = pool.indicesSizes?.Length ?? 0;
        int newCap = Math.Max(neededSpans, Math.Max(curCap * 2, 256));
        Array.Resize(ref pool.indicesSizes, newCap);
        Array.Resize(ref pool.indicesStartsByte, newCap * 2);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ClassifyQuadSlot(float[] xyz, int[] flags, int v0)
    {
        int f0 = flags[v0];
        if (((f0 | flags[v0 + 1] | flags[v0 + 2] | flags[v0 + 3]) & VertexFlags.WindModeBitsMask) != 0)
        {
            return SlotUnaligned;
        }

        int normBits = f0 & VertexFlags.NormalBitMask;
        if ((flags[v0 + 1] & VertexFlags.NormalBitMask) != normBits ||
            (flags[v0 + 2] & VertexFlags.NormalBitMask) != normBits ||
            (flags[v0 + 3] & VertexFlags.NormalBitMask) != normBits)
        {
            return SlotUnaligned;
        }

        int slot;
        float nx = 0f, ny = 0f, nz = 0f;
        if (normBits == NormalNorthFlags) { slot = SlotNorth; nz = -1f; }
        else if (normBits == NormalEastFlags) { slot = SlotEast; nx = 1f; }
        else if (normBits == NormalSouthFlags) { slot = SlotSouth; nz = 1f; }
        else if (normBits == NormalWestFlags) { slot = SlotWest; nx = -1f; }
        else if (normBits == NormalUpFlags) { slot = SlotUp; ny = 1f; }
        else if (normBits == NormalDownFlags) { slot = SlotDown; ny = -1f; }
        else
        {
            return SlotUnaligned;
        }

        return VerifyCardinalPlanarQuad(xyz, v0, nx, ny, nz) ? slot : SlotUnaligned;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool VerifyCardinalPlanarQuad(float[] xyz, int v0, float nx, float ny, float nz)
    {
        int b0 = v0 * 3;
        float x0 = xyz[b0], y0 = xyz[b0 + 1], z0 = xyz[b0 + 2];
        float e1x = xyz[b0 + 3] - x0, e1y = xyz[b0 + 4] - y0, e1z = xyz[b0 + 5] - z0;
        float e2x = xyz[b0 + 6] - x0, e2y = xyz[b0 + 7] - y0, e2z = xyz[b0 + 8] - z0;
        float e3x = xyz[b0 + 9] - x0, e3y = xyz[b0 + 10] - y0, e3z = xyz[b0 + 11] - z0;

        // Require all 4 vertices to sit on the face's cardinal plane (handles tilted/mod geometry safely).
        float d1 = e1x * nx + e1y * ny + e1z * nz;
        float d2 = e2x * nx + e2y * ny + e2z * nz;
        float d3 = e3x * nx + e3y * ny + e3z * nz;
        if (MathF.Abs(d1) > PlaneTolerance || MathF.Abs(d2) > PlaneTolerance || MathF.Abs(d3) > PlaneTolerance)
        {
            return false;
        }

        // Verify triangle winding cross products point positively along the declared face normal.
        float c1x = e1y * e2z - e1z * e2y;
        float c1y = e1z * e2x - e1x * e2z;
        float c1z = e1x * e2y - e1y * e2x;
        float dot1 = c1x * nx + c1y * ny + c1z * nz;
        if (dot1 <= MinAreaThreshold)
        {
            return false;
        }

        float c2x = e2y * e3z - e2z * e3y;
        float c2y = e2z * e3x - e2x * e3z;
        float c2z = e2x * e3y - e2y * e3x;
        float dot2 = c2x * nx + c2y * ny + c2z * nz;
        if (dot2 < -MinAreaThreshold)
        {
            return false;
        }

        // Reject degenerate or twisted non-axis cross components if non-negligible.
        float perp1 = (MathF.Abs(c1x) + MathF.Abs(c1y) + MathF.Abs(c1z)) - MathF.Abs(dot1);
        float perp2 = (MathF.Abs(c2x) + MathF.Abs(c2y) + MathF.Abs(c2z)) - MathF.Abs(dot2);
        return perp1 <= dot1 * CrossAxisToleranceRatio && perp2 <= MathF.Max(dot1, dot2) * CrossAxisToleranceRatio;
    }

    private static void ReorderQuadsIntoBuckets(
        MeshData mesh,
        int quadCount,
        int vertCount,
        byte[] quadSlots,
        int[] writeVertexCursor,
        int intStride,
        int shortStride,
        int floatStride,
        int byteStride)
    {
        float[] srcXyz = mesh.xyz!;
        float[] dstXyz = EnsureArray(ref t_xyzScratch, vertCount * 3);
        float[] srcUv = mesh.Uv!;
        float[] dstUv = EnsureArray(ref t_uvScratch, vertCount * 2);
        byte[] srcRgba = mesh.Rgba!;
        byte[] dstRgba = EnsureArray(ref t_rgbaScratch, vertCount * 4);
        int[] srcFlags = mesh.Flags!;
        int[] dstFlags = EnsureArray(ref t_flagsScratch, vertCount);

        int[]? srcCInts = intStride > 0 ? mesh.CustomInts!.Values : null;
        int[]? dstCInts = intStride > 0 ? EnsureArray(ref t_customIntsScratch, vertCount * intStride) : null;
        short[]? srcCShorts = shortStride > 0 ? mesh.CustomShorts!.Values : null;
        short[]? dstCShorts = shortStride > 0 ? EnsureArray(ref t_customShortsScratch, vertCount * shortStride) : null;
        float[]? srcCFloats = floatStride > 0 ? mesh.CustomFloats!.Values : null;
        float[]? dstCFloats = floatStride > 0 ? EnsureArray(ref t_customFloatsScratch, vertCount * floatStride) : null;
        byte[]? srcCBytes = byteStride > 0 ? mesh.CustomBytes!.Values : null;
        byte[]? dstCBytes = byteStride > 0 ? EnsureArray(ref t_customBytesScratch, vertCount * byteStride) : null;

        for (int q = 0; q < quadCount; q++)
        {
            int slot = quadSlots[q];
            int dstVert = writeVertexCursor[slot];
            writeVertexCursor[slot] = dstVert + 4;
            int srcVert = q << 2;

            Array.Copy(srcXyz, srcVert * 3, dstXyz, dstVert * 3, 12);
            Array.Copy(srcUv, srcVert * 2, dstUv, dstVert * 2, 8);
            Array.Copy(srcRgba, srcVert * 4, dstRgba, dstVert * 4, 16);
            Array.Copy(srcFlags, srcVert, dstFlags, dstVert, 4);

            if (srcCInts != null && dstCInts != null)
            {
                Array.Copy(srcCInts, srcVert * intStride, dstCInts, dstVert * intStride, 4 * intStride);
            }
            if (srcCShorts != null && dstCShorts != null)
            {
                Array.Copy(srcCShorts, srcVert * shortStride, dstCShorts, dstVert * shortStride, 4 * shortStride);
            }
            if (srcCFloats != null && dstCFloats != null)
            {
                Array.Copy(srcCFloats, srcVert * floatStride, dstCFloats, dstVert * floatStride, 4 * floatStride);
            }
            if (srcCBytes != null && dstCBytes != null)
            {
                Array.Copy(srcCBytes, srcVert * byteStride, dstCBytes, dstVert * byteStride, 4 * byteStride);
            }
        }

        Array.Copy(dstXyz, 0, srcXyz, 0, vertCount * 3);
        Array.Copy(dstUv, 0, srcUv, 0, vertCount * 2);
        Array.Copy(dstRgba, 0, srcRgba, 0, vertCount * 4);
        Array.Copy(dstFlags, 0, srcFlags, 0, vertCount);

        if (srcCInts != null && dstCInts != null)
        {
            Array.Copy(dstCInts, 0, srcCInts, 0, vertCount * intStride);
        }
        if (srcCShorts != null && dstCShorts != null)
        {
            Array.Copy(dstCShorts, 0, srcCShorts, 0, vertCount * shortStride);
        }
        if (srcCFloats != null && dstCFloats != null)
        {
            Array.Copy(dstCFloats, 0, srcCFloats, 0, vertCount * floatStride);
        }
        if (srcCBytes != null && dstCBytes != null)
        {
            Array.Copy(dstCBytes, 0, srcCBytes, 0, vertCount * byteStride);
        }
    }

    private static byte[] EnsureQuadSlotsScratch(int quadCount)
    {
        if (t_quadSlots == null || t_quadSlots.Length < quadCount)
        {
            t_quadSlots = new byte[Math.Max(quadCount, 256)];
        }
        return t_quadSlots;
    }

    private static T[] EnsureArray<T>(ref T[]? buffer, int minLength)
    {
        if (buffer == null || buffer.Length < minLength)
        {
            buffer = new T[Math.Max(minLength, 256)];
        }
        return buffer;
    }
}
