using System.Collections.Generic;

namespace Optimum.Patcher;

/// <summary>
/// Static fields the VintagestoryLib patch injects with a donor initializer that
/// <see cref="InjectedFieldInitializerVerifier"/> would otherwise reject: the initializer lives
/// in the donor static constructor, which nothing transplants into vanilla, so the field starts
/// at its CLR default (null, 0 or false) instead of the value written in the source.
///
/// Each entry was audited and is harmless for the reason given. A new static field with an
/// initializer must not be added here without the same audit: either assign it where it is read,
/// or show that its default and every reader that can run before the first render frame agree.
/// (Issue #589 was an injected static that nothing initialized and every reader dereferenced.)
/// </summary>
public static class LibStaticInitializerAllowList
{
    public static readonly IReadOnlySet<string> Names = new HashSet<string>
    {
        // Self-correcting cache: a default of 0 for the cached distance yields 0 squared, which is
        // what the property computes for a distance of 0, and any other distance recomputes it.
        "Vintagestory.Client.NoObf.SystemRenderEntities::_cachedShadowCullDistance",
        "Vintagestory.Client.NoObf.SystemRenderEntities::_cachedShadowCullDistanceSq",

        // Every Optimum setting below is overwritten from OptimumConfig in
        // ClientPlatformWindows.EnsureOptimumDefaults, the first statement of window_RenderFrame,
        // and the first client log shows that frame before any shader load. The initializers
        // (true, 80, 64, 1.0f, 0.2f) are therefore never observed. The only reader that could run
        // earlier is the FXAA define in ShaderRegistry, which does not.
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumEntityShadowCull>k__BackingField",
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumShadowCullDistance>k__BackingField",
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumDynamicLightScale>k__BackingField",
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumBackgroundFpsLimit>k__BackingField",
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumPreciseFramePacing>k__BackingField",
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumRepulsionGate>k__BackingField",
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumRepulsionDistance>k__BackingField",
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumAnimBlockLod>k__BackingField",
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumShadowFarVegetation>k__BackingField",
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumWeatherWindThrottle>k__BackingField",
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumParticleDistanceGate>k__BackingField",
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumChiselLod>k__BackingField",
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumChiselLodDistance>k__BackingField",
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumDynamicLightCache>k__BackingField",
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumRenderScale>k__BackingField",
        "Vintagestory.Client.NoObf.ClientSettings::<OptimumFsrSharpness>k__BackingField",

        // Null-checked before use (_optimumSharedIndirectCommands), or sentinels that only decide
        // when a uniform is uploaded again, so 0 instead of -1 re-uploads at most once or matches
        // the shader default (the _optimumLastFsr* fields).
        "Vintagestory.Client.NoObf.ClientPlatformWindows::_optimumSharedIndirectCommands",
        "Vintagestory.Client.NoObf.ClientPlatformWindows::_optimumLastFsrInputWidth",
        "Vintagestory.Client.NoObf.ClientPlatformWindows::_optimumLastFsrInputHeight",
        "Vintagestory.Client.NoObf.ClientPlatformWindows::_optimumLastFsrOutputWidth",
        "Vintagestory.Client.NoObf.ClientPlatformWindows::_optimumLastFsrOutputHeight",
        "Vintagestory.Client.NoObf.ClientPlatformWindows::_optimumLastFsrSharpness",
    };
}
