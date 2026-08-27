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
        return Alarming().IsMatch(line) || Whitelisted().IsMatch(trimmed);
    }

    [GeneratedRegex(@"\berror\b|FAILED|ERROR|throw|Exception|fatal:|does not apply", RegexOptions.IgnoreCase)]
    private static partial Regex Alarming();

    [GeneratedRegex(@"^(\[Optimum\]|==PHASE==|==>|✓|✗|Bootstrap complete|Decompiling|Cloning|Applying|Building|Packaging|Restored|Compil)", RegexOptions.IgnoreCase)]
    private static partial Regex Whitelisted();
}
