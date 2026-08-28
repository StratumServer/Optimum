namespace Optimum.Bootstrap.Core.Platform;

/// <summary>
/// Locates a PowerShell interpreter for the packaging and bootstrap scripts.
/// PowerShell 7 (<c>pwsh</c>) is preferred everywhere; on Windows the built-in
/// Windows PowerShell 5.1 (<c>powershell.exe</c>) is an accepted fallback, which
/// is what <c>scripts/install-windows.ps1</c> uses to run <c>bootstrap.ps1</c>.
/// The <c>package.ps1</c> family needs 5.1 or newer, so either satisfies it.
/// </summary>
public static class PowerShellHost
{
    /// <summary>
    /// The interpreter to spawn, or null when none is on PATH. Returns a bare
    /// command name (<c>pwsh</c> / <c>powershell</c>) so the caller passes it
    /// straight to a process launcher; use <see cref="Find"/> for an absolute
    /// path when one is needed.
    /// </summary>
    public static string? Resolve(ISystemProbe probe)
    {
        if (CommandSearch.Exists(probe, "pwsh"))
            return "pwsh";
        if (probe.Os == OsKind.Windows && CommandSearch.Exists(probe, "powershell"))
            return "powershell";
        return null;
    }

    /// <summary>The absolute path to the interpreter, or null when none is found.</summary>
    public static string? Find(ISystemProbe probe) =>
        CommandSearch.Which(probe, "pwsh")
        ?? (probe.Os == OsKind.Windows ? CommandSearch.Which(probe, "powershell") : null);
}
