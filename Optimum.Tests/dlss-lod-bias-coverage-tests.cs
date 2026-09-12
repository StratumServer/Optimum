using System;
using System.IO;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// DLSS plan, Phase 6: the texture LOD bias follows the upscaler's plan.
///
/// The bias an upscaler asks for (DLSS Programming Guide section 3.5,
/// log2(render / display) - 1) is published when the vendor feature is created,
/// which is after the shader load that made the terrain sampler objects and after
/// the frame's terrain was drawn - and a preset change deliberately reloads no
/// shaders. So the value has to be re-applied at publication, at the shader load,
/// on every preset change and when the upscaler stands down, at both places it
/// reaches the GPU: the parameter on the atlas textures and the chunkopaque /
/// chunktopsoil sampler objects that override it.
///
/// The GPU half of this is
/// <c>Optimum.Render.Vulkan.Tests/TerrainLodBiasFollowsThePlanTests</c>, which
/// measures the bias really in effect on a device.
/// </summary>
public class DlssLodBiasCoverageTests
{
    [Fact]
    public void TheAppliedBiasRuleKeepsTheUpscalerOffPathUntouched()
    {
        float applied = OptimumConfig.AppliedTerrainLodBias;
        bool taa = OptimumConfig.Taa;
        float scale = OptimumConfig.RenderScale;
        int[] atlases = OptimumConfig.LodBiasedAtlases;
        try
        {
            OptimumConfig.Taa = false;
            OptimumConfig.RenderScale = 1.0f;
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfig.InvalidateTerrainLodBias();
            OptimumConfig.RegisterLodBiasedAtlases(new[] { 1 });

            // Nothing asks for a bias: nothing is pending, so no call is made.
            Assert.Equal(0f, OptimumConfig.EffectiveTerrainLodBias);
            Assert.False(OptimumConfig.TerrainLodBiasPending());

            // A plan moves it, and it stays pending until it has been applied.
            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.SetUpscalerPlan(0.5f, -2.0f);
            Assert.Equal(-2.0f, OptimumConfig.EffectiveTerrainLodBias, 3);
            Assert.True(OptimumConfig.TerrainLodBiasPending());
            OptimumConfig.NoteTerrainLodBiasApplied(-2.0f, reachedAtlases: true);
            Assert.False(OptimumConfig.TerrainLodBiasPending());
            Assert.Equal(-2.0f, OptimumConfig.AppliedTerrainLodBias, 3);

            // A preset change is a new value, pending again.
            OptimumConfig.SetUpscalerPlan(1f / 3f, -2.585f);
            Assert.True(OptimumConfig.TerrainLodBiasPending());

            // Standing the upscaler down is a restore, not a no-op: 0 is pending
            // because something non-zero was written, and recording it puts the
            // state back to "never touched".
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfig.Upscaler = "off";
            Assert.Equal(0f, OptimumConfig.EffectiveTerrainLodBias);
            Assert.True(OptimumConfig.TerrainLodBiasPending());
            OptimumConfig.NoteTerrainLodBiasApplied(0f, reachedAtlases: true);
            Assert.True(float.IsNaN(OptimumConfig.AppliedTerrainLodBias));
            Assert.False(OptimumConfig.TerrainLodBiasPending());

            // An apply that reached no atlas is not recorded: the renderer can
            // publish a plan before the client has registered them, and the
            // per-frame poll has to finish the job.
            OptimumConfig.Upscaler = "dlss";
            OptimumConfig.SetUpscalerPlan(0.5f, -2.0f);
            OptimumConfig.NoteTerrainLodBiasApplied(-2.0f, reachedAtlases: false);
            Assert.True(OptimumConfig.TerrainLodBiasPending());
        }
        finally
        {
            OptimumConfig.Upscaler = "off";
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfig.RegisterLodBiasedAtlases(atlases);
            OptimumConfig.Taa = taa;
            OptimumConfig.RenderScale = scale;
            OptimumConfig.InvalidateTerrainLodBias();
            if (!float.IsNaN(applied)) OptimumConfig.NoteTerrainLodBiasApplied(applied, true);
        }
    }

    [Fact]
    public void OneApplierWritesBothCallSitesAndRemembersWhatItWrote()
    {
        string registry = PatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");

        Assert.Contains("public static void ApplyOptimumLodBias()", registry);
        Assert.Contains("if (!OptimumConfig.TerrainLodBiasPending())", registry);
        Assert.Contains("int[] atlases = OptimumConfig.LodBiasedAtlases;", registry);
        Assert.Contains("platform.SetTextureLodBias(atlases, bias);", registry);
        Assert.Contains("ApplyOptimumTerrainSamplerLodBias(bias);", registry);
        Assert.Contains("OptimumConfig.NoteTerrainLodBiasApplied(bias, reachedAtlases);", registry);
        // The shader load creates new sampler objects at the driver default, so
        // it forgets what was applied and writes again - no reload needed
        // anywhere else for the same reason.
        Assert.Contains("OptimumConfig.InvalidateTerrainLodBias();", registry);
        Assert.Contains("ApplyOptimumLodBias();", registry);
    }

    [Fact]
    public void TheRendererReAppliesWheneverThePlanMoves()
    {
        string upscale = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Upscale.cs");

        // Three call sites, one per way the effective value moves: a feature was
        // created (the plan is published), the tab changed the preset or turned
        // the slot off (the plan is cleared and no shader is reloaded), and the
        // upscaler was shut down with the platform.
        Assert.Equal(3, Count(upscale, "ShaderRegistry.ApplyOptimumLodBias();"));
        int created = upscale.IndexOf("if (!upscaler.EnsureFeature(plan)) return false;", StringComparison.Ordinal);
        Assert.True(created > 0);
        Assert.True(upscale.IndexOf("ShaderRegistry.ApplyOptimumLodBias();", created, StringComparison.Ordinal) > created);

        int settings = upscale.IndexOf("base.ApplyOptimumUpscalerSettings();", StringComparison.Ordinal);
        Assert.True(settings > 0);
        Assert.True(upscale.IndexOf("ShaderRegistry.ApplyOptimumLodBias();", settings, StringComparison.Ordinal) > settings);
    }

    [Fact]
    public void ThePollRegistersEveryAtlasThatIsSampledWithDerivatives()
    {
        string chunkRenderer = PatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");

        Assert.Contains("RegisterLodBiasedAtlases(CollectOptimumLodBiasedAtlases())", chunkRenderer);
        // Block atlases and entity atlases: both are world geometry sampled with
        // screen-space derivatives, so both alias the same way below native
        // resolution. The item atlas is left out on purpose - the GUI draws
        // inventory icons from it at display resolution.
        Assert.Contains("EntityTextureAtlasManager entityAtlases = game.EntityAtlasManager;", chunkRenderer);
        Assert.Contains("ids[next++] = entityTextures[j].TextureId;", chunkRenderer);
        Assert.DoesNotContain("ItemAtlasManager", chunkRenderer);
        // A new atlas texture carries the driver default, so both places that
        // produce one forget the applied value.
        Assert.Contains("OptimumConfig.InvalidateTerrainLodBias();", chunkRenderer);

        string clientMain = PatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");
        Assert.Contains("OptimumConfig.InvalidateTerrainLodBias();", clientMain);
    }

    [Fact]
    public void CecilPatcherShipsEveryLodBiasMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("\"ApplyOptimumLodBias\"", patcher);
        Assert.Contains("\"ApplyOptimumTerrainSamplerLodBias\"", patcher);
        Assert.Contains("\"ApplyOptimumSamplerLodBias\"", patcher);
        Assert.Contains("\"ApplyOptimumTextureLodBias\"", patcher);
        Assert.Contains("\"CollectOptimumLodBiasedAtlases\"", patcher);
        Assert.DoesNotContain("\"SetOptimumTextureLodBias\"", patcher);
        Assert.DoesNotContain("\"optimumTextureLodBias\"", patcher);
        // The bodies the calls live in are transplanted too.
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkRenderer\", \"OnBeforeRenderOpaque\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ChunkRenderer\", \"RuntimeAddBlockTextureAtlas\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ShaderRegistry\", \"loadRegisteredShaderPrograms\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"ReloadTextures\", 0", patcher);
    }

    private static int Count(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    private static string PatchedOrSource(string patchPath, string sourcePath)
    {
        try
        {
            return Read(patchPath);
        }
        catch (FileNotFoundException)
        {
            return Read(sourcePath);
        }
    }
}
