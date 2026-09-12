using System;

namespace Optimum.Render.Vulkan.Core;

/// <summary>What a lifetime call did, for the caller's log and for the tests.</summary>
internal enum NgxLifetimeOutcome
{
    /// <summary>The call did what it asked for.</summary>
    Done,

    /// <summary>NGX was never initialised here, so there was nothing to do.</summary>
    NotInitialized,

    /// <summary>NGX was already shut down in this process; the call did nothing.</summary>
    AlreadyShutDown,

    /// <summary>NGX is already up, so a second bring-up was refused.</summary>
    AlreadyInitialized,

    /// <summary>The shutdown was refused because a feature was still live.</summary>
    FeatureStillLive,
}

/// <summary>
/// The owner of <c>NVSDK_NGX_VULKAN_Init_ProjectID</c> and
/// <c>NVSDK_NGX_VULKAN_Shutdown1</c>. There is one of these per process
/// (<see cref="NgxLifetime.Process" />); the type is instantiable only so a test
/// can drive the contract without touching the process's own lifetime.
///
/// <para><b>Why an owner rather than a rule.</b> Both halves of the NGX lifetime
/// are process-wide, not per host: NGX allows exactly one Init/Shutdown pair per
/// process, and the shutdown has to happen after the last feature is released,
/// after the frame timeline is drained, and before the VkDevice it was initialised
/// on is destroyed. Every one of those is an ordering constraint between pieces
/// that do not otherwise know about each other - the settings tab, the upscaler
/// host, the device, the platform's teardown - so "call these in this order" is a
/// rule that some later call site will get wrong. Here there is nothing to get
/// wrong from the outside: the only way to shut NGX down is
/// <see cref="ShutDown" />, which performs the release and the drain itself,
/// refuses to run twice, and refuses to run while a feature is still live.</para>
///
/// <para><b>A settings change never comes here.</b> Switching the upscaler off,
/// changing the preset and a runtime stand-down all retire the feature onto the
/// frame timeline and leave NGX initialised - they must, because the process
/// cannot bring NGX back up afterwards and the user may switch back on. The only
/// caller of <see cref="ShutDown" /> is the platform's graphics teardown.</para>
///
/// <para><b>What actually crashed on 2026-09-12</b> was none of this: the driver's
/// <c>Shutdown1</c> takes a second, undeclared parameter and writes the SDK's
/// remaining reference count through it, while the SDK header declares one. That
/// is fixed in the shim (<c>native/optimum-ngx/optimum_ngx.c</c>); this type is
/// what makes sure the call it fixed happens exactly once, in the right place.</para>
/// </summary>
internal sealed class NgxLifetimeOwner
{
    private readonly object _gate = new();
    private readonly Func<IntPtr, NgxResult> _shutdownCall;

    private bool _initialized;
    private bool _shutDown;
    private IntPtr _device;
    private int _liveFeatures;

    /// <summary>
    /// <paramref name="shutdownCall" /> is the real <c>Shutdown1</c> unless a test
    /// substitutes a recorder for it.
    /// </summary>
    internal NgxLifetimeOwner(Func<IntPtr, NgxResult>? shutdownCall = null)
    {
        _shutdownCall = shutdownCall ?? (device =>
            NgxInterop.ManagedCallSiteIsSupported ? NgxInterop.Shutdown1(device) : NgxResult.FailNotImplemented);
    }

    /// <summary>Whether NGX is up and features may be created.</summary>
    internal bool Initialized
    {
        get { lock (_gate) { return _initialized; } }
    }

    /// <summary>
    /// Whether this process has spent its one NGX lifetime. Once true, no host may
    /// bring NGX up again for the life of the process.
    /// </summary>
    internal bool Spent
    {
        get { lock (_gate) { return _shutDown; } }
    }

    /// <summary>How many times <c>Shutdown1</c> was really called. Never more than one.</summary>
    internal int ShutdownCalls { get; private set; }

    /// <summary>How many times <c>Init</c> was really called. Never more than one.</summary>
    internal int InitCalls { get; private set; }

    /// <summary>Features created and not yet retired; the shutdown refuses to run above zero.</summary>
    internal int LiveFeatures
    {
        get { lock (_gate) { return _liveFeatures; } }
    }

    /// <summary>What <c>Shutdown1</c> answered, or <see cref="NgxResult.FailNotInitialized" /> before it ran.</summary>
    internal NgxResult ShutdownResult { get; private set; } = NgxResult.FailNotInitialized;

    /// <summary>The managed thread NGX was brought up on, or 0. Init and Shutdown belong to one thread.</summary>
    internal int OwningThread { get; private set; }

    /// <summary>
    /// Brings NGX up on one device, once per process. Anything other than
    /// <see cref="NgxLifetimeOutcome.Done" /> means the caller runs without an
    /// upscaler; <paramref name="result" /> carries what NGX answered when it was
    /// really called.
    /// </summary>
    internal NgxLifetimeOutcome Initialize(
        NgxSession session, IntPtr instance, IntPtr physicalDevice, IntPtr vkDevice, out NgxResult result) =>
        Initialize(vkDevice, () => session.Initialize(instance, physicalDevice, vkDevice), out result);

    /// <summary>
    /// The same, with the bring-up call itself passed in, so a test can pin the
    /// contract without a driver.
    /// </summary>
    internal NgxLifetimeOutcome Initialize(IntPtr vkDevice, Func<NgxResult> bringUp, out NgxResult result)
    {
        result = NgxResult.FailNotInitialized;
        lock (_gate)
        {
            if (_shutDown) return NgxLifetimeOutcome.AlreadyShutDown;
            if (_initialized) return NgxLifetimeOutcome.AlreadyInitialized;

            InitCalls++;
            result = bringUp();
            if (result != NgxResult.Success) return NgxLifetimeOutcome.NotInitialized;

            _initialized = true;
            _device = vkDevice;
            OwningThread = Environment.CurrentManagedThreadId;
            return NgxLifetimeOutcome.Done;
        }
    }

    /// <summary>A feature was created. Balanced by <see cref="FeatureRetired" />.</summary>
    internal void FeatureCreated()
    {
        lock (_gate) { _liveFeatures++; }
    }

    /// <summary>A feature went onto the frame timeline, so it no longer counts as live.</summary>
    internal void FeatureRetired()
    {
        lock (_gate) { if (_liveFeatures > 0) _liveFeatures--; }
    }

    /// <summary>
    /// The process's one shutdown, in the only order the driver survives - performed
    /// here rather than asked of the caller:
    /// <list type="number">
    /// <item><paramref name="releaseFeatures" /> retires whatever the owner still
    /// holds onto the frame timeline;</item>
    /// <item><paramref name="drainFrameTimeline" /> waits for every submitted frame
    /// and destroys the retire queue, so no live handle names NGX state any more;</item>
    /// <item><c>NVSDK_NGX_VULKAN_Shutdown1</c> runs, once, on the device NGX was
    /// initialised on - which the caller may destroy only afterwards.</item>
    /// </list>
    ///
    /// Called twice - which the platform's teardown can be, on the fallback path -
    /// the second call answers <see cref="NgxLifetimeOutcome.AlreadyShutDown" /> and
    /// touches nothing: a second <c>Shutdown1</c> is not survivable.
    /// </summary>
    internal NgxLifetimeOutcome ShutDown(
        Action? releaseFeatures, Func<int>? drainFrameTimeline, Action<string>? log = null)
    {
        lock (_gate)
        {
            if (_shutDown) return NgxLifetimeOutcome.AlreadyShutDown;
            if (!_initialized)
            {
                // Nothing was ever brought up, so there is nothing to shut down - but
                // the lifetime is spent all the same: a host that failed to initialise
                // must not try again on another device.
                _shutDown = true;
                return NgxLifetimeOutcome.NotInitialized;
            }

            // The order is performed here, not asked for.
            if (releaseFeatures != null) releaseFeatures();
            if (drainFrameTimeline != null) drainFrameTimeline();

            if (_liveFeatures > 0)
            {
                // Refusing is the safe half of the fatal pair: releasing a feature after
                // Shutdown1 takes the process down inside the driver, so NGX stays up and
                // the device is destroyed under it instead, which is survivable.
                if (log != null)
                {
                    log("[Optimum] DLSS: NGX was not shut down - " + _liveFeatures +
                        " feature(s) were still live after the release and the drain.");
                }
                return NgxLifetimeOutcome.FeatureStillLive;
            }

            _shutDown = true;
            ShutdownCalls++;
            ShutdownResult = _shutdownCall(_device);
            _initialized = false;
            _device = IntPtr.Zero;
            if (log != null)
            {
                log("[Optimum] DLSS: NVSDK_NGX_VULKAN_Shutdown1: " + NgxInterop.Describe(ShutdownResult));
            }
            return NgxLifetimeOutcome.Done;
        }
    }
}

/// <summary>
/// The process's one <see cref="NgxLifetimeOwner" />. Every NGX bring-up and the
/// one shutdown go through this; nothing else in the process may call
/// <c>NVSDK_NGX_VULKAN_Shutdown1</c>.
/// </summary>
internal static class NgxLifetime
{
    /// <summary>The owner for this process.</summary>
    internal static readonly NgxLifetimeOwner Process = new();

    internal static bool Initialized => Process.Initialized;

    internal static bool Spent => Process.Spent;

    internal static int ShutdownCalls => Process.ShutdownCalls;

    internal static int LiveFeatures => Process.LiveFeatures;

    internal static NgxResult ShutdownResult => Process.ShutdownResult;

    internal static NgxLifetimeOutcome Initialize(
        NgxSession session, IntPtr instance, IntPtr physicalDevice, IntPtr vkDevice, out NgxResult result) =>
        Process.Initialize(session, instance, physicalDevice, vkDevice, out result);

    internal static void FeatureCreated() => Process.FeatureCreated();

    internal static void FeatureRetired() => Process.FeatureRetired();

    internal static NgxLifetimeOutcome ShutDown(
        Action? releaseFeatures, Func<int>? drainFrameTimeline, Action<string>? log = null) =>
        Process.ShutDown(releaseFeatures, drainFrameTimeline, log);
}
