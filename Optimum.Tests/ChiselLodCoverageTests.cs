using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class ChiselLodCoverageTests
{
    [Theory]
    [InlineData("patches/VSSurvivalMod/Systems/Microblock/BEMicroBlock.cs.patch")]
    [InlineData("patches/VSSurvivalMod/Systems/Microblock/BEMicroBlock.cs.patch")]
    public void ChiselLodRegistersFullMeshForMediumRange(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.DoesNotContain("AddMeshData(Mesh, cmapdata, 0)", source);
        Assert.DoesNotContain("AddMeshData(Mesh, 0)", source);
        Assert.Contains("AddMeshData(Mesh, cmapdata, 2)", source);
        Assert.Contains("AddMeshData(Mesh, 2)", source);
        Assert.Contains("AddMeshData(lodMesh, cmapdata, 3)", source);
        Assert.Contains("AddMeshData(lodMesh, 3)", source);
    }

    [Theory]
    [InlineData("patches/VSSurvivalMod/Systems/Microblock/BEMicroBlock.cs.patch")]
    [InlineData("patches/VSSurvivalMod/Systems/Microblock/BEMicroBlock.cs.patch")]
    public void ChiselLodBuildsProxyFromMajorityMaterial(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.DoesNotContain("Block primaryBlock = Api.World.GetBlock(BlockIds[0])", source);
        Assert.DoesNotContain("tesselator.TesselateBlock(primaryBlock", source);
        Assert.Contains("GetMajorityMaterialId()", source);
        Assert.Contains("TesselatorManager.GetDefaultBlockMesh(primaryBlock)?.Clone()", source);
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
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch")]
    public void ChiselLodRoutesIntoSeparateChunkPools(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("currentOptimumChiselModeldataByRenderPassByLodLevel", source);
        Assert.Contains("centerOptimumChiselModeldataByRenderPassByLodLevel", source);
        Assert.Contains("edgeOptimumChiselModeldataByRenderPassByLodLevel", source);
        Assert.Contains("OptimumConfig.RouteChiselLodMeshes", source);
        Assert.Contains("centerOptimumChiselModeldataByRenderPassByLodLevel, out TesselatedChunkPart[] centerOptimumChiselParts, true", source);
        Assert.Contains("edgeOptimumChiselModeldataByRenderPassByLodLevel, out TesselatedChunkPart[] edgeOptimumChiselParts, true", source);
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
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"BuildBlockPolygons\", 3", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"BuildBlockPolygons_EdgeOnly\", 3", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"BuildDecorPolygons\", 5", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkTesselator\", \"GetMeshPoolForPass\", 3", programSource);
        Assert.Contains("\"Vintagestory.Client.NoObf.TesselatedChunkPart\", \"AddModelAndStoreLocation\", 8", programSource);
        Assert.Contains("\"populateTesselatedChunkPart\"", programSource);
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
        Assert.Contains("location.OptimumUseChiselLodDistance", source);
        Assert.Contains("InFrustumAndRange(location.FrustumCullSphere, location.FrustumVisible, location.LodLevel, location.OptimumUseChiselLodDistance)", source);
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
        Assert.Contains("distance <= chiselDistanceSq && distance < ViewDistanceSq", source);
        Assert.Contains("case 2:", source);
        Assert.Contains("case 3:", source);
    }

    [Theory]
    [InlineData("patches/VSSurvivalMod/Systems/Microblock/BEMicroBlock.cs.patch")]
    [InlineData("patches/VSSurvivalMod/Systems/Microblock/BEMicroBlock.cs.patch")]
    public void ChiselLodMicroblockRecordsDiagnostics(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("RouteChiselLodMeshes = true", source);
        Assert.Contains("RouteChiselLodMeshes = false", source);
        Assert.Contains("OptimumDiagnostics.RecordChiselLod", source);
        Assert.Contains("lodstats", source);
        Assert.Contains("lodreset", source);
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
