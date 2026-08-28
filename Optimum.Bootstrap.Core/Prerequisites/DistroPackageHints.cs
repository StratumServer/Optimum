using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Prerequisites;

/// <summary>
/// Ports <c>system_install_command</c> from <c>scripts/install-linux.sh</c>: a
/// copyable <c>sudo</c> command for the distro's package manager. Core never runs
/// these; it shows them.
/// </summary>
public static class DistroPackageHints
{
    public static string? InstallCommand(ISystemProbe probe, string package)
    {
        if (CommandSearch.Exists(probe, "apt-get"))
            return $"sudo apt-get install -y {package}";
        if (CommandSearch.Exists(probe, "dnf"))
            return $"sudo dnf install -y {package}";
        if (CommandSearch.Exists(probe, "pacman"))
            return $"sudo pacman -S --needed --noconfirm {package}";
        if (CommandSearch.Exists(probe, "zypper"))
            return $"sudo zypper --non-interactive install {package}";
        return null;
    }
}
