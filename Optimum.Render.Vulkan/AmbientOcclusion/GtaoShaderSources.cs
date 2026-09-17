using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.AmbientOcclusion;

/// <summary>
/// The AO compute shaders, <c>sources/shaders-vk/gtao/*.comp</c>, embedded in the renderer
/// assembly (so deploy and the packagers carry them with the DLL). Expands their
/// <c>#include "x.glsl"</c> lines from the same directory and defines the storage format
/// qualifiers of the formats the device chose right after <c>#version</c>.
/// </summary>
internal static class GtaoShaderSources
{
    public const string ResourcePrefix = "shaders-vk/gtao/";

    private static readonly ConcurrentDictionary<string, string> Raw = new(StringComparer.Ordinal);
    private static readonly Regex IncludeLine = new("^[ \\t]*#include[ \\t]+\"([^\"]+)\"[ \\t]*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    /// <summary>A resource of the gtao directory, as committed.</summary>
    public static string Read(string fileName) => Raw.GetOrAdd(fileName, name =>
    {
        Assembly assembly = typeof(GtaoShaderSources).Assembly;
        using Stream? stream = assembly.GetManifestResourceStream(ResourcePrefix + name);
        if (stream == null) throw new FileNotFoundException("embedded AO shader missing: " + ResourcePrefix + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    /// <summary>
    /// The compilable source of <paramref name="fileName" />: includes expanded (each file
    /// once) and <c>GTAO_DEPTH_FORMAT</c> / <c>GTAO_TERM_FORMAT</c> defined.
    /// </summary>
    public static string Build(string fileName, Format depthFormat, Format termFormat)
    {
        string source = Expand(Read(fileName), new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal));
        int versionEnd = source.IndexOf('\n', source.IndexOf("#version", StringComparison.Ordinal)) + 1;
        string defines = "#define GTAO_DEPTH_FORMAT " + Qualifier(depthFormat) + "\n" +
                         "#define GTAO_TERM_FORMAT " + Qualifier(termFormat) + "\n";
        return source.Insert(versionEnd, defines);
    }

    private static string Expand(string source, System.Collections.Generic.HashSet<string> included) =>
        IncludeLine.Replace(source, match =>
        {
            string name = match.Groups[1].Value;
            return included.Add(name) ? Expand(Read(name), included) : "";
        });

    /// <summary>The storage image format qualifier of a format a storage texture can have.</summary>
    public static string Qualifier(Format format) => format switch
    {
        Format.R8Unorm => "r8",
        Format.R8G8Unorm => "rg8",
        Format.R8G8B8A8Unorm => "rgba8",
        Format.R16Sfloat => "r16f",
        Format.R32Sfloat => "r32f",
        Format.R16G16B16A16Sfloat => "rgba16f",
        Format.R32G32B32A32Sfloat => "rgba32f",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "no storage qualifier for this format"),
    };
}
