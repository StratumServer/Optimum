using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Prerequisites;

public enum DistroPackageManager
{
    Apt,
    Dnf,
    Pacman,
    Zypper,
}

/// <summary>A reviewed package-manager invocation for one prerequisite.</summary>
public sealed record DistroPackagePlan(
    DistroPackageManager Manager,
    string ManagerPath,
    string PkexecPath,
    string Package,
    IReadOnlyList<string> Arguments,
    string ManualCommand);

/// <summary>
/// Maps prerequisite commands to distro package names and exact package-manager
/// arguments. The command string is only a copyable fallback; execution uses the
/// argument list through pkexec and never invokes a shell.
/// </summary>
public static class DistroPackageHints
{
    public static DistroPackagePlan? Plan(ISystemProbe probe, string command)
    {
        if (probe.Os != OsKind.Linux)
            return null;

        string? pkexec = CommandSearch.Which(probe, "pkexec");
        if (pkexec is null)
            return null;

        (DistroPackageManager Manager, string Path)? manager = FindManager(probe);
        if (manager is null)
            return null;

        string? package = PackageFor(manager.Value.Manager, command);
        if (package is null)
            return null;

        string[] arguments = manager.Value.Manager switch
        {
            DistroPackageManager.Apt => [manager.Value.Path, "install", "-y", package],
            DistroPackageManager.Dnf => [manager.Value.Path, "install", "-y", package],
            DistroPackageManager.Pacman => [manager.Value.Path, "-S", "--needed", "--noconfirm", package],
            DistroPackageManager.Zypper => [manager.Value.Path, "--non-interactive", "install", package],
            _ => throw new ArgumentOutOfRangeException(),
        };

        string manual = manager.Value.Manager switch
        {
            DistroPackageManager.Apt => $"sudo apt-get install -y {package}",
            DistroPackageManager.Dnf => $"sudo dnf install -y {package}",
            DistroPackageManager.Pacman => $"sudo pacman -S --needed --noconfirm {package}",
            DistroPackageManager.Zypper => $"sudo zypper --non-interactive install {package}",
            _ => throw new ArgumentOutOfRangeException(),
        };

        return new DistroPackagePlan(manager.Value.Manager, manager.Value.Path, pkexec,
            package, arguments, manual);
    }

    /// <summary>Copyable hint even when pkexec is not installed.</summary>
    public static string? InstallCommand(ISystemProbe probe, string command)
    {
        if (probe.Os != OsKind.Linux)
            return null;

        (DistroPackageManager Manager, string Path)? manager = FindManager(probe);
        if (manager is null)
            return null;
        string? package = PackageFor(manager.Value.Manager, command);
        if (package is null)
            return null;

        return manager.Value.Manager switch
        {
            DistroPackageManager.Apt => $"sudo apt-get install -y {package}",
            DistroPackageManager.Dnf => $"sudo dnf install -y {package}",
            DistroPackageManager.Pacman => $"sudo pacman -S --needed --noconfirm {package}",
            DistroPackageManager.Zypper => $"sudo zypper --non-interactive install {package}",
            _ => null,
        };
    }

    /// <summary>Actionable fallback for a Linux distribution without a supported manager.</summary>
    public static string? ManualInstruction(ISystemProbe probe, string command)
    {
        if (probe.Os != OsKind.Linux || PackageFor(DistroPackageManager.Apt, command) is null)
            return null;
        return $"No supported apt, dnf, pacman, or zypper manager was detected. Install {command} from your distribution's repositories, then check again.";
    }

    private static (DistroPackageManager Manager, string Path)? FindManager(ISystemProbe probe)
    {
        foreach ((DistroPackageManager manager, string command) in new[]
        {
            (DistroPackageManager.Apt, "apt-get"),
            (DistroPackageManager.Dnf, "dnf"),
            (DistroPackageManager.Pacman, "pacman"),
            (DistroPackageManager.Zypper, "zypper"),
        })
        {
            string? path = CommandSearch.Which(probe, command);
            if (path is not null)
                return (manager, path);
        }
        return null;
    }

    private static string? PackageFor(DistroPackageManager manager, string command) => command switch
    {
        "git" => "git",
        "perl" => "perl",
        "python3" => manager == DistroPackageManager.Pacman ? "python" : "python3",
        "curl" => "curl",
        "tar" => "tar",
        "chmod" => "coreutils",
        "unzip" => "unzip",
        "make" => "make",
        "cmake" => "cmake",
        "mkisofs" => manager == DistroPackageManager.Pacman ? "cdrtools" : "genisoimage",
        _ => null,
    };
}
