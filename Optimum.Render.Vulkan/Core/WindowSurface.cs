using System;
using System.Collections.Generic;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Creates a Vulkan presentation surface for a GLFW window.
///
/// The client already opens its window through OpenTK's GLFW bindings, so the
/// surface is created from the same window pointer rather than by standing up a
/// second windowing stack. The only change the client needs is to ask for
/// <c>ContextAPI.NoAPI</c> so GLFW does not create an OpenGL context alongside.
/// </summary>
internal static unsafe class WindowSurface
{
    /// <summary>Whether a Vulkan loader is reachable at all.</summary>
    public static bool VulkanSupported()
    {
        try
        {
            return GLFW.VulkanSupported();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The instance extensions the platform needs before a surface can be made.
    /// These must be enabled at instance creation, which is why they are asked
    /// for before the context exists.
    /// </summary>
    public static string[] RequiredInstanceExtensions()
    {
        try
        {
            return GLFW.GetRequiredInstanceExtensions() ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Creates the surface. The window pointer is the GLFW handle the client
    /// already holds.
    /// </summary>
    /// <summary>
    /// Destroys a surface that never reached a <see cref="Swapchain"/>. The
    /// swapchain owns the surface once it exists, so this is only for the
    /// failure paths between creation and hand-over; the instance must not be
    /// destroyed with a surface still alive under it.
    /// </summary>
    public static void Destroy(VulkanContext context, SurfaceKHR surface)
    {
        if (surface.Handle == 0) return;
        if (!context.Api.TryGetInstanceExtension(context.Instance, out KhrSurface surfaceApi)) return;
        surfaceApi.DestroySurface(context.Instance, surface, null);
        surfaceApi.Dispose();
    }

    public static bool TryCreate(
        VulkanContext context, IntPtr windowHandle, out SurfaceKHR surface, out string? failureReason)
    {
        surface = default;
        failureReason = null;

        if (windowHandle == IntPtr.Zero)
        {
            failureReason = "no window handle";
            return false;
        }

        try
        {
            var instanceHandle = new VkHandle(context.Instance.Handle);
            int result = GLFW.CreateWindowSurface(
                instanceHandle, (Window*)windowHandle, null, out VkHandle surfaceHandle);

            if (result != 0)
            {
                failureReason = "glfwCreateWindowSurface failed with " + result;
                return false;
            }

            surface = new SurfaceKHR((ulong)surfaceHandle.Handle);
            return true;
        }
        catch (Exception error)
        {
            failureReason = "surface creation threw: " + error.Message;
            return false;
        }
    }
}
