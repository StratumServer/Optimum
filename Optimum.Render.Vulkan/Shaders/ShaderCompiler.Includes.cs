using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Silk.NET.Shaderc;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>
/// Native shader sources (<c>sources/shaders-vk</c>, docs/vulkan-native-shaders.md section 1)
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
        var result = new ShaderCompileResult();
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
