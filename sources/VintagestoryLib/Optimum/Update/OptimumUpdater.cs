using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;

namespace Optimum.Update;

/// <summary>
/// Where the update is. Integer constants rather than an enum, for the same reason as <see cref="UpdateFailure"/>.
/// </summary>
public static class UpdatePhase
{
    /// <summary>Nothing to do: no newer release, or this install cannot be updated in place.</summary>
    public const int Idle = 0;
    /// <summary>A newer release exists and can be staged.</summary>
    public const int Available = 1;
    /// <summary>Downloading and verifying.</summary>
    public const int Staging = 2;
    /// <summary>Verified and waiting for the game to close.</summary>
    public const int Staged = 3;
    /// <summary>The helper was started and the game is closing.</summary>
    public const int Applying = 4;
    /// <summary>Staging failed; <see cref="OptimumUpdater.Detail"/> says why.</summary>
    public const int Failed = 5;
}

/// <summary>
/// The in-game side of the one-click update: decides whether the button is offered, stages the release in the
/// background and starts the helper that applies it after the game exits.
///
/// It touches no game state and no UI, so it can be driven from tests. The menu reads <see cref="Phase"/>,
/// <see cref="Detail"/> and <see cref="Progress"/> and is told about changes through <see cref="StateChanged"/>.
/// </summary>
public sealed class OptimumUpdater
{
    /// <summary>Set by an integrator that manages installs itself (RiftLauncher); the button is then never offered.</summary>
    public const string ManagedByEnvironmentVariable = "OPTIMUM_MANAGED_BY";

    private readonly string _gameDir;
    private readonly string _installedVersion;
    private readonly string _gameVersion;
    private readonly string _rid;
    private readonly Func<HttpClient>? _http;
    private readonly string? _releaseBase;
    private readonly Func<string, string?> _environment;
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private DateTime _lastNotify = DateTime.MinValue;
    private string _stageTarget = "";
    private string _manifestSha256 = "";
    private long _manifestSize;
    private string _archiveSha256 = "";
    private long _archiveSize;
    private OverlayManifest? _stagedManifest;

    public OptimumUpdater(
        string gameDir,
        string installedVersion,
        string gameVersion,
        string rid,
        Func<HttpClient>? http = null,
        string? releaseBase = null,
        Func<string, string?>? environment = null,
        Action<string>? log = null)
    {
        _gameDir = gameDir.TrimEnd('/', '\\');
        _installedVersion = installedVersion;
        _gameVersion = gameVersion;
        _rid = rid;
        _http = http;
        _releaseBase = releaseBase;
        _environment = environment ?? Environment.GetEnvironmentVariable;
        _log = log;
    }

    public int Phase { get; private set; } = UpdatePhase.Idle;
    public string TargetVersion { get; private set; } = "";
    public string Detail { get; private set; } = "";
    /// <summary>0 to 1 while staging.</summary>
    public double Progress { get; private set; }
    public string? StagedDirectory { get; private set; }

    public event Action? StateChanged;

    /// <summary>
    /// True when this build and this install can offer a one-click update at all. Each reason it cannot is a reason the
    /// menu keeps its plain link to the release page.
    /// </summary>
    public bool IsSupported(out string reason)
    {
        if (!OverlayStager.IsValidSha256Digest(_manifestSha256) || _manifestSize <= 0 ||
            !OverlayStager.IsValidSha256Digest(_archiveSha256) || _archiveSize <= 0)
        { reason = "the GitHub release asset digests are unavailable"; return false; }
        if (!string.IsNullOrEmpty(_environment(ManagedByEnvironmentVariable))) { reason = "another launcher manages this install"; return false; }
        if (!InstallLayout.IsPatchedInPlace(_gameDir)) { reason = "this install is not patched in place"; return false; }
        reason = "";
        return true;
    }

    /// <summary>Called by the update checker when it learns of a newer release.</summary>
    public void OfferRelease(string version, string manifestSha256, long manifestSize, string archiveSha256, long archiveSize)
    {
        if (!OverlayManifest.IsValidVersion(version) || OverlayManifest.CompareVersions(version, _installedVersion) <= 0) return;
        _manifestSha256 = manifestSha256;
        _manifestSize = manifestSize;
        _archiveSha256 = archiveSha256;
        _archiveSize = archiveSize;
        if (!IsSupported(out string reason))
        {
            lock (_gate)
            {
                TargetVersion = version;
                StagedDirectory = null;
                _stagedManifest = null;
                Phase = UpdatePhase.Idle;
                Detail = "";
            }
            _log?.Invoke($"One-click update not offered: {reason}.");
            Notify(force: true);
            return;
        }
        // A copy staged by an earlier run needs no download: offer the restart straight away.
        string? staged = OverlayStager.FindStaged(_gameDir, version, _rid, manifestSha256, out OverlayManifest? stagedManifest);
        lock (_gate)
        {
            if (Phase == UpdatePhase.Staging || Phase == UpdatePhase.Staged || Phase == UpdatePhase.Applying) return;
            TargetVersion = version;
            Detail = "";
            StagedDirectory = staged;
            _stagedManifest = stagedManifest;
            Phase = staged is not null ? UpdatePhase.Staged : UpdatePhase.Available;
        }
        Notify(force: true);
    }

    /// <summary>Starts downloading and verifying on a background thread. Returns at once.</summary>
    public void BeginStaging()
    {
        lock (_gate)
        {
            if (Phase != UpdatePhase.Available && Phase != UpdatePhase.Failed) return;
            _stageTarget = TargetVersion;
            Phase = UpdatePhase.Staging;
            Progress = 0;
            Detail = "";
        }
        Notify(force: true);
        var thread = new Thread(RunStage);
        thread.IsBackground = true;
        thread.Name = "Optimum update download";
        thread.Start();
    }

    private HttpClient CreateHttpClient()
    {
        if (_http is not null) return _http();
        var client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.Add("User-Agent", "Optimum-Updater/" + _installedVersion);
        return client;
    }

    private void OnStageProgress(StageProgress progress)
    {
        if (progress.Total > 0 && progress.Phase == "download") Progress = (double)progress.Done / progress.Total;
        Notify(force: false);
    }

    /// <summary>Runs on the staging thread. Exposed to tests through <see cref="BeginStaging"/> only.</summary>
    private void RunStage()
    {
        string target = _stageTarget;
        try
        {
            using HttpClient http = CreateHttpClient();
            var stager = new OverlayStager(http, _releaseBase);
            StageResult result = stager.Stage(_gameDir, _installedVersion, _gameVersion, _rid, target,
                _manifestSha256, _manifestSize, _archiveSha256, _archiveSize, OnStageProgress, CancellationToken.None);
            lock (_gate)
            {
                if (result.Ok)
                {
                    StagedDirectory = result.StagedDirectory;
                    _stagedManifest = result.Manifest;
                    Phase = UpdatePhase.Staged;
                    Detail = "";
                }
                else
                {
                    Phase = UpdatePhase.Failed;
                    Detail = result.Message;
                }
            }
            if (!result.Ok && _log is not null)
                _log("One-click update could not stage " + target + ": " + result.Failure + ": " + result.Message);
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                Phase = UpdatePhase.Failed;
                Detail = ex.Message;
            }
            if (_log is not null) _log("One-click update failed unexpectedly: " + ex);
        }
        Notify(force: true);
    }

    /// <summary>
    /// Builds the command that applies the staged update, or null when there is nothing safe to run. The staged copy of
    /// the CLI is used rather than anything already installed, so the file doing the replacing is not one being replaced.
    /// </summary>
    public ProcessStartInfo? BuildHelperCommand(int gameProcessId, string restartExecutable, string restartArguments, bool windows)
    {
        if (Phase != UpdatePhase.Staged || StagedDirectory is null) return null;
        string cli = Path.Combine(StagedDirectory, windows ? "optimum.exe" : "optimum");
        if (_stagedManifest is null || !OverlayStager.IsLaunchHelperValid(StagedDirectory, _manifestSha256, _stagedManifest, windows)) return null;

        var start = new ProcessStartInfo(cli)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = StagedDirectory,
        };
        start.ArgumentList.Add("apply-update");
        start.ArgumentList.Add("--game-dir"); start.ArgumentList.Add(_gameDir);
        start.ArgumentList.Add("--overlay"); start.ArgumentList.Add(StagedDirectory);
        start.ArgumentList.Add("--wait-pid"); start.ArgumentList.Add(gameProcessId.ToString());
        start.ArgumentList.Add("--manifest-sha256"); start.ArgumentList.Add(_manifestSha256);
        if (restartExecutable.Length > 0)
        {
            start.ArgumentList.Add("--restart"); start.ArgumentList.Add(restartExecutable);
            if (restartArguments.Length > 0) { start.ArgumentList.Add("--restart-args"); start.ArgumentList.Add(restartArguments); }
        }
        return start;
    }

    /// <summary>Starts the staged helper without blocking the game menu. The caller closes the game after it starts.</summary>
    public bool StartHelper(int gameProcessId, string restartExecutable, string restartArguments)
    {
        ProcessStartInfo? command = BuildHelperCommand(gameProcessId, restartExecutable, restartArguments, OperatingSystem.IsWindows());
        if (command is null)
        {
            lock (_gate) { Phase = UpdatePhase.Failed; Detail = "the staged update is not ready"; }
            Notify(force: true);
            return false;
        }
        try
        {
            using Process? process = Process.Start(command);
            if (process is null) throw new InvalidOperationException("the staged update helper did not start");
            lock (_gate) { Phase = UpdatePhase.Applying; Detail = ""; }
            Notify(force: true);
            return true;
        }
        catch (Exception ex)
        {
            lock (_gate) { Phase = UpdatePhase.Failed; Detail = ex.Message; }
            _log?.Invoke($"One-click update could not start its helper: {ex.Message}");
            Notify(force: true);
            return false;
        }
    }

    private void Notify(bool force)
    {
        if (!force)
        {
            DateTime now = DateTime.UtcNow;
            if (now - _lastNotify < TimeSpan.FromMilliseconds(500)) return;
            _lastNotify = now;
        }
        StateChanged?.Invoke();
    }
}
