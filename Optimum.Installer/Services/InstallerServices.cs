using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Installer.Services;

/// <summary>
/// The Core services the GUI drives in-process. The real app builds this from
/// <see cref="SystemProbe.Default"/>; a headless test builds it from fakes.
/// </summary>
public sealed record InstallerServices(
    ISystemProbe Probe,
    string? RepoRoot,
    IBuildDriver BuildDriver,
    IPackageInstaller Installer)
{
    /// <summary>
    /// Marshals a callback to the UI thread. Null means the default
    /// <c>Dispatcher.UIThread.Post</c>; a headless test injects a synchronous one.
    /// </summary>
    public Action<Action>? UiPost { get; init; }


    public static InstallerServices CreateReal()
    {
        var probe = SystemProbe.Default;
        return new InstallerServices(
            probe,
            Optimum.Bootstrap.Core.Build.RepoRoot.Discover(probe),
            new ScriptBuildDriver(probe),
            new PackageDeployer(probe));
    }
}
