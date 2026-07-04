using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class ChiselLodCoverageTests
{
    [Theory]
    [InlineData("VSSurvivalMod/Systems/Microblock/BEMicroBlock.cs")]
    [InlineData("patches/VSSurvivalMod/Systems/Microblock/BEMicroBlock.cs.patch")]
    public void ChiselLodRegistersFullMeshForMediumRange(string relativePath)
    {
        string source = File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.DoesNotContain("AddMeshData(Mesh, cmapdata, 0)", source);
        Assert.DoesNotContain("AddMeshData(Mesh, 0)", source);
        Assert.Contains("AddMeshData(Mesh, cmapdata, 2)", source);
        Assert.Contains("AddMeshData(Mesh, 2)", source);
        Assert.Contains("AddMeshData(lodMesh, cmapdata, 3)", source);
        Assert.Contains("AddMeshData(lodMesh, 3)", source);
    }

    [Theory]
    [InlineData("VSSurvivalMod/Systems/Microblock/BEMicroBlock.cs")]
    [InlineData("patches/VSSurvivalMod/Systems/Microblock/BEMicroBlock.cs.patch")]
    public void ChiselLodBuildsProxyFromMajorityMaterial(string relativePath)
    {
        string source = File.ReadAllText(FindRepositoryFile(relativePath));

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
        string source = File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("RouteChiselLodMeshes", source);
        Assert.Contains("OptimumDiagnostics", source);
        Assert.Contains("RecordChiselLod", source);
        Assert.Contains("GetChiselLodSummary", source);
    }

    [Theory]
    [InlineData("build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkTesselator.cs.patch")]
    public void ChiselLodRoutesIntoSeparateChunkPools(string relativePath)
    {
        string source = File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("currentOptimumChiselModeldataByRenderPassByLodLevel", source);
        Assert.Contains("centerOptimumChiselModeldataByRenderPassByLodLevel", source);
        Assert.Contains("edgeOptimumChiselModeldataByRenderPassByLodLevel", source);
        Assert.Contains("OptimumConfig.RouteChiselLodMeshes", source);
        Assert.Contains("centerOptimumChiselModeldataByRenderPassByLodLevel, out TesselatedChunkPart[] centerOptimumChiselParts, true", source);
        Assert.Contains("edgeOptimumChiselModeldataByRenderPassByLodLevel, out TesselatedChunkPart[] edgeOptimumChiselParts, true", source);
    }

    [Theory]
    [InlineData("VintagestoryApi/Client/MeshPool/MeshDataPool.cs")]
    [InlineData("patches/VintagestoryApi/Client/MeshPool/MeshDataPool.cs.patch")]
    public void ChiselLodPoolLocationsCarryCustomDistanceFlag(string relativePath)
    {
        string source = File.ReadAllText(FindRepositoryFile(relativePath));

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
    [InlineData("VSSurvivalMod/Systems/Microblock/BEMicroBlock.cs")]
    [InlineData("patches/VSSurvivalMod/Systems/Microblock/BEMicroBlock.cs.patch")]
    public void ChiselLodMicroblockRecordsDiagnostics(string relativePath)
    {
        string source = File.ReadAllText(FindRepositoryFile(relativePath));

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
