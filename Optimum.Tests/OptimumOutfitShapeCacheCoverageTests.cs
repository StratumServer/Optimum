using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// OptimumOutfitShapeCache is a brand-new type (not member-injected into an existing vanilla
/// type), so its own static field initializer (Cache = new()) runs correctly when Cecil clones
/// the whole type via InjectTypes - unlike per-field injection into an already-compiled type
/// (see CecilInjectedFieldInitializerTests), which never re-runs a constructor. This class
/// guards the registration that makes that safe: the type must stay in mod-patcher.cs's Types
/// list (whole-type clone, own .cctor included), never move to the Members dictionary
/// (field-only injection into EntityDressedHumanoid, which would silently drop the initializer
/// exactly like the four real crashes CecilInjectedFieldInitializerTests documents).
/// </summary>
public class OptimumOutfitShapeCacheCoverageTests
{
    [Fact]
    public void CacheTypeIsRegisteredForWholeTypeInjectionNotMemberInjection()
    {
        string patcher = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains("\"Vintagestory.GameContent.OptimumOutfitShapeCache\",", patcher);
    }

    [Fact]
    public void OnTesselationIsRegisteredAsAMethodTransplantTarget()
    {
        string patcher = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains(
            "new(\"Vintagestory.GameContent.EntityDressedHumanoid\", \"OnTesselation\", 2),",
            patcher);
    }

    [Fact]
    public void NewSourceFileIsCopiedIntoBothRuntimeDonorScripts()
    {
        // A whole-new-type overlay (no vanilla counterpart to diff against) has to be copied
        // into the decompiled tree directly - see the equivalent step for
        // OptimumStatusModSystem/CrucibleInFirepitRenderer a few lines above each of these.
        string bashScript = Read("scripts/prepare-runtime-donors.sh");
        string ps1Script = Read("scripts/prepare-runtime-donors.ps1");

        Assert.Contains("sources/VSSurvivalMod/Lore/Village/OptimumOutfitShapeCache.cs", bashScript);
        Assert.Contains("VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitShapeCache.cs", bashScript);
        Assert.Contains("sources/VSSurvivalMod/Lore/Village/OptimumOutfitShapeCache.cs", ps1Script);
        Assert.Contains("VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitShapeCache.cs", ps1Script);
    }

    [Fact]
    public void SourcesCopyAndWorkingTreeFileMatch()
    {
        // sources/ is the tracked file the runtime-donor pipeline actually copies from; the
        // gitignored working-tree copy under VSSurvivalMod/ must stay in sync with it, the same
        // way every other "new type" overlay in this repo does.
        string sourcesCopy = Read("sources/VSSurvivalMod/Lore/Village/OptimumOutfitShapeCache.cs");
        string workingCopy = Read("VSSurvivalMod/Lore/Village/OptimumOutfitShapeCache.cs");

        Assert.Equal(sourcesCopy, workingCopy);
    }

    [Fact]
    public void EntityDressedHumanoidPatchStoresAnIndependentCloneNotTheLiveShape()
    {
        // The whole safety argument for this cache hinges on Store() never being handed (and
        // TryGet() never handing out) the shape instance actually being mutated elsewhere -
        // OptimumOutfitShapeCache.Store() clones internally, so the call site here just needs
        // to pass its own local, not attempt to clone before calling (that would be redundant,
        // not wrong, but drifting from the class's own contract is a sign something regressed).
        string patch = Read("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/EntityDressedHumanoid.cs.patch");

        Assert.Contains("OptimumOutfitShapeCache.Store(optimumOutfitCacheKey, entityShape, fastSmallDictionary);", patch);
        Assert.Contains("OptimumOutfitShapeCache.TryGet(optimumOutfitCacheKey, out optimumCachedShape, out optimumCachedTextures)", patch);
    }

    [Fact]
    public void CacheConfigDefaultsOff()
    {
        // New code on the entity-appearance path, only compile/unit-test verified so far - see
        // OptimumConfig.EntityOutfitShapeCacheEnabled's own doc comment for the full reasoning.
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");

        Assert.Contains("public static bool EntityOutfitShapeCacheEnabled = false;", config);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
