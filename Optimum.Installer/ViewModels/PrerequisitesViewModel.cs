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

    /// <summary>Runs an installer that can be cancelled by the user.</summary>
    public Func<IBuildObserver, CancellationToken, Task<ToolAcquisitionResult>>? InstallCancellable { get; init; }

    /// <summary>Opens the tool's download page in the browser.</summary>
    public Action? OpenPage { get; init; }

    /// <summary>Opens a manual fallback while automatic acquisition is available.</summary>
    public Action? OpenManualPage { get; init; }

    /// <summary>Marshals a callback to the UI thread.</summary>
    public Action<Action>? Post { get; init; }

    /// <summary>Called after a successful in-place install so the screen can rescan.</summary>
    public Action? OnInstalled { get; init; }

    /// <summary>Requires a separate confirmation before invoking a system package manager.</summary>
    public bool RequiresConfirmation { get; init; }

    /// <summary>Exact package and command shown in the confirmation step.</summary>
    public string? ConfirmationDetail { get; init; }
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
    private CancellationTokenSource? _installCancellation;
    private bool _manualFallbackAvailable;
    private bool _awaitingConfirmation;

    public PrerequisiteRowViewModel(PrerequisiteResult result, PrerequisiteRowActions? actions = null)
    {
        Result = result;
        _actions = actions;
        if (HasAction)
            ActionCommand = new AsyncRelayCommand(RunActionAsync, () => !Installing);
        if (_actions?.InstallCancellable is not null)
            CancelInstallCommand = new RelayCommand(CancelInstall, () => Installing);
        if (_actions?.RequiresConfirmation == true)
        {
            ConfirmInstallCommand = new AsyncRelayCommand(ConfirmInstallAsync, () => AwaitingConfirmation && !Installing);
            CancelConfirmationCommand = new RelayCommand(CancelConfirmation, () => AwaitingConfirmation);
        }
        if (_actions?.OpenManualPage is not null)
            ManualDownloadCommand = new RelayCommand(() => _actions.OpenManualPage());
    }

    public PrerequisiteResult Result { get; }
    public IAsyncRelayCommand? ActionCommand { get; }
    public IRelayCommand? CancelInstallCommand { get; }
    public IAsyncRelayCommand? ConfirmInstallCommand { get; }
    public IRelayCommand? CancelConfirmationCommand { get; }
    public IRelayCommand? ManualDownloadCommand { get; }

    private bool HasAction => _actions?.Install is not null
        || _actions?.InstallCancellable is not null || _actions?.OpenPage is not null;

    public string Name => Result.Definition.DisplayName;
    public string StatusLabel => Result.State switch
    {
        PrerequisiteState.Ok => "Ready",
        PrerequisiteState.OptionalMissing => "Optional",
        PrerequisiteState.Outdated => "Update needed",
        PrerequisiteState.Missing when Result.Definition.Level == RequirementLevel.RequiredForPackaging => "Packaging",
        _ => "Required",
    };

    public bool IsReady => Result.State == PrerequisiteState.Ok;
    public bool IsOptional => Result.State == PrerequisiteState.OptionalMissing;
    public bool NeedsAttention => Result.State is PrerequisiteState.Missing or PrerequisiteState.Outdated;
    public bool CanCancelInstall => Installing && _actions?.InstallCancellable is not null;
    public bool AwaitingConfirmation
    {
        get => _awaitingConfirmation;
        private set
        {
            if (!SetProperty(ref _awaitingConfirmation, value))
                return;
            OnPropertyChanged(nameof(CanRequestInstall));
            (ConfirmInstallCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            (CancelConfirmationCommand as RelayCommand)?.NotifyCanExecuteChanged();
        }
    }
    public bool CanRequestInstall => !AwaitingConfirmation && !Installing;
    public string? ConfirmationDetail => _actions?.ConfirmationDetail;

    public string Detail => ActionStatus ?? Result.Guidance ?? (Result.State switch
    {
        PrerequisiteState.Ok when Result.DetectedVersion is { Length: > 0 } version =>
            $"Version {version}. Used for {Result.Definition.UsedBy}.",
        PrerequisiteState.Ok => $"Available for {Result.Definition.UsedBy}.",
        PrerequisiteState.OptionalMissing => $"Only needed for {Result.Definition.UsedBy}.",
        PrerequisiteState.Outdated => $"Install a supported version, then check again. Used for {Result.Definition.UsedBy}.",
        PrerequisiteState.Missing when Result.Definition.Id == PrerequisiteId.Git
            && Result.Acquisition == AcquisitionKind.Automatic
            && Result.AcquisitionCommand is { Length: > 0 } instructions => instructions,
        _ => $"Install this tool, then check again. Used for {Result.Definition.UsedBy}.",
    });

    /// <summary>The action button label, or null when there is nothing to do.</summary>
    public string? ActionLabel
    {
        get
        {
            if (_actions?.RequiresConfirmation == true)
                return "Review install";
            if (_actions?.Install is not null || _actions?.InstallCancellable is not null)
                return Installing ? "Installing..." : "Install";
            if (_actions?.OpenPage is not null)
                return "Get it";
            return null;
        }
    }

    public string? ManualFallbackLabel => _manualFallbackAvailable && _actions?.OpenManualPage is not null
        ? "Manual download"
        : null;

    /// <summary>A copyable distro / nix command, shown inline when there is one.</summary>
    public string? ManualCommand =>
        Result.Acquisition is AcquisitionKind.Manual or AcquisitionKind.None
            ? Result.AcquisitionCommand : null;

    public bool Installing
    {
        get => _installing;
        private set
        {
            if (!SetProperty(ref _installing, value))
                return;
            OnPropertyChanged(nameof(ActionLabel));
            OnPropertyChanged(nameof(CanCancelInstall));
            (ActionCommand as AsyncRelayCommand)?.NotifyCanExecuteChanged();
            (CancelInstallCommand as RelayCommand)?.NotifyCanExecuteChanged();
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

        if (_actions.RequiresConfirmation)
        {
            AwaitingConfirmation = true;
            ActionStatus = null;
            return;
        }

        await InstallAsync();
    }

    private async Task ConfirmInstallAsync()
    {
        if (!AwaitingConfirmation || Installing)
            return;
        AwaitingConfirmation = false;
        await InstallAsync();
    }

    private void CancelConfirmation()
    {
        AwaitingConfirmation = false;
        ActionStatus = "Installation was not started. You can use the command shown below.";
    }

    private async Task InstallAsync()
    {
        if (_actions is null || Installing)
            return;

        if (_actions.Install is null && _actions.InstallCancellable is null)
        {
            _actions.OpenPage?.Invoke();
            return;
        }

        Installing = true;
        SetManualFallbackAvailable(false);
        _installCancellation = _actions.InstallCancellable is null ? null : new CancellationTokenSource();
        ActionStatus = $"Installing {Name}...";
        try
        {
            var observer = new RowObserver(this, _actions.Post);
            ToolAcquisitionResult result;
            if (_actions.InstallCancellable is { } cancellable)
            {
                CancellationToken token = _installCancellation!.Token;
                result = await Task.Run(() => cancellable(observer, token));
            }
            else
            {
                result = await _actions.Install!(observer);
            }
            SetManualFallbackAvailable(!result.Ok && result.Reason != FailureReason.Cancelled);
            ActionStatus = result.Ok ? "Installed." : result.Message ?? "Installation failed.";
            if (result.Ok)
                _actions.OnInstalled?.Invoke();
        }
        catch (Exception ex)
        {
            SetManualFallbackAvailable(ex is not OperationCanceledException);
            ActionStatus = $"Installation failed: {ex.Message}";
        }
        finally
        {
            CancellationTokenSource? cancellation = _installCancellation;
            _installCancellation = null;
            cancellation?.Dispose();
            Installing = false;
        }
    }

    private void CancelInstall() => _installCancellation?.Cancel();

    private void SetManualFallbackAvailable(bool available)
    {
        if (_manualFallbackAvailable == available)
            return;
        _manualFallbackAvailable = available;
        OnPropertyChanged(nameof(ManualFallbackLabel));
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
    private readonly IGitAcquisition? _git;
    private readonly IDistroPackageAcquisition? _distroPackages;
    private readonly Action<Action> _post;
    private string? _repoRoot;
    private bool _gitReadyForSource;

    public PrerequisitesViewModel(
        ISystemProbe probe,
        string? repoRoot,
        ISourceProvider? sourceProvider = null,
        IAppimagetoolAcquisition? appimagetool = null,
        Action<Action>? uiPost = null,
        ISdkAcquisition? sdk = null,
        IIlspycmdAcquisition? ilspycmd = null,
        IGitAcquisition? git = null,
        IDistroPackageAcquisition? distroPackages = null)
    {
        _probe = probe;
        _repoRoot = repoRoot;
        _sourceProvider = sourceProvider;
        _appimagetool = appimagetool;
        _sdk = sdk;
        _ilspycmd = ilspycmd;
        _git = git;
        _distroPackages = distroPackages;
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
    public bool CanAcquireSource => RepoRootMissing && !AcquiringSource && _sourceProvider is not null
        && (_probe.Os != OsKind.Windows || _git is null || _gitReadyForSource);

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
        if (_sourceProvider is null || !CanAcquireSource)
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
            AttentionRows.Clear();
            if (_probe.Os == OsKind.Windows)
            {
                PrerequisiteResult git = new PrerequisiteScanner(_probe, string.Empty).ScanGit();
                _gitReadyForSource = git.State == PrerequisiteState.Ok;
                if (git.State != PrerequisiteState.Ok)
                    AttentionRows.Add(new PrerequisiteRowViewModel(git, ActionsFor(git)));
            }
            else
            {
                _gitReadyForSource = true;
            }
            OnPropertyChanged(nameof(CanAcquireSource));
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
        OnPropertyChanged(nameof(CanAcquireSource));
    }

    /// <summary>
    /// Maps a scan result to what the row can do about it. <c>Automatic</c> wires
    /// the matching in-place installer; <c>DownloadPage</c> wires a browser open;
    /// supported <c>Manual</c> Linux package plans offer a confirmed install and
    /// always retain the copyable command.
    /// </summary>
    private PrerequisiteRowActions? ActionsFor(PrerequisiteResult result)
    {
        Func<IBuildObserver, Task<ToolAcquisitionResult>>? install = result.Acquisition switch
        {
            AcquisitionKind.Automatic => InstallerFor(result.Definition.Id),
            _ => null,
        };

        Action? openPage = result.Acquisition is AcquisitionKind.DownloadPage or AcquisitionKind.Manual
            && result.DownloadUrl is { Length: > 0 } url
            ? () => OpenUrl(url)
            : null;
        Func<IBuildObserver, CancellationToken, Task<ToolAcquisitionResult>>? installCancellable =
            result.Definition.Id == PrerequisiteId.Git && _git is not null
                ? (obs, token) => _git.InstallAsync(obs, token)
                : null;

        DistroPackagePlan? distroPlan = result.Acquisition == AcquisitionKind.Manual
            ? _distroPackages?.PlanFor(result.Definition.Command)
            : null;
        if (distroPlan is not null && _distroPackages is not null)
            installCancellable = (obs, token) => _distroPackages.InstallAsync(distroPlan, obs, token);

        if (install is null && installCancellable is null && openPage is null
            && distroPlan is null)
            return null;

        return new PrerequisiteRowActions
        {
            Install = install,
            InstallCancellable = installCancellable,
            RequiresConfirmation = distroPlan is not null,
            ConfirmationDetail = distroPlan is null ? null
                : $"Optimum will ask the system authorization service to install {distroPlan.Package} using {distroPlan.Manager}. The package manager command is: {distroPlan.ManualCommand}",
            OpenPage = openPage,
            OpenManualPage = result.Definition.Id == PrerequisiteId.Git
                && result.Acquisition == AcquisitionKind.Automatic
                && result.DownloadUrl is { Length: > 0 } manualUrl
                    ? () => OpenUrl(manualUrl)
                    : null,
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
