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

    [ThreadStatic]
    private static OptimumGreedyMesher.MergedQuad[] _quadBuffer;

    [ThreadStatic]
    private static bool[] _eligible;

    [ThreadStatic]
    private static byte[] _faceFlags;

    [ThreadStatic]
    private static long[] _mergeKeys;

    // Flat face light cache: 1 int per face per block position (the flat
    // eligibility check guarantees all 4 corners equal, so one value
    // carries the face's full light state). Layout: [idx * 6 + face].
    // Only populated for faces that pass eligibility.
    [ThreadStatic]
    private static int[] _faceLightCache;

    // BlockId per position, cached during pass A so pass B's run-neighbor
    // checks and the merge-key loop don't chase Block references again.
    // Only valid where the candidate face flags are nonzero.
    [ThreadStatic]
    private static int[] _blockIdCache;

    // Tolerance applied beyond GreedyMeshFarDistance: 16-level light
    // (steps of 16 out of 255). Coarse enough to merge across most far
    // gradients, fine enough that the emitted light stays within one
    // perceptual step of vanilla at that distance.
    private const int FarLightTolerance = 4;

    /// <summary>
    /// Runs the greedy merge on the current chunk's interior and emits
    /// merged quads directly into <paramref name="centerPool"/>. Zeroes
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
        if (!OptimumConfig.GreedyMeshEnabled) return 0;

        if (!_geometryVerified)
        {
            VerifyFaceGeometry(tct);
            _geometryVerified = true;
            if (!OptimumConfig.GreedyMeshEnabled) return 0;
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

        // Effective light tolerance for this chunk: the configured base,
        // or FarLightTolerance beyond the far-distance band (horizontal,
        // chunk center vs. player). A torn/stale player position read at
        // worst mis-tiers one chunk for one tesselation - harmless.
        int tolerance = Math.Clamp(OptimumConfig.GreedyMeshLightTolerance, 0, 4);
        if (OptimumConfig.GreedyMeshFarDistance > 0)
        {
            var plr = game.EntityPlayer;
            if (plr != null)
            {
                double dx = chunkX * 32 + 16 - plr.Pos.X;
                double dz = chunkZ * 32 + 16 - plr.Pos.Z;
                if (dx * dx + dz * dz > OptimumConfig.GreedyMeshFarDistanceSq)
                {
                    tolerance = FarLightTolerance;
                }
            }
        }

        bool[] eligible = _eligible ??= new bool[Size3];
        byte[] faceFlags = _faceFlags ??= new byte[Size3];
        long[] mergeKeys = _mergeKeys ??= new long[Size3];
        int[] faceLightCache = _faceLightCache ??= new int[Size3 * 6];
        int[] blockIdCache = _blockIdCache ??= new int[Size3];
        OptimumGreedyMesher.MergedQuad[] quadBuffer = _quadBuffer ??= new OptimumGreedyMesher.MergedQuad[InteriorMax * InteriorMax];

        // --- Pass A (B2, B5, B11 + Stage A guards): per-face merge
        // candidates from the cheap checks only - visibility, block-type
        // eligibility, texture lookups. No light computation here: light
        // is the expensive part of tesselation (CalcBlockFaceLight's AO
        // path walks 8 neighbors per face) and the vanilla path recomputes
        // it for every face greedy doesn't consume, so computing it for
        // faces that can never merge would pay that cost twice for
        // nothing. Pass B computes it only for faces with a same-material
        // run neighbor. ---
        int candidateCount = 0;
        for (int y = 0; y < Size; y++)
        for (int z = 0; z < Size; z++)
        for (int x = 0; x < Size; x++)
        {
            int idx = y * Size2 + z * Size + x;
            byte rawFlags = currentChunkDraw32[idx];

            bool interior = x >= InteriorMin && x <= InteriorMax && z >= InteriorMin && z <= InteriorMax;
            if (rawFlags == 0 || !interior)
            {
                eligible[idx] = false;
                faceFlags[idx] = 0;
                continue;
            }

            int extIdx = (y + 1) * 34 * 34 + (z + 1) * 34 + (x + 1);
            Block block = currentChunkBlocksExt[extIdx];

            bool baseEligible = block.DrawType == EnumDrawType.Cube
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
                // Only the Opaque pass has a shader patched to decode the
                // tile-count sentinel (chunkopaque.vsh/.fsh). A cube block
                // declaring any other pass - e.g. grass-topped soil, which
                // is EnumChunkRenderPass.TopSoil - would route a merged
                // quad through an unpatched shader: single-tile UV smeared
                // across the whole span, and the hijacked flag bits (see
                // below) left live for that shader's own flag reads.
                && block.RenderPass == EnumChunkRenderPass.Opaque
                // EmitMergedQuad ORs the tile-count sentinel into bits 8-11
                // and 29-31 of the block's own VertexFlags (ZOffset,
                // Reflective, WindMode/WindData). A block that already uses
                // any of those bits would have them corrupted by the OR, or
                // - worse - a block with the Reflective bit naturally set
                // would false-trigger the sentinel in the shader on
                // ordinary vanilla-path (unmerged) quads, since eligibility
                // isn't consulted there. VertexFlags.cs documents WindData
                // doubling as reflective-mode storage when WindMode == 0,
                // so this combination exists in real content.
                && (block.VertexFlags.All & (VertexFlags.ZOffsetBitMask
                    | VertexFlags.ReflectiveBitMask | VertexFlags.WindBitsMask)) == 0;

            int[] subIds = baseEligible ? subidsByBlockAndFace[block.BlockId] : null;
            if (subIds == null)
            {
                eligible[idx] = false;
                faceFlags[idx] = 0;
                continue;
            }

            byte candFlags = 0;
            for (int face = 0; face < 6; face++)
            {
                int faceBit = 1 << face;
                if ((rawFlags & faceBit) == 0) continue;

                int subId = subIds[face];
                if (subId < 0 || subId >= texPositions.Length || texPositions[subId] == null) continue;

                candFlags |= (byte)faceBit;
            }

            blockIdCache[idx] = block.BlockId;
            faceFlags[idx] = candFlags;
            eligible[idx] = candFlags != 0;
            if (candFlags != 0) candidateCount++;
        }

        if (candidateCount == 0)
        {
            OptimumDiagnostics.RecordGreedyMeshChunk(0, 0);
            return 0;
        }

        // --- Pass B (B6): light, computed lazily. A face only gets a
        // CalcBlockFaceLight call if it has at least one same-BlockId
        // candidate neighbor along its merge axes (a "run neighbor") -
        // without one it can never merge, so it falls through to the
        // vanilla path untouched, with its light computed exactly once,
        // there. For run members: quantize the 4 corners by the effective
        // tolerance and require them equal. Matching the corner *pattern*
        // across blocks is not enough - a merged quad interpolates the
        // seed's corners once across the whole span while vanilla repeats
        // the per-block gradient, so flat (quantized) light is the only
        // case a single quad reproduces. The quantized value is what gets
        // emitted, so every block in a span agrees exactly. ---

        // Save vars state we mutate during CalcBlockFaceLight calls.
        Block savedBlock = vars.block;
        int savedExtIndex3d = vars.extIndex3d;

        int eligibleCount = 0;
        for (int y = 0; y < Size; y++)
        for (int z = InteriorMin; z <= InteriorMax; z++)
        for (int x = InteriorMin; x <= InteriorMax; x++)
        {
            int idx = y * Size2 + z * Size + x;
            byte candFlags = faceFlags[idx];
            if (candFlags == 0) continue;

            int extIdx = (y + 1) * 34 * 34 + (z + 1) * 34 + (x + 1);
            int myId = blockIdCache[idx];
            bool varsSet = false;

            byte safeFlags = 0;
            for (int face = 0; face < 6; face++)
            {
                int faceBit = 1 << face;
                if ((candFlags & faceBit) == 0) continue;

                if (!HasRunNeighbor(faceFlags, blockIdCache, face, faceBit, x, y, z, idx, myId)) continue;

                if (!varsSet)
                {
                    // Set vars state for CalcBlockFaceLight (it reads
                    // vars.block and vars.extIndex3d internally).
                    vars.block = currentChunkBlocksExt[extIdx];
                    vars.extIndex3d = extIdx;
                    varsSet = true;
                }

                vars.CalcBlockFaceLight(face, extIdx + moveIndex[face]);

                int q0 = OptimumGreedyMesher.QuantizeLight(vars.CurrentLightRGBByCorner[0], tolerance);
                int q1 = OptimumGreedyMesher.QuantizeLight(vars.CurrentLightRGBByCorner[1], tolerance);
                int q2 = OptimumGreedyMesher.QuantizeLight(vars.CurrentLightRGBByCorner[2], tolerance);
                int q3 = OptimumGreedyMesher.QuantizeLight(vars.CurrentLightRGBByCorner[3], tolerance);

                if (q0 != q1 || q0 != q2 || q0 != q3) continue;

                faceLightCache[idx * 6 + face] = q0;
                safeFlags |= (byte)faceBit;
            }

            faceFlags[idx] = safeFlags;
            eligible[idx] = safeFlags != 0;
            if (safeFlags != 0) eligibleCount++;
        }

        // Restore vars state.
        vars.block = savedBlock;
        vars.extIndex3d = savedExtIndex3d;

        if (eligibleCount == 0)
        {
            OptimumDiagnostics.RecordGreedyMeshChunk(0, 0);
            return 0;
        }

        int totalQuads = 0;
        int totalBlocksConsumed = 0;

        for (int face = 0; face < 6; face++)
        {
            int faceBit = 1 << face;

            // Merge key folds in BlockId (B4) and the flat (quantized)
            // face light value (B6), so the merge never crosses a
            // material or light boundary. Packed exactly into a long
            // (blockId in the high 32 bits, light in the low 32 bits)
            // instead of hashed: a 32-bit HashCode.Combine could collide
            // and silently merge two different materials or light levels
            // into one quad.
            for (int y = 0; y < Size; y++)
            for (int z = InteriorMin; z <= InteriorMax; z++)
            for (int x = InteriorMin; x <= InteriorMax; x++)
            {
                int idx = y * Size2 + z * Size + x;
                if ((faceFlags[idx] & faceBit) == 0) continue;

                mergeKeys[idx] = ((long)(uint)blockIdCache[idx] << 32) | (uint)faceLightCache[idx * 6 + face];
            }

            int quadCount = OptimumGreedyMesher.MergePass(
                mergeKeys, faceFlags, eligible, quadBuffer, maxWidth, maxHeight, onlyFace: face);

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

                // Flat face light from the cache (all blocks in the
                // merged span share the identical quantized value by
                // merge-key construction).
                int lightRgb = faceLightCache[seedIdx * 6 + face];

                EmitMergedQuad(centerPool, vars, block, quad, texPos, lightRgb);

                // Zero the covered faces out of currentChunkDraw32 so the
                // vanilla per-block loop does not emit them a second time.
                // A coplanar duplicate z-fights with the merged quad (the
                // two triangulations interpolate depth differently) and
                // flickers. Done here, per emitted quad, rather than from
                // MergePass's faceFlags bookkeeping, so a quad dropped by
                // the buffer cap still falls back to the vanilla path.
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

        OptimumDiagnostics.RecordGreedyMeshChunk(totalQuads, totalBlocksConsumed);
        return totalQuads;
    }

    /// <summary>
    /// True when the face at (x,y,z) has an adjacent same-BlockId merge
    /// candidate along either of its merge axes - the precondition for it
    /// ever merging into a span wider than one block. Axis mapping
    /// matches OptimumGreedyMesher.FaceSliceToIndex: faces 0/2 merge
    /// along X (col) and Y (row); 1/3 along Z and Y; 4/5 along X and Z.
    /// Coordinate bounds are checked explicitly because idx +/- step can
    /// silently wrap into the adjacent row of the flat array. Reads
    /// faceFlags as pass B mutates it, so a neighbor that already failed
    /// its own light check no longer counts - that only ever shrinks the
    /// set of faces we compute light for, never the set that could merge.
    /// </summary>
    private static bool HasRunNeighbor(
        byte[] faceFlags, int[] blockIdCache, int face, int faceBit,
        int x, int y, int z, int idx, int myId)
    {
        int colStep, colCoord, rowStep, rowCoord;
        switch (face)
        {
            case 0: case 2: // col=X, row=Y
                colStep = 1; colCoord = x; rowStep = Size2; rowCoord = y;
                break;
            case 1: case 3: // col=Z, row=Y
                colStep = Size; colCoord = z; rowStep = Size2; rowCoord = y;
                break;
            default: // 4, 5: col=X, row=Z
                colStep = 1; colCoord = x; rowStep = Size; rowCoord = z;
                break;
        }

        if (colCoord > 0 && (faceFlags[idx - colStep] & faceBit) != 0 && blockIdCache[idx - colStep] == myId) return true;
        if (colCoord < Size - 1 && (faceFlags[idx + colStep] & faceBit) != 0 && blockIdCache[idx + colStep] == myId) return true;
        if (rowCoord > 0 && (faceFlags[idx - rowStep] & faceBit) != 0 && blockIdCache[idx - rowStep] == myId) return true;
        if (rowCoord < Size - 1 && (faceFlags[idx + rowStep] & faceBit) != 0 && blockIdCache[idx + rowStep] == myId) return true;
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
