using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Shaderc;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>Outcome of a preprocess or compile step.</summary>
internal sealed class ShaderCompileResult
{
    public bool Success;
    public string? Error;
    public string PreprocessedText = "";
    public byte[] Spirv = Array.Empty<byte>();
}

/// <summary>
/// Wraps shaderc for the two jobs the backend needs: resolving the preprocessor
/// before the rewriter looks at the source, and turning rewritten GLSL into
/// SPIR-V.
///
/// Preprocessing is a separate pass on purpose. The game builds a block of
/// <c>#define</c>s per program - FXAA, SSAOLEVEL, SHADOWQUALITY, DYNLIGHTS and a
/// dozen more - and the shaders wrap declarations in <c>#if</c> on them, so the
/// set of uniforms a stage declares is not knowable until the conditionals are
/// resolved. Running a real preprocessor first means the rewriter only ever sees
/// straight-line declarations and never has to reason about conditionals.
/// </summary>
internal sealed unsafe class ShaderCompiler : IDisposable
{
    private readonly Shaderc _api;
    private readonly Compiler* _compiler;
    private bool _disposed;

    public ShaderCompiler()
    {
        _api = Shaderc.GetApi();
        _compiler = _api.CompilerInitialize();
        if (_compiler == null)
        {
            throw new InvalidOperationException("shaderc failed to initialise");
        }
    }

    /// <summary>
    /// Splices the program's <c>#define</c> prefix in after the version line and
    /// resolves the preprocessor, exactly where and how the OpenGL path does it
    /// in <c>ClientPlatformWindows.CompileShader</c>.
    /// </summary>
    public ShaderCompileResult Preprocess(string code, string prefixCode, string filename, EnumShaderType stage)
    {
        string spliced = SplicePrefix(RaiseVersionForPreprocessing(code), prefixCode);
        var result = new ShaderCompileResult();

        CompileOptions* options = CreateOptions();
        try
        {
            CompilationResult* compiled = CompileWith(
                spliced, filename, stage, options, preprocessOnly: true);
            try
            {
                if (!Succeeded(compiled, out string? error))
                {
                    result.Error = error;
                    return result;
                }

                result.PreprocessedText = ReadBytesAsText(compiled);
                result.Success = true;
                return result;
            }
            finally
            {
                _api.ResultRelease(compiled);
            }
        }
        finally
        {
            _api.CompileOptionsRelease(options);
        }
    }

    /// <summary>Compiles already-rewritten Vulkan GLSL to SPIR-V.</summary>
    public ShaderCompileResult Compile(string code, string filename, EnumShaderType stage)
    {
        var result = new ShaderCompileResult();

        CompileOptions* options = CreateOptions();
        try
        {
            CompilationResult* compiled = CompileWith(code, filename, stage, options, preprocessOnly: false);
            try
            {
                if (!Succeeded(compiled, out string? error))
                {
                    result.Error = error;
                    return result;
                }

                nuint length = _api.ResultGetLength(compiled);
                byte* bytes = (byte*)_api.ResultGetBytes(compiled);
                var spirv = new byte[(int)length];
                fixed (byte* destination = spirv)
                {
                    Buffer.MemoryCopy(bytes, destination, spirv.Length, (long)length);
                }

                result.Spirv = spirv;
                result.Success = true;
                return result;
            }
            finally
            {
                _api.ResultRelease(compiled);
            }
        }
        finally
        {
            _api.CompileOptionsRelease(options);
        }
    }

    /// <summary>
    /// Reproduces the vanilla splice: the prefix goes immediately after the
    /// newline that ends the <c>#version</c> line, because a version directive
    /// must be the first thing in a translation unit.
    /// </summary>
    internal static string SplicePrefix(string code, string prefixCode)
    {
        if (string.IsNullOrEmpty(prefixCode)) return code;

        int versionIndex = code.IndexOf("#version", StringComparison.Ordinal);
        int insertAt = code.IndexOf('\n', Math.Max(0, versionIndex)) + 1;
        if (insertAt <= 0) return prefixCode + code;

        return code.Insert(insertAt, prefixCode);
    }

    /// <summary>
    /// The lowest <c>#version</c> shaderc will preprocess for a SPIR-V target.
    ///
    /// The check runs during preprocessing, so a source below the floor is
    /// rejected before the rewriter ever gets to raise it. Every vanilla shader
    /// is well above this; it bites on the client's hardcoded 130 minimal-GUI
    /// program and would bite on any mod shader written to an old version.
    /// </summary>
    private const int MinimumPreprocessVersion = 140;

    /// <summary>
    /// Raises a below-floor <c>#version</c> to the version the rewriter targets
    /// anyway, so preprocessing sees something shaderc will accept.
    ///
    /// Only sources below the floor are touched, which means no vanilla shader
    /// changes at all. For the ones that do change, <c>__VERSION__</c> becomes
    /// 450 during preprocessing - a real difference, but the alternative is a
    /// shader that cannot be compiled for this backend.
    /// </summary>
    internal static string RaiseVersionForPreprocessing(string code)
    {
        if (string.IsNullOrEmpty(code)) return code;

        int versionIndex = code.IndexOf("#version", StringComparison.Ordinal);
        if (versionIndex < 0) return code;

        int numberStart = versionIndex + "#version".Length;
        while (numberStart < code.Length && (code[numberStart] == ' ' || code[numberStart] == '\t'))
        {
            numberStart++;
        }
        int numberEnd = numberStart;
        while (numberEnd < code.Length && char.IsAsciiDigit(code[numberEnd]))
        {
            numberEnd++;
        }
        if (numberEnd == numberStart) return code;

        if (!int.TryParse(code.AsSpan(numberStart, numberEnd - numberStart),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
        {
            return code;
        }
        if (version >= MinimumPreprocessVersion) return code;

        return string.Concat(
            code.AsSpan(0, numberStart), "450", code.AsSpan(numberEnd));
    }

    private CompileOptions* CreateOptions()
    {
        CompileOptions* options = _api.CompileOptionsInitialize();
        _api.CompileOptionsSetSourceLanguage(options, SourceLanguage.Glsl);
        _api.CompileOptionsSetTargetEnv(options, TargetEnv.Vulkan, (uint)EnvVersion.Vulkan13);
        _api.CompileOptionsSetTargetSpirv(options, SpirvVersion.Shaderc15);
        _api.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Performance);
        // The game resolves its own #include directives through ShaderRegistry
        // before a stage ever reaches this class, so no include resolver is
        // installed; an #include reaching shaderc is a genuine error.
        return options;
    }

    private CompilationResult* CompileWith(
        string code, string filename, EnumShaderType stage, CompileOptions* options, bool preprocessOnly)
    {
        byte[] source = Encoding.UTF8.GetBytes(code);
        byte[] name = Encoding.UTF8.GetBytes(filename ?? "shader");
        byte[] entry = Encoding.UTF8.GetBytes("main");

        fixed (byte* sourcePtr = source)
        fixed (byte* namePtr = name)
        fixed (byte* entryPtr = entry)
        {
            ShaderKind kind = ToShaderKind(stage);
            return preprocessOnly
                ? _api.CompileIntoPreprocessedText(
                    _compiler, sourcePtr, (nuint)source.Length, kind, namePtr, entryPtr, options)
                : _api.CompileIntoSpv(
                    _compiler, sourcePtr, (nuint)source.Length, kind, namePtr, entryPtr, options);
        }
    }

    private bool Succeeded(CompilationResult* result, out string? error)
    {
        if (result == null)
        {
            error = "shaderc returned no result";
            return false;
        }

        if (_api.ResultGetCompilationStatus(result) == CompilationStatus.Success)
        {
            error = null;
            return true;
        }

        byte* message = _api.ResultGetErrorMessage(result);
        error = message == null ? "unknown shaderc error" : Marshal.PtrToStringUTF8((IntPtr)message);
        return false;
    }

    private string ReadBytesAsText(CompilationResult* result)
    {
        nuint length = _api.ResultGetLength(result);
        byte* bytes = (byte*)_api.ResultGetBytes(result);
        return length == 0 || bytes == null
            ? ""
            : Encoding.UTF8.GetString(bytes, (int)length);
    }

    private static ShaderKind ToShaderKind(EnumShaderType stage) => stage switch
    {
        EnumShaderType.VertexShader => ShaderKind.VertexShader,
        EnumShaderType.FragmentShader => ShaderKind.FragmentShader,
        EnumShaderType.GeometryShader => ShaderKind.GeometryShader,
        _ => ShaderKind.VertexShader,
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_compiler != null)
        {
            _api.CompilerRelease(_compiler);
        }
        _api.Dispose();
    }
}
