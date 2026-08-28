using System.Text.RegularExpressions;

namespace Optimum.Installer.Services;

/// <summary>
/// Decides which raw subprocess lines reach the visible log pane. Ports the
/// filter in <c>scripts/install-windows.ps1:1281-1301</c>: anything that looks
/// like an error is always shown, a short whitelist of progress prefixes is
/// shown verbatim, and the rest is kept only in the saved raw log.
/// </summary>
public static partial class InstallerLogFilter
{
    public static bool IsInteresting(string line)
    {
        string trimmed = line.TrimStart();
        return Alarming().IsMatch(line) || Warned().IsMatch(line) || Whitelisted().IsMatch(trimmed);
    }

    /// <summary>
    /// The severity to colour a log line. A line reaching stderr is not
    /// automatically an error -- ilspycmd, git and dotnet all write progress and
    /// advisories there -- so the text decides: a real failure is
    /// <c>error</c>, an advisory ("not using the latest version") is
    /// <c>warn</c>, everything else is <c>info</c>.
    /// </summary>
    public static string Classify(string line, bool fromStdErr)
    {
        if (Alarming().IsMatch(line))
            return "error";
        if (Warned().IsMatch(line))
            return "warn";
        return "info";
    }

    [GeneratedRegex(@"\berror\b|FAILED|ERROR|\bthrow\b|Exception|fatal:|does not apply", RegexOptions.IgnoreCase)]
    private static partial Regex Alarming();

    [GeneratedRegex(@"\bwarn(ing)?\b|not using the latest|latest version is|please update|is deprecated|out of date|outdated", RegexOptions.IgnoreCase)]
    private static partial Regex Warned();

    [GeneratedRegex(@"^(\[Optimum\]|==PHASE==|==>|✓|✗|Bootstrap complete|Decompiling|Cloning|Applying|Building|Packaging|Restored|Compil)", RegexOptions.IgnoreCase)]
    private static partial Regex Whitelisted();
}
