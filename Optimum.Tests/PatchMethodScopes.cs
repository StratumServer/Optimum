using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Optimum.Tests;

/// <summary>
/// Per-METHOD attribution for unified diffs.
///
/// A patch on its own cannot say which method it changed: git's hunk headers for
/// these C# files name the enclosing TYPE (no diff=csharp driver is configured),
/// and three lines of context rarely reach a signature. So a patch file is
/// always paired with the tree it was applied to - the decompiled donor under
/// <c>.build/runtime-donors/</c>, or the mod fork - and every added line is
/// located inside that text, whose method spans this class parses.
///
/// Both trees are git-ignored, so callers must treat "no source tree" as
/// "cannot check" and fall back to the coarser per-type check rather than
/// failing: a clean clone has neither.
/// </summary>
public static class PatchMethodScopes
{
    public sealed record Scope(string Kind, string Name, int Start, int End);

    /// <summary>
    /// Every brace-delimited scope in <paramref name="source"/>, with the method
    /// ones named. Comments and string/char literals are skipped so a brace
    /// inside them cannot shift the nesting.
    /// </summary>
    public static List<Scope> Parse(string source)
    {
        var scopes = new List<Scope>();
        var stack = new Stack<(string Kind, string Name, int Start)>();
        var header = new StringBuilder();

        int i = 0;
        while (i < source.Length)
        {
            char c = source[i];
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                header.Append(' ');
                continue;
            }
            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                i += 2;
                while (i + 1 < source.Length && !(source[i] == '*' && source[i + 1] == '/')) i++;
                i = Math.Min(i + 2, source.Length);
                header.Append(' ');
                continue;
            }
            if (c == '"' || c == '\'')
            {
                i = SkipLiteral(source, i);
                header.Append(' ');
                continue;
            }
            if (c == '{')
            {
                string parentKind = stack.Count > 0 ? stack.Peek().Kind : "file";
                var scope = Classify(header.ToString(), parentKind);
                header.Clear();
                stack.Push((scope.Kind, scope.Name, i));
                i++;
                continue;
            }
            if (c == '}')
            {
                header.Clear();
                if (stack.Count > 0)
                {
                    var open = stack.Pop();
                    scopes.Add(new Scope(open.Kind, open.Name, open.Start, i));
                }
                i++;
                continue;
            }
            if (c == ';')
            {
                header.Clear();
                i++;
                continue;
            }
            header.Append(c);
            i++;
        }

        return scopes;
    }

    /// <summary>
    /// The innermost method scope containing <paramref name="index"/>, or null
    /// when the offset is not inside one (a field initializer, a property
    /// accessor, a type body).
    /// </summary>
    public static string? MethodAt(IReadOnlyList<Scope> scopes, int index)
    {
        Scope? best = null;
        foreach (var scope in scopes)
        {
            if (scope.Kind != "method" || index < scope.Start || index > scope.End) continue;
            if (best is null || scope.Start > best.Start) best = scope;
        }
        return best?.Name;
    }

    /// <summary>
    /// The distinct method names that the patch's added lines land in, located
    /// by finding each added line's text in <paramref name="source"/>.
    /// </summary>
    public static HashSet<string> MethodsTouched(string patchFile, string source) =>
        MethodsByAddedLine(patchFile, source, _ => true)
            .SelectMany(entry => entry.Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// marker -> the methods that add it, for every added line carrying one of
    /// <paramref name="markers"/>.
    /// </summary>
    public static Dictionary<string, HashSet<string>> MarkersByMethod(
        string patchFile, string source, IReadOnlyList<string> markers)
    {
        var byMethod = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (line, methods) in MethodsByAddedLine(patchFile, source, _ => true))
        {
            foreach (string marker in markers)
            {
                if (!line.Contains(marker, StringComparison.Ordinal)) continue;
                foreach (string method in methods)
                {
                    if (!byMethod.TryGetValue(method, out var set))
                    {
                        byMethod[method] = set = new HashSet<string>(StringComparer.Ordinal);
                    }
                    set.Add(marker);
                }
            }
        }
        return byMethod;
    }

    /// <summary>
    /// The donor decompile for a type, or null when the donor tree has not been
    /// prepared (scripts/prepare-runtime-donors.sh) in this checkout.
    /// </summary>
    public static string? FindDonorSource(string repoRoot, string project, string typeFullName)
    {
        string root = Path.Combine(repoRoot, ".build", "runtime-donors", project);
        if (!Directory.Exists(root)) return null;
        string direct = Path.Combine(root, Path.Combine(typeFullName.Split('.')) + ".cs");
        if (File.Exists(direct)) return direct;
        string shortName = typeFullName.Split('.').Last();
        return Directory
            .EnumerateFiles(root, shortName + ".cs", SearchOption.AllDirectories)
            .FirstOrDefault(path => !path.Contains("/obj/", StringComparison.Ordinal)
                && !path.Contains("/bin/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The fork file a patch under patches/&lt;project&gt;/** or
    /// patches/runtime/&lt;project&gt;/** was generated from, or null when that
    /// tree is not checked out (both are git-ignored).
    /// </summary>
    public static string? FindPatchedTreeFile(string repoRoot, string repoRelativePatch)
    {
        string trimmed = repoRelativePatch.Replace('\\', '/');
        if (!trimmed.EndsWith(".cs.patch", StringComparison.Ordinal)) return null;
        string body = trimmed.Substring(0, trimmed.Length - ".patch".Length);
        string candidate = body.StartsWith("patches/runtime/", StringComparison.Ordinal)
            ? Path.Combine(repoRoot, ".build", "runtime-donors",
                body.Substring("patches/runtime/".Length).Replace('/', Path.DirectorySeparatorChar))
            : body.StartsWith("patches/", StringComparison.Ordinal)
                ? Path.Combine(repoRoot, body.Substring("patches/".Length).Replace('/', Path.DirectorySeparatorChar))
                : null;
        return candidate != null && File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Added line -> the methods it occurs in. A line that is pure punctuation or
    /// a bare keyword ("{", "try", "return;") is dropped: it occurs everywhere
    /// and would attribute a hunk to unrelated methods.
    /// </summary>
    private static List<KeyValuePair<string, HashSet<string>>> MethodsByAddedLine(
        string patchFile, string source, Func<string, bool> accept)
    {
        var scopes = Parse(source);
        var result = new List<KeyValuePair<string, HashSet<string>>>();
        foreach (string raw in File.ReadLines(patchFile))
        {
            if (!raw.StartsWith("+", StringComparison.Ordinal) || raw.StartsWith("+++", StringComparison.Ordinal))
            {
                continue;
            }
            string text = raw.Substring(1).Trim();
            if (!IsDistinctive(text) || !accept(text)) continue;

            var methods = new HashSet<string>(StringComparer.Ordinal);
            for (int at = source.IndexOf(text, StringComparison.Ordinal); at >= 0;
                 at = source.IndexOf(text, at + 1, StringComparison.Ordinal))
            {
                string? method = MethodAt(scopes, at);
                if (method != null) methods.Add(method);
            }
            if (methods.Count > 0) result.Add(new(text, methods));
        }
        return result;
    }

    private static readonly HashSet<string> Boilerplate = new(StringComparer.Ordinal)
    {
        "{", "}", "try", "finally", "else", "return;", "break;", "continue;", "});", ")", "};",
    };

    private static bool IsDistinctive(string text) =>
        text.Length >= 8 && !Boilerplate.Contains(text) && !text.StartsWith("//", StringComparison.Ordinal)
        && !text.StartsWith("using ", StringComparison.Ordinal);

    private static readonly Regex TypeDeclaration =
        new(@"\b(class|struct|interface|record|enum)\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled);

    private static readonly Regex MethodName =
        new(@"([A-Za-z_][A-Za-z0-9_]*)\s*(?:<[^<>()]*>)?\s*$", RegexOptions.Compiled);

    private static (string Kind, string Name) Classify(string header, string parentKind)
    {
        string text = header.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (text.StartsWith("namespace ", StringComparison.Ordinal) || text.Contains(" namespace ", StringComparison.Ordinal))
        {
            return ("namespace", text);
        }
        var type = TypeDeclaration.Match(text);
        if (type.Success) return ("type", type.Groups[2].Value);

        if (parentKind == "type" || parentKind == "namespace" || parentKind == "file")
        {
            int paren = text.IndexOf('(');
            if (paren > 0)
            {
                var name = MethodName.Match(text.Substring(0, paren).TrimEnd());
                if (name.Success) return ("method", name.Groups[1].Value);
            }
            return ("other", text);
        }
        return ("block", string.Empty);
    }

    private static int SkipLiteral(string source, int start)
    {
        char quote = source[start];
        bool verbatim = start > 0 && source[start - 1] == '@' && quote == '"';
        int i = start + 1;
        while (i < source.Length)
        {
            char c = source[i];
            if (verbatim)
            {
                if (c == '"')
                {
                    if (i + 1 < source.Length && source[i + 1] == '"') { i += 2; continue; }
                    return i + 1;
                }
            }
            else
            {
                if (c == '\\') { i += 2; continue; }
                if (c == quote) return i + 1;
                if (c == '\n') return i + 1;
            }
            i++;
        }
        return source.Length;
    }
}
