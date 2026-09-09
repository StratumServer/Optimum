using System;
using System.Runtime.CompilerServices;
using Xunit;

// These tests drive a real GPU driver, and three of them additionally drive
// GLFW's process-global init and terminate. Neither is safe to do from several
// threads at once: xunit's default of running collections in parallel crashed
// the test host outright once the windowing tests joined the suite.
//
// The suite is a few seconds either way, so serialising it costs nothing worth
// having.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Optimum.Render.Vulkan.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Keeps implicit Vulkan layers out of the test process.
    ///
    /// Overlays a developer happens to have installed - MangoHud, gamescope's
    /// WSI layer, vendor layers - are loaded into every Vulkan instance on the
    /// machine, and they can produce validation errors of their own. The
    /// gamescope layer on this machine enables VK_KHR_present_mode_fifo_latest_ready
    /// without the VK_KHR_swapchain it depends on, which fails the validation
    /// assertions in twenty-five tests for a reason that has nothing to do with
    /// this backend. Excluding them makes the suite depend only on our own calls.
    ///
    /// Set before any Vulkan call, because the loader reads it when an instance
    /// is created.
    /// </summary>
    [ModuleInitializer]
    internal static void DisableImplicitLayers()
    {
        if (Environment.GetEnvironmentVariable("VK_LOADER_LAYERS_DISABLE") != null) return;

        Environment.SetEnvironmentVariable("VK_LOADER_LAYERS_DISABLE", "~implicit~");

        // .NET's SetEnvironmentVariable only updates the managed copy on Unix;
        // the Vulkan loader is native and reads the real environment, so it has
        // to be set through libc as well or nothing changes.
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            try
            {
                SetNativeEnvironmentVariable("VK_LOADER_LAYERS_DISABLE", "~implicit~", 1);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "setenv")]
    private static extern int SetNativeEnvironmentVariable(string name, string value, int overwrite);
}
