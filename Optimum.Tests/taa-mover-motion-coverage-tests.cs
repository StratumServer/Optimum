using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Source coverage for the TAA P4 "movers" carry-over: the standard-shader block
/// entity renderers that actually move and therefore have to write exact motion
/// instead of ghosting on the resolve's camera fallback.
///
/// The load-bearing test here is <see cref="EveryStandardShaderUserInTheModForksIsInstrumentedOrExplicitlyExempt" />.
/// It enumerates the mod forks rather than listing files, so a renderer added or
/// un-instrumented later fails the build instead of silently ghosting: the failure
/// mode this phase exists to fix is invisible in a screenshot of a still scene and
/// only shows up as a smear while something on screen is animating.
///
/// Text assertions only prove the wiring exists - the GPU test
/// (Optimum.Render.Vulkan.Tests/TaaMoverMotionTests) proves the numbers.
/// </summary>
public class TaaMoverMotionCoverageTests
{
    /// <summary>The mod forks that are sources of truth for mod code.</summary>
    private static readonly string[] ModForks = { "VSEssentials", "VSSurvivalMod", "VSCreativeMod" };

    /// <summary>
    /// Every standard-shader user that is deliberately NOT instrumented, with the
    /// reason. Adding a file here is a decision, not a shortcut: "it does not move"
    /// has to be true, because the resolve's camera reprojection is exact only for
    /// a surface that is static in the world.
    ///
    /// Keys are repository-relative paths with forward slashes.
    /// </summary>
    private static readonly Dictionary<string, string> NotInstrumented = new()
    {
        ["VSSurvivalMod/BlockEntityRenderer/AnvilPartRenderer.cs"] =
            "The anvil base, flux and work item sit at the block position. The top mesh sinks by " +
            "hammerHits/250 - a discrete step on a hit, not a per-frame animation - so a wrong " +
            "vector would last one frame; the hot work item itself is drawn by AnvilWorkItemRenderer " +
            "on the mod's own smithing program, which declares no motion output at all.",

        ["VSSurvivalMod/BlockEntityRenderer/BlockEntitySignPostRenderer.cs"] =
            "A text quad nailed to the sign post at a fixed offset from the block position.",

        ["VSSurvivalMod/BlockEntityRenderer/ChestLabelRenderer.cs"] =
            "A label quad nailed to the chest at a fixed offset from the block position.",

        ["VSSurvivalMod/BlockEntityRenderer/ClayFormRenderer.cs"] =
            "The work item is drawn at the block position and only changes when a voxel is added " +
            "or removed, which re-uploads the mesh. Its second draw is the recipe outline on " +
            "AfterFinalComposition, which is outside the temporal window entirely.",

        ["VSSurvivalMod/BlockEntityRenderer/CrucibleInFirepitRenderer.cs"] =
            "The crucible sits still in the firepit; only its glow changes.",

        ["VSSurvivalMod/BlockEntityRenderer/GroundStorageRenderer.cs"] =
            "Stacks are drawn at fixed offsets inside the block; the per-frame work is a " +
            "once-a-second temperature refresh, not motion.",

        ["VSSurvivalMod/BlockEntityRenderer/IngotMoldRenderer.cs"] =
            "The fill quad's height changes only when metal is poured in or the mold is emptied, " +
            "in discrete steps, and a step swaps the mesh, so history would be rejected anyway.",

        ["VSSurvivalMod/BlockEntityRenderer/KnappingRenderer.cs"] =
            "The knapping surface is drawn at the block position and changes only when a voxel is " +
            "knocked off, which re-uploads the mesh. Its AfterFinalComposition guide draw is " +
            "outside the temporal window entirely.",

        ["VSSurvivalMod/BlockEntityRenderer/SignRenderer.cs"] =
            "A text quad nailed to the sign at a fixed offset from the block position.",

        ["VSSurvivalMod/BlockEntityRenderer/ToolMoldRenderer.cs"] =
            "As IngotMoldRenderer: the fill level changes in discrete steps and each step picks a " +
            "different quad mesh, so there is no continuous motion to record.",

        ["VSSurvivalMod/Systems/SupportBeams/ModSystemSupportBeamPlacer.cs"] =
            "The beam preview follows the player's aim, but its model matrix is the fixed start " +
            "block and the shape is re-uploaded (reloadMeshRef) on every change of the end offset, " +
            "so a previous model matrix would never be valid history.",
    };

    /// <summary>
    /// Standard-shader users that must carry the writer. The P3 three (held items,
    /// dropped items, the quern) plus the P4 movers.
    /// </summary>
    private static readonly string[] Instrumented =
    {
        "VSEssentials/EntityRenderer/EntityShapeRenderer.cs",
        "VSEssentials/EntityRenderer/EntityItemRenderer.cs",
        "VSEssentials/Entities/EntityBlockFalling.cs",
        "VSSurvivalMod/BlockEntityRenderer/QuernTopRenderer.cs",
        "VSSurvivalMod/BlockEntityRenderer/HelveHammerRenderer.cs",
        "VSSurvivalMod/BlockEntityRenderer/FruitpressContentsRenderer.cs",
        "VSSurvivalMod/BlockEntityRenderer/ResonatorRenderer.cs",
        "VSSurvivalMod/BlockEntityRenderer/BloomeryContentsRenderer.cs",
        "VSSurvivalMod/BlockEntityRenderer/ForgeContentsRenderer.cs",
        "VSSurvivalMod/BlockEntityRenderer/FirepitContentsRenderer.cs",
        "VSSurvivalMod/BlockEntityRenderer/PotInFirepitRenderer.cs",
    };

    /// <summary>
    /// The named list and the scan have to agree: the scan is what catches a new
    /// renderer, the list is what catches an instrumented one quietly losing its
    /// writer while still being found by the scan.
    /// </summary>
    [Fact]
    public void EveryNamedInstrumentedRendererIsFoundByTheScanAndStillWritesMotion()
    {
        List<string> users = StandardShaderUsers();

        foreach (string source in Instrumented)
        {
            Assert.Contains(source, users);
            Assert.Contains("OptimumStandardMotion.Apply(", ReadRepositoryFile(source));
        }
    }

    // ------------------------------------------------------------- the census

    /// <summary>
    /// The table that makes the phase checkable: every file in the mod forks that
    /// draws through the standard shader program is either a motion writer or has
    /// a written reason not to be. Nothing may be silently absent.
    /// </summary>
    [Fact]
    public void EveryStandardShaderUserInTheModForksIsInstrumentedOrExplicitlyExempt()
    {
        List<string> users = StandardShaderUsers();

        // If the discovery itself breaks, everything below passes vacuously.
        Assert.True(users.Count >= 20,
            "only " + users.Count + " standard-shader users found; the scan is broken");

        var missing = new List<string>();
        foreach (string user in users)
        {
            string text = ReadRepositoryFile(user);
            bool instrumented = text.Contains("OptimumStandardMotion.Apply(", StringComparison.Ordinal);
            bool exempt = NotInstrumented.ContainsKey(user);

            if (instrumented && exempt)
            {
                missing.Add(user + " is instrumented AND on the exemption list");
            }
            else if (!instrumented && !exempt)
            {
                missing.Add(user + " draws on the standard shader, writes no motion, and has no " +
                            "reason on TaaMoverMotionCoverageTests.NotInstrumented");
            }
        }

        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    /// <summary>
    /// An exemption without a reason is a to-do pretending to be a decision.
    /// </summary>
    [Fact]
    public void EveryExemptionCarriesARealReasonAndNamesAFileThatStillExists()
    {
        foreach ((string path, string reason) in NotInstrumented)
        {
            Assert.True(reason.Length >= 60, path + ": the exemption reason is too thin to be one");
            Assert.True(
                File.Exists(Path.Combine(RepositoryRoot(), path.Replace('/', Path.DirectorySeparatorChar))),
                path + " is on the exemption list but no longer exists");
        }
    }

    /// <summary>
    /// The exemption list must not grow stale in the other direction either: a file
    /// listed there that no longer draws on the standard shader is a leftover.
    /// </summary>
    [Fact]
    public void NoExemptionNamesAFileThatNoLongerDrawsOnTheStandardShader()
    {
        List<string> users = StandardShaderUsers();
        foreach (string path in NotInstrumented.Keys)
        {
            Assert.Contains(path, users);
        }
    }

    // -------------------------------------------------------- the instrumented

    /// <summary>
    /// Each mover names itself to the per-object store and opens the draw-buffer
    /// window around its own draw. The window has to be narrow: a standard-shader
    /// draw that is not instrumented must stay outside it, or the attachment would
    /// keep whatever surface wrote there before.
    /// </summary>
    [Theory]
    [InlineData("VSSurvivalMod/BlockEntityRenderer/HelveHammerRenderer.cs",
        "OptimumStandardMotion.Apply(prog, this, meshref, ModelMat.Values);")]
    [InlineData("VSSurvivalMod/BlockEntityRenderer/FruitpressContentsRenderer.cs",
        "OptimumStandardMotion.Apply(prog, this, mashMeshref, ModelMat.Values);")]
    [InlineData("VSSurvivalMod/BlockEntityRenderer/ResonatorRenderer.cs",
        "OptimumStandardMotion.Apply(prog, this, cylinderMeshRef, ModelMat.Values);")]
    [InlineData("VSSurvivalMod/BlockEntityRenderer/BloomeryContentsRenderer.cs",
        "OptimumStandardMotion.Apply(prog, this, cubeModelRef, ModelMat.Values);")]
    [InlineData("VSSurvivalMod/BlockEntityRenderer/ForgeContentsRenderer.cs",
        "OptimumStandardMotion.Apply(prog, this, coalMeshRef, ModelMat.Values);")]
    [InlineData("VSSurvivalMod/BlockEntityRenderer/FirepitContentsRenderer.cs",
        "OptimumStandardMotion.Apply(prog, this, meshref, ModelMat.Values);")]
    [InlineData("VSEssentials/Entities/EntityBlockFalling.cs",
        "OptimumStandardMotion.Apply(prog, entity, entity.meshRef, ModelMat.Values);")]
    public void EveryMoverStoresItsPreviousTransformAndOpensTheWindow(string source, string apply)
    {
        string renderer = ReadRepositoryFile(source);

        Assert.Contains(apply, renderer);
        Assert.Contains("OptimumMotionWrite.Begin();", renderer);
        Assert.Contains("OptimumMotionWrite.End();", renderer);

        // Paired, and closed on the exception path: a window left open would put
        // the motion attachment in every later draw's mask. A file-wide search for
        // "finally" would pass on a renderer whose window is closed by a bare call
        // while some unrelated method has the keyword, so check it per window.
        Assert.Equal(
            Count(renderer, "OptimumMotionWrite.Begin();"),
            Count(renderer, "OptimumMotionWrite.End();"));
        AssertEveryWindowClosesInAFinally(source, renderer);
    }

    /// <summary>
    /// For every <c>OptimumMotionWrite.Begin();</c>, the <c>End();</c> that closes it
    /// must sit in a <c>finally</c> that opens after that Begin.
    /// </summary>
    private static void AssertEveryWindowClosesInAFinally(string source, string renderer)
    {
        const string beginCall = "OptimumMotionWrite.Begin();";
        const string endCall = "OptimumMotionWrite.End();";

        for (int begin = renderer.IndexOf(beginCall, StringComparison.Ordinal); begin >= 0;
             begin = renderer.IndexOf(beginCall, begin + beginCall.Length, StringComparison.Ordinal))
        {
            int end = renderer.IndexOf(endCall, begin, StringComparison.Ordinal);
            Assert.True(end > begin, source + ": a motion window opens and is never closed");

            int keyword = renderer.IndexOf("finally", begin, StringComparison.Ordinal);
            Assert.True(keyword > begin && keyword < end,
                source + ": the motion window opened at offset " + begin +
                " is not closed inside a finally block");
        }
    }

    /// <summary>
    /// The resonator runs the same body again on AfterFinalComposition, where
    /// Begin() refuses because the temporal window is closed. Rolling the transform
    /// history there would overwrite the identity's current transform with a later,
    /// time-driven ModelMat, so next frame's previous transform would be off by a
    /// sub-frame delta. Apply must therefore sit inside the window.
    /// </summary>
    [Fact]
    public void TheResonatorRollsItsHistoryOnlyInsideTheWindow()
    {
        string renderer = ReadRepositoryFile("VSSurvivalMod/BlockEntityRenderer/ResonatorRenderer.cs");

        int begin = renderer.IndexOf("OptimumMotionWrite.Begin();", StringComparison.Ordinal);
        int apply = renderer.IndexOf("OptimumStandardMotion.Apply(", StringComparison.Ordinal);
        int end = renderer.IndexOf("OptimumMotionWrite.End();", StringComparison.Ordinal);

        Assert.True(begin >= 0 && apply > begin && apply < end,
            "the resonator must roll its transform history inside the motion window");
        Assert.Contains("if (optimumMotionWrite)", renderer);
    }

    /// <summary>
    /// Two draws in one renderer need two identities. The pot body and its lid
    /// share a renderer instance and a Matrixf, so keying both on <c>this</c> would
    /// hand the lid the body's previous matrix - a zero vector on the one part of
    /// the pot that actually moves.
    /// </summary>
    [Fact]
    public void ThePotAndItsLidKeepSeparateHistories()
    {
        string renderer = ReadRepositoryFile("VSSurvivalMod/BlockEntityRenderer/PotInFirepitRenderer.cs");

        Assert.Contains(
            "OptimumStandardMotion.Apply(prog, this, potRef == null ? potWithFoodRef : potRef, ModelMat.Values);",
            renderer);
        Assert.Contains("OptimumStandardMotion.Apply(prog, lidRef, lidRef, ModelMat.Values);", renderer);
        Assert.Equal(2, Count(renderer, "OptimumStandardMotion.Apply("));
        Assert.Equal(2, Count(renderer, "OptimumMotionWrite.Begin();"));
        Assert.Equal(2, Count(renderer, "OptimumMotionWrite.End();"));
    }

    /// <summary>
    /// The falling-block renderer is one renderer for every falling block in view,
    /// so the identity has to be the entity. Keying on the renderer would give
    /// every block the last block's previous matrix.
    /// </summary>
    [Fact]
    public void FallingBlocksKeyTheirHistoryOnTheEntityAndShareOneWindow()
    {
        string renderer = ReadRepositoryFile("VSEssentials/Entities/EntityBlockFalling.cs");

        Assert.Contains("OptimumStandardMotion.Apply(prog, entity, entity.meshRef, ModelMat.Values);", renderer);
        Assert.DoesNotContain("OptimumStandardMotion.Apply(prog, this,", renderer);

        // One window around the loop, not one per block.
        Assert.Equal(1, Count(renderer, "OptimumMotionWrite.Begin();"));
        int begin = renderer.IndexOf("OptimumMotionWrite.Begin();", StringComparison.Ordinal);
        int loop = renderer.IndexOf("foreach (var entity in fallingBlocks.Values)", StringComparison.Ordinal);
        Assert.True(begin >= 0 && loop > begin, "the window must open before the loop, not inside it");
    }

    /// <summary>
    /// The helve hammer runs the same method for the shadow stages with a program
    /// that has no motion output, so the window must sit inside its Opaque branch.
    /// </summary>
    [Fact]
    public void TheHelveHammerOpensNoWindowInItsShadowBranch()
    {
        string renderer = ReadRepositoryFile("VSSurvivalMod/BlockEntityRenderer/HelveHammerRenderer.cs");

        int opaque = renderer.IndexOf("if (stage == EnumRenderStage.Opaque)", StringComparison.Ordinal);
        int elseBranch = renderer.IndexOf("} else", opaque, StringComparison.Ordinal);
        int begin = renderer.IndexOf("OptimumMotionWrite.Begin();", StringComparison.Ordinal);

        Assert.True(opaque >= 0 && elseBranch > opaque, "the stage branch moved");
        Assert.InRange(begin, opaque, elseBranch);
    }

    // --------------------------------------------------------------- the ship

    /// <summary>
    /// P3 finding (f): a mod-fork change only reaches the installed runtime through
    /// a mod-patcher manifest entry. Without these the vanilla bodies stay and every
    /// mover ghosts there while the build tree looks correct.
    /// </summary>
    [Theory]
    [InlineData("Vintagestory.GameContent.ModSystemRenderFallingBlocksFast")]
    [InlineData("Vintagestory.GameContent.HelveHammerRenderer")]
    [InlineData("Vintagestory.GameContent.FruitpressContentsRenderer")]
    [InlineData("Vintagestory.GameContent.ResonatorRenderer")]
    [InlineData("Vintagestory.GameContent.BloomeryContentsRenderer")]
    [InlineData("Vintagestory.GameContent.ForgeContentsRenderer")]
    [InlineData("Vintagestory.GameContent.FirepitContentsRenderer")]
    [InlineData("Vintagestory.GameContent.PotInFirepitRenderer")]
    public void ModPatcherManifestsCarryEveryChangedMover(string type)
    {
        string manifest = ReadRepositoryFile("Optimum.Patcher/mod-patcher.cs");

        Assert.Contains("new(\"" + type + "\", \"OnRenderFrame\", 2)", manifest);
    }

    // ----------------------------------------------------------------- helpers

    /// <summary>
    /// Every C# file in the mod forks that draws through the standard shader
    /// program, repository-relative with forward slashes. Discovery is by scan so
    /// that a new renderer cannot be missed by being absent from a list.
    /// </summary>
    private static List<string> StandardShaderUsers()
    {
        string root = RepositoryRoot();
        var users = new List<string>();

        foreach (string fork in ModForks)
        {
            string forkRoot = Path.Combine(root, fork);
            if (!Directory.Exists(forkRoot)) continue;

            foreach (string file in Directory.EnumerateFiles(forkRoot, "*.cs", SearchOption.AllDirectories))
            {
                string text = File.ReadAllText(file);
                if (!UsesStandardShader(text)) continue;

                users.Add(Path.GetRelativePath(root, file).Replace('\\', '/'));
            }
        }

        users.Sort(StringComparer.Ordinal);
        return users;
    }

    /// <summary>
    /// A file draws on the standard shader when it names it at all - the type
    /// <c>IStandardShaderProgram</c>, the <c>StandardShader</c> property or
    /// <c>PreparedStandardShader</c>, every one of which contains the same
    /// substring - and then draws a mesh with it. Deliberately loose on the first
    /// half: over-reporting costs an exemption line, under-reporting costs a
    /// ghosting renderer nobody notices.
    /// </summary>
    private static bool UsesStandardShader(string text)
    {
        if (!text.Contains("StandardShader", StringComparison.Ordinal)) return false;

        return text.Contains("RenderMesh(", StringComparison.Ordinal) ||
               text.Contains("RenderMultiTextureMesh(", StringComparison.Ordinal);
    }

    private static int Count(string text, string needle)
    {
        int count = 0;
        for (int i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        return File.ReadAllText(Path.Combine(RepositoryRoot(),
            relativePath.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static string RepositoryRoot()
    {
        return Path.GetDirectoryName(PatchReader.FindRepositoryFile("TAA-PLAN.md"))!;
    }
}
