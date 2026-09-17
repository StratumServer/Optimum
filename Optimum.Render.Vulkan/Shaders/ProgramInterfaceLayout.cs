using System;
using System.Collections.Generic;
using System.Globalization;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>One vertex input a program declares, and where it lives.</summary>
internal readonly record struct VertexInputSlot(string Name, int Location, GlslType Type);

/// <summary>One member of the program record (the generated default-uniform block).</summary>
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

/// <summary>
/// One sampler a program declares. Under the shared pipeline layout (plan decision 9)
/// a sampler is either one of set 0's fixed frame textures, read under its own name,
/// or a slot index into set 1's bindless array of its kind, carried in the push block
/// under the sampler's name.
/// </summary>
internal sealed class SamplerBinding
{
    public string Name = "";
    public string TypeName = "";

    /// <summary>
    /// Declaration order across the program, vertex stage first: the texture unit the
    /// client's own bookkeeping (<c>ShaderProgram.collectUniformNames</c>) assigns by default.
    /// </summary>
    public int Order;

    /// <summary>The set 0 binding when this is a fixed frame texture (<see cref="SetConvention.FrameTextures" />), else -1.</summary>
    public int FrameBinding = -1;

    /// <summary>The set 1 array the slot indexes; meaningful only when <see cref="IsFrameTexture" /> is false.</summary>
    public TextureKind Kind;

    /// <summary>Byte offset of the slot index in the push block, or -1 for a frame texture.</summary>
    public int PushOffset = -1;

    public bool IsFrameTexture => FrameBinding >= 0;
}

/// <summary>A named uniform or storage block and its set 2 binding.</summary>
internal sealed class BlockBinding
{
    public string BlockName = "";
    public int Set = SetConvention.StorageSet;
    public int Binding;
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
internal sealed partial class ProgramInterfaceLayout
{
    public const string BlockTypeName = "OptimumUniforms";
    public const string PushBlockTypeName = "OptimumDraw";

    /// <summary>Bytes of one sampler slot index in the push block.</summary>
    public const int SlotBytes = 4;

    /// <summary>
    /// The shared frame members each stage declared (see <see cref="FrameGlobals" />).
    /// Emitted per stage for the same reason <see cref="MembersByStage" /> is.
    /// </summary>
    public Dictionary<EnumShaderType, HashSet<string>> FrameMembersByStage { get; } = new();

    /// <summary>
    /// The array length each shared frame member was declared with in this program:
    /// a shader may read a prefix of the shared array (<c>pointLights[DYNLIGHTS]</c>).
    /// 0 for a member that is not an array.
    /// </summary>
    public Dictionary<string, int> FrameMemberDeclaredLengths { get; } = new(StringComparer.Ordinal);

    /// <summary>Whether the program reads anything from the shared frame block.</summary>
    public bool UsesFrameBlock => FrameMemberDeclaredLengths.Count > 0;

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

    /// <summary>Every sampler, frame textures included, in declaration order.</summary>
    public List<SamplerBinding> Samplers { get; } = new();
    public Dictionary<string, SamplerBinding> SamplersByName { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Which samplers each stage declared: a stage gets push members and body rewrites
    /// only for its own, for the reason <see cref="MembersByStage" /> exists.
    /// </summary>
    public Dictionary<EnumShaderType, HashSet<string>> SamplersByStage { get; } = new();

    /// <summary>Bytes of the push block: one slot index per non-frame sampler; 0 when there are none.</summary>
    public int PushConstantSize { get; private set; }

    /// <summary>Whether any sampler reads a set 0 frame texture.</summary>
    public bool UsesFrameTextures { get; private set; }

    /// <summary>Whether a draw of the program needs set 2: a record, a named block or a storage block.</summary>
    public bool UsesStorageSet => HasUniformBlock || UniformBlocks.Count > 0 || StorageBlocks.Count > 0;

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

    /// <summary>
    /// Colour locations the fragment shader actually assigns somewhere in its
    /// body. GL leaves an enabled attachment alone when the shader never writes
    /// its output (undefined by the spec, preserved by every driver we ship on);
    /// Vulkan writes undefined values into it. The device zeroes the colour
    /// write mask of every attachment outside this set so both backends keep
    /// the attachment's previous contents - the SSAO G-buffer under a
    /// fullscreen compose pass, for one.
    /// </summary>
    public HashSet<int> WrittenFragmentOutputs { get; } = new();

    /// <summary>Size of the program record in bytes; 0 when it has no members.</summary>
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

    internal static void WriteInitializer(byte[] buffer, UniformMember member)
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
    /// <param name="includes">
    /// The program's include files; a uniform whose owning include is among them
    /// reads the shared frame block (<see cref="FrameGlobals.TryPlace" />).
    /// </param>
    public static ProgramInterfaceLayout Build(
        IReadOnlyList<(EnumShaderType Stage, ParsedShader Parsed)> stages,
        IReadOnlyDictionary<string, int>? declaredAttributes = null,
        IReadOnlySet<string>? includes = null)
    {
        var layout = new ProgramInterfaceLayout();
        int offset = 0;
        int nextNamedBinding = SetConvention.NamedBlockFirstBinding;

        foreach ((EnumShaderType stage, ParsedShader parsed) in stages)
        {
            foreach (GlslDeclaration declaration in parsed.Declarations)
            {
                switch (declaration.Kind)
                {
                    case GlslDeclarationKind.DefaultUniform:
                        AddDefaultUniform(layout, declaration, stage, includes, ref offset);
                        break;
                    case GlslDeclarationKind.OpaqueUniform:
                        AddSampler(layout, declaration, stage);
                        break;
                    case GlslDeclarationKind.UniformBlock:
                        AddBlock(layout, layout.UniformBlocks, declaration, ref nextNamedBinding);
                        break;
                    case GlslDeclarationKind.StorageBlock:
                        AddBlock(layout, layout.StorageBlocks, declaration, ref nextNamedBinding);
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
        ProgramInterfaceLayout layout, GlslDeclaration declaration, EnumShaderType stage,
        IReadOnlySet<string>? includes, ref int offset)
    {
        if (!GlslType.TryParse(declaration.TypeName, out GlslType type))
        {
            // A struct-typed uniform, or a type this backend does not model. The
            // rewriter leaves the declaration alone, so the shader still compiles;
            // it simply is not settable through the generated block.
            return;
        }

        // A value Use() writes into every program that includes its owner: it reads
        // the shared frame block instead of taking room in this program's own.
        if (declaration.UnresolvedArraySize == null &&
            FrameGlobals.TryPlace(declaration.Name, type, declaration.ArrayLength, includes, out _))
        {
            if (!layout.FrameMembersByStage.TryGetValue(stage, out HashSet<string>? frameMembers))
            {
                frameMembers = new HashSet<string>(StringComparer.Ordinal);
                layout.FrameMembersByStage[stage] = frameMembers;
            }
            frameMembers.Add(declaration.Name);

            // Every stage is compiled with the same defines, so the lengths agree;
            // the longest is kept should they not, since each is a prefix.
            if (!layout.FrameMemberDeclaredLengths.TryGetValue(declaration.Name, out int known) ||
                declaration.ArrayLength > known)
            {
                layout.FrameMemberDeclaredLengths[declaration.Name] = declaration.ArrayLength;
            }
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

    /// <summary>
    /// Classifies a sampler under the shared layout. A name and type that match one of
    /// set 0's fixed frame textures read that binding; every other sampler takes the
    /// next push-block slot, in declaration order, and indexes the set 1 array of its
    /// GLSL type. A type set 1 has no array for, a sampler array, or more slots than
    /// the push block holds is a link error.
    /// </summary>
    private static void AddSampler(ProgramInterfaceLayout layout, GlslDeclaration declaration, EnumShaderType stage)
    {
        if (!layout.SamplersByStage.TryGetValue(stage, out HashSet<string>? stageSamplers))
        {
            stageSamplers = new HashSet<string>(StringComparer.Ordinal);
            layout.SamplersByStage[stage] = stageSamplers;
        }
        stageSamplers.Add(declaration.Name);

        if (layout.SamplersByName.TryGetValue(declaration.Name, out SamplerBinding? existing))
        {
            if (!string.Equals(existing.TypeName, declaration.TypeName, StringComparison.Ordinal))
            {
                layout.Errors.Add($"sampler '{declaration.Name}' is declared as '{existing.TypeName}' " +
                                  $"and '{declaration.TypeName}' in different stages");
            }
            return;
        }

        var binding = new SamplerBinding
        {
            Name = declaration.Name,
            TypeName = declaration.TypeName,
            Order = layout.Samplers.Count,
        };
        layout.Samplers.Add(binding);
        layout.SamplersByName[binding.Name] = binding;

        if (declaration.ArrayLength != 0 || declaration.UnresolvedArraySize != null)
        {
            layout.Errors.Add($"sampler '{declaration.Name}' is an array, which the shared layout's push slots cannot index");
            return;
        }

        foreach (SetConvention.Binding frame in SetConvention.FrameTextures)
        {
            if (string.Equals(frame.Name, declaration.Name, StringComparison.Ordinal) &&
                string.Equals(frame.GlslType, declaration.TypeName, StringComparison.Ordinal))
            {
                binding.FrameBinding = frame.Value;
                layout.UsesFrameTextures = true;
                return;
            }
        }

        if (!BindlessKinds.TryFromGlslType(declaration.TypeName, out TextureKind kind))
        {
            layout.Errors.Add($"sampler '{declaration.Name}' has type '{declaration.TypeName}', " +
                              "for which set 1 has no bindless array");
            return;
        }

        binding.Kind = kind;
        binding.PushOffset = layout.PushConstantSize;
        layout.PushConstantSize += SlotBytes;
        if (layout.PushConstantSize > SetConvention.PushConstantBytes)
        {
            layout.Errors.Add($"sampler '{declaration.Name}' needs push byte {layout.PushConstantSize}, " +
                              $"past the {SetConvention.PushConstantBytes} the shared layout holds");
        }
    }

    /// <summary>
    /// Gives a named block its set 2 binding. The game's <c>Animation</c> and
    /// <c>AnimationPrev</c> blocks take the convention's animation bindings, the first
    /// storage block takes FaceData's, and every other block takes the next binding of
    /// the named-block range in declaration order. A binding the shader stated is not
    /// kept: chunkopaque.vsh's <c>binding = 3</c> is the record's binding under the
    /// shared layout, and the mesh path binds FaceData by the convention's number.
    /// </summary>
    private static void AddBlock(
        ProgramInterfaceLayout layout, List<BlockBinding> blocks, GlslDeclaration declaration, ref int nextNamedBinding)
    {
        foreach (BlockBinding existing in blocks)
        {
            if (existing.BlockName == declaration.Name) return;
        }

        int binding;
        bool storage = declaration.Kind == GlslDeclarationKind.StorageBlock;
        if (!storage && declaration.Name == "Animation" && !HasBinding(layout, SetConvention.AnimationBinding))
        {
            binding = SetConvention.AnimationBinding;
        }
        else if (!storage && declaration.Name == "AnimationPrev" && !HasBinding(layout, SetConvention.AnimationPrevBinding))
        {
            binding = SetConvention.AnimationPrevBinding;
        }
        else if (storage && !HasBinding(layout, SetConvention.FaceDataBinding))
        {
            binding = SetConvention.FaceDataBinding;
        }
        else if (nextNamedBinding <= SetConvention.NamedBlockLastBinding)
        {
            binding = nextNamedBinding++;
        }
        else
        {
            layout.Errors.Add($"block '{declaration.Name}' does not fit set 2: the shared layout holds " +
                              $"{SetConvention.NamedBlockLastBinding - SetConvention.NamedBlockFirstBinding + 1} named blocks");
            return;
        }

        blocks.Add(new BlockBinding { BlockName = declaration.Name, Binding = binding });
    }

    private static bool HasBinding(ProgramInterfaceLayout layout, int binding)
    {
        foreach (BlockBinding block in layout.UniformBlocks) if (block.Binding == binding) return true;
        foreach (BlockBinding block in layout.StorageBlocks) if (block.Binding == binding) return true;
        return false;
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
        // Declared outputs that the body never assigns (a G-buffer output kept
        // under an #if that compiled out its store) are still declared; only
        // outputs with a store count as written.
        var fragmentOutputDeclarations = new List<GlslDeclaration>();
        string fragmentSource = "";

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
                    fragmentOutputDeclarations.Add(declaration);
                    fragmentSource = parsed.Source;
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
                    fragmentOutputDeclarations.Add(declaration);
                    fragmentSource = parsed.Source;
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

        foreach (GlslDeclaration declaration in fragmentOutputDeclarations)
        {
            int location = declaration.Location >= 0
                ? declaration.Location
                : layout.FragmentOutputLocations.TryGetValue(declaration.Name, out int assignedLocation) ? assignedLocation : -1;
            if (location < 0) continue;
            if (!TryGetWrittenFragmentOutputElements(fragmentSource, declaration.Name, out HashSet<int>? writtenElements))
            {
                continue;
            }
            int span = LocationSpan(declaration);
            // An index on a non-array output selects a component (or a matrix
            // column), not an attachment, so it still writes the whole span.
            if (writtenElements == null || declaration.ArrayLength == 0)
            {
                for (int i = 0; i < span; i++) layout.WrittenFragmentOutputs.Add(location + i);
                continue;
            }

            // Only some elements of an output array are stored to. Marking the
            // whole span written would leave colour writes on for attachments
            // the shader never touches, and Vulkan then writes undefined data
            // into them (GL would have preserved the attachment).
            int perElement = span / Math.Max(declaration.ArrayLength == 0 ? 1 : declaration.ArrayLength, 1);
            perElement = Math.Max(perElement, 1);
            foreach (int element in writtenElements)
            {
                for (int i = 0; i < perElement; i++)
                {
                    int slot = location + element * perElement + i;
                    if (slot < location + span) layout.WrittenFragmentOutputs.Add(slot);
                }
            }
        }
    }

    /// <summary>
    /// Whether the fragment body stores to <paramref name="name" />: a plain,
    /// swizzled or indexed assignment, or a compound one. Declarations are
    /// excluded by requiring the identifier not to be preceded by a type or
    /// the "out" keyword on the same statement.
    /// </summary>
    internal static bool FragmentOutputIsAssigned(string source, string name)
        => TryGetWrittenFragmentOutputElements(source, name, out _);

    /// <summary>
    /// Which elements of a fragment output the body stores to.
    /// Returns false when nothing stores to it at all. On true,
    /// <paramref name="elements" /> is null when the whole variable is written -
    /// a plain or swizzled store, or an index the parser cannot fold to a
    /// constant - and otherwise holds the constant element indices that are.
    /// </summary>
    internal static bool TryGetWrittenFragmentOutputElements(
        string source, string name, out HashSet<int>? elements)
    {
        elements = null;
        var store = new System.Text.RegularExpressions.Regex(
            @"(?<![\w.])" + System.Text.RegularExpressions.Regex.Escape(name) +
            @"\s*((?:\.[xyzwrgbastpq]+|\[[^\]]*\])*)\s*(=(?!=)|\+=|-=|\*=|/=)");
        bool assigned = false;
        var indices = new HashSet<int>();
        foreach (System.Text.RegularExpressions.Match match in store.Matches(source))
        {
            int lineStart = source.LastIndexOf('\n', Math.Max(match.Index - 1, 0)) + 1;
            string before = source.Substring(lineStart, match.Index - lineStart);
            if (System.Text.RegularExpressions.Regex.IsMatch(before, @"\bout\b|\bin\b|\buniform\b")) continue;

            assigned = true;
            string suffix = match.Groups[1].Value;
            if (!suffix.StartsWith("[", StringComparison.Ordinal))
            {
                // Whole variable or a swizzle of it: everything is written.
                elements = null;
                return true;
            }

            int close = suffix.IndexOf(']');
            string index = close < 0 ? "" : suffix.Substring(1, close - 1).Trim();
            if (!int.TryParse(index, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out int element)
                || element < 0)
            {
                // Dynamic index: assume every element can be written.
                elements = null;
                return true;
            }
            indices.Add(element);
        }

        if (!assigned) return false;
        elements = indices;
        return true;
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

    private static int Align(int value, int alignment) =>
        alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;
}
