using System;
using System.IO;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Config;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>
/// DLSS plan, Phase 2 step 4: the upscaler's lifetime, tied to this platform's
/// graphics lifetime.
///
/// Bring-up is in two halves because NGX's device extensions have to be
/// requested <i>at device creation</i>: the session and its requirement
/// contributor are prepared before <c>device.Initialize</c> and reach the
/// context through <c>ConfigureContextOptions</c>, and NGX is initialised on the
/// device once it exists. Teardown is in the only order the driver survives -
/// the feature is retired, the frame timeline drained, NGX shut down, and only
/// then the device destroyed - and NGX is never brought up again in the process.
///
/// Every failure is the same failure: no upscaler, one line in the client log,
/// the setting stood down for the session, and a frame that runs exactly the
/// chain it ran before the upscaler was asked for.
/// </summary>
public partial class VulkanClientPlatform
{
    private DlssUpscaler? upscaler;

    /// <summary>The upscaler this platform brought up, or null when none is running.</summary>
    internal DlssUpscaler? Upscaler => upscaler;

    /// <summary>Whether an upscaler is up and can be planned and evaluated this frame.</summary>
    internal bool UpscalerActive => upscaler != null && upscaler.Active;

    /// <summary>
    /// The first half: prepared before the device is created, so NGX's instance
    /// and device extensions reach <c>VulkanContext</c> through the S1
    /// requirement seam. Does nothing at all when the setting is off.
    /// </summary>
    internal void PrepareUpscaler(VulkanDevice target)
    {
        if (upscaler != null) return;
        if (!DlssUpscaler.Requested) return;

        upscaler = DlssUpscaler.TryPrepare(UpscalerDataPath(), LogUpscaler);
        NgxDeviceRequirements? requirements = upscaler?.Requirements;
        if (requirements == null) return;

        // Chained rather than assigned: the platform is not the only thing that
        // may want to configure the context, and a test's own configuration has
        // to survive an upscaler being prepared.
        Action<VulkanContextOptions>? configured = target.ConfigureContextOptions;
        target.ConfigureContextOptions = options =>
        {
            configured?.Invoke(options);
            options.RequirementContributors.Add(requirements);
        };
    }

    /// <summary>
    /// The second half: NGX is initialised on the device that was just created.
    /// A refusal is not a failed install - the client keeps the Vulkan device and
    /// renders without an upscaler.
    /// </summary>
    internal void BringUpUpscaler(VulkanDevice target)
    {
        if (upscaler == null) return;

        target.UpscalerHandles(out IntPtr instance, out IntPtr physicalDevice, out IntPtr deviceHandle);
        if (!upscaler.BringUp(target, instance, physicalDevice, deviceHandle))
        {
            // TryPrepare's session is disposed with it; nothing was initialised,
            // so there is nothing to shut down.
            upscaler.Dispose();
            upscaler = null;
        }
    }

    /// <summary>
    /// Teardown, before the device goes: retire the feature, drain the frame
    /// timeline, shut NGX down. Releasing a feature after
    /// <c>NVSDK_NGX_VULKAN_Shutdown1</c>, or shutting NGX down after the device
    /// it was initialised on is gone, takes the process down inside the driver.
    /// </summary>
    internal void ShutDownUpscaler()
    {
        if (upscaler == null) return;
        try
        {
            upscaler.Shutdown();
        }
        catch (Exception error)
        {
            // A vendor runtime throwing on teardown must not stop the client exiting.
            LogUpscaler("[Optimum] DLSS teardown: " + error.Message);
        }
        upscaler = null;
        OptimumConfig.ClearUpscalerPlan();
    }

    /// <summary>
    /// Where NGX keeps its own logs and caches. The client's data path, which is
    /// writable by definition, with a temp-directory fallback for a headless host
    /// that has none.
    /// </summary>
    private string UpscalerDataPath()
    {
        string? dataPath = CrashMarkerDataPath ?? GamePaths.DataPath;
        if (string.IsNullOrEmpty(dataPath)) dataPath = Path.GetTempPath();
        return Path.Combine(dataPath, "ModConfig", "optimum-ngx");
    }

    /// <summary>
    /// The upscaler's one line goes to the client log, like every other backend
    /// decision, so "why is DLSS not on" is answered by the log the user already
    /// sends.
    /// </summary>
    private void LogUpscaler(string line)
    {
        Logger?.Notification(line);
    }
}
