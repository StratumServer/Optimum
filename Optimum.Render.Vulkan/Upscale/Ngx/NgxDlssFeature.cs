using System;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// <c>NVSDK_NGX_DLSS_Feature_Flags</c> (<c>nvsdk_ngx_defs.h</c>).
/// </summary>
[Flags]
internal enum NgxDlssCreateFlags
{
    None = 0,
    /// <summary>The colour buffer is HDR. Optimum's colour is LDR RGBA8, so this stays off.</summary>
    IsHdr = 1 << 0,
    /// <summary>Motion vectors are at render resolution, not display resolution.</summary>
    MotionVectorsLowRes = 1 << 1,
    /// <summary>Motion vectors include the jitter. Ours exclude it (temporal contract §7.1).</summary>
    MotionVectorsJittered = 1 << 2,
    /// <summary>Depth is reversed (1 = near). Ours is not (temporal contract §7.3).</summary>
    DepthInverted = 1 << 3,
    /// <summary>Deprecated in SDK 310: sharpening is not supported any more.</summary>
    DoSharpening = 1 << 5,
    /// <summary>DLSS derives the exposure itself. Optimum has no exposure path (§7.5).</summary>
    AutoExposure = 1 << 6,
    AlphaUpscaling = 1 << 7,
}

/// <summary>
/// What a DLSS feature is created for. Changing any of it means a new feature:
/// NGX allocates its internal buffers at creation from these numbers, so they
/// cannot be varied per evaluate (see <see cref="NgxDlssFeature.Matches" />).
/// </summary>
internal readonly record struct NgxDlssSettings(
    uint RenderWidth,
    uint RenderHeight,
    uint DisplayWidth,
    uint DisplayHeight,
    NgxPerfQuality Quality,
    NgxDlssCreateFlags Flags)
{
    /// <summary>
    /// The flags Optimum's temporal contract implies, and the reasons are in the
    /// contract, not here: motion vectors are render-resolution
    /// (<see cref="NgxDlssCreateFlags.MotionVectorsLowRes" />) and exclude the
    /// jitter (no <see cref="NgxDlssCreateFlags.MotionVectorsJittered" />);
    /// depth is 0 = near (no <see cref="NgxDlssCreateFlags.DepthInverted" />);
    /// colour is LDR RGBA8 (no <see cref="NgxDlssCreateFlags.IsHdr" />) and there
    /// is no exposure path, so DLSS derives it
    /// (<see cref="NgxDlssCreateFlags.AutoExposure" />).
    /// </summary>
    public const NgxDlssCreateFlags ContractFlags =
        NgxDlssCreateFlags.MotionVectorsLowRes | NgxDlssCreateFlags.AutoExposure;

    public override string ToString() =>
        RenderWidth + "x" + RenderHeight + " -> " + DisplayWidth + "x" + DisplayHeight +
        " " + Quality + " [" + Flags + "]";
}

/// <summary>
/// One frame's evaluate inputs, in NGX's own terms. Every mapping from Optimum's
/// temporal contract (v1, <c>docs/temporal-frame-contract.md</c>) to these
/// members is done by the caller, and two of them differ from the contract's
/// "DLSS" column because that column is written for Streamline:
///
/// <list type="bullet">
/// <item><b>Motion-vector scale.</b> Raw NGX multiplies the sampled vector by
/// <c>MV.Scale.X/Y</c> to get <i>render pixels</i>. Our vectors are already
/// render pixels (§7.1), so the scale is (1, 1). Streamline's
/// <c>mvecScale = (1/renderWidth, 1/renderHeight)</c> is the NDC convention of
/// that layer, not of NGX.</item>
/// <item><b>Jitter sign.</b> NGX takes the raster displacement in the input
/// image's pixel coordinates, not the sign of a projection-matrix coefficient.
/// Our positive-height offscreen viewport maps <c>JitterPx</c> directly to that
/// displacement in both axes (§7.2); no negation or Y flip is needed.</item>
/// </list>
/// </summary>
internal readonly record struct NgxDlssEvaluation
{
    /// <summary>The game's adapter, also exercised by the multi-frame NGX readback test.</summary>
    internal static NgxDlssEvaluation FromTemporalContext(IOptimumTemporalContext frame) => new()
    {
        JitterOffsetX = frame.JitterPx.X,
        JitterOffsetY = frame.JitterPx.Y,
        MotionVectorScaleX = 1f,
        MotionVectorScaleY = 1f,
        Reset = frame.Reset,
    };

    public NgxDlssEvaluation()
    {
    }

    /// <summary>Raster displacement in input-image render pixels: <c>JitterPx</c>.</summary>
    public float JitterOffsetX { get; init; }
    public float JitterOffsetY { get; init; }

    /// <summary>Multiplier that turns a sampled vector into render pixels; (1, 1) for our vectors.</summary>
    public float MotionVectorScaleX { get; init; } = 1f;
    public float MotionVectorScaleY { get; init; } = 1f;

    /// <summary>
    /// 1 on any frame the temporal contract calls a reset (§5): the history is
    /// discarded rather than reprojected.
    /// </summary>
    public bool Reset { get; init; }

    /// <summary>
    /// The part of the colour/depth/motion images that actually holds this
    /// frame, when they are larger than the render resolution (dynamic
    /// resolution). 0 means "the whole image", which is what the feature was
    /// created for.
    /// </summary>
    public uint RenderSubrectWidth { get; init; }
    public uint RenderSubrectHeight { get; init; }

    /// <summary>
    /// Pre-exposure the colour was multiplied by. Optimum has no exposure path,
    /// so it is 1 (§7.5); 0 would be read as 1 by the SDK helper anyway.
    /// </summary>
    public float PreExposure { get; init; } = 1f;

    /// <summary>Exposure scale, 1 for the same reason.</summary>
    public float ExposureScale { get; init; } = 1f;

    /// <summary>
    /// Deprecated in SDK 310 (<see cref="NgxDlssCreateFlags.DoSharpening" />) and
    /// left at 0; DLSS sharpening is not supported any more.
    /// </summary>
    public float Sharpness { get; init; }
}

/// <summary>
/// A live DLSS Super Resolution feature: the NGX handle plus the parameter block
/// it was created with and evaluates through.
///
/// Lifecycle, in the order it has to happen:
/// <list type="number">
/// <item><see cref="Create" /> allocates a parameter block, fills in the
/// creation parameters the SDK's <c>NGX_VULKAN_CREATE_DLSS_EXT1</c> macro fills
/// in, and calls <c>CreateFeature1</c> on a command buffer <i>inside an open
/// frame</i>: NGX records initialisation work into it.</item>
/// <item><see cref="Evaluate" /> sets the per-frame parameters and calls
/// <c>EvaluateFeature</c> on the same frame's command buffer. The resources must
/// already be in the layouts NGX documents; this type places no barriers,
/// because NGX performs no synchronisation and neither does it.</item>
/// <item><see cref="Dispose" /> releases the feature and destroys the parameter
/// block. It must not run while an evaluate that named the handle is still in
/// flight, which is why it is an <see cref="IDisposable" />: the owner hands it
/// to the frame ring's retire queue rather than disposing it directly
/// (<c>VulkanDevice.RetireDlssFeature</c>).</item>
/// </list>
///
/// Nothing here throws. Every call answers an <see cref="NgxResult" />, so a
/// driver that refuses is a feature that stays null, not an exception on the
/// render thread.
/// </summary>
internal sealed unsafe class NgxDlssFeature : IDisposable
{
    private IntPtr _handle;
    private IntPtr _parameters;
    private bool _disposed;

    private NgxDlssFeature(IntPtr handle, IntPtr parameters, NgxDlssSettings settings)
    {
        _handle = handle;
        _parameters = parameters;
        Settings = settings;
    }

    /// <summary>What this feature was created for; see <see cref="Matches" />.</summary>
    public NgxDlssSettings Settings { get; }

    /// <summary>The NVSDK_NGX_Handle*, or zero once released.</summary>
    public IntPtr Handle => _handle;

    /// <summary>Whether the feature is still usable.</summary>
    public bool IsValid => !_disposed && _handle != IntPtr.Zero;

    /// <summary>
    /// Whether this feature can serve <paramref name="settings" />. Anything else
    /// - a resolution change, a different quality preset, a flag change - means
    /// retiring this one and creating another; NGX sizes its internal buffers at
    /// creation and the preset is fixed for the feature's lifetime.
    /// </summary>
    public bool Matches(NgxDlssSettings settings) => IsValid && Settings == settings;

    /// <summary>
    /// Creates the feature on <paramref name="commandBuffer" />, which must be
    /// recording inside an open frame.
    ///
    /// The parameter block comes from <c>AllocateParameters</c>, not
    /// <c>GetCapabilityParameters</c>: the capability block is the driver's own
    /// and is shared, while a feature needs one it owns for the whole of its
    /// life (the evaluate parameters are read at evaluate time, from this block).
    /// </summary>
    public static NgxResult Create(
        IntPtr device, CommandBuffer commandBuffer, NgxDlssSettings settings, out NgxDlssFeature? feature)
    {
        feature = null;
        if (!NgxInterop.ManagedCallSiteIsSupported) return NgxResult.FailShimMissing;
        if (settings.RenderWidth == 0 || settings.RenderHeight == 0 ||
            settings.DisplayWidth == 0 || settings.DisplayHeight == 0)
        {
            return NgxResult.FailInvalidParameter;
        }

        NgxResult allocated = NgxInterop.AllocateParameters(out IntPtr parameters);
        if (!NgxInterop.Succeeded(allocated)) return allocated;
        if (parameters == IntPtr.Zero) return NgxResult.FailInvalidParameter;

        var block = new NgxParameters(parameters);

        // Exactly what NGX_VULKAN_CREATE_DLSS_EXT1 sets, in its order.
        block.SetUInt(NgxParameterNames.CreationNodeMask, 1);
        block.SetUInt(NgxParameterNames.VisibilityNodeMask, 1);
        block.SetUInt(NgxParameterNames.Width, settings.RenderWidth);
        block.SetUInt(NgxParameterNames.Height, settings.RenderHeight);
        block.SetUInt(NgxParameterNames.OutWidth, settings.DisplayWidth);
        block.SetUInt(NgxParameterNames.OutHeight, settings.DisplayHeight);
        block.SetInt(NgxParameterNames.PerfQualityValue, (int)settings.Quality);
        block.SetInt(NgxParameterNames.DlssFeatureCreateFlags, (int)settings.Flags);
        block.SetInt(NgxParameterNames.DlssEnableOutputSubrects, 0);

        IntPtr handle = IntPtr.Zero;
        NgxResult created = NgxShim.IsAvailable
            ? NgxShim.CreateFeature1(device, (IntPtr)commandBuffer.Handle, NgxFeature.SuperSampling,
                parameters, out handle)
            : NgxResult.FailShimMissing;

        if (!NgxInterop.Succeeded(created) || handle == IntPtr.Zero)
        {
            NgxInterop.DestroyParameters(parameters);
            return NgxInterop.Succeeded(created) ? NgxResult.FailUnableToInitializeFeature : created;
        }

        feature = new NgxDlssFeature(handle, parameters, settings);
        return created;
    }

    /// <summary>
    /// Sets the per-frame parameters and calls <c>EvaluateFeature</c>.
    ///
    /// The four resource structs are taken <i>by value</i>, so their addresses
    /// are this frame's stack and stay valid for the whole call: NGX stores the
    /// pointers in the parameter block and dereferences them inside
    /// EvaluateFeature, so a struct that died between the Set and the evaluate
    /// would be read as garbage. Every optional input the SDK helper sets is set
    /// here too, to null or zero - the parameter block is reused frame after
    /// frame, and a pointer left in it from an earlier frame would be read as
    /// this frame's input.
    /// </summary>
    public NgxResult Evaluate(
        CommandBuffer commandBuffer,
        NgxResourceVk color, NgxResourceVk output,
        NgxResourceVk depth, NgxResourceVk motionVectors,
        in NgxDlssEvaluation frame)
    {
        if (!IsValid) return NgxResult.FailFeatureNotFound;
        if (!NgxShim.IsAvailable) return NgxResult.FailShimMissing;

        var block = new NgxParameters(_parameters);

        {
            NgxResourceVk* colorPtr = &color;
            NgxResourceVk* outputPtr = &output;
            NgxResourceVk* depthPtr = &depth;
            NgxResourceVk* motionPtr = &motionVectors;

            block.SetVoidPointer(NgxParameterNames.Color, (IntPtr)colorPtr);
            block.SetVoidPointer(NgxParameterNames.Output, (IntPtr)outputPtr);
            block.SetVoidPointer(NgxParameterNames.Depth, (IntPtr)depthPtr);
            block.SetVoidPointer(NgxParameterNames.MotionVectors, (IntPtr)motionPtr);

            block.SetFloat(NgxParameterNames.JitterOffsetX, frame.JitterOffsetX);
            block.SetFloat(NgxParameterNames.JitterOffsetY, frame.JitterOffsetY);
            block.SetFloat(NgxParameterNames.Sharpness, frame.Sharpness);
            block.SetInt(NgxParameterNames.Reset, frame.Reset ? 1 : 0);

            // The SDK helper substitutes 1 for a zero scale; do the same rather
            // than handing NGX a scale that annihilates every vector.
            block.SetFloat(NgxParameterNames.MvScaleX,
                frame.MotionVectorScaleX == 0f ? 1f : frame.MotionVectorScaleX);
            block.SetFloat(NgxParameterNames.MvScaleY,
                frame.MotionVectorScaleY == 0f ? 1f : frame.MotionVectorScaleY);

            // Optional inputs Optimum does not produce. Cleared every frame:
            // this block is long-lived and NGX reads whatever is in it.
            block.SetVoidPointer(NgxParameterNames.TransparencyMask, IntPtr.Zero);
            block.SetVoidPointer(NgxParameterNames.ExposureTexture, IntPtr.Zero);
            block.SetVoidPointer(NgxParameterNames.DlssInputBiasCurrentColorMask, IntPtr.Zero);
            block.SetUInt(NgxParameterNames.TonemapperType, 0);

            block.SetUInt(NgxParameterNames.DlssRenderSubrectDimensionsWidth,
                frame.RenderSubrectWidth == 0 ? Settings.RenderWidth : frame.RenderSubrectWidth);
            block.SetUInt(NgxParameterNames.DlssRenderSubrectDimensionsHeight,
                frame.RenderSubrectHeight == 0 ? Settings.RenderHeight : frame.RenderSubrectHeight);

            block.SetFloat(NgxParameterNames.DlssPreExposure,
                frame.PreExposure == 0f ? 1f : frame.PreExposure);
            block.SetFloat(NgxParameterNames.DlssExposureScale,
                frame.ExposureScale == 0f ? 1f : frame.ExposureScale);
            block.SetInt(NgxParameterNames.DlssIndicatorInvertXAxis, 0);
            block.SetInt(NgxParameterNames.DlssIndicatorInvertYAxis, 0);

            // Optimum hands over whole images, so every subrect starts at (0, 0).
            block.SetUInt(NgxParameterNames.DlssInputColorSubrectBaseX, 0);
            block.SetUInt(NgxParameterNames.DlssInputColorSubrectBaseY, 0);
            block.SetUInt(NgxParameterNames.DlssInputDepthSubrectBaseX, 0);
            block.SetUInt(NgxParameterNames.DlssInputDepthSubrectBaseY, 0);
            block.SetUInt(NgxParameterNames.DlssInputMvSubrectBaseX, 0);
            block.SetUInt(NgxParameterNames.DlssInputMvSubrectBaseY, 0);
            block.SetUInt(NgxParameterNames.DlssInputTranslucencySubrectBaseX, 0);
            block.SetUInt(NgxParameterNames.DlssInputTranslucencySubrectBaseY, 0);
            block.SetUInt(NgxParameterNames.DlssInputBiasCurrentColorSubrectBaseX, 0);
            block.SetUInt(NgxParameterNames.DlssInputBiasCurrentColorSubrectBaseY, 0);
            block.SetUInt(NgxParameterNames.DlssOutputSubrectBaseX, 0);
            block.SetUInt(NgxParameterNames.DlssOutputSubrectBaseY, 0);

            return NgxShim.EvaluateFeature(
                (IntPtr)commandBuffer.Handle, _handle, _parameters, IntPtr.Zero);
        }
    }

    /// <summary>
    /// Releases the feature and its parameter block. Only ever called from the
    /// frame ring's retire queue, once every frame that could name the handle has
    /// completed: NGX's own docs are explicit that releasing a feature whose
    /// evaluate is still executing is undefined.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        IntPtr handle = _handle;
        IntPtr parameters = _parameters;
        _handle = IntPtr.Zero;
        _parameters = IntPtr.Zero;

        if (handle != IntPtr.Zero && NgxShim.IsAvailable) LastReleaseResult = NgxShim.ReleaseFeature(handle);
        if (parameters != IntPtr.Zero) LastDestroyParametersResult = NgxInterop.DestroyParameters(parameters);
    }

    /// <summary>What <c>ReleaseFeature</c> answered, for the log and the tests.</summary>
    public NgxResult LastReleaseResult { get; private set; }

    /// <summary>What <c>DestroyParameters</c> answered.</summary>
    public NgxResult LastDestroyParametersResult { get; private set; }
}
