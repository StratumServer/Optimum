// Source: Optimum.Render.Vulkan.Tests/ShaderCorpus.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;

/// <summary>
/// Loads the game's shaders the way the client does, so translation is tested
/// against what actually reaches the driver rather than against the files on disk.
///
/// Two steps matter. ShaderRegistry expands <c>#include</c> before compiling, and
/// it de-duplicates per program, so a shader that pulls in fogandlight.fsh twice
/// gets it once. And registerDefaultShaderCodePrefixes prepends a block of
/// <c>#define</c>s whose values change which declarations survive the
/// preprocessor - a shader compiled with SSAOLEVEL 0 declares different uniforms
/// than the same file at SSAOLEVEL 2.
/// </summary>
internal static class ShaderCorpus
{
    /// <summary>
    /// The vanilla assets, or null when this checkout has not bootstrapped. The
    /// shaders are proprietary and never committed, so tests that need them skip
    /// rather than fail when they are absent.
    /// </summary>
    public static string? AssetRoot => _assetRoot ??= FindAssetRoot();
    private static string? _assetRoot;

    private static string? FindAssetRoot()
    {
        string? fromEnvironment = Environment.GetEnvironmentVariable("VINTAGE_STORY_ASSETS");
        if (!string.IsNullOrEmpty(fromEnvironment) &&
            Directory.Exists(Path.Combine(fromEnvironment, "shaders")))
        {
            return fromEnvironment;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(
                directory.FullName, ".vanilla", "win-x64", "vintagestory", "assets", "game");
            if (Directory.Exists(Path.Combine(candidate, "shaders")))
            {
                return candidate;
            }
            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>The repository root, found by walking up to the solution file.</summary>
    public static string RepositoryRoot => _repositoryRoot ??= FindRepositoryRoot();
    private static string? _repositoryRoot;

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "VintageStory.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Repository root with VintageStory.slnx not found.");
    }

    /// <summary>
    /// Every shader file, with Optimum's own overlays replacing their vanilla
    /// counterparts - which is what `make deploy` copies over the install, so it
    /// is what actually runs.
    /// </summary>
    public static Dictionary<string, string> LoadShaderFiles()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (AssetRoot != null)
        {
            foreach (string path in Directory.EnumerateFiles(Path.Combine(AssetRoot, "shaders")))
            {
                files[Path.GetFileName(path)] = File.ReadAllText(path);
            }
        }

        string overlays = Path.Combine(RepositoryRoot, "sources", "shaders");
        if (Directory.Exists(overlays))
        {
            foreach (string path in Directory.EnumerateFiles(overlays))
            {
                files[Path.GetFileName(path)] = File.ReadAllText(path);
            }
        }

        return files;
    }

    /// <summary>
    /// The shader includes, with Optimum's own overlays replacing their vanilla
    /// counterparts - the same relationship <see cref="LoadShaderFiles" /> has,
    /// and the same one `make deploy` and the package scripts produce on disk.
    /// Without the overlay the corpus would translate the vanilla vertexwarp.vsh
    /// while the client runs Optimum's WarpState one.
    /// </summary>
    public static Dictionary<string, string> LoadIncludes()
    {
        var includes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (AssetRoot != null)
        {
            string directory = Path.Combine(AssetRoot, "shaderincludes");
            if (Directory.Exists(directory))
            {
                foreach (string path in Directory.EnumerateFiles(directory))
                {
                    includes[Path.GetFileName(path)] = File.ReadAllText(path);
                }
            }
        }

        string overlays = Path.Combine(RepositoryRoot, "sources", "shaderincludes");
        if (Directory.Exists(overlays))
        {
            foreach (string path in Directory.EnumerateFiles(overlays))
            {
                includes[Path.GetFileName(path)] = File.ReadAllText(path);
            }
        }

        return includes;
    }

    /// <summary>
    /// Program base names that have both a vertex and a fragment shader, which is
    /// what ShaderRegistry requires of a loadable program.
    /// </summary>
    public static List<string> ProgramNames(Dictionary<string, string> files)
    {
        return files.Keys
            .Where(name => name.EndsWith(".vsh", StringComparison.OrdinalIgnoreCase))
            .Select(name => name[..^4])
            .Where(name => files.ContainsKey(name + ".fsh"))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
    }

    private static readonly Regex IncludePattern =
        new(@"^#include\s+(.*)", RegexOptions.Multiline | RegexOptions.Compiled);

    /// <summary>
    /// Mirrors ShaderRegistry.HandleIncludes, including its de-duplication: a
    /// file already pulled into this program expands to nothing the second time.
    /// </summary>
    public static string ExpandIncludes(
        string code, Dictionary<string, string> includes, HashSet<string>? seen = null)
    {
        seen ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        return IncludePattern.Replace(code, match =>
        {
            string filename = match.Groups[1].Value.Trim().ToLowerInvariant();
            if (!seen.Add(filename)) return "";
            return includes.TryGetValue(filename, out string? included)
                ? ExpandIncludes(included, includes, seen)
                : "";
        });
    }

    /// <summary>A set of the defines the client injects per program.</summary>
    public sealed class ShaderVariant
    {
        public string Name = "";
        public int Fxaa;
        public int SsaoLevel;
        public int Bloom;
        public int GodRays;
        public int ShadowQuality;
        public int DynLights;
        public int UseSsbo;
        public int WavingStuff = 1;
        public int FoamEffect = 1;
        public int ShinyEffect = 1;
        public int NormalView;
        public int UseOit = 1;
        public int GreedyMesh;
        public float MinBright;
        public int MaxAnimatedElements = 35;
        /// <summary>TAA motion-vector writers compiled in (OptimumConfig.EffectiveTaa).</summary>
        public int TaaMotion;
        /// <summary>Primary colour attachment the motion texture occupies: 4 with the SSAO G-buffer, 2 without.</summary>
        public int TaaMotionLocation = 2;
        /// <summary>ShaderRegistry's OPTIMUMAO: 1 while the Vulkan platform runs GTAO.</summary>
        public int OptimumAo;

        /// <summary>
        /// Defines a caller put on the program itself before the engine's block,
        /// the way ModSystemFpHands stamps ALLOWDEPTHOFFSET on its two private
        /// copies of standard and entityanimated. Those copies are the only place
        /// the gl_FragDepth writer exists, so without this the corpus never
        /// translates it.
        /// </summary>
        public string ExtraPrefix = "";

        public override string ToString() => Name;
    }

    /// <summary>
    /// Variants chosen to move every define that gates a declaration. The two
    /// extremes catch the common cases; the middle rows catch the combinations
    /// where one feature is on and its neighbours are not, which is where a
    /// conditional uniform is most likely to be missed.
    /// </summary>
    public static IEnumerable<ShaderVariant> Variants()
    {
        // USEOIT tracks ShaderProgramBase.Oit, which defaults to true for every
        // program; only the second Entityanimated registration turns it off. The
        // shaders that call into oit.fsh do so unconditionally, so a global
        // USEOIT 0 is not a configuration the client can produce.
        yield return new ShaderVariant
        {
            Name = "everything-off",
            WavingStuff = 0, FoamEffect = 0, ShinyEffect = 0,
        };
        yield return new ShaderVariant
        {
            Name = "everything-on",
            Fxaa = 1, SsaoLevel = 2, Bloom = 1, GodRays = 2, ShadowQuality = 2,
            DynLights = 8, UseSsbo = 1, NormalView = 1, GreedyMesh = 1, MinBright = 0.1f,
        };
        yield return new ShaderVariant
        {
            Name = "ssao-only",
            SsaoLevel = 1, DynLights = 4,
        };
        yield return new ShaderVariant
        {
            Name = "shadows-and-ssbo",
            ShadowQuality = 2, DynLights = 8, UseSsbo = 1,
        };
        // TAA on, without the SSAO G-buffer (motion at attachment 2) and with it
        // (attachment 4): the motion-vector writers only exist in these, and the
        // two rows move the output location the same way the client does.
        yield return new ShaderVariant
        {
            Name = "taa-no-ssao",
            TaaMotion = 1, TaaMotionLocation = 2,
        };
        yield return new ShaderVariant
        {
            Name = "taa-with-ssao",
            SsaoLevel = 2, DynLights = 4, TaaMotion = 1, TaaMotionLocation = 4,
        };
        // TAA on with the waving-stuff, foam and shiny settings off. Those are
        // ordinary client settings, and WAVINGSTUFF in particular is what gates
        // the bodies of every vertexwarp function the motion writers replay for
        // the previous frame - so with TAA on it is a shipped combination that
        // no other row produced (the "everything-off" row carries TAAMOTION 0).
        // The Vulkan AO row: TAA with the SSAO G-buffer and OPTIMUMAO 1, the combination that
        // compiles in the class-channel writes (chunkopaque's no-cull flag, the hand-view class
        // in standard and entityanimated) and scene-ssao's GTAO compose branch.
        yield return new ShaderVariant
        {
            Name = "taa-with-gtao",
            SsaoLevel = 2, DynLights = 4, TaaMotion = 1, TaaMotionLocation = 4, OptimumAo = 1,
        };
        yield return new ShaderVariant
        {
            Name = "taa-no-waving",
            TaaMotion = 1, TaaMotionLocation = 2,
            WavingStuff = 0, FoamEffect = 0, ShinyEffect = 0,
        };
    }

    /// <summary>
    /// Reproduces registerDefaultShaderCodePrefixes, including Optimum's own
    /// greedy-mesh defines from the ShaderRegistry patch.
    /// </summary>
    public static string PrefixFor(EnumShaderType stage, ShaderVariant variant)
    {
        var lines = new List<string>();

        if (stage == EnumShaderType.FragmentShader)
        {
            lines.Add($"#define FXAA {variant.Fxaa}");
            lines.Add($"#define SSAOLEVEL {variant.SsaoLevel}");
            lines.Add($"#define NORMALVIEW {variant.NormalView}");
            lines.Add($"#define BLOOM {variant.Bloom}");
            lines.Add($"#define GODRAYS {variant.GodRays}");
            lines.Add($"#define FOAMEFFECT {variant.FoamEffect}");
            lines.Add($"#define SHINYEFFECT {variant.ShinyEffect}");
            lines.Add($"#define SHADOWQUALITY {variant.ShadowQuality}");
            lines.Add($"#define DYNLIGHTS {variant.DynLights}");
            lines.Add($"#define USEOIT {variant.UseOit}");
            lines.Add($"#define GREEDYMESH {variant.GreedyMesh}");
            lines.Add($"#define GREEDYMESH_GRAD 0");
            lines.Add($"#define TAAMOTION {variant.TaaMotion}");
            lines.Add($"#define TAAMOTIONLOCATION {variant.TaaMotionLocation}");
            lines.Add($"#define OPTIMUMAO {variant.OptimumAo}");
        }
        else
        {
            lines.Add($"#define USESSBO {variant.UseSsbo}");
            lines.Add($"#define WAVINGSTUFF {variant.WavingStuff}");
            lines.Add($"#define FOAMEFFECT {variant.FoamEffect}");
            lines.Add($"#define SSAOLEVEL {variant.SsaoLevel}");
            lines.Add($"#define NORMALVIEW {variant.NormalView}");
            lines.Add($"#define SHINYEFFECT {variant.ShinyEffect}");
            lines.Add($"#define GODRAYS {variant.GodRays}");
            lines.Add($"#define MINBRIGHT {variant.MinBright.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
            lines.Add($"#define SHADOWQUALITY {variant.ShadowQuality}");
            lines.Add($"#define DYNLIGHTS {variant.DynLights}");
            lines.Add($"#define MAXANIMATEDELEMENTS {variant.MaxAnimatedElements}");
            lines.Add($"#define GREEDYMESH {variant.GreedyMesh}");
            lines.Add($"#define TAAMOTION {variant.TaaMotion}");
            lines.Add($"#define TAAMOTIONLOCATION {variant.TaaMotionLocation}");
            lines.Add($"#define OPTIMUMAO {variant.OptimumAo}");
        }

        string prefix = string.Join("\r\n", lines) + "\r\n";
        // The client puts the program's own PrefixCode first and appends the
        // engine block to it (ShaderRegistry.registerDefaultShaderCodePrefixes
        // does `PrefixCode = PrefixCode + ...`), so a caller's defines lead.
        return variant.ExtraPrefix.Length > 0
            ? variant.ExtraPrefix + "\r\n" + prefix
            : prefix;
    }

    /// <summary>Builds the two stages of one program, ready for translation.</summary>
    public static List<ShaderStageSource> BuildProgram(
        string programName,
        Dictionary<string, string> files,
        Dictionary<string, string> includes,
        ShaderVariant variant)
    {
        var stages = new List<ShaderStageSource>();

        foreach ((string extension, EnumShaderType stage) in new[]
        {
            (".vsh", EnumShaderType.VertexShader),
            (".fsh", EnumShaderType.FragmentShader),
            (".gsh", EnumShaderType.GeometryShader),
        })
        {
            if (!files.TryGetValue(programName + extension, out string? code)) continue;

            stages.Add(new ShaderStageSource
            {
                Stage = stage,
                Code = ExpandIncludes(code, includes),
                PrefixCode = PrefixFor(stage, variant),
                Filename = programName + extension,
            });
        }

        return stages;
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/NativeShaderTree.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;

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
}

// Source: Optimum.Render.Vulkan.Tests/SpirvReader.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// The few SPIR-V facts the native-shader tests check - names, decorations, struct members,
/// variables and specialization constants - read straight from the word stream. No reflection
/// library exists in the tree (docs/vulkan.md plans
/// <c>Shaders/SpirvReflection.cs</c>); until it does, this is deliberately minimal.
/// </summary>
internal sealed class SpirvReader
{
    private const uint Magic = 0x07230203;

    private const ushort OpName = 5;
    private const ushort OpMemberName = 6;
    private const ushort OpTypeInt = 21;
    private const ushort OpTypeFloat = 22;
    private const ushort OpTypeArray = 28;
    private const ushort OpTypeStruct = 30;
    private const ushort OpTypePointer = 32;
    private const ushort OpSpecConstant = 50;
    private const ushort OpVariable = 59;
    private const ushort OpDecorate = 71;
    private const ushort OpMemberDecorate = 72;

    public const uint DecorationSpecId = 1;
    public const uint DecorationArrayStride = 6;
    public const uint DecorationBinding = 33;
    public const uint DecorationDescriptorSet = 34;
    public const uint DecorationOffset = 35;

    public const uint StorageUniform = 2;
    public const uint StoragePushConstant = 9;

    public readonly Dictionary<uint, string> Names = new();
    public readonly Dictionary<(uint Type, uint Member), string> MemberNames = new();
    public readonly Dictionary<uint, Dictionary<uint, uint>> Decorations = new();
    public readonly Dictionary<(uint Type, uint Member), Dictionary<uint, uint>> MemberDecorations = new();
    public readonly Dictionary<uint, uint[]> Structs = new();
    public readonly Dictionary<uint, uint> ArrayElements = new();
    public readonly Dictionary<uint, (uint Storage, uint Type)> Pointers = new();
    public readonly Dictionary<uint, (uint PointerType, uint Storage)> Variables = new();
    public readonly Dictionary<uint, string> ScalarTypes = new();
    public readonly Dictionary<uint, uint> SpecConstantTypes = new();

    public static SpirvReader Parse(byte[] bytes)
    {
        if (bytes.Length < 20 || bytes.Length % 4 != 0) throw new ArgumentException("not a SPIR-V module");
        var words = new uint[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length);
        if (words[0] != Magic) throw new ArgumentException("bad SPIR-V magic");

        var reader = new SpirvReader();
        for (int at = 5; at < words.Length;)
        {
            ushort opcode = (ushort)(words[at] & 0xFFFF);
            int count = (int)(words[at] >> 16);
            if (count == 0) throw new ArgumentException("zero-length instruction at word " + at);
            ReadOnlySpan<uint> operands = new ReadOnlySpan<uint>(words, at + 1, count - 1);
            reader.Take(opcode, operands);
            at += count;
        }
        return reader;
    }

    private void Take(ushort opcode, ReadOnlySpan<uint> o)
    {
        switch (opcode)
        {
            case OpName: Names[o[0]] = ReadString(o[1..]); break;
            case OpMemberName: MemberNames[(o[0], o[1])] = ReadString(o[2..]); break;
            case OpTypeInt: ScalarTypes[o[0]] = o[2] == 1 ? "int" : "uint"; break;
            case OpTypeFloat: ScalarTypes[o[0]] = "float"; break;
            case OpTypeArray: ArrayElements[o[0]] = o[1]; break;
            case OpTypeStruct: Structs[o[0]] = o[1..].ToArray(); break;
            case OpTypePointer: Pointers[o[0]] = (o[1], o[2]); break;
            case OpSpecConstant: SpecConstantTypes[o[1]] = o[0]; break;
            case OpVariable: Variables[o[1]] = (o[0], o[2]); break;
            case OpDecorate:
                Get(Decorations, o[0])[o[1]] = o.Length > 2 ? o[2] : 1;
                break;
            case OpMemberDecorate:
                if (!MemberDecorations.TryGetValue((o[0], o[1]), out Dictionary<uint, uint>? member))
                {
                    MemberDecorations[(o[0], o[1])] = member = new Dictionary<uint, uint>();
                }
                member[o[2]] = o.Length > 3 ? o[3] : 1;
                break;
        }
    }

    /// <summary>The block type of the variable at <paramref name="set" />/<paramref name="binding" />, or null.</summary>
    public uint? BlockAt(uint set, uint binding)
    {
        foreach ((uint id, (uint pointer, uint _)) in Variables)
        {
            if (!Decorations.TryGetValue(id, out Dictionary<uint, uint>? d)) continue;
            if (d.TryGetValue(DecorationDescriptorSet, out uint s) && s == set &&
                d.TryGetValue(DecorationBinding, out uint b) && b == binding)
            {
                return Pointers[pointer].Type;
            }
        }
        return null;
    }

    /// <summary>The block types of every variable in <paramref name="storage" />.</summary>
    public List<uint> BlocksIn(uint storage)
    {
        var blocks = new List<uint>();
        foreach ((uint _, (uint pointer, uint variableStorage)) in Variables)
        {
            if (variableStorage == storage) blocks.Add(Pointers[pointer].Type);
        }
        return blocks;
    }

    public int MemberCount(uint structType) => Structs[structType].Length;

    public uint MemberOffset(uint structType, int member) =>
        MemberDecorations[(structType, (uint)member)][DecorationOffset];

    public string? MemberName(uint structType, int member) =>
        MemberNames.TryGetValue((structType, (uint)member), out string? name) ? name : null;

    public uint? ArrayStride(uint structType, int member)
    {
        uint type = Structs[structType][member];
        if (!ArrayElements.ContainsKey(type)) return null;
        return Decorations.TryGetValue(type, out Dictionary<uint, uint>? d) &&
               d.TryGetValue(DecorationArrayStride, out uint stride) ? stride : null;
    }

    /// <summary>Specialization constant id to its scalar type name.</summary>
    public Dictionary<uint, string> SpecIds()
    {
        var ids = new Dictionary<uint, string>();
        foreach ((uint id, Dictionary<uint, uint> d) in Decorations)
        {
            if (d.TryGetValue(DecorationSpecId, out uint specId) && SpecConstantTypes.TryGetValue(id, out uint type))
            {
                ids[specId] = ScalarTypes[type];
            }
        }
        return ids;
    }

    private static Dictionary<uint, uint> Get(Dictionary<uint, Dictionary<uint, uint>> map, uint id)
    {
        if (!map.TryGetValue(id, out Dictionary<uint, uint>? value)) map[id] = value = new Dictionary<uint, uint>();
        return value;
    }

    private static string ReadString(ReadOnlySpan<uint> words)
    {
        var bytes = new List<byte>();
        foreach (uint word in words)
        {
            for (int shift = 0; shift < 32; shift += 8)
            {
                byte b = (byte)(word >> shift);
                if (b == 0) return Encoding.UTF8.GetString(bytes.ToArray());
                bytes.Add(b);
            }
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}
}
