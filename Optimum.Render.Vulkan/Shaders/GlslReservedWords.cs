using System;
using System.Collections.Generic;
using System.Text;

namespace Optimum.Render.Vulkan.Shaders;

/// <summary>
/// Renames identifiers that GLSL 330 allows but GLSL 450 reserves.
///
/// Vulkan requires <c>#version 450</c>, and the language gained keywords between
/// the two versions. A shader that used one of them as an ordinary variable name
/// compiled fine against 330 and becomes a syntax error at 450 -
/// <c>ssao.fsh</c> has a local called <c>sample</c>, which 4.00 turned into an
/// interpolation qualifier.
///
/// The rename is a token-level substitution, so it catches declarations and uses
/// alike without needing to understand the code around them.
/// </summary>
internal static class GlslReservedWords
{
    private const string Prefix = "_optimum_kw_";

    /// <summary>
    /// Words reserved by 4.x that a 330 shader could legitimately have used as a
    /// name.
    ///
    /// <c>buffer</c> and <c>shared</c> are deliberately absent. Both became
    /// storage qualifiers in 4.30 and both are used as qualifiers by these
    /// shaders - chunkopaque.vsh declares <c>readonly buffer faceDataBuf</c> -
    /// so renaming them would break the declaration this backend depends on. A
    /// 330 shader using either as a variable name is possible in principle and
    /// would fail to compile with a clear message; that is the better trade.
    /// </summary>
    private static readonly string[] Reserved =
    {
        "sample", "patch", "subroutine", "precise",
        "resource", "filter", "active", "common", "partition", "superp",
        "input", "output",
    };

    /// <summary>
    /// Built-ins GL and Vulkan spell differently.
    ///
    /// The values also differ in principle - <c>gl_VertexIndex</c> counts from
    /// the draw's vertex offset and <c>gl_InstanceIndex</c> from its first
    /// instance, where the GL originals count from zero - but this client issues
    /// no draw with a non-zero base vertex or first instance, so the two agree
    /// everywhere they are used.
    /// </summary>
    private static readonly (string From, string To)[] BuiltinRenames =
    {
        ("gl_VertexID", "gl_VertexIndex"),
        ("gl_InstanceID", "gl_InstanceIndex"),
    };

    private static readonly Dictionary<string, string> Renames = BuildRenames();

    private static Dictionary<string, string> BuildRenames()
    {
        var renames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string word in Reserved) renames[word] = Prefix + word;
        foreach ((string from, string to) in BuiltinRenames) renames[from] = to;
        return renames;
    }

    /// <summary>Longest key, used to skip identifiers that cannot match.</summary>
    private static readonly int LongestRename = MaxKeyLength();

    private static int MaxKeyLength()
    {
        int longest = 0;
        foreach (string key in Renames.Keys) longest = Math.Max(longest, key.Length);
        return longest;
    }

    /// <summary>
    /// Returns the source with reserved identifiers renamed, or the original
    /// string when nothing needed changing.
    /// </summary>
    public static string Rename(string source)
    {
        if (string.IsNullOrEmpty(source)) return source;

        StringBuilder? builder = null;
        int copiedTo = 0;
        int position = 0;
        int length = source.Length;

        while (position < length)
        {
            char c = source[position];

            // Line comments and block comments are gone after preprocessing, but
            // this class is cheap to make safe against raw source too.
            if (c == '/' && position + 1 < length)
            {
                if (source[position + 1] == '/')
                {
                    while (position < length && source[position] != '\n') position++;
                    continue;
                }
                if (source[position + 1] == '*')
                {
                    int end = source.IndexOf("*/", position + 2, StringComparison.Ordinal);
                    position = end < 0 ? length : end + 2;
                    continue;
                }
            }

            if (!IsIdentifierStart(c))
            {
                position++;
                continue;
            }

            int start = position;
            while (position < length && IsIdentifierPart(source[position])) position++;

            // A word preceded by '.' is a struct field or a swizzle, never a
            // declaration, and renaming it would break the member it names.
            if (IsMemberAccess(source, start))
            {
                continue;
            }

            int wordLength = position - start;
            if (wordLength > LongestRename) continue;

            string word = source.Substring(start, wordLength);
            if (!Renames.TryGetValue(word, out string? replacement)) continue;

            builder ??= new StringBuilder(length + 64);
            builder.Append(source, copiedTo, start - copiedTo);
            builder.Append(replacement);
            copiedTo = position;
        }

        if (builder == null) return source;

        builder.Append(source, copiedTo, length - copiedTo);
        return builder.ToString();
    }

    private static bool IsMemberAccess(string source, int identifierStart)
    {
        int i = identifierStart - 1;
        while (i >= 0 && (source[i] == ' ' || source[i] == '\t')) i--;
        return i >= 0 && source[i] == '.';
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';
    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';
}
