using System;
using System.Collections.Generic;
using System.IO;
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Can Optimum drive NGX (DLSS Super Resolution and DLSS Frame Generation)
/// straight from C# on native Linux Vulkan? (DLSS spike, 2026-09-12.)
///
/// The answer, since the shim landed on 2026-09-12: yes, through
/// <c>native/optimum-ngx</c>.
/// The NVIDIA Linux driver's own <c>libnvidia-ngx.so.1</c> exports the whole
/// Vulkan NGX API and answers it on native X11 - on this RTX 4070 Laptop with
/// driver 615.71.09, <c>Init_ProjectID</c> with our own project GUID and engine
/// type CUSTOM returns Success, and the capability parameters report
/// SuperSampling.Available = 1 and FrameGeneration.Available = 1. But NGX
/// resolves its caller's module from its own return address, and a .NET P/Invoke
/// stub is JIT-compiled into anonymous memory, so every direct managed call
/// aborts the process inside the driver (see
/// <see cref="NgxInterop.ManagedCallSiteIsSupported" /> for the evidence and the
/// exact failure). So every call goes managed -> <see cref="NgxShim" /> ->
/// libOptimumNgx.so -> NGX, and the tests below bring NGX up for real on a
/// headless <see cref="VulkanDevice" /> built through seam S1 and read back the
/// same numbers <c>scripts/dev/ngx-probe.c</c> got from a C executable.
///
/// Everything skips, never fails, when the driver library or the NGX feature
/// libraries are missing. The feature libraries are NVIDIA redistributables and
/// are not in this repository: point <c>OPTIMUM_NGX_FEATURE_PATH</c> at a
/// directory holding <c>libnvidia-ngx-dlss.so.*</c> and
/// <c>libnvidia-ngx-dlssg.so.*</c> (DLSS SDK 310.9.1's
/// <c>lib/Linux_x86_64/rel</c>), and build the shim with <c>make native</c>.
/// </summary>
[Collection(NgxCollection.Name)]
public class NgxAvailabilityTests
{
    private readonly ITestOutputHelper _output;
    private readonly NgxRuntime _ngx;

    public NgxAvailabilityTests(ITestOutputHelper output, NgxRuntime ngx)
    {
        _output = output;
        _ngx = ngx;
    }

    /// <summary>
    /// The struct sizes the NGX headers imply on the x86-64 SysV ABI. Every one
    /// of these is passed by pointer into the driver, so a wrong offset is not a
    /// wrong answer but an abort inside libnvidia-ngx with no managed frame to
    /// read. Verified against the same layouts compiled from the headers by gcc.
    /// </summary>
    [Fact]
    public unsafe void TheInteropStructsHaveTheLayoutTheHeadersDefine()
    {
        Assert.Equal(16, sizeof(NgxPathListInfo));
        Assert.Equal(16, sizeof(NgxLoggingInfo));
        Assert.Equal(40, sizeof(NgxFeatureCommonInfo));
        Assert.Equal(24, sizeof(NgxProjectIdDescription));
        Assert.Equal(32, sizeof(NgxApplicationIdentifier));
        Assert.Equal(56, sizeof(NgxFeatureDiscoveryInfo));
        Assert.Equal(264, sizeof(NgxFeatureRequirement));
        Assert.Equal(260, sizeof(NgxExtensionProperties));
    }

    /// <summary>
    /// The result codes the spike quotes have to mean what the header says they
    /// mean, including the one the driver actually returns for the per-feature
    /// extension queries.
    /// </summary>
    [Fact]
    public void ResultCodesMatchTheHeader()
    {
        Assert.Equal(0x1u, (uint)NgxResult.Success);
        Assert.Equal(0xBAD00012u, (uint)NgxResult.FailNotImplemented);
        Assert.Equal(0xBAD0000Cu, (uint)NgxResult.FailOutOfDate);
        Assert.True(NgxInterop.Succeeded(NgxResult.Success));
        Assert.False(NgxInterop.Succeeded(NgxResult.FailNotImplemented));
        Assert.Contains("NotImplemented", NgxInterop.Describe(NgxResult.FailNotImplemented),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The environment the spike measured: the driver library, the feature
    /// libraries, and whether managed calls are possible at all. Reported rather
    /// than asserted, apart from the facts a DLSS backend depends on.
    /// </summary>
    [SkippableFact]
    public void TheNgxRuntimeIsPresentAndSaysWhetherManagedCallsArePossible()
    {
        using NgxSession session = RequireSession();

        Log("driver library " + NgxInterop.LibraryName + ": present");
        Log("NGX SDK API version: 0x" + NgxInterop.VersionApi.ToString("X7"));
        Log("feature library paths: " + string.Join(", ", session.FeaturePaths));
        foreach (string path in session.FeaturePaths)
        {
            foreach (string file in Directory.GetFiles(path, "libnvidia-ngx-*.so*"))
            {
                Log("  " + Path.GetFileName(file));
            }
        }
        Log("managed call site supported: " + NgxInterop.ManagedCallSiteIsSupported);
        Log("  " + NgxInterop.ManagedCallSiteDiagnosis);

        // The queries degrade rather than abort, which is the contract the rest
        // of the backend is written against.
        foreach (NgxFeature feature in Features)
        {
            NgxResult result = session.InstanceExtensions(feature, out List<string> extensions);
            Log(feature + " instance extension query: " + NgxInterop.Describe(result) +
                " -> [" + string.Join(", ", extensions) + "]");
            Assert.False(NgxInterop.Succeeded(result) && extensions.Count == 0,
                feature + " reported success with no extensions, which cannot be right");
        }

        Assert.NotEmpty(session.FeaturePaths);
        Assert.NotEmpty(NgxSession.DeviceExtensionFallback);
    }

    /// <summary>
    /// Deliverable (b): a real headless device whose instance and device carry
    /// everything NGX requires, built through the ordinary contributor seam with
    /// no change to <see cref="VulkanDevice" />.
    ///
    /// On driver 615.71.09 the per-feature queries answer FAIL_NotImplemented
    /// even from a native call site, and the SDK's own wrapper substitutes fixed
    /// lists for exactly that answer; those lists are what
    /// <see cref="NgxDeviceRequirements" /> asks for, and what this test checks
    /// really reached VkInstanceCreateInfo and VkDeviceCreateInfo.
    /// </summary>
    [SkippableFact]
    public void ADeviceBuiltThroughSeamS1CarriesEveryExtensionNgxRequires()
    {
        using NgxSession session = RequireSession();

        var requirements = new NgxDeviceRequirements(session, Features, Log);
        VulkanDevice device = GpuTest.NewDevice();
        Action<VulkanContextOptions>? configured = device.ConfigureContextOptions;
        device.ConfigureContextOptions = options =>
        {
            configured?.Invoke(options);
            options.RequirementContributors.Add(requirements);
        };

        using (device)
        {
            Skip.IfNot(device.Initialize(IntPtr.Zero, 0, 0, out string failureReason),
                "No usable Vulkan device: " + failureReason);

            VulkanContext context = device.ContextForTests;
            Log("device: " + context.Capabilities.DeviceName + " / " + context.Capabilities.DriverName);

            foreach (NgxFeature feature in Features)
            {
                Report(feature, "instance extensions", requirements.RequestedInstanceExtensions);
                Report(feature, "device extensions", requirements.RequestedDeviceExtensions);
            }
            Log("instance extensions enabled: " + string.Join(", ", context.EnabledInstanceExtensions));
            Log("device extensions enabled: " + string.Join(", ", context.EnabledDeviceExtensions));
            Log("extensions NGX named that this system refused: " +
                (requirements.Refused.Count == 0 ? "none" : string.Join(", ", requirements.Refused)));

            foreach (string extension in NgxSession.InstanceExtensionFallback)
            {
                Assert.Contains(extension, context.EnabledInstanceExtensions);
            }
            foreach (string extension in NgxSession.DeviceExtensionFallback)
            {
                Assert.Contains(extension, context.EnabledDeviceExtensions);
            }
            Assert.Empty(requirements.Refused);

            GpuTest.AssertClean(device);
        }
    }

    /// <summary>
    /// The shim has to be the build's own: its <c>OPTIMUM_NGX_SHIM_VERSION</c>
    /// must match what this managed code is compiled against, or a stale
    /// libOptimumNgx.so left beside the renderer from an older build would be
    /// called with signatures it no longer has. A mismatch has to make NGX
    /// unavailable, not crash - so the version check is the gate, not a hint.
    /// </summary>
    [SkippableFact]
    public void TheShimIsTheBuildsOwnAndReportsItsAbiVersion()
    {
        Skip.IfNot(NgxShim.Version != 0,
            "The NGX shim could not be loaded: " + NgxShim.Diagnosis +
            " (build it with `make native`).");

        Log("shim: " + NgxShim.Diagnosis);
        Log("shim runtime name: " + NgxShim.RuntimeName);
        Assert.Equal(NgxShim.ExpectedVersion, NgxShim.Version);
        Assert.True(NgxShim.IsAvailable);
        Assert.True(NgxInterop.ManagedCallSiteIsSupported,
            "with a loadable shim of the right version, NGX is reachable from managed code");
        Assert.Equal(NgxInterop.LibraryName, NgxShim.RuntimeName);
    }

    /// <summary>
    /// The deliverable: managed code brings NGX up through the shim on a live
    /// headless device and reads back exactly what the native probe read
    /// (RTX 4070 Laptop, driver 615.71.09, DLSS SDK 310.9.1) - Init Success,
    /// SuperSampling.Available = 1, FrameGeneration.Available = 1, and the DLSS
    /// optimal settings at 2560x1490 (Quality 1707x993, Performance 1280x745).
    ///
    /// Those numbers are asserted, not just reported: they are the proof that
    /// the shim forwards arguments and structs correctly rather than merely
    /// returning Success. The feature libraries decide them, so the test skips
    /// when they are absent and never fails for their absence.
    ///
    /// It then shuts NGX down and checks the device is still validation-clean,
    /// because NGX creates its own Vulkan objects on our device and a leak or a
    /// hazard it left behind would be ours to carry.
    /// </summary>
    [SkippableFact]
    public void NgxComesUpFromManagedCodeThroughTheShimAndReportsWhatTheNativeProbeSaw()
    {
        _ngx.Require();
        foreach (string line in _ngx.Diagnostics) Log(line);
        int mark = _ngx.MessageMark();

        Log("shim: " + NgxShim.Diagnosis);
        Log("call site: " + NgxInterop.ManagedCallSiteDiagnosis);

        VulkanDevice device = _ngx.Device;
        NgxSession session = _ngx.Session;

        // Pre-init discovery, which aborted the process before the shim.
        foreach (NgxFeature feature in Features)
        {
            NgxResult supportResult = session.FeatureRequirements(
                _ngx.Instance, _ngx.PhysicalDevice, feature,
                out NgxFeatureSupport supported, out uint minArch, out string minOs);
            Log(feature + " requirements: " + NgxInterop.Describe(supportResult) +
                " FeatureSupported=" + supported + " MinHwArchitecture=0x" + minArch.ToString("X") +
                " MinOsVersion='" + minOs + "'");
            Assert.Equal(NgxResult.Success, supportResult);
            Assert.Equal(NgxFeatureSupport.Supported, supported);
        }

        // NGX came up on the shared runtime; Shutdown1 is its business too,
        // because the driver allows exactly one lifetime per process (see
        // NgxRuntime), and its Dispose fails the run if that shutdown does not
        // return Success.
        Log("NVSDK_NGX_VULKAN_Init_ProjectID: " + NgxInterop.Describe(_ngx.InitResult));
        Assert.Equal(NgxResult.Success, _ngx.InitResult);

        NgxResult capabilities = NgxInterop.GetCapabilityParameters(out IntPtr handle);
        Log("NVSDK_NGX_VULKAN_GetCapabilityParameters: " + NgxInterop.Describe(capabilities));
        Assert.Equal(NgxResult.Success, capabilities);
        Assert.NotEqual(IntPtr.Zero, handle);

        var parameters = new NgxParameters(handle);
        try
        {
            uint superSampling = ReportUInt(parameters, NgxParameterNames.SuperSamplingAvailable);
            ReportUInt(parameters, NgxParameterNames.SuperSamplingNeedsUpdatedDriver);
            ReportUInt(parameters, NgxParameterNames.SuperSamplingMinDriverVersionMajor);
            ReportUInt(parameters, NgxParameterNames.SuperSamplingMinDriverVersionMinor);
            ReportInt(parameters, NgxParameterNames.SuperSamplingFeatureInitResult);

            uint frameGeneration = ReportUInt(parameters, NgxParameterNames.FrameGenerationAvailable);
            ReportUInt(parameters, NgxParameterNames.FrameGenerationNeedsUpdatedDriver);
            ReportUInt(parameters, NgxParameterNames.FrameGenerationMinDriverVersionMajor);
            ReportUInt(parameters, NgxParameterNames.FrameGenerationMinDriverVersionMinor);
            ReportInt(parameters, NgxParameterNames.FrameGenerationFeatureInitResult);

            Assert.Equal(1u, superSampling);
            Assert.Equal(1u, frameGeneration);

            // The optimal-settings callback lives inside libnvidia-ngx too, so
            // the shim owns that call as well.
            NgxResult quality = NgxSession.OptimalSettings(
                parameters, 2560, 1490, NgxPerfQuality.MaxQuality, out NgxOptimalSettings qualitySettings);
            Log("optimal settings 2560x1490 Quality: " + NgxInterop.Describe(quality) +
                " -> " + qualitySettings);
            Assert.Equal(NgxResult.Success, quality);
            Assert.Equal(1707u, qualitySettings.OptimalWidth);
            Assert.Equal(993u, qualitySettings.OptimalHeight);

            NgxResult performance = NgxSession.OptimalSettings(
                parameters, 2560, 1490, NgxPerfQuality.MaxPerf, out NgxOptimalSettings perfSettings);
            Log("optimal settings 2560x1490 Performance: " + NgxInterop.Describe(performance) +
                " -> " + perfSettings);
            Assert.Equal(NgxResult.Success, performance);
            Assert.Equal(1280u, perfSettings.OptimalWidth);
            Assert.Equal(745u, perfSettings.OptimalHeight);
        }
        finally
        {
            NgxResult destroyed = NgxInterop.DestroyParameters(parameters.Handle);
            Log("NVSDK_NGX_VULKAN_DestroyParameters: " + NgxInterop.Describe(destroyed));
        }

        GpuTest.AssertCleanSince(device, mark);
    }

    // ------------------------------------------------------------------ helpers

    private static readonly NgxFeature[] Features =
        { NgxFeature.SuperSampling, NgxFeature.FrameGeneration };

    /// <summary>
    /// Both to the test output and to stderr. xunit only flushes its buffer when
    /// a test ends, and a direct NGX call takes the whole test host down with an
    /// uncaught C++ exception, which would otherwise lose exactly the line that
    /// says where it died - the way this spike's central finding was located.
    /// </summary>
    private void Log(string line)
    {
        _output.WriteLine(line);
        Console.Error.WriteLine("[ngx] " + line);
        Console.Error.Flush();
    }

    private uint ReportUInt(NgxParameters parameters, string name)
    {
        NgxResult result = parameters.GetUInt(name, out uint value);
        Log("  " + name.PadRight(44) + (NgxInterop.Succeeded(result)
            ? " = " + value
            : " : " + NgxInterop.Describe(result)));
        return NgxInterop.Succeeded(result) ? value : 0;
    }

    private void ReportInt(NgxParameters parameters, string name)
    {
        NgxResult result = parameters.GetInt(name, out int value);
        Log("  " + name.PadRight(44) + (NgxInterop.Succeeded(result)
            ? " = " + value + " (0x" + ((uint)value).ToString("X8") + ")"
            : " : " + NgxInterop.Describe(result)));
    }

    private void Report(
        NgxFeature feature, string what,
        Dictionary<NgxFeature, (NgxResult Result, List<string> Extensions)> queried)
    {
        if (!queried.TryGetValue(feature, out (NgxResult Result, List<string> Extensions) entry))
        {
            Log(feature + " " + what + ": not queried");
            return;
        }
        Log(feature + " " + what + ": " + NgxInterop.Describe(entry.Result) +
            " -> [" + string.Join(", ", entry.Extensions) + "]");
    }

    /// <summary>Skips unless both the driver library and the feature libraries are here.</summary>
    private NgxSession RequireSession()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The NGX spike is written against the Linux driver library.");
        Skip.IfNot(NgxInterop.IsDriverLibraryPresent(),
            "The NVIDIA driver library " + NgxInterop.LibraryName + " is not installed.");

        IReadOnlyList<string> paths = NgxSession.FindFeaturePaths();
        Skip.If(paths.Count == 0,
            "No NGX feature libraries found; set " + NgxSession.FeaturePathVariable +
            " to a directory holding libnvidia-ngx-dlss.so.* and libnvidia-ngx-dlssg.so.*.");

        string dataPath = Path.Combine(
            Path.GetTempPath(), "optimum-ngx-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dataPath);
        return new NgxSession("1.0.0", dataPath, paths);
    }
}
