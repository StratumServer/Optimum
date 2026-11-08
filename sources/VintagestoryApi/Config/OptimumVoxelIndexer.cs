using System;
using System.Runtime.CompilerServices;

namespace Vintagestory.API.Config;

/// <summary>
/// Bit-shift arithmetic and flattened 1D span traversal utilities for 32x32x32 voxel chunks.
/// Replaces 3D array multiplications with single-cycle shifts and bitwise ORs, enabling
/// .NET Tier 1 JIT compiler bounds-check elimination (BCE) and branchless index translation.
/// </summary>
public static class OptimumVoxelIndexer
{
    public const int ChunkSize = 32;
    public const int ChunkShiftZ = 5;
    public const int ChunkShiftY = 10;
    public const int ChunkMask = 31;
    public const int VoxelCount = ChunkSize * ChunkSize * ChunkSize; // 32,768

    /// <summary>
    /// Computes the 1D index for a 3D (x, y, z) coordinate in a 32x32x32 chunk.
    /// Bitwise equivalent to (y * 1024) + (z * 32) + x with zero multiplication operations.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToIndex(int x, int y, int z)
    {
        return (y << ChunkShiftY) | (z << ChunkShiftZ) | x;
    }

    /// <summary>
    /// Computes the base index for a given (y, z) row within a 32x32x32 chunk.
    /// Hoisting this base offset outside inner X loops eliminates repeated coordinate math.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetBaseIndex(int y, int z)
    {
        return (y << ChunkShiftY) | (z << ChunkShiftZ);
    }

    /// <summary>
    /// Extracts the X coordinate (0-31) from a 1D voxel index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetX(int index) => index & ChunkMask;

    /// <summary>
    /// Extracts the Z coordinate (0-31) from a 1D voxel index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetZ(int index) => (index >> ChunkShiftZ) & ChunkMask;

    /// <summary>
    /// Extracts the Y coordinate (0-31) from a 1D voxel index.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int GetY(int index) => (index >> ChunkShiftY) & ChunkMask;

    /// <summary>
    /// Decomposes a 1D voxel index into its 3D (x, y, z) coordinates.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void FromIndex(int index, out int x, out int y, out int z)
    {
        x = index & ChunkMask;
        z = (index >> ChunkShiftZ) & ChunkMask;
        y = (index >> ChunkShiftY) & ChunkMask;
    }

    /// <summary>
    /// Maps 2D slice coordinates to a 1D voxel index for greedy meshing passes across faces 0-5.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FaceSliceToIndex(int face, int slice, int row, int col)
    {
        return face switch
        {
            0 or 2 => (row << ChunkShiftY) | (slice << ChunkShiftZ) | col,
            1 or 3 => (row << ChunkShiftY) | (col << ChunkShiftZ) | slice,
            _ => (slice << ChunkShiftY) | (row << ChunkShiftZ) | col,
        };
    }

    /// <summary>
    /// Obtains a contiguous 32-voxel 1D slice along the X axis for a given (y, z) coordinate.
    /// Inner loops iterating over the returned span benefit from JIT bounds-check elimination.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<T> GetRowSpan<T>(Span<T> voxels, int y, int z)
    {
        return voxels.Slice(GetBaseIndex(y, z), ChunkSize);
    }

    /// <summary>
    /// Obtains a contiguous read-only 32-voxel 1D slice along the X axis for a given (y, z) coordinate.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ReadOnlySpan<T> GetRowSpan<T>(ReadOnlySpan<T> voxels, int y, int z)
    {
        return voxels.Slice(GetBaseIndex(y, z), ChunkSize);
    }

    public delegate void VoxelAction<T>(ref T voxel, int x, int y, int z, int index);
    public delegate void ReadOnlyVoxelAction<T>(in T voxel, int x, int y, int z, int index);

    /// <summary>
    /// Iterates across all voxels in a 1D chunk span sequentially.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ForEachVoxel<T>(Span<T> voxels, VoxelAction<T> action)
    {
        int total = Math.Min(voxels.Length, VoxelCount);
        for (int i = 0; i < total; i++)
        {
            int x = i & ChunkMask;
            int z = (i >> ChunkShiftZ) & ChunkMask;
            int y = (i >> ChunkShiftY) & ChunkMask;
            action(ref voxels[i], x, y, z, i);
        }
    }

    /// <summary>
    /// Iterates across all read-only voxels in a 1D chunk span sequentially.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void ForEachVoxel<T>(ReadOnlySpan<T> voxels, ReadOnlyVoxelAction<T> action)
    {
        int total = Math.Min(voxels.Length, VoxelCount);
        for (int i = 0; i < total; i++)
        {
            int x = i & ChunkMask;
            int z = (i >> ChunkShiftZ) & ChunkMask;
            int y = (i >> ChunkShiftY) & ChunkMask;
            action(in voxels[i], x, y, z, i);
        }
    }
}
