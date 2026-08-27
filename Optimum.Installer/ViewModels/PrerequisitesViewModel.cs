using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Build;
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
    private readonly ISourceProvider? _sourceProvider;
    private readonly Action<Action> _post;
    private string? _repoRoot;

    public PrerequisitesViewModel(
        ISystemProbe probe,
        string? repoRoot,
        ISourceProvider? sourceProvider = null,
        Action<Action>? uiPost = null)
    {
        _probe = probe;
        _repoRoot = repoRoot;
        _sourceProvider = sourceProvider;
        _post = uiPost ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        Rescan();
    }

    public ObservableCollection<PrerequisiteRowViewModel> Rows { get; } = [];

    /// <summary>True while there is no Optimum checkout on the machine yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    [NotifyPropertyChangedFor(nameof(CanAcquireSource))]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private bool _repoRootMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    private int _blockingCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanContinue))]
    [NotifyPropertyChangedFor(nameof(CanAcquireSource))]
    private bool _acquiringSource;

    [ObservableProperty]
    private string _sourceStatus = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private string? _sourceError;

    public bool CanContinue => !RepoRootMissing && !AcquiringSource && BlockingCount == 0;

    /// <summary>The GUI can offer to download the source only if it was handed a provider.</summary>
    public bool CanAcquireSource => RepoRootMissing && !AcquiringSource && _sourceProvider is not null;

    /// <summary>Raised with the resolved repo root once the user moves on.</summary>
    public event Action<string>? ContinueRequested;

    [RelayCommand]
    private void Continue()
    {
        if (CanContinue && _repoRoot is not null)
            ContinueRequested?.Invoke(_repoRoot);
    }

    [RelayCommand]
    private async Task AcquireSourceAsync()
    {
        if (_sourceProvider is null || AcquiringSource || !RepoRootMissing)
            return;

        AcquiringSource = true;
        SourceError = null;
        SourceStatus = "Starting download...";

        var observer = new StatusObserver(text => _post(() => SourceStatus = text));
        SourceAcquisitionResult result;
        try
        {
            result = await Task.Run(() =>
                _sourceProvider.EnsureAsync(new SourceRequest(CoreInfo.Version), observer, CancellationToken.None));
        }
        catch (Exception ex)
        {
            result = SourceAcquisitionResult.Failure(FailureReason.SourceUnavailable, ex.Message);
        }

        _post(() =>
        {
            AcquiringSource = false;
            if (result.Ok && result.RepoRoot is not null)
            {
                _repoRoot = result.RepoRoot;
                SourceStatus = "Source ready.";
                Rescan();
            }
            else
            {
                SourceError = result.Message ?? "The download failed.";
                SourceStatus = string.Empty;
            }
        });
    }

    public string Summary => RepoRootMissing
        ? SourceError is not null
            ? $"Could not download the Optimum source: {SourceError}"
            : _sourceProvider is not null
                ? "Optimum source is not on this machine. It will be downloaded from GitHub."
                : "Run the installer from inside an Optimum checkout."
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

    /// <summary>Bridges the source download's progress to a single status line.</summary>
    private sealed class StatusObserver(Action<string> onText) : IBuildObserver
    {
        public void Phase(ProgressPhase phase, int percent, string detail) => onText(detail);

        public void Log(LogLevel level, string message) => onText(message);

        public void RawOutput(bool isError, string line)
        {
            string trimmed = line.Trim();
            if (trimmed.Length > 0)
                onText(trimmed);
        }
    }
}
