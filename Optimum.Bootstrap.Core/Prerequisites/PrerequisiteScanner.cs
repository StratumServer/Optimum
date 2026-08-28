using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Prerequisites;

/// <summary>
/// Detects every tool the bootstrap and packaging scripts need.
///
/// On Linux and macOS the tool list is the one in <c>scripts/check-prereqs.sh</c>
/// (a bash script describing what the <em>shell</em> pipeline needs). On Windows
/// that list does not apply: <c>scripts/bootstrap.ps1</c> reimplements every
/// fixup natively in PowerShell with "no perl/python3 dependency", and
/// <c>scripts/install-windows.ps1</c> requires only the .NET SDK, Git, and
/// PowerShell. The Windows list reflects that. The per-tool detection folds in
/// the richer probes from the two GUI installers.
/// </summary>
public sealed class PrerequisiteScanner(ISystemProbe probe, string repoRoot)
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    internal const string GitForWindowsUrl = "https://git-scm.com/download/win";

    private static readonly PrerequisiteDefinition[] UnixDefinitions =
    [
        new(PrerequisiteId.Dotnet, "dotnet", ".NET SDK 10", RequirementLevel.Required, "bootstrap, build"),
        new(PrerequisiteId.Git, "git", "Git", RequirementLevel.Required, "bootstrap, extract-patches"),
        new(PrerequisiteId.Perl, "perl", "Perl", RequirementLevel.Required, "bootstrap, extract-patches"),
        new(PrerequisiteId.Python3, "python3", "Python 3", RequirementLevel.Required, "bootstrap"),
        new(PrerequisiteId.Curl, "curl", "curl", RequirementLevel.Required, "bootstrap, packaging"),
        new(PrerequisiteId.Tar, "tar", "tar", RequirementLevel.Required, "bootstrap, packaging"),
        new(PrerequisiteId.Chmod, "chmod", "chmod (coreutils)", RequirementLevel.Required, "packaging"),
        new(PrerequisiteId.Pwsh, "pwsh", "PowerShell", RequirementLevel.RequiredForPackaging, "package-linux.ps1, package-macos.ps1, package.ps1"),
        new(PrerequisiteId.Unzip, "unzip", "unzip", RequirementLevel.Optional, "bootstrap (zip archives; a python3 fallback exists)"),
        new(PrerequisiteId.Ilspycmd, "ilspycmd", "ilspycmd (decompiler)", RequirementLevel.Optional, "bootstrap (auto-installs via dotnet tool)"),
        new(PrerequisiteId.Make, "make", "make", RequirementLevel.Optional, "package-macos (.dmg on Linux via libdmg-hfsplus)"),
        new(PrerequisiteId.Cmake, "cmake", "cmake", RequirementLevel.Optional, "package-macos (.dmg on Linux via libdmg-hfsplus)"),
        new(PrerequisiteId.Mkisofs, "mkisofs", "mkisofs or genisoimage", RequirementLevel.Optional, "package-macos (.dmg on Linux)"),
        new(PrerequisiteId.Innoextract, "innoextract", "innoextract 1.11 or newer", RequirementLevel.Optional, "package.ps1 (off-platform Windows package)"),
        new(PrerequisiteId.Appimagetool, "appimagetool", "appimagetool", RequirementLevel.Optional, "package-linux.sh --format appimage (auto-downloads)"),
    ];

    private static readonly PrerequisiteDefinition[] WindowsDefinitions =
    [
        new(PrerequisiteId.Dotnet, "dotnet", ".NET SDK 10", RequirementLevel.Required, "bootstrap, build"),
        new(PrerequisiteId.Git, "git", "Git", RequirementLevel.Required, "bootstrap, extract-patches"),
        new(PrerequisiteId.Pwsh, "pwsh", "PowerShell", RequirementLevel.Required, "bootstrap.ps1, package.ps1"),
        new(PrerequisiteId.Ilspycmd, "ilspycmd", "ilspycmd (decompiler)", RequirementLevel.Optional, "bootstrap (auto-installs via dotnet tool)"),
    ];

    private PrerequisiteDefinition[] Definitions =>
        probe.Os == OsKind.Windows ? WindowsDefinitions : UnixDefinitions;

    public IReadOnlyList<PrerequisiteResult> Scan() => Definitions.Select(Detect).ToArray();

    public bool AllRequiredPresent() => Scan().All(r => !r.BlocksBuild);

    private PrerequisiteResult Detect(PrerequisiteDefinition def) => def.Id switch
    {
        PrerequisiteId.Dotnet => DetectDotnet(def),
        PrerequisiteId.Ilspycmd => DetectIlspycmd(def),
        PrerequisiteId.Innoextract => DetectInnoextract(def),
        PrerequisiteId.Mkisofs => DetectEither(def, "mkisofs", "genisoimage"),
        PrerequisiteId.Appimagetool => DetectAppimagetool(def),
        PrerequisiteId.Pwsh => DetectPowerShell(def),
        PrerequisiteId.Git when probe.Os == OsKind.Windows => DetectGitOnWindows(def),
        _ => DetectPlain(def),
    };

    private PrerequisiteResult DetectPlain(PrerequisiteDefinition def)
    {
        string? path = CommandSearch.Which(probe, def.Command);
        if (path is not null)
            return Ok(def, path, null);

        return Missing(def, DistroAcquisition(def.Command));
    }

    /// <summary>
    /// PowerShell 7 (<c>pwsh</c>) satisfies the row everywhere; on Windows the
    /// built-in Windows PowerShell 5.1 is an accepted fallback, so the row is
    /// almost always Ready there.
    /// </summary>
    private PrerequisiteResult DetectPowerShell(PrerequisiteDefinition def)
    {
        string? path = PowerShellHost.Find(probe);
        if (path is not null)
            return Ok(def, path, null);

        return probe.Os == OsKind.Windows
            ? new PrerequisiteResult(def, PrerequisiteState.Missing, def.DisplayName, null, null,
                AcquisitionKind.DownloadPage, null, "https://aka.ms/powershell")
            : Missing(def, DistroAcquisition("pwsh"));
    }

    /// <summary>Git for Windows is a signed installer, not a package; point the user at it.</summary>
    private PrerequisiteResult DetectGitOnWindows(PrerequisiteDefinition def)
    {
        string? path = CommandSearch.Which(probe, def.Command);
        if (path is not null)
            return Ok(def, path, null);

        return new PrerequisiteResult(def, PrerequisiteState.Missing, def.DisplayName, null, null,
            AcquisitionKind.DownloadPage, null, GitForWindowsUrl);
    }

    private PrerequisiteResult DetectEither(PrerequisiteDefinition def, string first, string second)
    {
        string? path = CommandSearch.Which(probe, first) ?? CommandSearch.Which(probe, second);
        return path is not null ? Ok(def, path, null) : Missing(def, DistroAcquisition(first));
    }

    private PrerequisiteResult DetectDotnet(PrerequisiteDefinition def)
    {
        string? sdk = DotnetSdkProbe.Find(probe);
        if (sdk is not null)
        {
            ProcessOutcome outcome = probe.Run(sdk, ["--version"], ProbeTimeout);
            string? version = outcome.Started ? outcome.StandardOutput.Trim() : null;
            return Ok(def, sdk, version);
        }

        if (NixEnvironment.IsNixOs(probe))
        {
            return new PrerequisiteResult(def, PrerequisiteState.Missing,
                $"{def.DisplayName} (install through nixpkgs)", null, null,
                AcquisitionKind.Manual, NixEnvironment.DotnetSdkInstallCommand, null);
        }

        if (!NixEnvironment.DownloadedSdkRunnable(probe))
        {
            return new PrerequisiteResult(def, PrerequisiteState.Missing,
                $"{def.DisplayName} (non-FHS system: the dot.net installer will not run here)", null, null,
                AcquisitionKind.None, null, null);
        }

        return new PrerequisiteResult(def, PrerequisiteState.Missing, def.DisplayName, null, null,
            AcquisitionKind.Automatic, null, "https://dotnet.microsoft.com/download/dotnet/10.0");
    }

    private PrerequisiteResult DetectIlspycmd(PrerequisiteDefinition def)
    {
        IlspycmdCompatibility compat = ConfigFiles.ReadIlspycmdCompatibility(probe, repoRoot);
        string? path = CommandSearch.Which(probe, "ilspycmd")
            ?? ExistingOrNull(Path.Combine(probe.HomeDirectory, ".dotnet", "tools", "ilspycmd"));

        if (path is null)
        {
            return new PrerequisiteResult(def, PrerequisiteState.OptionalMissing,
                $"{def.DisplayName} {compat.Pin}", null, null,
                AcquisitionKind.Automatic, IlspycmdVersionCommand(compat.Pin), null);
        }

        string? version = ReadIlspycmdVersion(path);
        if (compat.Supports(version))
            return Ok(def, path, version);

        return new PrerequisiteResult(def, PrerequisiteState.Outdated,
            $"{def.DisplayName} {version ?? "unknown"} (needs {compat.Minimum} to {compat.Maximum})",
            path, version, AcquisitionKind.Automatic, IlspycmdVersionCommand(compat.Pin), null);
    }

    private PrerequisiteResult DetectInnoextract(PrerequisiteDefinition def)
    {
        string? path = CommandSearch.Which(probe, "innoextract");
        if (path is null)
            return Missing(def, AcquisitionKind.DownloadPage, null,
                "https://github.com/crazy-max/innoextract/releases");

        ProcessOutcome outcome = probe.Run(path, ["--version"], ProbeTimeout);
        (int major, int minor)? parsed = ParseInnoextractVersion(outcome.StandardOutput);
        if (parsed is { } v && (v.major > 1 || (v.major == 1 && v.minor >= 11)))
            return Ok(def, path, $"{v.major}.{v.minor}");

        return new PrerequisiteResult(def, PrerequisiteState.Outdated,
            $"{def.DisplayName} (found {parsed?.major}.{parsed?.minor}, need 1.11 or newer)",
            path, parsed is { } p ? $"{p.major}.{p.minor}" : null,
            AcquisitionKind.DownloadPage, null, "https://github.com/crazy-max/innoextract/releases");
    }

    private PrerequisiteResult DetectAppimagetool(PrerequisiteDefinition def)
    {
        string? path = CommandSearch.Which(probe, "appimagetool")
            ?? ExecutableOrNull(Path.Combine(repoRoot, ".tools", "appimagetool"))
            ?? ExecutableOrNull(Path.Combine(probe.HomeDirectory, ".tools", "appimagetool"));
        return path is not null
            ? Ok(def, path, null)
            : new PrerequisiteResult(def, PrerequisiteState.OptionalMissing, def.DisplayName, null, null,
                AcquisitionKind.Automatic, null, null);
    }

    private static string IlspycmdVersionCommand(string pin) =>
        $"dotnet tool update -g ilspycmd --version {pin} --allow-downgrade";

    private AcquisitionKind DistroAcquisition(string package) =>
        DistroPackageHints.InstallCommand(probe, package) is not null
            ? AcquisitionKind.Manual
            : AcquisitionKind.None;

    private PrerequisiteResult Ok(PrerequisiteDefinition def, string path, string? version) =>
        new(def, PrerequisiteState.Ok,
            version is null ? def.DisplayName : $"{def.DisplayName} ({version})",
            path, version, AcquisitionKind.None, null, null);

    private PrerequisiteResult Missing(PrerequisiteDefinition def, AcquisitionKind acquisition) =>
        Missing(def, acquisition, DistroPackageHints.InstallCommand(probe, def.Command), null);

    private PrerequisiteResult Missing(PrerequisiteDefinition def, AcquisitionKind acquisition, string? command, string? url)
    {
        PrerequisiteState state = def.Level == RequirementLevel.Optional
            ? PrerequisiteState.OptionalMissing
            : PrerequisiteState.Missing;
        return new PrerequisiteResult(def, state, def.DisplayName, null, null, acquisition, command, url);
    }

    private string? ExistingOrNull(string path) => probe.FileExists(path) ? path : null;

    private string? ExecutableOrNull(string path) => probe.IsExecutable(path) ? path : null;

    private string? ReadIlspycmdVersion(string path)
    {
        ProcessOutcome outcome = probe.Run(path, ["--version"], ProbeTimeout);
        if (!outcome.Started)
            return null;
        string firstLine = outcome.StandardOutput.Split('\n').FirstOrDefault() ?? string.Empty;
        string[] tokens = firstLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return tokens.Length >= 2 ? tokens[1].Trim() : null;
    }

    public static (int major, int minor)? ParseInnoextractVersion(string output)
    {
        foreach (string line in output.Split('\n'))
        {
            string trimmed = line.Trim();
            const string prefix = "innoextract ";
            if (!trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            string rest = trimmed[prefix.Length..];
            string[] parts = rest.Split('.', '-', ' ');
            if (parts.Length >= 2
                && int.TryParse(parts[0], out int major)
                && int.TryParse(parts[1], out int minor))
                return (major, minor);
        }

        return null;
    }
}
