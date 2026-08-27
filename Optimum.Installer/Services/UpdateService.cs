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
            _pending = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken);
            return _pending?.TargetFullRelease.Version.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async Task ApplyAsync(Action<int>? progress = null)
    {
        if (_manager is null || _pending is null)
            return;
        await _manager.DownloadUpdatesAsync(_pending, progress);
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
    }
}
