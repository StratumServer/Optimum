using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// VK_AMD_anti_lag's single entry point, loaded by address like the NV one
/// (<see cref="NvLowLatency2Functions" />): Silk.NET 2.23.0 has
/// VkAntiLagDataAMD but no wrapper class, and one function does not justify a
/// package.
///
/// The extension is also reachable on this machine's Intel iGPU through
/// VK_LAYER_MESA_anti_lag, which is what lets the AMD path run in tests at all.
/// This type only loads and reports; the backend that drives INPUT and PRESENT
/// stages is a later stage.
/// </summary>
internal sealed unsafe class AmdAntiLagFunctions
{
    private readonly delegate* unmanaged<Device, AntiLagDataAMD*, void> _antiLagUpdate;

    private AmdAntiLagFunctions(delegate* unmanaged<Device, AntiLagDataAMD*, void> antiLagUpdate) =>
        _antiLagUpdate = antiLagUpdate;

    /// <summary>
    /// Resolves vkAntiLagUpdateAMD on a device that enabled VK_AMD_anti_lag;
    /// null when it did not, or when the driver did not resolve it.
    /// </summary>
    public static AmdAntiLagFunctions? Load(Vk api, Device device)
    {
        if (device.Handle == 0) return null;

        nint update = (nint)api.GetDeviceProcAddr(device, "vkAntiLagUpdateAMD").Handle;
        if (update == 0) return null;

        return new AmdAntiLagFunctions((delegate* unmanaged<Device, AntiLagDataAMD*, void>)update);
    }

    /// <summary>
    /// One anti-lag update. Stage INPUT is the sleep and blocks; stage PRESENT is
    /// stamped immediately before vkQueuePresentKHR with the same frame index.
    /// </summary>
    public void AntiLagUpdate(Device device, ref AntiLagDataAMD data)
    {
        fixed (AntiLagDataAMD* pointer = &data)
        {
            _antiLagUpdate(device, pointer);
        }
    }
}
