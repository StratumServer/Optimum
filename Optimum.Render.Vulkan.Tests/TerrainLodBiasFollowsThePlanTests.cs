using System;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Platform;
using Silk.NET.Vulkan;
using Vintagestory.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// DLSS plan, Phase 6: the texture LOD bias follows the upscaler's plan.
///
/// The user's report on an RTX 4070 was "the lower the Quality, the more jitter
/// comes back" - DLAA clean, Quality nearly so, Performance and Ultra Performance
/// shimmering on foliage - and the bias is the input that scales with exactly that
/// ratio (DLSS Programming Guide section 3.5: log2(render / display) - 1, so -1.58
/// at Quality, -2.0 at Performance, -2.59 at Ultra Performance).
///
/// The bias reaches the GPU in two places and they must agree: the parameter on
/// each atlas texture, and the chunkopaque/chunktopsoil sampler objects, which
/// override that parameter on every unit they are bound to. So this asserts the
/// state the device would build its VkSampler from - <c>VulkanDevice.TextureLodBias</c>
/// and <c>SamplerLodBias</c>, the values the sampler cache keys on - and not a
/// config property, which would prove nothing about what the GPU samples with.
///
/// What fails without the fix: the plan is published when NGX creates the feature,
/// which is after this frame's terrain has been drawn and after the shader load
/// that made the sampler objects, and a preset change deliberately reloads no
/// shaders. Nothing re-applied the value at publication - the only driver was
/// ChunkRenderer's per-frame poll, so the bias in effect was one frame stale at
/// every preset change and depended on a chunk pass having run at all.
///
/// Skips, never fails, without the shim, the driver library or the NGX feature
/// libraries.
/// </summary>
[Collection(NgxCollection.Name)]
public class TerrainLodBiasFollowsThePlanTests
{
    private readonly ITestOutputHelper _output;
    private readonly NgxRuntime _ngx;

    public TerrainLodBiasFollowsThePlanTests(ITestOutputHelper output, NgxRuntime ngx)
    {
        _output = output;
        _ngx = ngx;
    }

    private const int DisplayWidth = 2560;
    private const int DisplayHeight = 1490;

    /// <summary>
    /// Quality, Performance and Ultra Performance in turn, driven the way the
    /// client drives them: plan through the vendor's optimal-settings query,
    /// create the feature (which publishes the plan), then the client's own
    /// applier. After each one the atlas textures and both terrain sampler
    /// objects must carry <c>OptimumConfig.EffectiveTerrainLodBias</c>, and the
    /// measured value must be the guide's log2(render / display) - 1.
    ///
    /// The preset changes run without a shader reload, which is the case the
    /// regression lived in.
    /// </summary>
    [SkippableFact]
    public void TheBiasInEffectFollowsEveryPresetTheUpscalerPlans()
    {
        _ngx.Require();
        int mark = _ngx.MessageMark();
        VulkanDevice seam = _ngx.Device;

        string upscaler = OptimumConfig.Upscaler;
        string preset = OptimumConfig.UpscalerQuality;
        // The baseline the "0 means do not touch the parameter" rule is defined
        // against: no in-house TAA term and no render-scale term, so every value
        // measured below is the upscaler's own.
        bool taa = OptimumConfig.Taa;
        float renderScale = OptimumConfig.RenderScale;
        OptimumConfig.Taa = false;
        OptimumConfig.RenderScale = 1.0f;
        var host = new DlssUpscaler(Log);
        using var terrain = new TerrainSamplers(seam);
        try
        {
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.Upscaler = "dlss";
            Assert.True(host.AdoptSession(_ngx.Session, seam, _ngx.VkDevice));
            Assert.True(host.Active);

            // Before anything is planned nothing has been written: the
            // upscaler-off path makes no parameter call at all.
            Assert.Equal(0f, terrain.AtlasBias());
            Assert.Equal(0f, terrain.SamplerBias());
            Assert.True(float.IsNaN(OptimumConfig.AppliedTerrainLodBias));

            foreach (string quality in new[] { "quality", "performance", "ultraperformance" })
            {
                OptimumConfig.UpscalerQuality = quality;

                // The preset change as the tab drives it: the live feature is
                // retired (which clears the published plan) and no shader is
                // reloaded.
                host.RetireFeature();
                ShaderRegistry.ApplyOptimumLodBias();
                Assert.Equal(0f, terrain.AtlasBias());
                Assert.Equal(0f, terrain.SamplerBias());

                Assert.True(host.TryPlan(DisplayWidth, DisplayHeight, quality, out UpscalePlan plan));
                seam.BeginFrame();
                Assert.True(host.EnsureFeature(plan));
                // The state the old code left: the plan is published, the shader
                // load is long past and no chunk pass has run, so both call sites
                // still carry the previous preset's value. This is the "before"
                // number in the report, and it is what the fix has to move.
                float staleAtlas = terrain.AtlasBias();
                float staleSampler = terrain.SamplerBias();

                // What the renderer does the moment the plan is published.
                ShaderRegistry.ApplyOptimumLodBias();
                seam.Present();

                float expected = OptimumConfig.RecommendedUpscalerLodBias(plan.RenderWidth, DisplayWidth);
                float effective = OptimumConfig.EffectiveTerrainLodBias;
                Log(quality + ": " + plan.RenderWidth + "x" + plan.RenderHeight + " -> " +
                    DisplayWidth + "x" + DisplayHeight +
                    ", guide bias " + expected.ToString("0.###") +
                    ", EffectiveTerrainLodBias " + effective.ToString("0.###") +
                    ", atlas textures " + staleAtlas.ToString("0.###") + " -> " +
                    terrain.AtlasBias().ToString("0.###") +
                    ", chunk samplers " + staleSampler.ToString("0.###") + " -> " +
                    terrain.SamplerBias().ToString("0.###"));

                // Without the re-apply the published plan reaches neither place.
                Assert.NotEqual(effective, staleAtlas, 3);
                Assert.NotEqual(effective, staleSampler, 3);

                Assert.Equal(expected, effective, 3);
                // The assertion that matters: what the GPU would sample with.
                Assert.Equal(effective, terrain.AtlasBias(), 3);
                Assert.Equal(effective, terrain.SamplerBias(), 3);
            }

            // Standing the upscaler down takes the parameter back off both places,
            // which is what makes the upscaler-off path byte-identical again.
            host.RetireFeature();
            OptimumConfig.Upscaler = "off";
            ShaderRegistry.ApplyOptimumLodBias();
            Assert.Equal(0f, OptimumConfig.EffectiveTerrainLodBias);
            Assert.Equal(0f, terrain.AtlasBias());
            Assert.Equal(0f, terrain.SamplerBias());
            Assert.True(float.IsNaN(OptimumConfig.AppliedTerrainLodBias));
        }
        finally
        {
            host.Shutdown();
            OptimumConfig.Upscaler = upscaler;
            OptimumConfig.UpscalerQuality = preset;
            OptimumConfig.Taa = taa;
            OptimumConfig.RenderScale = renderScale;
            OptimumConfig.ResetUpscalerRuntimeDisabledForTests();
            OptimumConfig.ClearUpscalerPlan();
            OptimumConfig.RegisterLodBiasedAtlases(Array.Empty<int>());
            OptimumConfig.InvalidateTerrainLodBias();
        }

        GpuTest.AssertCleanSince(seam, mark);
    }

    /// <summary>
    /// The atlas textures and the two terrain programs' sampler objects, on the
    /// fixture's device, reachable through the client's own statics: a platform
    /// bound to that device as <c>ScreenManager.Platform</c>, and
    /// <c>ShaderPrograms.Chunkopaque</c> / <c>Chunktopsoil</c> carrying real
    /// sampler objects created through <c>GenSampler</c>, exactly as the shader
    /// load creates them.
    /// </summary>
    private sealed class TerrainSamplers : IDisposable
    {
        private readonly VulkanDevice _seam;
        private readonly ClientPlatformAbstract _previousPlatform;
        private readonly ShaderProgramChunkopaque _previousOpaque;
        private readonly ShaderProgramChunktopsoil _previousTopsoil;
        private readonly int[] _atlases;

        public TerrainSamplers(VulkanDevice seam)
        {
            _seam = seam;
            _previousPlatform = ScreenManager.Platform;
            _previousOpaque = ShaderPrograms.Chunkopaque;
            _previousTopsoil = ShaderPrograms.Chunktopsoil;

            // ShaderRegistry's static initialiser creates every ShaderPrograms
            // object, so it has to run before the programs below are prepared -
            // otherwise the first call into the registry replaces them and the
            // sampler objects prepared here disappear.
            ShaderRegistry.ApplyOptimumLodBias();

            var platform = new VulkanClientPlatform(null!);
            platform.AdoptDeviceForTests(seam);
            ScreenManager.Platform = platform;

            if (ShaderPrograms.Chunkopaque == null) ShaderPrograms.Chunkopaque = new ShaderProgramChunkopaque();
            if (ShaderPrograms.Chunktopsoil == null) ShaderPrograms.Chunktopsoil = new ShaderProgramChunktopsoil();
            ShaderPrograms.Chunkopaque.SetCustomSampler("terrainTex", isLinear: false);
            ShaderPrograms.Chunkopaque.SetCustomSampler("terrainTexLinear", isLinear: true);
            ShaderPrograms.Chunktopsoil.SetCustomSampler("terrainTex", isLinear: false);
            ShaderPrograms.Chunktopsoil.SetCustomSampler("terrainTexLinear", isLinear: true);

            // Two mipmapped atlas-shaped textures standing in for the block and
            // entity atlases the client registers.
            _atlases = new[] { CreateAtlas(seam), CreateAtlas(seam) };
            OptimumConfig.RegisterLodBiasedAtlases(_atlases);
            OptimumConfig.InvalidateTerrainLodBias();
        }

        private static int CreateAtlas(VulkanDevice seam) =>
            seam.CreateUpscaleTexture(64, 64, Format.R8G8B8A8Unorm, storage: false);

        /// <summary>The bias every registered atlas carries; fails the test if they disagree.</summary>
        public float AtlasBias()
        {
            float first = _seam.TextureLodBias(_atlases[0]);
            for (int i = 1; i < _atlases.Length; i++)
            {
                Assert.Equal(first, _seam.TextureLodBias(_atlases[i]), 5);
            }
            return first;
        }

        /// <summary>The bias every terrain sampler object carries; fails if they disagree.</summary>
        public float SamplerBias()
        {
            float first = float.NaN;
            foreach (ShaderProgramBase program in new ShaderProgramBase[]
                { ShaderPrograms.Chunkopaque, ShaderPrograms.Chunktopsoil })
            {
                foreach (string name in new[] { "terrainTex", "terrainTexLinear" })
                {
                    Assert.True(program.customSamplers.ContainsKey(name),
                        "program " + program.GetType().Name + " has samplers [" +
                        string.Join(",", program.customSamplers.Keys) + "]");
                    float bias = _seam.SamplerLodBias(program.customSamplers[name]);
                    Assert.False(float.IsNaN(bias), "sampler " + name + " does not exist on the device");
                    if (float.IsNaN(first)) first = bias;
                    else Assert.Equal(first, bias, 5);
                }
            }
            return first;
        }

        public void Dispose()
        {
            ShaderPrograms.Chunkopaque = _previousOpaque;
            ShaderPrograms.Chunktopsoil = _previousTopsoil;
            ScreenManager.Platform = _previousPlatform;
        }
    }

    private void Log(string line) => _output.WriteLine(line);
}
