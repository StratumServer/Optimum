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

    public MainWindowViewModel(InstallerServices services)
    {
        _services = services;
        _repoRoot = services.RepoRoot;

        Action<Action> post = _services.UiPost ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        Prerequisites = new PrerequisitesViewModel(
            services.Probe, services.RepoRoot, services.SourceProvider, services.Appimagetool, post);
        Prerequisites.ContinueRequested += root =>
        {
            _repoRoot = root;
            Options = BuildOptions();
            CurrentScreen = WizardScreen.Options;
        };

        Options = BuildOptions();

        Eula = new EulaViewModel();
        Eula.DeclineRequested += () => CurrentScreen = WizardScreen.Options;
        Eula.AcceptRequested += () => InstallCompletion = StartInstallAsync();

        if (_services.Updates is { } updates)
            _ = CheckForUpdateAsync(updates);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatePromptVisible))]
    private UpdateBannerViewModel? _updateBanner;

    /// <summary>
    /// The self-update banner only shows before the build starts. Once Progress
    /// is running, restarting the app for an update would abandon a half-written
    /// install.
    /// </summary>
    public bool UpdatePromptVisible =>
        UpdateBanner is not null
        && CurrentScreen is WizardScreen.Prerequisites or WizardScreen.Options or WizardScreen.Review;

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
    [NotifyPropertyChangedFor(nameof(CurrentStepLabel))]
    [NotifyPropertyChangedFor(nameof(CurrentStepTitle))]
    [NotifyPropertyChangedFor(nameof(CurrentStepDescription))]
    [NotifyPropertyChangedFor(nameof(CurrentStepProgress))]
    private WizardScreen _currentScreen = WizardScreen.Prerequisites;

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
        WizardScreen.Options => "Choose how Optimum is installed",
        WizardScreen.Review => "Review before installing",
        WizardScreen.Progress => "Installing Optimum",
        WizardScreen.Completion when Completion?.Succeeded == true => "Installation complete",
        WizardScreen.Completion => "Installation needs attention",
        _ => "Optimum installer",
    };

    public string CurrentStepDescription => CurrentScreen switch
    {
        WizardScreen.Prerequisites => "Make sure the required source and tools are ready.",
        WizardScreen.Options => "Choose the destination, game data, version, and shortcuts.",
        WizardScreen.Review => "Confirm your choices and accept the local build notice.",
        WizardScreen.Progress => "Keep this window open while Optimum is built and installed.",
        WizardScreen.Completion when Completion?.Succeeded == true => "Optimum is ready to launch.",
        WizardScreen.Completion => "Review the message below, then retry when you are ready.",
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

    private void OnBuildFinished(InstallOutcome outcome)
    {
        var completion = new CompletionViewModel(outcome);
        completion.RetryRequested += RestartFromPrerequisites;
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
