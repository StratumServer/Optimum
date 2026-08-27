using Optimum.Bootstrap.Core.Install;

namespace Optimum.Installer.ViewModels;

/// <summary>What the Options screen collected, handed to the Progress screen.</summary>
public sealed record InstallSession(
    string RepoRoot,
    string InstallDirectory,
    string? DataPath,
    string? Version,
    ShortcutKinds Shortcuts);

public sealed record InstallOutcome(
    bool Succeeded,
    bool Cancelled,
    string Message,
    string? InstallDirectory,
    string? Launcher,
    string RawLogPath);
