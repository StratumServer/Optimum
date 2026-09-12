using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// DLSS plan, Phase 2 steps 1 and 2: with an upscaler active it owns the
/// temporal resolve, and nothing else may do that job at the same time.
///
/// Three passes stand down - the TAA resolve, the TAA sharpen and the FSR 1
/// blit - while everything that <i>produces</i> the upscaler's inputs stays on:
/// the jitter window, the motion attachment, every motion writer's shader
/// variant and the previous-bone UBO. That split is the whole of this phase's
/// gating, and it is the one thing a later refactor can silently get wrong, so
/// each half is pinned here against the shipped source.
/// </summary>
public class UpscalerMutualExclusionCoverageTests
{
    private const string PlatformPatch =
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch";
    private const string PlatformSource =
        "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs";

    /// <summary>
    /// The resolve refuses before it looks at its own targets: with both TAA and
    /// an upscaler switched on, the upscaler wins rather than the two blending
    /// the same frame into one history twice over.
    /// </summary>
    [Fact]
    public void TheTaaResolveStandsDownForAnUpscaler()
    {
        string body = MethodBodyAfter(Platform(), "public override bool RenderOptimumTaaResolve()");

        Assert.Contains("if (Vintagestory.API.Config.OptimumConfig.UpscalerReplacesTaa)", body);
        int upscaler = body.IndexOf("UpscalerReplacesTaa", StringComparison.Ordinal);
        int targets = body.IndexOf("if (!TaaTargetsReady", StringComparison.Ordinal);
        Assert.True(upscaler > 0 && targets > upscaler,
            "the upscaler check must come before the TAA targets check, so it wins when both are on:\n" + body);
        // And it drops the history rather than leaving it to be reprojected later.
        int history = body.IndexOf("_taaHistoryValid = false;", upscaler, StringComparison.Ordinal);
        Assert.True(history > upscaler && history < targets, "the refusal must invalidate the history:\n" + body);
    }

    /// <summary>
    /// The sharpen pass too: the upscaler's output is already reconstructed and
    /// sharpened by the vendor, and ours would be a second RCAS-shaped pass over
    /// it - the exact double-sharpening the FSR exclusion exists to avoid.
    /// </summary>
    [Fact]
    public void TheTaaSharpenStandsDownForAnUpscaler()
    {
        string body = MethodBodyAfter(Platform(), "public override int RenderOptimumTaaSharpen(int resolvedScene)");
        Assert.Contains("if (OptimumConfig.UpscalerReplacesTaa)", body);
        Assert.Contains("return resolvedScene;", body);
    }

    /// <summary>
    /// FSR 1 is an upscaler as well, and the two cannot both own the frame: with
    /// a temporal upscaler active the blit is a plain blit again.
    /// </summary>
    [Fact]
    public void TheFsrBlitStandsDownForAnUpscaler()
    {
        string body = MethodBodyAfter(Platform(), "public override bool OptimumFsrBlitActive()");
        Assert.Contains("&& !Vintagestory.API.Config.OptimumConfig.UpscalerReplacesTaa", body);
        // The gate is still single-sourced: the blit and the sharpen pass both
        // read this one member, which is why it exists.
        Assert.Contains("&& ClientSettings.OptimumRenderScale < 1.0f", body);
    }

    /// <summary>
    /// The inputs stay on. The jitter window opens for an upscaler exactly as it
    /// does for our resolve, and the sequence length follows the ratio the frame
    /// is really rendered at - the vendor's, when one owns the resolve.
    /// </summary>
    [Fact]
    public void TheJitterStaysOnAndFollowsTheUpscalersOwnRatio()
    {
        string main = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");

        Assert.Contains(
            "OptimumTemporal.Frame.JitterActive = OptimumConfig.EffectiveTaa || OptimumConfig.TaaJitterDev || " +
            "OptimumConfig.UpscalerReplacesTaa;",
            main);
        Assert.Contains("OptimumConfig.EffectiveTemporalRenderScale, MainCamera.ZNear", main);
        // And the old expression is gone, so there is one answer to "what scale is
        // this frame jittered for".
        Assert.DoesNotContain("OptimumConfig.EffectiveRenderScale, MainCamera.ZNear", main);
    }

    /// <summary>
    /// The motion attachment is allocated for either consumer, while the history
    /// slots and the sharpen target stay TAA's own: an upscaler keeps its history
    /// inside the vendor runtime and has no use for ours.
    /// </summary>
    [Fact]
    public void TheMotionAttachmentIsAllocatedForEitherConsumerAndTheHistoryOnlyForTaa()
    {
        string platform = Platform();

        Assert.Contains(
            "bool temporalRequested = taaRequested || Vintagestory.API.Config.OptimumConfig.UpscalerReplacesTaa;",
            platform);
        Assert.Contains("if (temporalRequested)", platform);
        // The history slots and the sharpen target are still behind taaRequested.
        Assert.Contains("list[OptimumTaaHistoryIndexA] = CreateOptimumHistoryTargetGl(num, num2);", platform);
        Assert.Contains(
            "optimumTaaTargetsReady = taaRequested && MotionAttachmentIndex >= 0",
            platform);
        // A motion attachment that could not be allocated costs both consumers
        // their vectors, so both stand down.
        Assert.Contains("DisableOptimumUpscaler(\"Primary motion attachment (GL): \" + error.Message);", platform);
    }

    /// <summary>
    /// One guard for the write windows, and it answers for both consumers: the
    /// resolve needs its history slots, an upscaler needs none of ours, and both
    /// need the motion attachment inside the jitter window.
    /// </summary>
    [Fact]
    public void OneGuardAnswersForBothTemporalConsumers()
    {
        string platform = Platform();
        string guard = MethodBodyAfter(platform, "public bool OptimumMotionWritesReady");

        Assert.Contains("if (MotionAttachmentIndex < 0) return false;", guard);
        Assert.Contains("if (Vintagestory.API.Config.OptimumConfig.UpscalerReplacesTaa) return true;", guard);
        Assert.Contains(
            "return Vintagestory.API.Config.OptimumConfig.EffectiveTaa && TaaTargetsReady;", guard);

        // Every window reads it, and none of them carries the old pair any more.
        Assert.Equal(3, Count(platform, "if (!OptimumMotionWritesReady) return false;"));
        Assert.DoesNotContain("if (MotionAttachmentIndex < 0 || !TaaTargetsReady) return false;", platform);
    }

    /// <summary>
    /// The shader-side inputs follow the pipeline rather than our resolve: the
    /// motion variant, the previous-bone UBO, the liquid velocity pass and the
    /// FXAA define all ask whether anything temporal is running, not whether our
    /// own resolve is.
    /// </summary>
    [Fact]
    public void EveryShaderSideInputAsksForThePipelineNotForOurResolve()
    {
        string registry = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderRegistry.cs");
        Assert.Contains("bool taaMotion = OptimumConfig.EffectiveTemporalPipeline;", registry);
        Assert.Contains("!OptimumConfig.EffectiveTemporalPipeline ? 1 : 0", registry);

        string entityanimated = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ShaderProgramEntityanimated.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ShaderProgramEntityanimated.cs");
        Assert.Contains(
            "if (!Oit && Vintagestory.API.Config.OptimumConfig.EffectiveTemporalPipeline)", entityanimated);

        string chunk = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ChunkRenderer.cs");
        Assert.Contains(
            "if (!Vintagestory.API.Config.OptimumConfig.EffectiveTemporalPipeline)", chunk);

        // The API's own definition of that question, in both copies of the fork.
        foreach (string config in new[]
        {
            Read("VintagestoryApi/Config/OptimumConfig.cs"),
            Read("sources/VintagestoryApi/Config/OptimumConfig.cs"),
        })
        {
            Assert.Contains(
                "public static bool EffectiveTemporalPipeline => EffectiveTaa || UpscalerReplacesTaa;", config);
            Assert.Contains("public static float EffectiveTemporalRenderScale =>", config);
        }
    }

    /// <summary>Every new lib member reaches the shipped DLL.</summary>
    [Fact]
    public void ThePatcherListsEveryNewMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"OptimumTemporalRequested\",", patcher);
        Assert.Contains("\"OptimumMotionWritesReady\",", patcher);
        Assert.Contains("\"DisableOptimumUpscaler\",", patcher);
        // The two methods whose bodies changed are transplant targets already.
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"MainRenderLoop\", 1", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"SetupDefaultFrameBuffers\", 0", patcher);
    }

    // --------------------------------------------------------------- helpers

    private static string Platform() => ReadPatchedOrSource(PlatformPatch, PlatformSource);

    private static int Count(string source, string needle)
    {
        int count = 0;
        int at = 0;
        while ((at = source.IndexOf(needle, at, StringComparison.Ordinal)) >= 0)
        {
            count++;
            at += needle.Length;
        }
        return count;
    }

    private static string MethodBodyAfter(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "no such member: " + signature);
        int open = source.IndexOf('{', start);
        Assert.True(open > start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0) return signature + source.Substring(open, i - open + 1);
            }
        }
        throw new InvalidOperationException("unterminated member: " + signature);
    }

    private static string ReadPatchedOrSource(string patchPath, string sourcePath)
    {
        string? resolved = TryFind(patchPath);
        return resolved != null ? PatchReader.ReadPatchedContent(resolved) : Read(sourcePath);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    private static string? TryFind(string relativePath)
    {
        try
        {
            return PatchReader.FindRepositoryFile(relativePath);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
