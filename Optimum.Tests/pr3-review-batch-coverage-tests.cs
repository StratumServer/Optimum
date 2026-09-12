using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The PR #3 review batch: the findings that survived verification against the
/// branch, each pinned where the defect was observable.
///
/// These are source-coverage tests in the sense the repo uses the phrase - they
/// hold a shape that a later edit would otherwise quietly undo. The behavioural
/// proofs live next to the behaviour: <c>TimelineLifetimeTests</c> and
/// <c>FrameRingTests</c> for the teardown contract, <c>OptimumConfigLodBiasTests</c>
/// for the DLAA bias, and the GPU tests for the frame path.
/// </summary>
public class Pr3ReviewBatchCoverageTests
{
    private const string Upscaler = "Optimum.Render.Vulkan/Upscale/DlssUpscaler.cs";
    private const string Session = "Optimum.Render.Vulkan/Upscale/Ngx/NgxSession.cs";
    private const string Upscale = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Upscale.cs";
    private const string FrameBuffers = "Optimum.Render.Vulkan/Platform/VulkanClientPlatform.FrameBuffers.cs";
    private const string Ring = "Optimum.Render.Vulkan/Core/FrameRing.cs";
    private const string Project = "Optimum.Render.Vulkan/Optimum.Render.Vulkan.csproj";

    /// <summary>
    /// NGX is shut down because it was brought up, never because the upscaler still
    /// works.
    ///
    /// <c>Unavailable</c> is set by every later <c>Fail</c> - the realistic one being
    /// a driver that refuses <c>CreateFeature1</c> - while NGX is still initialised
    /// on a live VkDevice. Gating Shutdown1 on it meant the client ran on, and
    /// <c>ShutdownGraphics</c> then destroyed the device with NGX still up: the
    /// use-after-free this class documents, and no Shutdown1 to spend the one process
    /// lifetime on either.
    /// </summary>
    [Fact]
    public void TheNgxShutdownIsGatedOnInitialisationNotOnUsability()
    {
        string upscaler = Read(Upscaler);
        string shutdown = Between(upscaler, "public void Shutdown()", "public void Dispose()");

        // The gate now lives in the one owner, which knows whether NGX is up; the
        // host only says whether the lifetime is its own to end.
        Assert.DoesNotContain("Unavailable == null", shutdown);
        Assert.Contains("if (_ownsSession)", shutdown);
        Assert.Contains("NgxLifetime.ShutDown(", shutdown);

        string owner = Read("Optimum.Render.Vulkan/Upscale/Ngx/NgxLifetime.cs");
        string ownerShutdown = Between(owner, "internal NgxLifetimeOutcome ShutDown(", "internal static class NgxLifetime");
        Assert.Contains("if (!_initialized)", ownerShutdown);
        Assert.DoesNotContain("Unavailable", ownerShutdown);

        // AdoptSession owns no lifetime, so a test host that adopts retires and
        // drains but must never shut the owner's NGX down.
        string adopt = Between(upscaler, "internal bool AdoptSession(", "// ---------------------------------------------------------------- plan");
        Assert.DoesNotContain("NgxLifetime.ShutDown", adopt);
        Assert.Contains("_ownsSession = false;", adopt);
    }

    /// <summary>
    /// Preparing NGX's log and cache directory is best effort in both places that do
    /// it. The host's own preparation already swallowed the two exceptions; the
    /// session then retried the same call unguarded, and nothing between it and
    /// <c>InitializeGraphics</c> catches - so an unwritable data path fell the whole
    /// client back to OpenGL over a directory NGX only writes logs into.
    /// </summary>
    [Fact]
    public void TheNgxDataDirectoryNeverAbortsTheVulkanBringUp()
    {
        string session = Read(Session);
        string initialize = Between(session, "public NgxResult Initialize(", "// There is deliberately no Shutdown here.");

        Assert.Contains("Directory.CreateDirectory(ApplicationDataPath)", initialize);
        Assert.Contains("catch (IOException)", initialize);
        Assert.Contains("catch (UnauthorizedAccessException)", initialize);
        Assert.Contains("!string.IsNullOrEmpty(ApplicationDataPath)", initialize);
    }

    /// <summary>
    /// A refused depth upscale leaves the display-resolution depth undefined on the
    /// frame the image was created and a frame stale afterwards, and the late 3D
    /// overlays test against it either way. The frame clears it to the far plane
    /// instead, every affected frame - not once, behind the one-shot log flag.
    /// </summary>
    [Fact]
    public void ARefusedDepthUpscaleClearsTheDisplayDepthEveryFrame()
    {
        string upscale = Read(Upscale);
        string refusal = Between(upscale, "// The overlays' depth, once per frame", "return true;");

        Assert.Contains("device.ClearDepthImageToFar(target.DepthTextureId);", refusal);
        // The clear is outside the once-per-session log guard: the guard only wraps
        // the log line, so the branch that clears cannot be the branch that logs.
        int clear = refusal.IndexOf("ClearDepthImageToFar", StringComparison.Ordinal);
        int guard = refusal.IndexOf("if (!upscaleDepthRefused)", StringComparison.Ordinal);
        Assert.True(clear >= 0 && guard > clear,
            "the clear must run before, and outside, the one-shot log guard");

        string device = Read("Optimum.Render.Vulkan/VulkanDevice.Dlss.cs");
        Assert.Contains("internal bool ClearDepthImageToFar(int destinationTexture)", device);
        Assert.Contains("CmdClearDepthStencilImage", device);
        Assert.Contains("new ClearDepthStencilValue(1f, 0)", device);
    }

    /// <summary>
    /// A failed upscaled-scene target leaves the whole set the wrong shape - Primary
    /// and everything sharing its size are at the vendor's reduced render size while
    /// the post chain is display-sized - so the build is rolled back and repeated
    /// with the upscaler stood down, and the target's own partial resources are
    /// released rather than orphaned outside the list.
    /// </summary>
    [Fact]
    public void AFailedUpscaledSceneTargetRollsTheFrameBufferBuildBack()
    {
        string buffers = Read(FrameBuffers);
        string rollback = Between(buffers,
            "list[OptimumUpscaledSceneIndex] = CreateOptimumUpscaledSceneTarget(", "// Optimum: TAA history");

        Assert.Contains("DisableOptimumUpscaler(\"the upscaled scene target (device): \"", rollback);
        Assert.Contains("DisposeFrameBuffers(list);", rollback);
        Assert.Contains("return SetupDefaultFrameBuffers();", rollback);

        // The retry terminates because the stand-down clears the setting the sizing
        // rule reads first; the rule is pinned by UpscalerRenderSizeRuleCoverageTests.
        string upscaler = Read(Upscaler);
        Assert.Contains("if (!OptimumConfig.UpscalerReplacesTaa) return false;", upscaler);

        // And the target creation is all-or-nothing: nothing it made reaches the
        // caller's list until the last line, so a partial build is the one thing
        // DisposeFrameBuffers cannot reach afterwards.
        string create = Between(buffers,
            "private FrameBufferRef CreateOptimumUpscaledSceneTarget(", "/// <summary>A depth-only target");
        Assert.Contains("catch", create);
        Assert.Contains("device.DeleteTexture(target.ColorTextureIds[0]);", create);
        Assert.Contains("device.DeleteTexture(target.DepthTextureId);", create);
        Assert.Contains("device.DeleteFramebuffer(target.FboId);", create);
        Assert.Contains("throw;", create);
    }

    /// <summary>
    /// The teardown contract: a frame reserved by BeginFrame and never submitted is
    /// closed before the drain waits, and the drain collects through its value.
    /// Otherwise every resource retired inside that frame stays queued for ever -
    /// including the DLSS feature the drain exists to release before Shutdown1.
    /// </summary>
    [Fact]
    public void DrainRetirementsClosesAnAbandonedFrameAndCollectsThroughItsValue()
    {
        string ring = Read(Ring);
        string drain = Between(ring, "public int DrainRetirements()", "public ulong AbortFrame()");

        Assert.Contains("AbortFrame()", drain);
        Assert.Contains("CollectThrough(", drain);
        Assert.Contains("Math.Max(_timeline.FrameCompleted, abandoned)", drain);

        // The abort is a reset of the slot's pool, not a submit: an abandoned
        // recording may sit inside a rendering scope, which vkEndCommandBuffer would
        // reject, while vkResetCommandPool only refuses buffers in the pending state.
        string abandon = Between(ring, "public ulong AbandonFrame()", "/// <summary>\n    /// After <see cref=\"EndFrameAndSubmit\" />");
        Assert.Contains("ResetCommandPool", abandon);
        Assert.Contains("abandoned <= _timeline.FrameSignalled", abandon);

        // The device stops describing itself as in-frame once the drain abandoned it.
        string device = Read("Optimum.Render.Vulkan/VulkanDevice.Dlss.cs");
        string deviceDrain = Between(device, "internal int DrainDeferredDeletions()", "/// <summary>\n    /// Point-upscales");
        Assert.Contains("_frameActive = false;", deviceDrain);
    }

    /// <summary>
    /// The Windows shim build creates its output directory and keeps the tail-call
    /// guard. Without the directory the link fails on a clean checkout and the
    /// fallback line blames a missing compiler; without the flag the shim may tail
    /// call into NGX, which then resolves the managed caller's module from the return
    /// address instead of the shim's and refuses - the whole reason the shim exists.
    /// </summary>
    [Fact]
    public void TheWindowsShimBuildCreatesItsOutputDirectoryAndKeepsTheTailCallGuard()
    {
        string project = Read(Project);
        string windows = Between(project, "<Exec Condition=\"'$(OS)' == 'Windows_NT'\"", "ContinueOnError");

        Assert.Contains("if not exist", windows);
        Assert.Contains("mkdir", windows);
        Assert.Contains("-fno-optimize-sibling-calls", windows);
        // Joined with a bare &amp;, never &amp;&amp;: cmd binds `if not exist X mkdir X &amp;&amp; cc`
        // as one conditional, so an existing directory would skip the compile too.
        Assert.DoesNotContain("mkdir &quot;$(NgxShimOut)&quot; &amp;&amp;", windows);

        // The same guard build.sh calls load-bearing, so the two hosts agree.
        string script = Read("native/optimum-ngx/build.sh");
        Assert.Contains("-fno-optimize-sibling-calls", script);
    }

    // ---- helpers -----------------------------------------------------------

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "not found: " + start);
        int to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to >= 0, "not found after " + start + ": " + end);
        return text[from..to];
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
