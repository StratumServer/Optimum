using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// OptimumOutfitTexturePrewarmerModSystem is a brand-new type, registered for whole-type Cecil
/// injection (own .cctor/static init runs correctly) rather than field injection into an
/// existing type - see OptimumOutfitShapeCacheCoverageTests and
/// CecilInjectedFieldInitializerTests for why that distinction matters. This guards the same
/// registration for the prewarmer.
/// </summary>
public class OptimumOutfitTexturePrewarmerCoverageTests
{
    [Fact]
    public void PrewarmerTypeIsRegisteredForWholeTypeInjection()
    {
        string patcher = Read("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains("\"Vintagestory.GameContent.OptimumOutfitTexturePrewarmerModSystem\",", patcher);
    }

    [Fact]
    public void NewSourceFileIsCopiedIntoBothRuntimeDonorScripts()
    {
        string bashScript = Read("scripts/prepare-runtime-donors.sh");
        string ps1Script = Read("scripts/prepare-runtime-donors.ps1");

        Assert.Contains("sources/VSSurvivalMod/Lore/Village/OptimumOutfitTexturePrewarmer.cs", bashScript);
        Assert.Contains("VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitTexturePrewarmer.cs", bashScript);
        Assert.Contains("sources/VSSurvivalMod/Lore/Village/OptimumOutfitTexturePrewarmer.cs", ps1Script);
        Assert.Contains("VSSurvivalMod/Vintagestory/GameContent/OptimumOutfitTexturePrewarmer.cs", ps1Script);
    }

    [Fact]
    public void SourcesCopyAndWorkingTreeFileMatch()
    {
        string sourcesCopy = Read("sources/VSSurvivalMod/Lore/Village/OptimumOutfitTexturePrewarmer.cs");
        string workingCopy = Read("VSSurvivalMod/Lore/Village/OptimumOutfitTexturePrewarmer.cs");

        Assert.Equal(sourcesCopy, workingCopy);
    }

    [Fact]
    public void PrewarmerHooksLevelFinalizeNotAnArbitraryThread()
    {
        // GetOrInsertTexture can touch GL (atlas allocation/upload), which requires the render
        // thread. capi.Event.LevelFinalize is a documented main-thread event (the same one
        // GuiManager.OnLevelFinalize already hooks elsewhere in this codebase) - guard against
        // this drifting to a background TyronThreadPool.QueueTask, which would crash or corrupt
        // the atlas on a GL-context-less thread.
        string source = Read("VSSurvivalMod/Lore/Village/OptimumOutfitTexturePrewarmer.cs");

        Assert.Contains("api.Event.LevelFinalize +=", source);
        Assert.DoesNotContain("TyronThreadPool", source);
    }

    [Fact]
    public void PrewarmFailuresArePerConfigNotFatal()
    {
        // One malformed outfit config must not abort the whole prewarm pass or crash the
        // loading screen - see the class's own doc comment for the reasoning.
        string source = Read("VSSurvivalMod/Lore/Village/OptimumOutfitTexturePrewarmer.cs");

        Assert.Contains("catch (System.Exception e)", source);
    }

    [Fact]
    public void PrewarmConfigDefaultsOff()
    {
        string config = Read("sources/VintagestoryApi/Config/OptimumConfig.cs");

        Assert.Contains("public static bool EntityOutfitTexturePrewarmEnabled = false;", config);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
