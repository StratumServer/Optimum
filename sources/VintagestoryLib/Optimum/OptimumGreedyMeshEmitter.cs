using System;
using Vintagestory.API.Client;
using Vintagestory.API.Client.Tesselation;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Vintagestory.Client.NoObf;

/// <summary>
/// Runtime greedy mesh pass for ChunkTesselator.BuildBlockPolygons.
/// Runs before the per-block loop, processes eligible Cube blocks,
/// zeroes their currentChunkDraw32 entry so the per-block path skips them.
///
/// Lives in build/VintagestoryLib/Optimum/ (donor code). The integration
/// point: ChunkTesselator.BuildBlockPolygons calls EmitGreedyQuads at
/// the top, after the edge model data setup but before the Y loop.
///
/// This is the second implementation (plus a B6 upgrade). The first (see
/// git history around commit 2945257) crashed and produced invisible
/// chunks; a full read of the vanilla emit path found 12 distinct bugs,
/// documented in docs/implementation-plans/greedy-mesh-phase2-fix-plan-
/// 2026-07-09.md. This rewrite is correct-by-construction against that
/// inventory:
///
///  B1  never reads currentModeldataByRenderPassByLodLevel; the caller
///      passes the center pool array directly.
///  B2  only merges interior columns (x,z in 1..30) and writes only into
///      the center pool, so BuildBlockPolygons_EdgeOnly's edge retess
///      (which has no greedy pass) never has to reconcile with it.
///  B4  merge key includes BlockId, so materials never merge together.
///  B5  every fallible lookup (texture subid, TextureAtlasPosition) is
///      resolved during eligibility, before any faceFlags bit is cleared.
///      Emission itself cannot fail.
///  B6  per-corner light/AO values computed via CalcBlockFaceLight during
///      the eligibility pass and folded into the merge key. Two faces
///      merge only when all 4 corner light values are identical. Emission
///      writes the true per-corner values, so output is pixel-identical
///      to vanilla regardless of whether smooth shadows are on or off.
///      This replaces the original "exclude SideAo faces" approach that
///      made the greedy pass produce zero quads with the default settings.
///  B7  returns per-face/quad counts for OptimumDiagnostics.
///  B8  the merge itself runs through the shared, unit-tested
///      OptimumGreedyMesher.MergePass instead of a duplicate algorithm.
///  B9  all geometry is chunk-local (matches vars.finalX = lX), never
///      world-space.
///  B10/B12 vertex corner order and UV assignment are derived directly
///      from the vanilla CubeFaceVertices.blockFaceVertices table and the
///      same fixed per-corner UV sequence CubeTesselator.DrawBlockFace
///      uses, instead of hand-rolled per-face switches. This also fixes
///      a previously-undiscovered winding bug (B12): the first attempt's
///      hand-rolled vertex order did not match vanilla's winding, which
///      would have backface-culled every merged quad.
///  B11 eligibility excludes texture bleed, per-position texture
///      variants (HasTiles/HasAlternates), alternatingVOffset, and
///      IDrawYAdjustable blocks - all position-dependent texturing that
///      merging would visibly break.
///
/// B3 (atlas UV tiling needs a fragment-shader wrap) is NOT fixed here -
/// that requires editing chunkopaque.vsh/.fsh and chunkshadowmap.vsh,
/// changes this environment cannot compile or visually verify, and a
/// broken chunk shader would blank the screen for every player. Instead,
/// OptimumConfig.GreedyMeshMaxMergeWidth/Height default to 1, which makes
/// every "merge" cover exactly one block - geometrically and UV-wise
/// identical to vanilla's own per-block emission (see the UV anchor
/// formula below: a width/height of 1 always reduces to the single-tile
/// UV rect). This is deliberately zero-benefit by default; it exists so
/// the rest of this rewrite (pool routing, eligibility, winding, light
/// keying, diagnostics) can be validated end-to-end without the atlas
/// bug. Raising the caps past 1 re-exposes B3 and must not happen before
/// Stage 3 (the shader wrap) lands and passes an in-game playtest.
/// </summary>
public static class OptimumGreedyMeshEmitter
{
    private const int Size = OptimumGreedyMesher.Size;
    private const int Size2 = OptimumGreedyMesher.Size2;
    private const int Size3 = OptimumGreedyMesher.Size3;

    // Edge columns (x or z == 0 or 31) belong to ChunkTesselator's edge mesh
    // parts, which BuildBlockPolygons_EdgeOnly retesselates independently
    // and without a greedy pass (bug B2). Only the interior is eligible.
    private const int InteriorMin = 1;
    private const int InteriorMax = Size - 2; // 30

    // Vertex emission order per face: offsets 7,5,4,6 into
    // CubeFaceVertices.blockFaceVertices[face], expressed as (colBit,rowBit)
    // for the merge's row/col axes (see OptimumGreedyMesher.FaceSliceToIndex
    // for the axis convention). Derived by hand from the vanilla table and
    // cross-checked against AddQuadIndices' (0,1,2)+(0,2,3) triangle fan
    // winding. VerifyFaceGeometry() re-derives this from the live
    // CubeFaceVertices table on first use and logs loudly if it ever
    // disagrees (e.g. after a vanilla update changes the table).
    //
    // Built with explicit index assignments, not a collection-initializer
    // literal: array-literal initializers compile to a FieldRVA-backed
    // <PrivateImplementationDetails> blob, which is lost across this
    // project's decompile -> recompile -> Cecil-transplant pipeline (see
    // docs/il-patcher-plan.md) and makes the patcher reject the whole file
    // as a self-reference error.
    private static readonly (int colBit, int rowBit)[][] FaceVertexBits = BuildFaceVertexBits();
    private static readonly bool[] ColAnchorIsX2 = BuildColAnchorIsX2();
    private static readonly bool[] RowAnchorIsY2 = BuildRowAnchorIsY2();
    private static readonly int[] SliceOffset = BuildSliceOffset();

    private static (int, int)[][] BuildFaceVertexBits()
    {
        var t = new (int, int)[6][];
        t[0] = new (int, int)[4]; t[0][0] = (0, 0); t[0][1] = (0, 1); t[0][2] = (1, 1); t[0][3] = (1, 0); // North
        t[1] = new (int, int)[4]; t[1][0] = (0, 0); t[1][1] = (0, 1); t[1][2] = (1, 1); t[1][3] = (1, 0); // East
        t[2] = new (int, int)[4]; t[2][0] = (1, 0); t[2][1] = (1, 1); t[2][2] = (0, 1); t[2][3] = (0, 0); // South
        t[3] = new (int, int)[4]; t[3][0] = (1, 0); t[3][1] = (1, 1); t[3][2] = (0, 1); t[3][3] = (0, 0); // West
        t[4] = new (int, int)[4]; t[4][0] = (0, 0); t[4][1] = (0, 1); t[4][2] = (1, 1); t[4][3] = (1, 0); // Up
        t[5] = new (int, int)[4]; t[5][0] = (0, 1); t[5][1] = (0, 0); t[5][2] = (1, 0); t[5][3] = (1, 1); // Down
        return t;
    }

    // Whether the texture's "x2"/"y2" edge sits at colBit/rowBit == 0 (the
    // merge span's unextended anchor corner) for this face. False means x1/y1
    // is the anchor instead. See EmitMergedQuad for the extrapolation formula.
    private static bool[] BuildColAnchorIsX2()
    {
        var a = new bool[6];
        a[0] = true; a[1] = true; a[2] = false; a[3] = false; a[4] = true; a[5] = true;
        return a;
    }

    private static bool[] BuildRowAnchorIsY2()
    {
        var a = new bool[6];
        a[0] = true; a[1] = true; a[2] = true; a[3] = true; a[4] = true; a[5] = false;
        return a;
    }

    // Does the face sit at the slice-axis coordinate itself, or one past it?
    // Matches vanilla's own +1 offsets for East/South/Up.
    private static int[] BuildSliceOffset()
    {
        var a = new int[6];
        a[0] = 0; a[1] = 1; a[2] = 1; a[3] = 0; a[4] = 1; a[5] = 0;
        return a;
    }

    private static bool _geometryVerified;

    // 2D slice scratch buffers (Size2 = 32x32 = 1,024 elements per array):
    // - _sliceMergeKeys: 8 KB (long[1024], packing (blockId << 32) | (uint)quantizedLight)
    // - _sliceBlockIds:  4 KB (int[1024])
    // - _sliceCandidate: 1 KB (bool[1024])
    // - _sliceEligible:  1 KB (bool[1024])
    // - _sliceConsumed:  1 KB (bool[1024])
    // Total 2D working set = 15 KB (< 16 KB), keeping the entire slice state resident
    // in L1d CPU cache and eliminating 1.25 MB of per-thread 3D volume scratch arrays.
    [ThreadStatic]
    private static OptimumGreedyMesher.MergedQuad[] _quadBuffer;

    [ThreadStatic]
    private static long[] _sliceMergeKeys;

    [ThreadStatic]
    private static int[] _sliceBlockIds;

    [ThreadStatic]
    private static bool[] _sliceCandidate;

    [ThreadStatic]
    private static bool[] _sliceEligible;

    [ThreadStatic]
    private static bool[] _sliceConsumed;

    // Tolerance applied beyond GreedyMeshFarDistance: 16-level light
    // (steps of 16 out of 255). Coarse enough to merge across most far
    // gradients, fine enough that the emitted light stays within one
    // perceptual step of vanilla at that distance.
    private const int FarLightTolerance = 4;

    /// <summary>
    /// Runs the greedy merge on the current chunk's interior 2D slice by 2D slice
    /// and emits merged quads directly into <paramref name="centerPool"/>. Zeroes
    /// currentChunkDraw32 entries for blocks whose faces got consumed.
    /// Returns the number of quads emitted (for diagnostics).
    /// </summary>
    public static int EmitGreedyQuads(
        ChunkTesselator tct,
        int chunkX,
        int chunkZ,
        byte[] currentChunkDraw32,
        Block[] currentChunkBlocksExt,
        MeshData[][] centerPool)
    {
        if (!OptimumConfig.EffectiveGreedyMesh) return 0;

        if (!_geometryVerified)
        {
            VerifyFaceGeometry(tct);
            _geometryVerified = true;
            if (!OptimumConfig.EffectiveGreedyMesh) return 0;
        }

        // Clamp to [1, 8]: the tile-count encoding in EmitMergedQuad
        // saturates at 8 (3 bits, tw/th = width/height - 1 capped to 7).
        // A config value above 8 would merge wider than the shader can
        // tile, producing a silent texture smear identical in kind to the
        // bug this encoding exists to prevent.
        int maxWidth = Math.Clamp(OptimumConfig.GreedyMeshMaxMergeWidth, 1, 8);
        int maxHeight = Math.Clamp(OptimumConfig.GreedyMeshMaxMergeHeight, 1, 8);

        // Two independent reasons to refuse tiled quads:
        // - GL 3.3 (non-SSBO) has no channel to carry tile bounds
        //   (chunkopaque.vsh's uvIn branch), so the fragment shader's
        //   tileBoundsSize division would be 0/0 on any merged quad.
        // - The chunk shaders were compiled with #define GREEDYMESH 0
        //   (feature off at last shader load): they would not decode the
        //   sentinel and would misread the hijacked flag bits.
        if (!tct.game.api.renderapi.UseSSBOs || !OptimumConfig.GreedyMeshShadersCompiledOn)
        {
            maxWidth = 1;
            maxHeight = 1;
        }

        // A 1x1 "merge" replaces one vanilla quad with one identical
        // greedy quad - all cost, zero benefit. Skip the whole pass.
        if (maxWidth == 1 && maxHeight == 1)
        {
            OptimumDiagnostics.RecordGreedyMeshChunk(0, 0);
            return 0;
        }

        TCTCache vars = tct.vars;
        var game = tct.game;
        TextureAtlasPosition[] texPositions = game.BlockAtlasManager.TextureAtlasPositionsByTextureSubId;
        int[][] subidsByBlockAndFace = game.FastBlockTextureSubidsByBlockAndFace;
        int[] moveIndex = TileSideEnum.MoveIndex;

        // Effective light tolerance for this chunk: clamped to 0 for near chunks
        // (<= 32 blocks from camera) to preserve exact vanilla ambient occlusion,
        // configured base for mid-range, or FarLightTolerance beyond far-distance band.
        int tolerance = Math.Clamp(OptimumConfig.GreedyMeshLightTolerance, 0, 4);
        var plr = game.EntityPlayer;
        if (plr != null)
        {
            tolerance = OptimumGreedyMesher.ComputeEffectiveTolerance(chunkX, chunkZ, plr.Pos.X, plr.Pos.Z);
        }

        long[] sliceMergeKeys = _sliceMergeKeys ??= new long[Size2];
        int[] sliceBlockIds = _sliceBlockIds ??= new int[Size2];
        bool[] sliceCandidate = _sliceCandidate ??= new bool[Size2];
        bool[] sliceEligible = _sliceEligible ??= new bool[Size2];
        bool[] sliceConsumed = _sliceConsumed ??= new bool[Size2];
        OptimumGreedyMesher.MergedQuad[] quadBuffer = _quadBuffer ??= new OptimumGreedyMesher.MergedQuad[InteriorMax * InteriorMax];

        // Save vars state we mutate during CalcBlockFaceLight calls.
        Block savedBlock = vars.block;
        int savedExtIndex3d = vars.extIndex3d;

        int totalQuads = 0;
        int totalBlocksConsumed = 0;

        for (int face = 0; face < 6; face++)
        {
            int faceBit = 1 << face;
            int moveDelta = moveIndex[face];

            // Axis bounds for interior columns (x in [1..30], z in [1..30], y in [0..31]):
            // faces 0,2 (N/S): slice=Z [1..30], row=Y [0..31], col=X [1..30]
            // faces 1,3 (E/W): slice=X [1..30], row=Y [0..31], col=Z [1..30]
            // faces 4,5 (U/D): slice=Y [0..31], row=Z [1..30], col=X [1..30]
            int sliceMin = face < 4 ? InteriorMin : 0;
            int sliceMax = face < 4 ? InteriorMax : Size - 1;
            int rowMin = face < 4 ? 0 : InteriorMin;
            int rowMax = face < 4 ? Size - 1 : InteriorMax;

            for (int slice = sliceMin; slice <= sliceMax; slice++)
            {
                Array.Clear(sliceCandidate, 0, Size2);

                // --- Slice Pass A: cheap visibility, block eligibility, and texture subId check ---
                int sliceCandidates = 0;
                for (int row = rowMin; row <= rowMax; row++)
                {
                    int rowBase = row * Size;
                    for (int col = InteriorMin; col <= InteriorMax; col++)
                    {
                        int idx = OptimumGreedyMesher.FaceSliceToIndex(face, slice, row, col);
                        if ((currentChunkDraw32[idx] & faceBit) == 0) continue;

                        int x, y, z;
                        switch (face)
                        {
                            case 0: case 2: x = col; y = row; z = slice; break;
                            case 1: case 3: x = slice; y = row; z = col; break;
                            default:        x = col; y = slice; z = row; break;
                        }

                        int extIdx = (y + 1) * 34 * 34 + (z + 1) * 34 + (x + 1);
                        Block block = currentChunkBlocksExt[extIdx];
                        if (!IsBlockBaseEligible(block)) continue;

                        int[] subIds = subidsByBlockAndFace[block.BlockId];
                        if (subIds == null) continue;

                        int subId = subIds[face];
                        if (subId < 0 || subId >= texPositions.Length || texPositions[subId] == null) continue;

                        int cell2d = rowBase + col;
                        sliceCandidate[cell2d] = true;
                        sliceBlockIds[cell2d] = block.BlockId;
                        sliceCandidates++;
                    }
                }

                // If fewer than 2 candidates exist in this slice, no merge can happen.
                if (sliceCandidates < 2) continue;

                // --- Slice Pass B: lazy CalcBlockFaceLight for candidates with a 2D run neighbor ---
                Array.Clear(sliceEligible, 0, Size2);
                int sliceEligibleCount = 0;

                for (int row = rowMin; row <= rowMax; row++)
                {
                    int rowBase = row * Size;
                    for (int col = InteriorMin; col <= InteriorMax; col++)
                    {
                        int cell2d = rowBase + col;
                        if (!sliceCandidate[cell2d]) continue;

                        int myId = sliceBlockIds[cell2d];
                        if (!HasSliceRunNeighbor(sliceCandidate, sliceBlockIds, row, col, cell2d, myId))
                        {
                            sliceCandidate[cell2d] = false;
                            continue;
                        }

                        int x, y, z;
                        switch (face)
                        {
                            case 0: case 2: x = col; y = row; z = slice; break;
                            case 1: case 3: x = slice; y = row; z = col; break;
                            default:        x = col; y = slice; z = row; break;
                        }

                        int extIdx = (y + 1) * 34 * 34 + (z + 1) * 34 + (x + 1);
                        vars.block = currentChunkBlocksExt[extIdx];
                        vars.extIndex3d = extIdx;
                        vars.CalcBlockFaceLight(face, extIdx + moveDelta);

                        int raw0 = vars.CurrentLightRGBByCorner[0];
                        int raw1 = vars.CurrentLightRGBByCorner[1];
                        int raw2 = vars.CurrentLightRGBByCorner[2];
                        int raw3 = vars.CurrentLightRGBByCorner[3];

                        int faceTolerance = tolerance;
                        if (OptimumGreedyMesher.HasBlockLight(raw0) ||
                            OptimumGreedyMesher.HasBlockLight(raw1) ||
                            OptimumGreedyMesher.HasBlockLight(raw2) ||
                            OptimumGreedyMesher.HasBlockLight(raw3))
                        {
                            faceTolerance = 0;
                        }

                        int q0 = OptimumGreedyMesher.QuantizeLight(raw0, faceTolerance);
                        int q1 = OptimumGreedyMesher.QuantizeLight(raw1, faceTolerance);
                        int q2 = OptimumGreedyMesher.QuantizeLight(raw2, faceTolerance);
                        int q3 = OptimumGreedyMesher.QuantizeLight(raw3, faceTolerance);

                        if (q0 != q1 || q0 != q2 || q0 != q3)
                        {
                            sliceCandidate[cell2d] = false;
                            continue;
                        }

                        sliceEligible[cell2d] = true;
                        sliceMergeKeys[cell2d] = ((long)(uint)myId << 32) | (uint)q0;
                        sliceEligibleCount++;
                    }
                }

                if (sliceEligibleCount == 0) continue;

                int quadCount = OptimumGreedyMesher.MergeSlice(
                    sliceMergeKeys, sliceEligible, sliceConsumed, quadBuffer,
                    face, slice, maxWidth, maxHeight);

                int emitCount = Math.Min(quadCount, quadBuffer.Length);
                for (int i = 0; i < emitCount; i++)
                {
                    OptimumGreedyMesher.MergedQuad quad = quadBuffer[i];
                    int seedIdx = OptimumGreedyMesher.FaceSliceToIndex(quad.Face, quad.Slice, quad.RowStart, quad.ColStart);
                    int seedX = seedIdx % Size;
                    int seedZ = (seedIdx / Size) % Size;
                    int seedY = seedIdx / Size2;
                    int extSeed = (seedY + 1) * 34 * 34 + (seedZ + 1) * 34 + (seedX + 1);
                    Block block = currentChunkBlocksExt[extSeed];

                    int subId = subidsByBlockAndFace[block.BlockId][face];
                    TextureAtlasPosition texPos = texPositions[subId];

                    // Extract the flat quantized face light directly from the low 32 bits of MergeKey.
                    int lightRgb = unchecked((int)(uint)quad.MergeKey);

                    EmitMergedQuad(centerPool, vars, block, quad, texPos, lightRgb);

                    for (int row = 0; row < quad.Height; row++)
                    for (int col = 0; col < quad.Width; col++)
                    {
                        int cellIdx = OptimumGreedyMesher.FaceSliceToIndex(
                            quad.Face, quad.Slice, quad.RowStart + row, quad.ColStart + col);
                        currentChunkDraw32[cellIdx] = (byte)(currentChunkDraw32[cellIdx] & ~faceBit);
                    }

                    totalQuads++;
                    totalBlocksConsumed += quad.Width * quad.Height;
                }
            }
        }

        // Restore vars state.
        vars.block = savedBlock;
        vars.extIndex3d = savedExtIndex3d;

        OptimumDiagnostics.RecordGreedyMeshChunk(totalQuads, totalBlocksConsumed);
        return totalQuads;
    }

    private static bool IsBlockBaseEligible(Block block)
    {
        return block.DrawType == EnumDrawType.Cube
            && block.FaceCullMode == EnumFaceCullMode.Default
            && (int)block.SideOpaque == 0x3F
            && block.RandomDrawOffset == 0
            && !block.ShapeUsesColormap
            && !block.LoadColorMapAnyway
            && !block.Frostable
            && !block.CanReceiveBleed
            && !block.HasTiles
            && !block.HasAlternates
            && !block.alternatingVOffset
            && block is not IDrawYAdjustable
            && block.RenderPass == EnumChunkRenderPass.Opaque
            && (block.VertexFlags.All & (VertexFlags.ZOffsetBitMask
                | VertexFlags.ReflectiveBitMask | VertexFlags.WindBitsMask)) == 0;
    }

    private static bool HasSliceRunNeighbor(
        bool[] sliceCandidate, int[] sliceBlockIds,
        int row, int col, int cell2d, int myId)
    {
        if (col > 0 && sliceCandidate[cell2d - 1] && sliceBlockIds[cell2d - 1] == myId) return true;
        if (col < Size - 1 && sliceCandidate[cell2d + 1] && sliceBlockIds[cell2d + 1] == myId) return true;
        if (row > 0 && sliceCandidate[cell2d - Size] && sliceBlockIds[cell2d - Size] == myId) return true;
        if (row < Size - 1 && sliceCandidate[cell2d + Size] && sliceBlockIds[cell2d + Size] == myId) return true;
        return false;
    }

    private static void EmitMergedQuad(
        MeshData[][] centerPool, TCTCache vars, Block block,
        OptimumGreedyMesher.MergedQuad quad, TextureAtlasPosition texPos,
        int lightRgb)
    {
        int face = quad.Face;
        int colStart = quad.ColStart, width = quad.Width;
        int rowStart = quad.RowStart, height = quad.Height;
        int sliceWorld = quad.Slice + SliceOffset[face];

        // UV strategy: when the merge spans more than 1 block in either
        // axis, emit single-tile UVs (the sub-texture rect covers one
        // tile). The fragment shader uses fract(vertexPosition.axis) to
        // derive which tile the fragment sits in and wraps the UV. The
        // SSBO packer sees normal [0,1] atlas UVs and does not clamp.
        // When width/height == 1, this reduces to the same output as
        // before (one tile, no tiling needed).
        float u1 = texPos.x1, u2 = texPos.x2;
        float v1 = texPos.y1, v2 = texPos.y2;

        int flags = block.VertexFlags.All | BlockFacing.ALLFACES[face].NormalPackedFlags;

        // Encode tile counts in spare renderFlags bits when merge > 1.
        // Bit 11 (Reflective) = sentinel indicating this is a greedy-
        // tiled quad (eligible blocks are never reflective).
        // Bits 29-31: (tileWidth - 1), capped to 7 (merge 1..8).
        // Bits 8-10: (tileHeight - 1), capped to 7 (merge 1..8).
        // The shader checks bit 11 first; only then does it interpret
        // bits 29-31 and 8-10 as tile counts and clear all three fields.
        if (width > 1 || height > 1)
        {
            int tw = Math.Min(width - 1, 7);
            int th = Math.Min(height - 1, 7);
            flags |= (1 << 11) | (tw << 29) | (th << 8);
        }

        MeshData mesh = centerPool[(int)block.RenderPass][texPos.atlasNumber];
        int vertBase = mesh.VerticesCount;

        (int colBit, int rowBit)[] order = FaceVertexBits[face];
        for (int v = 0; v < 4; v++)
        {
            int colBit = order[v].colBit;
            int rowBit = order[v].rowBit;

            float colWorld = colBit == 0 ? colStart : colStart + width;
            float rowWorld = rowBit == 0 ? rowStart : rowStart + height;

            // Single-tile UVs: each vertex gets the tile corner it maps
            // to (x1/x2 for col, y1/y2 for row). The shader handles
            // repeat via position-derived fract().
            float u = colBit == 0
                ? (ColAnchorIsX2[face] ? u2 : u1)
                : (ColAnchorIsX2[face] ? u1 : u2);
            float vv = rowBit == 0
                ? (RowAnchorIsY2[face] ? v2 : v1)
                : (RowAnchorIsY2[face] ? v1 : v2);

            float x, y, z;
            switch (face)
            {
                case 0: case 2: // col=X, row=Y, slice=Z
                    x = colWorld; y = rowWorld; z = sliceWorld;
                    break;
                case 1: case 3: // col=Z, row=Y, slice=X
                    z = colWorld; y = rowWorld; x = sliceWorld;
                    break;
                default: // 4,5: col=X, row=Z, slice=Y
                    x = colWorld; z = rowWorld; y = sliceWorld;
                    break;
            }

            // Flat face light: all 4 corners share the (quantized) value
            // the eligibility check proved equal.
            mesh.AddVertexWithFlags(x, y, z, u, vv, lightRgb, flags);
        }

        mesh.CustomInts.Add4(0);
        mesh.AddQuadIndices(vertBase);

        // Chunk-local coordinates (B9), matching vanilla's own
        // UpdateChunkMinMax(finalX, finalY, finalZ) calls.
        vars.UpdateChunkMinMax(colStart, rowStart, sliceWorld);
        vars.UpdateChunkMinMax(colStart + width, rowStart + height, sliceWorld);
    }

    /// <summary>
    /// Re-derives FaceVertexBits from the live CubeFaceVertices.blockFaceVertices
    /// table (the same table vanilla's own CubeTesselator.DrawBlockFace reads)
    /// and disables greedy meshing for this session if the hand-derived table
    /// above ever disagrees. Runs once, lazily, on the first EmitGreedyQuads
    /// call - CubeFaceVertices's static constructor has already run by then
    /// (TCTCache's own field initializer touches it before any tesselation
    /// happens).
    /// </summary>
    private static void VerifyFaceGeometry(ChunkTesselator tct)
    {
        // Avoid array-literal initializers here too (see the comment on
        // BuildFaceVertexBits above - same FieldRVA blob problem applies to
        // locals, not just static fields).
        int[] offsets = new int[4];
        offsets[0] = 7; offsets[1] = 5; offsets[2] = 4; offsets[3] = 6;
        // Axis role per face: 0=X, 1=Y, 2=Z.
        int[] colAxis = new int[6];
        colAxis[0] = 0; colAxis[1] = 2; colAxis[2] = 0; colAxis[3] = 2; colAxis[4] = 0; colAxis[5] = 0;
        int[] rowAxis = new int[6];
        rowAxis[0] = 1; rowAxis[1] = 1; rowAxis[2] = 1; rowAxis[3] = 1; rowAxis[4] = 2; rowAxis[5] = 2;

        for (int face = 0; face < 6; face++)
        {
            FastVec3f[] corners = CubeFaceVertices.blockFaceVertices[face];
            for (int v = 0; v < 4; v++)
            {
                FastVec3f c = corners[offsets[v]];
                float colVal = colAxis[face] == 0 ? c.X : (colAxis[face] == 1 ? c.Y : c.Z);
                float rowVal = rowAxis[face] == 0 ? c.X : (rowAxis[face] == 1 ? c.Y : c.Z);
                int colBit = colVal > 0.5f ? 1 : 0;
                int rowBit = rowVal > 0.5f ? 1 : 0;

                int expectedCol = FaceVertexBits[face][v].colBit;
                int expectedRow = FaceVertexBits[face][v].rowBit;
                if (colBit != expectedCol || rowBit != expectedRow)
                {
                    tct.game.Logger.Error(
                        "[Optimum] Greedy mesh face geometry mismatch at face={0} vertex={1}: " +
                        "hand-derived ({2},{3}) vs live CubeFaceVertices ({4},{5}). " +
                        "Disabling greedy meshing for this session - vanilla's vertex table " +
                        "changed and OptimumGreedyMeshEmitter's hardcoded tables need updating.",
                        face, v, expectedCol, expectedRow, colBit, rowBit);
                    OptimumConfig.GreedyMeshEnabled = false;
                    return;
                }
            }
        }
    }
}
