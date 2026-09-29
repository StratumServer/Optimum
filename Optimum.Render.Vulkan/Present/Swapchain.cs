using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;

using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The process-wide present id: one
/// value per <c>vkQueuePresentKHR</c>, monotonically increasing and never reset.
///
/// It is deliberately not the latency frame id and not a Frame timeline value.
/// The Frame timeline advances two or three times per frame, and the frame id is
/// allocated once per rendered frame; the present id counts presents, which is
/// what <c>VK_KHR_present_id</c> and every vendor's present-timing query mean by
/// it. One frame maps to one present today, and the map below keeps that pairing
/// explicit across partial submissions and swapchain recreation.
///
/// Global rather than per swapchain so the sequence survives recreation: a
/// resize, a vsync toggle or an OUT_OF_DATE rebuild must not restart it.
/// </summary>
internal static class PresentIdCounter
{
    private static long _next;

    /// <summary>The next present id; the first is 1.</summary>
    public static ulong Next() => (ulong)System.Threading.Interlocked.Increment(ref _next);

    /// <summary>The last id handed out, 0 before the first present.</summary>
    public static ulong Current => (ulong)System.Threading.Interlocked.Read(ref _next);
}

/// <summary>
/// The last presents' frame id per present id, kept small and wrapping: enough
/// to answer "which frame was present id N" for the frames a driver report can
/// still be about, never a growing map.
/// </summary>
internal sealed class PresentIdMap
{
    public const int DefaultCapacity = 64;

    private readonly ulong[] _presentIds;
    private readonly ulong[] _frameIds;
    private int _next;

    public PresentIdMap(int capacity = DefaultCapacity)
    {
        _presentIds = new ulong[capacity];
        _frameIds = new ulong[capacity];
    }

    /// <summary>The newest present id recorded, 0 before the first.</summary>
    public ulong LastPresentId { get; private set; }

    /// <summary>The frame id of the newest present recorded, 0 before the first.</summary>
    public ulong LastFrameId { get; private set; }

    public void Record(ulong presentId, ulong frameId)
    {
        _presentIds[_next] = presentId;
        _frameIds[_next] = frameId;
        _next = (_next + 1) % _presentIds.Length;
        LastPresentId = presentId;
        LastFrameId = frameId;
    }

    /// <summary>The frame that produced <paramref name="presentId" />, while it is still remembered.</summary>
    public bool TryGetFrameId(ulong presentId, out ulong frameId)
    {
        for (int i = 0; i < _presentIds.Length; i++)
        {
            if (_presentIds[i] == presentId && presentId != 0)
            {
                frameId = _frameIds[i];
                return true;
            }
        }
        frameId = 0;
        return false;
    }
}

/// <summary>An acquired swapchain image and the semaphores its present submission uses.</summary>
internal readonly struct PresentTarget
{
    public PresentTarget(SwapchainSlot slot, uint imageIndex, Semaphore acquireSemaphore)
    {
        Slot = slot;
        ImageIndex = imageIndex;
        AcquireSemaphore = acquireSemaphore;
    }

    public SwapchainSlot Slot { get; }
    public uint ImageIndex { get; }

    /// <summary>Signalled by the acquire; Submit B waits on it.</summary>
    public Semaphore AcquireSemaphore { get; }

    /// <summary>Signalled by Submit B; vkQueuePresentKHR waits on it.</summary>
    public Semaphore PresentSemaphore => Slot.PresentSemaphoreFor(ImageIndex);

    public Image Image => Slot.Images[ImageIndex];
    public Extent2D Extent => Slot.Extent;
}

/// <summary>
/// One vkCreateSwapchainKHR result and everything that belongs to it: images,
/// views, the acquire-semaphore free list (<c>imageCount + 1</c>) and one present
/// semaphore per image. Created by <see cref="Swapchain" /> and retired as one
/// unit through <see cref="SwapchainRetirement" /> once a successor image
/// reacquisition proves presentation has released it, so its semaphores die with it.
/// </summary>
internal sealed unsafe class SwapchainSlot : IDisposable
{
    private readonly VulkanContext _context;
    private readonly KhrSwapchain _api;
    private readonly Semaphore[] _acquireSemaphores;
    private readonly Semaphore[] _presentSemaphores;
    private readonly AcquireSemaphoreFreeList _freeAcquire;
    private readonly List<Fence> _pendingPresents = new();
    private readonly Stack<Fence> _freePresentFences = new();
    private bool _disposed;

    public SwapchainSlot(VulkanContext context, KhrSwapchain api, SwapchainKHR handle,
        Extent2D extent, Format format, PresentModeKHR presentMode)
    {
        _context = context;
        _api = api;
        Handle = handle;
        Extent = extent;
        Format = format;
        PresentMode = presentMode;

        uint count = 0;
        api.GetSwapchainImages(context.Device, handle, ref count, null);
        Images = new Image[count];
        fixed (Image* imagesPtr = Images)
        {
            api.GetSwapchainImages(context.Device, handle, ref count, imagesPtr);
        }

        Views = new ImageView[count];
        for (int i = 0; i < count; i++)
        {
            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = Images[i],
                ViewType = ImageViewType.Type2D,
                Format = format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
            };
            ImageView view;
            VulkanResult.Check(context.Api.CreateImageView(context.Device, &viewInfo, null, &view),
                "vkCreateImageView for a swapchain image");
            Views[i] = view;
        }

        // The present semaphore belongs to the IMAGE, not to a rolling counter:
        // vkQueuePresentKHR keeps waiting on it until that image is presented, and
        // the only moment it is provably free again is when the same image is
        // re-acquired. A counter-indexed semaphore could be re-signalled while an
        // earlier present still waits on it.
        _presentSemaphores = CreateSemaphores((int)count);
        _acquireSemaphores = CreateSemaphores(AcquireSemaphoreFreeList.CapacityFor(count));
        var handles = new ulong[_acquireSemaphores.Length];
        for (int i = 0; i < handles.Length; i++) handles[i] = _acquireSemaphores[i].Handle;
        _freeAcquire = new AcquireSemaphoreFreeList(handles);
    }

    public SwapchainKHR Handle { get; }
    public Image[] Images { get; }
    public ImageView[] Views { get; }
    public Extent2D Extent { get; }
    public Format Format { get; }
    public PresentModeKHR PresentMode { get; }
    public uint ImageCount => (uint)Images.Length;
    public int AcquireSemaphoreCount => _acquireSemaphores.Length;
    public int FreeAcquireSemaphores => _freeAcquire.FreeCount;

    /// <summary>Frame timeline value of the newest present submission that used one of this slot's images; 0 before the first.</summary>
    public ulong LastPresentValue { get; private set; }

    public uint? FirstPresentedImage { get; private set; }

    public void NotePresented(uint imageIndex) => FirstPresentedImage ??= imageIndex;

    public Semaphore PresentSemaphoreFor(uint imageIndex) => _presentSemaphores[imageIndex];

    public int PendingAcquireSemaphores => _freeAcquire.PendingCount;

    /// <summary>A semaphore for the next acquire; <paramref name="frameCompleted" /> releases those whose present submission finished.</summary>
    public Semaphore TakeAcquireSemaphore(ulong frameCompleted) => new(_freeAcquire.Take(frameCompleted));

    /// <summary>The acquire failed; the semaphore is untouched.</summary>
    public void ReturnAcquireSemaphore(Semaphore semaphore) => _freeAcquire.Return(semaphore.Handle);

    /// <summary>A submission carrying <paramref name="frameValue" /> waits on the semaphore; reusable once it completed.</summary>
    public void ReturnAcquireSemaphoreAfter(Semaphore semaphore, ulong frameValue) =>
        _freeAcquire.ReturnAfter(semaphore.Handle, frameValue);

    public void NotePresentSubmitted(ulong frameValue)
    {
        if (frameValue > LastPresentValue) LastPresentValue = frameValue;
    }

    public bool PresentsComplete()
    {
        for (int i = _pendingPresents.Count - 1; i >= 0; i--)
        {
            Fence fence = _pendingPresents[i];
            Result status = _context.Api.GetFenceStatus(_context.Device, fence);
            if (status == Result.NotReady) continue;
            VulkanResult.Check(status, "vkGetFenceStatus for presentation");
            _pendingPresents.RemoveAt(i);
            _freePresentFences.Push(fence);
        }
        return _pendingPresents.Count == 0;
    }

    public Fence PreparePresentFence()
    {
        if (!_context.Capabilities.PresentFencesEnabled) return default;
        PresentsComplete();
        if (_freePresentFences.TryPop(out Fence fence))
            VulkanResult.Check(_context.Api.ResetFences(_context.Device, 1, &fence), "vkResetFences for presentation");
        else
        {
            var info = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
            VulkanResult.Check(_context.Api.CreateFence(_context.Device, &info, null, &fence), "vkCreateFence for presentation");
        }
        return fence;
    }

    public void FinishPresentFence(Fence fence, Result result)
    {
        if (fence.Handle == 0) return;
        // Memory failures leave synchronization primitives untouched. Other present
        // errors either enqueue the operation or lose the device.
        if (result is Result.ErrorOutOfHostMemory or Result.ErrorOutOfDeviceMemory)
            _freePresentFences.Push(fence);
        else
            _pendingPresents.Add(fence);
    }

    private Semaphore[] CreateSemaphores(int count)
    {
        var semaphores = new Semaphore[count];
        var createInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        for (int i = 0; i < count; i++)
        {
            Semaphore semaphore;
            VulkanResult.Check(_context.Api.CreateSemaphore(_context.Device, &createInfo, null, &semaphore),
                "vkCreateSemaphore for a swapchain slot");
            semaphores[i] = semaphore;
        }
        return semaphores;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Teardown waits; ordinary retirement polls these fences before disposing.
        foreach (Fence pending in _pendingPresents)
        {
            Fence fence = pending;
            Result result = _context.Api.WaitForFences(_context.Device, 1, &fence, true, ulong.MaxValue);
            if (result != Result.ErrorDeviceLost) VulkanResult.Check(result, "vkWaitForFences for presentation teardown");
            _context.Api.DestroyFence(_context.Device, fence, null);
        }
        foreach (Fence fence in _freePresentFences) _context.Api.DestroyFence(_context.Device, fence, null);
        _pendingPresents.Clear();
        _freePresentFences.Clear();

        foreach (ImageView view in Views)
        {
            if (view.Handle != 0) _context.Api.DestroyImageView(_context.Device, view, null);
        }
        if (Handle.Handle != 0) _api.DestroySwapchain(_context.Device, Handle, null);
        foreach (Semaphore semaphore in _acquireSemaphores)
        {
            if (semaphore.Handle != 0) _context.Api.DestroySemaphore(_context.Device, semaphore, null);
        }
        foreach (Semaphore semaphore in _presentSemaphores)
        {
            if (semaphore.Handle != 0) _context.Api.DestroySemaphore(_context.Device, semaphore, null);
        }
    }
}

/// <summary>
/// The presentation chain.
///
/// Recreation follows the Khronos swapchain_recreation sample: the current
/// swapchain is always passed as <c>oldSwapchain</c>, nothing waits for the
/// device to go idle, and the replaced <see cref="SwapchainSlot" /> is retired on
/// the Frame timeline after the last present submission that used it. SUBOPTIMAL
/// (from acquire or present) rebuilds before the next acquire; OUT_OF_DATE
/// rebuilds and acquires once more; a zero extent (a minimised window) parks
/// presentation until the surface grows again.
///
/// The one flip of the image happens in the present path
/// (<see cref="BlitPresentPath" />), not here.
/// </summary>
internal sealed unsafe class Swapchain : IDisposable
{
    /// <summary>OPTIMUM_VULKAN_FIFO_RELAXED=0 forces plain FIFO (no promotion on missed vsyncs).</summary>
    public const string FifoRelaxedVariable = "OPTIMUM_VULKAN_FIFO_RELAXED";

    private readonly VulkanContext _context;
    private readonly KhrSurface _surfaceApi;
    private readonly KhrSwapchain _swapchainApi;
    private readonly SurfaceKHR _surface;
    private readonly SwapchainRetirement _retirement;
    private readonly ITimelineClock _clock;
    private readonly bool _relaxedAllowed;

    private SwapchainSlot? _current;
    private uint _width;
    private uint _height;
    private bool _vsync;
    private bool _relaxedPromoted;
    private bool _disposed;

    public Format Format { get; private set; } = Format.B8G8R8A8Unorm;
    public Extent2D Extent { get; private set; }
    public PresentModeKHR PresentMode { get; private set; } = PresentModeKHR.FifoKhr;
    public uint ImageCount => _current?.ImageCount ?? 0;

    /// <summary>Set when the chain is stale; the next acquire rebuilds it first.</summary>
    public bool NeedsRecreation { get; private set; }

    /// <summary>The surface has zero extent; nothing is acquired or presented until it grows.</summary>
    public bool Parked { get; private set; }

    /// <summary>Swapchains created so far, the first included.</summary>
    public int Creations { get; private set; }

    /// <summary>Replaced slots still waiting for the GPU.</summary>
    public int RetiredPending => _retirement.PendingCount;

    /// <summary>Why the last rebuild failed; null after a successful one.</summary>
    public string? RebuildFailure { get; private set; }

    /// <summary>The slot being acquired from. Tests only.</summary>
    internal SwapchainSlot? CurrentSlotForTests => _current;

    /// <summary>
    /// Whether <c>VkPresentIdKHR</c> may be chained onto the present (seam S2).
    /// Set from the capabilities when VK_KHR_present_id is enabled AND its feature
    /// was turned on; chaining it otherwise is a validation error, so it stays off
    /// by default. The id itself is allocated either way, so the frame to present
    /// mapping does not depend on the extension.
    /// </summary>
    internal bool PresentIdEnabled { get; set; }

    /// <summary>The frame id of each of the last presents, by present id (seam S2).</summary>
    internal PresentIdMap PresentIds { get; } = new();

    private Swapchain(VulkanContext context, KhrSurface surfaceApi, KhrSwapchain swapchainApi, SurfaceKHR surface,
        ITimelineClock clock)
    {
        _context = context;
        _surfaceApi = surfaceApi;
        _swapchainApi = swapchainApi;
        _surface = surface;
        _clock = clock;
        _retirement = new SwapchainRetirement(clock);
        _relaxedAllowed = Environment.GetEnvironmentVariable(FifoRelaxedVariable)?.Trim() != "0";
    }

    /// <remarks>
    /// Takes ownership of <paramref name="surface"/> on entry: on every failure
    /// return the surface is destroyed here, and on success the swapchain
    /// destroys it in <see cref="Dispose"/>. The caller never destroys it.
    /// </remarks>
    public static bool TryCreate(
        VulkanContext context, SurfaceKHR surface, uint width, uint height, bool vsync, ITimelineClock clock,
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

        var created = new Swapchain(context, surfaceApi, swapchainApi, surface, clock);
        created._width = width;
        created._height = height;
        created._vsync = vsync;
        if (!created.Build(out failureReason))
        {
            // A window that starts minimised is not a device the client can use.
            if (created.Parked) failureReason = "surface has zero extent";
            created.Dispose();
            return false;
        }

        swapchain = created;
        return true;
    }

    /// <summary>
    /// Builds a new slot from the current surface state, passing the current one
    /// as oldSwapchain and retiring it. Never waits. Returns false when parked (the
    /// current slot is kept for the rebuild that unparks) or when creation failed.
    /// </summary>
    private bool Build(out string? failureReason)
    {
        failureReason = null;

        _surfaceApi.GetPhysicalDeviceSurfaceCapabilities(
            _context.PhysicalDevice, _surface, out SurfaceCapabilitiesKHR capabilities);

        Extent2D extent = ChooseExtent(capabilities, _width, _height);
        if (SwapchainPolicy.IsParked(extent))
        {
            Parked = true;
            NeedsRecreation = true;
            return false;
        }
        Parked = false;

        Format = ChooseFormat(out ColorSpaceKHR colorSpace);
        PresentModeKHR presentMode = SwapchainPolicy.ChoosePresentMode(
            _vsync, _relaxedPromoted && _relaxedAllowed, SupportedPresentModes());
        uint imageCount = SwapchainPolicy.ChooseImageCount(
            capabilities.MinImageCount, capabilities.MaxImageCount, presentMode);

        SwapchainSlot? old = _current;
        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = Format,
            ImageColorSpace = colorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            // Transfer destination because the frame is blitted in rather than
            // rendered directly: the game renders into its own targets and the
            // last step copies the result across, flipping it on the way.
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferDstBit,
            ImageSharingMode = SharingMode.Exclusive,
            PreTransform = capabilities.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            // Always the current chain: images it has not handed out can be freed
            // by the driver right away, and presents already queued on it finish.
            OldSwapchain = old?.Handle ?? default,
        };

        Result result = _swapchainApi.CreateSwapchain(_context.Device, &createInfo, null, out SwapchainKHR handle);

        // Passing oldSwapchain retires it even when creation fails.
        if (old != null)
        {
            _retirement.Retire(old, old.LastPresentValue,
                _context.Capabilities.PresentFencesEnabled ? old.PresentsComplete : null);
            _current = null;
        }

        if (result != Result.Success)
        {
            failureReason = "vkCreateSwapchainKHR failed with " + result;
            RebuildFailure = failureReason;
            NeedsRecreation = true;
            return false;
        }

        _current = new SwapchainSlot(_context, _swapchainApi, handle, extent, Format, presentMode);
        Extent = extent;
        PresentMode = presentMode;
        Creations++;
        NeedsRecreation = false;
        RebuildFailure = null;
        return true;
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

    private PresentModeKHR[] SupportedPresentModes()
    {
        uint count = 0;
        _surfaceApi.GetPhysicalDeviceSurfacePresentModes(_context.PhysicalDevice, _surface, ref count, null);

        var modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* modesPtr = modes)
        {
            _surfaceApi.GetPhysicalDeviceSurfacePresentModes(_context.PhysicalDevice, _surface, ref count, modesPtr);
        }
        return modes;
    }

    /// <summary>
    /// Asks for a rebuild at the next acquire (a resize, a vsync toggle). Cheap
    /// and repeatable: a resize storm costs one rebuild per presented frame at most.
    /// </summary>
    public void RequestRebuild(uint width, uint height, bool vsync)
    {
        if (vsync != _vsync) _relaxedPromoted = false;
        _width = width;
        _height = height;
        _vsync = vsync;
        NeedsRecreation = true;
    }

    /// <summary>
    /// Sustained missed vsyncs under FIFO: rebuild as FIFO_RELAXED when the
    /// surface has it and the override allows it. Returns whether a rebuild was requested.
    /// </summary>
    public bool PromoteToRelaxedFifo()
    {
        if (!_vsync || _relaxedPromoted || !_relaxedAllowed) return false;
        if (Array.IndexOf(SupportedPresentModes(), PresentModeKHR.FifoRelaxedKhr) < 0) return false;
        _relaxedPromoted = true;
        NeedsRecreation = true;
        return true;
    }

    /// <summary>
    /// Acquires the next image, rebuilding first when the chain is stale.
    /// Returns false when nothing can be presented this frame: parked, the chain
    /// was still out of date after one rebuild, or the rebuild failed
    /// (<see cref="RebuildFailure" />). A resize or a monitor change is ordinary,
    /// never an error; a lost device throws.
    /// </summary>
    public bool TryAcquire(out PresentTarget target)
    {
        target = default;
        _retirement.Collect();

        if (NeedsRecreation || _current == null)
        {
            if (!Build(out _)) return false;
        }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            SwapchainSlot slot = _current!;
            Semaphore acquire = slot.TakeAcquireSemaphore(
                slot.FreeAcquireSemaphores == 0 ? _clock.FrameCompleted : 0);
            uint imageIndex = 0;

            long waitStart = VulkanStats.WaitStart();
            if (_context.AcquireDelayForTests > TimeSpan.Zero) System.Threading.Thread.Sleep(_context.AcquireDelayForTests);
            Result result = _swapchainApi.AcquireNextImage(
                _context.Device, slot.Handle, ulong.MaxValue, acquire, default, ref imageIndex);
            VulkanStats.NoteWait(WaitSite.SwapchainAcquire, waitStart);

            switch (SwapchainPolicy.OnAcquire(result, attempt))
            {
                case AcquireAction.Present:
                    target = new PresentTarget(slot, imageIndex, acquire);
                    return true;

                case AcquireAction.PresentThenRebuild:
                    // Usable this frame; rebuilt before the next acquire.
                    NeedsRecreation = true;
                    target = new PresentTarget(slot, imageIndex, acquire);
                    return true;

                case AcquireAction.RebuildAndRetry:
                    slot.ReturnAcquireSemaphore(acquire);
                    NeedsRecreation = true;
                    if (!Build(out _)) return false;
                    continue;

                case AcquireAction.SkipFrame:
                    slot.ReturnAcquireSemaphore(acquire);
                    NeedsRecreation = true;
                    return false;

                default:
                    slot.ReturnAcquireSemaphore(acquire);
                    // Anything else - a lost device above all - is reported rather
                    // than turned into a quiet "no image this frame", which reads
                    // as a freeze.
                    VulkanResult.Check(result, "vkAcquireNextImageKHR");
                    NeedsRecreation = true;
                    return false;
            }
        }
        return false;
    }

    /// <summary>
    /// The present submission waiting on <paramref name="target" />'s acquire
    /// semaphore was accepted with Frame value <paramref name="frameValue" />: the
    /// acquire semaphore is reusable once that value completes. If this reacquired
    /// the successor's first presented image, completion also releases retired chains.
    /// </summary>
    public void NotePresentSubmitted(in PresentTarget target, ulong frameValue)
    {
        if (target.Slot.FirstPresentedImage == target.ImageIndex)
            _retirement.NoteSuccessorReacquired(frameValue);
        target.Slot.ReturnAcquireSemaphoreAfter(target.AcquireSemaphore, frameValue);
        target.Slot.NotePresentSubmitted(frameValue);
    }

    /// <summary>
    /// Presents <paramref name="target" /> and returns the present id this present
    /// was given (seam S2): one per call, increasing across swapchain recreation,
    /// chained as <c>VkPresentIdKHR</c> when <see cref="PresentIdEnabled" />.
    /// <paramref name="frameId" /> is the latency frame id that produced it, kept
    /// in <see cref="PresentIds" />.
    /// </summary>
    public ulong Present(in PresentTarget target, ulong frameId = 0)
    {
        SwapchainKHR handle = target.Slot.Handle;
        Semaphore wait = target.PresentSemaphore;
        uint index = target.ImageIndex;

        // Allocated for every present, whether or not the extension carries it,
        // so the frame to present mapping is the same on every driver.
        ulong presentId = PresentIdCounter.Next();
        PresentIds.Record(presentId, frameId);

        var presentIdInfo = new PresentIdKHR
        {
            SType = StructureType.PresentIDKhr,
            SwapchainCount = 1,
            PPresentIds = &presentId,
        };

        Fence presentFence = target.Slot.PreparePresentFence();
        var fenceInfo = new SwapchainPresentFenceInfoEXT
        {
            SType = StructureType.SwapchainPresentFenceInfoExt,
            PNext = PresentIdEnabled ? &presentIdInfo : null,
            SwapchainCount = 1,
            PFences = &presentFence,
        };

        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            PNext = presentFence.Handle != 0 ? &fenceInfo : PresentIdEnabled ? &presentIdInfo : null,
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
        target.Slot.FinishPresentFence(presentFence, result);
        VulkanStats.NoteWait(WaitSite.Present, waitStart);
        if (result is Result.Success or Result.SuboptimalKhr) target.Slot.NotePresented(index);
        if (result is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr)
        {
            if (ReferenceEquals(target.Slot, _current)) NeedsRecreation = true;
        }
        else
        {
            VulkanResult.Check(result, "vkQueuePresentKHR");
        }

        return presentId;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Teardown, not recreation: everything this chain ever presented must be
        // finished before its slots go.
        VulkanStats.WaitDeviceIdle(_context.Api, _context.Device);
        _retirement.DisposeAll();
        _current?.Dispose();
        _current = null;
        // Nothing may be called against these handles again; the backend outlives
        // the swapchain (the device disposes it last).

        if (_surface.Handle != 0)
        {
            _surfaceApi.DestroySurface(_context.Instance, _surface, null);
        }

        _swapchainApi.Dispose();
        _surfaceApi.Dispose();
    }
}

/// <summary>
/// The wait stages of the present submission, checked in one place.
///
/// Submit B waits on two things: the Frame timeline at the value Submit A
/// signalled (the frame image it reads is finished), at COLOR_ATTACHMENT_OUTPUT;
/// and the acquire semaphore at the stage where the swapchain image is first
/// touched: TRANSFER for the flipped blit, COLOR_ATTACHMENT_OUTPUT for a raster
/// path (FSR's final pass). ALL_COMMANDS would also block the barrier and every
/// earlier command on the acquire, which is what the split exists to avoid.
/// </summary>
internal static class PresentWaitStages
{
    public const PipelineStageFlags FrameWait = PipelineStageFlags.ColorAttachmentOutputBit;
    public const PipelineStageFlags BlitAcquireWait = PipelineStageFlags.TransferBit;
    public const PipelineStageFlags RasterAcquireWait = PipelineStageFlags.ColorAttachmentOutputBit;

    /// <summary>Throws for a wait stage the present submission must not use.</summary>
    public static PipelineStageFlags RequireAcquireStage(PipelineStageFlags stage)
    {
        if (stage != BlitAcquireWait && stage != RasterAcquireWait)
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage,
                "the present submission waits on the acquire semaphore at TRANSFER or COLOR_ATTACHMENT_OUTPUT only");
        }
        return stage;
    }
}

/// <summary>
/// The presentation blit: the whole frame, GUI
/// included, renders into the owned default image, and this copies it into the
/// acquired swapchain image, flipped.
///
/// This inverted blit is the entire Y-flip story for the backend. Everything
/// upstream stays in OpenGL's orientation, which is what keeps intermediate
/// targets and screenshots byte-identical to the GL path; the display wants row 0
/// at the top, so the source rows are read bottom-to-top exactly once, here.
/// </summary>
internal sealed unsafe class BlitPresentPath
{
    private readonly VulkanContext _context;
    private readonly TextureManager _textures;
    private readonly Func<VulkanTexture?> _source;
    /// <summary>Created at the first record, so a path built for its stage table alone needs no texture table.</summary>
    private BarrierBatcher? _barriers;

    /// <summary>The acquired image's state; reset per frame, since its contents are discarded.</summary>
    private readonly ResourceStateTracker _swapchainImage = new(1, 1, depth: false);

    public BlitPresentPath(VulkanContext context, TextureManager textures, Func<VulkanTexture?> source)
    {
        _context = context;
        _textures = textures;
        _source = source;
    }

    public PipelineStageFlags AcquireWaitStage => PresentWaitStages.BlitAcquireWait;

    public void Record(CommandBuffer commandBuffer, in PresentTarget target)
    {
        Image destination = target.Image;

        // Every present leaves the swapchain image in PRESENT_SRC, and nothing
        // else writes it, so UNDEFINED discards nothing that matters. The
        // destination and the source move in one barrier command.
        // The acquire touched it last, at the stage this submission waits on it.
        BarrierBatcher barriers = _barriers ??= _textures.CreateBatcher();
        _swapchainImage.Reset((PipelineStageFlags2)(ulong)AcquireWaitStage);
        barriers.Require(destination, ImageAspectFlags.ColorBit, _swapchainImage, 0, 1, 0, 1,
            ResourceUsage.TransferDst, discard: true);

        VulkanTexture? source = _source();
        if (source != null) _textures.Require(barriers, commandBuffer, source, ResourceUsage.TransferSrc);
        barriers.Flush(commandBuffer);

        if (source != null)
        {

            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            };
            // Source Y runs backwards: this is the flip.
            blit.SrcOffsets.Element0 = new Offset3D(0, (int)source.Height, 0);
            blit.SrcOffsets.Element1 = new Offset3D((int)source.Width, 0, 1);
            blit.DstOffsets.Element0 = new Offset3D(0, 0, 0);
            blit.DstOffsets.Element1 = new Offset3D((int)target.Extent.Width, (int)target.Extent.Height, 1);

            _context.Api.CmdBlitImage(commandBuffer,
                source.Image, ImageLayout.TransferSrcOptimal,
                destination, ImageLayout.TransferDstOptimal,
                1, &blit, Filter.Linear);
        }

        // TRANSFER_DST (written at TRANSFER) to PRESENT_SRC (BOTTOM_OF_PIPE, no access).
        barriers.Require(destination, ImageAspectFlags.ColorBit, _swapchainImage, 0, 1, 0, 1,
            ResourceUsage.PresentSrc, discard: false);
        barriers.Flush(commandBuffer);
    }
}
