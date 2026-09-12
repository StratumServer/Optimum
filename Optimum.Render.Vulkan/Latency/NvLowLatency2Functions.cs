using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// VK_NV_low_latency2's five entry points, loaded by address exactly as
/// VK_NV_device_diagnostic_checkpoints is (<c>VulkanContext.LoadDiagnosticExtensions</c>):
/// Silk.NET 2.23.0 has the structures but no wrapper class for this extension,
/// and five functions do not justify another NuGet package and another native
/// DLL to ship.
///
/// This type only loads and reports; the backend that calls the functions is a
/// later stage (plan wave 3). <see cref="Load" /> returns null unless every entry
/// point resolved, so a caller never has to check them one at a time.
/// </summary>
internal sealed unsafe class NvLowLatency2Functions
{
    private readonly delegate* unmanaged<Device, SwapchainKHR, LatencySleepModeInfoNV*, Result> _setLatencySleepMode;
    private readonly delegate* unmanaged<Device, SwapchainKHR, LatencySleepInfoNV*, Result> _latencySleep;
    private readonly delegate* unmanaged<Device, SwapchainKHR, SetLatencyMarkerInfoNV*, void> _setLatencyMarker;
    private readonly delegate* unmanaged<Device, SwapchainKHR, GetLatencyMarkerInfoNV*, void> _getLatencyTimings;
    private readonly delegate* unmanaged<Queue, OutOfBandQueueTypeInfoNV*, void> _queueNotifyOutOfBand;

    private NvLowLatency2Functions(
        delegate* unmanaged<Device, SwapchainKHR, LatencySleepModeInfoNV*, Result> setLatencySleepMode,
        delegate* unmanaged<Device, SwapchainKHR, LatencySleepInfoNV*, Result> latencySleep,
        delegate* unmanaged<Device, SwapchainKHR, SetLatencyMarkerInfoNV*, void> setLatencyMarker,
        delegate* unmanaged<Device, SwapchainKHR, GetLatencyMarkerInfoNV*, void> getLatencyTimings,
        delegate* unmanaged<Queue, OutOfBandQueueTypeInfoNV*, void> queueNotifyOutOfBand)
    {
        _setLatencySleepMode = setLatencySleepMode;
        _latencySleep = latencySleep;
        _setLatencyMarker = setLatencyMarker;
        _getLatencyTimings = getLatencyTimings;
        _queueNotifyOutOfBand = queueNotifyOutOfBand;
    }

    /// <summary>
    /// Resolves the entry points on a device that enabled VK_NV_low_latency2.
    /// Null when the extension was not enabled, or when the driver resolved only
    /// part of it - a half-loaded extension is never used.
    /// </summary>
    public static NvLowLatency2Functions? Load(Vk api, Device device)
    {
        if (device.Handle == 0) return null;

        nint setMode = (nint)api.GetDeviceProcAddr(device, "vkSetLatencySleepModeNV").Handle;
        nint sleep = (nint)api.GetDeviceProcAddr(device, "vkLatencySleepNV").Handle;
        nint marker = (nint)api.GetDeviceProcAddr(device, "vkSetLatencyMarkerNV").Handle;
        nint timings = (nint)api.GetDeviceProcAddr(device, "vkGetLatencyTimingsNV").Handle;
        nint outOfBand = (nint)api.GetDeviceProcAddr(device, "vkQueueNotifyOutOfBandNV").Handle;

        if (setMode == 0 || sleep == 0 || marker == 0 || timings == 0 || outOfBand == 0) return null;

        return new NvLowLatency2Functions(
            (delegate* unmanaged<Device, SwapchainKHR, LatencySleepModeInfoNV*, Result>)setMode,
            (delegate* unmanaged<Device, SwapchainKHR, LatencySleepInfoNV*, Result>)sleep,
            (delegate* unmanaged<Device, SwapchainKHR, SetLatencyMarkerInfoNV*, void>)marker,
            (delegate* unmanaged<Device, SwapchainKHR, GetLatencyMarkerInfoNV*, void>)timings,
            (delegate* unmanaged<Queue, OutOfBandQueueTypeInfoNV*, void>)outOfBand);
    }

    /// <summary>Turns low-latency mode and boost on or off for one swapchain.</summary>
    public Result SetLatencySleepMode(Device device, SwapchainKHR swapchain, ref LatencySleepModeInfoNV info)
    {
        fixed (LatencySleepModeInfoNV* pointer = &info)
        {
            return _setLatencySleepMode(device, swapchain, pointer);
        }
    }

    /// <summary>The frame's sleep: returns once the driver signals the info's semaphore value.</summary>
    public Result LatencySleep(Device device, SwapchainKHR swapchain, ref LatencySleepInfoNV info)
    {
        fixed (LatencySleepInfoNV* pointer = &info)
        {
            return _latencySleep(device, swapchain, pointer);
        }
    }

    /// <summary>Stamps one phase marker of the frame.</summary>
    public void SetLatencyMarker(Device device, SwapchainKHR swapchain, ref SetLatencyMarkerInfoNV info)
    {
        fixed (SetLatencyMarkerInfoNV* pointer = &info)
        {
            _setLatencyMarker(device, swapchain, pointer);
        }
    }

    /// <summary>
    /// Reads back the driver's per-frame timing reports. Called twice, as the
    /// extension asks: once with a null array to learn the count, once to fill it.
    /// </summary>
    public void GetLatencyTimings(Device device, SwapchainKHR swapchain, ref GetLatencyMarkerInfoNV info)
    {
        fixed (GetLatencyMarkerInfoNV* pointer = &info)
        {
            _getLatencyTimings(device, swapchain, pointer);
        }
    }

    /// <summary>Marks work on a queue as out of band, so it is not attributed to a frame.</summary>
    public void QueueNotifyOutOfBand(Queue queue, ref OutOfBandQueueTypeInfoNV info)
    {
        fixed (OutOfBandQueueTypeInfoNV* pointer = &info)
        {
            _queueNotifyOutOfBand(queue, pointer);
        }
    }
}
