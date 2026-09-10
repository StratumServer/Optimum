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
/// Loose uniforms move into one generated block. GL's default uniform block has
/// no Vulkan equivalent, and this game declares 488 of them.
///
/// Samplers, uniform blocks and storage buffers gain descriptor set and binding
/// numbers, keeping any the shader already stated.
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

        AddHeaderEdits(parsed, layout, stage, edits);

        foreach (GlslDeclaration declaration in parsed.Declarations)
        {
            switch (declaration.Kind)
            {
                case GlslDeclarationKind.DefaultUniform:
                    // Its storage now lives in the generated block. Members keep
                    // their names there, so every use site still compiles.
                    if (layout.MembersByName.ContainsKey(declaration.Name))
                    {
                        edits.Add(new Edit(declaration.Start, declaration.Length, ""));
                    }
                    break;

                case GlslDeclarationKind.OpaqueUniform:
                    if (layout.SamplersByName.TryGetValue(declaration.Name, out SamplerBinding? sampler))
                    {
                        edits.Add(LayoutEdit(declaration, new (string, string)[]
                        {
                            ("set", ProgramInterfaceLayout.SamplerSet.ToString(CultureInfo.InvariantCulture)),
                            ("binding", sampler.Binding.ToString(CultureInfo.InvariantCulture)),
                        }));
                    }
                    break;

                case GlslDeclarationKind.UniformBlock:
                    AddBlockEdit(layout.UniformBlocks, declaration, edits);
                    break;

                case GlslDeclarationKind.StorageBlock:
                    AddBlockEdit(layout.StorageBlocks, declaration, edits);
                    break;

                case GlslDeclarationKind.Input:
                case GlslDeclarationKind.Output:
                    AddLocationEdit(layout, declaration, stage, edits);
                    break;
            }
        }

        if (emitDepthRemap)
        {
            AddDepthRemapEdits(parsed, stage, edits, result);
        }

        result.Code = ApplyEdits(source, edits);
        return result;
    }

    // -------------------------------------------------------------------- header

    private static void AddHeaderEdits(
        ParsedShader parsed, ProgramInterfaceLayout layout, EnumShaderType stage, List<Edit> edits)
    {
        string block = BuildUniformBlock(layout, stage);

        var header = new StringBuilder();
        header.Append("#version 450\n");
        if (block.Length > 0)
        {
            header.Append("#extension GL_EXT_scalar_block_layout : require\n");
        }
        header.Append(block);

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
    /// Emits the block that replaces GL's default uniform block, carrying only
    /// the members this stage declared.
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
        builder.Append(CultureInfo.InvariantCulture, $"\nlayout(scalar, set = {ProgramInterfaceLayout.DefaultBlockSet}");
        builder.Append(CultureInfo.InvariantCulture, $", binding = {ProgramInterfaceLayout.DefaultBlockBinding}) uniform ");
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

    // -------------------------------------------------------------- declarations

    private static void AddBlockEdit(List<BlockBinding> blocks, GlslDeclaration declaration, List<Edit> edits)
    {
        foreach (BlockBinding block in blocks)
        {
            if (block.BlockName != declaration.Name) continue;

            // The memory layout qualifier the shader chose (std140 / std430) is
            // preserved: those blocks are filled by UBO uploads whose striding
            // already matches, and only the default block needs scalar rules.
            edits.Add(LayoutEdit(declaration, new (string, string)[]
            {
                ("set", block.Set.ToString(CultureInfo.InvariantCulture)),
                ("binding", block.Binding.ToString(CultureInfo.InvariantCulture)),
            }));
            return;
        }
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

        edits.Add(LayoutEdit(declaration, new (string, string)[]
        {
            ("location", location.ToString(CultureInfo.InvariantCulture)),
        }));
    }

    /// <summary>
    /// Produces an edit that replaces the declaration's <c>layout(...)</c> clause
    /// with one carrying the given keys, preserving any others it already had.
    /// When there was no clause, the span is empty and this inserts one.
    /// </summary>
    private static Edit LayoutEdit(GlslDeclaration declaration, (string Key, string Value)[] additions)
    {
        var parts = new List<string>();
        var overridden = new HashSet<string>(StringComparer.Ordinal);
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

        foreach ((string key, string value) in additions)
        {
            parts.Add($"{key} = {value}");
        }

        return new Edit(declaration.LayoutStart, declaration.LayoutLength, $"layout({string.Join(", ", parts)}) ");
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

    /// <summary>
    /// A geometry stage snapshots <c>gl_Position</c> at every <c>EmitVertex()</c>,
    /// so a wrapper around <c>main</c> would run after every vertex has already
    /// left. The remap goes immediately before each emit instead.
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

            edits.Add(new Edit(at, 0, DepthRemapStatement + " "));
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
