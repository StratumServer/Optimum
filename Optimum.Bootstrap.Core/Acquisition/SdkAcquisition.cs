using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;

namespace Optimum.Bootstrap.Core.Acquisition;

/// <summary>
/// Plans a .NET SDK acquisition through the official <c>dotnet-install</c>
/// scripts. Ports the refusal in <c>install_dotnet10</c>: the glibc installer is
/// not attempted on NixOS or on any host whose dynamic linker is missing, and
/// the plan honours the <c>global.json</c> pin with <c>--jsonfile</c> rather than
/// the wider <c>--channel</c> the shell script uses.
/// </summary>
public static class SdkAcquisition
{
    public sealed record Plan(
        string ScriptUrl,
        string ScriptExecutable,
        IReadOnlyList<string> Arguments,
        string InstallDirectory);

    public sealed record Decision(bool CanRunScript, string? RefusalReason, Plan? Plan);

    public static Decision Evaluate(ISystemProbe probe, string repoRoot)
    {
        if (NixEnvironment.IsNixOs(probe))
        {
            return new Decision(false,
                $"NixOS: install the SDK with `{NixEnvironment.DotnetSdkInstallCommand}` instead.", null);
        }

        if (!NixEnvironment.DownloadedSdkRunnable(probe))
        {
            return new Decision(false,
                "This is a non-FHS system: the SDK from dot.net is a glibc build whose dynamic linker is not present here.", null);
        }

        string installDir = Path.Combine(probe.HomeDirectory, ".dotnet");
        string globalJson = Path.Combine(repoRoot, "global.json");
        bool windows = probe.Os == OsKind.Windows;

        var args = windows
            ? new List<string> { "-InstallDir", installDir, "-NoPath" }
            : new List<string> { "--install-dir", installDir, "--no-path" };

        if (probe.FileExists(globalJson))
            args.AddRange(windows ? ["-JSonFile", globalJson] : ["--jsonfile", globalJson]);
        else
            args.AddRange(windows ? ["-Channel", "10.0"] : ["--channel", "10.0"]);

        var plan = new Plan(
            windows ? "https://dot.net/v1/dotnet-install.ps1" : "https://dot.net/v1/dotnet-install.sh",
            windows ? PowerShellHost.Resolve(probe) ?? "powershell" : "bash",
            args,
            installDir);

        return new Decision(true, null, plan);
    }
}
