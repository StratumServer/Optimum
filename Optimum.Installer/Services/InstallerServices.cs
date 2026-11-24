using Optimum.Bootstrap.Core.Acquisition;
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

    /// <summary>Self-update for the installer. Null in a headless test.</summary>
    public IUpdateService? Updates { get; init; }

    /// <summary>
    /// Downloads the Optimum source when <see cref="RepoRoot"/> is null (a
    /// standalone installer that is not inside a checkout). Null in a headless
    /// test that supplies its own repo root.
    /// </summary>
    public ISourceProvider? SourceProvider { get; init; }

    /// <summary>Installs the optional AppImage packaging tool on supported Linux hosts.</summary>
    public IAppimagetoolAcquisition? Appimagetool { get; init; }

    /// <summary>Acquires a .NET SDK in place when one is missing. Null in a headless test.</summary>
    public ISdkAcquisition? Sdk { get; init; }

    /// <summary>Installs or realigns the pinned ilspycmd. Null in a headless test.</summary>
    public IIlspycmdAcquisition? Ilspycmd { get; init; }

    /// <summary>Acquires a user-scope or portable Git for Windows when Git is missing.</summary>
    public IGitAcquisition? Git { get; init; }

    /// <summary>Installs a selected Linux distro package after user confirmation.</summary>
    public IDistroPackageAcquisition? DistroPackages { get; init; }

    /// <summary>Session log written from startup through install completion.</summary>
    public IInstallerLog? SessionLog { get; init; }

    public static InstallerServices CreateReal() => CreateForProbe(SystemProbe.Default);

    internal static InstallerServices CreateForProbe(
        ISystemProbe probe,
        string? workingDirectory = null,
        string? logDirectory = null,
        string? version = null)
    {
        string effectiveVersion = version ?? Optimum.Bootstrap.Core.CoreInfo.Version;
        string effectiveWorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
        string expectedCache = SourceCache.Directory(probe, effectiveVersion);
        string? repoRoot = Optimum.Bootstrap.Core.Build.RepoRoot.DiscoverOrCached(
            probe, effectiveVersion, effectiveWorkingDirectory);

        InstallerSessionLog sessionLog = InstallerSessionLog.Create(logDirectory);
        sessionLog.WriteEvent("startup", $"Optimum Installer {effectiveVersion} ({probe.Os}/{probe.Arch})");
        sessionLog.WriteEvent("startup", $"Working directory: {effectiveWorkingDirectory}");
        sessionLog.WriteEvent("startup", $"Expected source cache: {expectedCache}");
        sessionLog.WriteEvent("startup", repoRoot is not null
            ? $"Resolved Optimum source: {repoRoot}"
            : "Optimum source was not found in the working directory hierarchy or version-matched cache.");

        return new InstallerServices(
            probe,
            repoRoot,
            new ScriptBuildDriver(probe),
            new PackageDeployer(probe))
        {
            Updates = new UpdateService(),
            SourceProvider = new GitSourceProvider(probe),
            Appimagetool = new AppimagetoolAcquisition(probe),
            Sdk = new SdkInstaller(probe),
            Ilspycmd = new IlspycmdInstaller(probe),
            Git = new GitInstaller(probe),
            DistroPackages = new DistroPackageAcquisition(probe),
            SessionLog = sessionLog,
        };
    }
}
