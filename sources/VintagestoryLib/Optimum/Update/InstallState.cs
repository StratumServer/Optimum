using System;
using System.IO;
using System.Text.Json;

namespace Optimum.Update;

/// <summary>
/// What the files in a game folder say about how Optimum got there.
///
/// Two layouts exist. An install patched in place (RiftLauncher, or `optimum patch`) holds already
/// patched assemblies and the pristine ones under .optimum/vanilla; re-running the patch from a newer
/// overlay is its update path. An install made by the Optimum installer keeps vanilla assemblies and
/// patches them at launch from .optimum/donors, so running the in-place patch over it would turn it
/// into the other layout. The one-click update only handles the first layout.
/// </summary>
public static class InstallLayout
{
    public const string StateFolder = ".optimum";
    public const string InstallerMarker = "standalone-install";
    public const string InstallerManifest = "install-manifest.json";

    public static bool IsPatchedInPlace(string gameDir)
    {
        string state = Path.Combine(gameDir, StateFolder);
        return File.Exists(Path.Combine(state, "manifest.json"))
            && File.Exists(Path.Combine(state, "vanilla", "VintagestoryLib.vanilla.dll"))
            && !File.Exists(Path.Combine(state, InstallerMarker))
            && !File.Exists(Path.Combine(state, InstallerManifest));
    }
}

/// <summary>
/// What the last update attempt did, written by the helper that applied it and read by the game on the
/// next start, so a failed update is explained instead of silently leaving the old version.
/// </summary>
public sealed class UpdateResult
{
    public const string FileName = "update-result.json";

    public UpdateResult(string version, bool ok, string reason, bool rolledBack, DateTimeOffset atUtc)
    {
        Version = version;
        Ok = ok;
        Reason = reason;
        RolledBack = rolledBack;
        AtUtc = atUtc;
    }

    public string Version { get; }
    public bool Ok { get; }
    public string Reason { get; }
    public bool RolledBack { get; }
    public DateTimeOffset AtUtc { get; }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static string PathFor(string gameDir) => Path.Combine(gameDir, InstallLayout.StateFolder, FileName);

    public void Write(string gameDir)
    {
        string path = PathFor(gameDir);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        File.WriteAllText(temp, JsonSerializer.Serialize(this));
        File.Move(temp, path, overwrite: true);
    }

    public static UpdateResult? Read(string gameDir)
    {
        try
        {
            string path = PathFor(gameDir);
            return File.Exists(path) ? JsonSerializer.Deserialize<UpdateResult>(File.ReadAllText(path), Options) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Clear(string gameDir)
    {
        try { File.Delete(PathFor(gameDir)); } catch (Exception) { }
    }
}

/// <summary>
/// One writer at a time for the files an update touches. The lock is a file created exclusively, so two
/// processes cannot both hold it, and one older than <see cref="StaleAfter"/> is taken to belong to a
/// process that died.
/// </summary>
public sealed class UpdateLock : IDisposable
{
    public const string FileName = "update.lock";
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(30);

    private readonly string path;
    private bool held;

    private UpdateLock(string path) { this.path = path; held = true; }

    public static UpdateLock? TryAcquire(string gameDir)
    {
        string path = Path.Combine(gameDir, InstallLayout.StateFolder, FileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream);
                writer.Write(Environment.ProcessId);
                return new UpdateLock(path);
            }
            catch (IOException)
            {
                try
                {
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < StaleAfter) return null;
                    File.Delete(path);
                }
                catch (IOException)
                {
                    return null;
                }
            }
        }
        return null;
    }

    public void Dispose()
    {
        if (!held) return;
        held = false;
        try { File.Delete(path); } catch (Exception) { }
    }
}
