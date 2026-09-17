using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>
/// <c>shaders.manifest.json</c>: what the offline compiler (<c>tools/shader-compiler</c>) produced
/// from <c>sources/shaders-vk</c> and what the runtime links against
/// (docs/vulkan-native-shaders.md section 6).
///
/// Written and read with <see cref="Utf8JsonWriter" /> and <see cref="JsonDocument" /> directly:
/// the output is byte-for-byte deterministic (the <c>--verify</c> gate compares it as bytes), and
/// no reflection-based serializer is involved at runtime.
/// </summary>
internal sealed class NativeShaderManifest
{
    /// <summary>Bumped whenever a field is added, removed or changes meaning; readers refuse any other value.</summary>
    public const int CurrentSchemaVersion = 1;

    public const string FileName = "shaders.manifest.json";

    /// <summary>The output directory, beside <c>Optimum.Render.Vulkan.dll</c>; never under <c>assets/</c>.</summary>
    public const string DirectoryName = "shaders-vk";

    public int SchemaVersion = CurrentSchemaVersion;

    /// <summary><see cref="ShaderCompiler.Identity" /> of the compiler that produced every blob.</summary>
    public string Toolchain = "";

    /// <summary>Sorted by name.</summary>
    public List<NativeProgram> Programs = new();

    public NativeProgram? FindProgram(string name) => Programs.Find(p => p.Name == name);

    public NativeVariant? Find(string program, string variantKey) =>
        FindProgram(program)?.Variants.Find(v => v.Key == variantKey);

    /// <summary>
    /// The variant key for a set of axis values: the sorted <c>NAME=value</c> list, comma separated,
    /// restricted to <paramref name="axes" /> (the axes the program branches on). Empty when none.
    /// </summary>
    public static string VariantKey(IEnumerable<string> axes, IReadOnlyDictionary<string, int> values)
    {
        var names = new List<string>(axes);
        names.Sort(StringComparer.Ordinal);
        var parts = new List<string>(names.Count);
        foreach (string name in names)
        {
            parts.Add(name + "=" + (values.TryGetValue(name, out int value) ? value : 0).ToString(CultureInfo.InvariantCulture));
        }
        return string.Join(",", parts);
    }

    // ------------------------------------------------------------------ writer

    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            writer.WriteString("toolchain", Toolchain);
            writer.WriteStartArray("programs");
            foreach (NativeProgram program in Programs) WriteProgram(writer, program);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray()) + "\n";
    }

    private static void WriteProgram(Utf8JsonWriter writer, NativeProgram program)
    {
        writer.WriteStartObject();
        writer.WriteString("name", program.Name);
        WriteStrings(writer, "axes", program.Axes);
        writer.WriteStartArray("variants");
        foreach (NativeVariant variant in program.Variants)
        {
            writer.WriteStartObject();
            writer.WriteString("key", variant.Key);

            writer.WriteStartArray("stages");
            foreach (NativeStage stage in variant.Stages)
            {
                writer.WriteStartObject();
                writer.WriteString("stage", stage.Stage);
                writer.WriteString("source", stage.Source);
                writer.WriteString("spirv", stage.Spirv);
                writer.WriteString("sha256", stage.Sha256);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            WriteBlock(writer, "push", variant.Push);
            WriteBlock(writer, "record", variant.Record);
            WriteStrings(writer, "frameMembers", variant.FrameMembers);

            writer.WriteStartArray("samplers");
            foreach (NativeSampler sampler in variant.Samplers)
            {
                writer.WriteStartObject();
                writer.WriteString("name", sampler.Name);
                writer.WriteString("glslType", sampler.GlslType);
                writer.WriteString("bindlessArray", sampler.BindlessArray);
                writer.WriteNumber("arrayBinding", sampler.ArrayBinding);
                writer.WriteNumber("pushOffset", sampler.PushOffset);
                writer.WriteNumber("order", sampler.Order);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("frameTextures");
            foreach (NativeFrameTexture texture in variant.FrameTextures)
            {
                writer.WriteStartObject();
                writer.WriteString("name", texture.Name);
                writer.WriteString("glslType", texture.GlslType);
                writer.WriteNumber("binding", texture.Binding);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("storageBindings");
            foreach (NativeStorageBinding binding in variant.StorageBindings)
            {
                writer.WriteStartObject();
                writer.WriteString("name", binding.Name);
                writer.WriteNumber("set", binding.Set);
                writer.WriteNumber("binding", binding.Binding);
                writer.WriteString("descriptorType", binding.DescriptorType);
                writer.WriteNumber("arrayLength", binding.ArrayLength);
                writer.WriteBoolean("runtimeArray", binding.RuntimeArray);
                writer.WriteBoolean("used", binding.Used);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            WriteInterface(writer, "vertexInputs", variant.VertexInputs);
            WriteInterface(writer, "fragmentOutputs", variant.FragmentOutputs);
            writer.WriteNumber("writtenOutputs", variant.WrittenOutputs);

            writer.WriteStartArray("specializationConstants");
            foreach (NativeSpecConstant constant in variant.SpecializationConstants)
            {
                writer.WriteStartObject();
                writer.WriteNumber("id", constant.Id);
                writer.WriteString("name", constant.Name);
                writer.WriteString("type", constant.Type);
                if (constant.Type == "bool") writer.WriteBoolean("default", constant.Default != 0);
                else writer.WriteNumber("default", constant.Default);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteBlock(Utf8JsonWriter writer, string property, NativeBlock? block)
    {
        if (block == null)
        {
            writer.WriteNull(property);
            return;
        }
        writer.WriteStartObject(property);
        writer.WriteString("typeName", block.TypeName);
        writer.WriteNumber("size", block.Size);
        writer.WriteStartArray("members");
        foreach (NativeMember member in block.Members)
        {
            writer.WriteStartObject();
            writer.WriteString("name", member.Name);
            writer.WriteString("type", member.Type);
            writer.WriteNumber("offset", member.Offset);
            writer.WriteNumber("size", member.Size);
            writer.WriteNumber("arrayLength", member.ArrayLength);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteInterface(Utf8JsonWriter writer, string property, List<NativeInterfaceVariable> variables)
    {
        writer.WriteStartArray(property);
        foreach (NativeInterfaceVariable variable in variables)
        {
            writer.WriteStartObject();
            writer.WriteNumber("location", variable.Location);
            writer.WriteString("name", variable.Name);
            writer.WriteString("type", variable.Type);
            writer.WriteNumber("arrayLength", variable.ArrayLength);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string property, List<string> values)
    {
        writer.WriteStartArray(property);
        foreach (string value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    // ------------------------------------------------------------------ reader

    /// <summary>
    /// Parses a manifest. Throws <see cref="InvalidDataException" /> for malformed JSON, a missing
    /// field, or a schema version other than <see cref="CurrentSchemaVersion" />.
    /// </summary>
    public static NativeShaderManifest Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException error)
        {
            throw new InvalidDataException("shader manifest is not valid JSON: " + error.Message, error);
        }

        using (document)
        {
            JsonElement root = document.RootElement;
            int version = Int(root, "schemaVersion");
            if (version != CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    "shader manifest schema version " + version + ", this build reads " + CurrentSchemaVersion);
            }

            var manifest = new NativeShaderManifest
            {
                SchemaVersion = version,
                Toolchain = Str(root, "toolchain"),
            };
            foreach (JsonElement program in Arr(root, "programs"))
            {
                manifest.Programs.Add(ReadProgram(program));
            }
            return manifest;
        }
    }

    public static NativeShaderManifest Load(string path) => Parse(File.ReadAllText(path));

    private static NativeProgram ReadProgram(JsonElement element)
    {
        var program = new NativeProgram { Name = Str(element, "name"), Axes = Strings(element, "axes") };
        foreach (JsonElement v in Arr(element, "variants"))
        {
            var variant = new NativeVariant
            {
                Key = Str(v, "key"),
                Push = ReadBlock(v, "push"),
                Record = ReadBlock(v, "record"),
                FrameMembers = Strings(v, "frameMembers"),
                VertexInputs = ReadInterface(v, "vertexInputs"),
                FragmentOutputs = ReadInterface(v, "fragmentOutputs"),
                WrittenOutputs = Get(v, "writtenOutputs").GetUInt32(),
            };
            foreach (JsonElement s in Arr(v, "stages"))
            {
                variant.Stages.Add(new NativeStage
                {
                    Stage = Str(s, "stage"),
                    Source = Str(s, "source"),
                    Spirv = Str(s, "spirv"),
                    Sha256 = Str(s, "sha256"),
                });
            }
            foreach (JsonElement s in Arr(v, "samplers"))
            {
                variant.Samplers.Add(new NativeSampler
                {
                    Name = Str(s, "name"),
                    GlslType = Str(s, "glslType"),
                    BindlessArray = Str(s, "bindlessArray"),
                    ArrayBinding = Int(s, "arrayBinding"),
                    PushOffset = Int(s, "pushOffset"),
                    Order = Int(s, "order"),
                });
            }
            foreach (JsonElement t in Arr(v, "frameTextures"))
            {
                variant.FrameTextures.Add(new NativeFrameTexture
                {
                    Name = Str(t, "name"),
                    GlslType = Str(t, "glslType"),
                    Binding = Int(t, "binding"),
                });
            }
            foreach (JsonElement b in Arr(v, "storageBindings"))
            {
                variant.StorageBindings.Add(new NativeStorageBinding
                {
                    Name = Str(b, "name"),
                    Set = Int(b, "set"),
                    Binding = Int(b, "binding"),
                    DescriptorType = Str(b, "descriptorType"),
                    ArrayLength = Int(b, "arrayLength"),
                    RuntimeArray = Get(b, "runtimeArray").GetBoolean(),
                    Used = Get(b, "used").GetBoolean(),
                });
            }
            foreach (JsonElement c in Arr(v, "specializationConstants"))
            {
                string type = Str(c, "type");
                JsonElement value = Get(c, "default");
                variant.SpecializationConstants.Add(new NativeSpecConstant
                {
                    Id = Int(c, "id"),
                    Name = Str(c, "name"),
                    Type = type,
                    Default = value.ValueKind switch
                    {
                        JsonValueKind.True => 1,
                        JsonValueKind.False => 0,
                        _ => value.GetDouble(),
                    },
                });
            }
            program.Variants.Add(variant);
        }
        return program;
    }

    private static NativeBlock? ReadBlock(JsonElement parent, string property)
    {
        JsonElement element = Get(parent, property);
        if (element.ValueKind == JsonValueKind.Null) return null;
        var block = new NativeBlock { TypeName = Str(element, "typeName"), Size = Int(element, "size") };
        foreach (JsonElement m in Arr(element, "members"))
        {
            block.Members.Add(new NativeMember
            {
                Name = Str(m, "name"),
                Type = Str(m, "type"),
                Offset = Int(m, "offset"),
                Size = Int(m, "size"),
                ArrayLength = Int(m, "arrayLength"),
            });
        }
        return block;
    }

    private static List<NativeInterfaceVariable> ReadInterface(JsonElement parent, string property)
    {
        var list = new List<NativeInterfaceVariable>();
        foreach (JsonElement e in Arr(parent, property))
        {
            list.Add(new NativeInterfaceVariable
            {
                Location = Int(e, "location"),
                Name = Str(e, "name"),
                Type = Str(e, "type"),
                ArrayLength = Int(e, "arrayLength"),
            });
        }
        return list;
    }

    private static JsonElement Get(JsonElement parent, string property)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(property, out JsonElement value))
        {
            throw new InvalidDataException("shader manifest: missing '" + property + "'");
        }
        return value;
    }

    private static string Str(JsonElement parent, string property) =>
        Get(parent, property).GetString() ?? throw new InvalidDataException("shader manifest: null '" + property + "'");

    private static int Int(JsonElement parent, string property) => Get(parent, property).GetInt32();

    private static JsonElement.ArrayEnumerator Arr(JsonElement parent, string property)
    {
        JsonElement value = Get(parent, property);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("shader manifest: '" + property + "' is not an array");
        }
        return value.EnumerateArray();
    }

    private static List<string> Strings(JsonElement parent, string property)
    {
        var list = new List<string>();
        foreach (JsonElement e in Arr(parent, property)) list.Add(e.GetString() ?? "");
        return list;
    }
}

internal sealed class NativeProgram
{
    /// <summary>The program's <c>PassName</c>, which is also its source file base name.</summary>
    public string Name = "";
    /// <summary>The variant axes the source branches on, sorted.</summary>
    public List<string> Axes = new();
    /// <summary>One per combination of <see cref="Axes" /> values, sorted by key.</summary>
    public List<NativeVariant> Variants = new();
}

internal sealed class NativeVariant
{
    /// <summary><see cref="NativeShaderManifest.VariantKey" />: sorted <c>NAME=value</c>, comma separated.</summary>
    public string Key = "";
    public List<NativeStage> Stages = new();
    /// <summary>The push-constant block; null when the program declares none.</summary>
    public NativeBlock? Push;
    /// <summary>The program record at set 2, <c>OPTIMUM_BINDING_PROGRAM_RECORD</c>; null when none.</summary>
    public NativeBlock? Record;
    /// <summary>FrameGlobals members the program reads under their own name (owner include rule), in block order.</summary>
    public List<string> FrameMembers = new();
    /// <summary>Bindless sampler slots, in push-block (GLSL 330 declaration) order.</summary>
    public List<NativeSampler> Samplers = new();
    /// <summary>The fixed set 0 textures the shipped modules sample.</summary>
    public List<NativeFrameTexture> FrameTextures = new();
    public List<NativeStorageBinding> StorageBindings = new();
    public List<NativeInterfaceVariable> VertexInputs = new();
    public List<NativeInterfaceVariable> FragmentOutputs = new();
    /// <summary>Bit n set when the shipped fragment module stores to the output at location n.</summary>
    public uint WrittenOutputs;
    public List<NativeSpecConstant> SpecializationConstants = new();
}

internal sealed class NativeStage
{
    /// <summary><c>vertex</c> or <c>fragment</c>.</summary>
    public string Stage = "";
    /// <summary>Source file name relative to the source directory.</summary>
    public string Source = "";
    /// <summary>SPIR-V file name relative to the manifest's directory.</summary>
    public string Spirv = "";
    /// <summary>Lower-case hex SHA-256 of the SPIR-V file.</summary>
    public string Sha256 = "";
}

internal sealed class NativeBlock
{
    public string TypeName = "";
    public int Size;
    public List<NativeMember> Members = new();
}

internal sealed class NativeMember
{
    public string Name = "";
    public string Type = "";
    public int Offset;
    public int Size;
    /// <summary>0 when not an array, -1 for a runtime-sized array.</summary>
    public int ArrayLength;
}

internal sealed class NativeSampler
{
    /// <summary>The GLSL 330 sampler name, which is also the push member's name.</summary>
    public string Name = "";
    public string GlslType = "";
    /// <summary>The set 1 array the slot indexes (<c>optimumTextures2D</c>, ...).</summary>
    public string BindlessArray = "";
    public int ArrayBinding;
    /// <summary>Offset of the <c>uint</c> slot index in the push block.</summary>
    public int PushOffset;
    /// <summary>Position among the program's sampler slots; matches the GLSL 330 declaration order by authoring.</summary>
    public int Order;
}

internal sealed class NativeFrameTexture
{
    public string Name = "";
    public string GlslType = "";
    public int Binding;
}

internal sealed class NativeStorageBinding
{
    public string Name = "";
    public int Set;
    public int Binding;
    /// <summary><c>storageBuffer</c> or <c>uniformBuffer</c>.</summary>
    public string DescriptorType = "";
    public int ArrayLength;
    public bool RuntimeArray;
    /// <summary>Whether a shipped (optimised) module still reads it.</summary>
    public bool Used;
}

internal sealed class NativeInterfaceVariable
{
    public int Location;
    public string Name = "";
    public string Type = "";
    public int ArrayLength;
}

internal sealed class NativeSpecConstant
{
    public int Id;
    public string Name = "";
    /// <summary><c>bool</c>, <c>int</c>, <c>uint</c>, <c>float</c> or <c>double</c>.</summary>
    public string Type = "";
    /// <summary>The declared default; a bool is 0 or 1.</summary>
    public double Default;
}
