namespace Optimum.Bootstrap.Core.Acquisition;

/// <summary>
/// The command that installs or realigns the pinned decompiler. Matches the
/// invocation the Linux installer logs and the shell test asserts:
/// <c>tool update -g ilspycmd --version &lt;pin&gt; --allow-downgrade</c>, run
/// through the discovered <c>dotnet</c>.
/// </summary>
public static class IlspycmdAcquisition
{
    public static IReadOnlyList<string> ToolArguments(string pin) =>
        ["tool", "update", "-g", "ilspycmd", "--version", pin, "--allow-downgrade"];

    public static string CommandLine(string dotnetExecutable, string pin) =>
        $"{dotnetExecutable} {string.Join(' ', ToolArguments(pin))}";
}
