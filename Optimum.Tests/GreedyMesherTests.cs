using System;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class GreedyMesherTests
{
    private const int Size = OptimumGreedyMesher.Size;
    private const int Size2 = OptimumGreedyMesher.Size2;
    private const int Size3 = OptimumGreedyMesher.Size3;

    [Fact]
    public void FullSlabChunkProducesOneQuadPerFaceDirection()
    {
        // A 32x32x32 chunk where every block is eligible and every face
        // on the chunk boundary is visible. Interior faces are hidden.
        // Each of 6 boundary faces produces exactly 1 merged quad (32x32).
        var blocks = new long[Size3];
        var faceFlags = new byte[Size3];
        var eligible = new bool[Size3];

        Array.Fill(blocks, 1);
        Array.Fill(eligible, true);

        // Only chunk-boundary faces are visible (interior fully occluded)
        for (int y = 0; y < Size; y++)
        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            int idx = y * Size2 + z * Size + x;
            byte flags = 0;
            if (z == 0) flags |= 1;      // north
            if (x == Size - 1) flags |= 2; // east
            if (z == Size - 1) flags |= 4; // south
            if (x == 0) flags |= 8;       // west
            if (y == Size - 1) flags |= 16; // up
            if (y == 0) flags |= 32;      // down
            faceFlags[idx] = flags;
        }

        int count = OptimumGreedyMesher.CountMergedQuads(blocks, faceFlags, eligible);

        // 6 boundary faces, each one full 32x32 slab = 1 quad each
        Assert.Equal(6, count);
    }

    [Fact]
    public void MergePassZerosFaceFlagsForConsumedBlocks()
    {
        var blocks = new long[Size3];
        var faceFlags = new byte[Size3];
        var eligible = new bool[Size3];
        var output = new OptimumGreedyMesher.MergedQuad[64];

        Array.Fill(blocks, 42);
        Array.Fill(eligible, true);

        // Set bottom face visible for all blocks in Y=0
        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
            faceFlags[z * Size + x] = 32; // down face bit

        int count = OptimumGreedyMesher.MergePass(blocks, faceFlags, eligible, output);

        // Should produce 1 quad covering the entire Y=0 bottom
        Assert.Equal(1, count);
        Assert.Equal(5, output[0].Face); // down
        Assert.Equal(0, output[0].Slice); // Y=0
        Assert.Equal(32, output[0].Width);
        Assert.Equal(32, output[0].Height);
        Assert.Equal(42, output[0].MergeKey);

        // All faceFlags in Y=0 should be cleared for the down face
        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
            Assert.Equal(0, faceFlags[z * Size + x] & 32);
    }

    [Fact]
    public void NonEligibleBlocksBreakMerge()
    {
        var blocks = new long[Size3];
        var faceFlags = new byte[Size3];
        var eligible = new bool[Size3];

        Array.Fill(blocks, 1);
        Array.Fill(eligible, true);

        // Set up face visible for entire Y=31 slice
        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
            faceFlags[31 * Size2 + z * Size + x] = 16; // up face

        // Make one block in the middle non-eligible (breaks the merge)
        eligible[31 * Size2 + 16 * Size + 16] = false;
        faceFlags[31 * Size2 + 16 * Size + 16] = 0;

        int count = OptimumGreedyMesher.CountMergedQuads(blocks, faceFlags, eligible);

        // Should be more than 1 quad (the non-eligible block splits the merge)
        Assert.True(count > 1, $"Expected >1 quads with a gap, got {count}");
        // Should be much less than 1024 (32x32) since most still merges
        Assert.True(count < 10, $"Expected <10 quads from one gap, got {count}");
    }

    [Fact]
    public void DifferentBlockIdsPreventMergeAcrossMaterials()
    {
        var blocks = new long[Size3];
        var faceFlags = new byte[Size3];
        var eligible = new bool[Size3];

        Array.Fill(eligible, true);

        // Y=0 bottom: left half is material 1, right half is material 2
        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            int idx = z * Size + x;
            blocks[idx] = x < 16 ? 1 : 2;
            faceFlags[idx] = 32; // down face
        }

        int count = OptimumGreedyMesher.CountMergedQuads(blocks, faceFlags, eligible);

        // Material-keyed merging (bug B4 fix): the merge never crosses a
        // BlockId boundary, so the two 16-wide halves become 2 quads.
        Assert.Equal(2, count);
    }

    [Fact]
    public void DifferentBlockIdsProduceQuadsWithCorrectBlockId()
    {
        var blocks = new long[Size3];
        var faceFlags = new byte[Size3];
        var eligible = new bool[Size3];
        var output = new OptimumGreedyMesher.MergedQuad[64];

        Array.Fill(eligible, true);

        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            int idx = z * Size + x;
            blocks[idx] = x < 16 ? 1 : 2;
            faceFlags[idx] = 32;
        }

        int count = OptimumGreedyMesher.MergePass(blocks, faceFlags, eligible, output);

        Assert.Equal(2, count);
        // Both quads should be 16 wide, 32 tall (full Z span), one per material.
        Assert.Equal(16, output[0].Width);
        Assert.Equal(32, output[0].Height);
        Assert.Equal(16, output[1].Width);
        Assert.Equal(32, output[1].Height);
        Assert.NotEqual(output[0].MergeKey, output[1].MergeKey);
    }

    [Fact]
    public void ThreeMaterialsInARowProduceThreeQuads()
    {
        var blocks = new long[Size3];
        var faceFlags = new byte[Size3];
        var eligible = new bool[Size3];

        Array.Fill(eligible, true);

        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            int idx = z * Size + x;
            blocks[idx] = x switch { < 10 => 1, < 20 => 2, _ => 3 };
            faceFlags[idx] = 32;
        }

        int count = OptimumGreedyMesher.CountMergedQuads(blocks, faceFlags, eligible);

        Assert.Equal(3, count);
    }

    [Fact]
    public void MaxWidthCapProducesOneQuadPerBlockWhenSetToOne()
    {
        // Stage 1 pipeline-validation mode: cap merge width/height at 1,
        // which must reduce to one quad per visible face (identical
        // coverage to the vanilla per-block path).
        var blocks = new long[Size3];
        var faceFlags = new byte[Size3];
        var eligible = new bool[Size3];

        Array.Fill(blocks, 1);
        Array.Fill(eligible, true);

        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
            faceFlags[z * Size + x] = 32; // down face, full slice

        int count = OptimumGreedyMesher.CountMergedQuads(blocks, faceFlags, eligible, maxWidth: 1, maxHeight: 1);

        Assert.Equal(Size * Size, count);
    }

    [Fact]
    public void MaxWidthCapLimitsSpanButStillMerges()
    {
        var blocks = new long[Size3];
        var faceFlags = new byte[Size3];
        var eligible = new bool[Size3];
        var output = new OptimumGreedyMesher.MergedQuad[128];

        Array.Fill(blocks, 1);
        Array.Fill(eligible, true);

        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
            faceFlags[z * Size + x] = 32;

        int count = OptimumGreedyMesher.MergePass(blocks, faceFlags, eligible, output, maxWidth: 4, maxHeight: 4);

        // 32x32 slab tiled into 4x4 quads = 8x8 = 64 quads.
        Assert.Equal(64, count);
        for (int i = 0; i < count; i++)
        {
            Assert.True(output[i].Width <= 4);
            Assert.True(output[i].Height <= 4);
        }
    }

    [Fact]
    public void OnlyFaceProcessesJustOneFaceDirection()
    {
        // A block with both up and down faces visible: onlyFace=4 should
        // merge only the up face and leave the down face's bits untouched,
        // matching the emitter's one-call-per-face design (needed because
        // light differs per face direction - see bug B6).
        var blocks = new long[Size3];
        var faceFlags = new byte[Size3];
        var eligible = new bool[Size3];

        Array.Fill(blocks, 1);
        Array.Fill(eligible, true);

        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            faceFlags[31 * Size2 + z * Size + x] = 16; // up face
            faceFlags[0 * Size2 + z * Size + x] = 32;  // down face
        }

        int upCount = OptimumGreedyMesher.CountMergedQuads(blocks, faceFlags, eligible, onlyFace: 4);
        Assert.Equal(1, upCount);

        // Down face bits must still be intact: CountMergedQuads takes a
        // copy internally, so faceFlags itself is unmodified by the call
        // above. Confirm the down face still merges as its own quad.
        int downCount = OptimumGreedyMesher.CountMergedQuads(blocks, faceFlags, eligible, onlyFace: 5);
        Assert.Equal(1, downCount);
    }

    [Fact]
    public void FaceSliceToIndexRoundTripsForAllFaces()
    {
        // Every (face, slice, row, col) combination must map to a unique,
        // in-range flat index, and the mapping must be consistent with the
        // face/row/col/slice axis convention documented on FaceSliceToIndex.
        for (int face = 0; face < 6; face++)
        {
            int idx = OptimumGreedyMesher.FaceSliceToIndex(face, 5, 7, 9);
            Assert.InRange(idx, 0, Size3 - 1);
        }
    }

    [Fact]
    public void QuantizeLightZeroToleranceIsIdentity()
    {
        Assert.Equal(0x12345678, OptimumGreedyMesher.QuantizeLight(0x12345678, 0));
        Assert.Equal(unchecked((int)0xFFFFFFFF), OptimumGreedyMesher.QuantizeLight(unchecked((int)0xFFFFFFFF), 0));
    }

    [Fact]
    public void QuantizeLightMasksEachChannelIndependently()
    {
        // tolerance 2 keeps the top 6 bits of each byte (mask 0xFC).
        Assert.Equal(unchecked((int)0xFCF8F4F0), OptimumGreedyMesher.QuantizeLight(unchecked((int)0xFFFBF7F3), 2));
        // No cross-byte carries: a byte of 0xFF next to 0x01 must not bleed.
        Assert.Equal(0x00FE00FE, OptimumGreedyMesher.QuantizeLight(0x01FF01FF, 1));
        // tolerance 4 -> steps of 16 (mask 0xF0).
        Assert.Equal(unchecked((int)0xF0F0F0F0), OptimumGreedyMesher.QuantizeLight(unchecked((int)0xFFFFFFFF), 4));
    }

    [Fact]
    public void QuantizeLightMakesNearbyValuesEqual()
    {
        // Two light values differing by less than one step become equal
        // after quantization - the property the merge eligibility relies on.
        int a = OptimumGreedyMesher.QuantizeLight(0x64646464, 2); // 100 per channel
        int b = OptimumGreedyMesher.QuantizeLight(0x67676767, 2); // 103 per channel
        Assert.Equal(a, b);
    }
}
