using System;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// PR #3 review: "a published plan" is <c>UpscalerRenderScale</c>, never
/// <c>UpscalerLodBias</c>.
///
/// <c>EffectiveTerrainLodBias</c> accumulates the render scale's own term and
/// TAA's mip bias, and an active upscaler is supposed to replace both with its
/// own - "with an upscaler active, its bias is the bias". The test for "active"
/// was a non-zero bias, but zero is a legitimate published bias: DLAA renders at
/// the display size, so log2 of the ratio is 0, and the offset slider makes 0
/// reachable at any preset. At bias 0 the replacement was skipped and the
/// accumulated terms stood - a -1 texture LOD bias on a frame that is being
/// rendered at the display resolution, which is exactly the over-sharpening the
/// upscaler path exists to avoid.
///
/// These run against the real <c>OptimumConfig</c>, so each one restores what it
/// changed.
/// </summary>
[Collection("OptimumConfigState")]
public class UpscalerLodBiasCoverageTests : IDisposable
{
    private readonly string _upscaler = OptimumConfig.Upscaler;
    private readonly float _renderScale = OptimumConfig.RenderScale;
    private readonly bool _taa = OptimumConfig.Taa;
    private readonly float _taaMipBias = OptimumConfig.TaaMipBias;

    public void Dispose()
    {
        OptimumConfig.Upscaler = _upscaler;
        OptimumConfig.RenderScale = _renderScale;
        OptimumConfig.Taa = _taa;
        OptimumConfig.TaaMipBias = _taaMipBias;
        OptimumConfig.ClearUpscalerPlan();
    }

    /// <summary>
    /// The regression: DLAA publishes scale 1 and bias 0, the render scale setting
    /// is at 0.5, and the bias that reaches the atlas samplers must be the plan's 0,
    /// not the -1 the render-scale term would otherwise contribute.
    /// </summary>
    [Fact]
    public void ADlaaPlanPublishesZeroBiasAndStillOwnsTheTerm()
    {
        OptimumConfig.Upscaler = "dlss";
        OptimumConfig.RenderScale = 0.5f;
        OptimumConfig.Taa = true;
        OptimumConfig.TaaMipBias = -0.5f;

        // What the renderer publishes for DLAA: the ratio is 1, so the vendor's bias
        // is log2(1) - offset clamped at 0.
        OptimumConfig.SetUpscalerPlan(1.0f, 0f);

        Assert.Equal(1.0f, OptimumConfig.UpscalerRenderScale);
        Assert.Equal(0f, OptimumConfig.UpscalerLodBias);
        Assert.Equal(0f, OptimumConfig.EffectiveTerrainLodBias);
    }

    /// <summary>An upscaling plan's own bias still wins, which never regressed.</summary>
    [Fact]
    public void AnUpscalingPlanStillOwnsTheTerm()
    {
        OptimumConfig.Upscaler = "dlss";
        OptimumConfig.RenderScale = 0.5f;
        OptimumConfig.Taa = true;
        OptimumConfig.TaaMipBias = -0.5f;

        OptimumConfig.SetUpscalerPlan(0.667f, -1.58f);

        Assert.Equal(-1.58f, OptimumConfig.EffectiveTerrainLodBias, 3);
    }

    /// <summary>
    /// And with no plan published the accumulated terms are still the answer: the new
    /// test must not claim a plan where there is none, or every frame before the
    /// first feature would drop the render scale's bias.
    /// </summary>
    [Fact]
    public void WithNoPlanPublishedTheAccumulatedTermsStand()
    {
        OptimumConfig.Upscaler = "dlss";
        OptimumConfig.RenderScale = 0.5f;
        OptimumConfig.Taa = true;
        OptimumConfig.TaaMipBias = -0.5f;
        OptimumConfig.ClearUpscalerPlan();

        Assert.Equal(0f, OptimumConfig.UpscalerRenderScale);
        // log2(0.5), plus TAA's mip bias when our own resolve is really running
        // (another test in the run may have stood TAA down at runtime).
        float expected = -1.0f + (OptimumConfig.EffectiveTaa ? -0.5f : 0f);
        Assert.Equal(expected, OptimumConfig.EffectiveTerrainLodBias, 3);
    }
}
