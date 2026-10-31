using System.Security.Cryptography;
using System.Text.Json;
using Optimum.Update;

namespace Optimum.Bootstrap.Core.Update;

/// <summary>
/// A copy of every file an overlay patch may write, taken before it runs, so that a patch that fails
/// halfway puts the previous Optimum version back instead of leaving two versions mixed together.
///
/// The patch's own rollback is not enough for this: it restores the pristine assemblies from
/// .optimum/vanilla, which turns a failed update into "Optimum removed". This one restores what was
/// there a moment ago.
/// </summary>
public sealed class UpdateSnapshot
{
    private sealed record Entry(string Path, bool Existed, string? Sha256);

    private readonly string _gameDir;
    private readonly string _filesDir;
    private readonly List<Entry> _entries;

    private UpdateSnapshot(string gameDir, string directory, List<Entry> entries)
    {
        _gameDir = gameDir;
        Directory = directory;
        _filesDir = System.IO.Path.Combine(directory, "files");
        _entries = entries;
    }

    public string Directory { get; }

    /// <summary>Everything the patch may create or replace, as game-relative paths with forward slashes.</summary>
    public static IReadOnlyList<string> PathsFor(OverlayManifest manifest, bool windows)
    {
        var paths = new List<string>
        {
            "VintagestoryLib.dll", "VintagestoryLib.pdb",
            "VintagestoryAPI.dll", "VintagestoryAPI.pdb",
            "Mods/VSEssentials.dll", "Mods/VSEssentials.pdb",
            "Mods/VSSurvivalMod.dll", "Mods/VSSurvivalMod.pdb",
            "Optimum.Api.Contracts.dll",
            "Optimum.GameContent.dll",
            windows ? "Optimum.exe" : "Optimum",
            ".optimum/version",
            ".optimum/manifest.json",
            ".optimum/vanilla/Optimum.GameContent.dll",
            ".optimum/vanilla/Optimum.GameContent.dll.absent",
        };
        // Shaders, language files and shader patches are copied or merged to the same relative path.
        foreach (OverlayFile file in manifest.Files)
            if (file.Path.StartsWith("assets/", StringComparison.Ordinal))
                paths.Add(file.Path);
        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static UpdateSnapshot Capture(string gameDir, string snapshotDir, IEnumerable<string> relativePaths)
    {
        var entries = new List<Entry>();
        string filesDir = System.IO.Path.Combine(snapshotDir, "files");
        System.IO.Directory.CreateDirectory(filesDir);
        foreach (string relative in relativePaths)
        {
            string source = Resolve(gameDir, relative);
            if (!File.Exists(source))
            {
                entries.Add(new Entry(relative, false, null));
                continue;
            }
            string target = Resolve(filesDir, relative);
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: true);
            entries.Add(new Entry(relative, true, Hash(target)));
        }
        File.WriteAllText(System.IO.Path.Combine(snapshotDir, "journal.json"), JsonSerializer.Serialize(entries));
        return new UpdateSnapshot(gameDir, snapshotDir, entries);
    }

    /// <summary>
    /// Puts every captured file back and removes those that did not exist. Returns the paths that could not be
    /// restored to exactly what was captured; empty means the game folder is as it was.
    /// </summary>
    public IReadOnlyList<string> Restore()
    {
        var failed = new List<string>();
        foreach (Entry entry in _entries)
        {
            string live = Resolve(_gameDir, entry.Path);
            try
            {
                if (entry.Existed)
                {
                    string saved = Resolve(_filesDir, entry.Path);
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(live)!);
                    string temp = live + ".restore-" + Guid.NewGuid().ToString("N");
                    File.Copy(saved, temp, overwrite: true);
                    File.Move(temp, live, overwrite: true);
                    if (!string.Equals(Hash(live), entry.Sha256, StringComparison.OrdinalIgnoreCase)) failed.Add(entry.Path);
                }
                else if (File.Exists(live))
                {
                    File.Delete(live);
                }
            }
            catch (Exception)
            {
                failed.Add(entry.Path);
            }
        }
        return failed;
    }

    private static string Resolve(string root, string relative) => System.IO.Path.Combine(root, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }
}
