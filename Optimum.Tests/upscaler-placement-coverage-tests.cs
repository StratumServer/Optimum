using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// DLSS plan, Phase 3: where the upscaler sits in the frame.
///
/// NVIDIA's DLSS Programming Guide section 3.1 fixes the placement: the evaluate happens
/// during post processing, before tone mapping, as close to the start of post processing
/// as possible, and every effect after it has to cope with the increased resolution. In
/// Optimum that means the world, its G-buffer, the motion attachment, SSAO and LiquidDepth
/// stay at the render size; the evaluate runs where the TAA resolve runs; and FindBright,
/// the blur chain, god rays, luma, the final composition, the late overlays, the blit and
/// the screenshots all work on the display-resolution target.
///
/// Every clause of that is a place a later change can quietly get wrong - a size read from
/// the wrong member, a pass left reading Primary - so each is pinned here against the
/// shipped source.
/// </summary>
public class UpscalerPlacementCoverageTests
{
    private const string PlatformSource =
        "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs";

    /// <summary>
    /// The allocator's old product is the display size, and the render size is the
    /// platform's answer for it. With no upscaler the platform answers false with both
    /// outputs left at the display size, which is how "upscaler off is byte-identical"
    /// is guaranteed rather than asserted.
    /// </summary>
    [Fact]
    public void TheAllocatorSplitsDisplaySizeFromRenderSize()
    {
        string platform = Platform();

        Assert.Contains(
            "int displayWidth = (int)((float)((NativeWindow)window).ClientSize.X * ssaaLevel);", platform);
        Assert.Contains(
            "int displayHeight = (int)((float)((NativeWindow)window).ClientSize.Y * ssaaLevel);", platform);
        Assert.Contains("if (displayWidth == 0 || displayHeight == 0)", platform);
        Assert.Contains("int num = displayWidth;", platform);
        Assert.Contains("int num2 = displayHeight;", platform);
        Assert.Contains(
            "bool upscaling = OptimumTryPlanUpscaleRenderSize(displayWidth, displayHeight, " +
            "out upscaleRenderWidth, out upscaleRenderHeight);", platform);

        // The neutral answer: no upscaler, and the render size is the display size.
        string plan = MethodBodyAfter(platform,
            "public virtual bool OptimumTryPlanUpscaleRenderSize(int displayWidth, int displayHeight, " +
            "out int renderWidth, out int renderHeight)");
        Assert.Contains("renderWidth = displayWidth;", plan);
        Assert.Contains("renderHeight = displayHeight;", plan);
        Assert.Contains("return false;", plan);
    }

    /// <summary>
    /// What is allocated at which size. The world's inputs keep the render size; every
    /// target downstream of the evaluate takes the display size, including the blur
    /// chain's own halves and quarters of it.
    /// </summary>
    [Fact]
    public void TheWorldKeepsTheRenderSizeAndThePostChainTakesTheDisplaySize()
    {
        string platform = Platform();

        // Primary and Transparent keep the render size: their declarations are the two
        // that still read num/num2, and the world's depth texture is allocated at it.
        Assert.Contains("GL.TexImage2D((TextureTarget)3553, 0, (PixelInternalFormat)33191, num, num2, 0,", platform);
        Assert.Contains("frameBufferRef = (list[1] = new FrameBufferRef", platform);
        // SSAO is half the render size; LiquidDepth a quarter of it.
        Assert.Contains("Width = (int)((float)num * num3),", platform);
        Assert.Contains("frameBufferRef = (list[5] = new FrameBufferRef", platform);

        // The post chain: the display size, and its halves and quarters.
        foreach (string downstream in new[]
        {
            "list[2] = new FrameBufferRef", "list[3] = new FrameBufferRef",
            "list[8] = new FrameBufferRef", "list[9] = new FrameBufferRef",
            "list[4] = new FrameBufferRef", "list[7] = new FrameBufferRef",
            "list[10] = new FrameBufferRef",
        })
        {
            int at = platform.IndexOf(downstream, StringComparison.Ordinal);
            Assert.True(at > 0, "no such slot: " + downstream);
            string declaration = platform.Substring(at, 200);
            Assert.Contains("Width = displayWidth", declaration);
            Assert.Contains("Height = displayHeight", declaration);
        }
    }

    /// <summary>
    /// The upscaled scene target: display-resolution colour plus its own depth, allocated
    /// only when a plan exists, and named in the parity dump like every other slot.
    /// </summary>
    [Fact]
    public void TheUpscaledSceneTargetIsAllocatedAtTheDisplaySizeWithItsOwnDepth()
    {
        string platform = Platform();

        Assert.Contains("private const int OptimumUpscaledSceneIndex = 22;", platform);
        Assert.Contains("if (upscaling)", platform);
        Assert.Contains(
            "FrameBufferRef optimumUpscaled = (list[OptimumUpscaledSceneIndex] = new FrameBufferRef", platform);
        Assert.Contains(
            "setupAttachment(optimumUpscaled, displayWidth, displayHeight, 0, val, (PixelInternalFormat)32856);",
            platform);
        Assert.Contains("optimumUpscaled.DepthTextureId = GL.GenTexture();", platform);
        Assert.Contains("case OptimumUpscaledSceneIndex:", platform);
        Assert.Contains("return \"OptimumUpscaledScene\";", platform);

        // The device path allocates the same shape, with the storage usage NGX requires
        // and a depth attachment of its own.
        string vulkan = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.FrameBuffers.cs");
        Assert.Contains("private const int OptimumUpscaledSceneIndex = 22;", vulkan);
        Assert.Contains("device.CreateUpscaleTexture(", vulkan);
        Assert.Contains("storage: true", vulkan);
        Assert.Contains("EnumFramebufferAttachment.DepthAttachment, target.DepthTextureId, 0", vulkan);
    }

    /// <summary>
    /// The evaluate runs where the resolve runs - after the resolve and the sharpen, which
    /// both stood down, and before anything reads the scene - and from that line on the
    /// post chain works on the upscaled target and its size.
    /// </summary>
    [Fact]
    public void TheEvaluateRunsWhereTheResolveRunsAndTheChainFollowsIt()
    {
        string post = MethodBodyAfter(Platform(),
            "public override void RenderPostprocessingEffects(float[] projectMatrix)");

        int resolve = post.IndexOf("RenderOptimumTaaResolve();", StringComparison.Ordinal);
        int sharpen = post.IndexOf("RenderOptimumTaaSharpen(postSceneTexture)", StringComparison.Ordinal);
        int upscale = post.IndexOf("optimumUpscaledThisFrame = RenderOptimumUpscale();", StringComparison.Ordinal);
        int findbright = post.IndexOf("LoadFrameBuffer(EnumFrameBuffer.FindBright);", StringComparison.Ordinal);
        Assert.True(resolve > 0 && sharpen > resolve && upscale > sharpen && findbright > upscale,
            "the evaluate must sit between the temporal passes it replaces and the first reader " +
            "of the scene:\n" + post);

        Assert.Contains("FrameBufferRef postTarget = OptimumCompositeFrameBuffer;", post);
        Assert.Contains("postSceneTexture = postTarget.ColorTextureIds[0];", post);
        Assert.Contains("postWidth = postTarget.Width;", post);
        Assert.Contains("postHeight = postTarget.Height;", post);
        // FXAA reads this frame's image too, not the jittered render-resolution one.
        Assert.Contains("luma.Scene2D = optimumUpscaledThisFrame ? postSceneTexture", post);
    }

    /// <summary>
    /// The composition writes the target that carries the frame, hands the world's
    /// draw-buffer mask back to Primary (where the world passes write, and the only target
    /// that has those attachments), and leaves the display-resolution target bound so the
    /// AfterFinalComposition overlays draw into the composited image.
    /// </summary>
    [Fact]
    public void TheCompositionWritesTheCompositeTargetAndLeavesItBoundForTheOverlays()
    {
        string final = MethodBodyAfter(Platform(), "public override void RenderFinalComposition()");

        Assert.Contains("FrameBufferRef compositeTarget = OptimumCompositeFrameBuffer;", final);
        Assert.Contains("CurrentFrameBuffer = compositeTarget;", final);
        Assert.Contains(
            "final.Uniform(\"invFrameSizeIn\", 1f / (float)compositeTarget.Width, 1f / (float)compositeTarget.Height);",
            final);

        int restore = final.IndexOf("LoadFrameBuffer(EnumFrameBuffer.Primary);", StringComparison.Ordinal);
        int mask = final.IndexOf("RestoreWorldDrawBuffers(RenderSSAO);", restore, StringComparison.Ordinal);
        int rebind = final.IndexOf("CurrentFrameBuffer = compositeTarget;", mask, StringComparison.Ordinal);
        Assert.True(restore > 0 && mask > restore && rebind > mask,
            "the world draw-buffer mask belongs to Primary, and the overlays to the composite " +
            "target:\n" + final);
    }

    /// <summary>
    /// One definition of "the target that holds the composited image", and every downstream
    /// reader asks it rather than naming Primary: the blit and both screenshot paths.
    /// </summary>
    [Fact]
    public void EveryDownstreamReaderAsksForTheCompositeTarget()
    {
        string platform = Platform();

        string composite = MethodBodyAfter(platform, "public FrameBufferRef OptimumCompositeFrameBuffer");
        Assert.Contains("if (optimumUpscaledThisFrame", composite);
        Assert.Contains("frameBuffers[OptimumUpscaledSceneIndex]", composite);
        Assert.Contains("return frameBuffers[0];", composite);

        string blit = MethodBodyAfter(platform, "public override void BlitPrimaryToDefault()");
        Assert.Contains("FrameBufferRef blitSource = OptimumCompositeFrameBuffer;", blit);
        Assert.Contains("int scene2D = blitSource.ColorTextureIds[0];", blit);

        // The screenshot paths bind Primary and capture what is bound; the redirect moves
        // that capture to the composited image and hands the binding back.
        string capture = MethodBodyAfter(platform,
            "private FrameBufferRef OptimumBindCompositeForCapture(FrameBufferRef current)");
        Assert.Contains("if (!optimumUpscaledThisFrame", capture);
        Assert.Contains("if (!ReferenceEquals(current, frameBuffers[0]))", capture);
        Assert.Contains("CurrentFrameBuffer = composite;", capture);

        foreach (string screenshot in new[]
        {
            "public override string SaveScreenshot(", "public override BitmapRef GrabScreenshot(bool withAlpha",
        })
        {
            string body = MethodBodyAfter(platform, screenshot);
            Assert.Contains("OptimumBindCompositeForCapture(restoreFrameBuffer)", body);
            Assert.Contains("CurrentFrameBuffer = restoreFrameBuffer;", body);
        }
    }

    /// <summary>
    /// A rebuild cannot leave a reader pointing at a disposed target, and it resets the
    /// temporal history through the path that already exists.
    /// </summary>
    [Fact]
    public void ARebuildDropsTheUpscaledFrameAndResetsTheHistory()
    {
        string rebuild = MethodBodyAfter(Platform(), "public override void RebuildFrameBuffers()");

        Assert.Contains("optimumUpscaledThisFrame = false;", rebuild);
        Assert.Contains("_taaHistoryValid = false;", rebuild);
        Assert.Contains("OptimumTemporal.RequestReset(EnumTemporalResetReason.Resize);", rebuild);
    }

    /// <summary>
    /// The device half: the plan comes from the vendor query, the evaluate hands over the
    /// contract's own values (jitter negated, motion scale 1, reset from the reset reason)
    /// and the overlays' depth is one nearest-neighbour upscale per frame.
    /// </summary>
    [Fact]
    public void TheDeviceHalfEvaluatesOnTheContractsTerms()
    {
        string upscale = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Upscale.cs");

        Assert.Contains("public override bool OptimumTryPlanUpscaleRenderSize(", upscale);
        // The plan still comes from the vendor query, now through the one rule that
        // asks the setting in force before the host (PR #3 item A,
        // DlssUpscaler.TryPlanForFrame); the query itself is asserted there.
        Assert.Contains(
            "DlssUpscaler.TryPlanForFrame(\n            upscaler, displayWidth, displayHeight, out renderWidth, out renderHeight, out UpscalePlan plan)",
            upscale);
        Assert.Contains(
            "host.TryPlan(displayWidth, displayHeight, OptimumConfig.UpscalerQuality, out plan)",
            Read("Optimum.Render.Vulkan/Upscale/DlssUpscaler.cs"));
        Assert.Contains("public override bool RenderOptimumUpscale()", upscale);
        Assert.Contains("NgxDlssEvaluation.FromTemporalContext(frame)", upscale);
        string adapter = Read("Optimum.Render.Vulkan/Upscale/Ngx/NgxDlssFeature.cs");
        Assert.Contains("JitterOffsetX = frame.JitterPx.X,", adapter);
        Assert.Contains("JitterOffsetY = frame.JitterPx.Y,", adapter);
        Assert.Contains("MotionVectorScaleX = 1f,", adapter);
        Assert.Contains("Reset = frame.Reset,", adapter);
        Assert.Contains("primary.ColorTextureIds[MotionAttachmentIndex],", upscale);
        Assert.Contains("device.UpscaleDepthNearest(primary.DepthTextureId, target.DepthTextureId)", upscale);
        // A failed evaluate stands the upscaler down instead of repeating itself.
        Assert.Contains("DisableOptimumUpscaler(\"NVSDK_NGX_VULKAN_EvaluateFeature: \"", upscale);

        string device = Read("Optimum.Render.Vulkan/VulkanDevice.Dlss.cs");
        Assert.Contains("internal bool UpscaleDepthNearest(int sourceTexture, int destinationTexture)", device);
        Assert.Contains("Filter.Nearest", device);
        Assert.Contains("ResourceUsage.TransferSrc", device);
        Assert.Contains("ResourceUsage.TransferDst", device);
    }

    /// <summary>Every new lib member reaches the shipped DLL, and the two new bodies with it.</summary>
    [Fact]
    public void ThePatcherListsEveryNewMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        foreach (string member in new[]
        {
            "OptimumUpscaledSceneIndex", "optimumUpscaledThisFrame", "OptimumUpscalerActive",
            "OptimumTryPlanUpscaleRenderSize", "RenderOptimumUpscale", "OptimumCompositeFrameBuffer",
            "OptimumUpscaledThisFrame", "OptimumBindCompositeForCapture",
        })
        {
            Assert.Contains("\"" + member + "\",", patcher);
        }
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"SaveScreenshot\", 5", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"GrabScreenshot\", 2", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"RenderFinalComposition\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"BlitPrimaryToDefault\", 0", patcher);

        // And the three virtuals the Vulkan platform overrides are in its own self-check,
        // so a lib without them falls back to OpenGL at install instead of mid-frame.
        string selfCheck = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.cs");
        Assert.Contains("new(false, \"get_OptimumUpscalerActive\"", selfCheck);
        Assert.Contains("new(false, \"OptimumTryPlanUpscaleRenderSize\"", selfCheck);
        Assert.Contains("new(false, \"RenderOptimumUpscale\"", selfCheck);
    }

    // --------------------------------------------------------------- helpers

    /// <summary>
    /// The donor source, not the patch: these assertions read whole method bodies, and a
    /// patch file holds only its hunks. <c>check-patches.sh</c> is what keeps the two in
    /// step, so reading the donor is reading what ships.
    /// </summary>
    private static string Platform() => Read(PlatformSource);

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

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

}
