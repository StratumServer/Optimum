using System.Text.RegularExpressions;

namespace Optimum.Installer.Services;

/// <summary>
/// Decides which raw subprocess lines reach the visible log pane and how to
/// colour them. The full stream is always kept in the saved raw log; the
/// visible pane is signal only: progress markers, real failures, and a handful
/// of advisories the user can act on. Routine compiler and SDK warnings
/// (CS0618 "obsolete", NETSDK1086, patch-tool noise) are dropped from the
/// visible pane -- there is nothing to do about someone else's deprecated API
/// during a decompile-and-recompile.
/// </summary>
public static partial class InstallerLogFilter
{
    public static bool IsInteresting(string line)
    {
        string trimmed = line.TrimStart();
        return Failure().IsMatch(line) || Advisory().IsMatch(line) || Whitelisted().IsMatch(trimmed);
    }

    /// <summary>
    /// The severity to colour a visible log line. A line on stderr is not
    /// automatically an error -- ilspycmd, git and dotnet all write there -- so
    /// the text decides: a real failure is <c>error</c>, an actionable advisory
    /// ("you are not using the latest version") is <c>warn</c>, everything else
    /// (including compiler warnings that slip through) is <c>info</c>.
    /// </summary>
    public static string Classify(string line, bool fromStdErr)
    {
        if (Failure().IsMatch(line))
            return "error";
        if (Advisory().IsMatch(line))
            return "warn";
        return "info";
    }

    // A real failure: an MSBuild/compiler error line, a failed patch hunk, a
    // non-zero failure count, an exception, a fatal git message. Deliberately
    // does not fire on "0 Error(s)" or a stray "error" inside advisory prose.
    [GeneratedRegex(
        @":\s*error\s+[A-Z]{1,6}\d|^\s*error:\s|\bpatch failed\b|hunk\s+#?\d+\s+FAILED\b|Saved rejects in|does not apply\b|did not survive|\.patch\.rej\b|\bBuild FAILED\b|(?-i:Exception)|^fatal:\s|[1-9]\d*\s+[Ee]rror(?:\(s\)|s)?\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex Failure();

    // An advisory the user can act on -- currently only the decompiler telling
    // us a newer build exists.
    [GeneratedRegex(
        @"not using the latest version|latest version is '|não está usando a vers[aã]o mais recente|please update the tool",
        RegexOptions.IgnoreCase)]
    private static partial Regex Advisory();

    [GeneratedRegex(@"^(\[Optimum\]|==PHASE==|==>|✓|✗|Bootstrap complete|Decompil|Clon|Applying|Building|Packaging|Restored|Compil|Patches:\s)", RegexOptions.IgnoreCase)]
    private static partial Regex Whitelisted();
}
