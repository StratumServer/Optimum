using System;
using System.Collections.Generic;
using System.Globalization;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>One member of the generated default-uniform block.</summary>
/// <summary>One vertex input a program declares, and where it lives.</summary>
internal readonly record struct VertexInputSlot(string Name, int Location, GlslType Type);

internal sealed class UniformMember
{
    public string Name = "";
    public GlslType Type;
    /// <summary>0 when the member is not an array.</summary>
    public int ArrayLength;
    /// <summary>Byte offset into the block.</summary>
    public int Offset;
    /// <summary>Total bytes, counting every array element.</summary>
    public int Size;
    /// <summary>Default value as written in the shader, or null.</summary>
    public string? Initializer;

    public int ElementCount => ArrayLength == 0 ? 1 : ArrayLength;
}

internal sealed class SamplerBinding
{
    public string Name = "";
    public string TypeName = "";
    public int Binding;
}

internal sealed class BlockBinding
{
    public string BlockName = "";
    public int Set;
    public int Binding;
    /// <summary>True when the shader declared the binding itself.</summary>
    public bool Explicit;
}

/// <summary>
/// The complete interface of a linked program: uniforms, samplers, blocks, and
/// the location assignments for every stage boundary.
///
/// All of it has to be resolved per program rather than per stage, because GL
/// links by name and Vulkan links by number. Two consequences drive the design:
///
/// A uniform named in two stages is one uniform in GL - <c>zNear</c> is declared
/// in both the vertex and fragment shader and carries one value - so the backend
/// generates a single uniform block, byte-identical in every stage, whose members
/// are the union of what the stages declare.
///
/// A varying has no location in GL, but SPIR-V requires one on every user-defined
/// input and output, and the vertex output and fragment input must agree. The
/// vanilla shaders declare bare <c>out vec2 texCoord;</c>, so the backend assigns
/// those numbers itself and hands the same assignment to both stages.
///
/// This is why a stage cannot be compiled to SPIR-V alone, and why
/// <c>CompileShader</c> only stages work that <c>LinkProgram</c> finishes.
/// </summary>
internal sealed class ProgramInterfaceLayout
{
    public const string BlockTypeName = "OptimumUniforms";
    public const string BlockInstanceName = "_optimum";
    public const int DefaultBlockSet = 0;
    public const int DefaultBlockBinding = 0;
    public const int SamplerSet = 1;
    public const int StorageSet = 2;

    /// <summary>Members in declaration order, vertex stage first.</summary>
    public List<UniformMember> Members { get; } = new();
    public Dictionary<string, UniformMember> MembersByName { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Which members each stage actually declared.
    ///
    /// The generated block is emitted per stage rather than whole, because a name
    /// that is a uniform in one stage can be something else entirely in another:
    /// bilateralblur.vsh declares <c>uniform vec2 frameSize</c> while its
    /// fragment shader declares <c>in vec2 frameSize</c>. Emitting the union into
    /// both stages would redefine the varying. Members carry explicit offsets, so
    /// each stage sees a subset of one shared buffer layout.
    /// </summary>
    public Dictionary<EnumShaderType, HashSet<string>> MembersByStage { get; } = new();

    public List<SamplerBinding> Samplers { get; } = new();
    public Dictionary<string, SamplerBinding> SamplersByName { get; } = new(StringComparer.Ordinal);

    public List<BlockBinding> UniformBlocks { get; } = new();
    public List<BlockBinding> StorageBlocks { get; } = new();

    /// <summary>Stage-to-stage varying locations, keyed by variable name.</summary>
    public Dictionary<string, int> VaryingLocations { get; } = new(StringComparer.Ordinal);

    /// <summary>Vertex attribute locations for inputs that declared none.</summary>
    public Dictionary<string, int> VertexInputLocations { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Every vertex input the program declares, whatever supplies it.
    ///
    /// A shader routinely reads attributes the mesh does not carry - the GUI
    /// quad has only positions and UVs, while gui.vsh also declares a colour, a
    /// render-flags int, a damage effect and a joint id. GL answers those reads
    /// with the constant generic attribute, so the draw is well defined; Vulkan
    /// has no equivalent and the values are undefined. Knowing the full set is
    /// what lets the device supply the same constants.
    /// </summary>
    public List<VertexInputSlot> VertexInputs { get; } = new();

    /// <summary>Fragment output locations for outputs that declared none.</summary>
    public Dictionary<string, int> FragmentOutputLocations { get; } = new(StringComparer.Ordinal);

    /// <summary>Size of the generated block in bytes; 0 when it has no members.</summary>
    public int BlockSize { get; private set; }

    public bool HasUniformBlock => BlockSize > 0;

    /// <summary>Diagnostics that made the layout unusable.</summary>
    public List<string> Errors { get; } = new();
    public bool HasErrors => Errors.Count > 0;

    /// <summary>
    /// Builds the shadow buffer the CPU writes uniforms into, pre-filled with any
    /// initialisers the shaders declared. GL applies those defaults at link time
    /// and shaders rely on it: final.fsh never assigns <c>extraGamma</c> unless
    /// colour grading is active and expects the declared 1.0.
    /// </summary>
    public byte[] CreateShadowBuffer()
    {
        var buffer = new byte[Math.Max(BlockSize, 0)];
        foreach (UniformMember member in Members)
        {
            if (member.Initializer != null)
            {
                WriteInitializer(buffer, member);
            }
        }
        return buffer;
    }

    private static void WriteInitializer(byte[] buffer, UniformMember member)
    {
        // Only scalar literal defaults are honoured. Every initialiser in the
        // shipped shaders is one; a constructor expression would need an
        // evaluator to be worth supporting.
        if (member.ArrayLength != 0 || member.Type.ComponentCount != 1) return;

        string text = member.Initializer!.Trim();
        switch (member.Type.Name)
        {
            case "float":
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
                {
                    BitConverter.TryWriteBytes(buffer.AsSpan(member.Offset), f);
                }
                break;
            case "int":
            case "uint":
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
                {
                    BitConverter.TryWriteBytes(buffer.AsSpan(member.Offset), i);
                }
                break;
            case "bool":
                BitConverter.TryWriteBytes(buffer.AsSpan(member.Offset), text == "true" ? 1 : 0);
                break;
        }
    }

    /// <summary>
    /// Unions the stages into one layout. Stages arrive in a fixed order - vertex,
    /// fragment, geometry - so the result is deterministic and the SPIR-V cache
    /// key is stable across runs.
    /// </summary>
    /// <param name="declaredAttributes">
    /// Locations from <c>IShaderProgram</c>'s BindAttribLocation map, for mods
    /// that name attributes through the API instead of a layout qualifier.
    /// </param>
    public static ProgramInterfaceLayout Build(
        IReadOnlyList<(EnumShaderType Stage, ParsedShader Parsed)> stages,
        IReadOnlyDictionary<string, int>? declaredAttributes = null)
    {
        var layout = new ProgramInterfaceLayout();
        int offset = 0;
        int nextUniformBlockBinding = DefaultBlockBinding + 1;
        int nextStorageBinding = 0;

        foreach ((EnumShaderType stage, ParsedShader parsed) in stages)
        {
            foreach (GlslDeclaration declaration in parsed.Declarations)
            {
                switch (declaration.Kind)
                {
                    case GlslDeclarationKind.DefaultUniform:
                        AddDefaultUniform(layout, declaration, stage, ref offset);
                        break;
                    case GlslDeclarationKind.OpaqueUniform:
                        AddSampler(layout, declaration);
                        break;
                    case GlslDeclarationKind.UniformBlock:
                        AddBlock(layout.UniformBlocks, declaration, DefaultBlockSet, ref nextUniformBlockBinding);
                        break;
                    case GlslDeclarationKind.StorageBlock:
                        AddBlock(layout.StorageBlocks, declaration, StorageSet, ref nextStorageBinding);
                        break;
                }
            }
        }

        AssignInterfaceLocations(layout, stages, declaredAttributes);

        layout.BlockSize = offset;
        return layout;
    }

    // ------------------------------------------------------------------ uniforms

    private static void AddDefaultUniform(
        ProgramInterfaceLayout layout, GlslDeclaration declaration, EnumShaderType stage, ref int offset)
    {
        if (!GlslType.TryParse(declaration.TypeName, out GlslType type))
        {
            // A struct-typed uniform, or a type this backend does not model. The
            // rewriter leaves the declaration alone, so the shader still compiles;
            // it simply is not settable through the generated block.
            return;
        }

        if (!layout.MembersByStage.TryGetValue(stage, out HashSet<string>? stageMembers))
        {
            stageMembers = new HashSet<string>(StringComparer.Ordinal);
            layout.MembersByStage[stage] = stageMembers;
        }
        stageMembers.Add(declaration.Name);

        if (declaration.UnresolvedArraySize != null)
        {
            layout.Errors.Add(
                $"uniform '{declaration.Name}' has array size '{declaration.UnresolvedArraySize}' " +
                $"which did not resolve to a constant in the {stage} stage");
            return;
        }

        if (layout.MembersByName.TryGetValue(declaration.Name, out UniformMember? existing))
        {
            // Declared in more than one stage. GL merges them; so do we, but only
            // when they agree - a mismatch is a bug GL would reject at link time.
            if (!existing.Type.Equals(type) || existing.ArrayLength != declaration.ArrayLength)
            {
                layout.Errors.Add(
                    $"uniform '{declaration.Name}' is declared as '{existing.Type.Name}' " +
                    $"and '{type.Name}' in different stages");
            }
            return;
        }

        offset = Align(offset, type.Alignment);

        var member = new UniformMember
        {
            Name = declaration.Name,
            Type = type,
            ArrayLength = declaration.ArrayLength,
            Offset = offset,
            Initializer = declaration.Initializer,
        };
        member.Size = type.Size * member.ElementCount;
        offset += member.Size;

        layout.Members.Add(member);
        layout.MembersByName[member.Name] = member;
    }

    private static void AddSampler(ProgramInterfaceLayout layout, GlslDeclaration declaration)
    {
        if (layout.SamplersByName.ContainsKey(declaration.Name)) return;

        var binding = new SamplerBinding
        {
            Name = declaration.Name,
            TypeName = declaration.TypeName,
            Binding = layout.Samplers.Count,
        };
        layout.Samplers.Add(binding);
        layout.SamplersByName[binding.Name] = binding;
    }

    private static void AddBlock(
        List<BlockBinding> blocks, GlslDeclaration declaration, int set, ref int nextBinding)
    {
        foreach (BlockBinding existing in blocks)
        {
            if (existing.BlockName == declaration.Name) return;
        }

        // A shader that names its own binding keeps it: chunkopaque.vsh declares
        // "layout(binding = 3, std430) readonly buffer faceDataBuf", and the mesh
        // path binds the vertex buffer to that exact index.
        int declared = ReadQualifierInt(declaration.LayoutQualifiers, "binding");

        // Set 0, binding 0 is where the generated OptimumUniforms block lives.
        // A shader that names that binding itself would register two blocks at
        // one descriptor binding, so it is treated as unnumbered and moves to
        // the next free binding; the rewriter re-emits the qualifier from here.
        if (set == DefaultBlockSet && declared == DefaultBlockBinding) declared = -1;

        blocks.Add(new BlockBinding
        {
            BlockName = declaration.Name,
            Set = set,
            Binding = declared >= 0 ? declared : nextBinding,
            Explicit = declared >= 0,
        });

        if (declared < 0) nextBinding++;
        else if (declared >= nextBinding) nextBinding = declared + 1;
    }

    // ----------------------------------------------------------------- locations

    /// <summary>
    /// Assigns the numbers SPIR-V demands and GLSL 330 leaves implicit: vertex
    /// attribute locations, stage-to-stage varying locations, and fragment output
    /// locations. Explicit qualifiers already in the source always win, and the
    /// generated numbers fill the gaps around them.
    /// </summary>
    private static void AssignInterfaceLocations(
        ProgramInterfaceLayout layout,
        IReadOnlyList<(EnumShaderType Stage, ParsedShader Parsed)> stages,
        IReadOnlyDictionary<string, int>? declaredAttributes)
    {
        var usedVertexInputs = new HashSet<int>();
        var usedVaryings = new HashSet<int>();
        var usedFragmentOutputs = new HashSet<int>();

        // Pass one: record every location the shaders stated outright.
        foreach ((EnumShaderType stage, ParsedShader parsed) in stages)
        {
            foreach (GlslDeclaration declaration in parsed.Declarations)
            {
                if (declaration.Location < 0) continue;

                if (stage == EnumShaderType.VertexShader && declaration.Kind == GlslDeclarationKind.Input)
                {
                    Occupy(usedVertexInputs, declaration.Location, LocationSpan(declaration));
                    RecordVertexInput(layout, declaration, declaration.Location);
                }
                else if (stage == EnumShaderType.FragmentShader && declaration.Kind == GlslDeclarationKind.Output)
                {
                    Occupy(usedFragmentOutputs, declaration.Location, LocationSpan(declaration));
                }
                else
                {
                    layout.VaryingLocations[declaration.Name] = declaration.Location;
                    Occupy(usedVaryings, declaration.Location, LocationSpan(declaration));
                }
            }
        }

        // Attribute locations bound through the API rather than the shader.
        if (declaredAttributes != null)
        {
            foreach (KeyValuePair<string, int> attribute in declaredAttributes)
            {
                layout.VertexInputLocations[attribute.Key] = attribute.Value;
                Occupy(usedVertexInputs, attribute.Value, 1);
            }
        }

        // Pass two: fill in the rest.
        foreach ((EnumShaderType stage, ParsedShader parsed) in stages)
        {
            foreach (GlslDeclaration declaration in parsed.Declarations)
            {
                if (declaration.Location >= 0) continue;
                int span = LocationSpan(declaration);

                if (stage == EnumShaderType.VertexShader && declaration.Kind == GlslDeclarationKind.Input)
                {
                    // A name already present came from declaredAttributes - bound
                    // through the API rather than the shader - and still needs
                    // recording, because the location is known but the type only
                    // appears here.
                    if (layout.VertexInputLocations.TryGetValue(declaration.Name, out int bound))
                    {
                        RecordVertexInput(layout, declaration, bound);
                        continue;
                    }
                    int assigned = Reserve(usedVertexInputs, span);
                    layout.VertexInputLocations[declaration.Name] = assigned;
                    RecordVertexInput(layout, declaration, assigned);
                }
                else if (stage == EnumShaderType.FragmentShader && declaration.Kind == GlslDeclarationKind.Output)
                {
                    if (layout.FragmentOutputLocations.ContainsKey(declaration.Name)) continue;
                    layout.FragmentOutputLocations[declaration.Name] = Reserve(usedFragmentOutputs, span);
                }
                else if (declaration.Kind is GlslDeclarationKind.Input or GlslDeclarationKind.Output)
                {
                    // A varying. The first stage to mention the name fixes the
                    // number; the matching stage reads it back out of the map, so
                    // vertex out and fragment in always agree.
                    if (layout.VaryingLocations.ContainsKey(declaration.Name)) continue;
                    layout.VaryingLocations[declaration.Name] = Reserve(usedVaryings, span);
                }
            }
        }
    }

    /// <summary>
    /// How many consecutive locations a variable consumes. A vector of any width
    /// fits in one; a matrix takes one per column; an array multiplies by its
    /// length.
    /// </summary>
    /// <summary>
    /// Notes a vertex input so the device can supply GL's constant default when
    /// the mesh does not carry it. Arrays and matrices are skipped: nothing in
    /// the game declares one as a vertex input, and spanning several locations
    /// would need a default per column rather than per attribute.
    /// </summary>
    private static void RecordVertexInput(
        ProgramInterfaceLayout layout, GlslDeclaration declaration, int location)
    {
        if (location < 0) return;
        if (declaration.ArrayLength != 0) return;
        if (!GlslType.TryParse(declaration.TypeName, out GlslType type)) return;
        if (type.IsMatrix || type.IsOpaque) return;

        foreach (VertexInputSlot existing in layout.VertexInputs)
        {
            if (existing.Location == location) return;
        }
        layout.VertexInputs.Add(new VertexInputSlot(declaration.Name, location, type));
    }

    private static int LocationSpan(GlslDeclaration declaration)
    {
        int elements = declaration.ArrayLength == 0 ? 1 : declaration.ArrayLength;
        int perElement = GlslType.TryParse(declaration.TypeName, out GlslType type) && type.IsMatrix
            ? type.Columns
            : 1;
        return Math.Max(1, elements * perElement);
    }

    private static void Occupy(HashSet<int> used, int start, int span)
    {
        for (int i = 0; i < span; i++) used.Add(start + i);
    }

    private static int Reserve(HashSet<int> used, int span)
    {
        int candidate = 0;
        while (true)
        {
            bool free = true;
            for (int i = 0; i < span; i++)
            {
                if (used.Contains(candidate + i)) { free = false; break; }
            }
            if (free)
            {
                Occupy(used, candidate, span);
                return candidate;
            }
            candidate++;
        }
    }

    private static int ReadQualifierInt(string? qualifiers, string key)
    {
        if (qualifiers == null) return -1;
        foreach (string part in qualifiers.Split(','))
        {
            int equals = part.IndexOf('=');
            if (equals < 0) continue;
            if (part.AsSpan(0, equals).Trim().SequenceEqual(key) &&
                int.TryParse(part.AsSpan(equals + 1).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }
        }
        return -1;
    }

    private static int Align(int value, int alignment) =>
        alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;
}
