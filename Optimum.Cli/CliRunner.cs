using Optimum.Bootstrap.Core;

namespace Optimum.Cli;

/// <summary>
/// The command dispatcher, split from <c>Program</c> so tests drive it with
/// injected writers and without a process boundary. Phase 0 implements only
/// <c>--version</c>; the verbs in INSTALLER-PLAN.md section 4 land in Phase 2.
/// </summary>
internal static class CliRunner
{
    public const int ExitOk = 0;
    public const int ExitUsage = 2;

    public static int Run(string[] args, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 1 && args[0] == "--version")
        {
            stdout.WriteLine(CoreInfo.Version);
            return ExitOk;
        }

        stderr.WriteLine("usage: optimum <verb> [--json] [flags]");
        stderr.WriteLine("verbs: preflight, build, install, validate, uninstall, capabilities");
        stderr.WriteLine("(only --version is implemented in this build)");
        return ExitUsage;
    }
}
