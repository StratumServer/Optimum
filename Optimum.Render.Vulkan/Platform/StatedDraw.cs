using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>
/// One draw of a program recorded natively from a <see cref="StatedRenderState" /> into an
/// explicit target: the generic native draw (VulkanClientPlatform.NativeStated.cs) and the GPU
/// tests' GL-shaped helpers both record through here, so the tests exercise the route the client's
/// unrecognised draws take.
///
/// What it states, and from where:
/// - target: <paramref name="framebufferId" />; every colour slot attached to it on the device is in
///   the pass (not the FrameBufferRef's own list: the OIT accumulation targets are attached to
///   Transparent at slots 3-5 without being in its ColorTextureIds, and a pass without them drops the
///   accumulated colour - 2026-09-17, water drew black until the slots came from the attachments);
///   the draw buffers stated for that target become per-attachment write masks (decision 4);
/// - blend, colour mask, depth, cull, line width, polygon mode, viewport and scissor: the stated state;
/// - textures: per sampler, the texture on the unit the program points it at (its SetSamplerUnit
///   mapping, else the sampler's declaration order), with the unit's standalone sampler if one is bound;
/// - the depth attachment of the target sampled with depth writes off is read in the read-only
///   layout (SamplesBoundDepth); sampled while written, the draw is refused.
/// </summary>
internal static class StatedDraw
{
    /// <summary>
    /// Records the draw. <paramref name="meshId" /> 0 is the fullscreen triangle;
    /// <paramref name="starts" /> is a pool's multi-draw. False with a reason: nothing was recorded.
    /// <paramref name="declared" /> names the pass the draw belongs to (its name, slots, reads and
    /// flags); without one the draw opens "Stated/&lt;target&gt;" over every attached slot.
    /// </summary>
    internal static bool Record(VulkanDevice device, StatedRenderState stated, int programId, int framebufferId,
        int meshId, int instances, int[]? starts, int[]? sizes, int groupCount, out string? refusal,
        PassDeclaration? declared = null)
    {
        refusal = null;
        RenderTargetFormats? all = device.NativeTargetFormats(framebufferId, uint.MaxValue);
        if (all == null) return Refused("framebuffer " + framebufferId + " does not exist", out refusal);
        int attached = all.ColorFormats.Length;
        uint slots = attached >= 32 ? uint.MaxValue : (1u << attached) - 1u;
        if (declared != null) slots &= declared.ColorSlots;

        int layoutId = meshId > 0 ? device.NativeMeshLayoutId(meshId) : MeshManager.EmptyLayoutId;
        if (layoutId < 0) return Refused("the mesh has no layout", out refusal);

        // Every sampler the program declares, from the unit it points at.
        List<string> names = device.SamplerNamesOf(programId);
        int depthTexture = device.NativeFramebufferDepthTexture(framebufferId);
        bool samplesBoundDepth = false;
        var reads = new int[names.Count];
        var units = new int[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            units[i] = device.NativeSamplerUnit(programId, names[i]);
            reads[i] = stated.TextureAt(units[i]);
            if (reads[i] != 0 && reads[i] == depthTexture)
            {
                if (stated.DepthWrite && stated.DepthTest)
                {
                    return Refused("it samples the depth attachment it writes", out refusal);
                }
                samplesBoundDepth = true;
            }
        }

        // A colour slot the draw samples while its draw buffer is off leaves the pass: GL reads it as
        // any texture (the composition writes Primary 0 and reads Primary 1). With its draw buffer on
        // it stays, and the device samples a copy of it (feedback).
        uint drawBuffers = stated.DrawBuffers(framebufferId);
        for (int slot = 0; slot < attached && slot < 32; slot++)
        {
            if (((drawBuffers >> slot) & 1) != 0 || ((slots >> slot) & 1) == 0) continue;
            int attachment = device.NativeFramebufferColorTexture(framebufferId, slot);
            if (attachment != 0 && Array.IndexOf(reads, attachment) >= 0) slots &= ~(1u << slot);
        }
        RenderTargetFormats? formats = device.NativeTargetFormats(framebufferId, slots);
        if (formats == null) return Refused("no formats for framebuffer " + framebufferId, out refusal);

        var blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
        for (int i = 0; i < blend.Length; i++) blend[i] = stated.AttachmentFor(framebufferId, i);

        var description = new NativePipelineDescription
        {
            ProgramId = programId,
            Blend = blend,
            DepthTest = stated.DepthTest,
            DepthWrite = stated.DepthWrite && !samplesBoundDepth,
            DepthCompare = stated.DepthCompare,
            Cull = stated.CullMode,
            Topology = meshId > 0 ? device.NativeMeshTopology(meshId) : PrimitiveTopology.TriangleList,
            PolygonMode = stated.Wireframe ? PolygonMode.Line : PolygonMode.Fill,
            LineWidth = stated.LineWidth,
            VertexLayoutId = layoutId,
            SamplesBoundDepth = samplesBoundDepth,
            Targets = formats,
        };
        NativePipeline? pipeline = device.RequestNativePipeline(description, out string error);
        if (pipeline == null) return Refused(error, out refusal);

        var textures = new NativeTexture[names.Count];
        for (int i = 0; i < names.Count; i++)
        {
            int sampler = stated.SamplerAt(units[i]);
            textures[i] = new NativeTexture(pipeline.Sampler(names[i]), reads[i],
                sampler != 0 ? device.NativeStandaloneSampler(sampler) : null);
        }

        int[] passReads = reads;
        if (declared != null && declared.Reads.Length > 0)
        {
            var union = new List<int>(declared.Reads);
            foreach (int read in reads) if (!union.Contains(read)) union.Add(read);
            passReads = union.ToArray();
        }
        Rect2D viewport = stated.Viewport;
        bool drawn = false;
        if (device.BeginNativePass(new NativePassDescription
        {
            Name = declared?.Name ?? "Stated/" + framebufferId,
            FramebufferId = framebufferId,
            ColorSlots = slots,
            Reads = passReads,
            TransientSlots = declared?.TransientSlots ?? 0,
            Flags = declared?.Flags ?? PassFlags.AllowSplit,
            Generic = true,
            ViewportX = viewport.Offset.X,
            ViewportY = viewport.Offset.Y,
            ViewportWidth = (int)viewport.Extent.Width,
            ViewportHeight = (int)viewport.Extent.Height,
            Scissor = stated.ScissorEnabled ? stated.Scissor : null,
        }))
        {
            drawn = meshId <= 0
                ? device.DrawNativeFullscreen(pipeline, textures)
                : starts != null
                    ? device.DrawNativeMeshMulti(pipeline, meshId, starts, sizes!, groupCount, textures)
                    : device.DrawNativeMeshInstanced(pipeline, meshId, instances, textures);
        }
        // The scope stays open: the next stated draw on the same target and slots coalesces into
        // this pass instead of ending the rendering scope and starting another; anything else
        // declares its own pass, which ends this one.
        device.EndNativePass(keepScope: true);
        return drawn;
    }

    private static bool Refused(string reason, out string? refusal)
    {
        refusal = reason;
        return false;
    }
}
