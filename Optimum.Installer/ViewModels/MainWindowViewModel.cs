using CommunityToolkit.Mvvm.ComponentModel;
using Optimum.Bootstrap.Core.Install;
using Optimum.Installer.Services;

namespace Optimum.Installer.ViewModels;

public enum WizardScreen
{
    Prerequisites,
    Options,
    Review,
    Progress,
    Completion,
}

public enum StepState
{
    Done,
    Current,
    Upcoming,
}

/// <summary>One layer of the step rail (INSTALLER-PLAN.md section 5).</summary>
public sealed record WizardStep(int Number, string Name, StepState State)
{
    public bool IsCurrent => State == StepState.Current;
    public bool IsDone => State == StepState.Done;

    /// <summary>Current or already done: the step the rail fills in.</summary>
    public bool IsReached => State is StepState.Current or StepState.Done;
}

/// <summary>
/// The wizard shell and its state machine (INSTALLER-PLAN.md section 5).
/// Prerequisites, Options, and Review allow the user to move backward; navigation
/// locks once Progress starts. Each screen view model raises the transition it
/// wants, and the shell decides whether to honour it.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly InstallerServices _services;
    private bool _installStarted;
    private string? _repoRoot;
    private string? _optionsRepoRoot;

    public MainWindowViewModel(InstallerServices services)
    {
        _services = services;
        _repoRoot = services.RepoRoot;

        Action<Action> post = _services.UiPost ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        Prerequisites = new PrerequisitesViewModel(
            services.Probe, services.RepoRoot, services.SourceProvider, services.Appimagetool, post,
            services.Sdk, services.Ilspycmd);
        Prerequisites.ContinueRequested += root =>
        {
            // Keep the user's Options choices when they step back to Prerequisites
            // and forward again. Only rebuild if the resolved source root changed
            // (a mid-wizard source download) or a retry cleared the screen.
            if (Options is null || _optionsRepoRoot != root)
            {
                _repoRoot = root;
                Options = BuildOptions();
            }
            CurrentScreen = WizardScreen.Options;
        };

        Options = BuildOptions();

        Eula = new EulaViewModel();
        Eula.DeclineRequested += () => CurrentScreen = WizardScreen.Options;
        Eula.AcceptRequested += () =>
        {
            // A second accept must not replace the in-flight task with a
            // completed one; keep the first run as the awaitable.
            if (!_installStarted)
                InstallCompletion = StartInstallAsync();
        };

        if (_services.Updates is { } updates)
            _ = CheckForUpdateAsync(updates);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatePromptVisible))]
    private UpdateBannerViewModel? _updateBanner;

    /// <summary>
    /// The self-update banner only shows on the two screens before consent. Once
    /// the user is reviewing the notice or a build is running, restarting the app
    /// for an update would interrupt consent or abandon a half-written install.
    /// It also hides once the user dismisses it.
    /// </summary>
    public bool UpdatePromptVisible =>
        UpdateBanner is { Dismissed: false }
        && CurrentScreen is WizardScreen.Prerequisites or WizardScreen.Options;

    partial void OnUpdateBannerChanged(UpdateBannerViewModel? value)
    {
        if (value is not null)
            value.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(UpdateBannerViewModel.Dismissed))
                    OnPropertyChanged(nameof(UpdatePromptVisible));
            };
    }

    private async Task CheckForUpdateAsync(IUpdateService updates)
    {
        string? version = await updates.CheckAsync();
        if (version is not null)
        {
            Action<Action> post = _services.UiPost ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
            post(() => UpdateBanner = new UpdateBannerViewModel(updates, version, post));
        }
    }

    /// <summary>The running (or finished) install, so a test can await it.</summary>
    internal Task InstallCompletion { get; private set; } = Task.CompletedTask;

    public string Title { get; } = "Optimum installer";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentViewModel))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    [NotifyPropertyChangedFor(nameof(UpdatePromptVisible))]
    [NotifyPropertyChangedFor(nameof(IsEulaOpen))]
    [NotifyPropertyChangedFor(nameof(CurrentStepNumber))]
    [NotifyPropertyChangedFor(nameof(StepIndex))]
    [NotifyPropertyChangedFor(nameof(CurrentStepLabel))]
    [NotifyPropertyChangedFor(nameof(CurrentStepTitle))]
    [NotifyPropertyChangedFor(nameof(CurrentStepDescription))]
    [NotifyPropertyChangedFor(nameof(CurrentStepProgress))]
    [NotifyPropertyChangedFor(nameof(Steps))]
    [NotifyPropertyChangedFor(nameof(HeaderVisible))]
    private WizardScreen _currentScreen = WizardScreen.Prerequisites;

    private static readonly string[] StepNames = ["System", "Options", "Review", "Install"];

    /// <summary>The rail labels for <c>suki:VerticalStepper</c>.</summary>
    public IReadOnlyList<string> StepLabels => StepNames;

    /// <summary>Zero-based current step for <c>suki:VerticalStepper.Index</c>.
    /// On a successful completion it points past the last step so the rail
    /// shows every step done rather than the last one still "current".</summary>
    public int StepIndex =>
        CurrentScreen == WizardScreen.Completion && Completion?.Succeeded == true
            ? StepNames.Length
            : CurrentStepNumber - 1;

    /// <summary>The four rail layers, restated on every screen change.</summary>
    public IReadOnlyList<WizardStep> Steps
    {
        get
        {
            int current = CurrentStepNumber;
            bool installed = CurrentScreen == WizardScreen.Completion && Completion?.Succeeded == true;
            var steps = new WizardStep[StepNames.Length];
            for (int i = 0; i < StepNames.Length; i++)
            {
                int number = i + 1;
                StepState state = installed || number < current ? StepState.Done
                    : number == current ? StepState.Current
                    : StepState.Upcoming;
                steps[i] = new WizardStep(number, StepNames[i], state);
            }
            return steps;
        }
    }

    /// <summary>The content header is hidden once the wizard resolves into Completion.</summary>
    public bool HeaderVisible => CurrentScreen != WizardScreen.Completion;

    /// <summary>Compatibility name for callers that need to know whether consent is active.</summary>
    public bool IsEulaOpen => CurrentScreen == WizardScreen.Review;

    public int CurrentStepNumber => CurrentScreen switch
    {
        WizardScreen.Prerequisites => 1,
        WizardScreen.Options => 2,
        WizardScreen.Review => 3,
        _ => 4,
    };

    public string CurrentStepLabel => $"Step {CurrentStepNumber} of 4";

    public string CurrentStepTitle => CurrentScreen switch
    {
        WizardScreen.Prerequisites => "Check your system",
        WizardScreen.Options => "Set up the install",
        WizardScreen.Review => "Review and consent",
        WizardScreen.Progress => "Building Optimum",
        WizardScreen.Completion when Completion?.Succeeded == true => "Optimum is installed",
        WizardScreen.Completion => "The install stopped",
        _ => "Optimum installer",
    };

    public string CurrentStepDescription => CurrentScreen switch
    {
        WizardScreen.Prerequisites => "The tools and source Optimum needs to build on this computer.",
        WizardScreen.Options => "Where Optimum goes, the game data it uses, and the shortcuts to add.",
        WizardScreen.Review => "Confirm the summary, then read and accept the build notice.",
        WizardScreen.Progress => "This runs on your computer and can take a few minutes the first time.",
        WizardScreen.Completion when Completion?.Succeeded == true => "Optimum is ready to launch.",
        WizardScreen.Completion => "Nothing on your system was changed. Fix the issue below, then try again.",
        _ => string.Empty,
    };

    public double CurrentStepProgress => CurrentScreen switch
    {
        WizardScreen.Prerequisites => 25,
        WizardScreen.Options => 50,
        WizardScreen.Review => 75,
        WizardScreen.Progress => 90,
        _ => 100,
    };

    public PrerequisitesViewModel Prerequisites { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentViewModel))]
    private OptionsViewModel _options = null!;

    public EulaViewModel Eula { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentViewModel))]
    private ProgressViewModel? _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentViewModel))]
    [NotifyPropertyChangedFor(nameof(Steps))]
    [NotifyPropertyChangedFor(nameof(StepIndex))]
    private CompletionViewModel? _completion;

    public bool CanGoBack => CurrentScreen == WizardScreen.Options;

    public ViewModelBase CurrentViewModel => CurrentScreen switch
    {
        WizardScreen.Options => Options,
        WizardScreen.Review => Eula,
        WizardScreen.Progress => Progress ?? (ViewModelBase)Prerequisites,
        WizardScreen.Completion => Completion ?? (ViewModelBase)Prerequisites,
        _ => Prerequisites,
    };

    private OptionsViewModel BuildOptions()
    {
        _optionsRepoRoot = _repoRoot;
        var options = new OptionsViewModel(_services.Probe, _repoRoot);
        options.BackRequested += () => CurrentScreen = WizardScreen.Prerequisites;
        options.ContinueRequested += OpenReview;
        return options;
    }

    private void OpenReview()
    {
        Eula.Configure(
            Options.InstallDirectory,
            Options.ResolvedDataPath,
            Options.SelectedVersion,
            Options.CreateMenuEntry,
            Options.CreateDesktopShortcut);
        Eula.Reset();
        CurrentScreen = WizardScreen.Review;
    }

    private async Task StartInstallAsync()
    {
        if (_installStarted || CurrentScreen != WizardScreen.Review || !Eula.CanAccept || _repoRoot is null)
            return;
        _installStarted = true;

        var session = new InstallSession(
            _repoRoot,
            Options.InstallDirectory,
            Options.ResolvedDataPath,
            Options.SelectedVersion,
            (Options.CreateMenuEntry ? ShortcutKinds.Menu : ShortcutKinds.None)
                | (Options.CreateDesktopShortcut ? ShortcutKinds.Desktop : ShortcutKinds.None));

        var progress = new ProgressViewModel(_services, session, _services.UiPost);
        progress.Finished += OnBuildFinished;
        Progress = progress;
        CurrentScreen = WizardScreen.Progress;

        await progress.RunAsync();
    }

    /// <summary>Raised when the shell should close (the launched game is up).</summary>
    public event Action? ExitRequested;

    private void OnBuildFinished(InstallOutcome outcome)
    {
        var completion = new CompletionViewModel(outcome);
        completion.RetryRequested += RestartFromPrerequisites;
        completion.ExitRequested += () => ExitRequested?.Invoke();
        Completion = completion;
        Progress = null;
        CurrentScreen = WizardScreen.Completion;
    }

    private void RestartFromPrerequisites()
    {
        _installStarted = false;
        Prerequisites.Rescan();
        Options = BuildOptions();
        Progress = null;
        Completion = null;
        CurrentScreen = WizardScreen.Prerequisites;
    }
}
