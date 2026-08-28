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
        ? "Optimum is installed"
        : Outcome.Cancelled
            ? "The install was cancelled"
            : "The install stopped";

    /// <summary>A line that adds to the headline rather than repeating it.</summary>
    public string Subtext => Outcome.Succeeded
        ? "Launch it from the button below, or from your application menu."
        : Outcome.Message;

    public string Message => Outcome.Message;
    public string? InstallDirectory => Outcome.InstallDirectory;

    public bool CanLaunch => Outcome.Launcher is not null && File.Exists(Outcome.Launcher);

    public bool HasLog => File.Exists(Outcome.RawLogPath);

    /// <summary>The log button is only useful when something went wrong.</summary>
    public bool ShowLog => HasLog && !Outcome.Succeeded;

    public event Action? RetryRequested;

    /// <summary>Raised once the launched game has a window; the shell then exits.</summary>
    public event Action? ExitRequested;

    /// <summary>True from the moment Launch is clicked until the shell closes.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LaunchCommand))]
    private bool _launching;

    public string LaunchLabel => Launching ? "Launching Optimum..." : "Launch Optimum";

    partial void OnLaunchingChanged(bool value) => OnPropertyChanged(nameof(LaunchLabel));

    [RelayCommand(CanExecute = nameof(CanRetry))]
    private void Retry() => RetryRequested?.Invoke();

    private bool CanLaunchNow => CanLaunch && !Launching;

    [RelayCommand(CanExecute = nameof(CanLaunchNow))]
    private async Task Launch()
    {
        if (Outcome.Launcher is not { } launcher || Launching)
            return;

        Launching = true;
        try
        {
            // Run the launcher script directly. UseShellExecute would route a .sh
            // through xdg-open on Linux, which opens it in an editor rather than
            // running it.
            ProcessStartInfo start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe", $"/c \"{launcher}\"") { UseShellExecute = false, CreateNoWindow = true }
                : new ProcessStartInfo(launcher) { UseShellExecute = false };
            start.WorkingDirectory = Path.GetDirectoryName(launcher) ?? Environment.CurrentDirectory;
            Process.Start(start);

            await WaitForGameWindowAsync();
        }
        catch (Exception)
        {
            // Let the user try again rather than leaving a dead spinner.
            Launching = false;
            return;
        }

        ExitRequested?.Invoke();
    }

    /// <summary>
    /// Holds the spinner until the launched game has put a window up. On Windows
    /// this polls the Optimum process for a main window handle; elsewhere there
    /// is no cheap window probe, so it waits a short fixed moment. Either way it
    /// gives up after 45s and closes the installer anyway.
    /// </summary>
    private static async Task WaitForGameWindowAsync()
    {
        if (!OperatingSystem.IsWindows())
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            return;
        }

        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            if (await Task.Run(() => HasVisibleWindow("Optimum") || HasVisibleWindow("Vintagestory")))
                return;
            await Task.Delay(400);
        }
    }

    private static bool HasVisibleWindow(string processName)
    {
        foreach (Process process in Process.GetProcessesByName(processName))
        {
            try
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero)
                    return true;
            }
            catch (Exception)
            {
                // Access denied / exited between the enumerate and the read.
            }
            finally
            {
                process.Dispose();
            }
        }
        return false;
    }

    [RelayCommand(CanExecute = nameof(HasLog))]
    private void ViewLog()
    {
        Process.Start(new ProcessStartInfo(Outcome.RawLogPath) { UseShellExecute = true });
    }
}
