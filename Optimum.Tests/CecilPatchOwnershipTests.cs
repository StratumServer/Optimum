using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Optimum.Tests;

public class CecilPatchOwnershipTests
{
    private static readonly HashSet<string> KnownUnownedLibPatches = new(StringComparer.Ordinal)
    {
        "patches/VintagestoryLib/Vintagestory.API.Common/EventHelper.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientWorldMap.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/ParticleManager.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/SvgLoader.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/SystemClientTickingBlocks.cs.patch",
    };

    [Fact]
    public void NewVintagestoryLibPatchesNeedCecilOwnership()
    {
        string repoRoot = FindRepositoryRoot();
        string patchesRoot = Path.Combine(repoRoot, "patches", "VintagestoryLib");
        string cecilListPath = Path.Combine(repoRoot, "patches", "cecil-owned.list");

        HashSet<string> owned = File.ReadAllLines(cecilListPath)
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("patches/VintagestoryLib/", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        string[] unowned = Directory.GetFiles(patchesRoot, "*.patch", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(repoRoot, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !owned.Contains(path))
            .Where(path => !KnownUnownedLibPatches.Contains(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(unowned);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "patches", "cecil-owned.list");
            if (File.Exists(candidate))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find patches/cecil-owned.list from {AppContext.BaseDirectory}.");
    }
}
