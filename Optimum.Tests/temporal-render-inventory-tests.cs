using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// A checked inventory of the game's render systems relevant to motion vectors and
/// jittered projection. Each assertion pins down a string in the actual source tree
/// (donor decompile or hand-maintained mod source) so the TAA plan's render-system
/// table stays true as the game and Optimum's patches evolve. This is not behavior
/// coverage - it is a tripwire: if any of these registrations move or are renamed,
/// the TAA plan needs to be revisited before it is trusted.
/// </summary>
public class TemporalRenderInventoryTests
{
    [Fact]
    public void ChunkRendererDrawsOpaqueAndTopsoilWithCameraMatrixOriginAndLiquidInRenderOIT()
    {
        string chunkRenderer = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");

        Assert.Contains("public void RenderOpaque(float dt)", chunkRenderer);
        Assert.Contains("internal void RenderOIT(float deltaTime)", chunkRenderer);
        Assert.Contains("ShaderProgramChunkopaque chunkopaque = ShaderPrograms.Chunkopaque;", chunkRenderer);
        Assert.Contains("ShaderProgramChunktopsoil chunktopsoil = ShaderPrograms.Chunktopsoil;", chunkRenderer);
        Assert.Contains("ShaderProgramChunkliquid chunkliquid = ShaderPrograms.Chunkliquid;", chunkRenderer);

        int renderOpaqueStart = chunkRenderer.IndexOf("public void RenderOpaque(float dt)");
        int renderOitStart = chunkRenderer.IndexOf("internal void RenderOIT(float deltaTime)");
        Assert.True(renderOpaqueStart >= 0 && renderOitStart > renderOpaqueStart);

        string renderOpaqueBody = chunkRenderer.Substring(renderOpaqueStart, renderOitStart - renderOpaqueStart);
        Assert.Contains("game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);", renderOpaqueBody);
        Assert.Contains("chunkopaque.TerrainTex2D", renderOpaqueBody);
        Assert.Contains("chunktopsoil.TerrainTex2D", renderOpaqueBody);

        string renderOitBody = chunkRenderer.Substring(renderOitStart, 400);
        Assert.Contains("game.GlLoadMatrix(game.MainCamera.CameraMatrixOrigin);", renderOitBody);
    }

    [Fact]
    public void SystemRenderParticlesRegistersOpaqueAndOitRenderers()
    {
        string particles = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderParticles.cs");

        Assert.Contains(
            "game.eventManager.RegisterRenderer(OnRenderFrame3D, EnumRenderStage.Opaque, \"rep-opa\", 0.6);",
            particles);
        Assert.Contains(
            "game.eventManager.RegisterRenderer(OnRenderFrame3DOIT, EnumRenderStage.OIT, \"rep-oit\", 0.6);",
            particles);
    }

    [Fact]
    public void SystemRenderOITLayersUsesSixDrawBuffers()
    {
        string oitLayers = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderOITLayers.cs");

        Assert.Contains("new DrawBuffersEnum[6]", oitLayers);
        Assert.Contains("DrawBuffersEnum.ColorAttachment0", oitLayers);
        Assert.Contains("DrawBuffersEnum.ColorAttachment5", oitLayers);
    }

    [Fact]
    public void EntityShapeRendererUploadsAnimationUboPerEntity()
    {
        string entityShapeRenderer = Read("VSEssentials/EntityRenderer/EntityShapeRenderer.cs");

        Assert.Contains(
            "prog.UBOs[\"Animation\"].Update(entity.AnimManager.Animator.Matrices, 0, entity.AnimManager.Animator.MaxJointId * 16 * 4);",
            entityShapeRenderer);
    }

    [Fact]
    public void ModSystemFpHandsCreatesItsOwnAnimationUbo()
    {
        string fpHands = Read("VSEssentials/EntityRenderer/ModSystemFpHands.cs");

        Assert.Contains(
            "fpModeHandShader.UBOs[\"Animation\"] = capi.Render.CreateUBO(fpModeHandShader, 0, \"Animation\", GlobalConstants.MaxAnimatedElements * 16 * 4);",
            fpHands);
    }

    [Fact]
    public void EntityItemRendererUsesTheStandardShader()
    {
        string entityItemRenderer = Read("VSEssentials/EntityRenderer/EntityItemRenderer.cs");

        Assert.Contains("IStandardShaderProgram prog = null;", entityItemRenderer);
        Assert.Contains("prog = rapi.StandardShader;", entityItemRenderer);
    }

    [Fact]
    public void QuernTopRendererUsesTheStandardShader()
    {
        string quernTopRenderer = Read("VSSurvivalMod/BlockEntityRenderer/QuernTopRenderer.cs");

        Assert.Contains("IStandardShaderProgram prog = rpi.PreparedStandardShader(pos.X, pos.Y, pos.Z);", quernTopRenderer);
    }

    [Fact]
    public void MechNetworkRendererIsInstanced()
    {
        string mechNetworkRenderer = Read("VSSurvivalMod/Systems/MechanicalPower/Renderer/MechNetworkRenderer.cs");

        Assert.Contains("1. Use instanced rendering to issue one draw call for all mech.power blocks of one type.", mechNetworkRenderer);
        Assert.Contains("prog = capi.Shader.GetProgramByName(\"instanced\");", mechNetworkRenderer);
    }

    [Fact]
    public void RiftRendererRendersAtAfterBlit()
    {
        string riftRenderer = Read("VSSurvivalMod/Systems/Rifts/RiftRenderer.cs");

        Assert.Contains("capi.Event.RegisterRenderer(this, EnumRenderStage.AfterBlit, \"riftrenderer\");", riftRenderer);
    }

    [Fact]
    public void KnappingClayFormAndAnvilRenderersRenderAtAfterFinalComposition()
    {
        string knapping = Read("VSSurvivalMod/BlockEntityRenderer/KnappingRenderer.cs");
        string clayForm = Read("VSSurvivalMod/BlockEntityRenderer/ClayFormRenderer.cs");
        string anvil = Read("VSSurvivalMod/BlockEntityRenderer/AnvilWorkItemRenderer.cs");

        Assert.Contains("capi.Event.RegisterRenderer(this, EnumRenderStage.AfterFinalComposition, \"knappingsurface\");", knapping);
        Assert.Contains("if (stage == EnumRenderStage.AfterFinalComposition)", knapping);

        Assert.Contains("if (stage == EnumRenderStage.AfterFinalComposition)", clayForm);
        Assert.Contains("api.Event.UnregisterRenderer(this, EnumRenderStage.AfterFinalComposition);", clayForm);

        Assert.Contains("if (stage == EnumRenderStage.AfterFinalComposition)", anvil);
        Assert.Contains("api.Event.UnregisterRenderer(this, EnumRenderStage.AfterFinalComposition);", anvil);
    }

    [Fact]
    public void VulkanPresentBlitIsTheOnlyYFlip()
    {
        string vulkanDevice = Read("Optimum.Render.Vulkan/VulkanDevice.cs");

        Assert.Contains("This inverted blit is the entire Y-flip story for the backend.", vulkanDevice);
        Assert.Contains("// Source Y runs backwards: this is the flip.", vulkanDevice);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
