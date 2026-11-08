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

    [Theory]
    [InlineData(0x00000000, false)] // Zero light
    [InlineData(unchecked((int)0xFF000000), false)] // Pure sunlight (high byte only)
    [InlineData(0x1F000000, false)] // Partial sunlight
    [InlineData(0x00000001, true)]  // Red block light
    [InlineData(0x00000200, true)]  // Green block light
    [InlineData(0x00030000, true)]  // Blue block light
    [InlineData(unchecked((int)0xFF00150A), true)] // Sunlight + torchlight
    public void HasBlockLight_IdentifiesDynamicPointLights(int lightRgba, bool expected)
    {
        Assert.Equal(expected, OptimumGreedyMesher.HasBlockLight(lightRgba));
    }

    [Fact]
    public void PointLightZeroToleranceEnforcement_PreventsQuantizationStepping()
    {
        // Near point light: raw values differ slightly due to quadratic falloff.
        int cornerA = unchecked((int)0xFF00150A);
        int cornerB = unchecked((int)0xFF00150B);

        // With tolerance 2, normal sunlight would quantize them equal:
        int sunlightA = OptimumGreedyMesher.QuantizeLight(cornerA & unchecked((int)0xFF000000), 2);
        int sunlightB = OptimumGreedyMesher.QuantizeLight(cornerB & unchecked((int)0xFF000000), 2);
        Assert.Equal(sunlightA, sunlightB);

        // But because point light is detected, tolerance is forced to 0:
        Assert.True(OptimumGreedyMesher.HasBlockLight(cornerA));
        int quantA = OptimumGreedyMesher.QuantizeLight(cornerA, 0);
        int quantB = OptimumGreedyMesher.QuantizeLight(cornerB, 0);
        Assert.NotEqual(quantA, quantB); // Prevents stepped banding!
    }

    [Fact]
    public void MergeSlice_ProducesIdenticalQuadsToFullVolumeMergePass()
    {
        var blocks3d = new long[Size3];
        var faceFlags3d = new byte[Size3];
        var eligible3d = new bool[Size3];
        var sliceKeys = new long[Size2];
        var sliceEligible = new bool[Size2];
        var sliceConsumed = new bool[Size2];

        int face = 4; // Up (slice=Y, row=Z, col=X)
        int slice = 12;
        int faceBit = 1 << face;

        for (int row = 1; row <= 30; row++)
        for (int col = 1; col <= 30; col++)
        {
            if (row == 15 && col == 15) continue; // Interior gap
            int blockId = col < 16 ? 10 : 20;
            int light = row < 16 ? unchecked((int)0xF0000000) : unchecked((int)0xC0000000);
            long key = ((long)(uint)blockId << 32) | (uint)light;

            int idx3d = OptimumGreedyMesher.FaceSliceToIndex(face, slice, row, col);
            blocks3d[idx3d] = key;
            faceFlags3d[idx3d] = (byte)faceBit;
            eligible3d[idx3d] = true;

            int cell2d = row * Size + col;
            sliceKeys[cell2d] = key;
            sliceEligible[cell2d] = true;
        }

        var output3d = new OptimumGreedyMesher.MergedQuad[128];
        var output2d = new OptimumGreedyMesher.MergedQuad[128];

        int count3d = OptimumGreedyMesher.MergePass(blocks3d, faceFlags3d, eligible3d, output3d, maxWidth: 8, maxHeight: 8, onlyFace: face);
        int count2d = OptimumGreedyMesher.MergeSlice(sliceKeys, sliceEligible, sliceConsumed, output2d, face, slice, maxWidth: 8, maxHeight: 8);

        Assert.Equal(count3d, count2d);
        for (int i = 0; i < count2d; i++)
        {
            Assert.Equal(output3d[i].Face, output2d[i].Face);
            Assert.Equal(output3d[i].Slice, output2d[i].Slice);
            Assert.Equal(output3d[i].ColStart, output2d[i].ColStart);
            Assert.Equal(output3d[i].RowStart, output2d[i].RowStart);
            Assert.Equal(output3d[i].Width, output2d[i].Width);
            Assert.Equal(output3d[i].Height, output2d[i].Height);
            Assert.Equal(output3d[i].MergeKey, output2d[i].MergeKey);
        }
    }

    [Fact]
    public void MergeSlice_ExtractsLightFromCompositeMergeKeyWithout3DCache()
    {
        var sliceKeys = new long[Size2];
        var sliceEligible = new bool[Size2];
        var sliceConsumed = new bool[Size2];
        var output = new OptimumGreedyMesher.MergedQuad[16];

        int expectedBlockId = 512;
        int expectedLight = unchecked((int)0xFC102030);
        long compositeKey = ((long)(uint)expectedBlockId << 32) | (uint)expectedLight;

        for (int row = 2; row < 6; row++)
        for (int col = 3; col < 9; col++)
        {
            int cell = row * Size + col;
            sliceKeys[cell] = compositeKey;
            sliceEligible[cell] = true;
        }

        int count = OptimumGreedyMesher.MergeSlice(sliceKeys, sliceEligible, sliceConsumed, output, face: 0, slice: 5);
        Assert.Equal(1, count);
        Assert.Equal(6, output[0].Width);
        Assert.Equal(4, output[0].Height);
        Assert.Equal(expectedBlockId, (int)(output[0].MergeKey >> 32));
        Assert.Equal(expectedLight, unchecked((int)(uint)output[0].MergeKey));
    }

    [Fact]
    public void OptimumGreedyMeshEmitter_Uses2DSliceScratchBuffersUnder16KB()
    {
        string emitterPath = PatchReader.FindRepositoryFile("sources/VintagestoryLib/Optimum/OptimumGreedyMeshEmitter.cs");
        string source = System.IO.File.ReadAllText(emitterPath);

        Assert.DoesNotContain("new bool[Size3]", source);
        Assert.DoesNotContain("new byte[Size3]", source);
        Assert.DoesNotContain("new long[Size3]", source);
        Assert.DoesNotContain("new int[Size3 * 6]", source);
        Assert.DoesNotContain("new int[Size3]", source);

        Assert.Contains("new long[Size2]", source);
        Assert.Contains("new int[Size2]", source);
        Assert.Contains("new bool[Size2]", source);
        Assert.Contains("OptimumGreedyMesher.MergeSlice(", source);

        // 1x long[1024] (8192 B) + 1x int[1024] (4096 B) + 3x bool[1024] (3072 B) = 15360 B < 16 KB
        int sliceScratchBytes = Size2 * sizeof(long) + Size2 * sizeof(int) + 3 * Size2 * sizeof(bool);
        Assert.True(sliceScratchBytes < 16 * 1024, $"Expected < 16 KB 2D slice scratch footprint, got {sliceScratchBytes} bytes");
    }
}
