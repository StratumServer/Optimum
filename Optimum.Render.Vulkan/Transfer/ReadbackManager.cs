using System;
using Silk.NET.Vulkan;

// The Transfer/ folder follows the plan's layout; the namespace stays Core until
// the renderer is reorganised.
namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// A pending copy of GPU data into a slot's readback arena: valid to read once
/// the Frame timeline passed <see cref="FrameValue" />, and until the slot starts
/// its next frame.
/// </summary>
internal readonly record struct ReadbackTicket(VulkanBuffer Buffer, ulong Offset, ulong Size, ulong FrameValue);

/// <summary>
/// Readback inside a frame without ending it.
///
/// <see cref="CopyToHost" /> records a barrier and a copy into the current
/// slot's readback arena (a host-visible buffer, bump-allocated, reset when the
/// slot starts a frame). The caller then submits what the frame has recorded
/// with <see cref="FrameRing.SubmitPartial" />, which continues recording in the
/// same slot with every arena cursor kept, so the frame counter does not move and
/// uniform snapshots stay valid. A caller that needs the bytes now - a
/// screenshot, the parity dump - waits on that one timeline value; nothing else
/// waits.
/// </summary>
internal sealed unsafe class ReadbackManager : IDisposable
{
    public const ulong MinimumArenaSize = 1UL << 20;

    // Depth copies need a buffer offset that is a multiple of 4; 8 covers every
    // format the dump path reads.
    private const ulong OffsetAlignment = 8;

    private readonly VulkanContext _context;
    private readonly TextureManager _textures;
    private readonly FrameRing _frames;
    private readonly VulkanBuffer?[] _arenas;
    private readonly ulong[] _cursors;
    private bool _disposed;

    public ReadbackManager(VulkanContext context, TextureManager textures, FrameRing frames)
    {
        _context = context;
        _textures = textures;
        _frames = frames;
        _arenas = new VulkanBuffer?[frames.FramesInFlight];
        _cursors = new ulong[frames.FramesInFlight];
    }

    /// <summary>The slot's previous frame has finished and every ticket into its arena was read.</summary>
    public void BeginSlot(int slotIndex) => _cursors[slotIndex] = 0;

    /// <summary>Bytes the slot's arena can hold. Tests only.</summary>
    internal ulong ArenaCapacity(int slotIndex) => _arenas[slotIndex]?.Size ?? 0;

    /// <summary>
    /// Records a copy of level 0 of <paramref name="texture" /> (the given region
    /// and aspect) into the current slot's arena, and the transitions around it.
    /// The caller has closed any open rendering scope and submits afterwards.
    /// </summary>
    public ReadbackTicket CopyToHost(VulkanTexture texture, int x, int y, uint width, uint height,
        ImageAspectFlags aspect, ulong bytes)
    {
        FrameSlot slot = _frames.Current;
        CommandBuffer commandBuffer = slot.CommandBuffer;
        // The copy writes whole texels whatever the caller asked for, so the
        // reservation covers them all; only the requested bytes are handed out.
        ulong texel = (ulong)TextureDump.BytesPerTexel(texture.Format);
        ulong copied = (ulong)width * height * texel;
        VulkanBuffer arena = Reserve(slot.Index, Math.Max(bytes, copied), OffsetAlignmentFor(texel), out ulong offset);

        ImageLayout restore = texture.Layout;
        _textures.TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);

        var region = new BufferImageCopy
        {
            BufferOffset = offset,
            ImageSubresource = new ImageSubresourceLayers(aspect, 0, 0, 1),
            ImageOffset = new Offset3D(x, y, 0),
            ImageExtent = new Extent3D(width, height, 1),
        };
        _context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
            ImageLayout.TransferSrcOptimal, arena.Handle, 1, &region);

        if (restore != ImageLayout.Undefined) _textures.TransitionTexture(commandBuffer, texture, restore);

        return new ReadbackTicket(arena, offset, Math.Min(bytes, copied), slot.FrameValue);
    }

    /// <summary>
    /// A buffer offset legal for a copy of texels of <paramref name="texelBytes" />:
    /// a multiple of the texel size (VUID-vkCmdCopyImageToBuffer-srcImage-07975,
    /// 16 for RGBA32F) and of 4 for depth (-04053). Eight alone put an RGBA32F copy
    /// that followed an RGBA8 one at an illegal offset.
    /// </summary>
    internal static ulong OffsetAlignmentFor(ulong texelBytes)
    {
        ulong alignment = OffsetAlignment;
        if (texelBytes == 0) return alignment;
        while (alignment % texelBytes != 0) alignment += OffsetAlignment;
        return alignment;
    }

    /// <summary>Whether the copy has run, without waiting.</summary>
    public bool IsReady(ReadbackTicket ticket) => _frames.Timeline.FrameCompleted >= ticket.FrameValue;

    /// <summary>
    /// Waits for the ticket's timeline value (counted at the readback site) and
    /// copies the bytes out. The ticket's command buffer must have been submitted.
    /// </summary>
    public void WaitAndCopy(ReadbackTicket ticket, IntPtr destination)
    {
        _frames.Timeline.WaitForFrame(ticket.FrameValue, WaitSite.Readback);
        System.Buffer.MemoryCopy((void*)((nint)ticket.Buffer.Mapped + (nint)ticket.Offset),
            (void*)destination, (long)ticket.Size, (long)ticket.Size);
    }

    private VulkanBuffer Reserve(int slotIndex, ulong bytes, ulong alignment, out ulong offset)
    {
        VulkanBuffer? arena = _arenas[slotIndex];
        ulong aligned = (_cursors[slotIndex] + alignment - 1) / alignment * alignment;

        if (arena == null || aligned + bytes > arena.Size)
        {
            ulong size = arena == null ? MinimumArenaSize : arena.Size * 2;
            while (size < bytes) size *= 2;

            // A submitted copy may still be writing the old arena and a ticket may
            // still name it; the timeline retires it after both.
            if (arena != null) _frames.DeferDeletion(arena);
            arena = new VulkanBuffer(_context, size, BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            _arenas[slotIndex] = arena;
            aligned = 0;
        }

        offset = aligned;
        _cursors[slotIndex] = aligned + bytes;
        return arena;
    }

    /// <summary>The caller has waited for every signalled frame first.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = 0; i < _arenas.Length; i++)
        {
            _arenas[i]?.Dispose();
            _arenas[i] = null;
        }
    }
}
