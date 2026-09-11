using System;
using System.Threading;
using Silk.NET.Vulkan;

// Silk.NET.Vulkan.Buffer collides with System.Buffer.
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Process-unique ids for device resources.
///
/// A Vulkan handle identifies an object only while it lives: destroy an image
/// view and the driver is free to hand the very same handle value to the next
/// one created. Anything that remembers a resource by handle - the descriptor
/// set cache does - would then mistake the newcomer for the dead one and serve
/// a set that points at freed memory. An id that is never reused is what such
/// a cache has to key on instead.
/// </summary>
internal static class ResourceIds
{
    private static long _next;

    public static ulong Next() => (ulong)Interlocked.Increment(ref _next);
}

/// <summary>A device buffer with its backing memory.</summary>
internal sealed unsafe class VulkanBuffer : IDisposable
{
    private readonly VulkanContext _context;
    private bool _disposed;

    public Buffer Handle { get; }
    public ulong Size { get; }

    /// <summary>Never reused, unlike <see cref="Handle" />; see <see cref="ResourceIds" />.</summary>
    public ulong Id { get; } = ResourceIds.Next();

    private MemoryAllocation _allocation;

    /// <summary>Which block this buffer's memory came from. For tests.</summary>
    internal ulong MemoryHandleForTest => _allocation.Memory.Handle;

    /// <summary>The block, offset and size this buffer occupies. For tests.</summary>
    internal MemoryAllocation Allocation => _allocation;

    /// <summary>Non-zero when the allocation is host visible and mapped.</summary>
    public IntPtr Mapped { get; private set; }

    /// <summary>
    /// The frame command buffer generation that last used this buffer; see
    /// <see cref="UploadManager.NoteUse(CommandBuffer, VulkanBuffer)" />.
    /// </summary>
    internal long FrameUse;

    public VulkanBuffer(VulkanContext context, ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties)
        : this(context, size, usage, properties, VulkanAllocator.InferClass(properties, linear: true))
    {
    }

    /// <summary>A buffer in an explicit pool class; see <see cref="MemoryPoolClass" />.</summary>
    public VulkanBuffer(VulkanContext context, ulong size, BufferUsageFlags usage, MemoryPropertyFlags properties,
        MemoryPoolClass poolClass)
    {
        _context = context;
        Size = size;

        var createInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };

        Vk api = context.Api;
        if (api.CreateBuffer(context.Device, &createInfo, null, out Buffer buffer) != Result.Success)
        {
            throw new InvalidOperationException("vkCreateBuffer failed");
        }
        Handle = buffer;

        MemoryRequirements requirements = VulkanAllocator.BufferRequirements(context, buffer, out bool dedicated);

        // A buffer is linear, so it shares blocks only with other buffers.
        _allocation = context.Allocator.Allocate(
            requirements, properties, linear: true, $"a {size} byte buffer", poolClass, dedicated, buffer, default);

        api.BindBufferMemory(context.Device, buffer, _allocation.Memory, _allocation.Offset);
        Mapped = _allocation.Mapped;
        if (context.PoisonFreshResources && Mapped != IntPtr.Zero)
        {
            VulkanPoison.FillHostMemory(Mapped, size);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // The mapping belongs to the block, not to this buffer, so it is not
        // unmapped here - the region simply goes back to the pool.
        Mapped = IntPtr.Zero;
        _context.Api.DestroyBuffer(_context.Device, Handle, null);
        _context.Allocator.Free(_allocation);
    }
}

/// <summary>An image, its memory and a default view.</summary>
internal sealed unsafe class VulkanImage : IDisposable
{
    private readonly VulkanContext _context;
    private bool _disposed;

    public Image Handle { get; }
    public ImageView View { get; }

    private MemoryAllocation _allocation;
    public Format Format { get; }
    public uint Width { get; }
    public uint Height { get; }

    /// <summary>
    /// Tracked so transitions can name the right old layout. Vulkan has no way to
    /// query it, so the backend has to remember.
    /// </summary>
    public ImageLayout Layout { get; set; } = ImageLayout.Undefined;

    public VulkanImage(
        VulkanContext context, uint width, uint height, Format format,
        ImageUsageFlags usage, ImageAspectFlags aspect)
    {
        _context = context;
        Width = width;
        Height = height;
        Format = format;

        var createInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = 1,
            ArrayLayers = 1,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };

        Vk api = context.Api;
        if (api.CreateImage(context.Device, &createInfo, null, out Image image) != Result.Success)
        {
            throw new InvalidOperationException("vkCreateImage failed");
        }
        Handle = image;

        MemoryRequirements requirements = VulkanAllocator.ImageRequirements(context, image, out bool dedicated);

        // Optimally tiled, so it never shares a block with a buffer.
        _allocation = context.Allocator.Allocate(
            requirements, MemoryPropertyFlags.DeviceLocalBit, linear: false, "an image",
            MemoryPoolClass.DeviceImages, dedicated, default, image);
        api.BindImageMemory(context.Device, image, _allocation.Memory, _allocation.Offset);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };
        if (api.CreateImageView(context.Device, &viewInfo, null, out ImageView view) != Result.Success)
        {
            throw new InvalidOperationException("vkCreateImageView failed");
        }
        View = view;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Vk api = _context.Api;
        api.DestroyImageView(_context.Device, View, null);
        api.DestroyImage(_context.Device, Handle, null);
        _context.Allocator.Free(_allocation);
    }
}

/// <summary>
/// Turns a Vulkan result into a failure that says something.
///
/// The backend used to ignore every result it got back. A lost device then
/// looked like nothing at all from inside: submits kept "succeeding", fence
/// waits returned immediately, and the client spun at a few frames a second
/// forever with no error anywhere - the fault was only visible to an external
/// overlay. A hang with no message is the worst possible failure mode, so every
/// submit, wait, acquire and present is checked, and a device loss is reported
/// where it happens rather than inferred later.
/// </summary>
internal static class VulkanResult
{
    /// <summary>Set once the device is gone, so the failure is reported once and not per call.</summary>
    public static volatile bool DeviceLost;

    /// <summary>Called with a description the moment something fails.</summary>
    public static Action<string>? OnFailure;

    /// <summary>
    /// Asked to describe a device loss after the fact, so the message can say
    /// what the GPU was doing rather than only that it stopped. Null when
    /// nothing on the device can answer.
    /// </summary>
    public static Func<string?>? DescribeDeviceLoss;

    public static void Check(Result result, string operation)
    {
        if (result == Result.Success || result == Result.SuboptimalKhr) return;

        bool lost = result is Result.ErrorDeviceLost;
        if (lost && DeviceLost) return;
        if (lost) DeviceLost = true;

        string message = lost
            ? operation + " reported the device was lost. The GPU driver aborted the work this " +
              "backend submitted; the session cannot continue."
            : operation + " failed with " + result;

        if (lost)
        {
            string? detail;
            try
            {
                detail = DescribeDeviceLoss?.Invoke();
            }
            catch (Exception e)
            {
                detail = "Describing the loss itself failed: " + e.Message;
            }
            if (!string.IsNullOrEmpty(detail)) message += " " + detail;
            message += " (" + VulkanMemory.LiveAllocations + " live device allocations.)";
        }

        OnFailure?.Invoke(message);
        throw new InvalidOperationException(message);
    }
}

internal static unsafe class VulkanMemory
{
    /// <summary>
    /// How many device allocations are currently outstanding.
    ///
    /// Vulkan caps this per device - commonly 4096 - and every buffer and image
    /// here owns its own allocation, so a world with a few hundred chunk meshes
    /// approaches the limit fast. Past it vkAllocateMemory starts failing, and an
    /// unchecked failure binds a null handle and faults the GPU rather than
    /// reporting anything. Counted so the failure can name its cause.
    /// </summary>
    private static int _liveAllocations;

    public static int LiveAllocations => Volatile.Read(ref _liveAllocations);

    public static void NoteAllocation() => Interlocked.Increment(ref _liveAllocations);

    public static void NoteFree() => Interlocked.Decrement(ref _liveAllocations);

    /// <summary>
    /// Allocates device memory, failing with a message that says what ran out.
    /// </summary>
    public static DeviceMemory Allocate(VulkanContext context, MemoryAllocateInfo allocateInfo, string what)
    {
        Result result = context.Api.AllocateMemory(context.Device, &allocateInfo, null, out DeviceMemory memory);
        if (result != Result.Success)
        {
            throw new InvalidOperationException(
                $"vkAllocateMemory failed for {what} with {result} after {LiveAllocations} live allocations " +
                $"({allocateInfo.AllocationSize} bytes requested)");
        }

        NoteAllocation();
        VulkanStats.NoteAllocation();
        return memory;
    }

    /// <summary>
    /// Picks a memory type satisfying both the resource's type mask and the
    /// requested properties.
    /// </summary>
    public static uint FindMemoryType(VulkanContext context, uint typeBits, MemoryPropertyFlags properties)
    {
        context.Api.GetPhysicalDeviceMemoryProperties(context.PhysicalDevice, out PhysicalDeviceMemoryProperties memory);

        for (uint i = 0; i < memory.MemoryTypeCount; i++)
        {
            bool typeAllowed = (typeBits & (1u << (int)i)) != 0;
            if (!typeAllowed) continue;

            MemoryPropertyFlags flags = memory.MemoryTypes[(int)i].PropertyFlags;
            if ((flags & properties) == properties) return i;
        }

        throw new InvalidOperationException($"no memory type with {properties}");
    }
}
