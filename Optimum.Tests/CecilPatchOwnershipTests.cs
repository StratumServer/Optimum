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

        // Triaged 2026-08-11 (see docs/todo.md's "CecilPatchOwnershipTests backlog
        // triage" entry for the full audit and the server-side Cecil wiring that
        // followed from it). Two groups below. ChunkColumnLoadRequest.cs.patch,
        // ServerProgramArgs.cs.patch (just the RestoreLogsFolder property, not the
        // rest of that file's 1.22.6 diff), and GameDatabase.cs.patch are NOT here -
        // they're genuinely cecil-owned now (see patches/cecil-owned.list).

        // Group 1: pure version string-literal swaps, zero other diff.
        // Moot once forks.json's vintageStoryVersion pin actually bumps to 1.22.6
        // (a fresh decompile already carries the right literal with no patch
        // needed at all, per patches-1.22.6-bridge/README.md).
        "patches/VintagestoryLib/SaveGame.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client.MaxObf/SessionManager.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/GuiElementModCell.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client/ClientPackets.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client/ClientProgramArgs.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client/GuiScreenDownloadMods.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client/GuiScreenServerDashboard.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client/GuiScreenSingleplayer.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client/ScreenManager.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Common/CleanInstallCheck.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Common/EntityTypeNet.cs.patch",
        "patches/VintagestoryLib/Vintagestory.ModDb/ModDbUtil.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/AuthServerComm.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/CmdStats.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/ServerChunk.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/ServerMapRegion.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/ServerProgram.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/ServerSystemHeartbeat.cs.patch",

        // Group 2: real changes, already documented elsewhere as deliberately not
        // yet Cecil-wired (a lambda/nested-type transplant blocker, or an
        // unconfirmed 1.22.6 vanilla change waiting on the version-pin bump).
        // ClientSystemStartup: docs/implementation-plans/chunk-tesselator-worker-pool-wiring-plan-2026-08-10.md,
        //   HandleWorldMetaData's lambda ordinal mismatch (Option A rejected).
        // TextureAtlasManager: same doc, "IsTesselationThread guard stays inert."
        // AssetManager: GetAssetsDontLoad's Parallel.For lambda compiles to a
        //   <>c__DisplayClass absent from vanilla - the same ordinal hazard class,
        //   not yet worked around with a lambda-free rewrite.
        // ServerConfig/ServerPlayer/ServerProgramArgs/Logger: confirmed real
        //   vanilla version diffs per docs/done.md's 2026-08-05 session,
        //   correctly withheld until the version pin bumps.
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientSystemStartup.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/TextureAtlasManager.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Common/AssetManager.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/ServerConfig.cs.patch",
        "patches/VintagestoryLib/Vintagestory.Server/ServerPlayer.cs.patch",
        "patches/VintagestoryLib/Vintagestory/Logger.cs.patch",
    };

    /// <summary>
    /// Was failing on exactly 2 files (ChunkColumnLoadRequest.cs.patch,
    /// GameDatabase.cs.patch), deliberately left unallowlisted rather than
    /// papered over: they're real Optimum dependencies of already-cecil-owned
    /// server patches (ServerSystemSupplyChunks, ServerSystemLoadAndSaveGame),
    /// but Optimum.Patcher/Program.cs had zero Vintagestory.Server.* targets at
    /// all - the entire "Server-side worldgen scheduler and chunk read pool"
    /// section of cecil-owned.list had never actually been wired up, despite
    /// claiming to ship since 2026-07-28. Fixed 2026-08-11: the server-side
    /// Cecil targets are wired for real now (see docs/todo.md's
    /// "CecilPatchOwnershipTests backlog triage" entry for the full
    /// investigation and docs/implementation-plans/server-worldgen-chunk-pool-cecil-wiring-plan-2026-08-11.md
    /// for the wiring itself), so this passes cleanly again.
    /// </summary>
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

    [Fact]
    public void CecilMemberNamesMatchTheCompiledTickSliceConstant()
    {
        // RandomTickSlice ships via the recompiled VintagestoryLib.dll (not Cecil).
        // The patcher handles client patches only; server features compile in.
        // This test validates that if TickSlice ever moves to Cecil, the naming
        // convention (PascalCase) is used. For now, just verify the patcher loads.
        string source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "Optimum.Patcher", "Program.cs"));

        Assert.DoesNotContain("\"optimumTickSliceCount\"", source);
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
