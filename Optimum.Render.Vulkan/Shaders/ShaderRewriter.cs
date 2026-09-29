using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Vintagestory.API.Client;

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>The result of rewriting one stage for Vulkan.</summary>
internal sealed class RewrittenShader
{
    public string Code = "";
    public List<string> Errors { get; } = new();
    public bool HasErrors => Errors.Count > 0;
}

/// <summary>
/// Turns a stage of GLSL 330 into GLSL 450 that glslang will accept for Vulkan.
///
/// It works by editing spans of the original source rather than regenerating it.
/// Anything the parser did not classify is copied through byte for byte, so a
/// construct this backend has never seen - in a mod shader, say - survives intact
/// instead of being mangled. The failure mode is a shader that still says what it
/// said before.
///
/// Five things actually change:
///
/// The version becomes 450 and the original <c>#extension</c> lines are dropped,
/// since they name GL extensions that either do not exist or are already core in
/// Vulkan GLSL.
///
/// Loose uniforms move into the program record, set 2's dynamic uniform buffer
/// (GL's default uniform block has no Vulkan equivalent, and this game declares
/// 488 of them), or into the shared frame block at set 0.
///
/// Every program targets the one shared pipeline layout (plan decision 9,
/// <see cref="SetConvention" />). A sampler named and typed like one of set 0's
/// frame textures reads that binding. Every other sampler becomes a slot index in
/// the push block under its own name, and every reference to it in the body - a
/// sampling call or an argument to a function - reads
/// <c>optimumTextures&lt;Kind&gt;[name]</c> from set 1. Named uniform blocks become
/// <c>layout(std140) readonly buffer</c> blocks in set 2, so the client's std140
/// bytes are read unchanged; storage blocks move to set 2 at the convention's
/// bindings.
///
/// Vertex inputs, varyings and fragment outputs gain the explicit locations
/// SPIR-V requires and GLSL 330 left implicit.
///
/// The last stage before rasterisation gains a wrapper around <c>main</c> that
/// remaps clip depth from GL's [-w, w] to Vulkan's [0, w]. Nothing else about the
/// coordinate system is touched: no Y flip, no matrix rewriting. GL and Vulkan
/// agree on the relationship between clip space, framebuffer memory and texture
/// coordinates; they disagree only on what to call the origin, and on depth.
/// </summary>
internal static class ShaderRewriter
{
    private const string MainReplacementName = "_optimum_main";

    private readonly record struct Edit(int Start, int Length, string Replacement);

    public static RewrittenShader Rewrite(
        ParsedShader parsed,
        ProgramInterfaceLayout layout,
        EnumShaderType stage,
        bool emitDepthRemap)
    {
        var result = new RewrittenShader();
        string source = parsed.Source;
        var edits = new List<Edit>();

        AddHeaderEdits(parsed, layout, stage, emitDepthRemap, edits);

        foreach (GlslDeclaration declaration in parsed.Declarations)
        {
            switch (declaration.Kind)
            {
                case GlslDeclarationKind.DefaultUniform:
                    // Its storage now lives in the generated block or the shared
                    // frame block. Members keep their names in both, so every use
                    // site still compiles.
                    if (layout.MembersByName.ContainsKey(declaration.Name) ||
                        layout.FrameMemberDeclaredLengths.ContainsKey(declaration.Name))
                    {
                        edits.Add(new Edit(declaration.Start, declaration.Length, ""));
                    }
                    break;

                case GlslDeclarationKind.OpaqueUniform:
                    if (layout.SamplersByName.TryGetValue(declaration.Name, out SamplerBinding? sampler))
                    {
                        if (sampler.IsFrameTexture)
                        {
                            edits.Add(LayoutEdit(declaration, new (string, string?)[]
                            {
                                ("set", Number(SetConvention.FrameSet)),
                                ("binding", Number(sampler.FrameBinding)),
                            }));
                        }
                        else
                        {
                            // The name is now the slot index in the push block.
                            edits.Add(new Edit(declaration.Start, declaration.Length, ""));
                        }
                    }
                    break;

                case GlslDeclarationKind.UniformBlock:
                    AddBlockEdit(layout.UniformBlocks, declaration, edits, asStorage: true);
                    break;

                case GlslDeclarationKind.StorageBlock:
                    AddBlockEdit(layout.StorageBlocks, declaration, edits, asStorage: false);
                    break;

                case GlslDeclarationKind.Input:
                case GlslDeclarationKind.Output:
                    AddLocationEdit(layout, declaration, stage, edits);
                    break;
            }
        }

        AddSamplerReferenceEdits(parsed, layout, stage, edits);

        if (emitDepthRemap)
        {
            AddDepthRemapEdits(parsed, stage, edits, result);
        }

        result.Code = ApplyEdits(source, edits);
        return result;
    }

    // -------------------------------------------------------------------- header

    private static void AddHeaderEdits(
        ParsedShader parsed, ProgramInterfaceLayout layout, EnumShaderType stage, bool emitDepthRemap,
        List<Edit> edits)
    {
        string frameBlock = BuildFrameBlock(layout, stage);
        string block = BuildUniformBlock(layout, stage);
        string push = BuildPushBlock(layout, stage);
        string arrays = BuildTextureArrays(layout, stage);

        var header = new StringBuilder();
        header.Append("#version 450\n");
        if (frameBlock.Length > 0 || block.Length > 0 || push.Length > 0)
        {
            header.Append("#extension GL_EXT_scalar_block_layout : require\n");
        }
        if (arrays.Length > 0)
        {
            header.Append("#extension GL_EXT_nonuniform_qualifier : require\n");
        }
        header.Append(frameBlock);
        header.Append(block);
        header.Append(push);
        header.Append(arrays);

        // The geometry stage's EmitVertex() replacement lives in the header so
        // it precedes every function that may call it.
        if (emitDepthRemap && stage == EnumShaderType.GeometryShader)
        {
            header.Append("void " + EmitVertexReplacementName + "() { " + DepthRemapStatement + " EmitVertex(); }\n");
        }

        if (parsed.VersionStart >= 0)
        {
            edits.Add(new Edit(parsed.VersionStart, parsed.VersionLength, header.ToString().TrimEnd('\n')));
        }
        else
        {
            edits.Add(new Edit(0, 0, header.ToString()));
        }

        // The originals name GL extensions - GL_ARB_explicit_attrib_location and
        // friends - that Vulkan GLSL either lacks or already includes.
        foreach ((int start, int length) in parsed.ExtensionDirectives)
        {
            edits.Add(new Edit(start, length, ""));
        }
    }

    /// <summary>
    /// Emits the shared frame block (<see cref="FrameGlobals" />) with the members
    /// this stage reads, at the offsets every program agrees on, each with the array
    /// length this program declared. Anonymous like the program's own block, so
    /// every reference in the body resolves unchanged.
    /// </summary>
    private static string BuildFrameBlock(ProgramInterfaceLayout layout, EnumShaderType stage)
    {
        if (!layout.FrameMembersByStage.TryGetValue(stage, out HashSet<string>? stageMembers)) return "";
        if (stageMembers.Count == 0) return "";

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"\nlayout(scalar, set = {FrameGlobals.Set}");
        builder.Append(CultureInfo.InvariantCulture, $", binding = {FrameGlobals.Binding}) uniform ");
        builder.Append(FrameGlobals.BlockTypeName);
        builder.Append("\n{\n");

        foreach (UniformMember member in FrameGlobals.Members)
        {
            if (!stageMembers.Contains(member.Name)) continue;

            builder.Append(CultureInfo.InvariantCulture, $"    layout(offset = {member.Offset}) ");
            builder.Append(member.Type.Name).Append(' ').Append(member.Name);
            int length = layout.FrameMemberDeclaredLengths[member.Name];
            if (length > 0)
            {
                builder.Append('[').Append(length.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            builder.Append(";\n");
        }

        builder.Append("};\n");
        return builder.ToString();
    }

    /// <summary>
    /// Emits the program record - the block that replaces GL's default uniform
    /// block, at set 2's record binding - carrying only the members this stage declared.
    ///
    /// Members keep their original names and the block is anonymous, so every
    /// reference in the shader body resolves unchanged. Each member states its
    /// offset explicitly, which is what lets a stage declare a subset without
    /// disturbing the shared layout the CPU writes into - and what avoids
    /// redefining a name that is a varying in the other stage.
    /// </summary>
    private static string BuildUniformBlock(ProgramInterfaceLayout layout, EnumShaderType stage)
    {
        if (!layout.HasUniformBlock) return "";
        if (!layout.MembersByStage.TryGetValue(stage, out HashSet<string>? stageMembers)) return "";
        if (stageMembers.Count == 0) return "";

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"\nlayout(scalar, set = {SetConvention.StorageSet}");
        builder.Append(CultureInfo.InvariantCulture, $", binding = {SetConvention.ProgramRecordBinding}) uniform ");
        builder.Append(ProgramInterfaceLayout.BlockTypeName);
        builder.Append("\n{\n");

        foreach (UniformMember member in layout.Members)
        {
            if (!stageMembers.Contains(member.Name)) continue;

            builder.Append(CultureInfo.InvariantCulture, $"    layout(offset = {member.Offset}) ");
            builder.Append(member.Type.Name).Append(' ').Append(member.Name);
            if (member.ArrayLength > 0)
            {
                builder.Append('[').Append(member.ArrayLength.ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            builder.Append(";\n");
        }

        builder.Append("};\n");
        return builder.ToString();
    }

    /// <summary>
    /// Emits the push block with one slot index per bindless sampler this stage
    /// declared, under the sampler's own name, at the offset the whole program agrees
    /// on. Anonymous, so the rewritten references read the index by that name.
    /// </summary>
    private static string BuildPushBlock(ProgramInterfaceLayout layout, EnumShaderType stage)
    {
        if (!layout.SamplersByStage.TryGetValue(stage, out HashSet<string>? stageSamplers)) return "";

        var builder = new StringBuilder();
        foreach (SamplerBinding sampler in layout.Samplers)
        {
            if (sampler.IsFrameTexture || !stageSamplers.Contains(sampler.Name)) continue;
            if (builder.Length == 0)
            {
                builder.Append("\nlayout(push_constant, scalar) uniform ").Append(ProgramInterfaceLayout.PushBlockTypeName);
                builder.Append("\n{\n");
            }
            // OPTIMUM_SAMPLER_SLOT(<type>, <name>) in bindings.glsl expands to the same declaration.
            builder.Append(CultureInfo.InvariantCulture, $"    layout(offset = {sampler.PushOffset}) uint {sampler.Name};");
            builder.Append(CultureInfo.InvariantCulture, $" // {sampler.TypeName}\n");
        }
        if (builder.Length == 0) return "";
        builder.Append("};\n");
        return builder.ToString();
    }

    /// <summary>The set 1 arrays this stage's bindless samplers index, declared as bindings.glsl declares them.</summary>
    private static string BuildTextureArrays(ProgramInterfaceLayout layout, EnumShaderType stage)
    {
        if (!layout.SamplersByStage.TryGetValue(stage, out HashSet<string>? stageSamplers)) return "";

        var kinds = new SortedSet<int>();
        foreach (SamplerBinding sampler in layout.Samplers)
        {
            if (!sampler.IsFrameTexture && stageSamplers.Contains(sampler.Name)) kinds.Add((int)sampler.Kind);
        }
        var builder = new StringBuilder();
        foreach (int kind in kinds)
        {
            SetConvention.Binding array = SetConvention.TextureArrays[kind];
            builder.Append(CultureInfo.InvariantCulture,
                $"layout(set = {SetConvention.TextureSet}, binding = {array.Value}) uniform {array.GlslType} {array.Name}[];\n");
        }
        return builder.ToString();
    }

    // -------------------------------------------------------------- declarations

    private static readonly string[] MemoryLayouts = { "std140", "std430", "shared", "packed" };

    /// <summary>
    /// Moves a named block to its set 2 binding. A uniform block becomes a
    /// <c>layout(std140) readonly buffer</c>: std140 is what the client's UBO uploads
    /// already stride to, and a storage buffer defaults to std430, so the memory
    /// layout is stated explicitly whatever the shader wrote.
    /// </summary>
    private static void AddBlockEdit(List<BlockBinding> blocks, GlslDeclaration declaration, List<Edit> edits,
        bool asStorage)
    {
        foreach (BlockBinding block in blocks)
        {
            if (block.BlockName != declaration.Name) continue;

            if (asStorage)
            {
                edits.Add(LayoutEdit(declaration, new (string, string?)[]
                {
                    ("std140", null),
                    ("set", Number(block.Set)),
                    ("binding", Number(block.Binding)),
                }, MemoryLayouts));
                if (declaration.StorageKeywordStart >= 0)
                {
                    edits.Add(new Edit(declaration.StorageKeywordStart, "uniform".Length, "readonly buffer"));
                }
                return;
            }

            // A storage block keeps the memory layout it chose (std430 for faceDataBuf).
            edits.Add(LayoutEdit(declaration, new (string, string?)[]
            {
                ("set", Number(block.Set)),
                ("binding", Number(block.Binding)),
            }));
            return;
        }
    }

    /// <summary>
    /// Rewrites every reference to a bindless sampler this stage declared into
    /// <c>optimumTextures&lt;Kind&gt;[name]</c>, where <c>name</c> is now the slot index
    /// in the push block. A sampler can only ever appear as a function argument - to
    /// <c>texture</c>, <c>texelFetch</c>, <c>textureLod</c>, <c>textureGather</c>,
    /// <c>textureSize</c> or to a function of the shader's own such as colormap's
    /// <c>getColorMapped</c> - so every reference is rewritten and no call form is
    /// singled out.
    ///
    /// Not rewritten: the global declarations (they have edits of their own), a field
    /// after a dot, a declaration of the same name (a parameter <c>sampler2D tex</c>, a
    /// local or struct member), and every use inside the scope such a declaration
    /// shadows the global in. Comments and preprocessor lines are skipped.
    /// </summary>
    private static void AddSamplerReferenceEdits(
        ParsedShader parsed, ProgramInterfaceLayout layout, EnumShaderType stage, List<Edit> edits)
    {
        if (!layout.SamplersByStage.TryGetValue(stage, out HashSet<string>? stageSamplers)) return;

        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (SamplerBinding sampler in layout.Samplers)
        {
            if (sampler.IsFrameTexture || !stageSamplers.Contains(sampler.Name)) continue;
            replacements[sampler.Name] = SetConvention.TextureArrays[(int)sampler.Kind].Name + "[" + sampler.Name + "]";
        }
        if (replacements.Count == 0) return;

        var skipped = new List<(int Start, int End)>();
        foreach (GlslDeclaration declaration in parsed.Declarations)
        {
            if (declaration.Kind is GlslDeclarationKind.Other) continue;
            skipped.Add((declaration.Start, declaration.End));
        }
        skipped.Sort(static (a, b) => a.Start.CompareTo(b.Start));

        string source = parsed.Source;
        var scopes = new List<HashSet<string>> { new(StringComparer.Ordinal) };
        HashSet<string>? parameters = null;
        int parenDepth = 0;
        string previous = ";";
        bool previousIsWord = false;
        int skip = 0;
        int i = 0;
        bool lineStart = true;

        while (i < source.Length)
        {
            while (skip < skipped.Count && skipped[skip].End <= i) skip++;
            if (skip < skipped.Count && skipped[skip].Start <= i)
            {
                i = skipped[skip].End;
                previous = ";";
                previousIsWord = false;
                continue;
            }

            char c = source[i];
            if (c == '\n') { lineStart = true; i++; continue; }
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (c == '#' && lineStart)
            {
                while (i < source.Length && source[i] != '\n')
                {
                    if (source[i] == '\\' && i + 1 < source.Length && source[i + 1] == '\n') i++;
                    i++;
                }
                continue;
            }
            lineStart = false;

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                int close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? source.Length : close + 2;
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_')) i++;
                string word = source.Substring(start, i - start);

                if (replacements.TryGetValue(word, out string? replacement) && previous != ".")
                {
                    bool declaration = previousIsWord && previous is not ("return" or "case");
                    if (declaration)
                    {
                        (scopes.Count == 1 && parenDepth > 0 ? parameters ??= new(StringComparer.Ordinal) : scopes[^1])
                            .Add(word);
                    }
                    else if (!Shadowed(scopes, word))
                    {
                        edits.Add(new Edit(start, word.Length, replacement));
                    }
                }

                previous = word;
                previousIsWord = true;
                continue;
            }

            switch (c)
            {
                case '(':
                    if (scopes.Count == 1 && parenDepth == 0) parameters = null;
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0) parenDepth--;
                    break;
                case '{':
                    // A function body sees its parameters; any other brace opens a plain scope.
                    scopes.Add(scopes.Count == 1 && parameters != null ? parameters : new(StringComparer.Ordinal));
                    parameters = null;
                    break;
                case '}':
                    if (scopes.Count > 1) scopes.RemoveAt(scopes.Count - 1);
                    break;
                case ';':
                    if (scopes.Count == 1) parameters = null;
                    break;
            }
            previous = c.ToString();
            previousIsWord = false;
            i++;
        }
    }

    private static bool Shadowed(List<HashSet<string>> scopes, string name)
    {
        // The outermost set only ever holds names declared at global scope inside a
        // struct or prototype parenthesis, which never shadow a use; start above it.
        for (int i = scopes.Count - 1; i >= 1; i--)
        {
            if (scopes[i].Contains(name)) return true;
        }
        return false;
    }

    private static void AddLocationEdit(
        ProgramInterfaceLayout layout, GlslDeclaration declaration, EnumShaderType stage, List<Edit> edits)
    {
        int location;
        if (stage == EnumShaderType.VertexShader && declaration.Kind == GlslDeclarationKind.Input)
        {
            if (!layout.VertexInputLocations.TryGetValue(declaration.Name, out location)) return;
        }
        else if (stage == EnumShaderType.FragmentShader && declaration.Kind == GlslDeclarationKind.Output)
        {
            if (!layout.FragmentOutputLocations.TryGetValue(declaration.Name, out location)) return;
        }
        else
        {
            if (!layout.VaryingLocations.TryGetValue(declaration.Name, out location)) return;
        }

        edits.Add(LayoutEdit(declaration, new (string, string?)[]
        {
            ("location", Number(location)),
        }));
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Produces an edit that replaces the declaration's <c>layout(...)</c> clause
    /// with one carrying the given keys (a null value adds a bare word such as
    /// <c>std140</c>), preserving any others it already had except
    /// <paramref name="removals" />. When there was no clause, the span is empty
    /// and this inserts one.
    /// </summary>
    private static Edit LayoutEdit(GlslDeclaration declaration, (string Key, string? Value)[] additions,
        params string[] removals)
    {
        var parts = new List<string>();
        var overridden = new HashSet<string>(removals, StringComparer.Ordinal);
        foreach ((string key, _) in additions) overridden.Add(key);

        if (declaration.LayoutQualifiers != null)
        {
            foreach (string raw in declaration.LayoutQualifiers.Split(','))
            {
                string part = raw.Trim();
                if (part.Length == 0) continue;

                int equals = part.IndexOf('=');
                string key = (equals < 0 ? part : part[..equals]).Trim();
                if (overridden.Contains(key)) continue;

                parts.Add(part);
            }
        }

        foreach ((string key, string? value) in additions)
        {
            parts.Add(value == null ? key : $"{key} = {value}");
        }

        // A replaced clause keeps the whitespace that followed it; an inserted one brings its own.
        string clause = $"layout({string.Join(", ", parts)})";
        return new Edit(declaration.LayoutStart, declaration.LayoutLength,
            declaration.LayoutLength == 0 ? clause + " " : clause);
    }

    // ---------------------------------------------------------------- depth remap

    /// <summary>
    /// Wraps <c>main</c> so clip-space depth lands in Vulkan's [0, w] range.
    ///
    /// Doing it here rather than by folding a correction into the projection
    /// matrix keeps every matrix in the game untouched - the frustum culler, the
    /// shadow orthographic projections and any matrix a mod builds all keep
    /// working, and the CPU-side code never has to know which backend is running.
    /// </summary>
    private const string DepthRemapStatement = "gl_Position.z = (gl_Position.z + gl_Position.w) * 0.5;";

    private static void AddDepthRemapEdits(
        ParsedShader parsed, EnumShaderType stage, List<Edit> edits, RewrittenShader result)
    {
        if (stage == EnumShaderType.GeometryShader)
        {
            AddGeometryDepthRemapEdits(parsed, edits, result);
            return;
        }

        if (!parsed.HasMain)
        {
            result.Errors.Add("stage has no main() to wrap for the Vulkan depth range");
            return;
        }

        edits.Add(new Edit(parsed.MainNameStart, "main".Length, MainReplacementName));

        edits.Add(new Edit(parsed.Source.Length, 0,
            "\n\nvoid main()\n{\n" +
            "    " + MainReplacementName + "();\n" +
            "    " + DepthRemapStatement + "\n" +
            "}\n"));
    }

    private const string EmitVertexReplacementName = "_optimum_emit_vertex";

    /// <summary>
    /// A geometry stage snapshots <c>gl_Position</c> at every <c>EmitVertex()</c>,
    /// so a wrapper around <c>main</c> would run after every vertex has already
    /// left. Each call is redirected to a helper that remaps and then emits,
    /// which keeps the call a single statement: an unbraced <c>if</c> or loop
    /// body around it keeps its scope, where an inserted extra statement would
    /// not.
    /// </summary>
    private static void AddGeometryDepthRemapEdits(ParsedShader parsed, List<Edit> edits, RewrittenShader result)
    {
        string source = parsed.Source;
        const string call = "EmitVertex";
        int found = 0;

        for (int at = source.IndexOf(call, StringComparison.Ordinal); at >= 0;
             at = source.IndexOf(call, at + call.Length, StringComparison.Ordinal))
        {
            bool startsWord = at == 0 || !(char.IsLetterOrDigit(source[at - 1]) || source[at - 1] == '_');
            int after = at + call.Length;
            while (after < source.Length && char.IsWhiteSpace(source[after])) after++;
            bool isCall = after < source.Length && source[after] == '(';
            if (!startsWord || !isCall) continue;

            edits.Add(new Edit(at, call.Length, EmitVertexReplacementName));
            found++;
        }

        if (found == 0)
        {
            result.Errors.Add("geometry stage never calls EmitVertex(), so no vertex gets the Vulkan depth range");
        }
    }

    // --------------------------------------------------------------------- edits

    /// <summary>
    /// Applies edits back to front so earlier offsets stay valid. Overlapping
    /// edits are a programming error here, not a shader error, so they assert
    /// rather than being silently resolved.
    /// </summary>
    private static string ApplyEdits(string source, List<Edit> edits)
    {
        edits.Sort(static (a, b) => b.Start != a.Start ? b.Start.CompareTo(a.Start) : b.Length.CompareTo(a.Length));

        var builder = new StringBuilder(source);
        int previousStart = int.MaxValue;

        foreach (Edit edit in edits)
        {
            if (edit.Start + edit.Length > previousStart)
            {
                throw new InvalidOperationException(
                    $"overlapping shader edits at {edit.Start}..{edit.Start + edit.Length} and {previousStart}");
            }
            builder.Remove(edit.Start, edit.Length);
            builder.Insert(edit.Start, edit.Replacement);
            previousStart = edit.Start;
        }

        return builder.ToString();
    }
}
