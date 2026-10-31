using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Patch;
using Optimum.Update;

namespace Optimum.Bootstrap.Core.Update;

public sealed record ApplyRequest(
    string GameDirectory,
    string OverlayDirectory,
    int? WaitForProcessId = null,
    TimeSpan? WaitTimeout = null,
    string? PublicKey = null);

public sealed record ApplyOutcome(bool Ok, string Version, string Reason, bool RolledBack);

/// <summary>
/// Applies a staged overlay to an install patched in place, and undoes it if anything goes wrong.
///
/// Run from the staged overlay's own copy of the CLI, after the game has exited. That is what makes it
/// safe on Windows: the program doing the replacing is not one of the files being replaced, and no game
/// DLL is loaded while they are swapped.
///
/// Nothing staged is trusted because it sits in a folder. The signed manifest kept beside the files is
/// verified again here and every file is hashed again, since the update may have been staged days ago.
/// </summary>
public sealed class UpdateApplier
{
    private readonly IGamePatcher _patcher;
    private readonly Func<int, bool> _isProcessAlive;
    private readonly bool _windows;

    public UpdateApplier(IGamePatcher patcher, Func<int, bool>? isProcessAlive = null, bool? windows = null)
    {
        _patcher = patcher;
        _isProcessAlive = isProcessAlive ?? DefaultIsAlive;
        _windows = windows ?? OperatingSystem.IsWindows();
    }

    public async Task<ApplyOutcome> ApplyAsync(ApplyRequest request, IBuildObserver? observer, CancellationToken cancellationToken)
    {
        string game = Path.GetFullPath(request.GameDirectory);
        ApplyOutcome outcome = await ApplyCoreAsync(request, game, observer, cancellationToken).ConfigureAwait(false);
        try
        {
            new UpdateResult(outcome.Version, outcome.Ok, outcome.Reason, outcome.RolledBack, DateTimeOffset.UtcNow).Write(game);
        }
        catch (Exception)
        {
            // The result is for the next start to show. Failing to write it must not hide how the update went.
        }
        return outcome;
    }

    private async Task<ApplyOutcome> ApplyCoreAsync(ApplyRequest request, string game, IBuildObserver? observer, CancellationToken cancellationToken)
    {
        string version = "unknown";
        UpdateSnapshot? snapshot = null;
        try
        {
            if (request.WaitForProcessId is int pid && !await WaitForExitAsync(pid, request.WaitTimeout ?? TimeSpan.FromSeconds(120), cancellationToken).ConfigureAwait(false))
                return Fail(version, "the game did not close in time, so nothing was changed");

            if (!InstallLayout.IsPatchedInPlace(game))
                return Fail(version, "this install is not patched in place");

            string overlay = Path.GetFullPath(request.OverlayDirectory);
            string pending = Path.Combine(game, ".optimum", "pending") + Path.DirectorySeparatorChar;
            if (!overlay.StartsWith(pending, StringComparison.OrdinalIgnoreCase))
                return Fail(version, "the overlay is not inside this install's pending folder");

            observer?.Phase(ProgressPhase.Verify, 5, "checking the staged update");
            string key = request.PublicKey ?? OptimumUpdateTrust.ManifestPublicKey;
            if (!TryReadStaged(overlay, key, out OverlayManifest? manifest, out string stagedError) || manifest is null)
                return Fail(version, stagedError);
            version = manifest.OptimumVersion;

            string? installed = ReadInstalledVersion(game);
            if (installed is null) return Fail(version, "the installed Optimum version could not be read");
            if (OverlayManifest.CompareVersions(manifest.OptimumVersion, installed) <= 0)
                return Fail(version, "the staged update is not newer than the installed version");
            if (manifest.Rid != CurrentRid()) return Fail(version, "the staged update is for another platform");

            using UpdateLock? held = UpdateLock.TryAcquire(game);
            if (held is null) return Fail(version, "another update is running");

            observer?.Phase(ProgressPhase.Patch, 15, "saving the current version");
            string snapshotDir = Path.Combine(game, ".optimum", "rollback", $"{installed}-{DateTime.UtcNow:yyyyMMddHHmmss}");
            snapshot = UpdateSnapshot.Capture(game, snapshotDir, UpdateSnapshot.PathsFor(manifest, _windows));

            PatchResult result;
            try
            {
                result = await _patcher.PatchAsync(new PatchRequest(game, overlay, Backup: true), observer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return RollBack(snapshot, version, "the update was cancelled");
            }
            catch (Exception ex)
            {
                return RollBack(snapshot, version, $"the patch failed: {ex.Message}");
            }

            if (!result.Ok)
                return RollBack(snapshot, version, result.Message ?? "the patch failed");

            string? problem = VerifyApplied(game, manifest);
            if (problem is not null)
                return RollBack(snapshot, version, problem);

            TryDelete(overlay);
            TryDeleteOlderSnapshots(Path.Combine(game, ".optimum", "rollback"), snapshotDir);
            observer?.Phase(ProgressPhase.Verify, 99, $"updated to {version}");
            return new ApplyOutcome(true, version, "updated", false);
        }
        catch (Exception ex) when (snapshot is not null)
        {
            return RollBack(snapshot, version, $"the update failed: {ex.Message}");
        }
        catch (Exception ex)
        {
            return Fail(version, $"the update failed: {ex.Message}");
        }
    }

    private static ApplyOutcome Fail(string version, string reason) => new(false, version, reason, false);

    private static ApplyOutcome RollBack(UpdateSnapshot snapshot, string version, string reason)
    {
        IReadOnlyList<string> failed = snapshot.Restore();
        return failed.Count == 0
            ? new ApplyOutcome(false, version, reason, RolledBack: true)
            : new ApplyOutcome(false, version, $"{reason}; restoring the previous version also failed for {string.Join(", ", failed)}", RolledBack: false);
    }

    private static bool TryReadStaged(string overlay, string key, out OverlayManifest? manifest, out string error)
    {
        manifest = null;
        try
        {
            string manifestPath = Path.Combine(overlay, OverlayStager.StagedManifest);
            string signaturePath = Path.Combine(overlay, OverlayStager.StagedManifestSignature);
            if (!File.Exists(manifestPath) || !File.Exists(signaturePath))
            {
                error = "the staged update has no signed manifest";
                return false;
            }
            byte[] bytes = File.ReadAllBytes(manifestPath);
            if (!ManifestSignature.Verify(bytes, File.ReadAllBytes(signaturePath), key))
            {
                error = "the staged manifest signature does not verify";
                return false;
            }
            if (!OverlayManifest.TryParse(bytes, out manifest, out string parseError) || manifest is null)
            {
                error = parseError;
                return false;
            }
            foreach (OverlayFile file in manifest.Files)
            {
                string path = Path.Combine(overlay, file.Path.Replace('/', Path.DirectorySeparatorChar));
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != file.Size)
                {
                    error = $"the staged file {file.Path} is missing or changed";
                    manifest = null;
                    return false;
                }
                using FileStream stream = File.OpenRead(path);
                if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    error = $"the staged file {file.Path} does not match the manifest";
                    manifest = null;
                    return false;
                }
            }
            error = "";
            return true;
        }
        catch (Exception ex)
        {
            manifest = null;
            error = $"the staged update could not be read: {ex.Message}";
            return false;
        }
    }

    private static string? ReadInstalledVersion(string game)
    {
        try
        {
            PatchInstallManifest? manifest = PatchInstallManifest.Deserialize(File.ReadAllText(Path.Combine(game, ".optimum", "manifest.json")));
            return manifest is not null && OverlayManifest.IsValidVersion(manifest.OptimumVersion) ? manifest.OptimumVersion : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>What the patch says it wrote must be what is on disk, and it must say it wrote this version.</summary>
    private static string? VerifyApplied(string game, OverlayManifest manifest)
    {
        PatchInstallManifest? written;
        try
        {
            written = PatchInstallManifest.Deserialize(File.ReadAllText(Path.Combine(game, ".optimum", "manifest.json")));
        }
        catch (Exception)
        {
            return "the patch left no readable record of what it wrote";
        }
        if (written is null) return "the patch left no readable record of what it wrote";
        if (written.OptimumVersion != manifest.OptimumVersion)
            return $"the patch recorded version {written.OptimumVersion} instead of {manifest.OptimumVersion}";
        if (written.Targets.Count == 0) return "the patch recorded no patched assembly";
        foreach (PatchTargetRecord target in written.Targets)
        {
            if (string.IsNullOrEmpty(target.PatchedHash)) continue;
            string path = Path.Combine(game, target.Assembly.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) return $"{target.Assembly} is missing after the patch";
            using FileStream stream = File.OpenRead(path);
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(stream)), BareDigest(target.PatchedHash), StringComparison.OrdinalIgnoreCase))
                return $"{target.Assembly} does not match what the patch recorded";
        }
        return null;
    }

    /// <summary>The patch writes hashes as "sha256:&lt;hex&gt;"; older records and tests use the bare digest.</summary>
    private static string BareDigest(string recorded) =>
        recorded.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? recorded.Substring("sha256:".Length) : recorded;

    private string CurrentRid() => _windows ? "win-x64" : "linux-x64";

    private async Task<bool> WaitForExitAsync(int pid, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (_isProcessAlive(pid))
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }
        // The process is gone but the OS may still be releasing its file handles.
        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private static bool DefaultIsAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static void TryDelete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch (Exception) { }
    }

    private static void TryDeleteOlderSnapshots(string root, string keep)
    {
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(root))
                if (!string.Equals(Path.GetFullPath(directory), Path.GetFullPath(keep), StringComparison.OrdinalIgnoreCase)) TryDelete(directory);
        }
        catch (Exception) { }
    }
}
