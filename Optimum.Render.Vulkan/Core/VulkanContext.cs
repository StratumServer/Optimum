using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;

namespace Optimum.Render.Vulkan.Core;

/// <summary>How the context should be brought up.</summary>
internal sealed class VulkanContextOptions
{
    /// <summary>No surface, no swapchain. Used by tests and capability probes.</summary>
    public bool Headless;

    /// <summary>Turns on the validation layers and the debug messenger.</summary>
    public bool EnableValidation;

    /// <summary>Pins a physical device by index; -1 picks automatically.</summary>
    public int PreferredDeviceIndex = -1;

    /// <summary>Instance extensions the window system needs (from GLFW).</summary>
    public string[] RequiredInstanceExtensions = Array.Empty<string>();

    /// <summary>Called with each validation message when validation is on.</summary>
    public Action<string>? DebugCallback;
}

/// <summary>What the chosen device can do, once it is up.</summary>
internal sealed class VulkanCapabilities
{
    public string DeviceName = "";
    public string DriverName = "";
    public uint ApiVersion;
    public PhysicalDeviceType DeviceType;
    public uint MaxImageDimension2D;
    public bool WideLines;
    public bool FillModeNonSolid;
    public bool SamplerAnisotropy;
    public bool MultiDrawIndirect;
    public float MaxSamplerLodBias;
    public int MaxBoundDescriptorSets;
    public ulong MinUniformBufferOffsetAlignment;
}

/// <summary>
/// Instance, physical device, logical device and queue.
///
/// Device selection and feature negotiation are the one place this backend is
/// allowed to give up. Anything missing here means the session falls back to
/// OpenGL with a logged reason rather than failing, so every check reports
/// instead of throwing.
/// </summary>
internal sealed unsafe class VulkanContext : IDisposable
{
    /// <summary>
    /// The floor. 1.3 makes dynamic rendering and synchronization2 core, which
    /// removes render-pass and framebuffer objects from the design entirely, and
    /// brings the dynamic pipeline state that keeps the pipeline cache small.
    /// Everything below it stays on OpenGL.
    /// </summary>
    public static readonly uint MinimumApiVersion = Vk.Version13;

    public Vk Api { get; private set; } = null!;
    public Instance Instance { get; private set; }
    public PhysicalDevice PhysicalDevice { get; private set; }
    public Device Device { get; private set; }
    public Queue GraphicsQueue { get; private set; }
    public uint GraphicsQueueFamily { get; private set; }

    /// <summary>
    /// Guards every submission to <see cref="GraphicsQueue" />.
    ///
    /// Vulkan requires a queue to be externally synchronised: vkQueueSubmit and
    /// vkQueuePresentKHR from two threads at once is undefined behaviour, and in
    /// practice loses the device. The client does exactly that - asset loading
    /// uploads textures off the main thread, each upload being its own
    /// submit-and-wait, while the render thread is submitting frames. GL made
    /// this impossible by having one context on one thread; here it has to be
    /// enforced.
    /// </summary>
    public object QueueLock { get; } = new();
    public VulkanCapabilities Capabilities { get; private set; } = new();

    /// <summary>
    /// Whether the validation layers are actually loaded, which is not the same
    /// as having been asked for: the layer has to be installed on the machine.
    /// Worth being able to check, because "no validation messages" otherwise
    /// reads as "nothing is wrong".
    /// </summary>
    public bool ValidationEnabled { get; private set; }

    /// <summary>Marks a diagnostic the layers reported at error severity.</summary>
    public const string ErrorPrefix = "[error] ";

    private ExtDebugUtils? _debugUtils;
    private DebugUtilsMessengerEXT _debugMessenger;
    private Action<string>? _debugCallback;
    private PfnDebugUtilsMessengerCallbackEXT _debugDelegate;
    private bool _disposed;

    private const string ValidationLayer = "VK_LAYER_KHRONOS_validation";

    /// <summary>
    /// Brings the context up, or explains why it cannot. Never throws for an
    /// ordinary unsupported-hardware outcome.
    /// </summary>
    public static bool TryCreate(
        VulkanContextOptions options, out VulkanContext? context, out string? failureReason)
    {
        context = null;
        failureReason = null;

        var created = new VulkanContext();
        try
        {
            created.Api = Vk.GetApi();
        }
        catch (Exception error)
        {
            failureReason = "no Vulkan loader: " + error.Message;
            return false;
        }

        try
        {
            if (!created.CreateInstance(options, out failureReason)) { created.Dispose(); return false; }
            if (!created.SelectPhysicalDevice(options, out failureReason)) { created.Dispose(); return false; }
            if (!created.CreateDevice(options, out failureReason)) { created.Dispose(); return false; }
        }
        catch (Exception error)
        {
            failureReason = error.Message;
            created.Dispose();
            return false;
        }

        context = created;
        return true;
    }

    // ------------------------------------------------------------------ instance

    private bool CreateInstance(VulkanContextOptions options, out string? failureReason)
    {
        failureReason = null;

        uint loaderVersion = Vk.Version10;
        if (Api.EnumerateInstanceVersion(ref loaderVersion) != Result.Success)
        {
            loaderVersion = Vk.Version10;
        }
        if (loaderVersion < MinimumApiVersion)
        {
            failureReason =
                $"Vulkan loader reports {VersionString(loaderVersion)}, " +
                $"but {VersionString(MinimumApiVersion)} is required";
            return false;
        }

        var extensions = new List<string>(options.RequiredInstanceExtensions);
        bool validation = options.EnableValidation && HasValidationLayer();
        if (validation)
        {
            extensions.Add(ExtDebugUtils.ExtensionName);
        }

        byte* applicationName = (byte*)SilkMarshal.StringToPtr("Optimum");
        byte* engineName = (byte*)SilkMarshal.StringToPtr("Optimum.Render.Vulkan");
        nint extensionsPtr = SilkMarshal.StringArrayToPtr(extensions);
        nint layersPtr = validation ? SilkMarshal.StringArrayToPtr(new[] { ValidationLayer }) : 0;

        try
        {
            var applicationInfo = new ApplicationInfo
            {
                SType = StructureType.ApplicationInfo,
                PApplicationName = applicationName,
                ApplicationVersion = new Version32(1, 0, 0),
                PEngineName = engineName,
                EngineVersion = new Version32(1, 0, 0),
                ApiVersion = MinimumApiVersion,
            };

            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PApplicationInfo = &applicationInfo,
                EnabledExtensionCount = (uint)extensions.Count,
                PpEnabledExtensionNames = (byte**)extensionsPtr,
                EnabledLayerCount = validation ? 1u : 0u,
                PpEnabledLayerNames = validation ? (byte**)layersPtr : null,
            };

            Result result = Api.CreateInstance(&createInfo, null, out Instance instance);
            if (result != Result.Success)
            {
                failureReason = "vkCreateInstance failed: " + result;
                return false;
            }
            Instance = instance;
        }
        finally
        {
            SilkMarshal.Free((nint)applicationName);
            SilkMarshal.Free((nint)engineName);
            SilkMarshal.Free(extensionsPtr);
            if (layersPtr != 0) SilkMarshal.Free(layersPtr);
        }

        if (validation)
        {
            SetUpDebugMessenger(options);
        }

        return true;
    }

    private bool HasValidationLayer()
    {
        uint count = 0;
        if (Api.EnumerateInstanceLayerProperties(ref count, null) != Result.Success || count == 0)
        {
            return false;
        }

        var layers = new LayerProperties[count];
        fixed (LayerProperties* layersPtr = layers)
        {
            if (Api.EnumerateInstanceLayerProperties(ref count, layersPtr) != Result.Success)
            {
                return false;
            }

            for (int i = 0; i < count; i++)
            {
                if (SilkMarshal.PtrToString((nint)layersPtr[i].LayerName) == ValidationLayer)
                {
                    return true;
                }
            }
        }
        return false;
    }

    private void SetUpDebugMessenger(VulkanContextOptions options)
    {
        if (!Api.TryGetInstanceExtension(Instance, out ExtDebugUtils debugUtils)) return;

        _debugUtils = debugUtils;
        _debugCallback = options.DebugCallback;
        ValidationEnabled = true;
        _debugDelegate = new PfnDebugUtilsMessengerCallbackEXT(OnDebugMessage);

        var createInfo = new DebugUtilsMessengerCreateInfoEXT
        {
            SType = StructureType.DebugUtilsMessengerCreateInfoExt,
            MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.WarningBitExt
                | DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt,
            MessageType = DebugUtilsMessageTypeFlagsEXT.GeneralBitExt
                | DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt,
            PfnUserCallback = _debugDelegate,
        };

        _debugUtils.CreateDebugUtilsMessenger(Instance, &createInfo, null, out _debugMessenger);
    }

    private uint OnDebugMessage(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT types,
        DebugUtilsMessengerCallbackDataEXT* data,
        void* userData)
    {
        string? message = SilkMarshal.PtrToString((nint)data->PMessage);
        if (message != null)
        {
            // Severity is prefixed rather than dropped. The layers report real
            // spec violations alongside advisories - "this fragment output has
            // no attachment and the write is unused" is a note, not a fault -
            // and the client turns diagnostics into thrown exceptions through
            // CheckGlError. Without the distinction every advisory would read as
            // a GL error and abort a frame that was fine.
            string prefix = severity.HasFlag(DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt)
                ? ErrorPrefix
                : "[warning] ";
            _debugCallback?.Invoke(prefix + message);
        }
        return Vk.False;
    }

    // ----------------------------------------------------------- device selection

    private bool SelectPhysicalDevice(VulkanContextOptions options, out string? failureReason)
    {
        failureReason = null;

        uint count = 0;
        Api.EnumeratePhysicalDevices(Instance, ref count, null);
        if (count == 0)
        {
            failureReason = "no Vulkan physical devices";
            return false;
        }

        var devices = new PhysicalDevice[count];
        fixed (PhysicalDevice* devicesPtr = devices)
        {
            Api.EnumeratePhysicalDevices(Instance, ref count, devicesPtr);
        }

        if (options.PreferredDeviceIndex >= 0)
        {
            if (options.PreferredDeviceIndex >= devices.Length)
            {
                failureReason = $"device index {options.PreferredDeviceIndex} out of range ({devices.Length} present)";
                return false;
            }

            PhysicalDevice pinned = devices[options.PreferredDeviceIndex];
            if (!IsUsable(pinned, out string? why))
            {
                failureReason = $"pinned device is unusable: {why}";
                return false;
            }
            PhysicalDevice = pinned;
            return true;
        }

        // Prefer a discrete GPU, then integrated, then anything usable. The
        // handheld this targets has only an integrated one; a desktop with both
        // should get the fast one.
        var rejections = new List<string>();
        PhysicalDevice best = default;
        int bestScore = -1;

        foreach (PhysicalDevice candidate in devices)
        {
            if (!IsUsable(candidate, out string? why))
            {
                rejections.Add(why!);
                continue;
            }

            PhysicalDeviceProperties properties = Api.GetPhysicalDeviceProperties(candidate);
            int score = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 3,
                PhysicalDeviceType.IntegratedGpu => 2,
                PhysicalDeviceType.VirtualGpu => 1,
                _ => 0,
            };

            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        if (bestScore < 0)
        {
            failureReason = "no usable Vulkan device: " + string.Join("; ", rejections);
            return false;
        }

        PhysicalDevice = best;
        return true;
    }

    /// <summary>
    /// A device is usable when it meets the API floor, exposes a graphics queue,
    /// and supports the features the renderer is built on.
    /// </summary>
    private bool IsUsable(PhysicalDevice device, out string? reason)
    {
        PhysicalDeviceProperties properties = Api.GetPhysicalDeviceProperties(device);
        string name = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "unknown";

        if (properties.ApiVersion < MinimumApiVersion)
        {
            reason = $"{name} reports {VersionString(properties.ApiVersion)}, " +
                     $"below {VersionString(MinimumApiVersion)}";
            return false;
        }

        if (!TryFindGraphicsQueue(device, out _))
        {
            reason = $"{name} has no graphics queue family";
            return false;
        }

        var vulkan13 = new PhysicalDeviceVulkan13Features { SType = StructureType.PhysicalDeviceVulkan13Features };
        var vulkan12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = &vulkan13,
        };
        var features = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &vulkan12,
        };
        Api.GetPhysicalDeviceFeatures2(device, &features);

        var missing = new List<string>();
        if (!vulkan13.DynamicRendering) missing.Add("dynamicRendering");
        if (!vulkan13.Synchronization2) missing.Add("synchronization2");
        // Scalar layout is what lets the game's tightly packed float[] uniform
        // uploads land in the generated block as a memcpy.
        if (!vulkan12.ScalarBlockLayout) missing.Add("scalarBlockLayout");
        if (!vulkan12.TimelineSemaphore) missing.Add("timelineSemaphore");
        // The OIT and SSAO passes set blend state per attachment.
        if (!features.Features.IndependentBlend) missing.Add("independentBlend");
        // Chunk rendering issues one indirect multidraw per pool.
        if (!features.Features.MultiDrawIndirect) missing.Add("multiDrawIndirect");

        if (missing.Count > 0)
        {
            reason = $"{name} lacks {string.Join(", ", missing)}";
            return false;
        }

        reason = null;
        return true;
    }

    private bool TryFindGraphicsQueue(PhysicalDevice device, out uint family)
    {
        family = 0;
        uint count = 0;
        Api.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);
        if (count == 0) return false;

        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* familiesPtr = families)
        {
            Api.GetPhysicalDeviceQueueFamilyProperties(device, ref count, familiesPtr);
        }

        for (uint i = 0; i < count; i++)
        {
            if (families[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                family = i;
                return true;
            }
        }
        return false;
    }

    // -------------------------------------------------------------- logical device

    private bool CreateDevice(VulkanContextOptions options, out string? failureReason)
    {
        failureReason = null;

        if (!TryFindGraphicsQueue(PhysicalDevice, out uint family))
        {
            failureReason = "graphics queue family disappeared between selection and creation";
            return false;
        }
        GraphicsQueueFamily = family;

        float priority = 1.0f;
        var queueCreateInfo = new DeviceQueueCreateInfo
        {
            SType = StructureType.DeviceQueueCreateInfo,
            QueueFamilyIndex = family,
            QueueCount = 1,
            PQueuePriorities = &priority,
        };

        PhysicalDeviceFeatures available = Api.GetPhysicalDeviceFeatures(PhysicalDevice);

        var enabledFeatures = new PhysicalDeviceFeatures
        {
            IndependentBlend = true,
            MultiDrawIndirect = true,
            // Optional. Wireframe debug and thick lines degrade rather than fail.
            FillModeNonSolid = available.FillModeNonSolid,
            WideLines = available.WideLines,
            SamplerAnisotropy = available.SamplerAnisotropy,
            DepthClamp = available.DepthClamp,
            ShaderClipDistance = available.ShaderClipDistance,
            OcclusionQueryPrecise = available.OcclusionQueryPrecise,
        };

        var vulkan13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            DynamicRendering = true,
            Synchronization2 = true,
        };
        var vulkan12 = new PhysicalDeviceVulkan12Features
        {
            SType = StructureType.PhysicalDeviceVulkan12Features,
            PNext = &vulkan13,
            ScalarBlockLayout = true,
            TimelineSemaphore = true,
        };
        var features2 = new PhysicalDeviceFeatures2
        {
            SType = StructureType.PhysicalDeviceFeatures2,
            PNext = &vulkan12,
            Features = enabledFeatures,
        };

        var deviceExtensions = new List<string>();
        if (!options.Headless) deviceExtensions.Add("VK_KHR_swapchain");

        nint extensionsPtr = deviceExtensions.Count > 0
            ? SilkMarshal.StringArrayToPtr(deviceExtensions)
            : 0;

        try
        {
            var createInfo = new DeviceCreateInfo
            {
                SType = StructureType.DeviceCreateInfo,
                PNext = &features2,
                QueueCreateInfoCount = 1,
                PQueueCreateInfos = &queueCreateInfo,
                EnabledExtensionCount = (uint)deviceExtensions.Count,
                PpEnabledExtensionNames = extensionsPtr == 0 ? null : (byte**)extensionsPtr,
            };

            Result result = Api.CreateDevice(PhysicalDevice, &createInfo, null, out Device device);
            if (result != Result.Success)
            {
                failureReason = "vkCreateDevice failed: " + result;
                return false;
            }
            Device = device;
        }
        finally
        {
            if (extensionsPtr != 0) SilkMarshal.Free(extensionsPtr);
        }

        GraphicsQueue = Api.GetDeviceQueue(Device, family, 0);
        Capabilities = ReadCapabilities();
        return true;
    }

    private VulkanCapabilities ReadCapabilities()
    {
        PhysicalDeviceProperties properties = Api.GetPhysicalDeviceProperties(PhysicalDevice);
        PhysicalDeviceFeatures features = Api.GetPhysicalDeviceFeatures(PhysicalDevice);

        var driverProperties = new PhysicalDeviceDriverProperties
        {
            SType = StructureType.PhysicalDeviceDriverProperties,
        };
        var properties2 = new PhysicalDeviceProperties2
        {
            SType = StructureType.PhysicalDeviceProperties2,
            PNext = &driverProperties,
        };
        Api.GetPhysicalDeviceProperties2(PhysicalDevice, &properties2);

        return new VulkanCapabilities
        {
            DeviceName = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? "unknown",
            DriverName = SilkMarshal.PtrToString((nint)driverProperties.DriverName) ?? "unknown",
            ApiVersion = properties.ApiVersion,
            DeviceType = properties.DeviceType,
            MaxImageDimension2D = properties.Limits.MaxImageDimension2D,
            WideLines = features.WideLines,
            FillModeNonSolid = features.FillModeNonSolid,
            SamplerAnisotropy = features.SamplerAnisotropy,
            MultiDrawIndirect = features.MultiDrawIndirect,
            MaxSamplerLodBias = properties.Limits.MaxSamplerLodBias,
            MaxBoundDescriptorSets = (int)properties.Limits.MaxBoundDescriptorSets,
            MinUniformBufferOffsetAlignment = properties.Limits.MinUniformBufferOffsetAlignment,
        };
    }

    public static string VersionString(uint version) =>
        $"{version >> 22}.{(version >> 12) & 0x3FF}.{version & 0xFFF}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (Device.Handle != 0)
        {
            Api.DeviceWaitIdle(Device);
            Api.DestroyDevice(Device, null);
        }

        if (_debugUtils != null && _debugMessenger.Handle != 0)
        {
            _debugUtils.DestroyDebugUtilsMessenger(Instance, _debugMessenger, null);
            _debugUtils.Dispose();
        }

        if (Instance.Handle != 0)
        {
            Api.DestroyInstance(Instance, null);
        }

        Api?.Dispose();
    }
}
