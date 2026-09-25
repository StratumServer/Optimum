

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>
/// The descriptor set convention of plan decision 9: one pipeline layout shared by
/// every program. Mirrors <c>sources/shaders-vk/include/bindings.glsl</c>, the
/// source of truth for native shaders. ShaderDeliveryTests checks compiled bindings
/// against the actual shared pipeline layout.
///
/// | Set | Update | Contents |
/// | 0 frame | once per frame | FrameGlobals UBO (dynamic offset) and the fixed frame textures |
/// | 1 textures | when a texture is created or retired | bindless combined-image-sampler arrays, one per GLSL sampled type, PARTIALLY_BOUND and UPDATE_AFTER_BIND |
/// | 2 storage | per draw | FaceData, the animation buffers, the program record and named blocks |
/// | push | per draw | texture slot indices and per-draw scalars, at most <see cref="PushConstantBytes" /> |
///
/// Array sizes are docs/vulkan.md#bindless-descriptors's starting sizes; the device
/// floor (<see cref="Core.DescriptorIndexingFloor" />) is their sum.
/// </summary>
internal static class SetConvention
{
    public const string IncludePath = "sources/shaders-vk/include/bindings.glsl";

    public const int FrameSet = 0;
    public const int TextureSet = 1;
    public const int StorageSet = 2;
    public const int SetCount = 3;

    /// <summary>The spec minimum; everything a draw needs that is larger lives in a per-frame record addressed from here.</summary>
    public const uint PushConstantBytes = 128;

    public const int FrameGlobalsBinding = 0;

    public const uint Texture2DCapacity = 16384;
    public const uint Texture2DArrayCapacity = 1024;
    public const uint TextureCubeCapacity = 256;
    public const uint Texture3DCapacity = 256;
    public const uint UnsignedTexture2DCapacity = 1024;
    public const uint SignedTexture2DCapacity = 256;
    public const uint Shadow2DCapacity = 128;
    public const uint Shadow2DArrayCapacity = 64;
    public const uint ShadowCubeCapacity = 64;

    public const uint TextureArrayCapacityTotal =
        Texture2DCapacity + Texture2DArrayCapacity + TextureCubeCapacity + Texture3DCapacity +
        UnsignedTexture2DCapacity + SignedTexture2DCapacity + Shadow2DCapacity + Shadow2DArrayCapacity +
        ShadowCubeCapacity;

    /// <summary>A binding declared once in bindings.glsl: its define, value and, for samplers, the declaration.</summary>
    public readonly record struct Binding(string Define, int Value, string GlslType, string Name, uint Capacity);

    /// <summary>
    /// Set 0's fixed frame textures, under the names the game's shaders already use
    /// (<c>fogandlight.fsh</c> declares the two shadow maps as <c>sampler2DShadow</c>).
    /// Binding 0 is the FrameGlobals block.
    /// </summary>
    public static readonly Binding[] FrameTextures =
    {
        new("OPTIMUM_BINDING_SHADOW_MAP_FAR", 1, "sampler2DShadow", "shadowMapFar", 1),
        new("OPTIMUM_BINDING_SHADOW_MAP_NEAR", 2, "sampler2DShadow", "shadowMapNear", 1),
        new("OPTIMUM_BINDING_SKY", 3, "sampler2D", "sky", 1),
        new("OPTIMUM_BINDING_GLOW", 4, "sampler2D", "glow", 1),
        new("OPTIMUM_BINDING_LIQUID_DEPTH", 5, "sampler2D", "liquidDepth", 1),
    };

    /// <summary>Set 1: one runtime-sized array per GLSL sampled type.</summary>
    public static readonly Binding[] TextureArrays =
    {
        new("OPTIMUM_BINDING_TEXTURES_2D", 0, "sampler2D", "optimumTextures2D", Texture2DCapacity),
        new("OPTIMUM_BINDING_TEXTURES_2D_ARRAY", 1, "sampler2DArray", "optimumTextures2DArray", Texture2DArrayCapacity),
        new("OPTIMUM_BINDING_TEXTURES_CUBE", 2, "samplerCube", "optimumTexturesCube", TextureCubeCapacity),
        new("OPTIMUM_BINDING_TEXTURES_3D", 3, "sampler3D", "optimumTextures3D", Texture3DCapacity),
        new("OPTIMUM_BINDING_TEXTURES_2D_UINT", 4, "usampler2D", "optimumTextures2DUint", UnsignedTexture2DCapacity),
        new("OPTIMUM_BINDING_TEXTURES_2D_INT", 5, "isampler2D", "optimumTextures2DInt", SignedTexture2DCapacity),
        new("OPTIMUM_BINDING_TEXTURES_2D_SHADOW", 6, "sampler2DShadow", "optimumTextures2DShadow", Shadow2DCapacity),
        new("OPTIMUM_BINDING_TEXTURES_2D_ARRAY_SHADOW", 7, "sampler2DArrayShadow", "optimumTextures2DArrayShadow", Shadow2DArrayCapacity),
        new("OPTIMUM_BINDING_TEXTURES_CUBE_SHADOW", 8, "samplerCubeShadow", "optimumTexturesCubeShadow", ShadowCubeCapacity),
    };

    /// <summary>
    /// Set 2's program record: every non-frame uniform that is not in the push block
    /// (docs/vulkan.md), a dynamic uniform buffer whose offset
    /// moves when the record changed. Kept out of <see cref="StorageBuffers" /> because it
    /// is a uniform buffer, not a storage buffer.
    /// </summary>
    public const int ProgramRecordBinding = 3;

    /// <summary>
    /// Set 2's storage buffers. FaceData is the chunk shaders' <c>faceDataBuf</c>; the
    /// animation pair holds the game's <c>Animation</c> and <c>AnimationPrev</c> blocks,
    /// read as std140 storage buffers (named after the blocks the rewriter maps there).
    /// </summary>
    public static readonly Binding[] StorageBuffers =
    {
        new("OPTIMUM_BINDING_FACE_DATA", 0, "buffer", "faceDataBuf", 1),
        new("OPTIMUM_BINDING_ANIMATION", 1, "buffer", "Animation", 1),
        new("OPTIMUM_BINDING_ANIMATION_PREV", 2, "buffer", "AnimationPrev", 1),
    };

    public const int FaceDataBinding = 0;
    public const int AnimationBinding = 1;
    public const int AnimationPrevBinding = 2;

    /// <summary>
    /// Set 2 bindings for every other named block a rewritten program declares, in
    /// declaration order: a GLSL 330 <c>uniform Block { ... }</c> becomes a
    /// <c>layout(std140) readonly buffer</c> here, so the client's std140 bytes are read
    /// unchanged. A program with more named blocks than this range fails to link.
    /// </summary>
    public const int NamedBlockFirstBinding = 4;
    public const int NamedBlockLastBinding = 7;

    /// <summary>Every set 2 binding: storage buffers, the record, and the named-block range.</summary>
    public const int StorageSetBindingCount = NamedBlockLastBinding + 1;
}

/// <summary>
/// The specialization constants of the native shaders (docs/vulkan.md).
/// Mirrors <c>sources/shaders-vk/include/specialization.glsl</c>, the source of truth for native
/// shaders. ShaderDeliveryTests checks the compiled constant ids, types and defaults.
///
/// Every constant replaces one quality or code-path define: a native source branches on
/// <c>if (OPTIMUM_BLOOM != 0)</c> where the GLSL 330 source has <c>#if BLOOM &gt; 0</c>, the
/// declarations the branch uses are unconditional, and a settings change becomes a pipeline-key
/// change instead of a recompile. The defines that change a declaration stay variant axes
/// (<c>TAAMOTION</c>, <c>USEOIT</c>, <c>USESSBO</c>, <c>GREEDYMESH</c>, ...) and are not here.
///
/// Defaults are 0, the value an undefined macro has in a GLSL <c>#if</c> and the value the
/// game's own includes fall back to (<c>fogandlight.vsh</c>: <c>#ifndef DYNLIGHTS / #define
/// DYNLIGHTS 0</c>, the same for <c>MINBRIGHT</c>). The runtime always specializes every
/// constant from the program's prefix, so a default only decides an unspecialized pipeline.
/// </summary>
internal static class SpecializationConvention
{
    public const string IncludePath = "sources/shaders-vk/include/specialization.glsl";

    /// <summary>One constant: its id, GLSL name and type, default, and the define it replaces.</summary>
    public readonly record struct Constant(uint Id, string Name, string GlslType, string Default, string Define);

    public static readonly Constant[] Constants =
    {
        new(0, "OPTIMUM_FXAA", "int", "0", "FXAA"),
        new(1, "OPTIMUM_SSAOLEVEL", "int", "0", "SSAOLEVEL"),
        new(2, "OPTIMUM_NORMALVIEW", "int", "0", "NORMALVIEW"),
        new(3, "OPTIMUM_BLOOM", "int", "0", "BLOOM"),
        new(4, "OPTIMUM_GODRAYS", "int", "0", "GODRAYS"),
        new(5, "OPTIMUM_FOAMEFFECT", "int", "0", "FOAMEFFECT"),
        new(6, "OPTIMUM_SHINYEFFECT", "int", "0", "SHINYEFFECT"),
        new(7, "OPTIMUM_SHADOWQUALITY", "int", "0", "SHADOWQUALITY"),
        new(8, "OPTIMUM_WAVINGSTUFF", "int", "0", "WAVINGSTUFF"),
        new(9, "OPTIMUM_MINBRIGHT", "float", "0.0", "MINBRIGHT"),
        new(10, "OPTIMUM_GREEDYMESH_GRAD", "int", "0", "GREEDYMESH_GRAD"),
        // DYNLIGHTS no longer sizes an array (the frame block's point-light arrays are fixed at
        // FrameGlobals.MaxDynamicLights and pointLightQuantity bounds the loop), but its zero
        // value still selects fogandlight.vsh's no-point-light path, which also skips the night
        // vision, MINBRIGHT and contrast terms. Keeping that path needs the value.
        new(11, "OPTIMUM_DYNLIGHTS", "int", "0", "DYNLIGHTS"),
        // Optimum AO (docs/vulkan.md#ambient-occlusion C.5, C.11): gates the class-channel writes and
        // scene-ssao's GTAO compose branch, never an output or a varying.
        new(12, "OPTIMUM_OPTIMUMAO", "int", "0", "OPTIMUMAO"),
    };
}
