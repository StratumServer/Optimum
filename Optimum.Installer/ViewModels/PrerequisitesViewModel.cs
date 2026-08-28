using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Acquisition;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;

namespace Optimum.Installer.ViewModels;

/// <summary>
/// What a prerequisite row can do about a missing tool, resolved from its
/// <see cref="AcquisitionKind"/>: run an installer in place, open a download
/// page, or (for a distro / nix command) just surface the command to copy.
/// </summary>
public sealed class PrerequisiteRowActions
{
    /// <summary>Runs the in-place installer and streams into the observer.</summary>
    public Func<IBuildObserver, Task<ToolAcquisitionResult>>? Install { get; init; }

    /// <summary>Opens the tool's download page in the browser.</summary>
    public Action? OpenPage { get; init; }

    /// <summary>Marshals a callback to the UI thread.</summary>
    public Action<Action>? Post { get; init; }

    /// <summary>Called after a successful in-place install so the screen can rescan.</summary>
    public Action? OnInstalled { get; init; }
}

/// <summary>
/// A single prerequisite row. Not a <see cref="ViewModelBase"/> so the
/// <see cref="ViewLocator"/> never tries to resolve a view for it: it is only
/// ever rendered through an explicit item template.
/// </summary>
public sealed class PrerequisiteRowViewModel : ObservableObject
{
    private readonly PrerequisiteRowActions? _actions;
    private bool _installing;
    private string? _actionStatus;

    public PrerequisiteRowViewModel(PrerequisiteResult result, PrerequisiteRowActions? actions = null)
    {
        Result = result;
        _actions = actions;
        if (HasAction)
            ActionCommand = new AsyncRelayCommand(RunActionAsync, () => !Installing);
    }

    public PrerequisiteResult Result { get; }
    public IAsyncRelayCommand? ActionCommand { get; }

    private bool HasAction => _actions?.Install is not null || _actions?.OpenPage is not null;

    public string Name => Result.Definition.DisplayName;
    public string StatusLabel => Result.State switch
    {
        PrerequisiteState.Ok => "Ready",
        PrerequisiteState.OptionalMissing => "Optional",
        PrerequisiteState.Outdated => "Update needed",
        _ => "Required",
    };

    public bool IsReady => Result.State == PrerequisiteState.Ok;
    public bool IsOptional => Result.State == PrerequisiteState.OptionalMissing;
    public bool NeedsAttention => Result.State is PrerequisiteState.Missing or PrerequisiteState.Outdated;

    public string Detail => ActionStatus ?? Result.State switch
    {
        PrerequisiteState.Ok when Result.DetectedVersion is { Length: > 0 } version =>
            $"Version {version}. Used for {Result.Definition.UsedBy}.",
        PrerequisiteState.Ok => $"Available for {Result.Definition.UsedBy}.",
        PrerequisiteState.OptionalMissing => $"Only needed for {Result.Definition.UsedBy}.",
        PrerequisiteState.Outdated => $"Install a supported version, then check again. Used for {Result.Definition.UsedBy}.",
        _ => $"Install this tool, then check again. Used for {Result.Definition.UsedBy}.",
    };

    /// <summary>The action button label, or null when there is nothing to do.</summary>
    public string? ActionLabel
    {
        get
        {
            if (_actions?.Install is not null)
                return Installing ? "Installing..." : "Install";
            if (_actions?.OpenPage is not null)
                return "Get it";
            return null;
        }
    }

    /// <summary>A copyable distro / nix command, shown inline when there is one.</summary>
    public string? ManualCommand =>
        Result.Acquisition == AcquisitionKind.Manual ? Result.AcquisitionCommand : null;

    public bool Installing
    {
        get => _installing;
        private set
        {
            if (!SetProperty(ref _installing, value))
                return;
            OnPropertyChanged(nameof(ActionLabel));
            (ActionCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
        }
    }

    public string? ActionDetail => ActionStatus ?? Result.AcquisitionCommand ?? Result.DownloadUrl;

    public string? ActionStatus
    {
        get => _actionStatus;
        private set
        {
            if (SetProperty(ref _actionStatus, value))
            {
                OnPropertyChanged(nameof(Detail));
                OnPropertyChanged(nameof(ActionDetail));
            }
        }
    }

    private async Task RunActionAsync()
    {
        if (_actions is null || Installing)
            return;

        if (_actions.Install is null)
        {
            _actions.OpenPage?.Invoke();
            return;
        }

        Installing = true;
        ActionStatus = $"Installing {Name}...";
        try
        {
            var observer = new RowObserver(this, _actions.Post);
            ToolAcquisitionResult result = await _actions.Install(observer);
            ActionStatus = result.Ok ? "Installed." : result.Message ?? "Installation failed.";
            if (result.Ok)
                _actions.OnInstalled?.Invoke();
        }
        catch (Exception ex)
        {
            ActionStatus = $"Installation failed: {ex.Message}";
        }
        finally
        {
            Installing = false;
        }
    }

    /// <summary>Pipes an installer's last line of output into the row detail.</summary>
    private sealed class RowObserver(PrerequisiteRowViewModel row, Action<Action>? post) : IBuildObserver
    {
        private void Show(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;
            if (post is null)
                row.ActionStatus = text.Trim();
            else
                post(() => row.ActionStatus = text.Trim());
        }

        public void Phase(ProgressPhase phase, int percent, string detail) => Show(detail);

        public void Log(LogLevel level, string message) => Show(message);

        public void RawOutput(bool isError, string line) => Show(line);
    }
}

public sealed partial class PrerequisitesViewModel : ViewModelBase
{
    private readonly ISystemProbe _probe;
    private readonly ISourceProvider? _sourceProvider;
    private readonly IAppimagetoolAcquisition? _appimagetool;
    private readonly ISdkAcquisition? _sdk;
    private readonly IIlspycmdAcquisition? _ilspycmd;
    private readonly Action<Action> _post;
    private string? _repoRoot;

    public PrerequisitesViewModel(
        ISystemProbe probe,
        string? repoRoot,
        ISourceProvider? sourceProvider = null,
        IAppimagetoolAcquisition? appimagetool = null,
        Action<Action>? uiPost = null,
        ISdkAcquisition? sdk = null,
        IIlspycmdAcquisition? ilspycmd = null)
    {
        _probe = probe;
        _repoRoot = repoRoot;
        _sourceProvider = sourceProvider;
        _appimagetool = appimagetool;
        _sdk = sdk;
        _ilspycmd = ilspycmd;
        _post = uiPost ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        Rescan();
    }

    public ObservableCollection<PrerequisiteRowViewModel> Rows { get; } = [];

    /// <summary>Rows that need a decision: missing, outdated, or an optional tool
    /// the user can install now. The rest are folded into <see cref="ReadySummary"/>.</summary>
    public ObservableCollection<PrerequisiteRowViewModel> AttentionRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReadyTools))]
    private string _readySummary = string.Empty;

    public bool HasReadyTools => ReadySummary.Length > 0;

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
            ? Rows.Any(row => row.IsOptional)
                ? "Ready to continue. Optional tools can be added now or later."
                : "Your system is ready for Optimum."
            : BlockingCount == 1
                ? "One required tool needs attention before you can continue."
                : $"{BlockingCount} required tools need attention before you can continue.";

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
        AttentionRows.Clear();
        var results = new PrerequisiteScanner(_probe, _repoRoot).Scan();
        var ready = new List<string>();
        foreach (var result in results)
        {
            var row = new PrerequisiteRowViewModel(result, ActionsFor(result));
            Rows.Add(row);

            if (result.State == PrerequisiteState.Ok)
                ready.Add(result.Definition.DisplayName);
            else
                AttentionRows.Add(row);
        }
        BlockingCount = results.Count(r => r.BlocksBuild);
        ReadySummary = ready.Count switch
        {
            0 => string.Empty,
            1 => $"{ready[0]} is ready.",
            _ => $"{ready.Count} tools ready: {string.Join(", ", ready)}.",
        };
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>
    /// Maps a scan result to what the row can do about it. <c>Automatic</c> wires
    /// the matching in-place installer; <c>DownloadPage</c> wires a browser open;
    /// <c>Manual</c> and <c>None</c> get no button (Manual shows its command
    /// inline).
    /// </summary>
    private PrerequisiteRowActions? ActionsFor(PrerequisiteResult result)
    {
        Func<IBuildObserver, Task<ToolAcquisitionResult>>? install = result.Acquisition switch
        {
            AcquisitionKind.Automatic => InstallerFor(result.Definition.Id),
            _ => null,
        };

        Action? openPage = result.Acquisition == AcquisitionKind.DownloadPage
            && result.DownloadUrl is { Length: > 0 } url
            ? () => OpenUrl(url)
            : null;

        if (install is null && openPage is null)
            return null;

        return new PrerequisiteRowActions
        {
            Install = install,
            OpenPage = openPage,
            Post = _post,
            OnInstalled = () => _post(Rescan),
        };
    }

    private Func<IBuildObserver, Task<ToolAcquisitionResult>>? InstallerFor(PrerequisiteId id) => id switch
    {
        PrerequisiteId.Appimagetool when _appimagetool is not null && _repoRoot is not null =>
            obs => _appimagetool.InstallAsync(_repoRoot, obs, CancellationToken.None),
        PrerequisiteId.Dotnet when _sdk is not null && _repoRoot is not null =>
            obs => _sdk.InstallAsync(_repoRoot, obs, CancellationToken.None),
        PrerequisiteId.Ilspycmd when _ilspycmd is not null && _repoRoot is not null =>
            obs => _ilspycmd.InstallAsync(_repoRoot, obs, CancellationToken.None),
        _ => null,
    };

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Best effort: a headless or locked-down host has no browser to open.
        }
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
