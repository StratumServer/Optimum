using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    // -------------------------------------------------------------------- queries

    private QueryRing _queryRing = null!;
    private ReadbackManager _readbacks = null!;

    /// <summary>Whether occlusion queries count samples exactly. Tests only.</summary>
    internal bool PreciseOcclusionForTests => _context.Capabilities.OcclusionQueryPrecise;

    /// <summary>Occlusion query pools across every frame slot. Tests only.</summary>
    internal int OcclusionQueryPoolsForTests => _queryRing.PoolCount;

    public int CreateOcclusionQuery() => _queryRing.Create();

    public void BeginOcclusionQuery(int queryId)
    {
        if (!_frameActive || !_queryRing.CanBegin(queryId)) return;

        // The slot's pools are reset at frame start, before any scope opens, so
        // the query begins inside the scope the covered draw uses. Only a pool
        // created just now needs a reset here, and a reset has to happen outside
        // a scope: one restart per pool ever, never in steady state. If the
        // scope later closes before the query ends, the ring's scope hooks
        // suspend it and resume it in the next scope.
        CommandBuffer commandBuffer = Commands;
        if (_queryRing.NextNeedsPool)
        {
            _targets.EndRendering(commandBuffer);
            _queryRing.AddPool(commandBuffer);
        }
        // No scope is opened for it: a query begun outside one is suspended and starts in the next
        // scope that opens - the native pass of the draw it covers (QueryRing.OnScopeOpened).
        _queryRing.Begin(queryId, commandBuffer, _targets.RenderingActive);
    }

    public void EndOcclusionQuery(int queryId)
    {
        if (_frameActive) _queryRing.End(queryId, _frames.Current.FrameValue, Commands);
    }

    /// <summary>
    /// GL_QUERY_RESULT_AVAILABLE without any wait: true once the Frame timeline
    /// passed the command buffer that ended the query, a frame or two later.
    /// </summary>
    public bool IsQueryResultAvailable(int queryId) => _queryRing.IsResultAvailable(queryId);

    /// <summary>
    /// The samples the latest query counted. Never waits and never submits: the
    /// client polls availability first (sun glare does), and a result asked for
    /// early returns the previous query's count, or "all visible" if there was
    /// none - for a query that gates culling or glare, the cheap failure.
    /// </summary>
    public int GetQueryResult(int queryId) => _queryRing.GetResult(queryId);

    /// <summary>
    /// Submits everything the frame has recorded so far and keeps recording it in
    /// the same slot, so a readback queued next sees work the frame already issued.
    /// The open upload batch rides along, first. No wait, no new slot, no frame
    /// counter increment: arena cursors and uniform snapshots carry on.
    /// </summary>
    private ulong SubmitPartial()
    {
        _targets.EndRendering(Commands);
        _bindless?.Flush();
        ulong submitted = _frames.SubmitPartial();
        Checkpoint(Commands, CheckpointMarker.FrameBegin(_frameCounter));
        return submitted;
    }

    public void DeleteQuery(int queryId) => _queryRing.Delete(queryId);

    // ------------------------------------------------------------------- readback

    /// <summary>
    /// Reads back every texture OPTIMUM_DUMP_TEXTURES asked for. Debug only; see
    /// <see cref="TextureDump" /> for why it exists.
    /// </summary>
    private void DumpRequestedTextures()
    {
        foreach (int textureId in TextureDump.Take())
        {
            VulkanTexture? texture = _textures.Get(textureId);
            if (texture == null)
            {
                RenderTrace.Write("texture dump: no texture " + textureId);
                continue;
            }

            int width = (int)texture.Width;
            int height = (int)texture.Height;
            byte[] data = ReadBackLevel0(texture);

            bool bgra = texture.Format is Format.B8G8R8A8Unorm or Format.B8G8R8A8Srgb;
            bool written = TextureDump.Write(textureId, width, height, bgra, texture.Format, data);
            if (written) TextureDump.Complete(textureId);

            RenderTrace.Write("texture dump: " + textureId + " " + width + "x" + height +
                " " + texture.Format + " mips=" + texture.MipLevels + " -> " + (written ? "ok" : "failed"));
        }
    }

    /// <summary>
    /// Copies level 0 of a texture into host memory, raw texels in the image's own
    /// format, rows in memory order (GL order: the backend never flips Y). Inside
    /// a frame it goes through <see cref="ReadBack" />, so the frame stays open.
    /// Depth images are copied through their depth aspect.
    /// </summary>
    /// <summary>Level 0 of a texture through the dump path's readback. Tests only.</summary>
    internal byte[] ReadBackLevel0ForTests(int textureId) =>
        ReadBackLevel0(_textures.Get(textureId) ?? throw new ArgumentException("no texture " + textureId));

    /// <summary>One mip level of a texture through the in-frame readback. Tests only; a frame must be open.</summary>
    internal byte[] ReadBackLevelForTests(int textureId, uint mipLevel)
    {
        VulkanTexture texture = _textures.Get(textureId) ?? throw new ArgumentException("no texture " + textureId);
        if (!_frameActive) throw new InvalidOperationException("a mip readback needs an open frame");
        uint width = Math.Max(1, texture.Width >> (int)mipLevel);
        uint height = Math.Max(1, texture.Height >> (int)mipLevel);
        ulong bytes = (ulong)width * height * (ulong)BytesPerPixel(texture.Format);
        var data = new byte[bytes];
        _targets.FlushPendingClears(Commands, texture);
        _targets.EndRendering(Commands);
        ReadbackTicket ticket = _readbacks.CopyToHost(texture, 0, 0, width, height, texture.Aspect, bytes, mipLevel);
        SubmitPartial();
        fixed (byte* destination = data) _readbacks.WaitAndCopy(ticket, (IntPtr)destination);
        return data;
    }

    private byte[] ReadBackLevel0(VulkanTexture texture)
    {
        int width = (int)texture.Width;
        int height = (int)texture.Height;
        ulong bytes = (ulong)width * (ulong)height * (ulong)BytesPerPixel(texture.Format);
        ImageAspectFlags aspect = (texture.Aspect & ImageAspectFlags.DepthBit) != 0
            ? ImageAspectFlags.DepthBit
            : ImageAspectFlags.ColorBit;

        byte[] data = new byte[bytes];
        fixed (byte* destination = data)
        {
            ReadBack(texture, 0, 0, (uint)width, (uint)height, aspect, bytes, (IntPtr)destination);
        }
        return data;
    }

    /// <summary>
    /// The one readback path: screenshots, the texture dump and the parity dump.
    ///
    /// Inside a frame the open scope closes, the copy is recorded into the frame
    /// itself (into the slot's readback arena), the recorded part is submitted
    /// with <see cref="SubmitPartial" /> and the caller waits on that single Frame
    /// timeline value; the frame carries on in the same slot, so every draw after
    /// the read still reaches the screen. Between frames the copy is appended to
    /// the open upload batch (after every upload recorded so far), which is
    /// submitted on its own; the queue runs it after every frame already submitted,
    /// so waiting on its Transfer value is enough. Neither path waits for the
    /// whole device.
    /// </summary>
    private void ReadBack(VulkanTexture texture, int x, int y, uint width, uint height,
        ImageAspectFlags aspect, ulong bytes, IntPtr destination)
    {
        if (_frameActive)
        {
            _targets.FlushPendingClears(Commands, texture);
            _targets.EndRendering(Commands);
            ReadbackTicket ticket = _readbacks.CopyToHost(texture, x, y, width, height, aspect, bytes);
            SubmitPartial();
            _readbacks.WaitAndCopy(ticket, destination);
            return;
        }

        // The copy writes whole texels of the image's format whatever the caller
        // sized its destination for; the buffer holds them all, the caller gets its bytes.
        ulong copied = (ulong)width * height * (ulong)BytesPerPixel(texture.Format);
        ulong handed = Math.Min(bytes, copied);
        using var readback = new VulkanBuffer(_context, Math.Max(bytes, copied),
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, MemoryPoolClass.Staging);

        ImageLayout restore = texture.Layout;
        CommandBuffer commandBuffer = _uploads.BeginRecording(inlineInFrame: false);
        try
        {
            _textures.TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);

            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(aspect, 0, 0, 1),
                ImageOffset = new Offset3D(x, y, 0),
                ImageExtent = new Extent3D(width, height, 1),
            };
            _context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
                ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);

            if (restore != ImageLayout.Undefined) _textures.TransitionTexture(commandBuffer, texture, restore);
        }
        finally
        {
            _uploads.EndRecording();
        }
        ulong transferValue = _uploads.SubmitStandalone();
        _frames.Timeline.WaitForTransfer(transferValue, WaitSite.Readback);

        System.Buffer.MemoryCopy((void*)readback.Mapped, (void*)destination, (long)bytes, (long)handed);
    }

    private void RecordGlInternalFormat(int textureId, int glInternalFormat)
    {
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture != null) texture.GlInternalFormat = glInternalFormat;
    }

    /// <summary>
    /// The parity dump's readback (<see cref="OptimumParityDump" />): level 0 in
    /// the representation glGetTexImage produces on the OpenGL path, decoded by
    /// <see cref="TextureDump.ToParityReadback" />. Debug only.
    /// </summary>
    public OptimumTextureReadback? ReadTextureForParity(int textureId)
    {
        if (!_frameActive) return null;
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture == null || texture.Cube || texture.Layers > 1) return null;

        byte[] data = ReadBackLevel0(texture);
        int glInternalFormat = texture.GlInternalFormat != 0
            ? texture.GlInternalFormat
            : TextureDump.GlInternalFormatOf(texture.Format);
        OptimumTextureReadback? readback = TextureDump.ToParityReadback(texture.Format, glInternalFormat,
            (int)texture.Width, (int)texture.Height, data);
        RenderTrace.Write("parity dump: texture " + textureId + " " + texture.Width + "x" + texture.Height +
            " " + texture.Format + " -> " + (readback != null ? "ok" : "undecodable"));
        return readback;
    }

    /// <summary>
    /// Bytes per texel for the formats the dump path is expected to see.
    /// Shared with <see cref="TextureDump.Write" />'s decode switch so the
    /// readback size and the reader always agree on the stride.
    /// </summary>
    private static int BytesPerPixel(Format format) => TextureDump.BytesPerTexel(format);

    /// <summary>Colour attachment 0 of an explicit target (the default one for <see cref="PassDeclaration.DefaultFramebuffer" />).</summary>
    internal void ReadFramebufferColor(int framebufferId, int x, int y, int width, int height, IntPtr destination)
    {
        EndNativePass();
        ReadFramebufferColor(_targets.Get(ResolveNativeFramebuffer(framebufferId)), x, y, width, height, destination);
    }

    private void ReadFramebufferColor(VulkanFramebuffer? target, int x, int y, int width, int height, IntPtr destination)
    {
        if (destination == IntPtr.Zero || width <= 0 || height <= 0) return;
        if (target == null) return;

        VulkanTexture? texture = _textures.Get(target.Color[0].TextureId);
        if (texture == null) return;

        ReadBack(texture, x, y, (uint)width, (uint)height, ImageAspectFlags.ColorBit,
            (ulong)width * (ulong)height * 4, destination);
    }

    /// <summary>
    /// The format of the default colour target, so the platform above knows the
    /// channel order the readback hands back rather than assuming one.
    /// </summary>
    internal Format DefaultColorFormat => DefaultColorTexture()?.Format ?? Format.R8G8B8A8Unorm;
}
