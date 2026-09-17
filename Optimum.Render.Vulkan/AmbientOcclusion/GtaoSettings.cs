using System;
using System.Globalization;

namespace Optimum.Render.Vulkan.AmbientOcclusion;

/// <summary>The per-slice integration (docs/research/ambient-occlusion.md C.3, C.13).</summary>
internal enum GtaoIntegration : uint
{
    /// <summary>32-sector bitmask with cosine-CDF sector boundaries (C.4); the default.</summary>
    BitmaskCosine = 0,
    /// <summary>32-sector bitmask with sectors uniform in angle (Therrien's original).</summary>
    BitmaskUniform = 1,
    /// <summary>XeGTAO's analytic horizon integral, unchanged.</summary>
    Horizon = 2,
}

/// <summary>The thickness model of the bitmask (C.5, C.13). WIDTH is not implemented.</summary>
internal enum GtaoThickness : uint
{
    Constant = 0,
    /// <summary>Grows linearly with view distance.</summary>
    Distance = 1,
    /// <summary>Distance-scaled and randomised per sample; the default.</summary>
    Random = 2,
}

/// <summary>The quality presets of C.2 / C.12: slices x steps per side.</summary>
internal enum GtaoPreset
{
    /// <summary>1x2 (4 fetches).</summary>
    Low,
    /// <summary>2x2 (8 fetches); handheld candidate A at render resolution.</summary>
    Medium,
    /// <summary>3x3 (18 fetches); the discrete preset.</summary>
    High,
    /// <summary>9x3 (54 fetches), two denoise passes; screenshots.</summary>
    Ultra,
}

/// <summary>The compose tone (C.11): the albedo hook for a later multi-bounce term.</summary>
internal enum GtaoTone
{
    Linear,
    /// <summary>GTAO 2016 eq. 10; refused unless an albedo texture is bound.</summary>
    MultiBounce,
}

/// <summary>Specialization constant ids; <c>sources/shaders-vk/gtao/common.glsl</c> is the source of truth.</summary>
internal static class GtaoSpecialization
{
    public const int Integration = 0;
    public const int SliceCount = 1;
    public const int StepsPerSlice = 2;
    public const int Thickness = 3;
    public const int ClassChannel = 4;
    public const int NoiseCycle = 5;
    public const int NormalEdges = 6;
    public const int FinalApply = 7;

    /// <summary>One more than the highest id: the length of a specialization array.</summary>
    public const int Count = 8;
}

/// <summary>
/// Everything the AO passes are parameterised by: the preset's counts, the C.13 variants
/// (specialization constants) and the uniforms (push constants). Pure, so the packing
/// and the environment overrides are testable without a device.
/// </summary>
internal sealed record GtaoSettings
{
    public const int PushConstantBytes = 80;

    public uint SliceCount { get; init; } = 2;
    public uint StepsPerSlice { get; init; } = 2;
    public uint DenoisePasses { get; init; } = 1;
    public GtaoIntegration Integration { get; init; } = GtaoIntegration.BitmaskCosine;
    public GtaoThickness Thickness { get; init; } = GtaoThickness.Random;
    public bool ClassChannel { get; init; } = true;
    /// <summary>64 (XeGTAO) or 61, coprime with the 8 and 32 jitter phases (C.7).</summary>
    public uint NoiseCycle { get; init; } = 64;
    public bool NormalEdges { get; init; }
    public GtaoTone Tone { get; init; } = GtaoTone.Linear;

    /// <summary>Effect radius in blocks (C.2: start 0.75, tuned in D).</summary>
    public float EffectRadius { get; init; } = 0.75f;
    public float RadiusMultiplier { get; init; } = 1.457f;
    public float FalloffRange { get; init; } = 0.615f;
    /// <summary>Measurement only (C.11): 1.0 is the physically meaningful value.</summary>
    public float FinalValuePower { get; init; } = 1.0f;
    public float SampleDistributionPower { get; init; } = 2.0f;
    public float DepthMipSamplingOffset { get; init; } = 3.30f;
    /// <summary>Solid surfaces, blocks (C.5: a fence post is 0.125-0.25, a block 1).</summary>
    public float ThicknessSolid { get; init; } = 0.5f;
    /// <summary>The thin class, blocks (C.5).</summary>
    public float ThicknessThin { get; init; } = 0.05f;
    /// <summary>Thickness growth per block of view distance; a starting value for D.</summary>
    public float ThicknessDistanceScale { get; init; } = 1f / 64f;
    /// <summary>Vanilla ssao.fsh's fade: clamp(1.2 - z / 250, 0, 1).</summary>
    public float FarFadeBias { get; init; } = 1.2f;
    public float FarFadeDistance { get; init; } = 250f;
    public float DenoiseBlurBeta { get; init; } = 1.2f;

    /// <summary>
    /// The preset's counts. With a temporal accumulator one denoise pass (XeGTAO v1.21,
    /// Bevy); without it two (C.7/C.8); Ultra always two (the screenshot row of C.12).
    /// </summary>
    public static GtaoSettings ForPreset(GtaoPreset preset, bool temporal)
    {
        (uint slices, uint steps) = preset switch
        {
            GtaoPreset.Low => (1u, 2u),
            GtaoPreset.Medium => (2u, 2u),
            GtaoPreset.High => (3u, 3u),
            _ => (9u, 3u),
        };
        uint passes = preset == GtaoPreset.Ultra || !temporal ? 2u : 1u;
        return new GtaoSettings { SliceCount = slices, StepsPerSlice = steps, DenoisePasses = passes };
    }

    /// <summary>The persisted preset name ("low", "medium", "high", "ultra"); anything else is Medium.</summary>
    public static GtaoPreset ParsePreset(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "low" => GtaoPreset.Low,
        "high" => GtaoPreset.High,
        "ultra" => GtaoPreset.Ultra,
        _ => GtaoPreset.Medium,
    };

    /// <summary>
    /// The C.13 measurement variants from the environment (never persisted):
    /// <c>OPTIMUM_AO_INTEGRATION</c> (bitmask-cos | bitmask-uniform | horizon),
    /// <c>OPTIMUM_AO_THICKNESS</c> (const | dist | random), <c>OPTIMUM_AO_CLASS_CHANNEL</c> (0 | 1),
    /// <c>OPTIMUM_AO_NOISE_CYCLE</c> (64 | 61), <c>OPTIMUM_AO_DENOISE_PASSES</c> (1 | 2 | 3),
    /// <c>OPTIMUM_AO_NORMAL_EDGES</c> (0 | 1), <c>OPTIMUM_AO_TONE</c> (linear | multibounce),
    /// <c>OPTIMUM_AO_FINAL_POWER</c> and <c>OPTIMUM_AO_RADIUS</c>. Unrecognised values keep the setting.
    /// </summary>
    public GtaoSettings WithEnvironment(Func<string, string?> variable)
    {
        GtaoSettings result = this;
        switch (variable("OPTIMUM_AO_INTEGRATION")?.Trim().ToLowerInvariant())
        {
            case "bitmask-cos": result = result with { Integration = GtaoIntegration.BitmaskCosine }; break;
            case "bitmask-uniform": result = result with { Integration = GtaoIntegration.BitmaskUniform }; break;
            case "horizon": result = result with { Integration = GtaoIntegration.Horizon }; break;
        }
        switch (variable("OPTIMUM_AO_THICKNESS")?.Trim().ToLowerInvariant())
        {
            case "const": result = result with { Thickness = GtaoThickness.Constant }; break;
            case "dist": result = result with { Thickness = GtaoThickness.Distance }; break;
            case "random": result = result with { Thickness = GtaoThickness.Random }; break;
        }
        switch (variable("OPTIMUM_AO_CLASS_CHANNEL")?.Trim())
        {
            case "0": result = result with { ClassChannel = false }; break;
            case "1": result = result with { ClassChannel = true }; break;
        }
        switch (variable("OPTIMUM_AO_NOISE_CYCLE")?.Trim())
        {
            case "64": result = result with { NoiseCycle = 64 }; break;
            case "61": result = result with { NoiseCycle = 61 }; break;
        }
        switch (variable("OPTIMUM_AO_DENOISE_PASSES")?.Trim())
        {
            case "1": result = result with { DenoisePasses = 1 }; break;
            case "2": result = result with { DenoisePasses = 2 }; break;
            case "3": result = result with { DenoisePasses = 3 }; break;
        }
        switch (variable("OPTIMUM_AO_NORMAL_EDGES")?.Trim())
        {
            case "0": result = result with { NormalEdges = false }; break;
            case "1": result = result with { NormalEdges = true }; break;
        }
        switch (variable("OPTIMUM_AO_TONE")?.Trim().ToLowerInvariant())
        {
            case "linear": result = result with { Tone = GtaoTone.Linear }; break;
            case "multibounce": result = result with { Tone = GtaoTone.MultiBounce }; break;
        }
        if (TryParsePositive(variable("OPTIMUM_AO_FINAL_POWER"), out float power)) result = result with { FinalValuePower = power };
        if (TryParsePositive(variable("OPTIMUM_AO_RADIUS"), out float radius)) result = result with { EffectRadius = radius };
        return result;
    }

    private static bool TryParsePositive(string? text, out float value) =>
        float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && value > 0f && float.IsFinite(value);

    /// <summary>
    /// The tone the compose pass may use: <see cref="GtaoTone.MultiBounce" /> needs a real
    /// albedo (the scene colour here is lit LDR radiance, C.11), so without an albedo
    /// texture it is refused and <paramref name="refusal" /> says why.
    /// </summary>
    public GtaoTone EffectiveTone(int albedoTexture, out string? refusal)
    {
        refusal = null;
        if (Tone != GtaoTone.MultiBounce || albedoTexture != 0) return Tone;
        refusal = "multi-bounce AO needs an albedo texture; the scene colour is lit radiance, so the linear tone is used";
        return GtaoTone.Linear;
    }

    /// <summary>The main pass's specialization values, indexed by <see cref="GtaoSpecialization" /> id.</summary>
    public uint[] MainSpecialization()
    {
        var values = new uint[GtaoSpecialization.Count];
        values[GtaoSpecialization.Integration] = (uint)Integration;
        values[GtaoSpecialization.SliceCount] = Math.Max(1, SliceCount);
        values[GtaoSpecialization.StepsPerSlice] = Math.Max(1, StepsPerSlice);
        values[GtaoSpecialization.Thickness] = (uint)Thickness;
        values[GtaoSpecialization.ClassChannel] = ClassChannel ? 1u : 0u;
        values[GtaoSpecialization.NoiseCycle] = Math.Max(1, NoiseCycle);
        values[GtaoSpecialization.NormalEdges] = NormalEdges ? 1u : 0u;
        values[GtaoSpecialization.FinalApply] = 1u;
        return values;
    }

    /// <summary>A denoise pass's specialization values: only FINAL_APPLY varies.</summary>
    public static uint[] DenoiseSpecialization(bool finalApply)
    {
        var values = new uint[GtaoSpecialization.Count];
        values[GtaoSpecialization.FinalApply] = finalApply ? 1u : 0u;
        return values;
    }

    /// <summary>The 80-byte push block of <c>common.glsl</c>, in declaration order.</summary>
    public byte[] PushConstants(GtaoProjection projection, uint noiseIndex)
    {
        var bytes = new byte[PushConstantBytes];
        int offset = 0;
        void Float(float value)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), value);
            offset += 4;
        }
        void UInt(uint value)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(offset, 4), value);
            offset += 4;
        }

        Float(projection.DepthUnpackMul);
        Float(projection.DepthUnpackAdd);
        Float(projection.NdcToViewMulX);
        Float(projection.NdcToViewMulY);
        Float(projection.NdcToViewAddX);
        Float(projection.NdcToViewAddY);
        Float(EffectRadius);
        Float(FalloffRange);
        Float(RadiusMultiplier);
        Float(FinalValuePower);
        Float(SampleDistributionPower);
        Float(DepthMipSamplingOffset);
        Float(ThicknessSolid);
        Float(ThicknessThin);
        Float(ThicknessDistanceScale);
        Float(FarFadeBias);
        Float(1f / FarFadeDistance);
        UInt(noiseIndex);
        Float(DenoiseBlurBeta);
        UInt(0);
        return bytes;
    }
}
