using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The presentation chain, and the only place in the backend where the image is
/// flipped.
///
/// Everything upstream renders in OpenGL's orientation, because GL and Vulkan
/// agree on how clip space maps to framebuffer memory and differ only in which
/// corner they name the origin. That keeps every intermediate target, every
/// render-to-texture round trip and every screenshot byte-identical to the GL
/// path. Only scanout disagrees - the display reads row 0 at the top - so the
/// correction happens once, here, as an inverted blit at present time.
/// </summary>
internal sealed unsafe class Swapchain : IDisposable
{
    private readonly VulkanContext _context;
    private readonly KhrSurface _surfaceApi;
    private readonly KhrSwapchain _swapchainApi;
    private readonly SurfaceKHR _surface;

    private SwapchainKHR _handle;
    private Image[] _images = Array.Empty<Image>();
    private ImageView[] _views = Array.Empty<ImageView>();
    private Semaphore[] _imageAvailable = Array.Empty<Semaphore>();
    private Semaphore[] _renderFinished = Array.Empty<Semaphore>();
    private int _semaphoreIndex;
    private bool _disposed;

    public Format Format { get; private set; } = Format.B8G8R8A8Unorm;
    public Extent2D Extent { get; private set; }
    public PresentModeKHR PresentMode { get; private set; } = PresentModeKHR.FifoKhr;
    public uint ImageCount => (uint)_images.Length;

    /// <summary>Set when the surface reports the chain is stale and it must be rebuilt.</summary>
    public bool NeedsRecreation { get; private set; }

    private Swapchain(VulkanContext context, KhrSurface surfaceApi, KhrSwapchain swapchainApi, SurfaceKHR surface)
    {
        _context = context;
        _surfaceApi = surfaceApi;
        _swapchainApi = swapchainApi;
        _surface = surface;
    }

    /// <remarks>
    /// Takes ownership of <paramref name="surface"/> on entry: on every failure
    /// return the surface is destroyed here, and on success the swapchain
    /// destroys it in <see cref="Dispose"/>. The caller never destroys it.
    /// </remarks>
    public static bool TryCreate(
        VulkanContext context, SurfaceKHR surface, uint width, uint height, bool vsync,
        out Swapchain? swapchain, out string? failureReason)
    {
        swapchain = null;
        failureReason = null;

        if (!context.Api.TryGetInstanceExtension(context.Instance, out KhrSurface surfaceApi))
        {
            failureReason = "VK_KHR_surface unavailable";
            WindowSurface.Destroy(context, surface);
            return false;
        }
        if (!context.Api.TryGetDeviceExtension(context.Instance, context.Device, out KhrSwapchain swapchainApi))
        {
            failureReason = "VK_KHR_swapchain unavailable";
            surfaceApi.DestroySurface(context.Instance, surface, null);
            surfaceApi.Dispose();
            return false;
        }

        // The graphics queue has to be able to present. A separate present queue
        // is possible in principle but does not occur on any desktop driver, and
        // supporting it would add a queue-ownership transfer to every frame.
        surfaceApi.GetPhysicalDeviceSurfaceSupport(
            context.PhysicalDevice, context.GraphicsQueueFamily, surface,
            out Silk.NET.Core.Bool32 supported);
        if (!supported)
        {
            failureReason = "the graphics queue family cannot present to this surface";
            surfaceApi.DestroySurface(context.Instance, surface, null);
            surfaceApi.Dispose();
            swapchainApi.Dispose();
            return false;
        }

        var created = new Swapchain(context, surfaceApi, swapchainApi, surface);
        if (!created.Build(width, height, vsync, out failureReason))
        {
            created.Dispose();
            return false;
        }

        swapchain = created;
        return true;
    }

    private bool Build(uint width, uint height, bool vsync, out string? failureReason)
    {
        failureReason = null;

        _surfaceApi.GetPhysicalDeviceSurfaceCapabilities(
            _context.PhysicalDevice, _surface, out SurfaceCapabilitiesKHR capabilities);

        Extent = ChooseExtent(capabilities, width, height);
        if (Extent.Width == 0 || Extent.Height == 0)
        {
            failureReason = "surface has zero extent";
            return false;
        }

        Format = ChooseFormat(out ColorSpaceKHR colorSpace);
        PresentMode = ChoosePresentMode(vsync);

        uint imageCount = capabilities.MinImageCount + 1;
        if (capabilities.MaxImageCount > 0 && imageCount > capabilities.MaxImageCount)
        {
            imageCount = capabilities.MaxImageCount;
        }

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = Format,
            ImageColorSpace = colorSpace,
            ImageExtent = Extent,
            ImageArrayLayers = 1,
            // Transfer destination because the frame is blitted in rather than
            // rendered directly: the game renders into its own targets and the
            // last step copies the result across, flipping it on the way.
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = PresentMode,
            Clipped = true,
            OldSwapchain = default,
        };

        if (_swapchainApi.CreateSwapchain(_context.Device, &createInfo, null, out SwapchainKHR handle)
            != Result.Success)
        {
            failureReason = "vkCreateSwapchainKHR failed";
            return false;
        }
        _handle = handle;

        uint count = 0;
        _swapchainApi.GetSwapchainImages(_context.Device, _handle, ref count, null);
        _images = new Image[count];
        fixed (Image* imagesPtr = _images)
        {
            _swapchainApi.GetSwapchainImages(_context.Device, _handle, ref count, imagesPtr);
        }

        _views = new ImageView[count];
        for (int i = 0; i < count; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _images[i],
                ViewType = ImageViewType.Type2D,
                Format = Format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            _context.Api.CreateImageView(_context.Device, &viewInfo, null, out _views[i]);
        }

        CreateSemaphores((int)count);
        NeedsRecreation = false;
        return true;
    }

    private void CreateSemaphores(int count)
    {
        DestroySemaphores();

        _imageAvailable = new Semaphore[count];
        _renderFinished = new Semaphore[count];

        var createInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        for (int i = 0; i < count; i++)
        {
            _context.Api.CreateSemaphore(_context.Device, &createInfo, null, out _imageAvailable[i]);
            _context.Api.CreateSemaphore(_context.Device, &createInfo, null, out _renderFinished[i]);
        }
        _semaphoreIndex = 0;
    }

    private Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities, uint width, uint height)
    {
        // A driver that pins the extent wins; otherwise clamp what we asked for.
        if (capabilities.CurrentExtent.Width != uint.MaxValue)
        {
            return capabilities.CurrentExtent;
        }

        return new Extent2D(
            Math.Clamp(width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
            Math.Clamp(height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));
    }

    /// <summary>
    /// Prefers a plain 8-bit BGRA format in sRGB colour space. The game's default
    /// framebuffer is linear - it never enables GL_FRAMEBUFFER_SRGB - so an
    /// _SRGB image format would apply a conversion the GL path never did and
    /// wash the picture out.
    /// </summary>
    private Format ChooseFormat(out ColorSpaceKHR colorSpace)
    {
        uint count = 0;
        _surfaceApi.GetPhysicalDeviceSurfaceFormats(_context.PhysicalDevice, _surface, ref count, null);

        var formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* formatsPtr = formats)
        {
            _surfaceApi.GetPhysicalDeviceSurfaceFormats(_context.PhysicalDevice, _surface, ref count, formatsPtr);
        }

        foreach (SurfaceFormatKHR candidate in formats)
        {
            if (candidate.Format is Format.B8G8R8A8Unorm or Format.R8G8B8A8Unorm)
            {
                colorSpace = candidate.ColorSpace;
                return candidate.Format;
            }
        }

        if (formats.Length > 0)
        {
            colorSpace = formats[0].ColorSpace;
            return formats[0].Format;
        }

        colorSpace = ColorSpaceKHR.SpaceSrgbNonlinearKhr;
        return Format.B8G8R8A8Unorm;
    }

    /// <summary>
    /// FIFO when vsync is on, since it is the only mode guaranteed present.
    /// Otherwise mailbox if the driver has it - it drops frames instead of
    /// tearing - and immediate as the fallback.
    /// </summary>
    private PresentModeKHR ChoosePresentMode(bool vsync)
    {
        if (vsync) return PresentModeKHR.FifoKhr;

        uint count = 0;
        _surfaceApi.GetPhysicalDeviceSurfacePresentModes(_context.PhysicalDevice, _surface, ref count, null);

        var modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* modesPtr = modes)
        {
            _surfaceApi.GetPhysicalDeviceSurfacePresentModes(_context.PhysicalDevice, _surface, ref count, modesPtr);
        }

        foreach (PresentModeKHR mode in modes)
        {
            if (mode == PresentModeKHR.MailboxKhr) return mode;
        }
        foreach (PresentModeKHR mode in modes)
        {
            if (mode == PresentModeKHR.ImmediateKhr) return mode;
        }
        return PresentModeKHR.FifoKhr;
    }

    /// <summary>
    /// Acquires the next image. Returns false when the chain is stale, which the
    /// caller turns into a rebuild rather than an error - a resize or a monitor
    /// change is ordinary.
    /// </summary>
    public bool TryAcquire(out uint imageIndex, out Semaphore waitSemaphore, out Semaphore signalSemaphore)
    {
        imageIndex = 0;
        waitSemaphore = _imageAvailable[_semaphoreIndex];

        long waitStart = VulkanStats.WaitStart();
        Result result = _swapchainApi.AcquireNextImage(
            _context.Device, _handle, ulong.MaxValue, waitSemaphore, default, ref imageIndex);
        VulkanStats.NoteWait(WaitSite.SwapchainAcquire, waitStart);

        // The render-finished semaphore belongs to the acquired IMAGE, not to a
        // rolling counter: vkQueuePresentKHR keeps waiting on it until that image
        // is presented, and the only moment it is provably free again is when
        // the same image is re-acquired. A counter-indexed semaphore could be
        // re-signalled while an earlier present still waits on it.
        signalSemaphore = _renderFinished[(int)imageIndex % Math.Max(_renderFinished.Length, 1)];

        if (result is Result.ErrorOutOfDateKhr)
        {
            NeedsRecreation = true;
            return false;
        }
        if (result == Result.SuboptimalKhr)
        {
            // Usable this frame; rebuilt before the next one.
            NeedsRecreation = true;
            return true;
        }
        // Anything else - a lost device above all - is reported rather than
        // turned into a quiet "no image this frame", which reads as a freeze.
        VulkanResult.Check(result, "vkAcquireNextImageKHR");
        return result == Result.Success;
    }

    public Image ImageAt(uint index) => _images[index];
    public ImageView ViewAt(uint index) => _views[index];

    public void Present(uint imageIndex, Semaphore waitSemaphore)
    {
        SwapchainKHR handle = _handle;
        Semaphore wait = waitSemaphore;
        uint index = imageIndex;

        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &wait,
            SwapchainCount = 1,
            PSwapchains = &handle,
            PImageIndices = &index,
        };

        // Presenting is a queue operation like any other, so it takes the same
        // lock as submission.
        Result result;
        long waitStart = VulkanStats.WaitStart();
        lock (_context.QueueLock)
        {
            result = _swapchainApi.QueuePresent(_context.GraphicsQueue, &presentInfo);
        }
        VulkanStats.NoteWait(WaitSite.Present, waitStart);
        if (result is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
        {
            NeedsRecreation = true;
        }
        else
        {
            VulkanResult.Check(result, "vkQueuePresentKHR");
        }

        _semaphoreIndex = (_semaphoreIndex + 1) % Math.Max(_imageAvailable.Length, 1);
    }

    public bool Recreate(uint width, uint height, bool vsync, out string? failureReason)
    {
        VulkanStats.WaitDeviceIdle(_context.Api, _context.Device);
        DestroyChain();
        return Build(width, height, vsync, out failureReason);
    }

    private void DestroyChain()
    {
        foreach (ImageView view in _views)
        {
            if (view.Handle != 0) _context.Api.DestroyImageView(_context.Device, view, null);
        }
        _views = Array.Empty<ImageView>();
        _images = Array.Empty<Image>();

        if (_handle.Handle != 0)
        {
            _swapchainApi.DestroySwapchain(_context.Device, _handle, null);
            _handle = default;
        }
    }

    private void DestroySemaphores()
    {
        foreach (Semaphore semaphore in _imageAvailable)
        {
            if (semaphore.Handle != 0) _context.Api.DestroySemaphore(_context.Device, semaphore, null);
        }
        foreach (Semaphore semaphore in _renderFinished)
        {
            if (semaphore.Handle != 0) _context.Api.DestroySemaphore(_context.Device, semaphore, null);
        }
        _imageAvailable = Array.Empty<Semaphore>();
        _renderFinished = Array.Empty<Semaphore>();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        VulkanStats.WaitDeviceIdle(_context.Api, _context.Device);
        DestroyChain();
        DestroySemaphores();

        if (_surface.Handle != 0)
        {
            _surfaceApi.DestroySurface(_context.Instance, _surface, null);
        }

        _swapchainApi.Dispose();
        _surfaceApi.Dispose();
    }
}
