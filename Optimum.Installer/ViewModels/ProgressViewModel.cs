using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Install;
using Optimum.Installer.Services;

namespace Optimum.Installer.ViewModels;

public sealed record LogLine(string Level, string Text);

/// <summary>
/// Runs the build then the deploy under one progress bar, feeding the phase
/// label, the bar, an honest time estimate, and a filtered log pane. It is its
/// own <see cref="IBuildObserver"/> and marshals every callback to the UI thread.
/// </summary>
public sealed partial class ProgressViewModel : ViewModelBase, IBuildObserver
{
    private readonly InstallerServices _services;
    private readonly InstallSession _session;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly StringBuilder _rawLog = new();

    public ProgressViewModel(InstallerServices services, InstallSession session, Action<Action>? post = null)
    {
        _services = services;
        _session = session;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
    }

    public ObservableCollection<LogLine> Log { get; } = [];

    public event Action<InstallOutcome>? Finished;

    [ObservableProperty]
    private string _phaseLabel = "Starting";

    [ObservableProperty]
    private string _statusDetail = string.Empty;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string _elapsed = "0:00";

    [ObservableProperty]
    private string? _estimatedRemaining;

    [ObservableProperty]
    private bool _cancelRequested;

    public async Task RunAsync()
    {
        _stopwatch.Start();
        string outputDirectory = Path.Combine(Path.GetTempPath(), "optimum-build-" + Guid.NewGuid().ToString("N"));

        BuildResult build;
        try
        {
            build = await _services.BuildDriver.RunAsync(
                new BuildRequest(_session.RepoRoot, outputDirectory, ClientArchive: null, _session.Version),
                this,
                _cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            build = BuildResult.Failure(FailureReason.Cancelled, "the build was cancelled");
        }

        if (!build.Ok)
        {
            Finish(build.Reason == FailureReason.Cancelled, build.Message ?? "the build failed");
            return;
        }

        Phase(ProgressPhase.Verify, 96, "installing");
        DeployResult deploy = _services.Installer.Deploy(
            new DeployRequest(build.RuntimePath!, _session.InstallDirectory, _session.DataPath, _session.Shortcuts),
            this);

        if (!deploy.Ok)
        {
            Finish(cancelled: false, deploy.Message ?? "the install failed");
            return;
        }

        Phase(ProgressPhase.Verify, 99, "done");
        Finish(cancelled: false, "Optimum is installed.", deploy.InstallDirectory, deploy.Launcher);
    }

    [RelayCommand]
    private void Cancel()
    {
        CancelRequested = true;
        StatusDetail = "cancelling";
        _cancellation.Cancel();
    }

    void IBuildObserver.Phase(ProgressPhase phase, int percent, string detail) => Phase(phase, percent, detail);

    void IBuildObserver.Log(LogLevel level, string message) =>
        _post(() => Log.Add(new LogLine(level.ToString().ToLowerInvariant(), message)));

    void IBuildObserver.RawOutput(bool isError, string line)
    {
        _rawLog.AppendLine(line);
        if (isError || InstallerLogFilter.IsInteresting(line))
            _post(() => Log.Add(new LogLine(isError ? "error" : "info", line)));
    }

    private void Phase(ProgressPhase phase, int percent, string detail) => _post(() =>
    {
        PhaseLabel = Humanize(phase);
        StatusDetail = detail;
        Percent = Math.Max(Percent, percent);
        Elapsed = FormatDuration(_stopwatch.Elapsed);
        EstimatedRemaining = Percent is > 5 and < 99
            ? "about " + FormatDuration(TimeSpan.FromSeconds(
                _stopwatch.Elapsed.TotalSeconds * (100 - Percent) / Percent)) + " left"
            : null;
    });

    private void Finish(bool cancelled, string message, string? installDir = null, string? launcher = null)
    {
        _stopwatch.Stop();
        string rawLogPath = Path.Combine(Path.GetTempPath(),
            $"optimum-install-{DateTime.Now:yyyy-MM-ddTHHmmss}.log");
        try { File.WriteAllText(rawLogPath, _rawLog.ToString()); }
        catch (IOException) { rawLogPath = "(log not written)"; }

        _post(() =>
        {
            Percent = cancelled ? Percent : 100;
            Finished?.Invoke(new InstallOutcome(
                Succeeded: installDir is not null,
                Cancelled: cancelled,
                Message: message,
                InstallDirectory: installDir,
                Launcher: launcher,
                RawLogPath: rawLogPath));
        });
    }

    private static string Humanize(ProgressPhase phase) => phase switch
    {
        ProgressPhase.Decompile => "Downloading and decompiling Vintage Story",
        ProgressPhase.Patch => "Applying Optimum patches",
        ProgressPhase.Assemble => "Compiling and packaging",
        ProgressPhase.Verify => "Verifying and installing",
        _ => "Working",
    };

    private static string FormatDuration(TimeSpan span) =>
        span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:D2}:{span.Seconds:D2}"
            : $"{span.Minutes}:{span.Seconds:D2}";
}
