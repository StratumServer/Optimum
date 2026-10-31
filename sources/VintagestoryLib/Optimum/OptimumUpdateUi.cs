using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Optimum.Update;
using Vintagestory.API.Config;
using Vintagestory.Client;

namespace Optimum;

/// <summary>
/// The main menu's side of the one-click update: what the Optimum line says, what its links do, and the
/// result of the last update. The work itself is in <see cref="OptimumUpdater"/>.
///
/// Links in the menu text are strings, so the actions are links too: https links open the release page
/// as before, and optimum-update: links drive the updater.
/// </summary>
public static class OptimumUpdateUi
{
    public const string StageLink = "optimum-update:stage";
    public const string ApplyLink = "optimum-update:apply";

    private static OptimumUpdater _updater;
    private static Action _refresh;
    private static string _pendingMessage;
    private static bool _resultRead;

    public static string GameDirectory => AppContext.BaseDirectory.TrimEnd('/', '\\');

    /// <summary>Called once the update checker found a newer release.</summary>
    public static void OnReleaseFound(string version)
    {
        try
        {
            EnsureUpdater().OfferRelease(version);
        }
        catch (Exception ex)
        {
            Log("could not offer the update: " + ex.Message);
        }
    }

    /// <summary>The main menu registers how to redraw itself; the updater calls it on the main thread when its state changes.</summary>
    public static void SetRefresh(Action compose)
    {
        _refresh = () => ScreenManager.EnqueueMainThreadTask(compose);
    }

    /// <summary>The menu text for the Optimum block: the tag, and below it what the update is doing.</summary>
    public static string MenuHtml(string tag, string releaseUrl)
    {
        ReadLastResultOnce();
        string html = "<a href=\"" + releaseUrl + "\">" + tag + "</a>";
        // The sidebar text grows upward from the bottom, so it gets two Optimum lines at most. The outcome of the last
        // update takes the second line; a failed update can be tried again after the next start.
        if (_pendingMessage is not null)
            return html + "<br><font color=\"#e3a15b\">" + _pendingMessage + "</font>";
        OptimumUpdater updater = _updater;
        if (updater is not null)
        {
            string target = updater.TargetVersion;
            switch (updater.Phase)
            {
                case UpdatePhase.Available:
                    html += "<br><a href=\"" + StageLink + "\"><font color=\"#8cdb6a\">" + Lang.Get("optimum-update-install", target) + "</font></a>";
                    break;
                case UpdatePhase.Staging:
                    html += "<br><font color=\"#8cdb6a\">" + Lang.Get("optimum-update-downloading", target, (int)(updater.Progress * 100)) + "</font>";
                    break;
                case UpdatePhase.Staged:
                    html += "<br><a href=\"" + ApplyLink + "\"><font color=\"#8cdb6a\">" + Lang.Get("optimum-update-restart", target) + "</font></a>";
                    break;
                case UpdatePhase.Applying:
                    html += "<br><font color=\"#8cdb6a\">" + Lang.Get("optimum-update-applying", target) + "</font>";
                    break;
                case UpdatePhase.Failed:
                    html += "<br><a href=\"" + StageLink + "\"><font color=\"#e3a15b\">" + Lang.Get("optimum-update-failed", target) + "</font></a>";
                    break;
            }
        }
        return html;
    }

    /// <summary>True when the link was one of ours and has been handled.</summary>
    public static bool TryHandleLink(string href, Func<bool> quit)
    {
        if (href == StageLink)
        {
            _updater?.BeginStaging();
            return true;
        }
        if (href == ApplyLink)
        {
            ApplyAndQuit(quit);
            return true;
        }
        return false;
    }

    private static void ApplyAndQuit(Func<bool> quit)
    {
        OptimumUpdater updater = _updater;
        if (updater is null) return;
        string executable = Environment.ProcessPath ?? "";
        string arguments = string.Join(" ", Environment.GetCommandLineArgs().Skip(1).Select(Quote));
        if (updater.StartHelper(Environment.ProcessId, executable, arguments))
            quit();
    }

    private static string Quote(string argument)
        => argument.Length > 0 && !argument.Contains(' ') && !argument.Contains('\t') && !argument.Contains('"') ? argument : "\"" + argument.Replace("\"", "\\\"") + "\"";

    private static OptimumUpdater EnsureUpdater()
    {
        if (_updater is not null) return _updater;
        string releaseBase = null;
#if OPTIMUM_UPDATE_TESTHOOK
        releaseBase = Environment.GetEnvironmentVariable("OPTIMUM_UPDATE_TEST_BASE");
#endif
        _updater = new OptimumUpdater(
            GameDirectory,
            OptimumInfo.Version,
            GameVersion.ShortGameVersion,
            OperatingSystem.IsWindows() ? "win-x64" : "linux-x64",
            releaseBase: releaseBase,
            log: Log);
        _updater.StateChanged += () => _refresh?.Invoke();
        return _updater;
    }

    /// <summary>
    /// A failed or finished update leaves a result file. Show it once, on the first menu draw of the next start.
    /// </summary>
    private static void ReadLastResultOnce()
    {
        if (_resultRead) return;
        _resultRead = true;
        try
        {
            string dir = GameDirectory;
            OverlayStager.SweepStale(dir, OptimumInfo.Version);
            UpdateResult result = UpdateResult.Read(dir);
            if (result is null) return;
            UpdateResult.Clear(dir);
            if (!result.Ok) OverlayStager.RemoveStaged(dir, result.Version);
            _pendingMessage = result.Ok
                ? Lang.Get("optimum-update-done", result.Version)
                : Lang.Get(result.RolledBack ? "optimum-update-rolledback" : "optimum-update-failed-last", result.Version);
            Log("last update: " + (result.Ok ? "ok " : "failed ") + result.Version + (result.Ok ? "" : ": " + result.Reason));
        }
        catch (Exception)
        {
        }
    }

    private static void Log(string message)
    {
        try
        {
            ScreenManager.Platform?.Logger?.Notification("[Optimum] " + message);
        }
        catch (Exception)
        {
        }
    }
}
