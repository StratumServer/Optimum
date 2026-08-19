using System;
using System.Runtime.CompilerServices;

namespace Vintagestory.API.Config;

/// <summary>
/// Binary greedy meshing for opaque cube blocks. Merges adjacent visible
/// faces of the same material into larger quads, reducing vertex count
/// on typical underground chunks.
///
/// Usage: call MergePass after CalculateVisibleFaces has populated the
/// face-flag array. The pass zeroes merged block entries and emits quads
/// directly. Non-eligible blocks stay untouched for the per-block path.
///
/// Merging is keyed by the caller-supplied "blocks" value (typically the
/// BlockId) so faces of different materials never combine into one quad
/// (see docs/implementation-plans/greedy-mesh-phase2-fix-plan-2026-07-09.md,
/// bug B4). Eligibility (drawtype, opacity, colormap, texture-bleed,
/// per-position texture variants, AO applicability) must be resolved by
/// the caller before this runs; this class only knows about the merge key
/// and the visible-face bitmask, not block semantics.
///
/// maxWidth/maxHeight bound the merge span (Stage 1 pipeline validation
/// runs with both capped at 1, which reduces to exactly vanilla's own
/// per-block emission order and is safe to enable without the atlas UV
/// wrap that wider merges need - see OptimumConfig.GreedyMeshMaxMergeWidth
/// and GreedyMeshMaxMergeHeight, and bug B3 in the fix plan).
/// </summary>
public static class OptimumGreedyMesher
{
    public const int Size = 32;
    public const int Size2 = Size * Size;
    public const int Size3 = Size * Size * Size;

    /// <summary>
    /// Holds one merged quad produced by the greedy pass. The runtime
    /// integration converts these into MeshData vertices.
    /// </summary>
    public struct MergedQuad
    {
        /// <summary>Face direction index (0-5): N E S W Up Down.</summary>
        public int Face;
        /// <summary>Slice index along the face normal (0-31).</summary>
        public int Slice;
        /// <summary>Start position in the row axis (0-31).</summary>
        public int ColStart;
        /// <summary>Width in blocks along the row axis.</summary>
        public int Width;
        /// <summary>Start position in the column axis (0-31).</summary>
        public int RowStart;
        /// <summary>Height in blocks along the column axis.</summary>
        public int Height;
        /// <summary>Merge key shared by every block this quad covers. Not
        /// necessarily a bare BlockId - callers that fold extra state (e.g.
        /// per-face light) into the key, as OptimumGreedyMeshEmitter does,
        /// see the composite value here.</summary>
        public long MergeKey;
    }

    /// <summary>
    /// Run the greedy merge on a chunk. Writes merged quads into the output
    /// span and returns the count. Blocks consumed by the merge get their
    /// faceFlags entry zeroed so the per-block path skips them.
    ///
    /// Parameters:
    ///   blocks: flat 32x32x32 array of merge keys (Y-major: index = y*1024 + z*32 + x).
    ///     A 64-bit key lets callers pack more than one value (e.g. BlockId
    ///     plus a light sample) without hash collisions merging the wrong
    ///     material - see OptimumGreedyMeshEmitter's (blockId << 32) | c0.
    ///   faceFlags: per-block visible face bitmask (CalculateVisibleFaces output)
    ///   eligible: per-block bool, true = merge candidate (caller-resolved: opaque cube,
    ///             no colormap, no texture bleed/variants, AO-safe, etc.)
    ///   output: destination span for merged quads
    ///   maxWidth/maxHeight: caps on merge span size (default: no cap)
    ///   onlyFace: process a single face direction (0-5) instead of all six.
    ///     The merge key ("blocks") often needs to vary by face direction
    ///     (e.g. a caller folding per-face neighbor light into the key, since
    ///     each face samples a different neighbor - see bug B6 in the fix
    ///     plan), so callers that do this call MergePass once per face with
    ///     a freshly recomputed key array. Default -1 processes all six
    ///     faces against one shared key array (used by the unit tests here).
    ///
    /// Returns: number of quads written (may exceed output.Length; only the
    /// first output.Length quads are written, matching CountMergedQuads for
    /// a large-enough buffer).
    /// </summary>
    public static int MergePass(
        ReadOnlySpan<long> blocks,
        Span<byte> faceFlags,
        ReadOnlySpan<bool> eligible,
        Span<MergedQuad> output,
        int maxWidth = Size,
        int maxHeight = Size,
        int onlyFace = -1)
    {
        int quadCount = 0;
        Span<bool> consumed = stackalloc bool[Size2];

        int faceStart = onlyFace < 0 ? 0 : onlyFace;
        int faceEnd = onlyFace < 0 ? 5 : onlyFace;
        for (int face = faceStart; face <= faceEnd; face++)
        {
            int faceBit = 1 << face;

            for (int slice = 0; slice < Size; slice++)
            {
                consumed.Clear();

                for (int row = 0; row < Size; row++)
                for (int col = 0; col < Size; col++)
                {
                    int seedIdx = FaceSliceToIndex(face, slice, row, col);
                    if (consumed[row * Size + col]) continue;
                    if (!eligible[seedIdx]) continue;
                    if ((faceFlags[seedIdx] & faceBit) == 0) continue;

                    long key = blocks[seedIdx];

                    int width = ExtendWidth(blocks, faceFlags, eligible, consumed, face, slice, row, col, key, faceBit, maxWidth);
                    int height = ExtendHeight(blocks, faceFlags, eligible, consumed, face, slice, row, col, width, key, faceBit, maxHeight);

                    MarkConsumedAndClear(faceFlags, consumed, face, slice, row, col, width, height, faceBit);

                    if (quadCount < output.Length)
                    {
                        output[quadCount] = new MergedQuad
                        {
                            Face = face,
                            Slice = slice,
                            ColStart = col,
                            Width = width,
                            RowStart = row,
                            Height = height,
                            MergeKey = key,
                        };
                    }
                    quadCount++;
                }
            }
        }

        return quadCount;
    }

    /// <summary>Count-only version for benchmarks (no output span needed).</summary>
    public static int CountMergedQuads(
        ReadOnlySpan<long> blocks,
        ReadOnlySpan<byte> faceFlags,
        ReadOnlySpan<bool> eligible,
        int maxWidth = Size,
        int maxHeight = Size,
        int onlyFace = -1)
    {
        // Copies faceFlags because the merge sweep clears consumed bits as
        // it goes (the count-only path must not require a mutable caller span).
        Span<byte> faceFlagsCopy = new byte[faceFlags.Length];
        faceFlags.CopyTo(faceFlagsCopy);

        int quadCount = 0;
        Span<bool> consumed = stackalloc bool[Size2];

        int faceStart = onlyFace < 0 ? 0 : onlyFace;
        int faceEnd = onlyFace < 0 ? 5 : onlyFace;
        for (int face = faceStart; face <= faceEnd; face++)
        {
            int faceBit = 1 << face;

            for (int slice = 0; slice < Size; slice++)
            {
                consumed.Clear();

                for (int row = 0; row < Size; row++)
                for (int col = 0; col < Size; col++)
                {
                    int seedIdx = FaceSliceToIndex(face, slice, row, col);
                    if (consumed[row * Size + col]) continue;
                    if (!eligible[seedIdx]) continue;
                    if ((faceFlagsCopy[seedIdx] & faceBit) == 0) continue;

                    long key = blocks[seedIdx];

                    int width = ExtendWidth(blocks, faceFlagsCopy, eligible, consumed, face, slice, row, col, key, faceBit, maxWidth);
                    int height = ExtendHeight(blocks, faceFlagsCopy, eligible, consumed, face, slice, row, col, width, key, faceBit, maxHeight);

                    MarkConsumedAndClear(faceFlagsCopy, consumed, face, slice, row, col, width, height, faceBit);

                    quadCount++;
                }
            }
        }

        return quadCount;
    }

    /// <summary>
    /// Quantizes a packed light value (4 independent byte channels: sun +
    /// RGB) to steps of 2^tolerance per channel by masking off the low
    /// bits - floor quantization, AND per byte, so there are no
    /// cross-channel carries. tolerance 0 returns the value unchanged.
    /// The greedy emitter quantizes all 4 corner values before its
    /// flat-light equality test, and emits the quantized value, so every
    /// block in a merged span (and any adjacent span with the same merge
    /// key) agrees exactly on the light it renders with.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int QuantizeLight(int lightRgba, int tolerance)
    {
        if (tolerance <= 0) return lightRgba;
        int keep = (0xFF << tolerance) & 0xFF;
        return lightRgba & unchecked(keep * 0x01010101);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int ToIndex(int x, int y, int z) => y * Size2 + z * Size + x;

    /// <summary>
    /// face 0,2 (N/S): slice=Z, row=Y, col=X.
    /// face 1,3 (E/W): slice=X, row=Y, col=Z.
    /// face 4,5 (Up/Down): slice=Y, row=Z, col=X.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int FaceSliceToIndex(int face, int slice, int row, int col)
    {
        return face switch
        {
            0 or 2 => ToIndex(col, row, slice),
            1 or 3 => ToIndex(slice, row, col),
            _ => ToIndex(col, slice, row), // 4, 5
        };
    }

    private static bool CellMatches(
        ReadOnlySpan<long> blocks, ReadOnlySpan<byte> faceFlags, ReadOnlySpan<bool> eligible,
        Span<bool> consumed, int face, int slice, int row, int col, long key, int faceBit)
    {
        if (row < 0 || row >= Size || col < 0 || col >= Size) return false;
        if (consumed[row * Size + col]) return false;
        int idx = FaceSliceToIndex(face, slice, row, col);
        if (!eligible[idx]) return false;
        if ((faceFlags[idx] & faceBit) == 0) return false;
        return blocks[idx] == key;
    }

    private static int ExtendWidth(
        ReadOnlySpan<long> blocks, ReadOnlySpan<byte> faceFlags, ReadOnlySpan<bool> eligible,
        Span<bool> consumed, int face, int slice, int row, int col, long key, int faceBit, int maxWidth)
    {
        int width = 1;
        while (width < maxWidth && CellMatches(blocks, faceFlags, eligible, consumed, face, slice, row, col + width, key, faceBit))
        {
            width++;
        }
        return width;
    }

    private static int ExtendHeight(
        ReadOnlySpan<long> blocks, ReadOnlySpan<byte> faceFlags, ReadOnlySpan<bool> eligible,
        Span<bool> consumed, int face, int slice, int row, int col, int width, long key, int faceBit, int maxHeight)
    {
        int height = 1;
        while (height < maxHeight)
        {
            bool rowMatches = true;
            for (int c = col; c < col + width; c++)
            {
                if (!CellMatches(blocks, faceFlags, eligible, consumed, face, slice, row + height, c, key, faceBit))
                {
                    rowMatches = false;
                    break;
                }
            }
            if (!rowMatches) break;
            height++;
        }
        return height;
    }

    private static void MarkConsumedAndClear(
        Span<byte> faceFlags, Span<bool> consumed,
        int face, int slice, int row, int col, int width, int height, int faceBit)
    {
        int clearMask = ~faceBit;
        for (int r = row; r < row + height; r++)
        for (int c = col; c < col + width; c++)
        {
            consumed[r * Size + c] = true;
            int idx = FaceSliceToIndex(face, slice, r, c);
            faceFlags[idx] = (byte)(faceFlags[idx] & clearMask);
        }
    }
}
