using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>What one build produced: the manifest, the SPIR-V files by name, and every error.</summary>
internal sealed class NativeShaderBuildResult
{
    public NativeShaderManifest Manifest = new();
    /// <summary>SPIR-V file name (relative to the manifest directory) to bytes.</summary>
    public SortedDictionary<string, byte[]> Files = new(StringComparer.Ordinal);
    public List<string> Errors = new();
    public bool Success => Errors.Count == 0;
}

/// <summary>
/// The offline native shader compiler behind <c>tools/shader-compiler</c>
/// (docs/vulkan-native-shaders.md sections 5 and 6).
///
/// For every <c>&lt;program&gt;.vert</c>/<c>.frag</c> pair in the source directory it resolves
/// <c>#include</c>s (the including file's directory first, then <c>include/</c>), finds the variant
/// axes the source branches on, compiles every combination twice - the shipped optimised module and
/// an unoptimised twin that keeps names and declarations - reflects both, checks the result against
/// the set convention, and records it in a <see cref="NativeShaderManifest" />.
/// </summary>
internal sealed class NativeShaderBuilder
{
    /// <summary>The define symbols that stay compile-time variants (contract section 5), sorted.</summary>
    public static readonly string[] VariantAxes =
    {
        "ALLOWDEPTHOFFSET", "GBUFFER", "GLOWSUB", "GREEDYMESH", "TAAMOTION", "USEOIT", "USESSBO", "VEC3SCALE",
    };

    public const string IncludeDirectoryName = "include";

    /// <summary>
    /// Declares a sampler slot in the push block: <c>OPTIMUM_SAMPLER_SLOT(sampler2D, terrainTex)</c>
    /// expands to <c>uint terrainTex</c> (bindings.glsl). A SPIR-V <c>uint</c> does not say which
    /// sampler type it indexes, so the builder reads the declaration from the source and checks it
    /// against the array the shipped module actually indexes.
    /// </summary>
    public const string SamplerSlotMacro = "OPTIMUM_SAMPLER_SLOT";

    private const int MaxIncludeDepth = 16;

    private static readonly Regex IncludeDirective = new(@"^[ \t]*#[ \t]*include[ \t]+[""<]([^"">]+)["">][^\r\n]*", RegexOptions.Multiline);
    private static readonly Regex ConditionalDirective = new(@"^[ \t]*#[ \t]*(if|elif|ifdef|ifndef)\b([^\r\n]*)", RegexOptions.Multiline);
    private static readonly Regex Identifier = new(@"\b[A-Za-z_][A-Za-z0-9_]*\b");
    private static readonly Regex DefinedAxis = new(@"\bdefined\s*\(?\s*([A-Za-z_][A-Za-z0-9_]*)");
    private static readonly Regex SamplerSlot = new(@"^(?![ \t]*#)[^\r\n]*?\bOPTIMUM_SAMPLER_SLOT\s*\(\s*(\w+)\s*,\s*(\w+)\s*\)", RegexOptions.Multiline);
    private static readonly Regex SamplerSlotAnywhere = new(@"\bOPTIMUM_SAMPLER_SLOT\s*\(\s*(\w+)\s*,\s*(\w+)\s*\)");

    private readonly ShaderCompiler _compiler;

    public NativeShaderBuilder(ShaderCompiler compiler)
    {
        _compiler = compiler;
    }

    // ------------------------------------------------------------------ build

    /// <summary>Builds every program in <paramref name="sourceDirectory" />, or only <paramref name="onlyProgram" />.</summary>
    public NativeShaderBuildResult Build(string sourceDirectory, string? onlyProgram = null)
    {
        var result = new NativeShaderBuildResult();
        result.Manifest.Toolchain = _compiler.Identity;

        if (!Directory.Exists(sourceDirectory))
        {
            result.Errors.Add("source directory not found: " + sourceDirectory);
            return result;
        }

        List<string> programs = DiscoverPrograms(sourceDirectory, result.Errors);
        if (onlyProgram != null)
        {
            if (!programs.Contains(onlyProgram))
            {
                result.Errors.Add("no program '" + onlyProgram + "' (needs " + onlyProgram + ".vert and " + onlyProgram + ".frag)");
                return result;
            }
            programs = new List<string> { onlyProgram };
        }

        foreach (string program in programs)
        {
            NativeProgram? built = BuildProgram(sourceDirectory, program, result);
            if (built != null) result.Manifest.Programs.Add(built);
        }
        return result;
    }

    /// <summary>Program names with both stages present, sorted; a lone stage is an error.</summary>
    public static List<string> DiscoverPrograms(string sourceDirectory, List<string> errors)
    {
        var vertex = new SortedSet<string>(StringComparer.Ordinal);
        var fragment = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(sourceDirectory))
        {
            string extension = Path.GetExtension(file);
            if (extension == ".vert") vertex.Add(Path.GetFileNameWithoutExtension(file));
            else if (extension == ".frag") fragment.Add(Path.GetFileNameWithoutExtension(file));
        }

        foreach (string name in vertex.Except(fragment)) errors.Add(name + ".vert has no " + name + ".frag");
        foreach (string name in fragment.Except(vertex)) errors.Add(name + ".frag has no " + name + ".vert");

        var programs = vertex.Intersect(fragment).ToList();
        programs.Sort(StringComparer.Ordinal);
        return programs;
    }

    private NativeProgram? BuildProgram(string sourceDirectory, string name, NativeShaderBuildResult result)
    {
        var stages = new (string Extension, string StageName, EnumShaderType Type)[]
        {
            ("vert", "vertex", EnumShaderType.VertexShader),
            ("frag", "fragment", EnumShaderType.FragmentShader),
        };

        var texts = new string[stages.Length];
        var includes = new SortedSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < stages.Length; i++)
        {
            string path = Path.Combine(sourceDirectory, name + "." + stages[i].Extension);
            try
            {
                texts[i] = ExpandIncludes(path, sourceDirectory, includes);
            }
            catch (InvalidDataException error)
            {
                result.Errors.Add(name + ": " + error.Message);
                return null;
            }
        }

        int errorsBefore = result.Errors.Count;
        List<string> axes = AxesOf(name, texts, result.Errors);
        Dictionary<string, string> slotTypes = SamplerSlotDeclarations(name, texts, result.Errors);
        if (result.Errors.Count > errorsBefore) return null;

        var program = new NativeProgram { Name = name, Axes = axes };
        for (int combination = 0; combination < 1 << axes.Count; combination++)
        {
            var values = new Dictionary<string, int>(StringComparer.Ordinal);
            var prefix = new StringBuilder();
            for (int a = 0; a < axes.Count; a++)
            {
                int value = (combination >> (axes.Count - 1 - a)) & 1;
                values[axes[a]] = value;
                prefix.Append("#define ").Append(axes[a]).Append(' ').Append(value.ToString(CultureInfo.InvariantCulture)).Append('\n');
            }
            string key = NativeShaderManifest.VariantKey(axes, values);
            string label = name + (key.Length > 0 ? " [" + key + "]" : "");
            string fileStem = name + string.Concat(axes.Select(axis => "." + axis + values[axis].ToString(CultureInfo.InvariantCulture)));

            var shipped = new SpirvModuleReflection[stages.Length];
            var declared = new SpirvModuleReflection[stages.Length];
            var variant = new NativeVariant { Key = key };
            bool compiled = true;
            for (int i = 0; i < stages.Length; i++)
            {
                string code = ShaderCompiler.SplicePrefix(texts[i], prefix.ToString());
                string fileName = name + "." + stages[i].Extension;
                ShaderCompileResult optimised = _compiler.Compile(code, fileName, stages[i].Type);
                ShaderCompileResult reflection = optimised.Success
                    ? _compiler.CompileForReflection(code, fileName, stages[i].Type)
                    : optimised;
                if (!optimised.Success || !reflection.Success)
                {
                    result.Errors.Add(label + " " + fileName + ": " + (optimised.Error ?? reflection.Error));
                    compiled = false;
                    continue;
                }

                shipped[i] = SpirvReflection.Reflect(optimised.Spirv);
                declared[i] = SpirvReflection.Reflect(reflection.Spirv);

                string spirvName = fileStem + "." + stages[i].Extension + ".spv";
                result.Files[spirvName] = optimised.Spirv;
                variant.Stages.Add(new NativeStage
                {
                    Stage = stages[i].StageName,
                    Source = fileName,
                    Spirv = spirvName,
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(optimised.Spirv)),
                });
            }
            if (!compiled) continue;

            if (DescribeVariant(label, variant, declared[0], declared[1], shipped[0], shipped[1], slotTypes, includes, result.Errors))
            {
                program.Variants.Add(variant);
            }
        }

        program.Variants.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return program;
    }

    /// <summary>Fills the variant's reflected fields and checks them; false when anything is wrong.</summary>
    private static bool DescribeVariant(
        string label, NativeVariant variant,
        SpirvModuleReflection vertex, SpirvModuleReflection fragment,
        SpirvModuleReflection shippedVertex, SpirvModuleReflection shippedFragment,
        Dictionary<string, string> slotTypes, IReadOnlySet<string> includes, List<string> errors)
    {
        int errorsBefore = errors.Count;

        // Push block: both stages include the same interface file, so they must agree.
        SpirvBlock? push = Agree(label, "push block", vertex.PushConstants, fragment.PushConstants, errors);
        if (push != null && push.Size > SetConvention.PushConstantBytes)
        {
            errors.Add(label + ": push block is " + push.Size + " B, the limit is " + SetConvention.PushConstantBytes);
        }
        variant.Push = ToNative(push);

        foreach (SpirvModuleReflection module in new[] { vertex, fragment })
        {
            foreach (SpirvDescriptorBinding binding in module.Bindings) CheckConvention(label, binding, errors);
        }

        SpirvBlock? record = Agree(label, "program record",
            RecordOf(vertex), RecordOf(fragment), errors);
        variant.Record = ToNative(record);

        // Sampler slots: uint push members declared with OPTIMUM_SAMPLER_SLOT, first in the block.
        if (push != null)
        {
            bool seenOther = false;
            for (int index = 0; index < push.Members.Count; index++)
            {
                SpirvBlockMember member = push.Members[index];
                var indexed = new SortedSet<(int Set, int Binding)>();
                foreach (SpirvModuleReflection module in new[] { shippedVertex, shippedFragment })
                {
                    if (module.PushMemberIndexes.TryGetValue(index, out SortedSet<(int Set, int Binding)>? found)) indexed.UnionWith(found);
                }

                if (!slotTypes.TryGetValue(member.Name, out string? glslType))
                {
                    seenOther = true;
                    if (indexed.Any(pair => pair.Set == SetConvention.TextureSet))
                    {
                        errors.Add(label + ": push member '" + member.Name + "' indexes a texture array but is not declared with " + SamplerSlotMacro);
                    }
                    continue;
                }

                if (member.GlslType != "uint" || member.ArrayLength != 0)
                {
                    errors.Add(label + ": sampler slot '" + member.Name + "' must be a uint, is " + member.GlslType);
                    continue;
                }
                if (seenOther)
                {
                    errors.Add(label + ": sampler slot '" + member.Name + "' follows a non-slot push member; slots come first");
                }

                SetConvention.Binding array = Array.Find(SetConvention.TextureArrays, b => b.GlslType == glslType);
                foreach ((int set, int binding) in indexed)
                {
                    if (set != SetConvention.TextureSet || binding != array.Value)
                    {
                        errors.Add(label + ": sampler slot '" + member.Name + "' is declared " + glslType + " (" + array.Name +
                                   ", set 1 binding " + array.Value + ") but indexes set " + set + " binding " + binding);
                    }
                }

                variant.Samplers.Add(new NativeSampler
                {
                    Name = member.Name,
                    GlslType = glslType,
                    BindlessArray = array.Name,
                    ArrayBinding = array.Value,
                    PushOffset = member.Offset,
                    Order = variant.Samplers.Count,
                });
            }
        }
        foreach (string slot in slotTypes.Keys.OrderBy(s => s, StringComparer.Ordinal))
        {
            bool inRecord = record?.Members.Any(m => m.Name == slot) ?? false;
            if (inRecord) errors.Add(label + ": sampler slot '" + slot + "' is declared in the program record; slots live in the push block");
        }

        foreach (UniformMember member in FrameGlobals.Members)
        {
            string? owner = FrameGlobals.OwnerOf(member.Name);
            if (owner != null && NativeIncludesFor(owner).Any(includes.Contains)) variant.FrameMembers.Add(member.Name);
        }

        var frameTextures = new SortedDictionary<int, NativeFrameTexture>();
        var storage = new SortedDictionary<(int, int), NativeStorageBinding>();
        foreach (SpirvModuleReflection module in new[] { vertex, fragment })
        {
            foreach (SpirvDescriptorBinding binding in module.Bindings)
            {
                if (binding.Set != SetConvention.StorageSet || binding.Binding == SetConvention.ProgramRecordBinding) continue;
                storage.TryAdd((binding.Set, binding.Binding), new NativeStorageBinding
                {
                    Name = binding.Name,
                    Set = binding.Set,
                    Binding = binding.Binding,
                    DescriptorType = binding.Kind == SpirvDescriptorKind.StorageBuffer ? "storageBuffer" : "uniformBuffer",
                    ArrayLength = binding.ArrayLength,
                    RuntimeArray = binding.RuntimeArray,
                });
            }
        }
        foreach (SpirvModuleReflection module in new[] { shippedVertex, shippedFragment })
        {
            foreach (SpirvDescriptorBinding binding in module.Bindings)
            {
                if (!module.UsedVariables.Contains(binding.VariableId)) continue;
                if (binding.Set == SetConvention.FrameSet)
                {
                    SetConvention.Binding texture = Array.Find(SetConvention.FrameTextures, b => b.Value == binding.Binding);
                    if (texture.Name != null)
                    {
                        frameTextures[binding.Binding] = new NativeFrameTexture
                        {
                            Name = texture.Name,
                            GlslType = texture.GlslType,
                            Binding = texture.Value,
                        };
                    }
                }
                else if (storage.TryGetValue((binding.Set, binding.Binding), out NativeStorageBinding? declared))
                {
                    declared.Used = true;
                }
            }
        }
        variant.FrameTextures.AddRange(frameTextures.Values);
        variant.StorageBindings.AddRange(storage.Values);

        variant.VertexInputs = vertex.Inputs.Select(ToNative).ToList();
        variant.FragmentOutputs = fragment.Outputs.Select(ToNative).ToList();
        foreach (int location in shippedFragment.WrittenOutputLocations)
        {
            if (location < 32) variant.WrittenOutputs |= 1u << location;
        }

        var constants = new SortedDictionary<int, NativeSpecConstant>();
        foreach (SpirvModuleReflection module in new[] { vertex, fragment })
        {
            foreach (SpirvSpecConstant constant in module.SpecConstants)
            {
                var native = new NativeSpecConstant
                {
                    Id = constant.SpecId,
                    Name = constant.Name,
                    Type = constant.GlslType,
                    Default = constant.DefaultValue,
                };
                if (!constants.TryGetValue(constant.SpecId, out NativeSpecConstant? existing))
                {
                    constants[constant.SpecId] = native;
                }
                else if (existing.Name != native.Name || existing.Type != native.Type || !existing.Default.Equals(native.Default))
                {
                    errors.Add(label + ": specialization constant " + constant.SpecId + " is '" + existing.Name + "' " + existing.Type +
                               " = " + existing.Default + " in one stage and '" + native.Name + "' " + native.Type + " = " + native.Default + " in the other");
                }
            }
        }
        variant.SpecializationConstants.AddRange(constants.Values);

        return errors.Count == errorsBefore;
    }

    /// <summary>The native include names that stand for a GLSL 330 owner file (<c>fogandlight.vsh</c> is <c>fogandlight.vert.glsl</c>).</summary>
    internal static IEnumerable<string> NativeIncludesFor(string owner)
    {
        string stem = Path.GetFileNameWithoutExtension(owner);
        string extension = Path.GetExtension(owner);
        string stage = extension switch
        {
            ".vsh" => "vert",
            ".fsh" => "frag",
            ".gsh" => "geom",
            _ => "",
        };
        if (stage.Length > 0) yield return stem + "." + stage + ".glsl";
        yield return stem + ".glsl";
    }

    private static SpirvBlock? RecordOf(SpirvModuleReflection module) =>
        module.Bindings.Find(b => b.Set == SetConvention.StorageSet && b.Binding == SetConvention.ProgramRecordBinding)?.Block;

    private static SpirvBlock? Agree(string label, string what, SpirvBlock? a, SpirvBlock? b, List<string> errors)
    {
        if (a == null || b == null) return a ?? b;
        if (Describe(a) != Describe(b))
        {
            errors.Add(label + ": the " + what + " differs between the stages: " + Describe(a) + " vs " + Describe(b));
        }
        return a;
    }

    private static string Describe(SpirvBlock block) =>
        block.TypeName + "{" + string.Join(";", block.Members.Select(m =>
            m.GlslType + " " + m.Name + (m.ArrayLength != 0 ? "[" + m.ArrayLength + "]" : "") + "@" + m.Offset)) + "}";

    private static void CheckConvention(string label, SpirvDescriptorBinding binding, List<string> errors)
    {
        string where = label + ": '" + binding.Name + "' at set " + binding.Set + " binding " + binding.Binding +
            " (reflected " + binding.Kind + " " + binding.GlslType + (binding.RuntimeArray ? "[]" : binding.ArrayLength > 0 ? "[" + binding.ArrayLength + "]" : "") + ")";
        switch (binding.Set)
        {
            case SetConvention.FrameSet:
                if (binding.Binding == SetConvention.FrameGlobalsBinding)
                {
                    if (binding.Kind != SpirvDescriptorKind.UniformBuffer) errors.Add(where + " must be the FrameGlobals uniform block");
                    return;
                }
                SetConvention.Binding frame = Array.Find(SetConvention.FrameTextures, b => b.Value == binding.Binding);
                if (frame.Name == null) errors.Add(where + " is not a binding of set 0 (bindings.glsl)");
                else if (binding.Kind != SpirvDescriptorKind.CombinedImageSampler || binding.GlslType != frame.GlslType || binding.ArrayLength != 0)
                {
                    errors.Add(where + " must be " + frame.GlslType + " " + frame.Name);
                }
                return;
            case SetConvention.TextureSet:
                SetConvention.Binding array = Array.Find(SetConvention.TextureArrays, b => b.Value == binding.Binding);
                if (array.Name == null) errors.Add(where + " is not a binding of set 1 (bindings.glsl)");
                // glslang sizes a `[]` array that is never indexed as a one-element array, so an
                // unused bindless array in the unoptimised twin is sized, not runtime.
                else if (binding.Kind != SpirvDescriptorKind.CombinedImageSampler || binding.GlslType != array.GlslType ||
                         !(binding.RuntimeArray || binding.ArrayLength > 0))
                {
                    errors.Add(where + " must be " + array.GlslType + " " + array.Name + "[]");
                }
                return;
            case SetConvention.StorageSet:
                if (binding.Binding == SetConvention.ProgramRecordBinding)
                {
                    if (binding.Kind != SpirvDescriptorKind.UniformBuffer) errors.Add(where + " must be the program record, a uniform block");
                    return;
                }
                if (Array.FindIndex(SetConvention.StorageBuffers, b => b.Value == binding.Binding) < 0)
                {
                    errors.Add(where + " is not a binding of set 2 (bindings.glsl)");
                }
                else if (binding.Kind != SpirvDescriptorKind.StorageBuffer)
                {
                    errors.Add(where + " must be a storage buffer");
                }
                return;
            default:
                errors.Add(where + ": only sets 0-" + (SetConvention.SetCount - 1) + " exist (bindings.glsl)");
                return;
        }
    }

    private static NativeBlock? ToNative(SpirvBlock? block) => block == null ? null : new NativeBlock
    {
        TypeName = block.TypeName,
        Size = block.Size,
        Members = block.Members.Select(m => new NativeMember
        {
            Name = m.Name,
            Type = m.GlslType,
            Offset = m.Offset,
            Size = m.Size,
            ArrayLength = m.ArrayLength,
        }).ToList(),
    };

    private static NativeInterfaceVariable ToNative(SpirvInterfaceVariable variable) => new()
    {
        Location = variable.Location,
        Name = variable.Name,
        Type = variable.GlslType,
        ArrayLength = variable.ArrayLength,
    };

    // ------------------------------------------------------------------ source scanning

    /// <summary>
    /// The file's text with every <c>#include</c> replaced by the included file, recursively.
    /// Include guards are the files' own business, as with <c>GL_GOOGLE_include_directive</c>.
    /// </summary>
    internal static string ExpandIncludes(string path, string sourceDirectory, ISet<string> included, int depth = 0)
    {
        if (depth > MaxIncludeDepth) throw new InvalidDataException("includes nest deeper than " + MaxIncludeDepth + " at " + path);
        string text = File.ReadAllText(path).Replace("\r\n", "\n");
        string directory = Path.GetDirectoryName(path) ?? sourceDirectory;

        return IncludeDirective.Replace(text, match =>
        {
            string name = match.Groups[1].Value;
            string? resolved = new[]
                {
                    Path.Combine(directory, name),
                    Path.Combine(sourceDirectory, IncludeDirectoryName, name),
                }
                .FirstOrDefault(File.Exists);
            if (resolved == null)
            {
                throw new InvalidDataException(Path.GetFileName(path) + " includes '" + name + "', found neither beside it nor in " + IncludeDirectoryName + "/");
            }
            included.Add(Path.GetFileName(resolved));
            return ExpandIncludes(resolved, sourceDirectory, included, depth + 1);
        });
    }

    /// <summary>The variant axes any conditional in the expanded stages tests, sorted.</summary>
    internal static List<string> AxesOf(string program, IEnumerable<string> texts, List<string> errors)
    {
        var axes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string text in texts)
        {
            foreach (Match match in ConditionalDirective.Matches(text))
            {
                string directive = match.Groups[1].Value;
                string condition = match.Groups[2].Value;
                foreach (Match token in Identifier.Matches(condition))
                {
                    if (Array.BinarySearch(VariantAxes, token.Value, StringComparer.Ordinal) < 0) continue;
                    if (directive is "ifdef" or "ifndef")
                    {
                        errors.Add(program + ": #" + directive + " " + token.Value + " - every axis is always defined (0 or 1); test it with #if");
                    }
                    axes.Add(token.Value);
                }
                foreach (Match defined in DefinedAxis.Matches(condition))
                {
                    if (Array.BinarySearch(VariantAxes, defined.Groups[1].Value, StringComparer.Ordinal) >= 0)
                    {
                        errors.Add(program + ": defined(" + defined.Groups[1].Value + ") - every axis is always defined (0 or 1); test its value");
                    }
                }
            }
        }
        return axes.ToList();
    }

    /// <summary>Sampler slot name to GLSL sampler type, from every <see cref="SamplerSlotMacro" /> use.</summary>
    internal static Dictionary<string, string> SamplerSlotDeclarations(string program, IEnumerable<string> texts, List<string> errors)
    {
        var slots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string text in texts)
        {
            // bindings.glsl documents the macro with an example use in a comment.
            foreach (Match match in SamplerSlot.Matches(StripComments(text)))
            {
                // The macro's own #define line is excluded by the pattern; a use can still share a
                // line with other text, so take every use on the matched line.
                foreach (Match use in SamplerSlotAnywhere.Matches(match.Value))
                {
                    string type = use.Groups[1].Value;
                    string name = use.Groups[2].Value;
                    if (Array.FindIndex(SetConvention.TextureArrays, b => b.GlslType == type) < 0)
                    {
                        errors.Add(program + ": sampler slot '" + name + "' has type " + type + ", which has no bindless array (bindings.glsl)");
                        continue;
                    }
                    if (slots.TryGetValue(name, out string? existing) && existing != type)
                    {
                        errors.Add(program + ": sampler slot '" + name + "' is declared both " + existing + " and " + type);
                        continue;
                    }
                    slots[name] = type;
                }
            }
        }
        return slots;
    }

    /// <summary>The text with <c>//</c> and <c>/* */</c> comments blanked out, line breaks kept.</summary>
    internal static string StripComments(string text)
    {
        var builder = new StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n') i++;
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                i += 2;
                while (i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'))
                {
                    if (text[i] == '\n') builder.Append('\n');
                    i++;
                }
                i = Math.Min(i + 2, text.Length);
                builder.Append(' ');
            }
            else
            {
                builder.Append(text[i]);
                i++;
            }
        }
        return builder.ToString();
    }

    // ------------------------------------------------------------------ output

    /// <summary>
    /// Writes a full build to <c>&lt;outputRoot&gt;/shaders-vk</c>: every SPIR-V file and the
    /// manifest, rewriting only files whose bytes changed, and deleting SPIR-V the build no longer
    /// produces.
    /// </summary>
    public static void Write(NativeShaderBuildResult result, string outputRoot)
    {
        string directory = Path.Combine(outputRoot, NativeShaderManifest.DirectoryName);
        Directory.CreateDirectory(directory);
        foreach (string stale in Directory.GetFiles(directory, "*.spv"))
        {
            if (!result.Files.ContainsKey(Path.GetFileName(stale))) File.Delete(stale);
        }
        foreach ((string name, byte[] bytes) in result.Files) WriteIfChanged(Path.Combine(directory, name), bytes);
        WriteIfChanged(Path.Combine(directory, NativeShaderManifest.FileName), Encoding.UTF8.GetBytes(result.Manifest.ToJson()));
    }

    /// <summary>
    /// Merges a one-program build into the manifest already in <paramref name="outputRoot" />, which
    /// must have been written by the same toolchain; replaces that program's SPIR-V.
    /// </summary>
    public static void WriteSingle(NativeShaderBuildResult result, string outputRoot)
    {
        string directory = Path.Combine(outputRoot, NativeShaderManifest.DirectoryName);
        string manifestPath = Path.Combine(directory, NativeShaderManifest.FileName);
        if (!File.Exists(manifestPath)) throw new InvalidDataException("no manifest at " + manifestPath + "; run --build first");

        NativeShaderManifest existing = NativeShaderManifest.Load(manifestPath);
        if (existing.Toolchain != result.Manifest.Toolchain)
        {
            throw new InvalidDataException("the manifest at " + manifestPath + " was built by another toolchain; run --build");
        }

        foreach (NativeProgram program in result.Manifest.Programs)
        {
            NativeProgram? old = existing.FindProgram(program.Name);
            if (old != null)
            {
                foreach (NativeStage stage in old.Variants.SelectMany(v => v.Stages))
                {
                    string stalePath = Path.Combine(directory, stage.Spirv);
                    if (!result.Files.ContainsKey(stage.Spirv) && File.Exists(stalePath)) File.Delete(stalePath);
                }
                existing.Programs.Remove(old);
            }
            existing.Programs.Add(program);
        }
        existing.Programs.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        foreach ((string name, byte[] bytes) in result.Files) WriteIfChanged(Path.Combine(directory, name), bytes);
        WriteIfChanged(manifestPath, Encoding.UTF8.GetBytes(existing.ToJson()));
    }

    /// <summary>Every way the files in <c>&lt;outputRoot&gt;/shaders-vk</c> differ from a fresh build; empty when identical.</summary>
    public static List<string> Compare(NativeShaderBuildResult result, string outputRoot)
    {
        var differences = new List<string>();
        string directory = Path.Combine(outputRoot, NativeShaderManifest.DirectoryName);
        string manifestPath = Path.Combine(directory, NativeShaderManifest.FileName);

        if (!File.Exists(manifestPath))
        {
            differences.Add("missing " + manifestPath);
        }
        else if (File.ReadAllText(manifestPath) != result.Manifest.ToJson())
        {
            differences.Add(NativeShaderManifest.FileName + " differs from a fresh build");
        }

        foreach ((string name, byte[] bytes) in result.Files)
        {
            string path = Path.Combine(directory, name);
            if (!File.Exists(path)) differences.Add("missing " + name);
            else if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) differences.Add(name + " differs (sha256 " +
                Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) + ", fresh build " + Convert.ToHexStringLower(SHA256.HashData(bytes)) + ")");
        }

        if (Directory.Exists(directory))
        {
            foreach (string file in Directory.GetFiles(directory, "*.spv"))
            {
                if (!result.Files.ContainsKey(Path.GetFileName(file))) differences.Add("unexpected " + Path.GetFileName(file));
            }
        }
        return differences;
    }

    private static void WriteIfChanged(string path, byte[] bytes)
    {
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes)) return;
        File.WriteAllBytes(path, bytes);
    }
}

/// <summary>
/// The command line of <c>tools/shader-compiler</c>, kept here so tests drive exactly what the
/// build runs:
/// <c>--build &lt;src&gt; &lt;out&gt;</c>, <c>--verify &lt;src&gt; &lt;out&gt;</c>,
/// <c>--single &lt;program&gt; &lt;src&gt; &lt;out&gt;</c>. Output lands in <c>&lt;out&gt;/shaders-vk</c>.
/// Exit codes: 0 success, 1 build or verify failure, 2 usage.
/// </summary>
internal static class NativeShaderTool
{
    public const string Usage =
        "usage: Optimum.Shaders.Compiler --build <source dir> <output dir>\n" +
        "       Optimum.Shaders.Compiler --verify <source dir> <output dir>\n" +
        "       Optimum.Shaders.Compiler --single <program> <source dir> <output dir>";

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        string mode = args.Length > 0 ? args[0] : "";
        int expected = mode == "--single" ? 4 : 3;
        if (mode is not ("--build" or "--verify" or "--single") || args.Length != expected)
        {
            error.WriteLine(Usage);
            return 2;
        }

        string? program = mode == "--single" ? args[1] : null;
        string source = args[expected - 2];
        string outputRoot = args[expected - 1];

        using var compiler = new ShaderCompiler();
        NativeShaderBuildResult result = new NativeShaderBuilder(compiler).Build(source, program);
        if (!result.Success)
        {
            foreach (string message in result.Errors) error.WriteLine("error: " + message);
            error.WriteLine("shader-compiler: " + result.Errors.Count + " error(s); nothing written");
            return 1;
        }

        int variants = result.Manifest.Programs.Sum(p => p.Variants.Count);
        string summary = result.Manifest.Programs.Count + " program(s), " + variants + " variant(s), " + result.Files.Count + " SPIR-V file(s)";
        try
        {
            switch (mode)
            {
                case "--build":
                    NativeShaderBuilder.Write(result, outputRoot);
                    output.WriteLine("shader-compiler: built " + summary + " into " + Path.Combine(outputRoot, NativeShaderManifest.DirectoryName));
                    return 0;
                case "--single":
                    NativeShaderBuilder.WriteSingle(result, outputRoot);
                    output.WriteLine("shader-compiler: rebuilt " + program + ", " + summary);
                    return 0;
                default:
                    List<string> differences = NativeShaderBuilder.Compare(result, outputRoot);
                    foreach (string difference in differences) error.WriteLine("differs: " + difference);
                    if (differences.Count > 0)
                    {
                        error.WriteLine("shader-compiler: " + differences.Count + " difference(s) against a fresh build of " + source);
                        return 1;
                    }
                    output.WriteLine("shader-compiler: verified " + summary);
                    return 0;
            }
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            error.WriteLine("error: " + failure.Message);
            return 1;
        }
    }
}
