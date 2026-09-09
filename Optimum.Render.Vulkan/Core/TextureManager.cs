using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The sampler state GL keeps on the texture object.
///
/// In GL these live on the texture and are changed with glTexParameter; in Vulkan
/// they belong to a separate immutable sampler object. Keeping them here as a
/// value and resolving to a cached sampler at bind time reproduces the GL
/// behaviour without creating an object per texture.
/// </summary>
/// <param name="Mipmapped">
/// Whether the GL min filter is one of the four MIPMAP forms. GL treats
/// GL_NEAREST and GL_LINEAR as "level 0 only" however many levels the texture
/// has, and Vulkan has no such filter - it always picks a level from the range
/// the sampler allows. So this decides the sampler's LOD clamp, and without it
/// a texture that merely owns a mip chain gets minified through it on surfaces
/// GL would have sampled sharp.
/// </param>
/// <param name="MaxLevel">
/// GL_TEXTURE_MAX_LEVEL, the highest mip the texture is allowed to use, or a
/// negative value for no limit. The client clamps this to the mipmap quality
/// setting after building a chain.
/// </param>
internal readonly record struct SamplerState(
    Filter MagFilter,
    Filter MinFilter,
    SamplerMipmapMode MipmapMode,
    SamplerAddressMode AddressU,
    SamplerAddressMode AddressV,
    float LodBias,
    bool CompareEnable,
    float MaxAnisotropy,
    BorderColor BorderColor,
    bool Mipmapped = false,
    int MaxLevel = -1)
{
    public static SamplerState Default => new(
        Filter.Nearest, Filter.Nearest, SamplerMipmapMode.Nearest,
        SamplerAddressMode.Repeat, SamplerAddressMode.Repeat,
        0f, false, 1f, BorderColor.FloatOpaqueBlack);

    /// <summary>
    /// The sampler's LOD ceiling. Anything under 1 confines sampling to level 0,
    /// which is what a non-mipmapping GL filter means.
    /// </summary>
    public float LodCeiling => !Mipmapped ? 0.25f
        : MaxLevel >= 0 ? MaxLevel + 1f
        : Vk.LodClampNone;
}

/// <summary>A texture, its memory, its view, and the GL state attached to it.</summary>
internal sealed unsafe class VulkanTexture : IDisposable
{
    private readonly VulkanContext _context;
    private bool _disposed;

    public Image Image { get; init; }
    public MemoryAllocation Allocation { get; init; }
    public ImageView View { get; init; }

    /// <summary>Never reused, unlike <see cref="View" />; see <see cref="ResourceIds" />.</summary>
    public ulong Id { get; } = ResourceIds.Next();

    public Format Format { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public uint MipLevels { get; init; }
    public uint Layers { get; init; }

    /// <summary>Whether the view is a cube rather than a six-layer array.</summary>
    public bool Cube { get; init; }
    public ImageAspectFlags Aspect { get; init; }

    /// <summary>Mutable, as glTexParameter is.</summary>
    public SamplerState State { get; set; } = SamplerState.Default;

    /// <summary>Tracked because Vulkan offers no way to query it.</summary>
    public ImageLayout Layout { get; set; } = ImageLayout.Undefined;

    /// <summary>
    /// Single-layer views, created on demand and keyed by layer.
    ///
    /// <see cref="View" /> covers the whole image, which is what a sampler wants.
    /// A colour attachment pointed at one layer of an array needs a view of that
    /// layer alone - the OIT accumulation target is one array attached three
    /// times, once per layer, and a whole-image view there sends all three
    /// attachments to the same layer.
    /// </summary>
    private readonly Dictionary<uint, ImageView> _layerViews = new();

    public VulkanTexture(VulkanContext context) => _context = context;

    public ImageView ViewOfLayer(uint layer)
    {
        if (layer == 0 && Layers <= 1) return View;
        if (_layerViews.TryGetValue(layer, out ImageView existing)) return existing;

        var createInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = Image,
            ViewType = ImageViewType.Type2D,
            Format = Format,
            SubresourceRange = new ImageSubresourceRange(Aspect, 0, MipLevels, layer, 1),
        };

        if (_context.Api.CreateImageView(_context.Device, &createInfo, null, out ImageView view) != Result.Success)
        {
            return View;
        }
        _layerViews[layer] = view;
        return view;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Vk api = _context.Api;
        foreach (ImageView layerView in _layerViews.Values)
        {
            if (layerView.Handle != 0) api.DestroyImageView(_context.Device, layerView, null);
        }
        _layerViews.Clear();
        if (View.Handle != 0) api.DestroyImageView(_context.Device, View, null);
        if (Image.Handle != 0) api.DestroyImage(_context.Device, Image, null);
        if (Allocation.IsValid) _context.Allocator.Free(Allocation);
    }
}

/// <summary>
/// Interns sampler objects by their state.
///
/// The game has a handful of distinct sampler configurations - nearest and linear,
/// clamped and repeating, plus the shadow-comparison and mip-bias variants - but
/// sets them on hundreds of textures. One object per distinct state rather than
/// per texture keeps the count in single digits.
/// </summary>
internal sealed unsafe class SamplerCache : IDisposable
{
    private readonly VulkanContext _context;
    private readonly Dictionary<SamplerState, Sampler> _samplers = new();
    private bool _disposed;

    public int Count => _samplers.Count;

    public SamplerCache(VulkanContext context) => _context = context;

    public Sampler Get(SamplerState state)
    {
        if (_samplers.TryGetValue(state, out Sampler existing)) return existing;

        float maxAnisotropy = _context.Capabilities.SamplerAnisotropy
            ? Math.Max(1f, state.MaxAnisotropy)
            : 1f;

        var createInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = state.MagFilter,
            MinFilter = state.MinFilter,
            MipmapMode = state.MipmapMode,
            AddressModeU = state.AddressU,
            AddressModeV = state.AddressV,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MipLodBias = Math.Clamp(state.LodBias, -_context.Capabilities.MaxSamplerLodBias,
                _context.Capabilities.MaxSamplerLodBias),
            AnisotropyEnable = maxAnisotropy > 1f,
            MaxAnisotropy = maxAnisotropy,
            CompareEnable = state.CompareEnable,
            // Shadow maps sample with a less-or-equal comparison, matching the
            // GL_COMPARE_REF_TO_TEXTURE mode the shadow passes enable.
            CompareOp = CompareOp.LessOrEqual,
            MinLod = 0f,
            MaxLod = state.LodCeiling,
            BorderColor = state.BorderColor,
            UnnormalizedCoordinates = false,
        };

        if (_context.Api.CreateSampler(_context.Device, &createInfo, null, out Sampler sampler) != Result.Success)
        {
            throw new InvalidOperationException("vkCreateSampler failed");
        }

        _samplers[state] = sampler;
        return sampler;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (Sampler sampler in _samplers.Values)
        {
            _context.Api.DestroySampler(_context.Device, sampler, null);
        }
        _samplers.Clear();
    }
}

/// <summary>
/// Owns every texture and hands out integer ids in place of GL names.
///
/// The ids have to stay integers because the game's public API exposes them:
/// <c>LoadedTexture.TextureId</c> and <c>FrameBufferRef.ColorTextureIds</c> are
/// fields mods read and pass back. So this is a handle table, and 0 means "no
/// texture" exactly as it does in GL.
/// </summary>
internal sealed unsafe class TextureManager : IDisposable
{
    /// <summary>GL_SHORT source pixels converted to GL_RGBA16 storage.</summary>
    internal static ushort ShortToUnorm16(short value) =>
        (ushort)((Math.Max(0, (int)value) * 65535L + 16383) / 32767);

    public void UploadNormalizedShorts(int id, int level, int x, int y,
        int width, int height, ReadOnlySpan<short> pixels)
    {
        int count = checked(width * height * 4);
        var converted = new ushort[count];
        for (int i = 0; i < count; i++) converted[i] = ShortToUnorm16(pixels[i]);

        fixed (ushort* source = converted)
        {
            Upload(id, level, x, y, (uint)width, (uint)height, (IntPtr)source, 8);
        }
    }

    private readonly VulkanContext _context;
    private readonly VulkanCommands _commands;
    private readonly List<VulkanTexture?> _textures = new();
    private readonly Stack<int> _freeIds = new();
    private bool _disposed;

    public SamplerCache Samplers { get; }

    public TextureManager(VulkanContext context, VulkanCommands commands)
    {
        _context = context;
        _commands = commands;
        Samplers = new SamplerCache(context);

        // Index 0 is reserved so a zero id never names a real texture.
        _textures.Add(null);
    }

    public int Count
    {
        get
        {
            int live = 0;
            foreach (VulkanTexture? texture in _textures)
            {
                if (texture != null) live++;
            }
            return live;
        }
    }

    public VulkanTexture? Get(int id) =>
        id > 0 && id < _textures.Count ? _textures[id] : null;

    private int Register(VulkanTexture texture)
    {
        if (_freeIds.Count > 0)
        {
            int reused = _freeIds.Pop();
            _textures[reused] = texture;
            return reused;
        }

        _textures.Add(texture);
        return _textures.Count - 1;
    }

    /// <summary>
    /// Creates a texture. Usage always includes transfer source and destination
    /// so uploads, readback and mipmap generation need no advance warning, which
    /// is the GL model where any texture can be updated at any time.
    /// </summary>
    public int Create(
        uint width, uint height, Format format,
        uint layers = 1, bool cube = false, bool generateMipmaps = false,
        ImageUsageFlags extraUsage = 0)
    {
        // GL tolerates a zero-sized texture - it creates nothing and carries on -
        // while Vulkan rejects the extent outright. The client asks for one when
        // a render target is sized from a window dimension that is still zero, so
        // this clamps rather than throwing, matching GL's forgiveness.
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        uint mipLevels = generateMipmaps ? MipLevelsFor(width, height) : 1;
        ImageAspectFlags aspect = IsDepthFormat(format)
            ? ImageAspectFlags.DepthBit
            : ImageAspectFlags.ColorBit;

        ImageUsageFlags usage =
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.TransferSrcBit
            | extraUsage
            | (IsDepthFormat(format)
                ? ImageUsageFlags.DepthStencilAttachmentBit
                : ImageUsageFlags.ColorAttachmentBit);

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Format = format,
            Extent = new Extent3D(width, height, 1),
            MipLevels = mipLevels,
            ArrayLayers = cube ? 6 : layers,
            Samples = SampleCountFlags.Count1Bit,
            Tiling = ImageTiling.Optimal,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
            Flags = cube ? ImageCreateFlags.CreateCubeCompatibleBit : 0,
        };

        Vk api = _context.Api;
        if (api.CreateImage(_context.Device, &imageInfo, null, out Image image) != Result.Success)
        {
            throw new InvalidOperationException("vkCreateImage failed");
        }

        api.GetImageMemoryRequirements(_context.Device, image, out MemoryRequirements requirements);
        MemoryAllocation allocation = _context.Allocator.Allocate(
            requirements, MemoryPropertyFlags.DeviceLocalBit, linear: false,
            $"a {width}x{height} {format} image");
        api.BindImageMemory(_context.Device, image, allocation.Memory, allocation.Offset);

        uint viewLayers = cube ? 6 : layers;
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = image,
            ViewType = cube ? ImageViewType.TypeCube
                : layers > 1 ? ImageViewType.Type2DArray
                : ImageViewType.Type2D,
            Format = format,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, mipLevels, 0, viewLayers),
        };
        api.CreateImageView(_context.Device, &viewInfo, null, out ImageView view);

        var texture = new VulkanTexture(_context)
        {
            Image = image,
            Allocation = allocation,
            View = view,
            Format = format,
            Width = width,
            Height = height,
            MipLevels = mipLevels,
            Layers = viewLayers,
            Cube = cube,
            Aspect = aspect,
        };

        return Register(texture);
    }

    /// <summary>
    /// Uploads pixels into a region. Staging plus a copy, then back to a
    /// shader-readable layout, submitted and waited on. That is stronger
    /// ordering than GL guarantees, which makes it correct; recording the copy
    /// inline in the frame's command buffer is the later optimisation.
    /// </summary>
    public void Upload(
        int textureId, int level, int x, int y, uint width, uint height,
        IntPtr pixels, int bytesPerPixel, uint layer = 0)
    {
        VulkanTexture? texture = Get(textureId);
        if (texture == null || pixels == IntPtr.Zero) return;

        ulong size = (ulong)width * height * (ulong)bytesPerPixel;
        if (size == 0) return;

        using var staging = new VulkanBuffer(_context, size,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        System.Buffer.MemoryCopy((void*)pixels, (void*)staging.Mapped, (long)size, (long)size);

        _commands.SubmitAndWait(commandBuffer =>
        {
            if (_context.CheckpointsAvailable)
            {
                _context.CmdSetCheckpoint(commandBuffer, CheckpointMarker.Upload(textureId, width, height));
            }

            TransitionTexture(commandBuffer, texture, ImageLayout.TransferDstOptimal);

            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(texture.Aspect, (uint)level, layer, 1),
                ImageOffset = new Offset3D(x, y, 0),
                ImageExtent = new Extent3D(width, height, 1),
            };
            _context.Api.CmdCopyBufferToImage(commandBuffer, staging.Handle, texture.Image,
                ImageLayout.TransferDstOptimal, 1, &region);

            TransitionTexture(commandBuffer, texture, ImageLayout.ShaderReadOnlyOptimal);
        });
    }

    /// <summary>
    /// Builds the mip chain by successive blits, which is how every Vulkan
    /// implementation of glGenerateMipmap works.
    /// </summary>
    public void GenerateMipmaps(int textureId)
    {
        VulkanTexture? texture = Get(textureId);
        if (texture == null || texture.MipLevels <= 1) return;

        _commands.SubmitAndWait(commandBuffer =>
        {
            Vk api = _context.Api;
            int mipWidth = (int)texture.Width;
            int mipHeight = (int)texture.Height;

            if (_context.CheckpointsAvailable)
            {
                _context.CmdSetCheckpoint(commandBuffer, CheckpointMarker.Mipmaps(textureId, texture.MipLevels));
            }

            TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);

            for (uint level = 1; level < texture.MipLevels; level++)
            {
                int nextWidth = Math.Max(1, mipWidth / 2);
                int nextHeight = Math.Max(1, mipHeight / 2);

                TransitionRange(commandBuffer, texture, level, 1,
                    ImageLayout.Undefined, ImageLayout.TransferDstOptimal);

                var blit = new ImageBlit
                {
                    SrcSubresource = new ImageSubresourceLayers(texture.Aspect, level - 1, 0, texture.Layers),
                    DstSubresource = new ImageSubresourceLayers(texture.Aspect, level, 0, texture.Layers),
                };
                blit.SrcOffsets.Element0 = new Offset3D(0, 0, 0);
                blit.SrcOffsets.Element1 = new Offset3D(mipWidth, mipHeight, 1);
                blit.DstOffsets.Element0 = new Offset3D(0, 0, 0);
                blit.DstOffsets.Element1 = new Offset3D(nextWidth, nextHeight, 1);

                api.CmdBlitImage(commandBuffer,
                    texture.Image, ImageLayout.TransferSrcOptimal,
                    texture.Image, ImageLayout.TransferDstOptimal,
                    1, &blit, Filter.Linear);

                TransitionRange(commandBuffer, texture, level, 1,
                    ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal);

                mipWidth = nextWidth;
                mipHeight = nextHeight;
            }

            texture.Layout = ImageLayout.TransferSrcOptimal;
            TransitionTexture(commandBuffer, texture, ImageLayout.ShaderReadOnlyOptimal);
        });
    }

    /// <summary>
    /// Applies a glTexParameter. Nothing touches the GPU: the state lives on the
    /// texture and resolves to a cached sampler when it is next bound.
    /// </summary>
    public void SetParameter(int textureId, int parameterName, float value)
    {
        VulkanTexture? texture = Get(textureId);
        if (texture == null) return;

        SamplerState state = texture.State;
        int integer = (int)value;

        texture.State = parameterName switch
        {
            GlEnums.TextureMinFilter => ApplyMinFilter(state, integer),
            GlEnums.TextureMagFilter => state with { MagFilter = GlEnums.FilterFrom(integer) },
            GlEnums.TextureWrapS => state with { AddressU = GlEnums.AddressModeFrom(integer) },
            GlEnums.TextureWrapT => state with { AddressV = GlEnums.AddressModeFrom(integer) },
            GlEnums.TextureLodBias => state with { LodBias = value },
            GlEnums.TextureMaxLevel => state with { MaxLevel = integer },
            GlEnums.TextureCompareMode => state with
            {
                CompareEnable = integer == GlEnums.TextureCompareRefToTexture,
            },
            _ => state,
        };
    }

    private static SamplerState ApplyMinFilter(SamplerState state, int glFilter)
    {
        (Filter filter, SamplerMipmapMode mode) = GlEnums.MinFilterFrom(glFilter);
        return state with
        {
            MinFilter = filter,
            MipmapMode = mode,
            Mipmapped = GlEnums.MinFilterUsesMipmaps(glFilter),
        };
    }

    /// <summary>
    /// Vulkan offers four fixed border colours where GL takes an arbitrary one.
    /// The SSAO targets use opaque white; anything else rounds to the nearest of
    /// the four rather than failing.
    /// </summary>
    public void SetBorderColor(int textureId, float r, float g, float b, float a)
    {
        VulkanTexture? texture = Get(textureId);
        if (texture == null) return;

        bool opaque = a >= 0.5f;
        bool white = (r + g + b) / 3f >= 0.5f;

        texture.State = texture.State with
        {
            BorderColor = opaque
                ? white ? BorderColor.FloatOpaqueWhite : BorderColor.FloatOpaqueBlack
                : BorderColor.FloatTransparentBlack,
        };
    }

    public void Delete(int textureId, FrameRing? ring = null)
    {
        VulkanTexture? texture = Get(textureId);
        if (texture == null) return;

        _textures[textureId] = null;
        _freeIds.Push(textureId);

        // Handing it to the ring means it outlives any frame still referencing it.
        if (ring != null) ring.DeferDeletion(texture);
        else texture.Dispose();
    }

    // ------------------------------------------------------------------ barriers

    public void TransitionTexture(CommandBuffer commandBuffer, VulkanTexture texture, ImageLayout target)
    {
        if (texture.Layout == target) return;
        TransitionRange(commandBuffer, texture, 0, texture.MipLevels, texture.Layout, target);
        texture.Layout = target;
    }

    private void TransitionRange(
        CommandBuffer commandBuffer, VulkanTexture texture,
        uint baseMip, uint mipCount, ImageLayout from, ImageLayout to)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
            DstStageMask = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            OldLayout = from,
            NewLayout = to,
            Image = texture.Image,
            SubresourceRange = new ImageSubresourceRange(texture.Aspect, baseMip, mipCount, 0, texture.Layers),
        };

        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };
        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
    }

    // -------------------------------------------------------------------- helpers

    public static uint MipLevelsFor(uint width, uint height) =>
        (uint)Math.Floor(Math.Log2(Math.Max(width, height))) + 1;

    public static bool IsDepthFormat(Format format) => format is
        Format.D16Unorm or Format.D32Sfloat or Format.D24UnormS8Uint or Format.D32SfloatS8Uint
        or Format.X8D24UnormPack32 or Format.D16UnormS8Uint;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (VulkanTexture? texture in _textures) texture?.Dispose();
        _textures.Clear();
        Samplers.Dispose();
    }
}
