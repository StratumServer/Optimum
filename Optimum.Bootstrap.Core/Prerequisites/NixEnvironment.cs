using System.Runtime.InteropServices;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Prerequisites;

/// <summary>
/// Ports the NixOS and non-FHS detection from <c>scripts/install-linux.sh</c>.
/// The <c>dotnet-install</c> script downloads a glibc SDK whose binaries hardcode
/// the system dynamic linker; on NixOS and other non-FHS systems that linker
/// lives in the Nix store, so the downloaded SDK cannot run. Core keeps the same
/// refusal and the same <c>nix profile install</c> substitute.
/// </summary>
public static class NixEnvironment
{
    public const string DotnetSdkInstallCommand = "nix profile install nixpkgs#dotnet-sdk_10";

    public static bool IsNixOs(ISystemProbe probe) =>
        probe.PathExists("/etc/NIXOS")
        || !string.IsNullOrEmpty(probe.GetEnvironmentVariable("NIX_STORE"));

    /// <summary>
    /// The dynamic linker path for the current architecture, or an empty string
    /// on an architecture the script does not know how to check. The
    /// <c>OPTIMUM_GLIBC_INTERPRETER</c> environment variable overrides it, which
    /// is how the shell tests simulate a non-FHS host.
    /// </summary>
    public static string GlibcInterpreterPath(ISystemProbe probe)
    {
        string? overridePath = probe.GetEnvironmentVariable("OPTIMUM_GLIBC_INTERPRETER");
        if (!string.IsNullOrEmpty(overridePath))
            return overridePath;

        return probe.Arch switch
        {
            Architecture.X64 => "/lib64/ld-linux-x86-64.so.2",
            Architecture.Arm64 => "/lib/ld-linux-aarch64.so.1",
            _ => string.Empty,
        };
    }

    /// <summary>
    /// True when a downloaded glibc SDK could run here: either the architecture
    /// is unknown (so the check is skipped) or the interpreter exists.
    /// </summary>
    public static bool DownloadedSdkRunnable(ISystemProbe probe)
    {
        string interpreter = GlibcInterpreterPath(probe);
        return interpreter.Length == 0 || probe.PathExists(interpreter);
    }
}
