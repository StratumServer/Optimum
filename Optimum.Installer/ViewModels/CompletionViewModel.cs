using System.Diagnostics;
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
        ? "Optimum is installed"
        : Outcome.Cancelled
            ? "The install was cancelled"
            : "The install stopped";

    /// <summary>A line that adds to the headline rather than repeating it.</summary>
    public string Subtext => Outcome.Succeeded
        ? "Launch Optimum from your application menu or the install folder."
        : Outcome.Message;

    public string Message => Outcome.Message;
    public string? InstallDirectory => Outcome.InstallDirectory;

    /// <summary>Offer to open the install folder only when there is one to open.</summary>
    public bool CanOpenFolder => Outcome.InstallDirectory is not null && Directory.Exists(Outcome.InstallDirectory);

    public bool HasLog => File.Exists(Outcome.RawLogPath);

    /// <summary>The log button is only useful when something went wrong.</summary>
    public bool ShowLog => HasLog && !Outcome.Succeeded;

    public event Action? RetryRequested;

    /// <summary>Raised when the user finishes the wizard; the shell then exits.</summary>
    public event Action? ExitRequested;

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry() => RetryRequested?.Invoke();

    /// <summary>Close the installer. Standard "Finish" button on the final screen.</summary>
    [RelayCommand]
    private void Finish() => ExitRequested?.Invoke();

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        if (Outcome.InstallDirectory is not { } dir)
            return;
        try
        {
            // Open the install directory in the platform file manager.
            ProcessStartInfo start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                : new ProcessStartInfo(dir) { UseShellExecute = true };
            Process.Start(start);
        }
        catch (Exception)
        {
            // Opening the folder is a convenience; ignore a failure rather than
            // breaking the finish screen.
        }
    }

    [RelayCommand(CanExecute = nameof(HasLog))]
    private void ViewLog()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Outcome.RawLogPath) { UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening the log is a convenience; ignore a failure rather than
            // breaking the finish screen.
        }
    }
}
