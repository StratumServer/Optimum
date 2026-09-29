using System;
using System.Threading;
using Silk.NET.Vulkan;
using Buffer = Silk.NET.Vulkan.Buffer;
using System.Collections.Generic;

namespace Optimum.Render.Vulkan.Core;

// Silk.NET.Vulkan.Buffer collides with System.Buffer.

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

    /// <summary>The highest id issued so far (0 before the first).</summary>
    public static ulong Highest => (ulong)Interlocked.Read(ref _next);
}

/// <summary>A device buffer with its backing memory.</summary>
internal sealed unsafe class VulkanBuffer : IDisposable
{
    private readonly VulkanContext _context;
    private bool _disposed;

    public Buffer Handle { get; }
    public ulong Size { get; }

    /// <summary>What the buffer was created for; the barriers around a staged copy name these uses.</summary>
    public BufferUsageFlags Usage { get; }

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
        Usage = usage;

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

/// <summary>
/// Which format a storage image is created in. A compute pass names the format it
/// wants (the AO working term wants R8_UNORM, the prefiltered depth R32F); a device
/// that cannot use that format as a storage image and sample it gets the first
/// wider format of the same kind that it can, ending in RGBA8 for unsigned
/// normalised formats and RGBA32F for float ones (the formats Vulkan guarantees
/// storage support for). A shader writing the first channel of the wider format
/// reads the same value back, so a fallback costs memory, never correctness.
///
/// Pure: the feature lookup is passed in, so the choice is testable without a device.
/// </summary>
internal static class StorageFormats
{
    /// <summary>What a storage image must support: storage writes and sampling.</summary>
    public const FormatFeatureFlags Required = FormatFeatureFlags.StorageImageBit | FormatFeatureFlags.SampledImageBit;

    /// <summary>The candidates for <paramref name="requested" />, the requested format first.</summary>
    public static IReadOnlyList<Format> CandidatesFor(Format requested) => requested switch
    {
        Format.R8Unorm => new[] { Format.R8Unorm, Format.R8G8Unorm, Format.R8G8B8A8Unorm },
        Format.R8G8Unorm => new[] { Format.R8G8Unorm, Format.R8G8B8A8Unorm },
        Format.R16Unorm => new[] { Format.R16Unorm, Format.R16G16B16A16Unorm, Format.R8G8B8A8Unorm },
        Format.R16Sfloat => new[] { Format.R16Sfloat, Format.R32Sfloat, Format.R16G16B16A16Sfloat, Format.R32G32B32A32Sfloat },
        Format.R32Sfloat => new[] { Format.R32Sfloat, Format.R32G32B32A32Sfloat },
        Format.R16G16Sfloat => new[] { Format.R16G16Sfloat, Format.R16G16B16A16Sfloat, Format.R32G32B32A32Sfloat },
        Format.R16G16B16A16Sfloat => new[] { Format.R16G16B16A16Sfloat, Format.R32G32B32A32Sfloat },
        Format.R8G8B8A8Unorm => new[] { Format.R8G8B8A8Unorm },
        _ => new[] { requested, Format.R8G8B8A8Unorm },
    };

    /// <summary>
    /// The first candidate whose optimal-tiling features include <see cref="Required" />;
    /// RGBA8 when none does (every Vulkan device supports it as a storage image).
    /// </summary>
    public static Format Choose(Format requested, Func<Format, FormatFeatureFlags> optimalFeatures)
    {
        foreach (Format candidate in CandidatesFor(requested))
        {
            if ((optimalFeatures(candidate) & Required) == Required) return candidate;
        }
        return Format.R8G8B8A8Unorm;
    }

    /// <summary>Whether a colour attachment usage may be added: the format must support it.</summary>
    public static bool SupportsColorAttachment(FormatFeatureFlags features) =>
        (features & FormatFeatureFlags.ColorAttachmentBit) != 0;
}

/// <summary>
/// The values poison mode (OPTIMUM_VULKAN_POISON=1) writes into fresh resources.
///
/// OpenGL and Vulkan both leave new storage undefined, but in practice GL
/// drivers hand out zeroed memory and Vulkan allocators hand out whatever the
/// previous tenant left. A read of never-written content therefore "works" on
/// one backend and flickers on the other. Poison makes such a read loud and
/// identical every frame: NaN for float formats, magenta (alpha 1) for
/// normalised and sRGB colour, 0xDEADBEEF for integer formats and host memory,
/// 0.5 for depth.
/// </summary>
internal static unsafe class VulkanPoison
{
    public const uint Word = 0xDEADBEEF;
    public const float Depth = 0.5f;

    public static bool IsCompressed(Format format) =>
        format.ToString().Contains("Block", StringComparison.Ordinal);

    public static bool IsFloat(Format format)
    {
        string name = format.ToString();
        return name.Contains("Sfloat", StringComparison.Ordinal) || name.Contains("Ufloat", StringComparison.Ordinal);
    }

    public static bool IsInteger(Format format)
    {
        string name = format.ToString();
        return name.Contains("Uint", StringComparison.Ordinal) || name.Contains("Sint", StringComparison.Ordinal);
    }

    public static ClearColorValue ColorFor(Format format)
    {
        var value = new ClearColorValue();
        if (IsFloat(format))
        {
            value.Float32_0 = float.NaN;
            value.Float32_1 = float.NaN;
            value.Float32_2 = float.NaN;
            value.Float32_3 = float.NaN;
        }
        else if (IsInteger(format))
        {
            // Uint and Sint clears read the same union bits.
            value.Uint32_0 = Word;
            value.Uint32_1 = Word;
            value.Uint32_2 = Word;
            value.Uint32_3 = Word;
        }
        else
        {
            value.Float32_0 = 1f;
            value.Float32_1 = 0f;
            value.Float32_2 = 1f;
            value.Float32_3 = 1f;
        }
        return value;
    }

    /// <summary>Writes 0xDEADBEEF as little-endian words over the whole range, a partial word at the tail.</summary>
    public static void FillHostMemory(IntPtr memory, ulong size)
    {
        byte* bytes = (byte*)memory;
        ulong words = size / 4;
        uint* wordPointer = (uint*)bytes;
        for (ulong i = 0; i < words; i++) wordPointer[i] = Word;
        for (ulong i = words * 4; i < size; i++) bytes[i] = (byte)(Word >> (int)(8 * (i % 4)));
    }
}
