using System;
using Optimum.Render.Vulkan.AmbientOcclusion;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Optimum AO (docs/research/ambient-occlusion.md section C): the GTAO visibility-bitmask pass
// on the device. The base's RenderPostprocessingEffects asks for it where vanilla SSAO would run
// and composes the returned texture through ApplyOptimumSceneSsao before the TAA resolve.
public partial class VulkanClientPlatform
{
    private GtaoRenderer? ambientOcclusion;

    /// <summary>This frame's output, or 0 when GTAO did not run (the debug outputs and the compose pass's reads key off it).</summary>
    private int ambientOcclusionOutput;

    /// <summary>The output texture whose sampler state was last set up.</summary>
    private int ambientOcclusionSampledTexture;

    /// <summary>Set when the passes cannot run on this device; vanilla SSAO then runs for the session.</summary>
    private string? ambientOcclusionFailure;

    private bool ambientOcclusionToneRefusalLogged;

    private (string Preset, bool Temporal, GtaoSettings Settings)? ambientOcclusionSettingsCache;

    /// <summary>
    /// Runs GTAO when the live shaders were built for it (OPTIMUMAO, stamped from
    /// <see cref="OptimumConfig.EffectiveGtao" /> and the SSAO G-buffer's condition) and returns
    /// the denoised visibility; 0 hands the frame to vanilla SSAO.
    /// </summary>
    public override int RenderOptimumAmbientOcclusion(float[] projectMatrix)
    {
        ambientOcclusionOutput = 0;
        if (device == null || projectMatrix == null || ambientOcclusionFailure != null) return 0;
        if (!OptimumConfig.AmbientOcclusionShadersUseGtao) return 0;
        FrameBufferRef? primary = FrameBuffers is { Count: > 0 } buffers ? buffers[0] : null;
        if (primary?.ColorTextureIds == null || primary.ColorTextureIds.Length < 4 || primary.DepthTextureId == 0) return 0;

        // The noise advances with the temporal clock only while TAA accumulates (C.7).
        bool temporal = OptimumConfig.EffectiveTaa && TaaTargetsReady;
        GtaoSettings settings = AmbientOcclusionSettings(temporal);
        if (!ambientOcclusionToneRefusalLogged && settings.EffectiveTone(0, out string? refusal) != settings.Tone)
        {
            ambientOcclusionToneRefusalLogged = true;
            Logger.Warning("[Optimum] AO: " + refusal);
        }
        uint noiseIndex = temporal ? (uint)(OptimumTemporal.Frame.FrameIndex & 0xFFFFFFFFL) : 0u;

        ambientOcclusion ??= new GtaoRenderer(device);
        int output = ambientOcclusion.Render(primary.DepthTextureId, primary.ColorTextureIds[2], projectMatrix, settings, noiseIndex);
        if (output == 0)
        {
            if (ambientOcclusion.ProgramsFailed)
            {
                ambientOcclusionFailure = ambientOcclusion.LastError ?? "unknown";
                Logger.Error("[Optimum] AO: GTAO is unavailable on this device, vanilla SSAO runs instead: " + ambientOcclusionFailure);
            }
            return 0;
        }
        if (output != ambientOcclusionSampledTexture)
        {
            // Composed with texelFetch at the same resolution; nearest and clamp keep any sampling exact.
            SetupOptimumTextureSampler(output, 9728, 33071);
            ambientOcclusionSampledTexture = output;
        }
        ambientOcclusionOutput = output;
        return output;
    }

    /// <summary>The debug outputs of this frame in the base's index order (working term, edges, depth level 0, output).</summary>
    public override int OptimumAmbientOcclusionDebugTexture(int index)
    {
        if (ambientOcclusionOutput == 0 || ambientOcclusion == null) return 0;
        return index switch
        {
            0 => ambientOcclusion.WorkingTermTexture,
            1 => ambientOcclusion.EdgesTexture,
            2 => ambientOcclusion.WorkingDepthTexture,
            3 => ambientOcclusion.OutputTexture,
            _ => 0,
        };
    }

    /// <summary>The preset with the measurement overrides from the environment, rebuilt only when the preset or TAA changes.</summary>
    private GtaoSettings AmbientOcclusionSettings(bool temporal)
    {
        string preset = OptimumConfig.AmbientOcclusionPreset ?? "";
        if (ambientOcclusionSettingsCache is { } cached && cached.Preset == preset && cached.Temporal == temporal)
        {
            return cached.Settings;
        }
        GtaoSettings settings = GtaoSettings.ForPreset(GtaoSettings.ParsePreset(preset), temporal)
            .WithEnvironment(Environment.GetEnvironmentVariable);
        ambientOcclusionSettingsCache = (preset, temporal, settings);
        return settings;
    }

    /// <summary>The size-dependent targets go with the framebuffers; the next frame recreates them.</summary>
    private void ReleaseAmbientOcclusionTargets()
    {
        ambientOcclusion?.ReleaseTargets();
        ambientOcclusionOutput = 0;
        ambientOcclusionSampledTexture = 0;
    }

    private void ReleaseAmbientOcclusion()
    {
        ambientOcclusion?.Dispose();
        ambientOcclusion = null;
        ambientOcclusionOutput = 0;
        ambientOcclusionSampledTexture = 0;
    }
}
