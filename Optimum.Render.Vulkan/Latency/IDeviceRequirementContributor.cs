using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// A subsystem that needs something from the instance or the device (plan seam
/// S1). The latency backends are the first of these; NGX/Streamline's
/// <c>GetFeatureInstance/DeviceExtensionRequirements</c> plug in the same way.
///
/// Contributors are consulted inside <see cref="VulkanContext.CreateInstance" />
/// and <see cref="VulkanContext.CreateDevice" />, before the create call, and
/// may only ever <em>ask</em>: an extension the loader or the driver does not
/// advertise is refused by <see cref="InstanceRequirements.Request" /> /
/// <see cref="DeviceRequirements.Request" /> and reported back, never named in
/// the create info. Naming an absent extension fails creation outright, which
/// would turn an optional feature into a silent fall back to OpenGL (rule 1).
/// </summary>
internal interface IDeviceRequirementContributor
{
    /// <summary>Short name for the log line when a request is refused.</summary>
    string Name { get; }

    /// <summary>Called once before vkCreateInstance.</summary>
    void ContributeInstanceExtensions(InstanceRequirements requirements);

    /// <summary>
    /// Called once before vkCreateDevice, with the physical device already
    /// chosen, so a contributor can query features before deciding what to ask
    /// for. Anything chained here is part of the VkDeviceCreateInfo pNext chain.
    /// </summary>
    void ContributeDeviceRequirements(DeviceRequirements requirements);
}

/// <summary>What the loader advertises, and what the instance will enable.</summary>
internal sealed class InstanceRequirements
{
    private readonly HashSet<string> _available;
    private readonly List<string> _enabled;

    public InstanceRequirements(HashSet<string> available, List<string> enabled)
    {
        _available = available;
        _enabled = enabled;
    }

    /// <summary>Notes about refused requests; never an error.</summary>
    public Action<string>? Log;

    public bool Has(string name) => _available.Contains(name);

    public bool IsEnabled(string name) => _enabled.Contains(name);

    /// <summary>
    /// Enables <paramref name="name" /> when the loader has it. Returns whether
    /// the instance will have it; asking twice is harmless.
    /// </summary>
    public bool Request(string name, string? requestedBy = null)
    {
        if (_enabled.Contains(name)) return true;
        if (!_available.Contains(name))
        {
            Log?.Invoke((requestedBy ?? "a contributor") + " asked for instance extension " + name +
                ", which the loader does not advertise; continuing without it");
            return false;
        }
        _enabled.Add(name);
        return true;
    }

    public IReadOnlyList<string> Enabled => _enabled;
}

/// <summary>
/// What the physical device advertises, what the logical device will enable, and
/// the one pNext chain of VkDeviceCreateInfo.
///
/// The chain replaces the single-slot "optionalFeatures" of the pre-latency
/// context: colour write, device fault and every contributor's feature struct
/// now link into one list, so two optional tiers can be on at the same time.
/// Structs handed to <see cref="ChainFeature{T}" /> are copied into unmanaged
/// scratch owned here and freed by <see cref="Dispose" /> after vkCreateDevice
/// has returned, so a contributor never has to keep memory pinned itself.
/// </summary>
internal sealed unsafe class DeviceRequirements : IDisposable
{
    private readonly Dictionary<string, uint> _available;
    private readonly List<string> _enabled;
    private readonly List<nint> _scratch = new();
    private void* _chain;
    private bool _disposed;

    public DeviceRequirements(
        Vk api, Instance instance, PhysicalDevice physicalDevice,
        Dictionary<string, uint> available, List<string> enabled)
    {
        Api = api;
        Instance = instance;
        PhysicalDevice = physicalDevice;
        _available = available;
        _enabled = enabled;
    }

    public Vk Api { get; }

    /// <summary>
    /// The instance the device is being created on. NGX's
    /// <c>GetFeatureDeviceExtensionRequirements</c> takes both the instance and
    /// the physical device, so a contributor has to be able to see it.
    /// </summary>
    public Instance Instance { get; }

    public PhysicalDevice PhysicalDevice { get; }

    /// <summary>Notes about refused requests; never an error.</summary>
    public Action<string>? Log;

    /// <summary>Whether the device advertises the extension at or above a revision.</summary>
    public bool Has(string name, uint minimumSpecVersion = 0) =>
        _available.TryGetValue(name, out uint version) && version >= minimumSpecVersion;

    /// <summary>The advertised revision, 0 when the device does not have the extension.</summary>
    public uint SpecVersion(string name) => _available.TryGetValue(name, out uint version) ? version : 0;

    public bool IsEnabled(string name) => _enabled.Contains(name);

    /// <summary>
    /// Enables <paramref name="name" /> when the device advertises it at or above
    /// <paramref name="minimumSpecVersion" />. Returns whether the device will
    /// have it; asking twice is harmless.
    /// </summary>
    public bool Request(string name, uint minimumSpecVersion = 0, string? requestedBy = null)
    {
        if (_enabled.Contains(name)) return true;
        if (!Has(name, minimumSpecVersion))
        {
            Log?.Invoke((requestedBy ?? "a contributor") + " asked for device extension " + name +
                (minimumSpecVersion > 0 ? " revision >= " + minimumSpecVersion : "") +
                ", which this device does not advertise (" +
                (_available.ContainsKey(name) ? "revision " + SpecVersion(name) : "absent") +
                "); continuing without it");
            return false;
        }
        _enabled.Add(name);
        return true;
    }

    /// <summary>
    /// Queries physical-device features through a caller-built pNext chain. The
    /// two-step shape the colour-write probe uses: query what is supported, then
    /// re-request only what is actually going to be used.
    /// </summary>
    public void QueryFeatures(void* chainHead)
    {
        var query = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = chainHead,
        };
        Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &query);
    }

    /// <summary>
    /// Links a struct the caller keeps alive (a stack local of the creating
    /// method) into the chain. Its own pNext is overwritten.
    /// </summary>
    public void ChainFeature(void* feature)
    {
        if (feature == null) return;
        // Every Vulkan structure begins with VkStructureType sType; void* pNext,
        // so pNext sits one pointer in on both 32- and 64-bit ABIs.
        *(void**)((byte*)feature + IntPtr.Size) = _chain;
        _chain = feature;
    }

    /// <summary>
    /// Copies <paramref name="feature" /> into scratch memory owned here and
    /// links it in. The copy lives until <see cref="Dispose" />, which the
    /// context calls after vkCreateDevice.
    /// </summary>
    public void ChainFeature<T>(T feature) where T : unmanaged
    {
        nint memory = Marshal.AllocHGlobal(sizeof(T));
        _scratch.Add(memory);
        *(T*)memory = feature;
        ChainFeature((void*)memory);
    }

    /// <summary>The head of the chain, null when nothing was chained.</summary>
    public void* Chain => _chain;

    public IReadOnlyList<string> Enabled => _enabled;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _chain = null;
        for (int i = 0; i < _scratch.Count; i++) Marshal.FreeHGlobal(_scratch[i]);
        _scratch.Clear();
    }
}
