using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Optimum.Installer.ViewModels;

public sealed partial class CompletionViewModel(InstallOutcome outcome) : ViewModelBase
{
    public InstallOutcome Outcome { get; } = outcome;

    public bool Succeeded => Outcome.Succeeded;
    public bool Failed => !Outcome.Succeeded && !Outcome.Cancelled;
    public bool Cancelled => Outcome.Cancelled;

    public string Headline => Outcome.Succeeded
        ? "Optimum is installed."
        : Outcome.Cancelled
            ? "The install was cancelled."
            : "The install did not finish.";

    public string Message => Outcome.Message;
    public string? InstallDirectory => Outcome.InstallDirectory;

    public bool CanLaunch => Outcome.Launcher is not null && File.Exists(Outcome.Launcher);

    public event Action? RetryRequested;

    [RelayCommand]
    private void Retry() => RetryRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private void Launch()
    {
        if (Outcome.Launcher is null)
            return;
        Process.Start(new ProcessStartInfo(Outcome.Launcher) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ViewLog()
    {
        if (File.Exists(Outcome.RawLogPath))
            Process.Start(new ProcessStartInfo(Outcome.RawLogPath) { UseShellExecute = true });
    }
}
