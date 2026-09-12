using System;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan;

/// <summary>
/// Seam D2: running a vendor upscaler's own command recording inside one of our
/// frames. Today that is DLSS Super Resolution; the shape is deliberately the
/// one FSR and XeSS will need too - resources tagged from our textures, the
/// barriers placed by <see cref="BarrierBatcher" />, the feature's lifetime on
/// the frame timeline, and our own cached state invalidated afterwards.
///
/// <b>What NGX leaves behind on our command buffer.</b> EvaluateFeature records
/// its own compute dispatches: it binds compute pipelines, its own descriptor
/// sets and push constants at the COMPUTE bind point, and may set dynamic state.
/// It does not touch a render pass (it cannot - a transition inside one is
/// illegal, and this seam closes any open scope before calling). Of Optimum's
/// own cached state:
/// <list type="bullet">
/// <item>the <i>graphics</i> pipeline and descriptor sets are rebound by every
/// draw (<c>BindPipeline</c> is unconditional per draw), so nothing to do;</item>
/// <item>the <see cref="DynamicStateCache" /> is the one thing carried across
/// draws, so it is invalidated here and the next draw re-records viewport,
/// scissor, depth and blend state;</item>
/// <item>image layouts NGX changed internally are restored by NGX before it
/// returns (DLSS Programming Guide §3.4: "always transitions buffers back to
/// these known states"), which is what lets our
/// <see cref="ResourceStateTracker" /> keep describing them as the layouts we
/// put them in.</item>
/// </list>
/// </summary>
public sealed unsafe partial class VulkanDevice
{
    /// <summary>
    /// A texture for an upscaler seam: the backend's ordinary image plus, when
    /// <paramref name="storage" /> is set, <c>VK_IMAGE_USAGE_STORAGE_BIT</c>,
    /// which every upscaler's output must carry.
    ///
    /// Named in Vulkan formats rather than GL tokens because the formats an
    /// upscaler wants (RG16F motion vectors, R32F depth) have no useful GL-token
    /// route through the client-facing API.
    /// </summary>
    internal int CreateUpscaleTexture(
        int width, int height, Format format, bool storage, IntPtr pixels = default, int bytesPerPixel = 0)
    {
        int id = _textures.Create((uint)width, (uint)height, format,
            extraUsage: storage ? ImageUsageFlags.StorageBit : 0);
        RecordGlInternalFormat(id, TextureDump.GlInternalFormatOf(format));
        if (pixels != IntPtr.Zero && bytesPerPixel > 0)
        {
            _textures.Upload(id, 0, 0, 0, (uint)width, (uint)height, pixels, bytesPerPixel);
        }
        return id;
    }

    /// <summary>
    /// The three handles a vendor runtime is initialised on. Zero on all three
    /// before <see cref="Initialize" /> has succeeded, which is the only state a
    /// caller has to distinguish.
    /// </summary>
    internal void UpscalerHandles(out IntPtr instance, out IntPtr physicalDevice, out IntPtr deviceHandle)
    {
        if (_context == null)
        {
            instance = IntPtr.Zero;
            physicalDevice = IntPtr.Zero;
            deviceHandle = IntPtr.Zero;
            return;
        }
        instance = (IntPtr)_context.Instance.Handle;
        physicalDevice = (IntPtr)_context.PhysicalDevice.Handle;
        deviceHandle = (IntPtr)_context.Device.Handle;
    }

    /// <summary>
    /// Creates a DLSS feature on this frame's command buffer. The frame must be
    /// open: NGX records initialisation work into the buffer it is given.
    /// </summary>
    internal NgxResult CreateDlssFeature(in NgxDlssSettings settings, out NgxDlssFeature? feature)
    {
        feature = null;
        if (!_frameActive) return NgxResult.FailNotInitialized;

        // NGX records outside any rendering scope.
        _targets.FlushAllPendingClears(Commands);
        _targets.EndRendering(Commands);

        NgxResult result = NgxDlssFeature.Create(
            (IntPtr)_context.Device.Handle, Commands, settings, out feature);

        // Creation records commands of NGX's own too, so the same restore applies.
        _dynamicState.Invalidate();
        return result;
    }

    /// <summary>
    /// Hands a feature to the frame ring: it is released once every frame that
    /// could have named its handle has completed. Never <c>Dispose</c> a feature
    /// directly from the render thread - an evaluate recorded this frame is still
    /// in flight.
    /// </summary>
    internal void RetireDlssFeature(NgxDlssFeature feature) => _frames.DeferDeletion(feature);

    /// <summary>
    /// Waits for every submitted frame and destroys everything the retire queue
    /// holds. Teardown only, and specifically the step that has to happen between
    /// retiring an upscaler's feature and shutting the vendor runtime down:
    /// <c>NVSDK_NGX_VULKAN_Shutdown1</c> tears down the state a live feature
    /// handle names, so releasing the feature afterwards is a use-after-free
    /// inside the driver (it takes the process down; measured 2026-09-12).
    /// </summary>
    /// <para>A frame that is still open when this runs is abandoned by the drain
    /// (its recording is thrown away and its Frame value counts as reached, because
    /// nothing will ever submit it), so the device must stop calling itself
    /// in-frame: every later Commands access would name a command buffer that went
    /// back to the initial state.</para>
    internal int DrainDeferredDeletions()
    {
        int collected = _frames.DrainRetirements();
        _frameActive = false;
        return collected;
    }

    /// <summary>
    /// Point-upscales one depth image into another, for the display-resolution depth the
    /// late 3D overlays test against after an upscale.
    ///
    /// NEAREST is not a quality choice: filtering a depth buffer invents depths that lie
    /// on neither surface, and Vulkan refuses a linear blit of a depth format outright.
    /// The blit needs <c>VK_FORMAT_FEATURE_BLIT_DST_BIT</c> on the destination format,
    /// which is not guaranteed for depth formats by the specification; it is present on
    /// the drivers that can run this path at all (DLSS is NVIDIA-only), and a driver
    /// without it gets false - and, from the caller, one log line - rather than a
    /// broken frame.
    /// </summary>
    internal bool UpscaleDepthNearest(int sourceTexture, int destinationTexture)
    {
        if (!_frameActive) return false;
        VulkanTexture? source = _textures.Get(sourceTexture);
        VulkanTexture? destination = _textures.Get(destinationTexture);
        if (source == null || destination == null) return false;
        if (!SupportsDepthBlitDestination(destination.Format)) return false;

        CommandBuffer commandBuffer = Commands;
        _targets.FlushAllPendingClears(commandBuffer);
        _targets.EndRendering(commandBuffer);

        _textures.Require(_barriers, commandBuffer, source, ResourceUsage.TransferSrc);
        _textures.Require(_barriers, commandBuffer, destination, ResourceUsage.TransferDst);
        _barriers.Flush(commandBuffer);

        ImageBlit region = default;
        region.SrcSubresource = new ImageSubresourceLayers(source.Aspect, 0, 0, 1);
        region.DstSubresource = new ImageSubresourceLayers(destination.Aspect, 0, 0, 1);
        region.SrcOffsets.Element1 = new Offset3D((int)source.Width, (int)source.Height, 1);
        region.DstOffsets.Element1 = new Offset3D((int)destination.Width, (int)destination.Height, 1);
        _context.Api.CmdBlitImage(commandBuffer,
            source.Image, ImageLayout.TransferSrcOptimal,
            destination.Image, ImageLayout.TransferDstOptimal,
            1, &region, Filter.Nearest);

        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("depth upscale src=" + sourceTexture + " dst=" + destinationTexture +
                " " + source.Width + "x" + source.Height + " -> " + destination.Width + "x" + destination.Height);
        }
        return true;
    }

    /// <summary>
    /// Clears a depth image to the far plane, for the display-resolution depth of a
    /// frame whose upscale of it was refused.
    ///
    /// Not a quality path and not a substitute for the point upscale: it is what
    /// keeps the buffer <i>defined</i>. A depth image the upscale never wrote holds
    /// undefined content on the frame it was created and the previous frame's
    /// silhouettes after that, and the late 3D overlays test against it - so they
    /// would either vanish behind depth that belongs to nothing or show through where
    /// the world occludes them. Far plane costs them their occlusion, which is the
    /// one degradation that is not wrong pixels.
    ///
    /// <c>vkCmdClearDepthStencilImage</c> rather than the blit, because it needs only
    /// TRANSFER_DST image usage - which this target has - and no format feature the
    /// refusal already told us is missing.
    /// </summary>
    internal bool ClearDepthImageToFar(int destinationTexture)
    {
        if (!_frameActive) return false;
        VulkanTexture? destination = _textures.Get(destinationTexture);
        if (destination == null) return false;

        CommandBuffer commandBuffer = Commands;
        _targets.FlushAllPendingClears(commandBuffer);
        _targets.EndRendering(commandBuffer);

        _textures.Require(_barriers, commandBuffer, destination, ResourceUsage.TransferDst);
        _barriers.Flush(commandBuffer);

        var value = new ClearDepthStencilValue(1f, 0);
        var range = new ImageSubresourceRange(destination.Aspect, 0, 1, 0, 1);
        _context.Api.CmdClearDepthStencilImage(commandBuffer,
            destination.Image, ImageLayout.TransferDstOptimal, &value, 1, &range);

        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("depth upscale refused, cleared dst=" + destinationTexture + " to the far plane");
        }
        return true;
    }

    private int _depthBlitDestinationSupport;

    /// <summary>
    /// Whether this device can be the destination of a depth blit, asked once and
    /// remembered. A refusal is logged once, not once a frame.
    /// </summary>
    private bool SupportsDepthBlitDestination(Format format)
    {
        if (_depthBlitDestinationSupport != 0) return _depthBlitDestinationSupport > 0;
        _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, format, out FormatProperties properties);
        bool supported = (properties.OptimalTilingFeatures & FormatFeatureFlags.BlitDstBit) != 0;
        _depthBlitDestinationSupport = supported ? 1 : -1;
        return supported;
    }

    /// <summary>
    /// Runs DLSS on this frame's command buffer: colour, depth and motion
    /// vectors at render resolution in, the display-resolution output written in
    /// place.
    ///
    /// The barriers are ours, because NGX places none: the three inputs move to
    /// SHADER_READ_ONLY_OPTIMAL readable from compute
    /// (<see cref="ResourceUsage.SampleExternal" />) and the output to GENERAL as
    /// a storage write (<see cref="ResourceUsage.StorageWriteExternal" />), all in
    /// one <c>vkCmdPipelineBarrier2</c>. The tracker then describes the output as
    /// written by a compute storage write, so whatever reads it next - a blit, a
    /// readback, the composition pass - barriers against that write correctly.
    /// </summary>
    internal NgxResult EvaluateDlss(
        NgxDlssFeature feature,
        int colorTexture, int depthTexture, int motionTexture, int outputTexture,
        in NgxDlssEvaluation frame)
    {
        if (feature == null || !feature.IsValid) return NgxResult.FailFeatureNotFound;
        if (!_frameActive) return NgxResult.FailNotInitialized;

        VulkanTexture? color = _textures.Get(colorTexture);
        VulkanTexture? depth = _textures.Get(depthTexture);
        VulkanTexture? motion = _textures.Get(motionTexture);
        VulkanTexture? output = _textures.Get(outputTexture);
        if (color == null || depth == null || motion == null || output == null)
        {
            return NgxResult.FailMissingInput;
        }

        CommandBuffer commandBuffer = Commands;

        // A transition cannot be recorded inside a rendering scope, and NGX's
        // dispatches cannot run inside one either.
        _targets.FlushAllPendingClears(commandBuffer);
        _targets.EndRendering(commandBuffer);

        _textures.Require(_barriers, commandBuffer, color, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commandBuffer, depth, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commandBuffer, motion, ResourceUsage.SampleExternal);
        _textures.Require(_barriers, commandBuffer, output, ResourceUsage.StorageWriteExternal);
        _barriers.Flush(commandBuffer);

        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("dlss evaluate " + feature.Settings +
                " color=" + colorTexture + " depth=" + depthTexture +
                " mv=" + motionTexture + " out=" + outputTexture);
        }

        NgxResult result = feature.Evaluate(
            commandBuffer,
            NgxResourceVk.Texture(color, readWrite: false),
            NgxResourceVk.Texture(output, readWrite: true),
            NgxResourceVk.Texture(depth, readWrite: false),
            NgxResourceVk.Texture(motion, readWrite: false),
            frame);

        // Seam D2's restore: see the class comment for what NGX leaves behind.
        _dynamicState.Invalidate();
        return result;
    }
}
