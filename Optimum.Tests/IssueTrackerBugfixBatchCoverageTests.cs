using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public class IssueTrackerBugfixBatchCoverageTests
{
    [Fact]
    public void FallingBlockGenMeshNoLongerAliasesTheSharedDefaultBlockMeshCache()
    {
        // #9220-class: GetDefaultBlockMesh returns ShapeTesselatorManager's
        // shared blockModelDatas[block.BlockId], not a fresh copy. Assigning
        // it into the reused `mesh` field used to let a later genMesh()
        // call's mesh.Clear() wipe that shared cache.
        string source = PatchReader.ReadPatch("patches/VSEssentials/Entities/EntityBlockFalling.cs.patch");

        Assert.DoesNotContain("mesh = capi.TesselatorManager.GetDefaultBlockMesh(entity.Block);", source);
        Assert.Contains("MeshData meshToUpload = mesh;", source);
        Assert.Contains("meshToUpload = capi.TesselatorManager.GetDefaultBlockMesh(entity.Block);", source);
        Assert.Contains("capi.Render.UploadMultiTextureMesh(meshToUpload);", source);
    }

    [Theory]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client/RenderAPIBase.cs.patch")]
    [InlineData("patches/VintagestoryLib/Vintagestory.Client/RenderAPIBase.cs.patch")]
    public void RenderMultiTextureMeshSkipsDisposedMeshrefs(string relativePath)
    {
        // #8881/#8950/#8982-class: rendering a disposed meshref feeds freed
        // GL handles into plat.RenderMesh.
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("if (mmr == null || mmr.Disposed) return;", source);
        Assert.Contains("if (vao == null || vao.Disposed) continue;", source);
    }

    [Fact]
    public void RenderMultiTextureMeshIsRegisteredAsACecilTransplantTarget()
    {
        string programSource = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/Program.cs"));
        Assert.Contains("\"Vintagestory.Client.RenderAPIBase\", \"RenderMultiTextureMesh\", 3", programSource);
    }

    [Fact]
    public void PsychedelicPitchDriftIsClampedToTheSameBoundsAsNormalMouseLook()
    {
        // #9381: Pos.Pitch/MousePitch accumulate unbounded here, outside
        // ClientMain's normal clamp-then-sync path (UpdateCameraYawPitch),
        // letting prolonged psychedelic intensity drift pitch past the
        // poles and flip the camera. Same bounds ClientMain uses.
        string source = File.ReadAllText(FindRepositoryFile("VintagestoryApi/Client/Render/PerceptionEffects/PsychedelicPerceptionEffect.cs"));

        Assert.DoesNotContain("Pos.Pitch += dp;", source);
        Assert.DoesNotContain("MousePitch += dp;", source);
        Assert.Contains("GameMath.Clamp(capi.World.Player.Entity.Pos.Pitch + dp, 1.5857964f, 4.697389f)", source);
        Assert.Contains("GameMath.Clamp(capi.Input.MousePitch + dp, 1.5857964f, 4.697389f)", source);
    }

    [Theory]
    [InlineData("scripts/package.ps1")]
    [InlineData("scripts/package-linux.ps1")]
    [InlineData("scripts/package-macos.ps1")]
    [InlineData("scripts/package-linux.sh")]
    [InlineData("scripts/package-macos.sh")]
    public void PackageScriptsPatchFromPristineVanillaLib(string relativePath)
    {
        string source = relativePath.EndsWith(".patch") ? PatchReader.ReadPatch(relativePath) : File.ReadAllText(FindRepositoryFile(relativePath));

        Assert.Contains("VintagestoryLib.vanilla.dll", source);
    }

    [Fact]
    public void IlHookSkipsAlreadyInsertedHookCalls()
    {
        string source = File.ReadAllText(FindRepositoryFile("Optimum.Patcher/ILHook.cs"));

        Assert.Contains("AlreadyCallsHook", source);
        Assert.Contains("continue;", source);
    }

    [Fact]
    public void DeployValidatesShaderOverlaysBeforeCopying()
    {
        string makefile = File.ReadAllText(FindRepositoryFile("Makefile"));
        string validator = File.ReadAllText(FindRepositoryFile("scripts/validate-shader-assets.sh"));

        Assert.Contains("deploy: patch-il check-shaders", makefile);
        Assert.Contains("void\\s+main", validator);
    }

    [Fact]
    public void OptimumSettingsTabMovesBackButtonAfterInjectedTab()
    {
        string source = PatchReader.ReadPatch("patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiCompositeSettings.cs.patch");

        Assert.Contains("oButtonBounds.WithFixedWidth(w);", source);
        Assert.Contains("backButtonBounds.FixedRightOf(oButtonBounds, 25.0);", source);
    }

    private static string FindRepositoryFile(string relativePath)
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

        throw new FileNotFoundException($"Could not find {relativePath} from {AppContext.BaseDirectory}.");
    }
}
