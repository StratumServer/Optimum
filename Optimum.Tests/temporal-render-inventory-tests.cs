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

    /// <summary>
    /// TAA P4 statuses for the classes the plan's inventory table calls out for
    /// this phase, pinned to the code that implements them.
    /// </summary>
    [Fact]
    public void TheSkyAndCloudRowIsTheSkyMotionPassAndNoCelestialWriter()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        string clientMain = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");

        // The pass exists, is registered, and is called last in the scene phase.
        Assert.Contains("internal bool RenderOptimumSkyMotion()", platform);
        Assert.Contains("optimumSkyMotionPlatform.RenderOptimumSkyMotion();", clientMain);
        Assert.Contains(
            "RegisterOptimumShaderProgram(\"taa-skymotion\", ShaderPrograms.TaaSkyMotion = new ShaderProgram());",
            Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs"));

        // Night sky and sky colour draw with the depth test off, so their pixels
        // keep depth 1 and the resolve's infinite-direction fallback owns them;
        // the sun and moon test depth but never write it. That is the whole
        // reason none of the three has a writer.
        Assert.Contains("GlDisableDepthTest();",
            Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderNightSky.cs"));
        Assert.Contains("GlDisableDepthTest();",
            Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSkyColor.cs"));
        Assert.Contains("GlDepthMask(flag: false);",
            Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderSunMoon.cs"));

        // The aurora and the volumetric clouds are OIT content: their coverage
        // reaches the sky pass through the Transparent target's revealage.
        Assert.Contains("capi.Event.RegisterRenderer(this, EnumRenderStage.OIT, \"aurora\");",
            Read("VSEssentials/Systems/Weather/AuroraRenderer.cs"));
        // The aurora goes through oit.fsh, so its coverage lands in the very
        // revealage attachment the sky pass reads. The vanilla shaders are
        // proprietary and never committed, so an un-bootstrapped checkout skips.
        string? aurora = VanillaShaderArchive.TryRead("shaders/aurora.fsh");
        if (aurora != null) Assert.Contains("#include oit.fsh", aurora);
    }

    [Fact]
    public void TheDecalRowIsAWriterOfItsOwn()
    {
        string decals = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/SystemRenderDecals.cs");

        // Drawn on Primary in the AfterOIT stage, and instrumented there.
        Assert.Contains("EnumRenderStage.AfterOIT, \"decals\", 0.5", decals);
        Assert.Contains("optimumPlatform.BeginMotionWrite()", decals);
        Assert.Contains("SetOptimumMotionUniforms(shaderProgramDecals);", decals);
        Assert.Contains("layout(location = TAAMOTIONLOCATION) out vec4 outMotion;",
            Read("sources/shaders/decals.fsh"));
    }

    [Fact]
    public void TheLateStageRowsAreRefusedTheMotionWindow()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        string clientMain = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");

        Assert.Contains("if (!OptimumTemporal.Frame.JitterActive) return false;", platform);
        Assert.Contains("OptimumTemporal.Frame.JitterActive = false;", clientMain);
    }

    [Fact]
    public void VulkanPresentBlitIsTheOnlyYFlip()
    {
        // Phase 1B step 4 moved the blit into the present path (Submit B); the
        // flip itself is unchanged and still happens exactly once.
        string presentPath = Read("Optimum.Render.Vulkan/Present/IPresentPath.cs");
        string vulkanDevice = Read("Optimum.Render.Vulkan/VulkanDevice.cs");

        Assert.Contains("This inverted blit is the entire Y-flip story for the backend.", presentPath);
        Assert.Contains("// Source Y runs backwards: this is the flip.", presentPath);
        Assert.Contains("blit.SrcOffsets.Element0 = new Offset3D(0, (int)source.Height, 0);", presentPath);
        Assert.DoesNotContain("this is the flip", vulkanDevice);
        Assert.DoesNotContain("CmdBlitImage", vulkanDevice.Substring(vulkanDevice.IndexOf("    public void Present()", System.StringComparison.Ordinal)));
        Assert.Contains("_presentPath = new BlitPresentPath(", vulkanDevice);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
