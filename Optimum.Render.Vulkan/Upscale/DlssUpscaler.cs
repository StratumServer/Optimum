using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Config;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// What an upscaler asks the engine to render at, answered by the vendor's own
/// query rather than by a hard-coded ratio per preset.
///
/// Everything downstream is derived here so no second place can invent its own
/// number: the jitter phase count (the temporal contract's
/// <c>max(1, ceil(8 * upscale^2))</c>, §2, with the upscale factor this plan
/// really uses), and the texture LOD bias every vendor SDK recommends
/// (<c>log2(render/display) - 1</c>).
/// </summary>
internal readonly record struct UpscalePlan(
    int RenderWidth, int RenderHeight, int DisplayWidth, int DisplayHeight, NgxPerfQuality Quality)
{
    /// <summary>Whether this plan describes a usable render target pair.</summary>
    public bool IsValid =>
        RenderWidth > 0 && RenderHeight > 0 && DisplayWidth > 0 && DisplayHeight > 0 &&
        RenderWidth <= DisplayWidth && RenderHeight <= DisplayHeight;

    /// <summary>Render pixels per display pixel on the X axis; 1 for DLAA.</summary>
    public float RenderScale => DisplayWidth <= 0 ? 1f : (float)RenderWidth / DisplayWidth;

    /// <summary>The bias the SDK recommends for this ratio; 0 when there is no upscale.</summary>
    public float LodBias => OptimumConfig.RecommendedUpscalerLodBias(RenderWidth, DisplayWidth);

    /// <summary>
    /// The contract's phase count for this plan's real upscale factor, so the
    /// sequence length follows the vendor's render size instead of the config's
    /// render scale (temporal contract §7.2 explicitly allows taking it from the
    /// SDK, and DLSS asks for 8 * upscale^2 like the others).
    /// </summary>
    public int JitterPhaseCount => Math.Max(1, (int)Math.Ceiling(
        8.0 * (RenderScale > 0f ? 1.0 / RenderScale : 1.0) * (RenderScale > 0f ? 1.0 / RenderScale : 1.0)));

    public NgxDlssSettings ToSettings() => new(
        (uint)RenderWidth, (uint)RenderHeight, (uint)DisplayWidth, (uint)DisplayHeight,
        Quality, NgxDlssSettings.ContractFlags);

    public override string ToString() =>
        RenderWidth + "x" + RenderHeight + " -> " + DisplayWidth + "x" + DisplayHeight +
        " " + Quality + " (scale " + RenderScale.ToString("0.###") +
        ", lod bias " + LodBias.ToString("0.##") + ", " + JitterPhaseCount + " jitter phases)";
}

/// <summary>
/// DLSS Super Resolution as the client's upscaler slot: the piece between the
/// setting (<c>OptimumConfig.Upscaler</c>) and the device seam
/// (<c>VulkanDevice.CreateDlssFeature</c> / <c>EvaluateDlss</c>).
///
/// It owns three things the frame must not have to think about:
/// <list type="number">
/// <item><b>Availability.</b> Every way DLSS can be absent - the setting is off,
/// the native shim is missing, the driver library is not installed, no NGX
/// feature libraries, a non-NVIDIA GPU, an NGX that refuses to initialise, a
/// feature that refuses to create - ends in exactly the same place:
/// <see cref="Active" /> false, one line logged, and
/// <c>OptimumConfig.DisableUpscalerAtRuntime</c> so the rest of the client runs
/// the pre-DLSS chain for the session. Nothing here throws and nothing aborts.</item>
/// <item><b>The plan.</b> The render size and the preset come from the SDK's own
/// optimal-settings query for the display size, never from a table here.</item>
/// <item><b>Lifetime.</b> One feature per (render size, display size, preset);
/// a change retires the old one onto the frame timeline and creates a new one,
/// so a resize or a preset change cannot leak features. NGX allows exactly one
/// lifetime per process: after <see cref="Shutdown" /> this host refuses to
/// bring NGX up again, for the life of the process.</item>
/// </list>
/// </summary>
internal sealed class DlssUpscaler : IDisposable
{
    /// <summary>
    /// NGX allows exactly one Init/Shutdown pair per process (the second
    /// <c>Shutdown1</c> segfaults inside the driver; measured 2026-09-12). Once a
    /// host in this process has shut NGX down, no host may bring it up again.
    /// </summary>
    private static bool _processLifetimeSpent;

    private readonly Action<string> _log;
    private NgxSession? _session;
    private bool _ownsSession;
    private VulkanDevice? _device;
    private IntPtr _vkDevice;
    private NgxDlssFeature? _feature;
    private bool _disposed;

    public DlssUpscaler(Action<string>? log = null)
    {
        _log = log ?? (_ => { });
    }

    /// <summary>Whether the config asks for DLSS at all, before anything is loaded.</summary>
    public static bool Requested => OptimumConfig.EffectiveUpscalerIsDlss;

    /// <summary>Why the upscaler is not running, or null while it is.</summary>
    public string? Unavailable { get; private set; } = "not brought up";

    /// <summary>Whether DLSS is up on a device and can be planned and evaluated.</summary>
    public bool Active => !_disposed && _session != null && _device != null && Unavailable == null;

    /// <summary>The plan the live feature serves, or default when there is none.</summary>
    public UpscalePlan Plan { get; private set; }

    /// <summary>The S1 requirement contributor, created with the session; null when there is none.</summary>
    public NgxDeviceRequirements? Requirements { get; private set; }

    /// <summary>How many features this host has created, and how many it has retired. Never leaks: the two agree once shut down.</summary>
    public int FeaturesCreated { get; private set; }

    public int FeaturesRetired { get; private set; }

    /// <summary>The live feature, for tests that want to see it change across a resize.</summary>
    internal NgxDlssFeature? Feature => _feature;

    // ------------------------------------------------------------ bring-up

    /// <summary>
    /// The half that has to happen <i>before</i> the Vulkan device exists: NGX's
    /// instance and device extensions are requested at device creation, so the
    /// session and its requirement contributor are built first and handed to the
    /// context options.
    ///
    /// Answers null when DLSS is not wanted or cannot possibly work here, having
    /// logged the one line and stood the setting down. A null here is not an
    /// error: the client carries on with the chain it has.
    /// </summary>
    public static DlssUpscaler? TryPrepare(string applicationDataPath, Action<string>? log = null)
    {
        var host = new DlssUpscaler(log);
        if (!Requested)
        {
            host.Unavailable = "the Upscaler setting is not \"dlss\"";
            return null;
        }
        if (_processLifetimeSpent)
        {
            host.Fail("NGX was already shut down in this process and allows exactly one lifetime");
            return null;
        }

        string? diagnosis = Diagnose();
        if (diagnosis != null)
        {
            host.Fail(diagnosis);
            return null;
        }

        IReadOnlyList<string> paths = NgxSession.FindFeaturePaths();
        string dataPath = applicationDataPath;
        try
        {
            if (!string.IsNullOrEmpty(dataPath)) Directory.CreateDirectory(dataPath);
        }
        catch (IOException)
        {
            // NGX only writes logs and caches there; an unwritable path is not
            // worth refusing the feature over.
        }
        catch (UnauthorizedAccessException)
        {
        }

        host._session = new NgxSession("1.0.0", dataPath, paths);
        host._ownsSession = true;
        host.Requirements = new NgxDeviceRequirements(
            host._session, new[] { NgxFeature.SuperSampling }, line => host._log("[Optimum] DLSS: " + line));
        host.Unavailable = "the device has not been brought up yet";
        return host;
    }

    /// <summary>
    /// The half that happens once the device exists: NGX is initialised on it.
    /// False means the upscaler is not available and the setting has been stood
    /// down; the caller carries on with the old chain.
    /// </summary>
    public bool BringUp(VulkanDevice device, IntPtr instance, IntPtr physicalDevice, IntPtr vkDevice)
    {
        if (_disposed) return Fail("the upscaler host was already shut down");
        if (_processLifetimeSpent)
        {
            return Fail("NGX was already shut down in this process and allows exactly one lifetime");
        }
        if (instance == IntPtr.Zero || physicalDevice == IntPtr.Zero || vkDevice == IntPtr.Zero)
        {
            return Fail("the Vulkan device did not publish the handles NGX needs");
        }
        if (_session == null || device == null)
        {
            return Fail("there is no NGX session to bring up on this device");
        }

        NgxResult initialized = _session.Initialize(instance, physicalDevice, vkDevice);
        if (initialized != NgxResult.Success)
        {
            return Fail("NVSDK_NGX_VULKAN_Init_ProjectID: " + NgxInterop.Describe(initialized));
        }

        _device = device;
        _vkDevice = vkDevice;
        Unavailable = null;
        _log("[Optimum] DLSS Super Resolution is available and will upscale the frame.");
        return true;
    }

    /// <summary>
    /// Adopts an NGX session somebody else brought up - the one-per-process test
    /// runtime - without taking over its lifetime. Init and Shutdown stay the
    /// owner's business; everything else (plans, features, evaluates) is this
    /// host's, exactly as in the client.
    /// </summary>
    internal bool AdoptSession(NgxSession session, VulkanDevice device, IntPtr vkDevice)
    {
        if (_disposed) return false;
        _session = session;
        _ownsSession = false;
        _device = device;
        _vkDevice = vkDevice;
        Unavailable = null;
        return true;
    }

    // ---------------------------------------------------------------- plan

    /// <summary>
    /// What to render at for this display size, from the SDK's own query. False
    /// leaves <paramref name="plan" /> at its default and, when the query itself
    /// failed, stands the upscaler down.
    /// </summary>
    public bool TryPlan(int displayWidth, int displayHeight, string preset, out UpscalePlan plan)
    {
        plan = default;
        if (!Active || displayWidth <= 0 || displayHeight <= 0) return false;

        NgxPerfQuality quality = QualityOf(preset);
        NgxResult capabilities = NgxInterop.GetCapabilityParameters(out IntPtr handle);
        if (capabilities != NgxResult.Success || handle == IntPtr.Zero)
        {
            return Fail("NVSDK_NGX_VULKAN_GetCapabilityParameters: " + NgxInterop.Describe(capabilities));
        }

        try
        {
            NgxResult result = NgxSession.OptimalSettings(
                new NgxParameters(handle), (uint)displayWidth, (uint)displayHeight, quality,
                out NgxOptimalSettings settings);
            if (result != NgxResult.Success || settings.OptimalWidth == 0 || settings.OptimalHeight == 0)
            {
                return Fail("the optimal-settings query for " + displayWidth + "x" + displayHeight +
                    " " + quality + " answered " + NgxInterop.Describe(result));
            }

            plan = new UpscalePlan(
                (int)settings.OptimalWidth, (int)settings.OptimalHeight,
                displayWidth, displayHeight, quality);
            return plan.IsValid;
        }
        finally
        {
            NgxInterop.DestroyParameters(handle);
        }
    }

    /// <summary>
    /// The preset names the setting may hold, mapped onto NGX's own enum.
    /// Anything else is Quality, which is what the config parser already
    /// guarantees; this is the second line of defence, not the first.
    /// </summary>
    public static NgxPerfQuality QualityOf(string? preset) =>
        string.Equals(preset, "dlaa", StringComparison.OrdinalIgnoreCase) ? NgxPerfQuality.Dlaa :
        string.Equals(preset, "balanced", StringComparison.OrdinalIgnoreCase) ? NgxPerfQuality.Balanced :
        string.Equals(preset, "performance", StringComparison.OrdinalIgnoreCase) ? NgxPerfQuality.MaxPerf :
        string.Equals(preset, "ultraperformance", StringComparison.OrdinalIgnoreCase) ? NgxPerfQuality.UltraPerformance :
        NgxPerfQuality.MaxQuality;

    // ------------------------------------------------------------ lifetime

    /// <summary>
    /// Makes sure a feature exists for <paramref name="plan" />, creating one on
    /// this frame's command buffer and retiring any feature that was created for
    /// a different plan. Must be called inside an open frame: NGX records
    /// initialisation work into the command buffer it is given.
    ///
    /// False means no feature - the caller runs the frame without the upscale.
    /// A creation failure stands the upscaler down, because a driver that refuses
    /// to create the feature will refuse again next frame.
    /// </summary>
    public bool EnsureFeature(in UpscalePlan plan)
    {
        if (!Active || _device == null || !plan.IsValid) return false;

        NgxDlssSettings settings = plan.ToSettings();
        if (_feature != null && _feature.Matches(settings))
        {
            return true;
        }

        // A plan change is a new feature, never a reconfigured one: NGX sizes its
        // internal buffers at creation. The old one goes onto the frame timeline,
        // because an evaluate recorded this frame may still name it.
        RetireFeature();

        NgxResult created = _device.CreateDlssFeature(settings, out NgxDlssFeature? feature);
        if (created != NgxResult.Success || feature == null)
        {
            return Fail("NVSDK_NGX_VULKAN_CreateFeature1 " + settings + ": " + NgxInterop.Describe(created));
        }

        _feature = feature;
        FeaturesCreated++;
        Plan = plan;
        // The LOD bias belongs to the plan that was really created, not to the one
        // that was asked for, so the samplers follow a feature that fell back.
        OptimumConfig.SetUpscalerLodBias(plan.LodBias);
        _log("[Optimum] DLSS feature created: " + plan);
        return true;
    }

    /// <summary>
    /// Runs the upscale for this frame. The caller has already made sure the
    /// feature matches the plan (<see cref="EnsureFeature" />) and that the four
    /// textures are the render-resolution colour, depth and motion and the
    /// display-resolution output.
    /// </summary>
    public NgxResult Evaluate(
        int colorTexture, int depthTexture, int motionTexture, int outputTexture, in NgxDlssEvaluation frame)
    {
        if (!Active || _device == null || _feature == null) return NgxResult.FailFeatureNotFound;
        return _device.EvaluateDlss(_feature, colorTexture, depthTexture, motionTexture, outputTexture, frame);
    }

    /// <summary>
    /// Retires the live feature onto the frame timeline. Never destroys it
    /// inline: an evaluate recorded this frame is still in flight.
    /// </summary>
    public void RetireFeature()
    {
        if (_feature == null) return;
        NgxDlssFeature feature = _feature;
        _feature = null;
        Plan = default;
        FeaturesRetired++;
        OptimumConfig.SetUpscalerLodBias(0f);
        if (_device != null) _device.RetireDlssFeature(feature);
        else feature.Dispose();
    }

    /// <summary>
    /// Teardown, in the only order the driver survives: the feature is retired,
    /// the frame timeline is drained so nothing still names its handle, then NGX
    /// is shut down - and never brought up again in this process.
    ///
    /// A host that adopted somebody else's session shuts nothing down; the owner
    /// does that, in the same order.
    /// </summary>
    public void Shutdown()
    {
        if (_disposed) return;
        _disposed = true;

        RetireFeature();
        _device?.DrainDeferredDeletions();

        if (_ownsSession && _session != null && _vkDevice != IntPtr.Zero && Unavailable == null)
        {
            _processLifetimeSpent = true;
            NgxResult shutdown = NgxSession.Shutdown(_vkDevice);
            _log("[Optimum] DLSS: NVSDK_NGX_VULKAN_Shutdown1: " + NgxInterop.Describe(shutdown));
        }
        if (_ownsSession) _session?.Dispose();

        _session = null;
        _device = null;
        _vkDevice = IntPtr.Zero;
        Unavailable = "shut down";
        OptimumConfig.SetUpscalerLodBias(0f);
    }

    public void Dispose() => Shutdown();

    // --------------------------------------------------------------- detail

    /// <summary>
    /// Everything the environment has to provide before NGX can be brought up at
    /// all, each with the sentence a user can act on. Null means "nothing in the
    /// way".
    /// </summary>
    private static string? Diagnose()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows())
        {
            return "DLSS is reached through the NVIDIA driver library, which this platform does not have";
        }
        if (!NgxInterop.IsDriverLibraryPresent())
        {
            return "the NVIDIA driver library " + NgxInterop.LibraryName + " is not installed";
        }
        if (!NgxShim.IsAvailable)
        {
            return "the NGX shim is not loadable: " + NgxShim.Diagnosis;
        }
        NgxResult runtime = NgxShim.LoadRuntime();
        if (!NgxInterop.Succeeded(runtime))
        {
            return "the NGX runtime did not load: " + NgxInterop.Describe(runtime) + " " + NgxShim.LastLoadError;
        }
        if (NgxSession.FindFeaturePaths().Count == 0)
        {
            return "no NGX feature libraries were found; set " + NgxSession.FeaturePathVariable +
                " to the directory holding libnvidia-ngx-dlss.so.*";
        }
        return null;
    }

    /// <summary>
    /// The single failure path: one line in the log, the setting stood down for
    /// the session, and false to the caller. Always false, so a caller can
    /// <c>return Fail(...)</c>.
    /// </summary>
    private bool Fail(string reason)
    {
        Unavailable = reason;
        bool first = OptimumConfig.DisableUpscalerAtRuntime();
        if (first)
        {
            _log("[Optimum] DLSS is unavailable (" + reason + "); rendering without an upscaler.");
        }
        return false;
    }
}
