using System;
using System.Collections.Generic;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>One stage's input to translation.</summary>
internal sealed class ShaderStageSource
{
    public EnumShaderType Stage;
    /// <summary>Include-expanded GLSL, as ShaderRegistry produces it.</summary>
    public string Code = "";
    /// <summary>The program's <c>#define</c> block.</summary>
    public string PrefixCode = "";
    public string Filename = "shader";
}

/// <summary>A whole program, translated and ready to become pipeline stages.</summary>
internal sealed class TranslatedProgram
{
    public ProgramInterfaceLayout Layout = new();
    public Dictionary<EnumShaderType, byte[]> Spirv { get; } = new();
    /// <summary>The rewritten GLSL per stage, kept for diagnostics.</summary>
    public Dictionary<EnumShaderType, string> RewrittenSource { get; } = new();
    public List<string> Errors { get; } = new();
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// Drives a program from GLSL 330 to SPIR-V.
///
/// The order is forced by GL's semantics rather than chosen: preprocess every
/// stage first (declarations hide behind <c>#if</c>), then parse them all, then
/// resolve the program-wide interface, and only then rewrite and compile. Nothing
/// can be finalised per stage because uniforms and varyings are matched by name
/// across the whole program.
/// </summary>
internal static class ShaderTranslator
{
    /// <summary>
    /// Stages in the order the interface layout walks them. Fixed rather than
    /// incidental, so uniform offsets and varying locations - and therefore the
    /// SPIR-V cache key - are identical from run to run.
    /// </summary>
    private static readonly EnumShaderType[] StageOrder =
    {
        EnumShaderType.VertexShader,
        EnumShaderType.FragmentShader,
        EnumShaderType.GeometryShader,
    };

    public static TranslatedProgram Translate(
        IReadOnlyList<ShaderStageSource> stages,
        ShaderCompiler compiler,
        IReadOnlyDictionary<string, int>? declaredAttributes = null)
    {
        var program = new TranslatedProgram();

        var ordered = new List<ShaderStageSource>();
        foreach (EnumShaderType stage in StageOrder)
        {
            foreach (ShaderStageSource candidate in stages)
            {
                if (candidate.Stage == stage) ordered.Add(candidate);
            }
        }

        // Preprocess and parse.
        var parsed = new List<(EnumShaderType Stage, ParsedShader Parsed)>();
        foreach (ShaderStageSource stage in ordered)
        {
            ShaderCompileResult preprocessed =
                compiler.Preprocess(stage.Code, stage.PrefixCode, stage.Filename, stage.Stage);

            if (!preprocessed.Success)
            {
                program.Errors.Add($"{stage.Filename}: preprocessing failed: {preprocessed.Error}");
                continue;
            }

            // Rename identifiers 4.50 reserved before anything reads the source,
            // so the parser and the rewriter both see the same names.
            string source = GlslReservedWords.Rename(preprocessed.PreprocessedText);
            parsed.Add((stage.Stage, GlslParser.Parse(source)));
        }

        if (program.Errors.Count > 0) return program;

        program.Layout = ProgramInterfaceLayout.Build(parsed, declaredAttributes);
        foreach (string error in program.Layout.Errors)
        {
            program.Errors.Add(error);
        }
        if (program.Errors.Count > 0) return program;

        // The depth remap belongs on the last stage before rasterisation.
        EnumShaderType depthRemapStage = EnumShaderType.VertexShader;
        foreach ((EnumShaderType stage, _) in parsed)
        {
            if (stage == EnumShaderType.GeometryShader) depthRemapStage = stage;
        }

        // Rewrite and compile.
        foreach ((EnumShaderType stage, ParsedShader shader) in parsed)
        {
            string filename = FilenameFor(ordered, stage);

            RewrittenShader rewritten =
                ShaderRewriter.Rewrite(shader, program.Layout, stage, emitDepthRemap: stage == depthRemapStage);

            program.RewrittenSource[stage] = rewritten.Code;

            foreach (string error in rewritten.Errors)
            {
                program.Errors.Add($"{filename}: {error}");
            }
            if (rewritten.HasErrors) continue;

            ShaderCompileResult compiled = compiler.Compile(rewritten.Code, filename, stage);
            if (!compiled.Success)
            {
                program.Errors.Add($"{filename}: {compiled.Error}");
                continue;
            }

            program.Spirv[stage] = compiled.Spirv;
        }

        return program;
    }

    private static string FilenameFor(IReadOnlyList<ShaderStageSource> stages, EnumShaderType stage)
    {
        foreach (ShaderStageSource candidate in stages)
        {
            if (candidate.Stage == stage) return candidate.Filename;
        }
        return stage.ToString();
    }
}
