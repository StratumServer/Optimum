using System.Collections.ObjectModel;
using System.Text;
using System.Diagnostics;
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
/// label, the bar, an elapsed clock that ticks on its own, and a filtered log
/// pane. It is its own <see cref="IBuildObserver"/> and marshals every callback
/// to the UI thread. It owns the temporary build directory and deletes it when
/// it is done with it.
/// </summary>
public sealed partial class ProgressViewModel : ViewModelBase, IBuildObserver
{
    private readonly InstallerServices _services;
    private readonly InstallSession _session;
    private readonly Action<Action> _post;
    private readonly CancellationTokenSource _forceful = new();
    private readonly CancellationTokenSource _graceful = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly StringBuilder _rawLog = new();
    private readonly Lock _rawLogGate = new();

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
        using var clock = new Timer(_ => _post(RefreshClock), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        string outputDirectory = Path.Combine(Path.GetTempPath(), "optimum-build-" + Guid.NewGuid().ToString("N"));
        InstallOutcome outcome;
        try
        {
            outcome = await BuildAndDeployAsync(outputDirectory);
        }
        finally
        {
            _stopwatch.Stop();
            TryDeleteDirectory(outputDirectory);
            _forceful.Dispose();
            _graceful.Dispose();
        }

        var settled = new TaskCompletionSource();
        _post(() =>
        {
            Percent = outcome.Succeeded ? 100 : Percent;
            Finished?.Invoke(outcome);
            settled.SetResult();
        });
        await settled.Task;
    }

    private async Task<InstallOutcome> BuildAndDeployAsync(string outputDirectory)
    {
        BuildResult build;
        try
        {
            build = await _services.BuildDriver.RunAsync(
                new BuildRequest(_session.RepoRoot, outputDirectory, ClientArchive: null, _session.Version),
                this,
                _forceful.Token,
                _graceful.Token);
        }
        catch (OperationCanceledException)
        {
            build = BuildResult.Failure(FailureReason.Cancelled, "the build was cancelled");
        }

        if (!build.Ok)
            return WriteOutcome(build.Reason == FailureReason.Cancelled, build.Message ?? "the build failed", null, null);

        Phase(ProgressPhase.Verify, 96, "installing");
        DeployResult deploy = _services.Installer.Deploy(
            new DeployRequest(build.RuntimePath!, _session.InstallDirectory, _session.DataPath, _session.Shortcuts),
            this);

        if (!deploy.Ok)
            return WriteOutcome(cancelled: false, deploy.Message ?? "the install failed", null, null);

        Phase(ProgressPhase.Verify, 99, "done");
        return WriteOutcome(cancelled: false, "Optimum is installed.", deploy.InstallDirectory, deploy.Launcher);
    }

    [RelayCommand]
    private void Cancel()
    {
        CancelRequested = true;
        _post(() => StatusDetail = "cancelling");
        _graceful.Cancel();
        // Force-kill if the pipeline has not stopped on its own after a grace period.
        _ = Task.Delay(TimeSpan.FromSeconds(10)).ContinueWith(_ =>
        {
            if (!_forceful.IsCancellationRequested)
                _forceful.Cancel();
        }, TaskScheduler.Default);
    }

    void IBuildObserver.Phase(ProgressPhase phase, int percent, string detail) => Phase(phase, percent, detail);

    void IBuildObserver.Log(LogLevel level, string message) =>
        _post(() => Log.Add(new LogLine(level.ToString().ToLowerInvariant(), message)));

    void IBuildObserver.RawOutput(bool isError, string line)
    {
        lock (_rawLogGate)
            _rawLog.AppendLine(line);
        if (isError || InstallerLogFilter.IsInteresting(line))
            _post(() => Log.Add(new LogLine(isError ? "error" : "info", line)));
    }

    private void Phase(ProgressPhase phase, int percent, string detail) => _post(() =>
    {
        PhaseLabel = Humanize(phase);
        StatusDetail = detail;
        Percent = Math.Max(Percent, percent);
        RefreshClock();
    });

    private void RefreshClock()
    {
        Elapsed = FormatDuration(_stopwatch.Elapsed);
        EstimatedRemaining = Percent is > 8 and < 99
            ? "about " + FormatDuration(TimeSpan.FromSeconds(
                _stopwatch.Elapsed.TotalSeconds * (100 - Percent) / Percent)) + " left (rough)"
            : null;
    }

    private InstallOutcome WriteOutcome(bool cancelled, string message, string? installDir, string? launcher)
    {
        string rawLogPath = Path.Combine(Path.GetTempPath(),
            $"optimum-install-{DateTime.Now:yyyy-MM-ddTHHmmss}.log");
        try
        {
            lock (_rawLogGate)
                File.WriteAllText(rawLogPath, _rawLog.ToString());
        }
        catch (IOException) { rawLogPath = "(log not written)"; }

        return new InstallOutcome(
            Succeeded: installDir is not null,
            Cancelled: cancelled,
            Message: message,
            InstallDirectory: installDir,
            Launcher: launcher,
            RawLogPath: rawLogPath);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException) { /* best effort */ }
        catch (UnauthorizedAccessException) { /* best effort */ }
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
