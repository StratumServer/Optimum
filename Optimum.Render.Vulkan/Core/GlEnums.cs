using System;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// Translates the raw OpenGL constants the game passes around into Vulkan enums.
///
/// The client and its mods hand these across the backend seam as plain integers -
/// <c>GlStencilFunc(515, 1, 255)</c>, <c>GlDepthFunc</c>, <c>BlendFunc(770, 771)</c>
/// - because that is what they already hold and what the GL path consumed. Keeping
/// them as integers at the boundary is what lets a mod calling
/// <c>IRenderAPI.GlStencilFunc</c> work on this backend without knowing it exists.
/// </summary>
internal static class GlEnums
{
    // Blend factors.
    private const int Zero = 0;
    private const int One = 1;
    private const int SrcColor = 0x0300;
    private const int OneMinusSrcColor = 0x0301;
    private const int SrcAlpha = 0x0302;
    private const int OneMinusSrcAlpha = 0x0303;
    private const int DstAlpha = 0x0304;
    private const int OneMinusDstAlpha = 0x0305;
    private const int DstColor = 0x0306;
    private const int OneMinusDstColor = 0x0307;
    private const int SrcAlphaSaturate = 0x0308;

    public static BlendFactor BlendFactorFrom(int glFactor) => glFactor switch
    {
        Zero => BlendFactor.Zero,
        One => BlendFactor.One,
        SrcColor => BlendFactor.SrcColor,
        OneMinusSrcColor => BlendFactor.OneMinusSrcColor,
        SrcAlpha => BlendFactor.SrcAlpha,
        OneMinusSrcAlpha => BlendFactor.OneMinusSrcAlpha,
        DstAlpha => BlendFactor.DstAlpha,
        OneMinusDstAlpha => BlendFactor.OneMinusDstAlpha,
        DstColor => BlendFactor.DstColor,
        OneMinusDstColor => BlendFactor.OneMinusDstColor,
        SrcAlphaSaturate => BlendFactor.SrcAlphaSaturate,
        _ => BlendFactor.One,
    };

    public static BlendOp BlendOpFrom(int glEquation) => glEquation switch
    {
        0x8006 => BlendOp.Add,              // GL_FUNC_ADD
        0x800A => BlendOp.Subtract,         // GL_FUNC_SUBTRACT
        0x800B => BlendOp.ReverseSubtract,  // GL_FUNC_REVERSE_SUBTRACT
        0x8007 => BlendOp.Min,              // GL_MIN
        0x8008 => BlendOp.Max,              // GL_MAX
        _ => BlendOp.Add,
    };

    public static CompareOp CompareOpFrom(int glFunc) => glFunc switch
    {
        0x0200 => CompareOp.Never,
        0x0201 => CompareOp.Less,
        0x0202 => CompareOp.Equal,
        0x0203 => CompareOp.LessOrEqual,
        0x0204 => CompareOp.Greater,
        0x0205 => CompareOp.NotEqual,
        0x0206 => CompareOp.GreaterOrEqual,
        0x0207 => CompareOp.Always,
        _ => CompareOp.Less,
    };

    public static StencilOp StencilOpFrom(int glOp) => glOp switch
    {
        0x1E00 => StencilOp.Keep,
        0x0000 => StencilOp.Zero,
        0x1E01 => StencilOp.Replace,
        0x1E02 => StencilOp.IncrementAndClamp,
        0x1E03 => StencilOp.DecrementAndClamp,
        0x150A => StencilOp.Invert,
        0x8507 => StencilOp.IncrementAndWrap,
        0x8508 => StencilOp.DecrementAndWrap,
        _ => StencilOp.Keep,
    };

    public static PrimitiveTopology TopologyFrom(EnumDrawMode mode) => mode switch
    {
        EnumDrawMode.Triangles => PrimitiveTopology.TriangleList,
        EnumDrawMode.Lines => PrimitiveTopology.LineList,
        EnumDrawMode.LineStrip => PrimitiveTopology.LineStrip,
        _ => PrimitiveTopology.TriangleList,
    };

    /// <summary>
    /// Vulkan can only change topology dynamically within a class, so the class
    /// is part of the pipeline key while the exact topology is not.
    /// </summary>
    public static int TopologyClassOf(PrimitiveTopology topology) => topology switch
    {
        PrimitiveTopology.PointList => 0,
        PrimitiveTopology.LineList or PrimitiveTopology.LineStrip => 1,
        _ => 2,
    };

    public static Format TextureFormatFrom(EnumTextureInternalFormat format) => format switch
    {
        EnumTextureInternalFormat.Rgba8 => Format.R8G8B8A8Unorm,
        EnumTextureInternalFormat.Rgba16f => Format.R16G16B16A16Sfloat,
        EnumTextureInternalFormat.R16f => Format.R16Sfloat,
        EnumTextureInternalFormat.DepthComponent32 => Format.D32Sfloat,
        _ => Format.R8G8B8A8Unorm,
    };

    /// <summary>
    /// Formats the vanilla framebuffers use that the public enum does not name.
    /// SetupDefaultFrameBuffers passes these as raw GL internal formats.
    /// </summary>
    public static Format TextureFormatFromGl(int glInternalFormat) => glInternalFormat switch
    {
        0x8058 => Format.R8G8B8A8Unorm,        // GL_RGBA8
        0x881A => Format.R16G16B16A16Sfloat,   // GL_RGBA16F
        0x822D => Format.R16Sfloat,            // GL_R16F
        // TAA stores linear view depth here. Falling back to RGBA8 clamps every
        // distance above 1, making the next resolve reject otherwise valid history.
        0x822E => Format.R32Sfloat,            // GL_R32F
        0x8C3A => Format.B10G11R11UfloatPack32,// GL_R11F_G11F_B10F
        0x8814 => Format.R32G32B32A32Sfloat,   // GL_RGBA32F
        0x805B => Format.R16G16B16A16Unorm,    // GL_RGBA16, the cloud map's tile data
        0x8051 => Format.R8G8B8A8Unorm,        // GL_RGB8, promoted: RGB is not a
        0x1907 => Format.R8G8B8A8Unorm,        // GL_RGB     guaranteed attachment format
        0x8CAC => Format.D32Sfloat,            // GL_DEPTH_COMPONENT32F
        0x81A5 => Format.D16Unorm,             // GL_DEPTH_COMPONENT16
        // GL_BGRA. The GL bodies use it as a source pixel format against an
        // RGBA8 internal format; here it names a BGRA-ordered image, so Cairo
        // and GUI uploads land with their channels in the right places.
        0x80E1 => Format.B8G8R8A8Unorm,
        _ => Format.R8G8B8A8Unorm,
    };

    public static Filter FilterFrom(int glFilter) => glFilter switch
    {
        0x2600 => Filter.Nearest,   // GL_NEAREST
        0x2601 => Filter.Linear,    // GL_LINEAR
        _ => Filter.Nearest,
    };

    /// <summary>
    /// GL's minification filters fold the mipmap mode into the same constant.
    /// </summary>
    public static (Filter Filter, SamplerMipmapMode MipmapMode) MinFilterFrom(int glFilter) => glFilter switch
    {
        0x2600 => (Filter.Nearest, SamplerMipmapMode.Nearest),                 // GL_NEAREST
        0x2601 => (Filter.Linear, SamplerMipmapMode.Nearest),                  // GL_LINEAR
        0x2700 => (Filter.Nearest, SamplerMipmapMode.Nearest),                 // NEAREST_MIPMAP_NEAREST
        0x2701 => (Filter.Linear, SamplerMipmapMode.Nearest),                  // LINEAR_MIPMAP_NEAREST
        0x2702 => (Filter.Nearest, SamplerMipmapMode.Linear),                  // NEAREST_MIPMAP_LINEAR
        0x2703 => (Filter.Linear, SamplerMipmapMode.Linear),                   // LINEAR_MIPMAP_LINEAR
        _ => (Filter.Nearest, SamplerMipmapMode.Nearest),
    };

    /// <summary>
    /// Whether a GL min filter samples the mip chain at all. Only the four
    /// MIPMAP forms do; GL_NEAREST and GL_LINEAR read level 0 however many
    /// levels the texture owns.
    /// </summary>
    public static bool MinFilterUsesMipmaps(int glFilter) =>
        glFilter is 0x2700 or 0x2701 or 0x2702 or 0x2703;

    public static SamplerAddressMode AddressModeFrom(int glWrap) => glWrap switch
    {
        0x2901 => SamplerAddressMode.Repeat,             // GL_REPEAT
        0x812F => SamplerAddressMode.ClampToEdge,        // GL_CLAMP_TO_EDGE
        0x812D => SamplerAddressMode.ClampToBorder,      // GL_CLAMP_TO_BORDER
        0x8370 => SamplerAddressMode.MirroredRepeat,     // GL_MIRRORED_REPEAT
        _ => SamplerAddressMode.ClampToEdge,
    };

    // Texture parameter names the client actually sets.
    public const int TextureMinFilter = 0x2801;
    public const int TextureMagFilter = 0x2800;
    public const int TextureWrapS = 0x2802;
    public const int TextureWrapT = 0x2803;
    public const int TextureCompareMode = 0x884C;
    public const int TextureLodBias = 0x8501;
    public const int TextureMaxLevel = 0x813D;
    public const int TextureBorderColor = 0x1004;
    public const int TextureCompareModeNone = 0;
    public const int TextureCompareRefToTexture = 0x884E;
}
