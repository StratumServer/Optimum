using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan;

/// <summary>
/// The passthrough comparison upscaler's whole GPU cost: one magnifying colour blit
/// from the render-resolution scene colour to the display-resolution upscale target.
///
/// It is deliberately the plainest thing that can occupy a vendor upscaler's slot.
/// Nothing here is reconstruction, nothing is temporal, and nothing touches a vendor
/// runtime: what the frame shows is the render-resolution image magnified, so whatever
/// shimmer remains was produced before the upscaler was ever asked anything.
/// <c>vkCmdBlitImage</c> rather than a fullscreen pass for the same reason the depth
/// upscale uses it - it needs no pipeline, no descriptor set and no shader of ours, so
/// the comparison cannot accidentally measure our own blit shader.
/// </summary>
public sealed unsafe partial class VulkanDevice
{
    /// <summary>
    /// Magnifies one colour image into another. <paramref name="linear" /> selects
    /// <c>VK_FILTER_LINEAR</c> (the default the setting ships with) over
    /// <c>VK_FILTER_NEAREST</c>, which shows the render grid itself.
    ///
    /// False, never an exception, when the frame is closed, a texture is missing, or
    /// this driver cannot blit between these formats - the caller then stands the
    /// upscaler down exactly as every other upscaler failure does. A linear blit
    /// additionally needs <c>SAMPLED_IMAGE_FILTER_LINEAR</c> on the source format,
    /// which the specification does not guarantee for every format, so that is asked
    /// before it is used and nearest is the fallback rather than a refusal.
    /// </summary>
    internal bool BlitColorScaled(int sourceTexture, int destinationTexture, bool linear)
    {
        if (!_frameActive) return false;
        VulkanTexture? source = _textures.Get(sourceTexture);
        VulkanTexture? destination = _textures.Get(destinationTexture);
        if (source == null || destination == null) return false;
        if (!SupportsBlit(source.Format, FormatFeatureFlags.BlitSrcBit)) return false;
        if (!SupportsBlit(destination.Format, FormatFeatureFlags.BlitDstBit)) return false;

        bool filterLinear = linear && SupportsBlit(source.Format, FormatFeatureFlags.SampledImageFilterLinearBit);

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
            1, &region, filterLinear ? Filter.Linear : Filter.Nearest);

        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("passthrough upscale src=" + sourceTexture + " dst=" + destinationTexture +
                " " + source.Width + "x" + source.Height + " -> " + destination.Width + "x" + destination.Height +
                " filter=" + (filterLinear ? "linear" : "nearest"));
        }
        return true;
    }

    /// <summary>
    /// Whether this device's optimal tiling supports a format feature, asked of the
    /// driver each time but answered from one cheap query; the results are not cached
    /// because this runs once per frame at most, unlike a per-draw path.
    /// </summary>
    private bool SupportsBlit(Format format, FormatFeatureFlags feature)
    {
        _context.Api.GetPhysicalDeviceFormatProperties(_context.PhysicalDevice, format, out FormatProperties properties);
        return (properties.OptimalTilingFeatures & feature) != 0;
    }
}
