using System;
using System.Collections.Generic;

namespace Optimum.Render.Vulkan.Graph;

/// <summary>How a compute pass uses one bound image.</summary>
public enum ComputeAccess
{
    /// <summary>A combined image sampler over <see cref="ComputeBinding.MipCount" /> levels: SHADER_READ_ONLY_OPTIMAL.</summary>
    Sampled,
    /// <summary>A storage image read with imageLoad and never written: GENERAL.</summary>
    StorageRead,
    /// <summary>A storage image written with imageStore without reading it first: GENERAL.</summary>
    StorageWrite,
    /// <summary>A storage image read and written: GENERAL.</summary>
    StorageReadWrite,
}

/// <summary>
/// One image a compute pass binds: the descriptor binding in the program's pass set,
/// the texture, how it is used and which levels. A storage binding names exactly one
/// level (a storage image descriptor is one level); a sampled binding names a range,
/// and only that range moves to SHADER_READ_ONLY_OPTIMAL, so a prefilter can sample
/// level n and store level n + 1 of the same image in one pass.
/// </summary>
/// <param name="Binding">The <c>layout(binding = N)</c> of the pass set.</param>
/// <param name="TextureId">A texture id of the device.</param>
/// <param name="Access">What the dispatch does with it.</param>
/// <param name="BaseMip">The first level the binding covers.</param>
/// <param name="MipCount">The number of levels; exactly 1 for storage.</param>
/// <param name="Linear">Sampled only: linear min/mag filtering instead of nearest (always clamp to edge, nearest mip).</param>
public readonly record struct ComputeBinding(
    uint Binding, int TextureId, ComputeAccess Access, uint BaseMip = 0, uint MipCount = 1, bool Linear = false);

/// <summary>
/// One vkCmdDispatch of a pass. Either explicit group counts, or
/// <see cref="SizeFromBinding" /> naming the index (into <see cref="ComputePassDeclaration.Bindings" />)
/// of the image whose level extent the groups must cover with the program's local size.
/// </summary>
public sealed class ComputeDispatch
{
    public uint GroupsX = 1;
    public uint GroupsY = 1;
    public uint GroupsZ = 1;

    /// <summary>Index into the pass's bindings whose level extent sizes the dispatch; -1 uses the explicit counts.</summary>
    public int SizeFromBinding = -1;

    /// <summary>Pushed for the compute stage before the dispatch; at most the program's push constant size.</summary>
    public byte[]? PushConstants;

    public static ComputeDispatch Explicit(uint x, uint y, uint z = 1, byte[]? pushConstants = null) =>
        new() { GroupsX = x, GroupsY = y, GroupsZ = z, PushConstants = pushConstants };

    public static ComputeDispatch Covering(int bindingIndex, byte[]? pushConstants = null) =>
        new() { SizeFromBinding = bindingIndex, PushConstants = pushConstants };
}

/// <summary>
/// A compute pass as its owner declares it (the frame graph's second pass kind; the
/// AO is its first user). The recorder closes any open rendering scope, queues one
/// barrier per binding from its <see cref="ComputeAccess" /> (GENERAL for storage,
/// SHADER_READ_ONLY_OPTIMAL for sampled, per level), flushes them as one command,
/// binds the pipeline for <see cref="Specialization" /> and one descriptor set, and
/// records every dispatch. No rendering scope is open at any point of it.
/// </summary>
public sealed class ComputePassDeclaration
{
    public string Name = "";

    /// <summary>A program from <c>VulkanDevice.CreateComputeProgram</c>.</summary>
    public int ProgramId;

    /// <summary>Specialization constant values; <c>constant_id = i</c> takes element i (4 bytes each: uint, int, float bits or bool).</summary>
    public uint[] Specialization = Array.Empty<uint>();

    public ComputeBinding[] Bindings = Array.Empty<ComputeBinding>();

    public ComputeDispatch[] Dispatches = Array.Empty<ComputeDispatch>();
}

/// <summary>The size and level count of a bound texture, for validation without a device.</summary>
internal readonly record struct ComputeImageInfo(uint Width, uint Height, uint MipLevels, uint Layers);

/// <summary>
/// The device-free half of a compute pass: which usage each binding is, whether the
/// bindings are consistent, how many groups cover an image, and the signature the
/// frame plan sees.
/// </summary>
internal static class ComputePassPlanner
{
    public static ResourceUsage UsageOf(ComputeAccess access) => access switch
    {
        ComputeAccess.Sampled => ResourceUsage.SampleCompute,
        ComputeAccess.StorageRead => ResourceUsage.StorageReadCompute,
        ComputeAccess.StorageWrite => ResourceUsage.StorageWrite,
        ComputeAccess.StorageReadWrite => ResourceUsage.StorageReadWrite,
        _ => throw new ArgumentOutOfRangeException(nameof(access), access, null),
    };

    public static bool IsStorage(ComputeAccess access) => access != ComputeAccess.Sampled;

    public static bool Writes(ComputeAccess access) =>
        access is ComputeAccess.StorageWrite or ComputeAccess.StorageReadWrite;

    /// <summary>The extent of one level: the base extent halved per level, never below 1.</summary>
    public static uint LevelExtent(uint extent, uint mip) => Math.Max(1u, mip >= 32 ? 1u : extent >> (int)mip);

    /// <summary>Work groups of <paramref name="localSize" /> that cover <paramref name="extent" /> texels.</summary>
    public static uint GroupsCovering(uint extent, uint localSize) =>
        localSize == 0 ? 0 : (extent + localSize - 1) / localSize;

    /// <summary>
    /// Why the declaration cannot be recorded, or null. Checks every binding names a
    /// live single-layer texture and levels inside it, that storage bindings name one
    /// level, that no binding number repeats, that no level is bound twice where one
    /// of the uses writes (one layout per level per pass), and that every dispatch
    /// sizes from an existing binding.
    /// </summary>
    public static string? Validate(ComputePassDeclaration pass, Func<int, ComputeImageInfo?> image)
    {
        if (pass.Dispatches.Length == 0) return "compute pass '" + pass.Name + "' has no dispatch";

        var numbers = new HashSet<uint>();
        for (int i = 0; i < pass.Bindings.Length; i++)
        {
            ComputeBinding binding = pass.Bindings[i];
            if (!numbers.Add(binding.Binding)) return "binding " + binding.Binding + " is bound twice";

            ComputeImageInfo? info = image(binding.TextureId);
            if (info == null) return "binding " + binding.Binding + " names no texture (" + binding.TextureId + ")";
            if (info.Value.Layers > 1) return "binding " + binding.Binding + " names a layered texture";
            if (binding.MipCount == 0) return "binding " + binding.Binding + " covers no level";
            if (IsStorage(binding.Access) && binding.MipCount != 1)
                return "storage binding " + binding.Binding + " must name exactly one level";
            if (binding.BaseMip + binding.MipCount > info.Value.MipLevels)
                return "binding " + binding.Binding + " names levels " + binding.BaseMip + "+" + binding.MipCount +
                       " of a " + info.Value.MipLevels + "-level texture";

            for (int j = 0; j < i; j++)
            {
                ComputeBinding other = pass.Bindings[j];
                if (other.TextureId != binding.TextureId) continue;
                bool overlap = binding.BaseMip < other.BaseMip + other.MipCount &&
                               other.BaseMip < binding.BaseMip + binding.MipCount;
                if (!overlap) continue;
                if (UsageOf(binding.Access) != UsageOf(other.Access) || Writes(binding.Access))
                    return "bindings " + other.Binding + " and " + binding.Binding +
                           " use one level of texture " + binding.TextureId + " in two ways";
            }
        }

        foreach (ComputeDispatch dispatch in pass.Dispatches)
        {
            if (dispatch.SizeFromBinding >= pass.Bindings.Length)
                return "a dispatch sizes from binding index " + dispatch.SizeFromBinding + " of " + pass.Bindings.Length;
        }
        return null;
    }

    /// <summary>The group counts of <paramref name="dispatch" />.</summary>
    public static (uint X, uint Y, uint Z) Groups(ComputeDispatch dispatch, ComputePassDeclaration pass,
        Func<int, ComputeImageInfo?> image, uint localSizeX, uint localSizeY)
    {
        if (dispatch.SizeFromBinding < 0) return (dispatch.GroupsX, dispatch.GroupsY, dispatch.GroupsZ);
        ComputeBinding binding = pass.Bindings[dispatch.SizeFromBinding];
        ComputeImageInfo info = image(binding.TextureId) ?? default;
        return (GroupsCovering(LevelExtent(info.Width, binding.BaseMip), localSizeX),
            GroupsCovering(LevelExtent(info.Height, binding.BaseMip), localSizeY), 1);
    }

    /// <summary>
    /// What the frame plan sees of the pass: every written texture as a non-transient
    /// attachment use (so a raster pass that attaches it later never loads DONT_CARE
    /// over the dispatch's result) and every read texture in <see cref="PassSignature.Reads" />.
    /// The extent is the first written level's, or the first binding's.
    /// </summary>
    public static PassSignature Signature(int nameId, ComputePassDeclaration pass, Func<int, ComputeImageInfo?> image)
    {
        var uses = new List<AttachmentUse>();
        var reads = new List<int>();
        uint width = 0, height = 0;
        foreach (ComputeBinding binding in pass.Bindings)
        {
            if (Writes(binding.Access))
            {
                var use = new AttachmentUse(binding.TextureId, UsageOf(binding.Access), false);
                if (!uses.Contains(use)) uses.Add(use);
                if (width == 0 && image(binding.TextureId) is { } written)
                {
                    width = LevelExtent(written.Width, binding.BaseMip);
                    height = LevelExtent(written.Height, binding.BaseMip);
                }
            }
            if (binding.Access != ComputeAccess.StorageWrite && !reads.Contains(binding.TextureId))
            {
                reads.Add(binding.TextureId);
            }
        }
        if (width == 0 && pass.Bindings.Length > 0 && image(pass.Bindings[0].TextureId) is { } first)
        {
            width = LevelExtent(first.Width, pass.Bindings[0].BaseMip);
            height = LevelExtent(first.Height, pass.Bindings[0].BaseMip);
        }

        return new PassSignature
        {
            NameId = nameId,
            Attachments = uses.ToArray(),
            Reads = reads.ToArray(),
            Width = (int)width,
            Height = (int)height,
            FormatsId = -1,
        };
    }
}
