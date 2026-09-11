using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The TAA motion writers live in two places at once. The fork trees
/// (VSEssentials/, VSSurvivalMod/) are what a from-source build compiles, and
/// `patches/&lt;mod&gt;/**` is their checked-in record. The installed-launcher path
/// never sees those: it decompiles the user's own mod assemblies, applies
/// `patches/runtime/&lt;mod&gt;/**` to that decompiled tree, compiles it, and lets
/// Cecil transplant the method bodies named in Optimum.Patcher/mod-patcher.cs.
/// A mover instrumented only in the fork therefore keeps its vanilla body for
/// every installed player: it draws with no motion vector and ghosts on the
/// camera fallback, silently, with every fork test still green.
///
/// These tests pin the two sides together. They read only checked-in patch
/// files, never the git-ignored fork trees, so they run the same in a clean
/// clone as on a developer machine.
/// </summary>
public sealed class TaaRuntimeDonorCoverageTests
{
    /// <summary>
    /// The calls that mark a class as a TAA motion writer. A fork patch that
    /// adds any of them describes work the installed runtime needs too.
    /// </summary>
    private static readonly string[] MotionMarkers =
    [
        "OptimumStandardMotion.Apply",
        "OptimumMotionWrite.Begin",
        "OptimumMotionWrite.End",
        "OptimumInstanceMotion.CreateInstanceFloats",
        "OptimumInstanceMotion.WriteInstance",
        "OptimumInstanceMotion.NoteDevice",
        "OptimumInstanceMotion.ApplyPassUniforms",
        "OptimumInstanceMotion.InstanceFloats",
        "OptimumConfig.EffectiveTaa",
    ];

    /// <summary>
    /// Fork patch -> runtime donor patch, both repository-relative. The mapping
    /// is spelled out rather than derived because the two trees are shaped
    /// differently: the fork keeps its own folders and file names
    /// (Entities/EntityBlockFalling.cs holds ModSystemRenderFallingBlocksFast;
    /// AngledGearBlockRenderer.cs holds AngledGearsBlockRenderer) while ILSpy
    /// lays the donor out by namespace and names each file after its type.
    /// <see cref="EveryInstrumentedForkPatchIsListedHere"/> fails if a new
    /// motion writer appears in a fork patch without an entry here.
    /// </summary>
    private static readonly Dictionary<string, string> DonorByForkPatch = new()
    {
        ["patches/VSEssentials/Entities/EntityBlockFalling.cs.patch"] =
            "patches/runtime/VSEssentials/Vintagestory/GameContent/ModSystemRenderFallingBlocksFast.cs.patch",
        ["patches/VSEssentials/EntityRenderer/EntityItemRenderer.cs.patch"] =
            "patches/runtime/VSEssentials/Vintagestory/GameContent/EntityItemRenderer.cs.patch",
        ["patches/VSEssentials/EntityRenderer/EntityPlayerShapeRenderer.cs.patch"] =
            "patches/runtime/VSEssentials/Vintagestory/GameContent/EntityPlayerShapeRenderer.cs.patch",
        ["patches/VSEssentials/EntityRenderer/EntityShapeRenderer.cs.patch"] =
            "patches/runtime/VSEssentials/Vintagestory/GameContent/EntityShapeRenderer.cs.patch",
        ["patches/VSEssentials/EntityRenderer/ModSystemFpHands.cs.patch"] =
            "patches/runtime/VSEssentials/Vintagestory/GameContent/ModSystemFpHands.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/BloomeryContentsRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/BloomeryContentsRenderer.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/FirepitContentsRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/FirepitContentsRenderer.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/ForgeContentsRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/ForgeContentsRenderer.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/FruitpressContentsRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/FruitpressContentsRenderer.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/HelveHammerRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/HelveHammerRenderer.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/PotInFirepitRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/PotInFirepitRenderer.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/QuernTopRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/QuernTopRenderer.cs.patch",
        ["patches/VSSurvivalMod/BlockEntityRenderer/ResonatorRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/ResonatorRenderer.cs.patch",
        ["patches/VSSurvivalMod/Lore/ResoArchives/EchoChamberRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/EchoChamberRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/AngledCageGearRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/AngledCageGearRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/AngledGearBlockRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/AngledGearsBlockRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/ClutchBlockRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/ClutchBlockRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/CreativeRotorRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/CreativeRotorRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/GenericMechBlockRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/GenericMechBlockRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/MechBlockRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/MechBlockRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/MechNetworkRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/MechNetworkRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/PulverizerRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/PulverizerRenderer.cs.patch",
        ["patches/VSSurvivalMod/Systems/MechanicalPower/Renderer/TransmissionBlockRenderer.cs.patch"] =
            "patches/runtime/VSSurvivalMod/Vintagestory/GameContent/Mechanics/TransmissionBlockRenderer.cs.patch",
    };

    public static IEnumerable<object[]> DonorPairs =>
        DonorByForkPatch.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new object[] { pair.Key, pair.Value });

    [Theory]
    [MemberData(nameof(DonorPairs))]
    public void RuntimeDonorCarriesTheSameMotionWritersAsTheFork(string forkPatch, string runtimePatch)
    {
        string forkPath = Path.Combine(RepoRoot(), forkPatch);
        string runtimePath = Path.Combine(RepoRoot(), runtimePatch);

        Assert.True(File.Exists(forkPath), $"{forkPatch} is missing; refresh it with scripts/extract-patches.sh.");
        Assert.True(
            File.Exists(runtimePath),
            $"{runtimePatch} is missing. The fork instruments this class for TAA but the installed-launcher " +
            "path has no donor for it, so Cecil would transplant a vanilla body and the surface would ghost.");

        var forkMarkers = MarkersAdded(forkPath);
        var runtimeMarkers = MarkersAdded(runtimePath);

        Assert.True(
            forkMarkers.Count > 0,
            $"{forkPatch} no longer adds any TAA motion call; drop its entry from DonorByForkPatch.");

        var missing = forkMarkers.Except(runtimeMarkers).OrderBy(m => m, StringComparer.Ordinal).ToList();
        Assert.True(
            missing.Count == 0,
            $"{runtimePatch} is behind {forkPatch}: the fork adds {string.Join(", ", missing)} but the runtime " +
            "donor does not. Regenerate the donor patch against a pristine .build/runtime-donors decompile.");
    }

    [Fact]
    public void EveryInstrumentedForkPatchIsListedHere()
    {
        var instrumented = new List<string>();
        foreach (string project in new[] { "VSEssentials", "VSSurvivalMod", "VSCreativeMod" })
        {
            string dir = Path.Combine(RepoRoot(), "patches", project);
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (string file in Directory.EnumerateFiles(dir, "*.patch", SearchOption.AllDirectories))
            {
                if (MarkersAdded(file).Count > 0)
                {
                    instrumented.Add(Relative(file));
                }
            }
        }

        var unlisted = instrumented
            .Where(path => !DonorByForkPatch.ContainsKey(path))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            unlisted.Count == 0,
            "These fork patches add TAA motion calls but have no runtime donor mapping in " +
            "TaaRuntimeDonorCoverageTests.DonorByForkPatch, so the installed-launcher path would keep " +
            "vanilla bodies for them:\n  " + string.Join("\n  ", unlisted));
    }

    [Fact]
    public void EveryMappedForkPatchStillExists()
    {
        var gone = DonorByForkPatch.Keys
            .Where(path => !File.Exists(Path.Combine(RepoRoot(), path)))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            gone.Count == 0,
            "DonorByForkPatch names fork patches that no longer exist:\n  " + string.Join("\n  ", gone));
    }

    /// <summary>
    /// The motion calls a patch <em>adds</em> (+ lines only). Context lines are
    /// excluded on purpose: a call that merely sits next to an unrelated hunk is
    /// not this patch's work.
    /// </summary>
    private static HashSet<string> MarkersAdded(string patchFile)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(patchFile))
        {
            if (!line.StartsWith("+", StringComparison.Ordinal) || line.StartsWith("+++", StringComparison.Ordinal))
            {
                continue;
            }
            foreach (string marker in MotionMarkers)
            {
                if (line.Contains(marker, StringComparison.Ordinal))
                {
                    found.Add(marker);
                }
            }
        }
        return found;
    }

    private static string RepoRoot() =>
        Path.GetDirectoryName(PatchReader.FindRepositoryFile("VERSION"))!;

    private static string Relative(string path) =>
        Path.GetRelativePath(RepoRoot(), path).Replace('\\', '/');
}
