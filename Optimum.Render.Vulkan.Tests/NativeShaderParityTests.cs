using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Static parity between every native program in <c>sources/shaders-vk</c> and the GLSL 330 program it
/// replaces (docs/vulkan-native-shaders.md section 2). Data-driven over the tree: a family stage adds
/// shaders, never test code.
///
/// The native side is the manifest the offline compiler's library entry point
/// (<see cref="NativeShaderBuilder.Build" />) produces for the tree. The GLSL 330 side is the program
/// <see cref="ShaderCorpus" /> builds from the effective sources (<c>sources/shaders</c> override, else
/// the vanilla asset) with the prefix defines of the matching variant. No GPU is needed.
/// </summary>
public sealed class NativeShaderParityTests
{
    private static string SourceDirectory => Path.Combine(ShaderCorpus.RepositoryRoot, "sources", "shaders-vk");

    private static readonly Lazy<(NativeShaderBuildResult? Result, string Reason)> Built = new(() =>
    {
        if (!NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason)) return (null, reason);
        using (compiler)
        {
            return (new NativeShaderBuilder(compiler!).Build(SourceDirectory), "");
        }
    });

    public static IEnumerable<object[]> Programs() =>
        NativeShaderBuilder.DiscoverPrograms(SourceDirectory, new List<string>()).Select(name => new object[] { name });

    private static NativeShaderBuildResult RequireBuild()
    {
        (NativeShaderBuildResult? result, string reason) = Built.Value;
        Skip.If(result == null, reason);
        return result!;
    }

    private static List<string> ErrorsOf(NativeShaderBuildResult result, string program) =>
        result.Errors.Where(e => e.StartsWith(program + " ", StringComparison.Ordinal) || e.StartsWith(program + ":", StringComparison.Ordinal)).ToList();

    // ------------------------------------------------------------------ the GLSL 330 oracle

    /// <summary>
    /// <c>ShaderProgram.collectUniformNames</c> (build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderProgram.cs:57-68),
    /// character for character: the same pattern and options, run over each stage's include-expanded
    /// <c>Code</c> (never the prefix, never preprocessed) in the order <c>Compile</c> calls it (vertex,
    /// fragment, geometry). It therefore sees names inside inactive <c>#if</c> blocks and comments, and a
    /// type outside its list is invisible to it.
    /// </summary>
    private static readonly Regex CollectUniformNames = new(
        "(\\s|\\r\\n)uniform\\s*(?<type>float|int|ivec2|ivec3|ivec4|vec2|vec3|vec4|sampler2DShadow|sampler2D|samplerCube|mat3|mat4x3|mat4)\\s*(\\[[\\d\\w]+\\])?\\s*(?<var>[\\d\\w]+)",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture);

    internal sealed class Oracle
    {
        /// <summary>The name set, each with every type the pattern captured for it.</summary>
        public readonly SortedDictionary<string, SortedSet<string>> Names = new(StringComparer.Ordinal);
        /// <summary>textureLocations: a repeated sampler name is reassigned the current count, as the client does.</summary>
        public readonly Dictionary<string, int> TextureLocations = new(StringComparer.Ordinal);
        /// <summary>
        /// The pattern has no sampler2DArray, so <c>uniform sampler2DArray OITaccumulation</c> matches as type
        /// sampler2D named <c>Array</c>. The client registers that name and unit; the program's real sampler is the
        /// declared one. Key: the name the client sees; value: the declared sampler2DArray name. The runtime answers
        /// the client's name with the declared sampler's slot.
        /// </summary>
        public readonly Dictionary<string, string> ArraySamplerAliases = new(StringComparer.Ordinal);
    }

    private static readonly Regex DeclaredSampler2DArray = new(@"\G(\s|\r\n)uniform\s*sampler2DArray\s+(?<var>[\d\w]+)",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture);

    internal static Oracle CollectOracle(IEnumerable<ShaderStageSource> stages)
    {
        var oracle = new Oracle();
        foreach (ShaderStageSource stage in stages.OrderBy(s => s.Stage switch
                 {
                     EnumShaderType.VertexShader => 0,
                     EnumShaderType.FragmentShader => 1,
                     _ => 2,
                 }))
        {
            foreach (Match item in CollectUniformNames.Matches(stage.Code))
            {
                string value = item.Groups["var"].Value;
                string type = item.Groups["type"].ToString();
                Match array = DeclaredSampler2DArray.Match(stage.Code, item.Index);
                if (value == "Array" && array.Success)
                {
                    oracle.ArraySamplerAliases["Array"] = array.Groups["var"].Value;
                    value = array.Groups["var"].Value;
                    type = "sampler2DArray";
                }
                if (!oracle.Names.TryGetValue(value, out SortedSet<string>? types))
                {
                    oracle.Names[value] = types = new SortedSet<string>(StringComparer.Ordinal);
                }
                types.Add(type);
                if (type.Contains("sampler"))
                {
                    oracle.TextureLocations[value] = oracle.TextureLocations.Count;
                }
            }
        }
        return oracle;
    }

    // ------------------------------------------------------------------ variants

    /// <summary>
    /// The defines every value of every axis is checked under. Axes a native program branches on override
    /// the base; everything else keeps the base's value, so a define that changes a GLSL 330 declaration
    /// without being an axis of the native program makes the bases disagree and fails.
    /// </summary>
    private static readonly string[] BaseVariants = { "everything-off", "everything-on", "taa-with-ssao" };

    internal static Dictionary<string, int> ParseKey(string key)
    {
        var values = new Dictionary<string, int>(StringComparer.Ordinal);
        if (key.Length == 0) return values;
        foreach (string pair in key.Split(','))
        {
            string[] parts = pair.Split('=');
            values[parts[0]] = int.Parse(parts[1], CultureInfo.InvariantCulture);
        }
        return values;
    }

    /// <summary>
    /// Maps a native variant back to ShaderRegistry's prefix defines (registerDefaultShaderCodePrefixes,
    /// ShaderRegistry.cs:462-540). GBUFFER is SSAOLEVEL &gt; 0; TAAMOTIONLOCATION follows it (4 with the
    /// G-buffer, 2 without); the per-registration defines are stamped as the program's own prefix, which
    /// the GLSL 330 sources test with <c>defined()</c>.
    /// </summary>
    internal static ShaderCorpus.ShaderVariant VariantFor(string baseName, IReadOnlyDictionary<string, int> axes)
    {
        ShaderCorpus.ShaderVariant variant = ShaderCorpus.Variants().Single(v => v.Name == baseName);
        var extra = new List<string>();
        foreach ((string axis, int value) in axes.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            switch (axis)
            {
                case "TAAMOTION": variant.TaaMotion = value; break;
                case "GBUFFER": variant.SsaoLevel = value == 0 ? 0 : Math.Max(1, variant.SsaoLevel); break;
                case "USEOIT": variant.UseOit = value; break;
                case "USESSBO": variant.UseSsbo = value; break;
                case "GREEDYMESH": variant.GreedyMesh = value; break;
                case "ALLOWDEPTHOFFSET" or "GLOWSUB" or "VEC3SCALE":
                    if (value != 0) extra.Add("#define " + axis + " 1");
                    break;
                default: throw new InvalidOperationException("no GLSL 330 mapping for axis " + axis);
            }
        }
        variant.TaaMotionLocation = variant.SsaoLevel > 0 ? 4 : 2;
        variant.ExtraPrefix = string.Join("\r\n", extra);
        variant.Name = baseName + (axes.Count > 0 ? " [" + NativeShaderManifest.VariantKey(axes.Keys.OrderBy(k => k, StringComparer.Ordinal), axes) + "]" : "");
        return variant;
    }

    // ------------------------------------------------------------------ interface extraction

    private static int LocationSpan(string type, int arrayLength)
    {
        Match matrix = Regex.Match(type, @"^d?mat(\d)(x\d)?$");
        int columns = matrix.Success ? int.Parse(matrix.Groups[1].Value, CultureInfo.InvariantCulture) : 1;
        return columns * Math.Max(1, arrayLength);
    }

    /// <summary>
    /// The GLSL 330 stage's vertex inputs or fragment outputs after preprocessing, with the locations the
    /// Vulkan path assigns: explicit ones first, then each unlocated declaration takes the lowest free span in
    /// declaration order (ProgramInterfaceLayout.AssignInterfaceLocations; for a single unlocated output that
    /// is location 0, as GL assigns it).
    /// </summary>
    internal static List<(int Location, string Name, string Type, int ArrayLength)> Glsl330Interface(
        ShaderCompiler compiler, ShaderStageSource stage, GlslDeclarationKind kind)
    {
        ShaderCompileResult preprocessed = compiler.Preprocess(stage.Code, stage.PrefixCode, stage.Filename, stage.Stage);
        Assert.True(preprocessed.Success, stage.Filename + ": " + preprocessed.Error);

        var declarations = GlslParser.Parse(preprocessed.PreprocessedText).Declarations.Where(d => d.Kind == kind).ToList();
        var used = new HashSet<int>();
        var result = new List<(int, string, string, int)>();
        foreach (GlslDeclaration declaration in declarations.Where(d => d.Location >= 0))
        {
            for (int i = 0; i < LocationSpan(declaration.TypeName, declaration.ArrayLength); i++) used.Add(declaration.Location + i);
            result.Add((declaration.Location, declaration.Name, declaration.TypeName, declaration.ArrayLength));
        }
        foreach (GlslDeclaration declaration in declarations.Where(d => d.Location < 0))
        {
            int span = LocationSpan(declaration.TypeName, declaration.ArrayLength);
            int location = 0;
            while (Enumerable.Range(location, span).Any(used.Contains)) location++;
            for (int i = 0; i < span; i++) used.Add(location + i);
            result.Add((location, declaration.Name, declaration.TypeName, declaration.ArrayLength));
        }
        return result.OrderBy(entry => entry.Item1).ToList();
    }

    private static string Describe(IEnumerable<string> entries) => "[" + string.Join(", ", entries) + "]";

    private static void CompareLists(List<string> failures, string label, string what, IList<string> native, IList<string> glsl330)
    {
        if (native.SequenceEqual(glsl330)) return;
        failures.Add(label + ": " + what + " differ: native " + Describe(native) + ", GLSL 330 " + Describe(glsl330) +
                     "; only native " + Describe(native.Except(glsl330)) + ", only GLSL 330 " + Describe(glsl330.Except(native)));
    }

    /// <summary>The include file names a program's two stages pull in, transitively (the builder's own expansion).</summary>
    internal static SortedSet<string> IncludesOf(string program)
    {
        var included = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string extension in new[] { ".vert", ".frag" })
        {
            NativeShaderBuilder.ExpandIncludes(Path.Combine(SourceDirectory, program + extension), SourceDirectory, included);
        }
        return included;
    }

    internal static List<NativeShaderTree.Port> PortsOf(IEnumerable<string> includes) =>
        includes.Where(name => File.Exists(Path.Combine(NativeShaderTree.IncludeDirectory, name)))
            .Select(NativeShaderTree.PortOf).Where(port => port != null).Select(port => port!).ToList();

    // ------------------------------------------------------------------ parity

    [SkippableFact]
    public void TheNativeTreeBuildsWithoutErrors()
    {
        NativeShaderBuildResult result = RequireBuild();
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.NotEmpty(result.Manifest.Programs);
    }

    /// <summary>
    /// Section 2, per program and per variant: the name set with its GLSL types, the sampler order, the push
    /// block limit, the include port headers' program uniforms, and - under every base variant - the vertex
    /// inputs and fragment outputs.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Programs))]
    public void TheNativeProgramMatchesItsGlsl330Program(string program)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped vanilla assets.");
        NativeShaderBuildResult result = RequireBuild();
        List<string> errors = ErrorsOf(result, program);
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        NativeProgram native = result.Manifest.FindProgram(program)
            ?? throw new InvalidOperationException(program + " is missing from the manifest");

        Dictionary<string, string> files = ShaderCorpus.LoadShaderFiles();
        Dictionary<string, string> includes = ShaderCorpus.LoadIncludes();
        Assert.True(files.ContainsKey(program + ".vsh") && files.ContainsKey(program + ".fsh"),
            program + ": no GLSL 330 program " + program + ".vsh/.fsh to compare with (a native program is named after the GLSL 330 files it replaces)");

        // The name set does not depend on defines: the oracle reads unpreprocessed text.
        Oracle oracle = CollectOracle(ShaderCorpus.BuildProgram(program, files, includes, VariantFor("everything-off", new Dictionary<string, int>())));
        List<NativeShaderTree.Port> ports = PortsOf(IncludesOf(program));

        Skip.IfNot(NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason), reason);
        var failures = new List<string>();
        using (compiler)
        {
            foreach (NativeVariant variant in native.Variants)
            {
                string label = program + (variant.Key.Length > 0 ? " [" + variant.Key + "]" : " [no axes]");
                CheckNames(failures, label, variant, oracle, ports);
                CheckSamplerOrder(failures, label, variant, oracle);
                CheckPortUniforms(failures, label, variant, ports);
                if (variant.Push != null && variant.Push.Size > SetConvention.PushConstantBytes)
                {
                    failures.Add(label + ": push block is " + variant.Push.Size + " B, the limit is " + SetConvention.PushConstantBytes);
                }

                Dictionary<string, int> axes = ParseKey(variant.Key);
                foreach (string baseName in BaseVariants)
                {
                    ShaderCorpus.ShaderVariant defines = VariantFor(baseName, axes);
                    List<ShaderStageSource> stages = ShaderCorpus.BuildProgram(program, files, includes, defines);
                    string where = label + " vs GLSL 330 " + defines.Name;

                    var inputs = Glsl330Interface(compiler!, stages.Single(s => s.Stage == EnumShaderType.VertexShader), GlslDeclarationKind.Input);
                    CompareLists(failures, where, "vertex inputs (location type)",
                        variant.VertexInputs.OrderBy(v => v.Location).Select(v => v.Location + " " + v.Type + (v.ArrayLength != 0 ? "[" + v.ArrayLength + "]" : "")).ToList(),
                        inputs.Select(v => v.Location + " " + v.Type + (v.ArrayLength != 0 ? "[" + v.ArrayLength + "]" : "")).ToList());

                    var outputs = Glsl330Interface(compiler!, stages.Single(s => s.Stage == EnumShaderType.FragmentShader), GlslDeclarationKind.Output);
                    CompareLists(failures, where, "fragment outputs (location name)",
                        variant.FragmentOutputs.OrderBy(v => v.Location).Select(v => v.Location + " " + v.Name).ToList(),
                        outputs.Select(v => v.Location + " " + v.Name).ToList());
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>Frame members through owner includes, frame textures of the ported includes, push and record members, sampler names.</summary>
    private static void CheckNames(List<string> failures, string label, NativeVariant variant, Oracle oracle, List<NativeShaderTree.Port> ports)
    {
        var native = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void Add(string name, string type, string source)
        {
            if (!native.TryGetValue(name, out SortedSet<string>? types)) native[name] = types = new SortedSet<string>(StringComparer.Ordinal);
            if (types.Count > 0 && !types.Contains(type)) failures.Add(label + ": '" + name + "' is declared twice with different types (" + string.Join("/", types) + " and " + type + " from " + source + ")");
            types.Add(type);
        }

        foreach (string member in variant.FrameMembers)
        {
            Assert.True(FrameGlobals.TryGetMember(member, out UniformMember frame), member + " is not a FrameGlobals member");
            Add(member, frame.Type.Name, "the frame block");
        }
        foreach (NativeShaderTree.Port port in ports)
        {
            foreach (NativeShaderTree.Declaration texture in port.FrameTextures) Add(texture.Name, texture.Type, port.Include);
        }
        var slots = variant.Samplers.Select(s => s.Name).ToHashSet(StringComparer.Ordinal);
        foreach (NativeMember member in variant.Push?.Members ?? new List<NativeMember>())
        {
            if (!slots.Contains(member.Name)) Add(member.Name, member.Type, "the push block");
        }
        foreach (NativeMember member in variant.Record?.Members ?? new List<NativeMember>()) Add(member.Name, member.Type, "the record");
        foreach (NativeSampler sampler in variant.Samplers) Add(sampler.Name, sampler.GlslType, "a sampler slot");
        // A set-0 frame texture the program declares itself (cloudvolumetric's liquidDepth, without including
        // underwatereffects) is the bindings.glsl declaration every native stage already has, as the rewriter
        // treats a sampler whose name and type match SetConvention.FrameTextures.
        foreach ((string name, SortedSet<string> types) in oracle.Names)
        {
            if (native.ContainsKey(name)) continue;
            foreach (string type in types)
            {
                if (SetConvention.FrameTextures.Any(binding => binding.Name == name && binding.GlslType == type))
                {
                    Add(name, type, "a set-0 frame texture the program declares itself");
                }
            }
        }

        var onlyNative = native.Keys.Except(oracle.Names.Keys).ToList();
        var onlyGlsl330 = oracle.Names.Keys.Except(native.Keys).ToList();
        var typeDiffers = native.Keys.Intersect(oracle.Names.Keys)
            .Where(name => !native[name].SetEquals(oracle.Names[name]))
            .Select(name => name + " native " + string.Join("/", native[name]) + " GLSL 330 " + string.Join("/", oracle.Names[name]))
            .ToList();
        if (onlyNative.Count + onlyGlsl330.Count + typeDiffers.Count > 0)
        {
            failures.Add(label + ": uniform names differ from collectUniformNames: only native " + Describe(onlyNative) +
                         ", only GLSL 330 " + Describe(onlyGlsl330) + ", type differs " + Describe(typeDiffers));
        }
    }

    /// <summary>The sampler order is the texture unit collectUniformNames assigns.</summary>
    private static void CheckSamplerOrder(List<string> failures, string label, NativeVariant variant, Oracle oracle)
    {
        CompareLists(failures, label, "samplers (texture unit name)",
            variant.Samplers.OrderBy(s => s.Order).Select(s => s.Order + " " + s.Name).ToList(),
            oracle.TextureLocations.Where(t => variant.Samplers.Any(s => s.Name == t.Key) || !IsFrameTexture(t.Key))
                .OrderBy(t => t.Value).Select((t, index) => index + " " + t.Key).ToList());

        var duplicateUnits = oracle.TextureLocations.GroupBy(t => t.Value).Where(g => g.Count() > 1).Select(g => g.Key + ": " + string.Join("/", g.Select(t => t.Key))).ToList();
        if (duplicateUnits.Count > 0) failures.Add(label + ": the GLSL 330 program assigns one texture unit to several samplers " + Describe(duplicateUnits));
    }

    private static bool IsFrameTexture(string name) => SetConvention.FrameTextures.Any(binding => binding.Name == name);

    /// <summary>
    /// Every program uniform an included port's header lists is a frame member when the program includes that
    /// name's owner, and otherwise a push or record member with the header's type.
    /// </summary>
    private static void CheckPortUniforms(List<string> failures, string label, NativeVariant variant, List<NativeShaderTree.Port> ports)
    {
        var members = (variant.Push?.Members ?? new List<NativeMember>()).Concat(variant.Record?.Members ?? new List<NativeMember>())
            .ToDictionary(m => m.Name, m => m, StringComparer.Ordinal);
        foreach (NativeShaderTree.Port port in ports)
        {
            foreach (NativeShaderTree.Declaration uniform in port.ProgramUniforms)
            {
                if (variant.FrameMembers.Contains(uniform.Name)) continue;
                if (!members.TryGetValue(uniform.Name, out NativeMember? member))
                {
                    failures.Add(label + ": " + port.Include + " needs program uniform '" + uniform.Text + "' in the push block or record");
                    continue;
                }
                string declared = member.Type + " " + member.Name + (member.ArrayLength > 0 ? "[" + member.ArrayLength + "]" : "");
                string wanted = Regex.Replace(uniform.Text, @"\[\s*([^\]]*?)\s*\]", m =>
                    GlslParser.TryEvaluateConstantInt(m.Groups[1].Value, out int length) ? "[" + length + "]" : m.Value);
                if (declared != wanted) failures.Add(label + ": " + port.Include + " needs '" + wanted + "', the program declares '" + declared + "'");
            }
        }
    }

    // ------------------------------------------------------------------ compile

    /// <summary>
    /// Every variant of the program is built (2^axes of them), every shipped module passes spirv-val when it is on
    /// PATH, and the push block and record reflect exactly as the source declares them: the same members in the
    /// same order with scalar-layout offsets.
    /// </summary>
    [SkippableTheory]
    [MemberData(nameof(Programs))]
    public void EveryVariantCompilesValidatesAndReflectsItsDeclaredBlocks(string program)
    {
        NativeShaderBuildResult result = RequireBuild();
        List<string> errors = ErrorsOf(result, program);
        Assert.True(errors.Count == 0, string.Join("\n", errors));
        NativeProgram native = result.Manifest.FindProgram(program)
            ?? throw new InvalidOperationException(program + " is missing from the manifest");
        Assert.Equal(1 << native.Axes.Count, native.Variants.Count);

        string? spirvVal = FindOnPath("spirv-val");
        var failures = new List<string>();
        Skip.IfNot(NativeShaderTree.TryCreateCompiler(out ShaderCompiler? compiler, out string reason), reason);
        using (compiler)
        {
            foreach (NativeVariant variant in native.Variants)
            {
                string label = program + (variant.Key.Length > 0 ? " [" + variant.Key + "]" : " [no axes]");
                Dictionary<string, int> axes = ParseKey(variant.Key);
                string prefix = string.Concat(axes.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => "#define " + a.Key + " " + a.Value + "\n"));

                Assert.Equal(new[] { "vertex", "fragment" }, variant.Stages.Select(s => s.Stage));
                foreach (NativeStage stage in variant.Stages)
                {
                    if (spirvVal != null) Validate(failures, label, spirvVal, stage.Spirv, result.Files[stage.Spirv]);

                    EnumShaderType type = stage.Stage == "vertex" ? EnumShaderType.VertexShader : EnumShaderType.FragmentShader;
                    string expanded = NativeShaderBuilder.ExpandIncludes(Path.Combine(SourceDirectory, stage.Source), SourceDirectory, new HashSet<string>());
                    ShaderCompileResult preprocessed = compiler!.Preprocess(expanded, prefix, stage.Source, type);
                    Assert.True(preprocessed.Success, label + " " + stage.Source + ": " + preprocessed.Error);

                    CheckDeclaredBlock(failures, label + " " + stage.Source + " push block", DeclaredBlock(preprocessed.PreprocessedText, push: true), variant.Push);
                    CheckDeclaredBlock(failures, label + " " + stage.Source + " record", DeclaredBlock(preprocessed.PreprocessedText, push: false), variant.Record);
                }
            }
        }
        if (spirvVal == null) Console.WriteLine("spirv-val is not on PATH; the SPIR-V validation part of " + program + " was skipped");

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static readonly Regex BlockDeclaration = new(@"layout\s*\(([^)]*)\)\s*uniform\s+(\w+)\s*\{([^}]*)\}", RegexOptions.Singleline);
    private static readonly Regex MemberDeclaration = new(@"^\s*(\w+)\s+(\w+)\s*(?:\[\s*(\d+)\s*\])?\s*$");

    /// <summary>The members (type, name, array length) of the push block or the record as the preprocessed source declares them, or null.</summary>
    internal static List<(string Type, string Name, int ArrayLength)>? DeclaredBlock(string preprocessed, bool push)
    {
        foreach (Match block in BlockDeclaration.Matches(preprocessed))
        {
            string layout = Regex.Replace(block.Groups[1].Value, @"\s+", "");
            bool isPush = layout.Split(',').Contains("push_constant");
            bool isRecord = layout.Split(',').Contains("set=" + SetConvention.StorageSet) &&
                            layout.Split(',').Contains("binding=" + SetConvention.ProgramRecordBinding);
            if (push ? !isPush : !isRecord) continue;

            var members = new List<(string, string, int)>();
            foreach (string statement in block.Groups[3].Value.Split(';'))
            {
                if (statement.Trim().Length == 0) continue;
                Match member = MemberDeclaration.Match(statement);
                Assert.True(member.Success, "cannot read block member '" + statement.Trim() + "'");
                members.Add((member.Groups[1].Value, member.Groups[2].Value,
                    member.Groups[3].Success ? int.Parse(member.Groups[3].Value, CultureInfo.InvariantCulture) : 0));
            }
            return members;
        }
        return null;
    }

    /// <summary>Bytes of one element under GL_EXT_scalar_block_layout, where every member aligns to its 4-byte scalar.</summary>
    private static int ScalarSize(string type)
    {
        Match vector = Regex.Match(type, @"^[iub]?vec(\d)$");
        if (vector.Success) return 4 * int.Parse(vector.Groups[1].Value, CultureInfo.InvariantCulture);
        Match matrix = Regex.Match(type, @"^mat(\d)(?:x(\d))?$");
        if (matrix.Success)
        {
            int columns = int.Parse(matrix.Groups[1].Value, CultureInfo.InvariantCulture);
            int rows = matrix.Groups[2].Success ? int.Parse(matrix.Groups[2].Value, CultureInfo.InvariantCulture) : columns;
            return 4 * columns * rows;
        }
        return type is "float" or "int" or "uint" or "bool" ? 4 : throw new InvalidOperationException("no scalar size for " + type);
    }

    private static void CheckDeclaredBlock(List<string> failures, string label, List<(string Type, string Name, int ArrayLength)>? declared, NativeBlock? reflected)
    {
        if (declared == null || reflected == null)
        {
            if ((declared == null) != (reflected == null)) failures.Add(label + ": declared " + (declared != null) + ", reflected " + (reflected != null));
            return;
        }

        int offset = 0;
        var expected = new List<string>();
        foreach ((string type, string name, int arrayLength) in declared)
        {
            offset = (offset + 3) & ~3;
            int size = ScalarSize(type) * Math.Max(1, arrayLength);
            expected.Add(type + " " + name + (arrayLength != 0 ? "[" + arrayLength + "]" : "") + " @" + offset + " " + size);
            offset += size;
        }
        CompareLists(failures, label, "members (type name @offset size)",
            reflected.Members.Select(m => m.Type + " " + m.Name + (m.ArrayLength != 0 ? "[" + m.ArrayLength + "]" : "") + " @" + m.Offset + " " + m.Size).ToList(),
            expected);
        if (reflected.Size != offset) failures.Add(label + ": reflected size " + reflected.Size + " B, declared members end at " + offset + " B");
    }

    private static string? FindOnPath(string tool)
    {
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            string candidate = Path.Combine(directory, OperatingSystem.IsWindows() ? tool + ".exe" : tool);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static void Validate(List<string> failures, string label, string spirvVal, string name, byte[] spirv)
    {
        string path = Path.Combine(Path.GetTempPath(), "optimum-native-parity-" + Guid.NewGuid().ToString("N") + ".spv");
        try
        {
            File.WriteAllBytes(path, spirv);
            var start = new ProcessStartInfo(spirvVal)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            // The environment the renderer creates: Vulkan 1.3 with scalarBlockLayout, which the device floor
            // requires and the device enables (Core/VulkanContext.cs: the floor lists a missing scalarBlockLayout,
            // device creation sets ScalarBlockLayout = true in the Vulkan 1.2 features). Every push block and
            // record is scalar-laid-out (contract section 4), so without the flag
            // spirv-val checks a device this renderer never runs on.
            start.ArgumentList.Add("--target-env");
            start.ArgumentList.Add("vulkan1.3");
            start.ArgumentList.Add("--scalar-block-layout");
            start.ArgumentList.Add(path);
            using Process process = Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) failures.Add(label + ": spirv-val rejects " + name + ":\n" + output);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
