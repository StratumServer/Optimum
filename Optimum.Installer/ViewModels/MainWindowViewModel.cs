using CommunityToolkit.Mvvm.ComponentModel;
using Optimum.Bootstrap.Core.Install;
using Optimum.Installer.Services;

namespace Optimum.Installer.ViewModels;

public enum WizardScreen
{
    Prerequisites,
    Options,
    Progress,
    Completion,
}

/// <summary>
/// The wizard shell and its state machine (INSTALLER-PLAN.md section 5). The EULA
/// is a modal over Options, not a screen. Backward navigation is allowed from
/// Options to Prerequisites and blocked once Progress starts. Each screen view
/// model raises the transition it wants; the shell decides whether to honour it.
/// </summary>
public sealed partial class MainWindowViewModel : ViewModelBase
{
    private readonly InstallerServices _services;

    public MainWindowViewModel(InstallerServices services)
    {
        _services = services;

        Prerequisites = new PrerequisitesViewModel(services.Probe, services.RepoRoot);
        Prerequisites.ContinueRequested += () => CurrentScreen = WizardScreen.Options;

        Options = new OptionsViewModel(services.Probe, services.RepoRoot);
        Options.BackRequested += () => CurrentScreen = WizardScreen.Prerequisites;
        Options.ContinueRequested += OpenEula;

        Eula = new EulaViewModel();
        Eula.DeclineRequested += () => IsEulaOpen = false;
        Eula.AcceptRequested += () => InstallCompletion = StartInstallAsync();
    }

    /// <summary>The running (or finished) install, so a test can await it.</summary>
    internal Task InstallCompletion { get; private set; } = Task.CompletedTask;

    public string Title { get; } = "Optimum installer";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentViewModel))]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    private WizardScreen _currentScreen = WizardScreen.Prerequisites;

    [ObservableProperty]
    private bool _isEulaOpen;

    public PrerequisitesViewModel Prerequisites { get; }

    public OptionsViewModel Options { get; }

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
        WizardScreen.Progress => Progress ?? (ViewModelBase)Prerequisites,
        WizardScreen.Completion => Completion ?? (ViewModelBase)Prerequisites,
        _ => Prerequisites,
    };

    private void OpenEula()
    {
        Eula.Reset();
        IsEulaOpen = true;
    }

    private async Task StartInstallAsync()
    {
        if (!Eula.CanAccept || _services.RepoRoot is null)
            return;

        IsEulaOpen = false;

        var session = new InstallSession(
            _services.RepoRoot,
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
        CurrentScreen = WizardScreen.Completion;
    }

    private void RestartFromPrerequisites()
    {
        Prerequisites.Rescan();
        Progress = null;
        Completion = null;
        CurrentScreen = WizardScreen.Prerequisites;
    }
}
