using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>The specialization constants of one native program, as a pipeline's stages take them.</summary>
internal sealed class NativeSpecialization
{
    /// <summary>One constant: its id, and where its 4 bytes sit in <see cref="Data" />.</summary>
    public readonly record struct Entry(uint Id, uint Offset, uint Size);

    public Entry[] Entries = Array.Empty<Entry>();
    public byte[] Data = Array.Empty<byte>();
}

/// <summary>What the GLSL 330 text of a program says that the manifest does not: sampler units and initializers.</summary>
internal sealed class GlslUniformOracle
{
    /// <summary>
    /// <c>ShaderProgram.collectUniformNames</c> (build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderProgram.cs:57-68),
    /// pattern and options verbatim.
    /// </summary>
    private static readonly Regex CollectUniformNames = new(
        "(\\s|\\r\\n)uniform\\s*(?<type>float|int|ivec2|ivec3|ivec4|vec2|vec3|vec4|sampler2DShadow|sampler2D|samplerCube|mat3|mat4x3|mat4)\\s*(\\[[\\d\\w]+\\])?\\s*(?<var>[\\d\\w]+)",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture | RegexOptions.Compiled);

    private static readonly Regex Initializer = new(
        @"\buniform\s+\w+\s+(?<name>\w+)\s*=\s*(?<value>[^;]+);", RegexOptions.Compiled);

    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex LineComment = new(@"//[^\n]*", RegexOptions.Compiled);

    private readonly List<(string Name, string Type, int Unit)> _samplers = new();
    private readonly Dictionary<string, string> _initializers = new(StringComparer.Ordinal);

    /// <param name="stageCodes">The include-expanded GLSL 330 stage texts in the order the client scans them (vertex, fragment).</param>
    public GlslUniformOracle(IEnumerable<string> stageCodes)
    {
        var units = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<(string Name, string Type)>();
        foreach (string code in stageCodes)
        {
            foreach (Match match in CollectUniformNames.Matches(code))
            {
                string type = match.Groups["type"].Value;
                if (!type.Contains("sampler", StringComparison.Ordinal)) continue;
                string name = match.Groups["var"].Value;
                // textureLocations[value] = textureLocations.Count: a name seen again takes the current count.
                units[name] = units.Count;
                order.Add((name, type));
            }

            string stripped = LineComment.Replace(BlockComment.Replace(code, " "), " ");
            foreach (Match match in Initializer.Matches(stripped))
            {
                _initializers.TryAdd(match.Groups["name"].Value, match.Groups["value"].Value.Trim());
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach ((string name, string type) in order)
        {
            if (seen.Add(name)) _samplers.Add((name, type, units[name]));
        }
    }

    /// <summary>Each sampler name once, with the unit the client assigns it, in unit order.</summary>
    public IEnumerable<(string Name, string Type, int Unit)> SamplersByUnit()
    {
        var sorted = new List<(string Name, string Type, int Unit)>(_samplers);
        sorted.Sort((a, b) => a.Unit.CompareTo(b.Unit));
        return sorted;
    }

    /// <summary>The initializer a GLSL 330 declaration of <paramref name="name" /> carries, or null.</summary>
    public string? InitializerOf(string name) => _initializers.TryGetValue(name, out string? value) ? value : null;
}

/// <summary>
/// The native shaders at runtime (docs/vulkan.md): the manifest and SPIR-V
/// beside <c>Optimum.Render.Vulkan.dll</c>, or a source tree compiled at device start, looked up per
/// program at <c>VulkanDevice.LinkProgram</c>.
///
/// Loaded once. SPIR-V is read and its SHA-256 checked the first time a variant is linked; a file that
/// fails the check fails that variant for the life of the library, and the program links through the
/// rewriter.
/// </summary>
internal sealed class NativeShaderLibrary
{
    /// <summary>
    /// <c>0</c> forces the rewriter for every program (A/B runs); <see cref="ForceValue" /> links natively even
    /// where the launcher's mod shader scan says a mod replaced the program (development runs without the launcher).
    /// </summary>
    public const string EnabledVariable = "OPTIMUM_VK_NATIVE_SHADERS";

    /// <summary>The <see cref="EnabledVariable" /> value that ignores the mod shader scan.</summary>
    public const string ForceValue = "force";

    /// <summary>Whether <see cref="EnabledVariable" />'s value asks to ignore the mod shader scan.</summary>
    public static bool IgnoresModScan(string? enabledVariable) =>
        string.Equals(enabledVariable?.Trim(), ForceValue, StringComparison.OrdinalIgnoreCase);

    /// <summary>A <c>sources/shaders-vk</c> tree to compile at device start instead of the shipped manifest.</summary>
    public const string SourceVariable = "OPTIMUM_VK_SHADER_SOURCE";

    public enum Mode { Off, Directory, Source }

    /// <summary>How a link request came out.</summary>
    public enum Outcome
    {
        /// <summary>No native program of that name (or a geometry stage): the rewriter, as before.</summary>
        Miss,
        /// <summary>Linked from the manifest.</summary>
        Native,
        /// <summary>A native program exists but could not be used: the rewriter, counted as failed.</summary>
        Failed,
    }

    public NativeShaderManifest Manifest { get; }

    /// <summary>Where the manifest came from, for the log.</summary>
    public string Origin { get; }

    private readonly string? _directory;
    private readonly IReadOnlyDictionary<string, byte[]>? _files;
    private readonly Dictionary<string, byte[]> _verified = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _rejected = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    private NativeShaderLibrary(NativeShaderManifest manifest, string origin, string? directory, IReadOnlyDictionary<string, byte[]>? files)
    {
        Manifest = manifest;
        Origin = origin;
        _directory = directory;
        _files = files;
    }

    // ------------------------------------------------------------------ loading

    /// <summary>
    /// Decides where native shaders come from. Off wins (the environment's <c>0</c> or the device setting);
    /// then an explicit directory (tests), then <see cref="SourceVariable" />, then <c>shaders-vk</c> beside
    /// the renderer assembly.
    /// </summary>
    public static (Mode Mode, string? Path, string Reason) Resolve(
        bool? enabledSetting, string? directorySetting, string? enabledVariable, string? sourceVariable, string? assemblyDirectory)
    {
        if (enabledSetting == false) return (Mode.Off, null, "native shaders off by device setting");
        if (enabledVariable?.Trim() == "0") return (Mode.Off, null, EnabledVariable + "=0");
        if (!string.IsNullOrEmpty(directorySetting)) return (Mode.Directory, directorySetting, "");
        if (!string.IsNullOrWhiteSpace(sourceVariable)) return (Mode.Source, sourceVariable, "");
        if (string.IsNullOrEmpty(assemblyDirectory)) return (Mode.Off, null, "renderer assembly location unknown");
        return (Mode.Directory, System.IO.Path.Combine(assemblyDirectory, NativeShaderManifest.DirectoryName), "");
    }

    /// <summary>
    /// Reads <c>shaders.manifest.json</c> from <paramref name="directory" />. Null, with the one reason, when it is
    /// missing, malformed, of another schema version, or built with other compile options than <paramref name="toolchain" />.
    ///
    /// A toolchain is <c>ShaderCompiler.Identity</c>: the options, then after the last <c>;</c> the shaderc library
    /// (its SHA-256, or the Silk package). Only the options must match: a manifest built on another platform or with
    /// another shaderc build is still accepted, because every stage's SHA-256 pins the SPIR-V bytes themselves. The
    /// differing library is then the non-empty <paramref name="reason" /> of a loaded library, for the status line.
    /// </summary>
    public static NativeShaderLibrary? Load(string directory, string toolchain, out string reason)
    {
        string path = System.IO.Path.Combine(directory, NativeShaderManifest.FileName);
        if (!File.Exists(path))
        {
            reason = "no manifest at " + path;
            return null;
        }

        NativeShaderManifest manifest;
        try
        {
            manifest = NativeShaderManifest.Load(path);
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException
                                          or InvalidOperationException or FormatException or System.Text.Json.JsonException)
        {
            reason = "manifest " + path + " rejected: " + error.Message;
            return null;
        }

        reason = "";
        if (manifest.Toolchain != toolchain)
        {
            (string builtOptions, string builtLibrary) = SplitToolchain(manifest.Toolchain);
            (string ownOptions, string ownLibrary) = SplitToolchain(toolchain);
            if (builtOptions != ownOptions)
            {
                reason = "manifest " + path + " was built by toolchain '" + manifest.Toolchain + "', this renderer compiles with '" + toolchain + "'";
                return null;
            }
            reason = "manifest built with shader library '" + builtLibrary + "', this renderer has '" + ownLibrary +
                     "' (same options; the SPIR-V is pinned by its per-stage sha256)";
        }

        return new NativeShaderLibrary(manifest, path, directory, null);
    }

    /// <summary>A toolchain identity split at its last <c>;</c>: the compile options and the shaderc library.</summary>
    internal static (string Options, string Library) SplitToolchain(string? toolchain)
    {
        toolchain ??= "";
        int split = toolchain.LastIndexOf(';');
        return split < 0 ? (toolchain, "") : (toolchain[..split], toolchain[(split + 1)..]);
    }

    /// <summary>
    /// Compiles a source tree through <see cref="NativeShaderBuilder" />, the offline tool's library. Programs
    /// that built are usable even when others failed; <paramref name="reason" /> then names the failures.
    /// </summary>
    public static NativeShaderLibrary? BuildFromSource(string sourceDirectory, ShaderCompiler compiler, out string reason)
    {
        if (!Directory.Exists(sourceDirectory))
        {
            reason = "source tree " + sourceDirectory + " not found";
            return null;
        }

        NativeShaderBuildResult result;
        try
        {
            result = new NativeShaderBuilder(compiler).Build(sourceDirectory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            reason = "source tree " + sourceDirectory + " did not build: " + error.Message;
            return null;
        }

        reason = result.Success
            ? ""
            : result.Errors.Count + " build error(s) in " + sourceDirectory + ", first: " + result.Errors[0];
        return new NativeShaderLibrary(result.Manifest, "source " + sourceDirectory, null, result.Files);
    }

    /// <summary>A library over an in-memory build, for tests.</summary>
    internal static NativeShaderLibrary FromBuild(NativeShaderBuildResult result) =>
        new(result.Manifest, "in-memory build", null, result.Files);

    /// <summary>The verified bytes of one stage's SPIR-V; false with the reason when unreadable or its hash disagrees.</summary>
    public bool TryGetSpirv(NativeStage stage, out byte[] spirv, out string error)
    {
        lock (_lock)
        {
            if (_verified.TryGetValue(stage.Spirv, out spirv!))
            {
                error = "";
                return true;
            }
            if (_rejected.TryGetValue(stage.Spirv, out error!))
            {
                spirv = Array.Empty<byte>();
                return false;
            }

            byte[]? bytes = null;
            if (_files != null)
            {
                if (!_files.TryGetValue(stage.Spirv, out bytes)) error = stage.Spirv + " is not in the build";
            }
            else
            {
                string path = System.IO.Path.Combine(_directory!, stage.Spirv);
                try
                {
                    bytes = File.ReadAllBytes(path);
                }
                catch (Exception readError) when (readError is IOException or UnauthorizedAccessException)
                {
                    error = stage.Spirv + " unreadable: " + readError.Message;
                }
            }

            if (bytes != null)
            {
                string actual = Convert.ToHexStringLower(SHA256.HashData(bytes));
                if (actual == stage.Sha256 && bytes.Length % 4 == 0)
                {
                    _verified[stage.Spirv] = bytes;
                    spirv = bytes;
                    error = "";
                    return true;
                }
                error = stage.Spirv + " sha256 " + actual + ", manifest says " + stage.Sha256;
            }

            _rejected[stage.Spirv] = error;
            spirv = Array.Empty<byte>();
            return false;
        }
    }

    // ------------------------------------------------------------------ defines

    private static readonly Regex Define = new(@"^[ \t]*#[ \t]*define[ \t]+(\w+)(?:[ \t]+([^\r\n]*?))?[ \t]*\r?$", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// The <c>#define</c>s of a program's prefixes (ShaderRegistry.registerDefaultShaderCodePrefixes plus the
    /// program's own), stages merged in the order given. A define without a value maps to the empty string.
    /// </summary>
    public static Dictionary<string, string> ParseDefines(IEnumerable<string> prefixes)
    {
        var defines = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string prefix in prefixes)
        {
            foreach (Match match in Define.Matches(prefix ?? ""))
            {
                defines[match.Groups[1].Value] = match.Groups[2].Value.Trim();
            }
        }
        return defines;
    }

    /// <summary>The per-registration axes the GLSL 330 sources test with <c>defined()</c>: present is 1.</summary>
    private static readonly HashSet<string> PresenceAxes = new(StringComparer.Ordinal) { "ALLOWDEPTHOFFSET", "GLOWSUB", "VEC3SCALE" };

    /// <summary>
    /// The value of each axis of <paramref name="axes" /> as the manifest keys it (contract section 5):
    /// <c>GBUFFER</c> is <c>SSAOLEVEL &gt; 0</c>, the per-registration axes are 1 when defined, every other axis is
    /// its define's value. An absent define is 0, as in an <c>#if</c>. False for a value that is not 0 or 1.
    /// </summary>
    public static bool TryAxisValues(IEnumerable<string> axes, IReadOnlyDictionary<string, string> defines,
        out Dictionary<string, int> values, out string error)
    {
        values = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string axis in axes)
        {
            int value;
            if (PresenceAxes.Contains(axis))
            {
                value = defines.ContainsKey(axis) ? 1 : 0;
            }
            else if (axis == "GBUFFER")
            {
                if (!TryIntDefine(defines, "SSAOLEVEL", out int ssao, out error)) return false;
                value = ssao > 0 ? 1 : 0;
            }
            else
            {
                if (!TryIntDefine(defines, axis, out value, out error)) return false;
                if (value is not (0 or 1))
                {
                    error = "axis " + axis + " is " + value + ", the manifest has 0 and 1";
                    return false;
                }
            }
            values[axis] = value;
        }
        error = "";
        return true;
    }

    private static bool TryIntDefine(IReadOnlyDictionary<string, string> defines, string name, out int value, out string error)
    {
        error = "";
        if (!defines.TryGetValue(name, out string? text))
        {
            value = 0;
            return true;
        }
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) return true;
        error = "#define " + name + " '" + text + "' is not an integer";
        return false;
    }

    /// <summary>The manifest variant key of <paramref name="program" /> for a program's defines; null with the reason when it has none.</summary>
    public static string? VariantKeyFor(NativeProgram program, IReadOnlyDictionary<string, string> defines, out string error)
    {
        if (!TryAxisValues(program.Axes, defines, out Dictionary<string, int> values, out error)) return null;
        return NativeShaderManifest.VariantKey(program.Axes, values);
    }

    /// <summary>
    /// The specialization data for a variant: every constant the manifest lists, valued from the define
    /// <see cref="SpecializationConvention" /> maps it to (the same setting ShaderRegistry stamps into the prefix),
    /// 0 when the prefix does not define it.
    /// </summary>
    public static bool TryBuildSpecialization(NativeVariant variant, IReadOnlyDictionary<string, string> defines,
        out NativeSpecialization specialization, out string error)
    {
        var entries = new List<NativeSpecialization.Entry>();
        var data = new List<byte>();
        foreach (NativeSpecConstant constant in variant.SpecializationConstants)
        {
            SpecializationConvention.Constant? convention = null;
            foreach (SpecializationConvention.Constant candidate in SpecializationConvention.Constants)
            {
                if (candidate.Id == (uint)constant.Id) convention = candidate;
            }
            if (convention == null || convention.Value.Name != constant.Name)
            {
                specialization = new NativeSpecialization();
                error = "specialization constant " + constant.Id + " '" + constant.Name + "' is not in the convention";
                return false;
            }

            defines.TryGetValue(convention.Value.Define, out string? text);
            byte[] bytes;
            switch (constant.Type)
            {
                case "float":
                    float f = 0;
                    if (text != null && !float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out f))
                    {
                        specialization = new NativeSpecialization();
                        error = "#define " + convention.Value.Define + " '" + text + "' is not a float";
                        return false;
                    }
                    bytes = BitConverter.GetBytes(f);
                    break;
                case "int" or "uint" or "bool":
                    int i = 0;
                    if (text != null && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out i))
                    {
                        specialization = new NativeSpecialization();
                        error = "#define " + convention.Value.Define + " '" + text + "' is not an integer";
                        return false;
                    }
                    bytes = BitConverter.GetBytes(constant.Type == "bool" ? (i != 0 ? 1 : 0) : i);
                    break;
                default:
                    specialization = new NativeSpecialization();
                    error = "specialization constant '" + constant.Name + "' has type " + constant.Type;
                    return false;
            }
            entries.Add(new NativeSpecialization.Entry((uint)constant.Id, (uint)data.Count, (uint)bytes.Length));
            data.AddRange(bytes);
        }

        specialization = new NativeSpecialization { Entries = entries.ToArray(), Data = data.ToArray() };
        error = "";
        return true;
    }

    // ------------------------------------------------------------------ linking

    /// <summary>
    /// Looks <paramref name="passName" /> up and, on a hit, builds the program from the manifest: SPIR-V per stage,
    /// the layout, the specialization. <paramref name="stages" /> are the GLSL 330 stages the program carries, for
    /// the defines, the sampler units and the initializers.
    /// </summary>
    public Outcome TryLink(string passName, IReadOnlyList<ShaderStageSource> stages,
        out TranslatedProgram? program, out string detail)
    {
        program = null;
        detail = "";

        ShaderStageSource? vertex = null, fragment = null;
        foreach (ShaderStageSource stage in stages)
        {
            if (stage.Stage == EnumShaderType.VertexShader) vertex = stage;
            else if (stage.Stage == EnumShaderType.FragmentShader) fragment = stage;
            else return Outcome.Miss;
        }

        NativeProgram? native = Manifest.FindProgram(passName);
        if (native == null) return Outcome.Miss;

        if (vertex == null || fragment == null)
        {
            detail = "the GLSL 330 program lacks a vertex or fragment stage";
            return Outcome.Failed;
        }

        Dictionary<string, string> defines = ParseDefines(new[] { vertex.PrefixCode, fragment.PrefixCode });
        string? key = VariantKeyFor(native, defines, out string keyError);
        if (key == null)
        {
            detail = keyError;
            return Outcome.Failed;
        }
        detail = key;

        NativeVariant? variant = native.Variants.Find(v => v.Key == key);
        if (variant == null)
        {
            detail = "no variant '" + key + "'";
            return Outcome.Failed;
        }

        var translated = new TranslatedProgram { IsNative = true };
        foreach ((string stageName, EnumShaderType type) in new[]
                 { ("vertex", EnumShaderType.VertexShader), ("fragment", EnumShaderType.FragmentShader) })
        {
            NativeStage? stage = variant.Stages.Find(s => s.Stage == stageName);
            if (stage == null)
            {
                detail = "[" + key + "] has no " + stageName + " stage";
                return Outcome.Failed;
            }
            if (!TryGetSpirv(stage, out byte[] spirv, out string spirvError))
            {
                detail = "[" + key + "] " + spirvError;
                return Outcome.Failed;
            }
            translated.Spirv[type] = spirv;
        }

        if (!TryBuildSpecialization(variant, defines, out NativeSpecialization specialization, out string specError))
        {
            detail = "[" + key + "] " + specError;
            return Outcome.Failed;
        }
        translated.Specialization = specialization;

        var oracle = new GlslUniformOracle(new[] { vertex.Code, fragment.Code });
        translated.Layout = ProgramInterfaceLayout.FromNative(variant, oracle);
        if (translated.Layout.HasErrors)
        {
            detail = "[" + key + "] " + string.Join("; ", translated.Layout.Errors);
            return Outcome.Failed;
        }

        program = translated;
        return Outcome.Native;
    }
}
