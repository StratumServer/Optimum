using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Optimum.Render.Vulkan.Shaders;

internal enum GlslDeclarationKind
{
    /// <summary>A loose <c>uniform float x;</c> - GL's default uniform block.</summary>
    DefaultUniform,
    /// <summary>A sampler or image handle, which becomes a descriptor.</summary>
    OpaqueUniform,
    /// <summary>An explicit <c>layout(std140) uniform Block { ... };</c>.</summary>
    UniformBlock,
    /// <summary>An explicit <c>layout(std430) buffer Block { ... };</c>.</summary>
    StorageBlock,
    Input,
    Output,
    /// <summary>Anything the parser deliberately does not touch.</summary>
    Other
}

/// <summary>One top-level declaration, with the source span it occupies.</summary>
internal sealed class GlslDeclaration
{
    public GlslDeclarationKind Kind;
    public int Start;
    public int Length;

    public string TypeName = "";
    public string Name = "";

    /// <summary>0 when the declaration is not an array.</summary>
    public int ArrayLength;

    /// <summary>The array size as written, when it did not parse as an integer.</summary>
    public string? UnresolvedArraySize;

    /// <summary>Right-hand side of a default value, or null. GL allows these on
    /// uniforms and several shaders rely on them (final.fsh's extraGamma).</summary>
    public string? Initializer;

    /// <summary>Contents of <c>layout(...)</c> as written, or null.</summary>
    public string? LayoutQualifiers;

    /// <summary>
    /// Absolute span of the <c>layout(...)</c> clause. When the declaration has
    /// none, this is a zero-length span at the point one would be inserted, so
    /// the rewriter can treat "replace" and "add" as the same operation.
    /// </summary>
    public int LayoutStart;
    public int LayoutLength;

    /// <summary>Explicit location from a layout qualifier, else -1.</summary>
    public int Location = -1;

    /// <summary>Interpolation and auxiliary qualifiers preceding the type.</summary>
    public string Qualifiers = "";

    public int End => Start + Length;
}

/// <summary>The parse of one shader stage.</summary>
internal sealed class ParsedShader
{
    public string Source = "";
    public List<GlslDeclaration> Declarations = new();

    /// <summary>Span of the <c>#version</c> line, or (-1, 0) when absent.</summary>
    public int VersionStart = -1;
    public int VersionLength;
    public int VersionNumber;

    /// <summary>Spans of every <c>#extension</c> line.</summary>
    public List<(int Start, int Length)> ExtensionDirectives = new();

    /// <summary>Span of the identifier <c>main</c> in its function signature.</summary>
    public int MainNameStart = -1;

    public bool HasMain => MainNameStart >= 0;
}

/// <summary>
/// A deliberately shallow GLSL reader.
///
/// It understands top-level declarations and nothing else: it tracks brace depth,
/// skips comments and strings, and classifies each statement at depth zero. It
/// never builds an expression tree and never looks inside a function body.
///
/// That shallowness is the point. The rewriter edits spans of the original source
/// rather than regenerating it, so any construct this parser does not recognise -
/// including whatever a mod author writes - survives verbatim. The failure mode
/// is "left alone", not "mangled".
/// </summary>
internal static class GlslParser
{
    public static ParsedShader Parse(string source)
    {
        var result = new ParsedShader { Source = source };
        int position = 0;
        int length = source.Length;

        while (position < length)
        {
            position = SkipTrivia(source, position);
            if (position >= length) break;

            if (source[position] == '#')
            {
                position = ReadDirective(source, position, result);
                continue;
            }

            int statementStart = position;
            position = ReadTopLevelStatement(source, position, out bool hadBraceBlock, out int mainNameStart);

            if (mainNameStart >= 0 && result.MainNameStart < 0)
            {
                result.MainNameStart = mainNameStart;
            }

            if (position > statementStart)
            {
                GlslDeclaration? declaration =
                    Classify(source, statementStart, position - statementStart, hadBraceBlock);
                if (declaration != null)
                {
                    result.Declarations.Add(declaration);
                }
            }
            else
            {
                // Defensive: never spin on an unexpected character.
                position = statementStart + 1;
            }
        }

        return result;
    }

    // ------------------------------------------------------------------ scanning

    private static int SkipTrivia(string source, int position)
    {
        int length = source.Length;
        while (position < length)
        {
            char c = source[position];
            if (c == '/' && position + 1 < length)
            {
                if (source[position + 1] == '/')
                {
                    while (position < length && source[position] != '\n') position++;
                    continue;
                }
                if (source[position + 1] == '*')
                {
                    position += 2;
                    while (position + 1 < length && !(source[position] == '*' && source[position + 1] == '/'))
                    {
                        position++;
                    }
                    position = Math.Min(position + 2, length);
                    continue;
                }
            }
            if (!char.IsWhiteSpace(c)) break;
            position++;
        }
        return position;
    }

    /// <summary>Reads a preprocessor line, recording #version and #extension.</summary>
    private static int ReadDirective(string source, int position, ParsedShader result)
    {
        int start = position;
        int length = source.Length;

        // A directive can be continued with a trailing backslash.
        while (position < length)
        {
            if (source[position] == '\\' && position + 1 < length &&
                (source[position + 1] == '\n' || source[position + 1] == '\r'))
            {
                position += 2;
                continue;
            }
            if (source[position] == '\n') break;
            position++;
        }

        string line = source.Substring(start, position - start);
        string trimmed = line.TrimStart('#', ' ', '\t');

        if (trimmed.StartsWith("version", StringComparison.Ordinal))
        {
            result.VersionStart = start;
            result.VersionLength = position - start;
            foreach (string token in trimmed.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int version))
                {
                    result.VersionNumber = version;
                    break;
                }
            }
        }
        else if (trimmed.StartsWith("extension", StringComparison.Ordinal))
        {
            result.ExtensionDirectives.Add((start, position - start));
        }

        return position;
    }

    /// <summary>
    /// Reads one top-level statement. A declaration ends at its semicolon; a
    /// function definition ends at the closing brace of its body. A block
    /// declaration has both - <c>uniform B { ... } name;</c> - and the trailing
    /// semicolon is included.
    /// </summary>
    private static int ReadTopLevelStatement(string source, int position, out bool hadBraceBlock, out int mainNameStart)
    {
        int length = source.Length;
        int depth = 0;
        hadBraceBlock = false;
        mainNameStart = -1;

        int lastIdentifierStart = -1;
        int lastIdentifierLength = 0;

        while (position < length)
        {
            position = SkipTrivia(source, position);
            if (position >= length) break;

            char c = source[position];

            // Read a whole identifier in one go. Accumulating character by
            // character across the trivia skip would run "void main" together
            // into a single nine-character token and lose the function name.
            if (IsIdentifierStart(c))
            {
                int identifierStart = position;
                while (position < length && IsIdentifierPart(source[position])) position++;
                lastIdentifierStart = identifierStart;
                lastIdentifierLength = position - identifierStart;
                continue;
            }

            if (c == '{')
            {
                // The identifier immediately before a top-level '(' ... '{' is the
                // function name. Only "main" matters, and only at depth 0.
                if (depth == 0)
                {
                    hadBraceBlock = true;
                }
                depth++;
                position++;
                continue;
            }

            if (c == '}')
            {
                depth--;
                position++;
                if (depth <= 0)
                {
                    int after = SkipTrivia(source, position);
                    if (after < length && source[after] == ';')
                    {
                        return after + 1;
                    }
                    return position;
                }
                continue;
            }

            if (c == '(' && depth == 0 && lastIdentifierLength == 4 &&
                string.CompareOrdinal(source, lastIdentifierStart, "main", 0, 4) == 0)
            {
                mainNameStart = lastIdentifierStart;
            }

            if (c == ';' && depth == 0)
            {
                return position + 1;
            }

            position++;
        }

        return position;
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    // ---------------------------------------------------------------- classifying

    /// <summary>
    /// Qualifiers that may sit between a layout clause and the storage keyword.
    /// The parser steps over them to reach the part it cares about.
    ///
    /// The memory qualifiers matter as much as the interpolation ones: the chunk
    /// shaders declare <c>readonly buffer faceDataBuf</c>, and failing to step
    /// over <c>readonly</c> leaves the storage block unrecognised, which lands it
    /// in the wrong descriptor set.
    /// </summary>
    private static readonly string[] SkippableQualifiers =
    {
        "flat", "smooth", "noperspective", "centroid", "sample", "invariant", "precise",
        "highp", "mediump", "lowp",
        "readonly", "writeonly", "coherent", "volatile", "restrict",
    };

    private static GlslDeclaration? Classify(string source, int start, int length, bool hadBraceBlock)
    {
        string text = source.Substring(start, length);
        string stripped = StripComments(text);

        var declaration = new GlslDeclaration { Start = start, Length = length };

        int cursor = 0;
        // Default the insertion point to the head of the declaration, so a
        // declaration with no layout clause still has a valid place to gain one.
        declaration.LayoutStart = start;
        declaration.LayoutLength = 0;

        ReadLayoutInto(stripped, ref cursor, declaration, start);

        var qualifiers = new List<string>();
        string? storage = null;

        while (true)
        {
            int save = cursor;
            string? word = ReadIdentifier(stripped, ref cursor);
            if (word == null) { cursor = save; break; }

            if (word == "uniform" || word == "buffer" || word == "in" || word == "out" ||
                word == "attribute" || word == "varying" || word == "shared")
            {
                storage = word;
                break;
            }

            if (Array.IndexOf(SkippableQualifiers, word) >= 0)
            {
                qualifiers.Add(word);
                continue;
            }

            if (word == "layout")
            {
                cursor = save;
                ReadLayoutInto(stripped, ref cursor, declaration, start);
                continue;
            }

            // A const, a struct, a function, a plain global: not ours.
            cursor = save;
            break;
        }

        declaration.Qualifiers = string.Join(" ", qualifiers);

        if (storage == null)
        {
            declaration.Kind = GlslDeclarationKind.Other;
            return declaration;
        }

        // An interface block: "uniform Name { ... }" / "buffer Name { ... }".
        if (hadBraceBlock && (storage == "uniform" || storage == "buffer"))
        {
            declaration.Kind = storage == "uniform"
                ? GlslDeclarationKind.UniformBlock
                : GlslDeclarationKind.StorageBlock;
            declaration.Name = ReadIdentifier(stripped, ref cursor) ?? "";
            return declaration;
        }

        if (hadBraceBlock)
        {
            declaration.Kind = GlslDeclarationKind.Other;
            return declaration;
        }

        string? typeName = ReadIdentifier(stripped, ref cursor);
        if (typeName == null)
        {
            declaration.Kind = GlslDeclarationKind.Other;
            return declaration;
        }
        declaration.TypeName = typeName;

        // C-style array-on-the-type: "uniform vec3[64] samples;" (ssao.fsh).
        ReadArraySuffix(stripped, ref cursor, declaration);

        string? name = ReadIdentifier(stripped, ref cursor);
        if (name == null)
        {
            declaration.Kind = GlslDeclarationKind.Other;
            return declaration;
        }
        declaration.Name = name;

        // Array-on-the-name: "uniform vec3 pointLights[100];".
        ReadArraySuffix(stripped, ref cursor, declaration);

        SkipSpace(stripped, ref cursor);
        if (cursor < stripped.Length && stripped[cursor] == '=')
        {
            cursor++;
            int initializerStart = cursor;
            int end = stripped.IndexOf(';', cursor);
            if (end < 0) end = stripped.Length;
            declaration.Initializer = stripped.Substring(initializerStart, end - initializerStart).Trim();
            cursor = end;
        }

        // A comma-separated declaration list ("uniform float a, b;") is legal GLSL
        // but appears nowhere in this game or its shaders. Leaving it alone is
        // safer than half-handling it: it will fail to compile with a clear
        // message rather than silently losing a uniform.
        SkipSpace(stripped, ref cursor);
        if (cursor < stripped.Length && stripped[cursor] == ',')
        {
            declaration.Kind = GlslDeclarationKind.Other;
            return declaration;
        }

        declaration.Kind = storage switch
        {
            "uniform" => GlslType.IsOpaqueTypeName(typeName)
                ? GlslDeclarationKind.OpaqueUniform
                : GlslDeclarationKind.DefaultUniform,
            "in" or "attribute" => GlslDeclarationKind.Input,
            "out" or "varying" => GlslDeclarationKind.Output,
            _ => GlslDeclarationKind.Other,
        };

        return declaration;
    }

    private static void ReadArraySuffix(string text, ref int cursor, GlslDeclaration declaration)
    {
        SkipSpace(text, ref cursor);
        if (cursor >= text.Length || text[cursor] != '[') return;

        int close = text.IndexOf(']', cursor);
        if (close < 0) return;

        string inside = text.Substring(cursor + 1, close - cursor - 1).Trim();
        cursor = close + 1;

        if (TryEvaluateConstantInt(inside, out int size))
        {
            declaration.ArrayLength = size;
        }
        else
        {
            declaration.UnresolvedArraySize = inside;
        }
    }

    /// <summary>
    /// Evaluates an integer constant expression from an array size.
    ///
    /// GLSL permits any constant expression there and the shaders use it -
    /// fogandlight.vsh declares <c>uniform vec4 fogSpheres[3 * 8];</c>. By this
    /// point the preprocessor has already substituted every macro, so what is
    /// left is arithmetic over literals.
    /// </summary>
    internal static bool TryEvaluateConstantInt(string expression, out int value)
    {
        int cursor = 0;
        value = 0;

        if (!TryParseAdditive(expression, ref cursor, out int result)) return false;

        SkipSpace(expression, ref cursor);
        if (cursor != expression.Length) return false;

        value = result;
        return true;
    }

    private static bool TryParseAdditive(string text, ref int cursor, out int value)
    {
        value = 0;
        if (!TryParseMultiplicative(text, ref cursor, out int left)) return false;

        while (true)
        {
            SkipSpace(text, ref cursor);
            if (cursor >= text.Length) break;

            char op = text[cursor];
            if (op != '+' && op != '-') break;

            cursor++;
            if (!TryParseMultiplicative(text, ref cursor, out int right)) return false;
            left = op == '+' ? left + right : left - right;
        }

        value = left;
        return true;
    }

    private static bool TryParseMultiplicative(string text, ref int cursor, out int value)
    {
        value = 0;
        if (!TryParseUnary(text, ref cursor, out int left)) return false;

        while (true)
        {
            SkipSpace(text, ref cursor);
            if (cursor >= text.Length) break;

            char op = text[cursor];
            if (op != '*' && op != '/' && op != '%') break;

            cursor++;
            if (!TryParseUnary(text, ref cursor, out int right)) return false;
            if (op != '*' && right == 0) return false;

            left = op switch
            {
                '*' => left * right,
                '/' => left / right,
                _ => left % right,
            };
        }

        value = left;
        return true;
    }

    private static bool TryParseUnary(string text, ref int cursor, out int value)
    {
        value = 0;
        SkipSpace(text, ref cursor);
        if (cursor >= text.Length) return false;

        char c = text[cursor];
        if (c == '+' || c == '-')
        {
            cursor++;
            if (!TryParseUnary(text, ref cursor, out int inner)) return false;
            value = c == '-' ? -inner : inner;
            return true;
        }

        if (c == '(')
        {
            cursor++;
            if (!TryParseAdditive(text, ref cursor, out int inner)) return false;
            SkipSpace(text, ref cursor);
            if (cursor >= text.Length || text[cursor] != ')') return false;
            cursor++;
            value = inner;
            return true;
        }

        if (!char.IsDigit(c)) return false;

        int start = cursor;
        while (cursor < text.Length && char.IsDigit(text[cursor])) cursor++;

        // A trailing 'u' suffix is legal on an integer literal.
        if (cursor < text.Length && (text[cursor] == 'u' || text[cursor] == 'U')) cursor++;

        return int.TryParse(
            text.AsSpan(start, cursor - start).TrimEnd('u').TrimEnd('U'),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>
    /// Reads a <c>layout(...)</c> clause if one is present and records both its
    /// contents and its absolute span on the declaration.
    /// </summary>
    private static void ReadLayoutInto(string text, ref int cursor, GlslDeclaration declaration, int declarationStart)
    {
        SkipSpace(text, ref cursor);
        int clauseStart = cursor;

        string? qualifiers = ReadLayoutQualifier(text, ref cursor);
        if (qualifiers == null) return;

        declaration.LayoutQualifiers = qualifiers;
        declaration.LayoutStart = declarationStart + clauseStart;
        declaration.LayoutLength = cursor - clauseStart;
        declaration.Location = ReadLocation(qualifiers);
    }

    private static string? ReadLayoutQualifier(string text, ref int cursor)
    {
        int save = cursor;
        SkipSpace(text, ref cursor);
        if (!MatchWord(text, ref cursor, "layout")) { cursor = save; return null; }

        SkipSpace(text, ref cursor);
        if (cursor >= text.Length || text[cursor] != '(') { cursor = save; return null; }

        int depth = 0;
        int start = cursor + 1;
        while (cursor < text.Length)
        {
            if (text[cursor] == '(') depth++;
            else if (text[cursor] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    string inside = text.Substring(start, cursor - start);
                    cursor++;
                    return inside;
                }
            }
            cursor++;
        }

        cursor = save;
        return null;
    }

    private static int ReadLocation(string qualifiers)
    {
        foreach (string part in qualifiers.Split(','))
        {
            int equals = part.IndexOf('=');
            if (equals < 0) continue;
            if (part.AsSpan(0, equals).Trim().SequenceEqual("location") &&
                int.TryParse(part.AsSpan(equals + 1).Trim(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out int location))
            {
                return location;
            }
        }
        return -1;
    }

    private static bool MatchWord(string text, ref int cursor, string word)
    {
        if (cursor + word.Length > text.Length) return false;
        if (string.CompareOrdinal(text, cursor, word, 0, word.Length) != 0) return false;
        int after = cursor + word.Length;
        if (after < text.Length && (char.IsLetterOrDigit(text[after]) || text[after] == '_')) return false;
        cursor = after;
        return true;
    }

    private static string? ReadIdentifier(string text, ref int cursor)
    {
        SkipSpace(text, ref cursor);
        if (cursor >= text.Length || !IsIdentifierStart(text[cursor])) return null;

        int start = cursor;
        while (cursor < text.Length && (char.IsLetterOrDigit(text[cursor]) || text[cursor] == '_')) cursor++;
        return text.Substring(start, cursor - start);
    }

    private static void SkipSpace(string text, ref int cursor)
    {
        while (cursor < text.Length && char.IsWhiteSpace(text[cursor])) cursor++;
    }

    /// <summary>
    /// Blanks comments while preserving offsets, so spans stay valid. The
    /// production path sees preprocessed source with comments already gone; this
    /// keeps the parser usable on raw source in tests and on mod shaders that
    /// reach it by another route.
    /// </summary>
    internal static string StripComments(string text)
    {
        var builder = new StringBuilder(text);
        int i = 0;
        while (i < text.Length)
        {
            if (text[i] == '/' && i + 1 < text.Length)
            {
                if (text[i + 1] == '/')
                {
                    while (i < text.Length && text[i] != '\n') builder[i++] = ' ';
                    continue;
                }
                if (text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    end = end < 0 ? text.Length : end + 2;
                    while (i < end)
                    {
                        if (text[i] != '\n') builder[i] = ' ';
                        i++;
                    }
                    continue;
                }
            }
            i++;
        }
        return builder.ToString();
    }
}
