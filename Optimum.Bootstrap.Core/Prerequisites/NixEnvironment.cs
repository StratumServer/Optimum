using System.Runtime.InteropServices;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Prerequisites;

/// <summary>
/// Ports the NixOS and non-FHS detection from <c>scripts/install-linux.sh</c>.
/// The <c>dotnet-install</c> script downloads a glibc SDK whose binaries hardcode
/// the system dynamic linker; on NixOS and other non-FHS systems that linker
/// lives in the Nix store, so the downloaded SDK cannot run. Core keeps the same
/// refusal and points users to the project's locked development shell instead
/// of changing a user or system profile.
///
/// The whole concept is Linux-only: the checks short-circuit on Windows and
/// macOS, where the <c>dot.net</c> installer ships a native runtime with no
/// glibc interpreter dependency.
/// </summary>
public static class NixEnvironment
{
    public static string DevelopmentShellCommand(string repoRoot) =>
        $"nix --extra-experimental-features 'nix-command flakes' develop --no-update-lock-file {QuoteForShell(repoRoot)}";

    public const string DevelopmentShellDetails =
        "This project-local shell provides the .NET SDK pinned by global.json and the bootstrap tools. " +
        "Run the command in a terminal, then restart the installer from that shell so it can detect the tools. " +
        "It requires Nix flakes for this command and does not install packages into a profile or system configuration.";

    public static PrerequisiteResult MissingFromDevelopmentShell(
        PrerequisiteDefinition definition, string repoRoot, ISystemProbe probe)
    {
        bool supported = probe.Arch is Architecture.X64 or Architecture.Arm64;
        string command = supported
            ? DevelopmentShellCommand(repoRoot)
            : $"nix search nixpkgs '{SearchTerm(definition)}'";
        string guidance = supported
            ? DevelopmentShellDetails
            : $"The project Nix shell does not define a verified environment for {probe.Arch}. " +
              "Search for a .NET 10 SDK and the listed bootstrap tools for this architecture, add available packages to a user development shell, and restart the installer from it. " +
              "The installer does not change system configuration.";

        return new(definition, PrerequisiteState.Missing,
            $"{definition.DisplayName} (provided by the project Nix shell)", null, null,
            AcquisitionKind.Manual, command, null, guidance);
    }

    private static string SearchTerm(PrerequisiteDefinition definition) => definition.Id switch
    {
        PrerequisiteId.Dotnet => "dotnet-sdk_10",
        PrerequisiteId.Python3 => "python3",
        PrerequisiteId.Tar => "gnutar",
        PrerequisiteId.Chmod => "coreutils",
        PrerequisiteId.Pwsh => "powershell",
        _ => definition.Command,
    };

    public static bool IsNixOs(ISystemProbe probe) =>
        probe.Os == OsKind.Linux
        && (probe.PathExists("/etc/NIXOS")
            || !string.IsNullOrEmpty(probe.GetEnvironmentVariable("NIX_STORE")));

    private static string QuoteForShell(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// The dynamic linker path for the current architecture, or an empty string
    /// on an architecture the script does not know how to check. The
    /// <c>OPTIMUM_GLIBC_INTERPRETER</c> environment variable overrides it, which
    /// is how the shell tests simulate a non-FHS host.
    /// </summary>
    public static string GlibcInterpreterPath(ISystemProbe probe)
    {
        if (probe.Os != OsKind.Linux)
            return string.Empty;

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
