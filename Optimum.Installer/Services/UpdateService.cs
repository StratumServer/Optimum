using System.Net.Http;
using Velopack;
using Velopack.Sources;

namespace Optimum.Installer.Services;

public interface IUpdateService
{
    /// <summary>The version of an available update, or null when there is none (or self-update does not apply).</summary>
    Task<string?> CheckAsync(CancellationToken cancellationToken = default);

    /// <summary>Downloads and applies the last checked update, then restarts. Does not return on success.</summary>
    Task ApplyAsync(Action<int>? progress = null);
}

/// <summary>
/// Thin wrapper over Velopack's <see cref="UpdateManager"/>. A no-op when the
/// installer is not running from a Velopack install (a loose build, a developer
/// run), so the wizard never blocks on it.
/// </summary>
public sealed class UpdateService : IUpdateService
{
    // The installer's own releases. The channel is the RID so the win and linux
    // feeds do not cross. Overridable for a staging feed.
    public static string ReleaseRepository { get; set; } = "https://github.com/StratumServer/Optimum";

    private readonly UpdateManager? _manager;
    private readonly Lock _gate = new();
    private UpdateInfo? _pending;

    public UpdateService()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(ReleaseRepository, null, prerelease: false));
            _manager = manager.IsInstalled ? manager : null;
        }
        catch (Exception)
        {
            _manager = null;
        }
    }

    public async Task<string?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (_manager is null)
            return null;
        try
        {
            UpdateInfo? info = await _manager.CheckForUpdatesAsync();
            lock (_gate)
                _pending = info;
            return info?.TargetFullRelease.Version.ToString();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or InvalidOperationException)
        {
            // A background check is best effort: a broken feed, a rate limit, or
            // an offline machine must not block the wizard.
            return null;
        }
    }

    public async Task ApplyAsync(Action<int>? progress = null)
    {
        UpdateInfo? pending;
        lock (_gate)
            pending = _pending;
        if (_manager is null || pending is null)
            return;
        await _manager.DownloadUpdatesAsync(pending, progress);
        _manager.ApplyUpdatesAndRestart(pending.TargetFullRelease);
    }
}
