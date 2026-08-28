namespace Optimum.Bootstrap.Core.Prerequisites;

public enum PrerequisiteId
{
    Dotnet,
    Git,
    Perl,
    Python3,
    Curl,
    Tar,
    Chmod,
    Pwsh,
    Unzip,
    Ilspycmd,
    Make,
    Cmake,
    Mkisofs,
    Innoextract,
    Appimagetool,
}

public enum RequirementLevel
{
    /// <summary>Bootstrap and build cannot run without it.</summary>
    Required,

    /// <summary>
    /// Only the packaging step needs it. <c>scripts/check-prereqs.sh</c> marks
    /// <c>pwsh</c> as required outright, which tells a Linux user building a
    /// <c>tar.gz</c> to install PowerShell for no reason. Core narrows it.
    /// </summary>
    RequiredForPackaging,

    /// <summary>A missing optional tool only skips a package target.</summary>
    Optional,
}

public enum PrerequisiteState
{
    Ok,

    /// <summary>Present but the wrong version (ilspycmd out of range, innoextract below 1.11).</summary>
    Outdated,

    /// <summary>A required or packaging tool that is not installed.</summary>
    Missing,

    /// <summary>An optional tool that is not installed.</summary>
    OptionalMissing,
}

/// <summary>How the installer can resolve a missing prerequisite.</summary>
public enum AcquisitionKind
{
    /// <summary>Nothing the installer can do; the user installs it and retries.</summary>
    None,

    /// <summary>The installer runs it without the user leaving the app (SDK script, ilspycmd tool).</summary>
    Automatic,

    /// <summary>The installer shows a copyable command (a distro package, the Nix profile command).</summary>
    Manual,

    /// <summary>The installer opens a download page.</summary>
    DownloadPage,
}

public sealed record PrerequisiteDefinition(
    PrerequisiteId Id,
    string Command,
    string DisplayName,
    RequirementLevel Level,
    string UsedBy);

public sealed record PrerequisiteResult(
    PrerequisiteDefinition Definition,
    PrerequisiteState State,
    string Label,
    string? DetectedPath,
    string? DetectedVersion,
    AcquisitionKind Acquisition,
    string? AcquisitionCommand,
    string? DownloadUrl)
{
    public bool BlocksBuild => State is PrerequisiteState.Missing or PrerequisiteState.Outdated
        && Definition.Level is RequirementLevel.Required;
}
