using System;
using Vintagestory.API.Config;

namespace Optimum.Tests;

/// <summary>
/// The whole of the upscaler state a test may move, captured and put back.
///
/// <c>OptimumConfig</c> is process-global static state and this assembly runs its
/// tests one at a time (<c>CollectionBehavior(DisableTestParallelization = true)</c>
/// in <c>AssemblyInfo.cs</c>), so a cleanup that <i>forces defaults</i> instead of
/// restoring what it found is not a cleanup: it hands the next test in the run a
/// different world than the one it would have had. Two of the LOD-bias tests used
/// to end with <c>Upscaler = "off"</c> and <c>ClearUpscalerPlan()</c> whatever the
/// caller's state had been (CodeRabbit, PR #3, second round).
///
/// Restoring is ordered: the plan first (publishing or clearing one invalidates the
/// applied marker), then the atlas registration, then the applied marker last, so
/// the "Optimum never touched the sampler parameter" state (NaN) is restored as
/// itself rather than as a concrete bias.
/// </summary>
internal readonly struct OptimumConfigSnapshot
{
    private readonly string _upscaler;
    private readonly string _quality;
    private readonly bool _taa;
    private readonly float _renderScale;
    private readonly float _lodBiasOffset;
    private readonly float _planRenderScale;
    private readonly float _planLodBias;
    private readonly int[] _atlases;
    private readonly float _appliedBias;

    private OptimumConfigSnapshot(
        string upscaler, string quality, bool taa, float renderScale, float lodBiasOffset,
        float planRenderScale, float planLodBias, int[] atlases, float appliedBias)
    {
        _upscaler = upscaler;
        _quality = quality;
        _taa = taa;
        _renderScale = renderScale;
        _lodBiasOffset = lodBiasOffset;
        _planRenderScale = planRenderScale;
        _planLodBias = planLodBias;
        _atlases = atlases;
        _appliedBias = appliedBias;
    }

    public static OptimumConfigSnapshot Capture() => new(
        OptimumConfig.Upscaler,
        OptimumConfig.UpscalerQuality,
        OptimumConfig.Taa,
        OptimumConfig.RenderScale,
        OptimumConfig.UpscalerLodBiasOffset,
        OptimumConfig.UpscalerRenderScale,
        OptimumConfig.UpscalerLodBias,
        OptimumConfig.LodBiasedAtlases,
        OptimumConfig.AppliedTerrainLodBias);

    public void Restore()
    {
        OptimumConfig.Upscaler = _upscaler;
        OptimumConfig.UpscalerQuality = _quality;
        OptimumConfig.Taa = _taa;
        OptimumConfig.RenderScale = _renderScale;
        OptimumConfig.UpscalerLodBiasOffset = _lodBiasOffset;

        // "A plan is published" is the render scale, never the bias: a DLAA plan
        // carries a bias of 0 and is still a plan.
        if (_planRenderScale > 0f) OptimumConfig.SetUpscalerPlan(_planRenderScale, _planLodBias);
        else OptimumConfig.ClearUpscalerPlan();

        OptimumConfig.RegisterLodBiasedAtlases(_atlases);

        OptimumConfig.InvalidateTerrainLodBias();
        if (!float.IsNaN(_appliedBias)) OptimumConfig.NoteTerrainLodBiasApplied(_appliedBias, reachedAtlases: true);
    }
}
