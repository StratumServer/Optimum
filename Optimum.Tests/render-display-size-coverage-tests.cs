using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// DLSS plan, Phase 1: render resolution and display resolution are explicit, and each
/// has exactly one source of truth.
///
/// Before this phase the product <c>ClientSize * ClientSettings.SSAA</c> was recomputed in
/// the framebuffer allocator, in the world viewport (GuiScreenRunningGame.RenderToPrimary)
/// and in about fifteen post-processing sites. An upscaler makes the render size something
/// the engine chooses rather than something that formula produces, so every consumer has to
/// read it: from the framebuffer it is drawing into, or from the platform's RenderWidth /
/// RenderHeight when no target is bound. These tests fail if a recomputation comes back.
/// </summary>
public class RenderDisplaySizeCoverageTests
{
    private const string PlatformSource =
        "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs";

    /// <summary>
    /// The shape the post chain used - <c>ssaaLevel * (float)window.ClientSize.*</c> - is
    /// gone from the platform entirely. Not one viewport, not one uniform.
    /// </summary>
    [Fact]
    public void NoPostProcessingSiteRecomputesSsaaTimesClientSize()
    {
        string platform = WithoutComments(Platform());

        Assert.Equal(0, Count(platform, "ssaaLevel * (float)((NativeWindow)window).ClientSize"));
        Assert.Equal(0, Count(platform, "(float)x * ssaaLevel"));
        Assert.Equal(0, Count(platform, "(float)y * ssaaLevel"));
        Assert.Equal(0, Count(platform, "ssaaLevel * (float)x"));
        Assert.Equal(0, Count(platform, "ssaaLevel * (float)y"));
    }

    /// <summary>
    /// The product survives in exactly two places per axis: the allocator, which is where the
    /// render size is decided, and the RenderWidth / RenderHeight fallback for the window
    /// before the first allocation. Both are before the post chain in the file, and the
    /// fallback comes first because the properties are declared above SetupDefaultFrameBuffers.
    /// </summary>
    [Fact]
    public void TheProductSurvivesOnlyInTheAllocatorAndTheRenderSizeFallback()
    {
        string platform = Platform();

        const string widthProduct = "(int)((float)((NativeWindow)window).ClientSize.X * ssaaLevel)";
        const string heightProduct = "(int)((float)((NativeWindow)window).ClientSize.Y * ssaaLevel)";
        Assert.Equal(2, Count(platform, widthProduct));
        Assert.Equal(2, Count(platform, heightProduct));

        int renderWidthGetter = platform.IndexOf("public override int RenderWidth", StringComparison.Ordinal);
        int allocator = platform.IndexOf(
            "public virtual List<FrameBufferRef> SetupDefaultFrameBuffers()", StringComparison.Ordinal);
        int fallback = platform.IndexOf(widthProduct, StringComparison.Ordinal);
        int allocation = platform.IndexOf(widthProduct, allocator, StringComparison.Ordinal);

        Assert.True(renderWidthGetter >= 0, "ClientPlatformWindows does not override RenderWidth");
        Assert.True(allocator > renderWidthGetter);
        Assert.InRange(fallback, renderWidthGetter, allocator);
        Assert.True(allocation > allocator);
    }

    /// <summary>
    /// The render size is Primary's allocated size - the framebuffer is the carrier - and the
    /// display size is the window's client size. Neither is a stored copy that a resize could
    /// leave stale.
    /// </summary>
    [Fact]
    public void TheRenderSizeIsReadFromPrimaryAndTheDisplaySizeFromTheWindow()
    {
        string platform = Platform();

        int renderWidth = platform.IndexOf("public override int RenderWidth", StringComparison.Ordinal);
        int renderHeight = platform.IndexOf("public override int RenderHeight", StringComparison.Ordinal);
        int displayWidth = platform.IndexOf("public override int DisplayWidth", StringComparison.Ordinal);
        int displayHeight = platform.IndexOf("public override int DisplayHeight", StringComparison.Ordinal);
        Assert.True(renderWidth >= 0 && renderHeight > renderWidth);
        Assert.True(displayWidth > renderHeight && displayHeight > displayWidth);

        string renderBodies = platform.Substring(renderWidth, displayWidth - renderWidth);
        Assert.Contains("return primary.Width;", renderBodies);
        Assert.Contains("return primary.Height;", renderBodies);
        Assert.Equal(2, Count(renderBodies, "FrameBufferRef primary = buffers[0];"));

        int displayEnd = platform.IndexOf("public override List<FrameBufferRef> FrameBuffers", StringComparison.Ordinal);
        string displayBodies = platform.Substring(
            displayWidth, (displayEnd > displayWidth ? displayEnd : platform.Length) - displayWidth);
        Assert.Contains("return ((NativeWindow)window).ClientSize.X;", displayBodies);
        Assert.Contains("return ((NativeWindow)window).ClientSize.Y;", displayBodies);
    }

    /// <summary>
    /// Every viewport in LoadFrameBuffer / UnloadFrameBuffer reads a size instead of deriving
    /// one: the blur, god-ray, SSAO and Primary cases from the target they select, the default
    /// framebuffer from the display size, and the unload restore from the render size.
    /// </summary>
    [Fact]
    public void EveryViewportSiteReadsAFramebufferOrThePlatformSize()
    {
        string platform = Platform();

        Assert.Contains("GlViewport(0, 0, DisplayWidth, DisplayHeight);", platform);
        Assert.Contains("FrameBufferRef medResBlur = frameBuffers[(int)framebuffer];", platform);
        Assert.Contains("GlViewport(0, 0, medResBlur.Width, medResBlur.Height);", platform);
        Assert.Contains("FrameBufferRef lowResBlur = frameBuffers[(int)framebuffer];", platform);
        Assert.Contains("GlViewport(0, 0, lowResBlur.Width, lowResBlur.Height);", platform);
        Assert.Contains("FrameBufferRef godRaysTarget = frameBuffers[(int)framebuffer];", platform);
        Assert.Contains("GlViewport(0, 0, godRaysTarget.Width, godRaysTarget.Height);", platform);
        Assert.Contains("GlViewport(0, 0, primaryTarget.Width, primaryTarget.Height);", platform);
        // The SSAO cases already read their target before this phase; they must stay that way.
        Assert.Contains("GlViewport(0, 0, frameBufferRef2.Width, frameBufferRef2.Height);", platform);
        // UnloadFrameBuffer restores the world viewport with no target bound.
        Assert.Contains("GlViewport(0, 0, RenderWidth, RenderHeight);", platform);
    }

    /// <summary>
    /// The post chain reads the render size once and hands the same two integers to every
    /// viewport restore and every inverse-frame-size uniform in the method.
    /// </summary>
    [Fact]
    public void ThePostChainReadsTheRenderSizeOnceAndUsesItEverywhere()
    {
        string platform = Platform();
        int start = platform.IndexOf(
            "public override void RenderPostprocessingEffects(", StringComparison.Ordinal);
        int end = platform.IndexOf("public override void ClearSsaoTarget()", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string post = WithoutComments(platform.Substring(start, end - start));

        Assert.Equal(1, Count(post, "int renderWidth = RenderWidth;"));
        Assert.Equal(1, Count(post, "int renderHeight = RenderHeight;"));
        // DLSS plan, Phase 3: the passes after the upscale work on the target that holds
        // this frame's image, which is the display-resolution one when an upscaler ran and
        // Primary otherwise - one pair of integers again, read once.
        Assert.Equal(1, Count(post, "int postWidth = renderWidth;"));
        Assert.Equal(1, Count(post, "int postHeight = renderHeight;"));
        Assert.Equal(3, Count(post, "GlViewport(0, 0, postWidth, postHeight);"));
        Assert.Contains("blur.Uniform(\"frameSize\", (float)postWidth, (float)postHeight);", post);
        Assert.Contains(
            "godrays.Uniform(\"invFrameSizeIn\", 1f / (float)postWidth, 1f / (float)postHeight);", post);
        Assert.Contains("ssao.Uniform(\"screenSize\", (float)renderWidth * num, (float)renderHeight * num);", post);
        // The quarter-resolution blur viewport comes from the target it is about to bind.
        Assert.Contains("FrameBufferRef lowResBlurTarget = frameBuffers[9];", post);
        Assert.Contains("GlViewport(0, 0, lowResBlurTarget.Width, lowResBlurTarget.Height);", post);
        Assert.DoesNotContain("ClientSize", post);
    }

    /// <summary>The final composition's inverse frame size is the render size, read from Primary.</summary>
    [Fact]
    public void TheFinalCompositionReadsTheRenderSize()
    {
        string platform = Platform();
        int start = platform.IndexOf("public override void RenderFinalComposition()", StringComparison.Ordinal);
        int end = platform.IndexOf(
            "public override void BeginFinalCompositionDrawBuffers()", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string final = WithoutComments(platform.Substring(start, end - start));

        // DLSS plan, Phase 3: the size of the target the pass draws into, which is
        // Primary's - the render size - with no upscaler and the display size with one.
        Assert.Contains(
            "final.Uniform(\"invFrameSizeIn\", 1f / (float)compositeTarget.Width, 1f / (float)compositeTarget.Height);",
            final);
        Assert.DoesNotContain("ssaaLevel", final);
        Assert.DoesNotContain("ClientSize", final);
    }

    /// <summary>
    /// The world viewport reads the platform's render size. Recomputing WindowSize * SSAA
    /// there was the second copy of the allocator's formula.
    /// </summary>
    [Fact]
    public void TheWorldViewportReadsThePlatformRenderSize()
    {
        string screen = Read("build/VintagestoryLib/Vintagestory.Client/GuiScreenRunningGame.cs");

        int start = screen.IndexOf("public override void RenderToPrimary(", StringComparison.Ordinal);
        int end = screen.IndexOf("Reconnect();", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        string body = WithoutComments(screen.Substring(start, end - start));

        Assert.Contains("platform.GlViewport(0, 0, platform.RenderWidth, platform.RenderHeight);", body);
        Assert.DoesNotContain("ClientSettings.SSAA", body);
        Assert.DoesNotContain("WindowSize.Width", body);
    }

    /// <summary>
    /// The low-res blur slot's record has to describe the texture it owns, because the viewport
    /// is read from it now. Vanilla left slot 8 at zero.
    /// </summary>
    [Fact]
    public void TheLowResBlurSlotRecordsItsOwnSize()
    {
        string platform = Platform();
        int slot = platform.IndexOf("frameBufferRef = (list[8] = new FrameBufferRef", StringComparison.Ordinal);
        Assert.True(slot >= 0);
        string declaration = platform.Substring(slot, 200);

        // DLSS plan, Phase 3: the blur chain is downstream of the upscale, so its quarter
        // is a quarter of the display size - the same number as before whenever no
        // upscaler is running.
        Assert.Contains("Width = displayWidth / 4", declaration);
        Assert.Contains("Height = displayHeight / 4", declaration);
    }

    /// <summary>
    /// Both resolutions are declared on the abstract platform, so a caller needs no cast and the
    /// device platform inherits working bodies. Neutral bodies answer the window size.
    /// </summary>
    [Fact]
    public void TheAbstractPlatformDeclaresBothResolutions()
    {
        string abstractPlatform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");

        Assert.Contains("public virtual int RenderWidth", abstractPlatform);
        Assert.Contains("public virtual int RenderHeight", abstractPlatform);
        Assert.Contains("public virtual int DisplayWidth", abstractPlatform);
        Assert.Contains("public virtual int DisplayHeight", abstractPlatform);

        int render = abstractPlatform.IndexOf("public virtual int RenderWidth", StringComparison.Ordinal);
        int end = abstractPlatform.IndexOf("public virtual int MotionAttachmentIndex", render, StringComparison.Ordinal);
        Assert.True(end > render);
        Assert.Equal(2, Count(abstractPlatform.Substring(render, end - render), "return WindowSize.Width;"));
        Assert.Equal(2, Count(abstractPlatform.Substring(render, end - render), "return WindowSize.Height;"));
    }

    /// <summary>Every new or changed member is a Cecil target, or none of this ships.</summary>
    [Fact]
    public void ThePatcherListsEveryNewMember()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Equal(2, Count(patcher, "\"RenderWidth\","));
        Assert.Equal(2, Count(patcher, "\"RenderHeight\","));
        Assert.Equal(2, Count(patcher, "\"DisplayWidth\","));
        Assert.Equal(2, Count(patcher, "\"DisplayHeight\","));
        Assert.Contains("new(\"Vintagestory.Client.GuiScreenRunningGame\", \"RenderToPrimary\", 1)", patcher);

        string owned = Read("patches/cecil-owned.list");
        Assert.Contains("patches/VintagestoryLib/Vintagestory.Client/GuiScreenRunningGame.cs.patch", owned);
    }

    /// <summary>
    /// The identity the viewport replacement rests on: for a non-negative float, truncating
    /// after the division by 2 or 4 gives the same integer as truncating first and then
    /// dividing the integer. That is why reading the blur target's num/2 and num/4 sizes is
    /// byte-identical to the old (int)(ssaa * ClientSize / 2f).
    /// </summary>
    [Fact]
    public void FramebufferSizesEqualTheOldTruncatedExpressions()
    {
        int[] clientSizes = { 600, 601, 1279, 1280, 1281, 1920, 2560, 3841 };
        float[] scales = { 0.25f, 0.5f, 0.75f, 1f, 2f };

        foreach (int client in clientSizes)
        {
            foreach (float ssaa in scales)
            {
                int render = (int)((float)client * ssaa);
                Assert.Equal((int)(ssaa * (float)client / 2f), render / 2);
                Assert.Equal((int)(ssaa * (float)client / 4f), render / 4);
                Assert.Equal((int)(ssaa * (float)client), render);
            }
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// The source of truth for lib code is build/**, which is what extract-patches.sh turns into
    /// the shipped patch; the patch file itself holds only hunks, so occurrence counts have to be
    /// taken from the whole file.
    /// </summary>
    private static string Platform() => Read(PlatformSource);

    /// <summary>Drops // line comments, so a comment that names the banned formula is not a hit.</summary>
    private static string WithoutComments(string source)
    {
        var stripped = new System.Text.StringBuilder(source.Length);
        foreach (string line in source.Split('\n'))
        {
            string trimmed = line.TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("///", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal))
            {
                continue;
            }
            stripped.Append(line).Append('\n');
        }
        return stripped.ToString();
    }

    private static int Count(string haystack, string needle)
    {
        int count = 0;
        int at = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (at >= 0)
        {
            count++;
            at = haystack.IndexOf(needle, at + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }
}
