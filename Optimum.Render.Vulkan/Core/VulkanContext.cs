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

    /// <summary>Comma list of extra layer features: sync, best, gpu.</summary>
    public string ValidationFeatures = "";

    /// <summary>Pins a physical device by index; -1 picks automatically.</summary>
    public int PreferredDeviceIndex = -1;

    /// <summary>Instance extensions the window system needs (from GLFW).</summary>
    public string[] RequiredInstanceExtensions = Array.Empty<string>();

    /// <summary>Called with each validation message when validation is on.</summary>
    public Action<string>? DebugCallback;

    /// <summary>
    /// Fills freshly created images and host-visible buffers with a loud value
    /// before first use (see <see cref="VulkanPoison" />). Null reads
    /// OPTIMUM_VULKAN_POISON once, at context creation.
    /// </summary>
    public bool? Poison;

    /// <summary>
    /// Forces a colour write tier (<see cref="Core.ColorWriteTier" />); null reads
    /// OPTIMUM_VULKAN_COLOR_WRITE_TIER. A tier the device lacks degrades to the next below.
    /// </summary>
    public ColorWriteTier? ColorWriteTier;

    /// <summary>
    /// Forces a latency backend (<see cref="Core.LatencyBackendKind" />); null reads
    /// OPTIMUM_VULKAN_LATENCY. A backend the device lacks degrades to Native, the
    /// same rule the colour-write tier follows. Tests use it to pin a backend.
    /// </summary>
    public LatencyBackendKind? LatencyBackend;

    /// <summary>
    /// Tests only: sleeps this long before every vkAcquireNextImageKHR, standing
    /// in for a compositor that holds images back (PresentDecouplingTests).
    /// </summary>
    public TimeSpan AcquireDelayForTests;
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
    /// <summary>Enabled whenever available; occlusion queries then count samples exactly, like GL_SAMPLES_PASSED.</summary>
    public bool OcclusionQueryPrecise;
    public float MaxSamplerLodBias;
    public int MaxBoundDescriptorSets;
    public ulong MinUniformBufferOffsetAlignment;
    public ulong MaxUniformBufferRange;
    public uint MaxColorAttachments = 8;

    /// <summary>VK_EXT_color_write_enable enabled (only when the selected tier uses it).</summary>
    public bool ColorWriteEnable;
    /// <summary>VK_EXT_extended_dynamic_state3 colorWriteMask enabled (only for the mask tier).</summary>
    public bool DynamicColorWriteMask;
    /// <summary>colorBlendEnable + colorBlendEquation enabled alongside the mask tier: the blend set is dynamic.</summary>
    public bool DynamicColorBlend;
    /// <summary>The tier draws use; see <see cref="Core.ColorWriteTier" />.</summary>
    public ColorWriteTier ColorWriteTier = ColorWriteTier.PipelineKey;
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

    /// <summary>
    /// Backs every buffer and image out of a few large blocks. See
    /// <see cref="VulkanAllocator" /> for why one allocation per resource is not
    /// an option.
    /// </summary>
    public VulkanAllocator Allocator { get; private set; } = null!;

    /// <summary>
    /// VK_EXT_memory_budget is enabled, so the allocator reads per-heap budgets
    /// from the driver. Off when the device lacks it or OPTIMUM_VULKAN_NO_MEMORY_BUDGET=1
    /// forces the heap x 0.7 fallback.
    /// </summary>
    public bool MemoryBudgetAvailable { get; private set; }
    public VulkanCapabilities Capabilities { get; private set; } = new();

    /// <summary>vkCmdSetColorWriteEnableEXT, when the enable tier is selected.</summary>
    public ExtColorWriteEnable? ColorWriteEnableApi { get; private set; }

    /// <summary>vkCmdSetColorWriteMaskEXT / BlendEnable / BlendEquation, when the mask tier is selected.</summary>
    public ExtExtendedDynamicState3? DynamicState3Api { get; private set; }

    /// <summary>
    /// Whether the validation layers are actually loaded, which is not the same
    /// as having been asked for: the layer has to be installed on the machine.
    /// Worth being able to check, because "no validation messages" otherwise
    /// reads as "nothing is wrong".
    /// </summary>
    public bool ValidationEnabled { get; private set; }

    /// <summary>
    /// Whether freshly created images and host-visible buffers are filled with
    /// <see cref="VulkanPoison" /> values. Fixed for the context's life.
    /// </summary>
    public bool PoisonFreshResources { get; private set; }

    /// <summary>Tests only; see <see cref="VulkanContextOptions.AcquireDelayForTests" />.</summary>
    public TimeSpan AcquireDelayForTests { get; private set; }

    /// <summary>OPTIMUM_VULKAN_POISON: any value but empty and "0" turns poison mode on.</summary>
    public const string PoisonVariable = "OPTIMUM_VULKAN_POISON";

    internal static bool PoisonRequested(string? setting) =>
        !string.IsNullOrWhiteSpace(setting) && setting.Trim() != "0";

    /// <summary>Marks a diagnostic the layers reported at error severity.</summary>
    public const string ErrorPrefix = "[error] ";

    /// <summary>
    /// Whether the driver records GPU checkpoints (VK_NV_device_diagnostic_checkpoints).
    ///
    /// A device loss otherwise says only that the GPU gave up. With checkpoints
    /// the driver also reports the last marker each pipeline stage reached, which
    /// names the draw or copy it was executing when it stopped. NVIDIA only; on
    /// by default where present, off with OPTIMUM_VULKAN_CHECKPOINTS=0.
    /// </summary>
    public bool CheckpointsAvailable { get; private set; }

    /// <summary>Whether VK_EXT_device_fault can describe a loss after the fact.</summary>
    public bool DeviceFaultAvailable { get; private set; }

    // Loaded by address rather than through an extension package: two entry
    // points do not justify a dependency and another native DLL to ship.
    private nint _cmdSetCheckpoint;
    private nint _getQueueCheckpointData;
    private ExtDeviceFault? _deviceFault;

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
        created.AcquireDelayForTests = options.AcquireDelayForTests;
        created.PoisonFreshResources = options.Poison
            ?? PoisonRequested(Environment.GetEnvironmentVariable(PoisonVariable));
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

        // Extra layer features (sync validation, best practices, GPU assisted)
        // ride on VK_EXT_validation_features. Parse them before the extension
        // list is marshalled, because the extension has to be enabled under
        // exactly the same condition as the pNext chain below - a chained
        // struct whose extension was never enabled is ignored at best.
        // Only when the layer actually advertises it: the extension is deprecated
        // in favour of VK_EXT_layer_settings, and naming one the layer does not
        // have fails vkCreateInstance outright - which would turn a diagnostic
        // environment variable into a silent fall back to OpenGL (rule 1).
        List<ValidationFeatureEnableEXT> enables = ParseValidationFeatures(options.ValidationFeatures);
        bool chainValidationFeatures = validation && enables.Count > 0
            && LayerAdvertisesExtension(Api, ValidationLayer, ValidationFeaturesExtensionName);
        if (validation)
        {
            extensions.Add(ExtDebugUtils.ExtensionName);
        }
        if (chainValidationFeatures)
        {
            extensions.Add(ValidationFeaturesExtensionName);
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

            ValidationFeatureEnableEXT* enablesPtr = stackalloc ValidationFeatureEnableEXT[Math.Max(enables.Count, 1)];
            for (int i = 0; i < enables.Count; i++) enablesPtr[i] = enables[i];
            var validationFeatures = new ValidationFeaturesEXT
            {
                SType = StructureType.ValidationFeaturesExt,
                EnabledValidationFeatureCount = (uint)enables.Count,
                PEnabledValidationFeatures = enablesPtr,
            };

            var createInfo = new InstanceCreateInfo
            {
                SType = StructureType.InstanceCreateInfo,
                PNext = chainValidationFeatures ? &validationFeatures : null,
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

    /// <summary>Name of VK_EXT_validation_features; Silk.NET has no wrapper class for it.</summary>
    internal const string ValidationFeaturesExtensionName = "VK_EXT_validation_features";

    /// <summary>
    /// Whether <paramref name="layerName" /> advertises <paramref name="extensionName" />
    /// as an instance extension. A layer's extensions are invisible to the
    /// loader-level enumeration, so the layer has to be named explicitly.
    /// </summary>
    internal static bool LayerAdvertisesExtension(Vk api, string layerName, string extensionName)
    {
        nint layer = SilkMarshal.StringToPtr(layerName);
        try
        {
            uint count = 0;
            if (api.EnumerateInstanceExtensionProperties((byte*)layer, &count, null) != Result.Success
                || count == 0)
            {
                return false;
            }

            var properties = new ExtensionProperties[count];
            fixed (ExtensionProperties* propertiesPtr = properties)
            {
                if (api.EnumerateInstanceExtensionProperties((byte*)layer, &count, propertiesPtr) != Result.Success)
                {
                    return false;
                }
                for (int i = 0; i < count; i++)
                {
                    // The name is a fixed-size buffer, readable only through a pointer.
                    if (SilkMarshal.PtrToString((nint)propertiesPtr[i].ExtensionName) == extensionName)
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        finally
        {
            SilkMarshal.Free(layer);
        }
    }

    /// <summary>Maps the comma list from OPTIMUM_VULKAN_VALIDATION_FEATURES onto layer feature flags.</summary>
    internal static List<ValidationFeatureEnableEXT> ParseValidationFeatures(string? features)
    {
        var enables = new List<ValidationFeatureEnableEXT>();
        foreach (string feature in (features ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (feature.ToLowerInvariant())
            {
                case "sync": enables.Add(ValidationFeatureEnableEXT.SynchronizationValidationExt); break;
                case "best": enables.Add(ValidationFeatureEnableEXT.BestPracticesExt); break;
                case "gpu": enables.Add(ValidationFeatureEnableEXT.GpuAssistedExt); break;
            }
        }
        return enables;
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
            // The layer names the check separately (SYNC-HAZARD-WRITE-AFTER-WRITE,
            // BestPractices-..., a VUID); current layers no longer repeat it in
            // the text, and without it a log line cannot be grouped or pinned.
            string? id = SilkMarshal.PtrToString((nint)data->PMessageIdName);
            string idTag = string.IsNullOrEmpty(id) ? "" : "[" + id + "] ";
            _debugCallback?.Invoke(prefix + idTag + message);
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

        // Diagnostics for a lost device. Both are optional and cost nothing when
        // the GPU is healthy, so they are taken wherever the driver offers them.
        HashSet<string> deviceExtensionsAvailable = EnumerateDeviceExtensions();
        bool checkpointsDisabled =
            Environment.GetEnvironmentVariable("OPTIMUM_VULKAN_CHECKPOINTS") is "0" or "off" or "false";
        bool wantCheckpoints = !checkpointsDisabled && IntPtr.Size == 8
            && deviceExtensionsAvailable.Contains("VK_NV_device_diagnostic_checkpoints");

        var faultFeatures = new PhysicalDeviceFaultFeaturesEXT
        {
            SType = StructureType.PhysicalDeviceFaultFeaturesExt,
        };
        bool wantDeviceFault = false;
        if (deviceExtensionsAvailable.Contains("VK_EXT_device_fault"))
        {
            var query = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &faultFeatures,
            };
            Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &query);
            wantDeviceFault = faultFeatures.DeviceFault;

            // Re-request only the feature that is wanted; the query may have
            // reported others this backend has no use for.
            faultFeatures = new PhysicalDeviceFaultFeaturesEXT
            {
                SType = StructureType.PhysicalDeviceFaultFeaturesExt,
                DeviceFault = wantDeviceFault,
            };
        }

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

        // Optional tier (Phase 2, C4): colour write masks as dynamic state. Only the
        // extension the selected tier uses is enabled, so validation sees exactly
        // what draws record. OPTIMUM_VULKAN_COLOR_WRITE_TIER forces a fallback.
        var colorWriteFeatures = new PhysicalDeviceColorWriteEnableFeaturesEXT
        {
            SType = StructureType.PhysicalDeviceColorWriteEnableFeaturesExt,
        };
        var dynamicState3Features = new PhysicalDeviceExtendedDynamicState3FeaturesEXT
        {
            SType = StructureType.PhysicalDeviceExtendedDynamicState3FeaturesExt,
        };
        bool hasColorWriteEnable = deviceExtensionsAvailable.Contains("VK_EXT_color_write_enable");
        bool hasDynamicState3 = deviceExtensionsAvailable.Contains("VK_EXT_extended_dynamic_state3");
        if (hasColorWriteEnable || hasDynamicState3)
        {
            colorWriteFeatures.PNext = hasDynamicState3 ? &dynamicState3Features : null;
            var query = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = hasColorWriteEnable ? &colorWriteFeatures : &dynamicState3Features,
            };
            Api.GetPhysicalDeviceFeatures2(PhysicalDevice, &query);
        }
        bool canEnable = hasColorWriteEnable && colorWriteFeatures.ColorWriteEnable;
        bool canMask = hasDynamicState3 && dynamicState3Features.ExtendedDynamicState3ColorWriteMask;
        bool canBlend = canMask && dynamicState3Features.ExtendedDynamicState3ColorBlendEnable
            && dynamicState3Features.ExtendedDynamicState3ColorBlendEquation;
        ColorWriteTier colorWriteTier = DeviceCaps.SelectColorWriteTier(canEnable, canMask,
            options.ColorWriteTier ?? DeviceCaps.FromEnvironment());

        // Re-request only what the tier uses; the queries may have reported more.
        colorWriteFeatures = new PhysicalDeviceColorWriteEnableFeaturesEXT
        {
            SType = StructureType.PhysicalDeviceColorWriteEnableFeaturesExt,
            PNext = wantDeviceFault ? &faultFeatures : null,
            ColorWriteEnable = true,
        };
        dynamicState3Features = new PhysicalDeviceExtendedDynamicState3FeaturesEXT
        {
            SType = StructureType.PhysicalDeviceExtendedDynamicState3FeaturesExt,
            PNext = wantDeviceFault ? &faultFeatures : null,
            ExtendedDynamicState3ColorWriteMask = true,
            ExtendedDynamicState3ColorBlendEnable = canBlend,
            ExtendedDynamicState3ColorBlendEquation = canBlend,
        };
        void* optionalFeatures = colorWriteTier switch
        {
            ColorWriteTier.DynamicEnable => &colorWriteFeatures,
            ColorWriteTier.DynamicMask => &dynamicState3Features,
            _ => wantDeviceFault ? &faultFeatures : null,
        };

        var vulkan13 = new PhysicalDeviceVulkan13Features
        {
            SType = StructureType.PhysicalDeviceVulkan13Features,
            PNext = optionalFeatures,
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
        if (wantCheckpoints) deviceExtensions.Add("VK_NV_device_diagnostic_checkpoints");
        if (wantDeviceFault) deviceExtensions.Add("VK_EXT_device_fault");

        // Optional tier: per-heap budgets from the driver; without it the
        // allocator budgets heap x 0.7. The env override forces the fallback.
        bool wantMemoryBudget = deviceExtensionsAvailable.Contains("VK_EXT_memory_budget")
            && Environment.GetEnvironmentVariable("OPTIMUM_VULKAN_NO_MEMORY_BUDGET") != "1";
        if (wantMemoryBudget) deviceExtensions.Add("VK_EXT_memory_budget");
        if (colorWriteTier == ColorWriteTier.DynamicEnable) deviceExtensions.Add("VK_EXT_color_write_enable");
        if (colorWriteTier == ColorWriteTier.DynamicMask) deviceExtensions.Add("VK_EXT_extended_dynamic_state3");

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
        LoadDiagnosticExtensions(wantCheckpoints, wantDeviceFault);
        Capabilities = ReadCapabilities();
        Capabilities.ColorWriteTier = colorWriteTier;
        Capabilities.ColorWriteEnable = colorWriteTier == ColorWriteTier.DynamicEnable;
        Capabilities.DynamicColorWriteMask = colorWriteTier == ColorWriteTier.DynamicMask;
        Capabilities.DynamicColorBlend = colorWriteTier == ColorWriteTier.DynamicMask && canBlend;
        if (Capabilities.ColorWriteEnable && Api.TryGetDeviceExtension(Instance, Device, out ExtColorWriteEnable writeEnable))
        {
            ColorWriteEnableApi = writeEnable;
        }
        if (Capabilities.DynamicColorWriteMask && Api.TryGetDeviceExtension(Instance, Device, out ExtExtendedDynamicState3 state3))
        {
            DynamicState3Api = state3;
        }
        MemoryBudgetAvailable = wantMemoryBudget;
        Allocator = new VulkanAllocator(this);
        return true;
    }

    private HashSet<string> EnumerateDeviceExtensions()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        uint count = 0;
        Result result = Api.EnumerateDeviceExtensionProperties(PhysicalDevice, (byte*)null, &count, null);
        if (result != Result.Success || count == 0) return names;

        var properties = new ExtensionProperties[count];
        fixed (ExtensionProperties* propertiesPtr = properties)
        {
            Api.EnumerateDeviceExtensionProperties(PhysicalDevice, (byte*)null, &count, propertiesPtr);

            // The name is a fixed-size buffer, readable only through a pointer.
            for (int i = 0; i < count; i++)
            {
                string? name = SilkMarshal.PtrToString((nint)propertiesPtr[i].ExtensionName);
                if (name != null) names.Add(name);
            }
        }
        return names;
    }

    private void LoadDiagnosticExtensions(bool checkpoints, bool deviceFault)
    {
        if (checkpoints)
        {
            nint set = (nint)Api.GetDeviceProcAddr(Device, "vkCmdSetCheckpointNV").Handle;
            nint get = (nint)Api.GetDeviceProcAddr(Device, "vkGetQueueCheckpointDataNV").Handle;
            if (set != 0 && get != 0)
            {
                _cmdSetCheckpoint = set;
                _getQueueCheckpointData = get;
                CheckpointsAvailable = true;
            }
        }

        if (deviceFault && Api.TryGetDeviceExtension(Instance, Device, out ExtDeviceFault fault))
        {
            _deviceFault = fault;
            DeviceFaultAvailable = true;
        }
    }

    /// <summary>Records a checkpoint marker into the command stream. No-op without the extension.</summary>
    public void CmdSetCheckpoint(CommandBuffer commandBuffer, nint marker)
    {
        if (_cmdSetCheckpoint == 0) return;
        ((delegate* unmanaged<CommandBuffer, void*, void>)_cmdSetCheckpoint)(commandBuffer, (void*)marker);
    }

    /// <summary>
    /// The last checkpoint each stage of the graphics queue reached. Meaningful
    /// after a device loss. The caller synchronises the queue.
    /// </summary>
    public List<(PipelineStageFlags Stage, nint Marker)> ReadQueueCheckpoints()
    {
        var checkpoints = new List<(PipelineStageFlags, nint)>();
        if (_getQueueCheckpointData == 0) return checkpoints;

        var get = (delegate* unmanaged<Queue, uint*, CheckpointDataNV*, void>)_getQueueCheckpointData;

        uint count = 0;
        get(GraphicsQueue, &count, null);
        if (count == 0) return checkpoints;

        var data = new CheckpointDataNV[count];
        for (int i = 0; i < data.Length; i++) data[i].SType = StructureType.CheckpointDataNV;
        fixed (CheckpointDataNV* dataPtr = data)
        {
            get(GraphicsQueue, &count, dataPtr);
        }

        for (int i = 0; i < count; i++)
        {
            checkpoints.Add((data[i].Stage, (nint)data[i].PCheckpointMarker));
        }
        return checkpoints;
    }

    /// <summary>The driver's own account of a device loss, or null without the extension.</summary>
    public string? ReadDeviceFault()
    {
        if (_deviceFault == null) return null;

        var counts = new DeviceFaultCountsEXT { SType = StructureType.DeviceFaultCountsExt };
        if (_deviceFault.GetDeviceFaultInfo(Device, &counts, null) != Result.Success) return null;

        var addresses = new DeviceFaultAddressInfoEXT[Math.Max(counts.AddressInfoCount, 1u)];
        var vendors = new DeviceFaultVendorInfoEXT[Math.Max(counts.VendorInfoCount, 1u)];
        var info = new DeviceFaultInfoEXT { SType = StructureType.DeviceFaultInfoExt };

        // The binary blob is vendor-private and can be large; it is not asked for.
        counts.VendorBinarySize = 0;

        var text = new System.Text.StringBuilder();

        fixed (DeviceFaultAddressInfoEXT* addressPtr = addresses)
        fixed (DeviceFaultVendorInfoEXT* vendorPtr = vendors)
        {
            info.PAddressInfos = counts.AddressInfoCount > 0 ? addressPtr : null;
            info.PVendorInfos = counts.VendorInfoCount > 0 ? vendorPtr : null;

            Result result = _deviceFault.GetDeviceFaultInfo(Device, &counts, &info);
            if (result != Result.Success && result != Result.Incomplete) return null;

            // The description strings are fixed-size buffers, readable only
            // through a pointer, so everything is formatted while still pinned.
            DeviceFaultInfoEXT* infoPtr = &info;
            string description = SilkMarshal.PtrToString((nint)infoPtr->Description) ?? "";
            text.Append("Driver fault report: '").Append(description.Trim()).Append('\'');

            for (int i = 0; i < counts.AddressInfoCount; i++)
            {
                text.Append("; ").Append(addressPtr[i].AddressType)
                    .Append(" at 0x").Append(addressPtr[i].ReportedAddress.ToString("x"))
                    .Append(" (precision ").Append(addressPtr[i].AddressPrecision).Append(')');
            }
            for (int i = 0; i < counts.VendorInfoCount; i++)
            {
                string vendor = SilkMarshal.PtrToString((nint)vendorPtr[i].Description) ?? "";
                text.Append("; vendor code ").Append(vendorPtr[i].VendorFaultCode)
                    .Append(" data ").Append(vendorPtr[i].VendorFaultData)
                    .Append(" '").Append(vendor.Trim()).Append('\'');
            }
        }
        return text.ToString();
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
            OcclusionQueryPrecise = features.OcclusionQueryPrecise,
            MaxSamplerLodBias = properties.Limits.MaxSamplerLodBias,
            MaxBoundDescriptorSets = (int)properties.Limits.MaxBoundDescriptorSets,
            MinUniformBufferOffsetAlignment = properties.Limits.MinUniformBufferOffsetAlignment,
            MaxUniformBufferRange = properties.Limits.MaxUniformBufferRange,
            MaxColorAttachments = properties.Limits.MaxColorAttachments,
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
            VulkanStats.WaitDeviceIdle(Api, Device);

            // Memory blocks are freed while the device still exists, and after
            // the wait, so nothing is executing against them.
            Allocator?.Dispose();
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
