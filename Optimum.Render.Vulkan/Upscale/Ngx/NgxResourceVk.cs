using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// <c>NVSDK_NGX_ImageViewInfo_VK</c> (<c>nvsdk_ngx_defs_vk.h</c>): one of our
/// image views plus the metadata NGX cannot recover from the handle.
///
/// The layout is the C struct's, on the x86-64 SysV ABI: two 8-byte handles,
/// then <c>VkImageSubresourceRange</c> (five 32-bit words), then the format and
/// the dimensions. 48 bytes, asserted in
/// <c>NgxDlssEvaluateTests.TheResourceStructHasTheLayoutTheHeadersDefine</c>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NgxImageViewInfoVk
{
    /// <summary>VkImageView.</summary>
    public ulong ImageView;
    /// <summary>VkImage the view names.</summary>
    public ulong Image;
    public uint AspectMask;
    public uint BaseMipLevel;
    public uint LevelCount;
    public uint BaseArrayLayer;
    public uint LayerCount;
    /// <summary>VkFormat.</summary>
    public uint Format;
    public uint Width;
    public uint Height;
}

/// <summary>
/// <c>NVSDK_NGX_BufferInfo_VK</c>. Optimum hands NGX no buffers today; the type
/// exists so the union in <see cref="NgxResourceVk" /> is not a bare byte blob.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct NgxBufferInfoVk
{
    public ulong Buffer;
    public uint SizeInBytes;
}

/// <summary>
/// <c>NVSDK_NGX_Resource_VK</c>: how every texture reaches DLSS.
///
/// The C type is a union of the image-view and buffer infos followed by the
/// resource type and a <c>bool ReadWrite</c>. The union is the size of its
/// larger member (48 bytes), so <see cref="Type" /> sits at offset 48 and
/// <see cref="ReadWrite" /> at 52, and the whole struct is 56 bytes with the
/// trailing padding to its 8-byte alignment. C++ <c>bool</c> is one byte and
/// only 0 or 1 is a valid value, so <see cref="ReadWrite" /> is a byte here and
/// is only ever written through the factory below.
///
/// NGX reads this through a pointer we keep alive across the evaluate call; the
/// pointer is stored in the parameter block, so the struct must not be a local
/// that dies before <c>EvaluateFeature</c> runs (see
/// <see cref="NgxDlssFeature.Evaluate" />, which pins them).
/// </summary>
[StructLayout(LayoutKind.Sequential, Size = 56)]
internal struct NgxResourceVk
{
    /// <summary>The union, in its image-view form. Optimum never uses the buffer form.</summary>
    public NgxImageViewInfoVk ImageViewInfo;

    /// <summary>NVSDK_NGX_Resource_VK_Type: 0 = image view, 1 = buffer.</summary>
    public uint Type;

    /// <summary>
    /// C++ <c>bool</c>. True only for an image whose <c>VkImageUsageFlags</c>
    /// include <c>VK_IMAGE_USAGE_STORAGE_BIT</c> and which is handed over in
    /// GENERAL: the DLSS output. Lying here earns FAIL_RWFlagMissing.
    /// </summary>
    public byte ReadWrite;

    /// <summary>NVSDK_NGX_RESOURCE_VK_TYPE_VK_IMAGEVIEW.</summary>
    public const uint TypeImageView = 0;

    /// <summary>NVSDK_NGX_RESOURCE_VK_TYPE_VK_BUFFER.</summary>
    public const uint TypeBuffer = 1;

    /// <summary>
    /// Tags one of our image/view pairs for NGX, the way
    /// <c>NVSDK_NGX_Create_ImageView_Resource_VK</c> does.
    ///
    /// <paramref name="readWrite" /> is the caller's promise about the image, not
    /// a request: false means the image is in SHADER_READ_ONLY_OPTIMAL and NGX
    /// will sample it, true means it carries STORAGE usage and is in GENERAL and
    /// NGX will write it. The barriers that make those layouts true are the
    /// caller's too - NGX performs no synchronisation of its own (DLSS
    /// Programming Guide §3.4).
    /// </summary>
    public static NgxResourceVk ImageView(
        ImageView view, Image image, ImageSubresourceRange range, Format format,
        uint width, uint height, bool readWrite) => new()
    {
        ImageViewInfo = new NgxImageViewInfoVk
        {
            ImageView = view.Handle,
            Image = image.Handle,
            AspectMask = (uint)range.AspectMask,
            BaseMipLevel = range.BaseMipLevel,
            LevelCount = range.LevelCount,
            BaseArrayLayer = range.BaseArrayLayer,
            LayerCount = range.LayerCount,
            Format = (uint)format,
            Width = width,
            Height = height,
        },
        Type = TypeImageView,
        ReadWrite = readWrite ? (byte)1 : (byte)0,
    };

    /// <summary>
    /// The same, for a texture the backend owns: level 0 and layer 0 of its
    /// whole-image view, which is the only subresource an upscaler ever reads or
    /// writes.
    /// </summary>
    public static NgxResourceVk Texture(VulkanTexture texture, bool readWrite) => ImageView(
        texture.View, texture.Image,
        new ImageSubresourceRange(texture.Aspect, 0, texture.MipLevels, 0, texture.Layers),
        texture.Format, texture.Width, texture.Height, readWrite);
}
