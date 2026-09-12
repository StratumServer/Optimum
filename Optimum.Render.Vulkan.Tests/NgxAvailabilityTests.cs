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
/// The answer this suite pins down is "the API surface yes, the call site no".
/// The NVIDIA Linux driver's own <c>libnvidia-ngx.so.1</c> exports the whole
/// Vulkan NGX API and answers it on native X11 - on this RTX 4070 Laptop with
/// driver 615.71.09, <c>Init_ProjectID</c> with our own project GUID and engine
/// type CUSTOM returns Success, and the capability parameters report
/// SuperSampling.Available = 1 and FrameGeneration.Available = 1. But NGX
/// resolves its caller's module from its own return address, and a .NET P/Invoke
/// stub is JIT-compiled into anonymous memory, so every direct managed call
/// aborts the process inside the driver (see
/// <see cref="NgxInterop.ManagedCallSiteIsSupported" /> for the evidence and the
/// exact failure). The interop below is therefore written and layout-checked,
/// but it degrades instead of calling until Optimum has a call site inside a
/// real shared object.
///
/// What still runs for real here is the part that decides whether the rest can
/// ever work: a headless <see cref="VulkanDevice" /> built through seam S1 with
/// the extensions NGX requires, which is deliverable (b) of the spike minus the
/// calls the driver refuses to take from managed code.
///
/// Everything skips, never fails, when the driver library or the NGX feature
/// libraries are missing. The feature libraries are NVIDIA redistributables and
/// are not in this repository: point <c>OPTIMUM_NGX_FEATURE_PATH</c> at a
/// directory holding <c>libnvidia-ngx-dlss.so.*</c> and
/// <c>libnvidia-ngx-dlssg.so.*</c>.
/// </summary>
public class NgxAvailabilityTests
{
    private readonly ITestOutputHelper _output;

    public NgxAvailabilityTests(ITestOutputHelper output) => _output = output;

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
