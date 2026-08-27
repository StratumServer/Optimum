using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Optimum.Installer.ViewModels;

public sealed partial class CompletionViewModel(InstallOutcome outcome) : ViewModelBase
{
    public InstallOutcome Outcome { get; } = outcome;

    public bool Succeeded => Outcome.Succeeded;
    public bool Cancelled => Outcome.Cancelled;

    /// <summary>Anything that did not succeed can be retried, cancelled runs included.</summary>
    public bool CanRetry => !Outcome.Succeeded;

    public string Headline => Outcome.Succeeded
        ? "Optimum is installed."
        : Outcome.Cancelled
            ? "The install was cancelled."
            : "The install did not finish.";

    public string Message => Outcome.Message;
    public string? InstallDirectory => Outcome.InstallDirectory;

    public bool CanLaunch => Outcome.Launcher is not null && File.Exists(Outcome.Launcher);

    public bool HasLog => File.Exists(Outcome.RawLogPath);

    public event Action? RetryRequested;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry() => RetryRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanLaunch))]
    private void Launch()
    {
        if (Outcome.Launcher is not { } launcher)
            return;

        // Run the launcher script directly. UseShellExecute would route a .sh
        // through xdg-open on Linux, which opens it in an editor rather than
        // running it.
        ProcessStartInfo start = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", $"/c \"{launcher}\"") { UseShellExecute = false, CreateNoWindow = true }
            : new ProcessStartInfo(launcher) { UseShellExecute = false };
        start.WorkingDirectory = Path.GetDirectoryName(launcher) ?? Environment.CurrentDirectory;
        Process.Start(start);
    }

    [RelayCommand(CanExecute = nameof(HasLog))]
    private void ViewLog()
    {
        Process.Start(new ProcessStartInfo(Outcome.RawLogPath) { UseShellExecute = true });
    }
}
