using System;
using System.Collections.Generic;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Seam S1 for NGX: asks the instance and the device for exactly the extensions
/// NGX reports for the features it is asked about
/// (<c>NVSDK_NGX_VULKAN_GetFeatureInstance/DeviceExtensionRequirements</c>),
/// which is the supported way to build a device DLSS can then be initialised on.
///
/// Like every contributor it may only ask: a refused request is logged and the
/// feature simply stays unavailable, never a failed vkCreateDevice (rule 1).
/// The lists it collected are readable afterwards, which is what the spike test
/// reports.
/// </summary>
internal sealed class NgxDeviceRequirements : IDeviceRequirementContributor
{
    private readonly NgxSession _session;
    private readonly IReadOnlyList<NgxFeature> _features;
    private readonly Action<string>? _log;

    public NgxDeviceRequirements(
        NgxSession session, IReadOnlyList<NgxFeature> features, Action<string>? log = null)
    {
        _session = session;
        _features = features;
        _log = log;
    }

    public string Name => "NGX";

    /// <summary>Per feature: the result of the query and the extensions it named.</summary>
    public Dictionary<NgxFeature, (NgxResult Result, List<string> Extensions)> RequestedInstanceExtensions { get; }
        = new();

    /// <summary>Per feature: the result of the query and the extensions it named.</summary>
    public Dictionary<NgxFeature, (NgxResult Result, List<string> Extensions)> RequestedDeviceExtensions { get; }
        = new();

    /// <summary>Extensions NGX named that this instance or device does not have.</summary>
    public List<string> Refused { get; } = new();

    public void ContributeInstanceExtensions(InstanceRequirements requirements)
    {
        foreach (NgxFeature feature in _features)
        {
            NgxResult result = _session.InstanceExtensions(feature, out List<string> extensions);
            if (result == NgxResult.FailNotImplemented)
            {
                // What the SDK's own wrapper does with this answer, which is the
                // answer driver 615.71.09 gives on Linux.
                extensions = new List<string>(NgxSession.InstanceExtensionFallback);
                _log?.Invoke("NGX instance extension query for " + feature +
                    " is not implemented by this driver; using the SDK's fallback list");
            }
            else if (!NgxInterop.Succeeded(result))
            {
                RequestedInstanceExtensions[feature] = (result, extensions);
                _log?.Invoke("NGX instance extension query for " + feature + " returned " +
                    NgxInterop.Describe(result));
                continue;
            }
            RequestedInstanceExtensions[feature] = (result, extensions);
            foreach (string extension in extensions)
            {
                if (!requirements.Request(extension, Name)) Refused.Add("instance:" + extension);
            }
        }
    }

    public void ContributeDeviceRequirements(DeviceRequirements requirements)
    {
        IntPtr instance = (IntPtr)requirements.Instance.Handle;
        IntPtr physicalDevice = (IntPtr)requirements.PhysicalDevice.Handle;

        foreach (NgxFeature feature in _features)
        {
            NgxResult result = _session.DeviceExtensions(
                instance, physicalDevice, feature, out List<string> extensions);
            if (result == NgxResult.FailNotImplemented)
            {
                extensions = new List<string>(NgxSession.DeviceExtensionFallback);
                _log?.Invoke("NGX device extension query for " + feature +
                    " is not implemented by this driver; using the SDK's fallback list");
            }
            else if (!NgxInterop.Succeeded(result))
            {
                RequestedDeviceExtensions[feature] = (result, extensions);
                _log?.Invoke("NGX device extension query for " + feature + " returned " +
                    NgxInterop.Describe(result));
                continue;
            }
            RequestedDeviceExtensions[feature] = (result, extensions);
            foreach (string extension in extensions)
            {
                if (!requirements.Request(extension, 0, Name)) Refused.Add("device:" + extension);
            }
        }

        // The extension list NGX names is not the whole requirement: DLSS calls
        // vkGetBufferDeviceAddress on buffers it allocates on our device, and
        // VK_KHR_buffer_device_address without the bufferDeviceAddress feature
        // is a validation error on every evaluate. NGX's own queries never
        // mention features, so this is ours to ask for.
        BufferDeviceAddress = requirements.RequestBufferDeviceAddress(Name);
        if (!BufferDeviceAddress) Refused.Add("feature:bufferDeviceAddress");
    }

    /// <summary>Whether the device will have the <c>bufferDeviceAddress</c> feature DLSS needs.</summary>
    public bool BufferDeviceAddress { get; private set; }
}
