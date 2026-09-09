using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Desktop;

namespace Optimum.InteropProbe;

/// <summary>
/// Reports which OpenGL-to-D3D / OpenGL-to-Vulkan texture sharing paths the
/// current driver actually exposes.
///
/// Vintage Story renders in OpenGL, but every upscaler worth having (XeSS,
/// DLSS, FSR2+) and all frame generation run in D3D or Vulkan. Bridging that
/// gap needs the driver to let us share a texture across APIs. Which of the
/// candidate routes exist is a per-driver question with no reliable answer
/// short of asking the driver, and Intel's OpenGL driver is the one most
/// likely to come up short - so ask it directly rather than trusting forum
/// archaeology.
///
/// Extension strings are necessary but not sufficient: drivers do advertise
/// entry points that then fail in use. A missing extension is decisive, a
/// present one warrants a real allocate-and-import test afterwards.
/// </summary>
internal static class Program
{
    private static int Main()
    {
        Console.WriteLine("Optimum OpenGL interop probe");
        Console.WriteLine("============================");
        Console.WriteLine();

        NativeWindow window;
        try
        {
            window = CreateHiddenContext();
        }
        catch (Exception error)
        {
            Console.WriteLine("Could not create an OpenGL context: " + error.Message);
            return 1;
        }

        using (window)
        {
            window.Context.MakeCurrent();

            Console.WriteLine("Vendor   : " + GL.GetString(StringName.Vendor));
            Console.WriteLine("Renderer : " + GL.GetString(StringName.Renderer));
            Console.WriteLine("Version  : " + GL.GetString(StringName.Version));
            Console.WriteLine("OS       : " + RuntimeInformation.OSDescription);
            Console.WriteLine();

            HashSet<string> glExtensions = ReadGlExtensions();
            HashSet<string> wglExtensions = ReadWglExtensions();
            Console.WriteLine($"Reported {glExtensions.Count} GL extensions"
                + (OperatingSystem.IsWindows() ? $", {wglExtensions.Count} WGL extensions" : string.Empty));
            Console.WriteLine();

            ReportDirect3DRoute(glExtensions, wglExtensions);
            ReportVulkanRoute(glExtensions);
        }

        return 0;
    }

    private static NativeWindow CreateHiddenContext()
    {
        // A 3.3 core context matches what the client itself asks for, so the
        // driver hands back the same extension set the real renderer sees.
        return new NativeWindow(new NativeWindowSettings
        {
            ClientSize = new Vector2i(64, 64),
            StartVisible = false,
            Title = "Optimum interop probe",
            APIVersion = new Version(3, 3),
            Profile = OpenTK.Windowing.Common.ContextProfile.Core,
        });
    }

    /// <summary>
    /// The D3D route, and the one the Skyrim Community Shaders architecture
    /// plugs into: share the scene texture with a D3D11 device, hand it to
    /// XeSS (libxess_dx11 on Intel adapters), and let a D3D12 proxy swapchain
    /// own presentation for frame generation.
    /// </summary>
    private static void ReportDirect3DRoute(HashSet<string> glExtensions, HashSet<string> wglExtensions)
    {
        Console.WriteLine("Route A - OpenGL to Direct3D");
        Console.WriteLine("----------------------------");

        if (!OperatingSystem.IsWindows())
        {
            Console.WriteLine("  n/a   Windows only; rerun this on the target device.");
            Console.WriteLine();
            return;
        }

        // The extension string and the entry point can disagree. Report both:
        // an exported wglDXOpenDeviceNV with no advertised string still means
        // the code path exists to be tested.
        bool interop = wglExtensions.Contains("WGL_NV_DX_interop");
        bool interop2 = wglExtensions.Contains("WGL_NV_DX_interop2");
        Report("WGL_NV_DX_interop", interop, "D3D9 sharing");
        Report("WGL_NV_DX_interop2", interop2, "D3D10/11 sharing - the one that matters");

        foreach (string name in new[]
        {
            "wglDXOpenDeviceNV",
            "wglDXRegisterObjectNV",
            "wglDXLockObjectsNV",
            "wglDXUnlockObjectsNV",
        })
        {
            Report(name + " (entry point)", WglGetProcAddress(name) != IntPtr.Zero, null);
        }

        // D3D12 resources can also be imported straight into GL, skipping the
        // D3D11 hop entirely. Cleaner when present, but the less-implemented
        // of the two routes.
        Report("GL_EXT_memory_object_win32", glExtensions.Contains("GL_EXT_memory_object_win32"),
            "direct D3D11/D3D12 resource import");
        Report("GL_EXT_semaphore_win32", glExtensions.Contains("GL_EXT_semaphore_win32"),
            "D3D12 fence import");

        Console.WriteLine();
        if (interop2)
        {
            Console.WriteLine("  => Viable. Share via WGL_NV_DX_interop2 into D3D11, then reuse the");
            Console.WriteLine("     existing D3D11/D3D12 proxy design unchanged.");
        }
        else if (glExtensions.Contains("GL_EXT_memory_object_win32"))
        {
            Console.WriteLine("  => Viable, but only via direct D3D12 resource import. No D3D11 hop,");
            Console.WriteLine("     so the sharing layer differs from the Skyrim one.");
        }
        else
        {
            Console.WriteLine("  => Blocked. This driver exposes no OpenGL-to-D3D sharing path;");
            Console.WriteLine("     reaching XeSS would mean a real renderer backend, not a bridge.");
        }

        Console.WriteLine();
    }

    /// <summary>
    /// The Vulkan route: share into a Vulkan device and run XeSS-SR, DLSS-SR
    /// or FSR2 there. Super resolution only - frame generation needs to own
    /// presentation, and XeSS-FG is D3D12-only regardless.
    /// </summary>
    private static void ReportVulkanRoute(HashSet<string> glExtensions)
    {
        Console.WriteLine("Route B - OpenGL to Vulkan");
        Console.WriteLine("--------------------------");

        bool memory = glExtensions.Contains("GL_EXT_memory_object");
        bool semaphore = glExtensions.Contains("GL_EXT_semaphore");
        Report("GL_EXT_memory_object", memory, "shared allocations");
        Report("GL_EXT_semaphore", semaphore, "cross-API sync");

        // The base extensions are platform-agnostic; the handle type is not.
        // Windows imports NT handles, Linux imports file descriptors, and a
        // driver can ship the base pair without either.
        bool handles;
        if (OperatingSystem.IsWindows())
        {
            bool memoryWin32 = glExtensions.Contains("GL_EXT_memory_object_win32");
            bool semaphoreWin32 = glExtensions.Contains("GL_EXT_semaphore_win32");
            Report("GL_EXT_memory_object_win32", memoryWin32, "NT handle import");
            Report("GL_EXT_semaphore_win32", semaphoreWin32, "NT handle import");
            handles = memoryWin32 && semaphoreWin32;
        }
        else
        {
            bool memoryFd = glExtensions.Contains("GL_EXT_memory_object_fd");
            bool semaphoreFd = glExtensions.Contains("GL_EXT_semaphore_fd");
            Report("GL_EXT_memory_object_fd", memoryFd, "fd import");
            Report("GL_EXT_semaphore_fd", semaphoreFd, "fd import");
            handles = memoryFd && semaphoreFd;
        }

        Console.WriteLine();
        Console.WriteLine(memory && semaphore && handles
            ? "  => Viable. Super resolution can run in a Vulkan sidecar with the\n     renderer left in OpenGL."
            : "  => Blocked. No Vulkan sharing path on this driver.");
        Console.WriteLine();
    }

    private static void Report(string name, bool present, string? note)
    {
        string suffix = note is null ? string.Empty : "  - " + note;
        Console.WriteLine($"  {(present ? "yes  " : "NO   ")} {name}{suffix}");
    }

    private static HashSet<string> ReadGlExtensions()
    {
        var extensions = new HashSet<string>(StringComparer.Ordinal);
        GL.GetInteger(GetPName.NumExtensions, out int count);
        for (int i = 0; i < count; i++)
        {
            extensions.Add(GL.GetString(StringNameIndexed.Extensions, i));
        }

        return extensions;
    }

    /// <summary>
    /// WGL extensions live outside the GL extension string and need a device
    /// context to query, so they are absent from the list above even when the
    /// driver supports them.
    /// </summary>
    private static HashSet<string> ReadWglExtensions()
    {
        var extensions = new HashSet<string>(StringComparer.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            return extensions;
        }

        IntPtr address = WglGetProcAddress("wglGetExtensionsStringARB");
        if (address == IntPtr.Zero)
        {
            address = WglGetProcAddress("wglGetExtensionsStringEXT");
            if (address == IntPtr.Zero)
            {
                return extensions;
            }

            var readExt = Marshal.GetDelegateForFunctionPointer<WglGetExtensionsStringExt>(address);
            AddAll(extensions, Marshal.PtrToStringAnsi(readExt()));
            return extensions;
        }

        var readArb = Marshal.GetDelegateForFunctionPointer<WglGetExtensionsStringArb>(address);
        AddAll(extensions, Marshal.PtrToStringAnsi(readArb(WglGetCurrentDC())));
        return extensions;
    }

    private static void AddAll(HashSet<string> target, string? spaceSeparated)
    {
        if (string.IsNullOrWhiteSpace(spaceSeparated))
        {
            return;
        }

        foreach (string name in spaceSeparated.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            target.Add(name);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WglGetExtensionsStringArb(IntPtr deviceContext);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate IntPtr WglGetExtensionsStringExt();

    [DllImport("opengl32.dll", EntryPoint = "wglGetProcAddress", CharSet = CharSet.Ansi)]
    private static extern IntPtr WglGetProcAddress(string name);

    [DllImport("opengl32.dll", EntryPoint = "wglGetCurrentDC")]
    private static extern IntPtr WglGetCurrentDC();
}
