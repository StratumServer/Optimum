using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;

namespace Optimum.Installer.ViewModels;

/// <summary>
/// A single prerequisite row. Not a <see cref="ViewModelBase"/> so the
/// <see cref="ViewLocator"/> never tries to resolve a view for it: it is only
/// ever rendered through an explicit item template.
/// </summary>
public sealed class PrerequisiteRowViewModel(PrerequisiteResult result) : ObservableObject
{
    public PrerequisiteResult Result { get; } = result;

    public string Name => Result.Definition.DisplayName;
    public string Status => Result.State.ToString();
    public string Detail => Result.Label;

    /// <summary>The action button label, or null when there is nothing to do.</summary>
    public string? ActionLabel => Result.Acquisition switch
    {
        AcquisitionKind.Automatic => "Install",
        AcquisitionKind.Manual => "Copy command",
        AcquisitionKind.DownloadPage => "Download",
        _ => null,
    };

    public string? ActionDetail => Result.AcquisitionCommand ?? Result.DownloadUrl;
}

public sealed partial class PrerequisitesViewModel : ViewModelBase
{
    private readonly ISystemProbe _probe;
    private readonly string? _repoRoot;

    public PrerequisitesViewModel(ISystemProbe probe, string? repoRoot)
    {
        _probe = probe;
        _repoRoot = repoRoot;
        Rescan();
    }

    public ObservableCollection<PrerequisiteRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    private bool _repoRootMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    private int _blockingCount;

    public bool CanContinue => !RepoRootMissing && BlockingCount == 0;

    public event Action? ContinueRequested;

    [RelayCommand]
    private void Continue()
    {
        if (CanContinue)
            ContinueRequested?.Invoke();
    }

    public string Summary => RepoRootMissing
        ? "Run the installer from inside an Optimum checkout."
        : BlockingCount == 0
            ? "All required tools are present."
            : $"{BlockingCount} required tool(s) still missing.";

    [RelayCommand]
    public void Rescan()
    {
        Rows.Clear();

        if (_repoRoot is null)
        {
            RepoRootMissing = true;
            BlockingCount = 0;
            OnPropertyChanged(nameof(Summary));
            return;
        }

        RepoRootMissing = false;
        var results = new PrerequisiteScanner(_probe, _repoRoot).Scan();
        foreach (var result in results)
            Rows.Add(new PrerequisiteRowViewModel(result));
        BlockingCount = results.Count(r => r.BlocksBuild);
        OnPropertyChanged(nameof(Summary));
    }
}
