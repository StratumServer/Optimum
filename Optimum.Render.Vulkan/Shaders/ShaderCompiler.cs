using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Shaderc;
using Vintagestory.API.Client;
using System.IO;
using System.Runtime.CompilerServices;

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
internal sealed unsafe partial class ShaderCompiler : IDisposable
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

    /// <summary>
    /// Where compiled modules are kept between launches; null compiles every time.
    /// The filename only names errors (no debug info is emitted), so it is not part of the key.
    /// </summary>
    public ShaderBinaryCache? BinaryCache { get; set; }

    /// <summary>
    /// The options <see cref="CreateOptions" /> sets, spelled out for the cache key.
    /// Change both together.
    /// </summary>
    internal const string OptionsIdentity = "glsl;vulkan1.3;spirv1.5;performance;no-debug-info;no-include-resolver";

    private static string? _nativeIdentity;

    /// <summary>
    /// Everything besides the source that decides the SPIR-V: the options and the
    /// shaderc build. The C API exposes no compiler version, so the build is the
    /// loaded library's own hash (docs/vulkan.md#caches §5).
    /// </summary>
    public string Identity => OptionsIdentity + ";" + (_nativeIdentity ??= NativeLibraryIdentity());

    internal static string NativeLibraryIdentity()
    {
        string name = OperatingSystem.IsWindows() ? "shaderc_shared.dll"
            : OperatingSystem.IsMacOS() ? "libshaderc_shared.dylib"
            : "libshaderc_shared.so";
        string runtime = RuntimeInformation.RuntimeIdentifier;

        foreach (string? directory in new[]
                 {
                     AppContext.BaseDirectory,
                     System.IO.Path.GetDirectoryName(typeof(ShaderCompiler).Assembly.Location),
                 })
        {
            if (string.IsNullOrEmpty(directory)) continue;
            foreach (string candidate in new[]
                     {
                         System.IO.Path.Combine(directory, name),
                         System.IO.Path.Combine(directory, "runtimes", runtime, "native", name),
                     })
            {
                try
                {
                    if (!System.IO.File.Exists(candidate)) continue;
                    using System.IO.FileStream stream = System.IO.File.OpenRead(candidate);
                    return "shaderc-sha256:" +
                        Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
                }
                catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException)
                {
                }
            }
        }

        // Not found where the packagers put it: fall back to the binding's version,
        // which pins the native package it ships with.
        return "silk-shaderc-" + typeof(Shaderc).Assembly.GetName().Version;
    }

    /// <summary>Compiles already-rewritten Vulkan GLSL to SPIR-V.</summary>
    public ShaderCompileResult Compile(string code, string filename, EnumShaderType stage)
    {
        string? key = null;
        if (BinaryCache != null)
        {
            key = ShaderBinaryCache.KeyFor(code, stage, Identity);
            byte[]? cached = BinaryCache.TryGet(key);
            if (cached != null) return new ShaderCompileResult { Success = true, Spirv = cached };
        }

        ShaderCompileResult compiled = CompileUncached(code, filename, stage);
        if (key != null && compiled.Success) BinaryCache!.Put(key, compiled.Spirv);
        return compiled;
    }

    /// <summary>
    /// Compiles with optimisation off and never through the cache. The optimiser strips every
    /// <c>OpName</c> and drops declarations nothing uses, so the offline shader compiler reflects
    /// names and declared interfaces from this twin of the shipped module
    /// (docs/vulkan.md). Never shipped.
    /// </summary>
    public ShaderCompileResult CompileForReflection(string code, string filename, EnumShaderType stage) =>
        CompileUncached(code, filename, stage, optimize: false);

    /// <summary>
    /// Stage tag for compute modules in the binary cache key: GL_COMPUTE_SHADER, which no
    /// client stage uses, so a compute module never shares a key with a vertex or fragment one.
    /// </summary>
    internal const EnumShaderType ComputeStageTag = (EnumShaderType)37305;

    /// <summary>
    /// Compiles a native Vulkan GLSL compute shader (<c>sources/shaders-vk/**.comp</c>)
    /// to SPIR-V. No prefix, no rewriter: native shaders are written for the backend.
    /// </summary>
    public ShaderCompileResult CompileCompute(string code, string filename)
    {
        string? key = null;
        if (BinaryCache != null)
        {
            key = ShaderBinaryCache.KeyFor(code, ComputeStageTag, Identity);
            byte[]? cached = BinaryCache.TryGet(key);
            if (cached != null) return new ShaderCompileResult { Success = true, Spirv = cached };
        }

        ShaderCompileResult compiled = CompileUncached(code, filename, ComputeStageTag);
        if (key != null && compiled.Success) BinaryCache!.Put(key, compiled.Spirv);
        return compiled;
    }

    private ShaderCompileResult CompileUncached(string code, string filename, EnumShaderType stage, bool optimize = true)
    {
        CompileOptions* options = CreateOptions();
        if (!optimize) _api.CompileOptionsSetOptimizationLevel(options, OptimizationLevel.Zero);
        try
        {
            CompilationResult* compiled = CompileWith(code, filename, stage, options, preprocessOnly: false);
            try
            {
                return ReadCompiledSpirv(compiled);
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
        if (versionIndex < 0) return prefixCode + code;

        // A #version with nothing after it is a complete first line; the
        // prefix follows it rather than displacing it.
        int lineEnd = code.IndexOf('\n', versionIndex);
        if (lineEnd < 0) return code + "\n" + prefixCode;

        return code.Insert(lineEnd + 1, prefixCode);
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

    private ShaderCompileResult ReadCompiledSpirv(CompilationResult* compiled)
    {
        var result = new ShaderCompileResult();
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
        ComputeStageTag => ShaderKind.ComputeShader,
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

/// <summary>
/// Native shader sources (<c>sources/shaders-vk</c>, docs/vulkan.md)
/// resolve their own <c>#include "file.glsl"</c> through shaderc, unlike the game's GLSL 330
/// programs, whose includes <c>ShaderRegistry</c> expands before the rewriter sees them. The
/// options are the same as <see cref="Compile" />'s plus an include resolver, and the result is
/// never cached: the cache key covers only the top-level text, not the files it pulls in.
/// </summary>
internal sealed unsafe partial class ShaderCompiler
{
    /// <summary>
    /// Compiles a native GLSL 450 stage whose quoted includes resolve against the including
    /// file's directory first and then <paramref name="includeDirectory" />.
    /// </summary>
    public ShaderCompileResult CompileNative(string code, string filename, EnumShaderType stage, string includeDirectory)
    {
        var resolver = new IncludeResolver(includeDirectory);
        GCHandle handle = GCHandle.Alloc(resolver);

        CompileOptions* options = CreateOptions();
        try
        {
            _api.CompileOptionsSetIncludeCallbacks(
                options,
                new PfnIncludeResolveFn(&ResolveInclude),
                new PfnIncludeResultReleaseFn(&ReleaseInclude),
                (void*)GCHandle.ToIntPtr(handle));

            CompilationResult* compiled = CompileWith(code, filename, stage, options, preprocessOnly: false);
            try
            {
                return ReadCompiledSpirv(compiled);
            }
            finally
            {
                _api.ResultRelease(compiled);
            }
        }
        finally
        {
            _api.CompileOptionsRelease(options);
            handle.Free();
        }
    }

    private sealed class IncludeResolver
    {
        private readonly string _directory;

        public IncludeResolver(string directory) => _directory = directory;

        /// <summary>The resolved path and text, or null and the reason.</summary>
        public (string? Path, string Text) Resolve(string requested, string requesting, bool relative)
        {
            if (relative)
            {
                string? requestingDirectory = Path.GetDirectoryName(requesting);
                if (!string.IsNullOrEmpty(requestingDirectory))
                {
                    string besideRequester = Path.GetFullPath(Path.Combine(requestingDirectory, requested));
                    if (File.Exists(besideRequester)) return (besideRequester, File.ReadAllText(besideRequester));
                }
            }

            string inDirectory = Path.GetFullPath(Path.Combine(_directory, requested));
            if (File.Exists(inDirectory)) return (inDirectory, File.ReadAllText(inDirectory));

            return (null, "include '" + requested + "' not found in " + _directory);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static IncludeResult* ResolveInclude(
        void* userData, byte* requestedSource, int type, byte* requestingSource, nuint includeDepth)
    {
        var resolver = (IncludeResolver)GCHandle.FromIntPtr((IntPtr)userData).Target!;
        string requested = Marshal.PtrToStringUTF8((IntPtr)requestedSource) ?? "";
        string requesting = Marshal.PtrToStringUTF8((IntPtr)requestingSource) ?? "";

        (string? path, string text) = resolver.Resolve(requested, requesting, relative: type == (int)IncludeType.Relative);

        // shaderc's convention: an empty source name marks a failure, and the content is the message.
        var include = (IncludeResult*)NativeMemory.AllocZeroed((nuint)sizeof(IncludeResult));
        include->SourceName = CopyUtf8(path ?? "", out nuint nameLength);
        include->SourceNameLength = nameLength;
        include->Content = CopyUtf8(text, out nuint contentLength);
        include->ContentLength = contentLength;
        return include;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void ReleaseInclude(void* userData, IncludeResult* include)
    {
        if (include == null) return;
        NativeMemory.Free(include->SourceName);
        NativeMemory.Free(include->Content);
        NativeMemory.Free(include);
    }

    private static byte* CopyUtf8(string text, out nuint length)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        var copy = (byte*)NativeMemory.Alloc((nuint)Math.Max(1, bytes.Length));
        bytes.AsSpan().CopyTo(new Span<byte>(copy, bytes.Length));
        length = (nuint)bytes.Length;
        return copy;
    }
}
