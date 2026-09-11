using System;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Graph;

/// <summary>
/// <see cref="ITransientBacking" /> over the device's texture table: images in the
/// Transient memory pool class, released through the device (descriptor eviction, then
/// destruction once the timelines passed), rebinding by texture id.
/// </summary>
internal sealed class TextureTransientBacking : ITransientBacking
{
    private readonly TextureManager _textures;
    private readonly Action<int> _release;

    /// <param name="textures">The device's texture table.</param>
    /// <param name="release">The device's texture release (evicts descriptor sets, retires on the timeline).</param>
    public TextureTransientBacking(TextureManager textures, Action<int> release)
    {
        _textures = textures;
        _release = release;
    }

    public int Create(TransientImageDesc desc) =>
        _textures.Create(desc.Width, desc.Height, desc.Format, layers: desc.Layers,
            generateMipmaps: desc.MipLevels > 1, poolClass: MemoryPoolClass.Transient);

    public void Destroy(int textureId) => _release(textureId);

    public ulong BytesOf(int textureId) => _textures.Get(textureId)?.Allocation.Size ?? 0;

    public bool TryDescribe(int textureId, out TransientImageDesc desc)
    {
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture == null || texture.Cube || texture.Aspect != ImageAspectFlags.ColorBit)
        {
            desc = default;
            return false;
        }
        desc = new TransientImageDesc(texture.Width, texture.Height, texture.Format, texture.MipLevels, texture.Layers);
        return true;
    }

    public void Discard(int textureId)
    {
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture != null) _textures.DiscardContents(texture);
    }

    public void Rebind(int logicalTextureId, int physicalTextureId) => _textures.Rebind(logicalTextureId, physicalTextureId);

    public void RestoreBindings() => _textures.RestoreBindings();
}
