namespace Optimum.Render.Vulkan.Shaders;

/// <summary>
/// The specialization constants of the native shaders (docs/vulkan-native-shaders.md section 5).
/// Mirrors <c>sources/shaders-vk/include/specialization.glsl</c>, the source of truth for native
/// shaders; SpecializationConventionTests keeps the two in agreement and checks every constant
/// against the <c>#define</c> <c>ShaderRegistry.registerDefaultShaderCodePrefixes</c> stamps.
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
        // Optimum AO (docs/research/ambient-occlusion.md C.5, C.11): gates the class-channel writes and
        // scene-ssao's GTAO compose branch, never an output or a varying.
        new(12, "OPTIMUM_OPTIMUMAO", "int", "0", "OPTIMUMAO"),
    };
}
