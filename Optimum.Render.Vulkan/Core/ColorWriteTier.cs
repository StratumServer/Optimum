using System;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// How a draw's effective colour write mask reaches the GPU (Phase 2, contract C4).
///
/// The effective mask of attachment i is <c>drawBufferEnabled(i) ? colorMask : 0</c>,
/// then masked by the outputs the program writes. glDrawBuffers and the TAA motion
/// windows change it between draws of one pass; every tier expresses that without
/// restarting the rendering scope. Ordered from most to least capable.
/// </summary>
internal enum ColorWriteTier
{
    /// <summary>
    /// The write-mask set is interned into the pipeline key: a mask change selects
    /// another pipeline. Always available; bounded by programs used inside a window x 2.
    /// </summary>
    PipelineKey = 0,

    /// <summary>
    /// VK_EXT_extended_dynamic_state3 colorWriteMask: the full per-attachment mask is
    /// dynamic, so neither glColorMask nor glDrawBuffers is in the key. With
    /// colorBlendEnable + colorBlendEquation also present the blend set leaves the
    /// key as well.
    /// </summary>
    DynamicMask = 1,

    /// <summary>
    /// VK_EXT_color_write_enable: glDrawBuffers is exactly a per-attachment enable,
    /// zero extra pipelines; glColorMask stays in the key as before.
    /// </summary>
    DynamicEnable = 2,
}

/// <summary>The optional-feature tier table: selection with an env override that forces a fallback.</summary>
internal static class DeviceCaps
{
    /// <summary>enable | mask | pipeline. A forced tier the device lacks degrades to the next one below.</summary>
    public const string ColorWriteTierVariable = "OPTIMUM_VULKAN_COLOR_WRITE_TIER";

    /// <summary>Parses an override; null for empty or unknown values.</summary>
    public static ColorWriteTier? ParseColorWriteTier(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            "enable" or "dynamic-enable" => ColorWriteTier.DynamicEnable,
            "mask" or "dynamic-mask" => ColorWriteTier.DynamicMask,
            "pipeline" or "pipeline-key" or "key" => ColorWriteTier.PipelineKey,
            _ => null,
        };

    /// <summary>
    /// The best tier the device supports, or the forced one when it is supported;
    /// a forced tier the device lacks falls to the best supported tier below it.
    /// </summary>
    public static ColorWriteTier SelectColorWriteTier(bool colorWriteEnable, bool colorWriteMask, ColorWriteTier? forced)
    {
        ColorWriteTier ceiling = forced ?? ColorWriteTier.DynamicEnable;
        if (ceiling == ColorWriteTier.DynamicEnable && colorWriteEnable) return ColorWriteTier.DynamicEnable;
        if (ceiling >= ColorWriteTier.DynamicMask && colorWriteMask) return ColorWriteTier.DynamicMask;
        return ColorWriteTier.PipelineKey;
    }

    /// <summary>The token written to the device log line and the stats.</summary>
    public static string Token(ColorWriteTier tier) => tier switch
    {
        ColorWriteTier.DynamicEnable => "enable",
        ColorWriteTier.DynamicMask => "mask",
        _ => "pipeline",
    };

    public static ColorWriteTier? FromEnvironment() =>
        ParseColorWriteTier(Environment.GetEnvironmentVariable(ColorWriteTierVariable));
}
