using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Graph;

/// <summary>
/// What a command does with an image. Layout, pipeline stage and access all
/// derive from this (<see cref="UsageState.For" />), never from the layout
/// alone: two uses can share a layout and still differ in stage (a depth
/// attachment read only by the depth test versus one also sampled by the
/// fragment shader).
/// </summary>
public enum ResourceUsage
{
    /// <summary>Colour attachment, written without reading the destination.</summary>
    ColorWrite,
    /// <summary>Colour attachment with blending: the destination is read and written.</summary>
    ColorBlend,
    /// <summary>Depth attachment with writes on.</summary>
    DepthWrite,
    /// <summary>Depth attachment with writes off, read by the depth test only.</summary>
    DepthReadOnly,
    /// <summary>Depth attachment with writes off, also sampled by the fragment shader.</summary>
    DepthReadOnlySampled,
    /// <summary>Sampled by a fragment shader.</summary>
    SampleFragment,
    /// <summary>Sampled by a vertex shader.</summary>
    SampleVertex,
    /// <summary>Read as a storage image.</summary>
    StorageRead,
    /// <summary>Source of a copy or blit.</summary>
    TransferSrc,
    /// <summary>Destination of a copy, blit or clear.</summary>
    TransferDst,
    /// <summary>Handed to vkQueuePresentKHR.</summary>
    PresentSrc,
}

/// <summary>
/// The layout, stage and access of one <see cref="ResourceUsage" />: the
/// destination side of a barrier into that usage.
/// </summary>
internal readonly record struct UsageState(ImageLayout Layout, PipelineStageFlags2 Stage, AccessFlags2 Access)
{
    /// <summary>The fragment test stages a depth attachment is used at.</summary>
    public const PipelineStageFlags2 DepthTests =
        PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit;

    /// <summary>Every access bit that writes.</summary>
    public const AccessFlags2 WriteAccessMask =
        AccessFlags2.ColorAttachmentWriteBit | AccessFlags2.DepthStencilAttachmentWriteBit |
        AccessFlags2.TransferWriteBit | AccessFlags2.ShaderStorageWriteBit | AccessFlags2.MemoryWriteBit;

    /// <summary>
    /// The usage table. <paramref name="depth" /> is the image's aspect: an
    /// attachment usage on a depth image resolves to its depth form and a depth
    /// attachment usage on a colour image to its colour form, so a caller that
    /// only knows "attachment" gets the right one. Sampling keeps
    /// SHADER_READ_ONLY_OPTIMAL for both aspects, because that is the layout the
    /// descriptor writes name.
    /// </summary>
    public static UsageState For(ResourceUsage usage, bool depth) => Normalise(usage, depth) switch
    {
        ResourceUsage.ColorWrite => new(ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit),
        ResourceUsage.ColorBlend => new(ImageLayout.ColorAttachmentOptimal,
            PipelineStageFlags2.ColorAttachmentOutputBit,
            AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit),
        ResourceUsage.DepthWrite => new(ImageLayout.DepthAttachmentOptimal, DepthTests,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit),
        ResourceUsage.DepthReadOnly => new(ImageLayout.DepthReadOnlyOptimal, DepthTests,
            AccessFlags2.DepthStencilAttachmentReadBit),
        ResourceUsage.DepthReadOnlySampled => new(ImageLayout.DepthReadOnlyOptimal,
            DepthTests | PipelineStageFlags2.FragmentShaderBit,
            AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.ShaderSampledReadBit),
        ResourceUsage.SampleFragment => new(ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.FragmentShaderBit, AccessFlags2.ShaderSampledReadBit),
        ResourceUsage.SampleVertex => new(ImageLayout.ShaderReadOnlyOptimal,
            PipelineStageFlags2.VertexShaderBit, AccessFlags2.ShaderSampledReadBit),
        ResourceUsage.StorageRead => new(ImageLayout.General,
            PipelineStageFlags2.FragmentShaderBit | PipelineStageFlags2.ComputeShaderBit,
            AccessFlags2.ShaderStorageReadBit),
        ResourceUsage.TransferSrc => new(ImageLayout.TransferSrcOptimal,
            PipelineStageFlags2.TransferBit, AccessFlags2.TransferReadBit),
        ResourceUsage.TransferDst => new(ImageLayout.TransferDstOptimal,
            PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit),
        ResourceUsage.PresentSrc => new(ImageLayout.PresentSrcKhr,
            PipelineStageFlags2.BottomOfPipeBit, AccessFlags2.None),
        _ => throw new System.ArgumentOutOfRangeException(nameof(usage), usage, null),
    };

    /// <summary>
    /// The write a usage performs, which the next barrier must make available.
    /// An attachment is written by its store op even with writes off: a
    /// read-only depth attachment is still stored, and synchronization
    /// validation reports the next transition as write-after-write unless the
    /// barrier names that write (2026-09-11).
    /// </summary>
    public static (PipelineStageFlags2 Stage, AccessFlags2 Access) WriteOf(ResourceUsage usage, bool depth) =>
        Normalise(usage, depth) switch
        {
            ResourceUsage.ColorWrite or ResourceUsage.ColorBlend =>
                (PipelineStageFlags2.ColorAttachmentOutputBit, AccessFlags2.ColorAttachmentWriteBit),
            ResourceUsage.DepthWrite or ResourceUsage.DepthReadOnly or ResourceUsage.DepthReadOnlySampled =>
                (DepthTests, AccessFlags2.DepthStencilAttachmentWriteBit),
            ResourceUsage.TransferDst => (PipelineStageFlags2.TransferBit, AccessFlags2.TransferWriteBit),
            _ => (PipelineStageFlags2.None, AccessFlags2.None),
        };

    /// <summary>
    /// The usage a layout stands for, for callers that still speak in layouts
    /// (tests, a readback restoring what it found). Attachment layouts map to
    /// the widest use of that layout: blending for colour, sampled for
    /// read-only depth.
    /// </summary>
    public static ResourceUsage ForLayout(ImageLayout layout) => layout switch
    {
        ImageLayout.ShaderReadOnlyOptimal => ResourceUsage.SampleFragment,
        ImageLayout.ColorAttachmentOptimal => ResourceUsage.ColorBlend,
        ImageLayout.DepthAttachmentOptimal or ImageLayout.DepthStencilAttachmentOptimal => ResourceUsage.DepthWrite,
        ImageLayout.DepthReadOnlyOptimal or ImageLayout.DepthStencilReadOnlyOptimal => ResourceUsage.DepthReadOnlySampled,
        ImageLayout.TransferSrcOptimal => ResourceUsage.TransferSrc,
        ImageLayout.TransferDstOptimal => ResourceUsage.TransferDst,
        ImageLayout.PresentSrcKhr => ResourceUsage.PresentSrc,
        ImageLayout.General => ResourceUsage.StorageRead,
        _ => throw new System.ArgumentOutOfRangeException(nameof(layout), layout, "no usage stands for this layout"),
    };

    private static ResourceUsage Normalise(ResourceUsage usage, bool depth) => (usage, depth) switch
    {
        (ResourceUsage.ColorWrite, true) => ResourceUsage.DepthWrite,
        (ResourceUsage.ColorBlend, true) => ResourceUsage.DepthWrite,
        (ResourceUsage.DepthWrite, false) => ResourceUsage.ColorBlend,
        (ResourceUsage.DepthReadOnly, false) => ResourceUsage.ColorBlend,
        (ResourceUsage.DepthReadOnlySampled, false) => ResourceUsage.ColorBlend,
        _ => usage,
    };
}

/// <summary>
/// The readers a buffer can have, derived from its usage flags. Buffers have no
/// layout, so the barriers around a staged copy name every use the buffer was
/// created for instead of ALL_COMMANDS.
/// </summary>
internal static class BufferUsageState
{
    public static (PipelineStageFlags2 Stage, AccessFlags2 Access) UsesOf(BufferUsageFlags usage)
    {
        PipelineStageFlags2 stage = PipelineStageFlags2.None;
        AccessFlags2 access = AccessFlags2.None;
        if ((usage & BufferUsageFlags.VertexBufferBit) != 0)
        {
            stage |= PipelineStageFlags2.VertexAttributeInputBit;
            access |= AccessFlags2.VertexAttributeReadBit;
        }
        if ((usage & BufferUsageFlags.IndexBufferBit) != 0)
        {
            stage |= PipelineStageFlags2.IndexInputBit;
            access |= AccessFlags2.IndexReadBit;
        }
        if ((usage & BufferUsageFlags.UniformBufferBit) != 0)
        {
            stage |= PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit;
            access |= AccessFlags2.UniformReadBit;
        }
        if ((usage & BufferUsageFlags.StorageBufferBit) != 0)
        {
            stage |= PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.FragmentShaderBit |
                     PipelineStageFlags2.ComputeShaderBit;
            access |= AccessFlags2.ShaderStorageReadBit;
        }
        if ((usage & BufferUsageFlags.IndirectBufferBit) != 0)
        {
            stage |= PipelineStageFlags2.DrawIndirectBit;
            access |= AccessFlags2.IndirectCommandReadBit;
        }
        if ((usage & BufferUsageFlags.TransferSrcBit) != 0)
        {
            stage |= PipelineStageFlags2.TransferBit;
            access |= AccessFlags2.TransferReadBit;
        }
        if ((usage & BufferUsageFlags.TransferDstBit) != 0)
        {
            // A previous staged copy wrote it: that write must be made available too.
            stage |= PipelineStageFlags2.TransferBit;
            access |= AccessFlags2.TransferWriteBit;
        }
        return (stage, access);
    }
}
