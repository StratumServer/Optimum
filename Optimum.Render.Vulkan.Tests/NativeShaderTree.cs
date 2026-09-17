using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The native shader tree (<c>sources/shaders-vk</c>) as the tests see it: the include files, the
/// machine-readable header every ported include carries, include closures, and a shaderc compiler
/// that resolves the includes.
///
/// A port's header states what the GLSL 330 file declared and where each name now comes from:
/// <code>
/// // optimum-port-of: fogandlight.fsh
/// // optimum-port: verbatim | transformed
/// // optimum-frame-owner: fogandlight.fsh          (members this file owns read the frame block)
/// // optimum-frame-texture: sampler2DShadow shadowMapFar
/// // optimum-program-uniform: float flatFogDensity  (declared by the including program)
/// // optimum-program-symbol: vec4 rgbaFog            (a non-uniform name the program supplies)
/// </code>
/// </summary>
internal static class NativeShaderTree
{
    public static string IncludeDirectory =>
        Path.Combine(ShaderCorpus.RepositoryRoot, "sources", "shaders-vk", "include");

    public static string Read(string include) =>
        File.ReadAllText(Path.Combine(IncludeDirectory, include)).Replace("\r\n", "\n");

    public static List<string> IncludeNames() =>
        Directory.EnumerateFiles(IncludeDirectory, "*.glsl")
            .Select(path => Path.GetFileName(path)!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

    public readonly record struct Declaration(string Type, string Name, string Text);

    public sealed class Port
    {
        public string Include = "";
        public string GameFile = "";
        public string Kind = "";
        public string? FrameOwner;
        public readonly List<Declaration> FrameTextures = new();
        public readonly List<Declaration> ProgramUniforms = new();
        public readonly List<Declaration> ProgramSymbols = new();
    }

    private static readonly Regex HeaderLine = new(@"^// optimum-([a-z-]+): (.+)$", RegexOptions.Multiline);
    private static readonly Regex DeclarationText = new(@"^(\w+)\s+(\w+)\s*(\[[^\]]*\])?\s*(=.*)?$");
    private static readonly Regex IncludeLine = new(@"^\s*#include\s+""([^""]+)""", RegexOptions.Multiline);

    /// <summary>The header of a ported include, or null for a file that ports nothing (bindings, motion, ...).</summary>
    public static Port? PortOf(string include)
    {
        string text = Read(include);
        var port = new Port { Include = include };
        foreach (Match line in HeaderLine.Matches(text))
        {
            string value = line.Groups[2].Value.Trim();
            switch (line.Groups[1].Value)
            {
                case "port-of": port.GameFile = value; break;
                case "port": port.Kind = value; break;
                case "frame-owner": port.FrameOwner = value; break;
                case "frame-texture": port.FrameTextures.Add(Parse(value)); break;
                case "program-uniform": port.ProgramUniforms.Add(Parse(value)); break;
                case "program-symbol": port.ProgramSymbols.Add(Parse(value)); break;
            }
        }
        return port.GameFile.Length == 0 ? null : port;
    }

    private static Declaration Parse(string value)
    {
        Match match = DeclarationText.Match(value);
        if (!match.Success) throw new FormatException("bad header declaration: " + value);
        return new Declaration(match.Groups[1].Value, match.Groups[2].Value,
            match.Groups[1].Value + " " + match.Groups[2].Value + match.Groups[3].Value);
    }

    public static IEnumerable<string> DirectIncludes(string include) =>
        IncludeLine.Matches(Read(include)).Select(match => match.Groups[1].Value);

    /// <summary>The include and everything it pulls in, transitively.</summary>
    public static List<string> Closure(string include)
    {
        var seen = new List<string>();
        var pending = new Stack<string>();
        pending.Push(include);
        while (pending.Count > 0)
        {
            string next = pending.Pop();
            if (seen.Contains(next)) continue;
            seen.Add(next);
            foreach (string child in DirectIncludes(next)) pending.Push(child);
        }
        return seen;
    }

    /// <summary>The stages an include can be compiled in: a .vsh port is vertex-only, .fsh and motion fragment-only.</summary>
    public static EnumShaderType[] StagesOf(string include)
    {
        Port? port = PortOf(include);
        string gameFile = port?.GameFile ?? "";
        if (gameFile.EndsWith(".vsh", StringComparison.Ordinal)) return new[] { EnumShaderType.VertexShader };
        if (gameFile.EndsWith(".fsh", StringComparison.Ordinal) || include == "motion.glsl")
        {
            return new[] { EnumShaderType.FragmentShader };
        }
        return new[] { EnumShaderType.VertexShader, EnumShaderType.FragmentShader };
    }

    public static bool TryCreateCompiler(out ShaderCompiler? compiler, out string reason)
    {
        try
        {
            compiler = new ShaderCompiler();
            reason = "";
            return true;
        }
        catch (Exception error) when (error is DllNotFoundException or InvalidOperationException)
        {
            compiler = null;
            reason = "shaderc unavailable: " + error.Message;
            return false;
        }
    }

    public static ShaderCompileResult Compile(ShaderCompiler compiler, string source, EnumShaderType stage, string name)
    {
        string extension = stage == EnumShaderType.VertexShader ? ".vert" : ".frag";
        return compiler.CompileNative(source, Path.Combine(IncludeDirectory, name + extension), stage, IncludeDirectory);
    }
}
