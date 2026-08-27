using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.DataPath;
using Optimum.Bootstrap.Core.Paths;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Installer.ViewModels;

public sealed partial class OptionsViewModel : ViewModelBase
{
    private readonly ISystemProbe _probe;

    public OptionsViewModel(ISystemProbe probe, string? repoRoot)
    {
        _probe = probe;
        RepoRoot = repoRoot;

        InstallDirectory = DefaultInstallDirectory(probe);

        DataPathDetection detected = DataPathProbe.Detect(probe);
        if (detected.Path is not null)
        {
            UseSeparateDataFolder = true;
            DataPath = detected.Path;
            DataPathHint = detected.HasActiveSession
                ? "Detected a Vintage Story data folder with a signed-in session."
                : "Detected an existing Vintage Story data folder.";
        }

        if (repoRoot is not null)
        {
            foreach (string version in Capabilities.Read(probe, repoRoot).SupportedVersions)
                Versions.Add(version);
        }
        SelectedVersion = Versions.FirstOrDefault();

        Validate();
    }

    public string? RepoRoot { get; }

    public ObservableCollection<string> Versions { get; } = [];

    public bool ShowVersionChoice => Versions.Count > 1;

    [ObservableProperty]
    private string? _selectedVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    private string _installDirectory = string.Empty;

    [ObservableProperty]
    private bool _useSeparateDataFolder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    private string _dataPath = string.Empty;

    [ObservableProperty]
    private string? _dataPathHint;

    [ObservableProperty]
    private bool _createMenuEntry = true;

    [ObservableProperty]
    private bool _createDesktopShortcut;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    private string? _validationError;

    public bool CanContinue => ValidationError is null && InstallDirectory.Length > 0;

    public event Action? ContinueRequested;
    public event Action? BackRequested;

    [RelayCommand]
    private void Continue()
    {
        Validate();
        if (CanContinue)
            ContinueRequested?.Invoke();
    }

    [RelayCommand]
    private void Back() => BackRequested?.Invoke();

    partial void OnInstallDirectoryChanged(string value) => Validate();

    partial void OnUseSeparateDataFolderChanged(bool value) => Validate();

    partial void OnDataPathChanged(string value) => Validate();

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(InstallDirectory))
        {
            ValidationError = "Choose an install directory.";
            return;
        }

        string? data = UseSeparateDataFolder && DataPath.Length > 0 ? DataPath : null;
        InstallPathVerdict verdict = InstallPathGuard.Check(_probe, new InstallPathRequest(InstallDirectory, data));
        ValidationError = verdict.Ok ? null : verdict.Rejection;
    }

    public string? ResolvedDataPath => UseSeparateDataFolder && DataPath.Length > 0 ? DataPath : null;

    private static string DefaultInstallDirectory(ISystemProbe probe) => probe.Os switch
    {
        OsKind.Windows => Path.Combine(
            probe.GetEnvironmentVariable("LOCALAPPDATA") ?? probe.HomeDirectory, "Programs", "Optimum"),
        OsKind.MacOs => Path.Combine(probe.HomeDirectory, "Applications", "Optimum"),
        _ => Path.Combine(
            probe.GetEnvironmentVariable("XDG_DATA_HOME") ?? Path.Combine(probe.HomeDirectory, ".local", "share"),
            "optimum"),
    };
}
