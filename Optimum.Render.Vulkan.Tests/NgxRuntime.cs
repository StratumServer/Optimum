using System;
using System.Collections.Generic;
using System.IO;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Core;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The one NGX bring-up in this test process: one headless
/// <see cref="VulkanDevice" /> built through seam S1 with everything NGX
/// requires, one <c>NVSDK_NGX_VULKAN_Init_ProjectID</c>, one
/// <c>NVSDK_NGX_VULKAN_Shutdown1</c>.
///
/// It is shared rather than per test because NGX only supports one lifetime per
/// process on this driver, and the failure is fatal rather than an error code:
/// the <i>second</i> <c>Shutdown1</c> in a process segfaults inside
/// <c>libnvidia-ngx.so.1</c> (measured 2026-09-12, driver 615.71.09; core dump
/// stack <c>libnvidia-ngx+0xa1898</c> under
/// <c>OptimumNgx_VulkanShutdown</c>), whether or not each init was paired with
/// its own device and its own features. So every NGX test shares this one and
/// nothing else calls Init or Shutdown.
///
/// The runtime never fails a test by existing: when the shim, the driver library
/// or the NGX feature libraries are missing it simply comes up unavailable and
/// <see cref="Require" /> skips.
/// </summary>
public sealed class NgxRuntime : IDisposable
{
    private readonly List<string> _log = new();
    private NgxSession? _session;
    private VulkanDevice? _device;
    private bool _disposed;

    public NgxRuntime()
    {
        Unavailable = Diagnose();
        if (Unavailable != null) return;

        IReadOnlyList<string> paths = NgxSession.FindFeaturePaths();
        string dataPath = Path.Combine(
            Path.GetTempPath(), "optimum-ngx-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(dataPath);
        _session = new NgxSession("1.0.0", dataPath, paths);
        Log("feature library paths: " + string.Join(", ", paths));

        Requirements = new NgxDeviceRequirements(
            _session, new[] { NgxFeature.SuperSampling, NgxFeature.FrameGeneration }, Log);

        VulkanDevice device = GpuTest.NewDevice();
        Action<VulkanContextOptions>? configured = device.ConfigureContextOptions;
        device.ConfigureContextOptions = options =>
        {
            configured?.Invoke(options);
            options.RequirementContributors.Add(Requirements);
        };

        if (!device.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            device.Dispose();
            Unavailable = "No usable Vulkan device: " + failureReason;
            return;
        }
        _device = device;

        VulkanContext context = device.ContextForTests;
        Log("device: " + context.Capabilities.DeviceName + " / " + context.Capabilities.DriverName);
        if (!context.Capabilities.DeviceName.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            Unavailable = "NGX needs the NVIDIA driver; this device is " + context.Capabilities.DeviceName + ".";
            return;
        }

        Instance = (IntPtr)context.Instance.Handle;
        PhysicalDevice = (IntPtr)context.PhysicalDevice.Handle;
        VkDevice = (IntPtr)context.Device.Handle;

        InitResult = _session.Initialize(Instance, PhysicalDevice, VkDevice);
        Log("NVSDK_NGX_VULKAN_Init_ProjectID: " + NgxInterop.Describe(InitResult));
        if (InitResult != NgxResult.Success)
        {
            Unavailable = "NVSDK_NGX_VULKAN_Init_ProjectID: " + NgxInterop.Describe(InitResult);
            return;
        }
        Initialized = true;
    }

    /// <summary>Why NGX is not usable here, or null when it is.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>Whether NGX came up on <see cref="Device" />.</summary>
    public bool Initialized { get; }

    /// <summary>What Init answered, for the tests that report it.</summary>
    internal NgxResult InitResult { get; }

    /// <summary>What Shutdown answered, once <see cref="Dispose" /> has run.</summary>
    internal NgxResult ShutdownResult { get; private set; }

    internal NgxSession Session => _session ?? throw new InvalidOperationException(Unavailable ?? "no session");

    public VulkanDevice Device => _device ?? throw new InvalidOperationException(Unavailable ?? "no device");

    /// <summary>The S1 contributor that built the device, for the extension report.</summary>
    internal NgxDeviceRequirements? Requirements { get; }

    public IntPtr Instance { get; }
    public IntPtr PhysicalDevice { get; }
    public IntPtr VkDevice { get; }

    /// <summary>Lines the bring-up produced, for a test to echo into its own output.</summary>
    public IReadOnlyList<string> Diagnostics => _log;

    /// <summary>Skips the calling test unless NGX is up on this runtime's device.</summary>
    public void Require()
    {
        Skip.If(!Initialized, Unavailable ?? "NGX did not come up.");
    }

    /// <summary>
    /// How many layer messages the shared device has recorded so far. A test
    /// takes one at its start and asserts only on what follows, because the
    /// device outlives it and someone else's messages are not its to judge.
    /// </summary>
    public int MessageMark() => _device == null ? 0 : GpuTest.MessagesOf(_device).Count;

    private void Log(string line) => _log.Add(line);

    /// <summary>Everything the environment has to provide before NGX can be brought up at all.</summary>
    private static string? Diagnose()
    {
        if (!OperatingSystem.IsLinux()) return "NGX is reached through the Linux driver library here.";
        if (!NgxInterop.IsDriverLibraryPresent())
        {
            return "The NVIDIA driver library " + NgxInterop.LibraryName + " is not installed.";
        }
        if (!NgxShim.IsAvailable)
        {
            return "The NGX shim is not loadable: " + NgxShim.Diagnosis + " (build it with `make native`).";
        }

        NgxResult runtime = NgxShim.LoadRuntime();
        if (!NgxInterop.Succeeded(runtime))
        {
            return "The shim could not load the NGX runtime: " + NgxInterop.Describe(runtime) + " " +
                NgxShim.LastLoadError;
        }
        if (NgxSession.FindFeaturePaths().Count == 0)
        {
            return "No NGX feature libraries found; set " + NgxSession.FeaturePathVariable +
                " to a directory holding libnvidia-ngx-dlss.so.* and libnvidia-ngx-dlssg.so.*.";
        }
        return null;
    }

    /// <summary>
    /// The process's one shutdown, in the only order that is safe: everything the
    /// frame timeline still holds is destroyed first (a DLSS feature released
    /// after <c>Shutdown1</c> takes the process down), then NGX goes, then the
    /// device NGX was initialised on.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_device != null)
        {
            _device.DrainDeferredDeletions();
            if (Initialized)
            {
                ShutdownResult = NgxSession.Shutdown(VkDevice);
                Log("NVSDK_NGX_VULKAN_Shutdown1: " + NgxInterop.Describe(ShutdownResult));
            }
            _device.Dispose();
        }
        _session?.Dispose();

        if (Initialized && ShutdownResult != NgxResult.Success)
        {
            throw new InvalidOperationException(
                "NVSDK_NGX_VULKAN_Shutdown1 did not succeed: " + NgxInterop.Describe(ShutdownResult));
        }
    }
}

/// <summary>
/// Every test that brings NGX up belongs here, so they share the one
/// <see cref="NgxRuntime" /> the driver allows per process.
/// </summary>
[CollectionDefinition(Name)]
public sealed class NgxCollection : ICollectionFixture<NgxRuntime>
{
    public const string Name = "NGX runtime (one per process)";
}
