using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class ChiselLodCoverageTests
{
    [Theory]
    [InlineData("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityMicroBlock.cs.patch")]
    public void ChiselLodRegistersFullMeshForMediumRange(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.DoesNotContain("AddMeshData(Mesh, cmapdata, 0)", source);
        Assert.DoesNotContain("AddMeshData(Mesh, 0)", source);
        Assert.Contains("AddMeshData(Mesh, 2)", source);
        Assert.Contains("AddMeshData(lodMesh, 3)", source);
    }

    [Theory]
    [InlineData("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityMicroBlock.cs.patch")]
    public void ChiselLodBuildsProxyFromMajorityMaterial(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.DoesNotContain("Block primaryBlock = Api.World.GetBlock(BlockIds[0])", source);
        Assert.DoesNotContain("tesselator.TesselateBlock(primaryBlock", source);
        Assert.Contains("GetMajorityMaterialId()", source);
        Assert.Contains("(BlockIds.Length == 1) ? BlockIds[0] : GetMajorityMaterialId()", source);
        Assert.Contains("VolumeRel >= 0.35f", source);
        Assert.Contains("primaryBlock.RenderPass == EnumChunkRenderPass.Opaque", source);
        Assert.Contains("primaryBlock.VertexFlags.GlowLevel == 0", source);
        Assert.Contains("TesselatorManager.GetDefaultBlockMesh(primaryBlock)", source);
        Assert.DoesNotContain("?.Clone()", source);
    }

    [Theory]
    [InlineData("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityMicroBlock.cs.patch")]
    public void ChiselLodVolumeGatesLowVolumeMicroblocks(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        // Prevents open-space, window cutouts, and thin railings (< 35% solid volume)
        // from snapping into solid 1x1x1 unit cubes at distance
        Assert.Contains("VolumeRel >= 0.35f", source);
        Assert.Contains("bool canUseDefaultMesh = VolumeRel >= 0.35f", source);
    }

    [Theory]
    [InlineData("sources/VintagestoryApi/Config/OptimumConfig.cs")]
    [InlineData("VintagestoryApi/Config/OptimumConfig.cs")]
    public void ChiselLodExposesRoutingAndDiagnostics(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("RouteChiselLodMeshes", source);
        Assert.Contains("OptimumDiagnostics", source);
        Assert.Contains("RecordChiselLod", source);
        Assert.Contains("GetChiselLodSummary", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch")]
    public void ChiselLodRoutesIntoSeparateChunkPools(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("currentOptimumChiselModeldataByRenderPassByLodLevel", source);
        Assert.Contains("centerOptimumChiselModeldataByRenderPassByLodLevel", source);
        Assert.Contains("edgeOptimumChiselModeldataByRenderPassByLodLevel", source);
        Assert.Contains("OptimumConfig.RouteChiselLodMeshes", source);
        Assert.Contains("populateOptimumChiselTesselatedChunkPart(centerOptimumChiselModeldataByRenderPassByLodLevel, out TesselatedChunkPart[] centerOptimumChiselParts)", source);
        Assert.Contains("populateOptimumChiselTesselatedChunkPart(edgeOptimumChiselModeldataByRenderPassByLodLevel, out TesselatedChunkPart[] edgeOptimumChiselParts)", source);
    }

    [Fact]
    public void ChiselLodChunkTesselatorKeepsReloadLockVanillaCompatible()
    {
        // The patch must not retype ReloadLock to System.Threading.Lock (which would
        // break cecil transplant, same class of bug as chunksLock in 0.2.1).
        // If it doesn't appear in the patch at all, the field stays vanilla (object).
        string patch = File.ReadAllText(FindRepositoryFile("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch"));

        Assert.DoesNotContain("+\tpublic readonly Lock ReloadLock", patch);
        Assert.DoesNotContain("+\tpublic Lock ReloadLock", patch);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch")]
    public void ChiselLodPoolsAreAllocatedInTransplantedAtlasUpdate(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("centerOptimumChiselModeldataByRenderPassByLodLevel == null", source);
        Assert.Contains("edgeOptimumChiselModeldataByRenderPassByLodLevel == null", source);
        Assert.Contains("centerOptimumChiselModeldataByRenderPassByLodLevel[i] = new MeshData[values.Length][];", source);
        Assert.Contains("edgeOptimumChiselModeldataByRenderPassByLodLevel[i] = new MeshData[values.Length][];", source);
    }

    [Fact]
    public void ChiselLodClosedSourceRouteIsRegisteredAsCecilTargets()
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));
        string cecilList = File.ReadAllText(FindRepositoryFile("patches/cecil-owned.list"));

        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"UpdateForAtlasses\", 1", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"NowProcessChunk\", 5", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"populateTesselatedChunkPart\", 2", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"BuildBlockPolygons\", 3", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"BuildBlockPolygons_EdgeOnly\", 3", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"BuildDecorPolygons\", 5", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"GetMeshPoolForPass\", 3", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.TesselatedChunkPart\", \"AddModelAndStoreLocation\", 8", programSource);
        Assert.Contains("\"populateOptimumChiselTesselatedChunkPart\"", programSource);
        Assert.Contains("\"MergeTesselatedChunkParts\"", programSource);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch", cecilList);
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client.NoObf/TesselatedChunkPart.cs.patch", cecilList);
    }

    [Theory]
    [InlineData("VintagestoryApi/Client/MeshPool/MeshDataPool.cs")]
    [InlineData("patches/VintagestoryApi/Client/MeshPool/MeshDataPool.cs.patch")]
    public void ChiselLodPoolLocationsCarryCustomDistanceFlag(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("OptimumUseChiselLodDistance", source);
        // 1.22.7: method is InFrustumAndRange (renamed from SphereInFrustumAndRange),
        // and inside the class the fields are accessed without a 'location.' prefix.
        Assert.Contains("InFrustumAndRange(FrustumCullSphere, FrustumVisible, LodLevel, OptimumUseChiselLodDistance)", source);
    }

    [Theory]
    [InlineData("VintagestoryApi/Client/Render/FrustumCulling.cs")]
    [InlineData("patches/VintagestoryApi/Client/Render/FrustumCulling.cs.patch")]
    public void ChiselLodFrustumUsesConfiguredDistance(string relativePath)
    {
        string path = FindRepositoryFile(relativePath);
        Assert.True(File.Exists(path), $"{relativePath} must exist.");

        string source = File.ReadAllText(path);

        Assert.Contains("optimumUseChiselLodDistance", source);
        Assert.Contains("OptimumConfig.ChiselLodDistanceSq", source);
        Assert.Contains("distance > chiselDistanceSq || distance >= ViewDistanceSq", source);
        Assert.Contains("case 2:", source);
        Assert.Contains("case 3:", source);
    }

    // Regression: shadow passes had zero chisel-LOD awareness, so the LOD3 full-cube proxy
    // always entered the depth map (CullInstantShadowPassFar's `LodLevel >= 1` accepts 3, and
    // CullInstantShadowPassNear had no LOD check at all). Result: a carved block cast a
    // full-block shadow at any distance, including right next to the camera.
    [Theory]
    [InlineData("VintagestoryApi/Client/MeshPool/MeshDataPool.cs")]
    [InlineData("patches/VintagestoryApi/Client/MeshPool/MeshDataPool.cs.patch")]
    public void ChiselLodShadowPassesRespectChiselDistance(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains(
            "case EnumFrustumCullMode.CullInstantShadowPassNear:",
            source);
        Assert.Contains(
            "culler.InFrustumShadowPass(FrustumCullSphere) && culler.OptimumChiselLodVisible(FrustumCullSphere, LodLevel, OptimumUseChiselLodDistance)",
            source);
        Assert.Contains(
            "culler.InFrustumShadowPass(FrustumCullSphere) && LodLevel >= 1 && culler.OptimumChiselLodVisible(FrustumCullSphere, LodLevel, OptimumUseChiselLodDistance)",
            source);
        // The vanilla, chisel-unaware shadow expressions must be gone.
        Assert.DoesNotContain(
            "culler.InFrustumShadowPass(FrustumCullSphere);",
            source);
        Assert.DoesNotContain(
            "culler.InFrustumShadowPass(FrustumCullSphere) && LodLevel >= 1;",
            source);
    }

    [Theory]
    [InlineData("VintagestoryApi/Client/Render/FrustumCulling.cs")]
    [InlineData("patches/VintagestoryApi/Client/Render/FrustumCulling.cs.patch")]
    public void ChiselLodFrustumExposesSharedShadowHelper(string relativePath)
    {
        string source = File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains(
            "public bool OptimumChiselLodVisible(Sphere sphere, int lodLevel, bool optimumUseChiselLodDistance)",
            source);
        Assert.Contains("!optimumUseChiselLodDistance || !OptimumConfig.ChiselLodEnabled", source);
        Assert.Contains("return distance <= chiselDistanceSq;", source);
        Assert.Contains("return distance > chiselDistanceSq;", source);
    }

    // Runtime counterpart of the shadow fix: vanilla's shadow cases never call
    // InFrustumAndRange, so a second Cecil hook folds each InFrustumShadowPass result
    // through OptimumApiBridge.InFrustumShadowPass.
    [Fact]
    public void ChiselLodShadowHookIsWiredIntoApiPatcher()
    {
        string patcher = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/api-patcher.cs"));

        Assert.Contains("PatchChiselLodShadowHook", patcher);
        Assert.Contains("method.Name == \"InFrustumShadowPass\" && method.Parameters.Count == 3", patcher);
        Assert.Contains("method.Name == \"InFrustumShadowPass\" &&", patcher);
        // Both shadow cases (near + far) must be hooked, hence the exact-count guard.
        Assert.Contains("Expected 2 chisel LOD shadow hooks", patcher);
        // The hook pushes culler (arg 2) and the location (this) after the vanilla call.
        Assert.Contains("Instruction.Create(OpCodes.Ldarg_2)", patcher);
        Assert.Contains("optimumShadowCheck", patcher);
    }

    // Regression: the bridge ignored lodLevel entirely. Because TesselatedChunkPart flags
    // ALL LOD levels of a chisel chunk part, LOD2 and LOD3 got the same answer - both visible
    // up close (z-fighting), both invisible past the chisel distance (block disappears).
    [Fact]
    public void ChiselLodBridgeBranchesOnLodLevel()
    {
        string source = File.ReadAllText(FindRepositoryFile("optimum-api-contracts/optimum-api-bridge.cs"));

        // The buggy shape: one distance test returned for every chisel-flagged location.
        Assert.DoesNotContain(
            "return playerPos.HorDistanceSqTo(sphere.x, sphere.z) < OptimumConfig.ChiselLodDistanceSq;",
            source);

        Assert.Contains("lodLevel == 2 || lodLevel == 3", source);
        Assert.Contains("lodLevel == 2", source);
        Assert.Contains("innerThreshold", source);
        Assert.Contains("outerThreshold", source);
        Assert.Contains("nowVisible ? outerThreshold : innerThreshold", source);
        // The base frustum / view-distance bound must still be enforced.
        Assert.Contains("culler.InFrustumAndRange(sphere, nowVisible, 1)", source);
        Assert.Contains("return culler.InFrustumAndRange(sphere, nowVisible, lodLevel);", source);

        // Shadow-pass bridge entry point used by the new Cecil hook.
        Assert.Contains(
            "public static bool InFrustumShadowPass(bool baseResult, FrustumCulling culler, ModelDataPoolLocation location)",
            source);
        Assert.Contains("location.FrustumCullSphere", source);
        Assert.Contains("location.LodLevel", source);
    }

    [Theory]
    [InlineData("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityMicroBlock.cs.patch")]
    public void ChiselLodMicroblockRecordsDiagnostics(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("RouteChiselLodMeshes = true", source);
        Assert.Contains("RouteChiselLodMeshes = false", source);
        Assert.Contains("OptimumDiagnostics.RecordChiselLod", source);
    }

    [Fact]
    public void ChiselLodCommandsRegisteredClientSide()
    {
        string source = File.ReadAllText(FindRepositoryFile("sources/VSEssentials/Systems/OptimumStatus.cs"));

        Assert.Contains("lodstats", source);
        Assert.Contains("lodreset", source);
        Assert.Contains("GetChiselLodSummary", source);
        Assert.Contains("ResetChiselLod", source);
    }

    [Fact]
    public void ChiselLodSettings_DefaultDistanceAndSliderClamp_EnforcesMinimum64And48()
    {
        string clientSettingsPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientSettings.cs.patch");
        string guiSettingsPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch");
        string configSource = File.ReadAllText(FindRepositoryFile("sources/VintagestoryApi/Config/OptimumConfig.cs"));

        Assert.Contains("public static int OptimumChiselLodDistance { get; set; } = 64;", clientSettingsPatch);
        Assert.Contains("SetValues(ClientSettings.OptimumChiselLodDistance, 48, 256, 16, \" blocks\")", guiSettingsPatch);
        Assert.Contains("public static int ChiselLodDistance = 64;", configSource);
        Assert.Contains("public int ChiselLodDistance { get; set; } = 64;", configSource);

        Vintagestory.API.Config.OptimumConfig.SetChiselLodDistance(32);
        Assert.Equal(48, Vintagestory.API.Config.OptimumConfig.ChiselLodDistance);
        Assert.Equal(48.0 * 48.0, Vintagestory.API.Config.OptimumConfig.ChiselLodDistanceSq);

        Vintagestory.API.Config.OptimumConfig.SetChiselLodDistance(64);
        Assert.Equal(64, Vintagestory.API.Config.OptimumConfig.ChiselLodDistance);
        Assert.Equal(64.0 * 64.0, Vintagestory.API.Config.OptimumConfig.ChiselLodDistanceSq);
    }

    [Fact]
    public void ChiselLod_GuardsPlaceholderAndMissingProxiesInPatches()
    {
        string microblockPatch = PatchReader.ReadPatch("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BlockEntityMicroBlock.cs.patch");
        string stmPatch = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/ShapeTesselatorManager.cs.patch");

        Assert.Contains("primaryBlockId > 0", microblockPatch);
        Assert.Contains("OptimumApiBridge.IsEligibleChiselLodBlock(primaryBlock)", microblockPatch);
        Assert.Contains("OptimumApiBridge.IsPlaceholderChiselLodMesh(capi, lodMesh)", microblockPatch);
        Assert.Contains("OptimumApiBridge.RegisterUnknownBlockModelData(this, unknownBlockModelData)", stmPatch);
        Assert.Contains("OptimumApiBridge.UnregisterUnknownBlockModelData(this)", stmPatch);
    }

    [Fact]
    public void ChiselLodIsEligibleBlock_RejectsInvalidAndNonOpaqueMaterials()
    {
        Assert.False(OptimumApiBridge.IsEligibleChiselLodBlock(null));

        Assert.False(OptimumApiBridge.IsEligibleChiselLodBlock(new Block
        {
            BlockId = 10,
            Code = new AssetLocation("game", "rock-granite"),
            IsMissing = true,
            DrawType = EnumDrawType.Cube,
            BlockMaterial = EnumBlockMaterial.Stone,
            RenderPass = EnumChunkRenderPass.Opaque,
            VertexFlags = new VertexFlags(0),
        }));

        Assert.False(OptimumApiBridge.IsEligibleChiselLodBlock(new Block
        {
            BlockId = 0,
            Code = new AssetLocation("game", "air"),
            DrawType = EnumDrawType.Cube,
            BlockMaterial = EnumBlockMaterial.Stone,
            RenderPass = EnumChunkRenderPass.Opaque,
        }));

        Assert.False(OptimumApiBridge.IsEligibleChiselLodBlock(new Block
        {
            BlockId = -1,
            Code = new AssetLocation("game", "rock-granite"),
            DrawType = EnumDrawType.Cube,
            BlockMaterial = EnumBlockMaterial.Stone,
            RenderPass = EnumChunkRenderPass.Opaque,
        }));

        Assert.False(OptimumApiBridge.IsEligibleChiselLodBlock(new Block
        {
            BlockId = 10,
            Code = null,
            DrawType = EnumDrawType.Cube,
            BlockMaterial = EnumBlockMaterial.Stone,
            RenderPass = EnumChunkRenderPass.Opaque,
        }));

        Assert.False(OptimumApiBridge.IsEligibleChiselLodBlock(new Block
        {
            BlockId = 10,
            Code = new AssetLocation("game", "unknown"),
            DrawType = EnumDrawType.Cube,
            BlockMaterial = EnumBlockMaterial.Stone,
            RenderPass = EnumChunkRenderPass.Opaque,
        }));

        Assert.False(OptimumApiBridge.IsEligibleChiselLodBlock(new Block
        {
            BlockId = 10,
            Code = new AssetLocation("somemod", "UNKNOWN"),
            DrawType = EnumDrawType.Cube,
            BlockMaterial = EnumBlockMaterial.Stone,
            RenderPass = EnumChunkRenderPass.Opaque,
        }));

        Assert.False(OptimumApiBridge.IsEligibleChiselLodBlock(new Block
        {
            BlockId = 10,
            Code = new AssetLocation("game", "emptything"),
            DrawType = EnumDrawType.Empty,
            BlockMaterial = EnumBlockMaterial.Stone,
            RenderPass = EnumChunkRenderPass.Opaque,
        }));

        Assert.False(OptimumApiBridge.IsEligibleChiselLodBlock(new Block
        {
            BlockId = 10,
            Code = new AssetLocation("game", "airlike"),
            DrawType = EnumDrawType.Cube,
            BlockMaterial = EnumBlockMaterial.Air,
            RenderPass = EnumChunkRenderPass.Opaque,
        }));

        Assert.False(OptimumApiBridge.IsEligibleChiselLodBlock(new Block
        {
            BlockId = 10,
            Code = new AssetLocation("game", "meta-collider"),
            DrawType = EnumDrawType.Cube,
            BlockMaterial = EnumBlockMaterial.Meta,
            RenderPass = EnumChunkRenderPass.Opaque,
        }));

        Assert.False(OptimumApiBridge.IsEligibleChiselLodBlock(new Block
        {
            BlockId = 10,
            Code = new AssetLocation("game", "glass-plain"),
            DrawType = EnumDrawType.Cube,
            BlockMaterial = EnumBlockMaterial.Glass,
            RenderPass = EnumChunkRenderPass.Transparent,
        }));

        Assert.False(OptimumApiBridge.IsEligibleChiselLodBlock(new Block
        {
            BlockId = 10,
            Code = new AssetLocation("game", "ember"),
            DrawType = EnumDrawType.Cube,
            BlockMaterial = EnumBlockMaterial.Stone,
            RenderPass = EnumChunkRenderPass.Opaque,
            VertexFlags = new VertexFlags(12),
        }));

        Assert.True(OptimumApiBridge.IsEligibleChiselLodBlock(new Block
        {
            BlockId = 10,
            Code = new AssetLocation("game", "rock-granite"),
            DrawType = EnumDrawType.Cube,
            BlockMaterial = EnumBlockMaterial.Stone,
            RenderPass = EnumChunkRenderPass.Opaque,
            VertexFlags = new VertexFlags(0),
        }));

        Assert.True(OptimumApiBridge.IsEligibleChiselLodBlock(new Block
        {
            BlockId = 10,
            Code = new AssetLocation("game", "rock-granite"),
            DrawType = EnumDrawType.Cube,
            BlockMaterial = EnumBlockMaterial.Stone,
            RenderPass = EnumChunkRenderPass.Opaque,
            VertexFlags = null,
        }));
    }

    [Fact]
    public void ChiselLodIsPlaceholderMesh_RejectsStructuralUnknownRefAndUnknownTexture()
    {
        var unknownTex = new TextureAtlasPosition
        {
            atlasTextureId = 255,
            x1 = 0.05f,
            y1 = 0.25f,
            x2 = 0.06f,
            y2 = 0.27f,
        };
        var tessMgr = StubProxy.Create<ITesselatorManager>(_ => null);
        var atlas = StubProxy.Create<IBlockTextureAtlasAPI>(name => name == "get_UnknownTexturePosition" ? unknownTex : null);
        var capi = StubProxy.Create<ICoreClientAPI>(name => name switch
        {
            "get_TesselatorManager" => tessMgr,
            "get_BlockTextureAtlas" => atlas,
            _ => null,
        });

        Assert.True(OptimumApiBridge.IsPlaceholderChiselLodMesh(capi, null));
        Assert.True(OptimumApiBridge.IsPlaceholderChiselLodMesh(null, CreateValidQuadMesh(verticesCount: 0, indicesCount: 36, faces: 6, atlasId: 255, u0: 0.5f, v0: 0.5f)));
        Assert.True(OptimumApiBridge.IsPlaceholderChiselLodMesh(null, CreateValidQuadMesh(verticesCount: 24, indicesCount: 0, faces: 6, atlasId: 255, u0: 0.5f, v0: 0.5f)));
        Assert.True(OptimumApiBridge.IsPlaceholderChiselLodMesh(null, CreateValidQuadMesh(verticesCount: 24, indicesCount: 36, faces: 0, atlasId: 255, u0: 0.5f, v0: 0.5f)));

        var noXyzFacesArray = CreateValidQuadMesh(24, 36, 6, 255, 0.5f, 0.5f);
        noXyzFacesArray.XyzFaces = null;
        Assert.True(OptimumApiBridge.IsPlaceholderChiselLodMesh(capi, noXyzFacesArray));

        var noTextureIds = CreateValidQuadMesh(24, 36, 6, 255, 0.5f, 0.5f);
        noTextureIds.TextureIds = Array.Empty<int>();
        Assert.True(OptimumApiBridge.IsPlaceholderChiselLodMesh(capi, noTextureIds));

        var validGraniteMesh = CreateValidQuadMesh(24, 36, 6, atlasId: 255, u0: 0.5f, v0: 0.5f);
        Assert.False(OptimumApiBridge.IsPlaceholderChiselLodMesh(capi, validGraniteMesh));

        // Registered unknownBlockModelData reference must be rejected even if structurally populated
        var registeredUnknownMesh = CreateValidQuadMesh(24, 36, 6, atlasId: 255, u0: 0.5f, v0: 0.5f);
        OptimumApiBridge.RegisterUnknownBlockModelData(tessMgr, registeredUnknownMesh);
        try
        {
            Assert.True(OptimumApiBridge.IsPlaceholderChiselLodMesh(capi, registeredUnknownMesh));
            Assert.False(OptimumApiBridge.IsPlaceholderChiselLodMesh(capi, validGraniteMesh));
        }
        finally
        {
            OptimumApiBridge.UnregisterUnknownBlockModelData(tessMgr);
        }
        Assert.False(OptimumApiBridge.IsPlaceholderChiselLodMesh(capi, registeredUnknownMesh));

        // Face using UnknownTexturePosition without TextureIndices (defaults to slot 0) must be rejected
        var unknownTexMesh = CreateValidQuadMesh(24, 36, 6, atlasId: 255, u0: 0.055f, v0: 0.26f);
        Assert.True(OptimumApiBridge.IsPlaceholderChiselLodMesh(capi, unknownTexMesh));

        // Multi-atlas mesh where only face 1 points at UnknownTexturePosition must be rejected
        var multiAtlasUnknown = CreateValidQuadMesh(8, 12, 2, atlasId: 100, u0: 0.5f, v0: 0.5f);
        multiAtlasUnknown.TextureIds = new[] { 100, 255 };
        multiAtlasUnknown.TextureIndices = new byte[] { 0, 1 };
        multiAtlasUnknown.TextureIndicesCount = 2;
        multiAtlasUnknown.Uv = new[]
        {
            0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f, 0.5f,
            0.052f, 0.252f, 0.058f, 0.252f, 0.058f, 0.268f, 0.052f, 0.268f,
        };
        Assert.True(OptimumApiBridge.IsPlaceholderChiselLodMesh(capi, multiAtlasUnknown));

        // Same UVs on a DIFFERENT atlas ID must NOT trigger false positive
        var differentAtlasMesh = CreateValidQuadMesh(24, 36, 6, atlasId: 100, u0: 0.055f, v0: 0.26f);
        Assert.False(OptimumApiBridge.IsPlaceholderChiselLodMesh(capi, differentAtlasMesh));

        // Zero-area UnknownTexturePosition must NOT trigger false positive at UV origin
        var zeroAtlasPos = new TextureAtlasPosition { atlasTextureId = 255 };
        var zeroAtlas = StubProxy.Create<IBlockTextureAtlasAPI>(name => name == "get_UnknownTexturePosition" ? zeroAtlasPos : null);
        var zeroCapi = StubProxy.Create<ICoreClientAPI>(name => name switch
        {
            "get_TesselatorManager" => tessMgr,
            "get_BlockTextureAtlas" => zeroAtlas,
            _ => null,
        });
        var originUvMesh = CreateValidQuadMesh(24, 36, 6, atlasId: 255, u0: 0f, v0: 0f);
        Assert.False(OptimumApiBridge.IsPlaceholderChiselLodMesh(zeroCapi, originUvMesh));
    }

    [Fact]
    public void ChiselLodShapeTesselatorManager_TracksAndUnregistersUnknownMeshWhenDonorBuilt()
    {
        string donorPath = FindOptionalRepositoryFile("build/VintagestoryLib/bin/Release/net10.0/VintagestoryLib.dll");
        if (donorPath == null)
        {
            return;
        }

        Assembly donorAsm = Assembly.LoadFrom(donorPath);
        Type stmType = donorAsm.GetType("Vintagestory.Client.NoObf.ShapeTesselatorManager", throwOnError: true)!;
        object stm = RuntimeHelpers.GetUninitializedObject(stmType);

        // Give unknownMesh valid-looking structural fields so only ReferenceEquals registration proves tracking
        MeshData unknownMesh = CreateValidQuadMesh(24, 36, 6, atlasId: 255, u0: 0.5f, v0: 0.5f);
        stmType.GetField("unknownBlockModelData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stm, unknownMesh);
        stmType.GetField("blockModelDatas", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(stm, new MeshData[16]);

        MethodInfo tesselateBlock = stmType.GetMethod("TesselateBlock", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
        MethodInfo disposeMethod = stmType.GetMethod("Dispose", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;

        var missingBlock = new Block
        {
            BlockId = 5,
            Code = new AssetLocation("missingmod", "carved-oak"),
            IsMissing = true,
        };

        var capi = StubProxy.Create<ICoreClientAPI>(name => name == "get_TesselatorManager" ? stm : null);
        tesselateBlock.Invoke(stm, new object?[] { missingBlock, false });
        try
        {
            Assert.True(OptimumApiBridge.IsPlaceholderChiselLodMesh(capi, unknownMesh));
        }
        finally
        {
            disposeMethod.Invoke(stm, Array.Empty<object?>());
        }

        Assert.False(OptimumApiBridge.IsPlaceholderChiselLodMesh(capi, unknownMesh));
    }

    private static MeshData CreateValidQuadMesh(int verticesCount, int indicesCount, int faces, int atlasId, float u0, float v0)
    {
        int allocVertices = Math.Max(verticesCount, Math.Max(faces * 4, 4));
        float[] uv = new float[allocVertices * 2];
        for (int i = 0; i < uv.Length; i += 2)
        {
            uv[i] = u0;
            uv[i + 1] = v0;
        }

        return new MeshData
        {
            VerticesCount = verticesCount,
            IndicesCount = indicesCount,
            XyzFaces = faces > 0 ? new byte[faces] : Array.Empty<byte>(),
            XyzFacesCount = faces,
            VerticesPerFace = 4,
            TextureIds = new[] { atlasId },
            Uv = uv,
        };
    }

    private static string? FindOptionalRepositoryFile(string relativePath)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            directory = directory.Parent;
        }
        return null;
    }

    public class StubProxy : DispatchProxy
    {
        private System.Func<string, object?> resolver = _ => null;

        public static T Create<T>(System.Func<string, object?> resolver) where T : class
        {
            object proxy = Create<T, StubProxy>();
            ((StubProxy)proxy).resolver = resolver;
            return (T)proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            return targetMethod != null ? resolver(targetMethod.Name) : null;
        }
    }

    private static string FindRepositoryFile(string relativePath)
    {
        DirectoryInfo directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
