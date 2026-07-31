using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// OptimumOutfitAnimatorCache is a brand-new type (not member-injected into an existing vanilla
/// type), so its own static field initializer (Cache = new()) runs correctly when Cecil clones
/// the whole type via InjectTypes - see OptimumOutfitShapeCacheCoverageTests for the same
/// reasoning and CecilInjectedFieldInitializerTests for the four real crashes this class of bug
/// caused historically. This guards the same registration for the animator cache, plus the new
/// optimumAnimatorCacheKey field injected into EntityDressedHumanoid (an existing type, so this
/// one field-only injects correctly since it has no initializer to lose - defaults to null,
/// which is exactly what a fresh instance field would be anyway).
/// </summary>
public class OptimumOutfitAnimatorCacheCoverageTests
{
    [Fact]
    public void CacheTypeIsRegisteredForWholeTypeInjectionNotMemberInjection()
    {
        string patcher = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains("\"Vintagestory.GameContent.OptimumOutfitAnimatorCache\",", patcher);
    }

    [Fact]
    public void OnTesselationThreeArgOverloadIsRegisteredAsAMethodTransplantTarget()
    {
        string patcher = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains(
            "new(\"Vintagestory.GameContent.EntityDressedHumanoid\", \"OnTesselation\", 3),",
            patcher);
    }

    [Fact]
    public void AnimatorCacheKeyFieldIsRegisteredForMemberInjection()
    {
        string patcher = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains("\"optimumAnimatorCacheKey\",", patcher);
    }

    [Fact]
    public void NewSourceFileIsCopiedIntoBothRuntimeDonorScripts()
    {
        string bashScript = Read("scripts/prepare-runtime-donors.sh");
        string ps1Script = Read("scripts/prepare-runtime-donors.ps1");

        Assert.Contains("sources/VSSurvivalMod/Lore/Village/OptimumOutfitAnimatorCache.cs", bashScript);
        Assert.Contains("VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitAnimatorCache.cs", bashScript);
        Assert.Contains("sources/VSSurvivalMod/Lore/Village/OptimumOutfitAnimatorCache.cs", ps1Script);
        Assert.Contains("VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitAnimatorCache.cs", ps1Script);
    }

    [Fact]
    public void SourcesCopyAndWorkingTreeFileMatch()
    {
        string sourcesCopy = Read("sources/VSSurvivalMod/Lore/Village/OptimumOutfitAnimatorCache.cs");
        string workingCopy = Read("VSSurvivalMod/Lore/Village/OptimumOutfitAnimatorCache.cs");

        Assert.Equal(sourcesCopy, workingCopy);
    }

    [Fact]
    public void PatchFallsBackToVanillaLoadAnimatorOnCacheMissAndStoresResult()
    {
        // The whole safety argument mirrors OptimumOutfitShapeCache: a miss must fall through to
        // vanilla's own (correct, if expensive) AnimManager.LoadAnimator, and the result gets
        // stored for next time - never silently skip building an animator.
        string patch = Read("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/EntityDressedHumanoid.cs.patch");

        Assert.Contains("OptimumOutfitAnimatorCache.TryApply(", patch);
        Assert.Contains("AnimManager.LoadAnimator(World.Api, this, entityShape, AnimManager.Animator?.Animations, requirePosesOnServer, willDisableElements, \"head\");", patch);
        Assert.Contains("OptimumOutfitAnimatorCache.Store(optimumAnimatorCacheKey, entityShape, AnimManager.Animator);", patch);
    }

    [Fact]
    public void PatchGatesOnEnabledFlagBeforeDuplicatingVanillaOnTesselationLogic()
    {
        // The duplicated overlay/behavior/willDisableElements block is only safe under the
        // assumption documented on OptimumConfig.EntityOutfitAnimatorCacheEnabled (trader/
        // villager entity types have none of those); when the flag is off, this must delegate
        // straight to base.OnTesselation instead of running the duplicated copy for no reason.
        string patch = Read("patches/runtime/VSSurvivalMod/Vintagestory/GameContent/EntityDressedHumanoid.cs.patch");

        Assert.Contains("if (!OptimumOutfitAnimatorCache.Enabled)", patch);
        Assert.Contains("base.OnTesselation(ref entityShape, shapePathForLogging, ref shapeIsCloned);", patch);
    }

    [Fact]
    public void CacheConfigDefaultsOff()
    {
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");

        Assert.Contains("public static bool EntityOutfitAnimatorCacheEnabled = false;", config);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
