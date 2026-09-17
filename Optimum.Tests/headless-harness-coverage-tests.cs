using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The headless render harness's lib side: the two patched call sites, the
/// members the Cecil transplant has to carry, and the Cecil rules the new bodies
/// have to obey. Everything here reads the patch (or the working tree when the
/// patch is not there yet), so a body that is silently dropped from the patch
/// fails a test rather than a run.
///
/// Silence is part of the same contract: the harness runs in the user's own session
/// with no window, and it must not play sound at them either. The mixer is created
/// muted and every later attempt to restore the volume is answered with silence,
/// while the persisted sound settings are never touched - a capture that changed
/// what the user hears the next time they play would be the harness leaking into the
/// session it borrowed.
/// </summary>
public class HeadlessHarnessCoverageTests
{
    private const string PlatformPatch = "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch";
    private const string PlatformSource = "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs";

    [Fact]
    public void ClientProgramHidesTheWindowOnlyUnderTheEnvironmentSwitch()
    {
        string program = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client/ClientProgram.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client/ClientProgram.cs");

        int guard = program.IndexOf("if (Vintagestory.API.Config.OptimumHeadless.Enabled)", StringComparison.Ordinal);
        Assert.True(guard >= 0, "ClientProgram has no headless guard");

        int visible = program.IndexOf("val2.StartVisible = false;", StringComparison.Ordinal);
        int focused = program.IndexOf("val2.StartFocused = false;", StringComparison.Ordinal);
        Assert.True(visible > guard, "StartVisible is set outside the headless guard");
        Assert.True(focused > guard, "StartFocused is set outside the headless guard");

        // Inside the same block, and before the window is opened.
        int open = program.IndexOf("AttemptToOpenWindow(", StringComparison.Ordinal);
        Assert.True(open > focused, "the window is opened before the headless settings are applied");
        Assert.True(focused - guard < 400, "the headless settings drifted out of their guard block");
    }

    [Fact]
    public void RenderFrameCallsTheHarnessBesideTheParityDumpAndBeforeEndFrame()
    {
        string platform = ReadPatchedOrSource(PlatformPatch, PlatformSource);

        int parity = platform.IndexOf("OptimumRunParityDump();", StringComparison.Ordinal);
        // Before anything that searches from it: IndexOf(_, -1) throws
        // ArgumentOutOfRangeException, which says nothing about what is missing.
        Assert.True(parity >= 0, "the parity dump call site is gone");

        int guard = platform.IndexOf("if (Vintagestory.API.Config.OptimumHeadless.Active)", StringComparison.Ordinal);
        int tick = platform.IndexOf("OptimumHeadlessTick();", StringComparison.Ordinal);
        int endFrame = platform.IndexOf("EndFrame();", parity, StringComparison.Ordinal);

        Assert.True(guard > parity, "the harness is not gated behind OptimumHeadless.Active after the dump");
        Assert.True(tick > guard, "OptimumHeadlessTick is not inside its guard");
        Assert.True(endFrame > tick, "the harness runs after presentation instead of before it");
    }

    [Fact]
    public void TheHarnessCapturesThroughTheBackendAgnosticReadbackAndAsksTheChannelOrder()
    {
        string platform = ReadPatchedOrSource(PlatformPatch, PlatformSource);
        string capture = Body(platform, "private void OptimumHeadlessCaptureFrame(long worldFrame)");

        // The polymorphic readback, not an OS capture: this is the whole reason a
        // never-mapped window still produces frames.
        Assert.Contains("ReadDefaultFramebuffer(0, 0, width, height, handle.AddrOfPinnedObject());", capture);
        Assert.DoesNotContain("GrabScreenshot", capture);
        Assert.DoesNotContain("SaveScreenshot", capture);

        // The channel order is asked, never assumed - GL reads GL_BGRA and the
        // device's default target is R8G8B8A8.
        Assert.Contains("OptimumHeadless.WriteFrame(worldFrame, width, height, pixels, OptimumDefaultFramebufferIsBgra)", capture);
    }

    /// <summary>
    /// The capture is sized by the window, which is the size <c>BlitPrimaryToDefault</c>
    /// blits the finished frame into <c>EnumFrameBuffer.Default</c> at. The tick is called
    /// from window_RenderFrame after the whole render, where that blit has already
    /// happened; that placement is pinned by
    /// RenderFrameCallsTheHarnessBesideTheParityDumpAndBeforeEndFrame above, and the size
    /// is pinned here, so a capture never reads a render-resolution target instead.
    /// </summary>
    [Fact]
    public void TheCaptureIsSizedByTheWindow()
    {
        string platform = ReadPatchedOrSource(PlatformPatch, PlatformSource);
        string capture = Body(platform, "private void OptimumHeadlessCaptureFrame(long worldFrame)");

        // The window's client size, which is the size Default is blitted at.
        Assert.Contains("int width = ((NativeWindow)window).ClientSize.X;", capture);
        Assert.Contains("int height = ((NativeWindow)window).ClientSize.Y;", capture);
        // And the buffer it allocates is that size, so a short read cannot pass.
        Assert.Contains("byte[] pixels = new byte[width * height * 4];", capture);
        // Not the size of a render target.
        Assert.DoesNotContain("frameBuffers[0]", capture);
    }

    [Fact]
    public void TheCommandScriptIsRoutedTheWayTheChatHudRoutesTypedCommands()
    {
        string platform = ReadPatchedOrSource(PlatformPatch, PlatformSource);
        string run = Body(platform, "private void OptimumHeadlessRunCommand(ClientMain game, string line)");

        // The client prefix runs locally (.cam drives the scripted camera) and
        // everything else goes to the server (/time, /weather set the fixed scene).
        Assert.Contains("Vintagestory.Common.ChatCommandApi.ClientCommandPrefix", run);
        Assert.Contains("game.api.chatcommandapi.Execute(commandName, game.player, game.currentGroupid, arguments, null);", run);
        Assert.Contains("game.api.SendChatMessage(line, game.currentGroupid, null);", run);
    }

    [Fact]
    public void TheHarnessWaitsForTheWorldAndPinsTheSimulatedStep()
    {
        string platform = ReadPatchedOrSource(PlatformPatch, PlatformSource);
        string tick = Body(platform, "private void OptimumHeadlessTick()");

        // The same "the player is actually in the world" gate the parity dump uses.
        Assert.Contains("game == null || !game.BlocksReceivedAndLoaded", tick);
        // Reproducibility: the field vanilla's own cinematic recorder sets.
        Assert.Contains("game.DeltaTimeLimiter = OptimumHeadless.FixedDeltaTime;", tick);
        // Its own frame counter, so the parity dump's is untouched.
        Assert.Contains("optimumHeadlessWorldFrames = worldFrame + 1;", tick);
        // The line scripts/dev/headless-capture.sh waits for.
        Assert.Contains("\"[Optimum] headless: \"", tick);
    }

    [Fact]
    public void EveryNewLibMemberIsListedForTheCecilTransplant()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        string[] platformMembers =
        {
            "optimumHeadlessWorldFrames", "optimumHeadlessCommandsDone", "optimumHeadlessCaptureDone",
            "optimumHeadlessFramesWritten", "OptimumHeadlessTick", "OptimumHeadlessRunCommands",
            "OptimumHeadlessRunCommand", "OptimumHeadlessCaptureFrame",
        };
        foreach (string member in platformMembers)
        {
            Assert.Contains("\"" + member + "\"", patcher);
        }

        // The channel-order virtual, injected into the abstract platform: the
        // harness reads it, and a backend whose readback cannot produce BGRA would
        // override it (none does today - the Vulkan device converts instead).
        Assert.Contains("\"OptimumDefaultFramebufferIsBgra\"", patcher);

        // Both patched bodies live in methods the patcher already replaces whole.
        Assert.Contains("new(\"Vintagestory.Client.ClientProgram\", \"Start\", 2)", patcher);
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"window_RenderFrame\", 1)", patcher);
    }

    /// <summary>
    /// The channel order is one answer on both backends, and the Vulkan device is
    /// what makes it so.
    ///
    /// <para>The harness originally answered "the Vulkan
    /// readback is RGBA" through an override of this virtual. That made the harness
    /// correct and left every other caller wrong - the OpenGL body of
    /// <c>ReadDefaultFramebuffer</c> reads <c>GL_BGRA</c>, and its only vanilla
    /// caller, <c>Screenshot.GrabScreenshot</c> (the screenshot key and the AVI
    /// recorder), decodes into an <c>SKBitmap</c> declared <c>Bgra8888</c>, so every
    /// Vulkan screenshot came out red/blue swapped. The conversion moved into
    /// <c>VulkanClientPlatform.ReadDefaultFramebuffer</c> (Leaf.cs), where it fixes
    /// all of them at once, and the platform inherits the base's "true". This test
    /// is what keeps the override from coming back without the seam conversion
    /// being undone with it - including in the comments, which told the next reader
    /// the override still existed long after the override was gone.</para>
    /// </summary>
    [Fact]
    public void BothBackendsAnswerTheChannelOrderAndTheDeviceConvertsToIt()
    {
        string abstractPlatform = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs");
        Assert.Contains("public virtual bool OptimumDefaultFramebufferIsBgra", abstractPlatform);
        // The comment on it has to say what the code does: no platform overrides it.
        Assert.DoesNotContain("VulkanClientPlatform overrides this to false", abstractPlatform);
        Assert.Contains("no platform overrides this", abstractPlatform);
        // The base says BGRA, which is what the OpenGL body really produces.
        Assert.Contains("GL.ReadPixels(x, y, width, height, (PixelFormat)32993",
            ReadPatchedOrSource(PlatformPatch, PlatformSource));

        // Nothing overrides it any more: the platform's readback converts instead.
        string vulkan = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Frame.cs");
        Assert.DoesNotContain("override bool OptimumDefaultFramebufferIsBgra", vulkan);

        string leaf = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Leaf.cs");
        int at = leaf.IndexOf(
            "public override void ReadDefaultFramebuffer(int x, int y, int width, int height, IntPtr destination)",
            StringComparison.Ordinal);
        Assert.True(at > 0, "VulkanClientPlatform.ReadDefaultFramebuffer is gone");
        string body = leaf[at..leaf.IndexOf("\n    }", at, StringComparison.Ordinal)];
        Assert.Contains("device.ReadFramebufferColor(CurrentTargetId, x, y, width, height, destination);", body);
        Assert.Contains("PixelOrder.SwapRedAndBlue(destination, (long)width * height);", body);
        // A target that is already BGRA is left alone, so the format is asked.
        Assert.Contains("device.DefaultColorFormat is Format.B8G8R8A8Unorm", body);

        // ... and the conversion itself is the R <-> B swap, not something else.
        string pixelOrder = Read("Optimum.Render.Vulkan/Core/PixelOrder.cs");
        Assert.Contains("texel[0] = texel[2];", pixelOrder);
        Assert.Contains("texel[2] = first;", pixelOrder);

        // The device stays untouched: it is the general "read a target's colour 0"
        // operation the GPU tests inspect attachments with, in their stored order.
        string deviceFile = Read("Optimum.Render.Vulkan/VulkanDevice.cs");
        int deviceAt = deviceFile.IndexOf(
            "private void ReadFramebufferColor(VulkanFramebuffer? target, int x, int y, int width, int height, IntPtr destination)",
            StringComparison.Ordinal);
        Assert.True(deviceAt > 0, "VulkanDevice.ReadFramebufferColor is gone");
        Assert.DoesNotContain("SwapRedAndBlue",
            deviceFile[deviceAt..deviceFile.IndexOf("\n    }", deviceAt, StringComparison.Ordinal)]);
    }

    [Fact]
    public void TheNewLibBodiesObeyTheCecilRules()
    {
        string platform = ReadPatchedOrSource(PlatformPatch, PlatformSource);
        string[] bodies =
        {
            Body(platform, "private void OptimumHeadlessTick()"),
            Body(platform, "private void OptimumHeadlessRunCommands(ClientMain game)"),
            Body(platform, "private void OptimumHeadlessRunCommand(ClientMain game, string line)"),
            Body(platform, "private void OptimumHeadlessCaptureFrame(long worldFrame)"),
        };
        foreach (string body in bodies)
        {
            // No lambdas, no LINQ predicates, no local functions: the transplant
            // cannot carry the compiler-generated types any of those produce.
            Assert.DoesNotContain("=>", body);
            Assert.DoesNotContain("delegate", body);
            Assert.DoesNotContain(".Where(", body);
            Assert.DoesNotContain(".Select(", body);
            Assert.DoesNotContain(".Any(", body);
            Assert.DoesNotContain("foreach", body);
        }
    }

    [Fact]
    public void AnAbsurdFrameCountIsClampedInsteadOfAllocated()
    {
        // PlanFrames runs inside a static initialiser, so an unbounded count from
        // the environment does not produce a bad capture, it takes the client down
        // with an OutOfMemoryException wrapped in a TypeInitializationException.
        const long max = Vintagestory.API.Config.OptimumHeadless.MaxFrames;
        long[] clamped = Vintagestory.API.Config.OptimumHeadless.PlanFrames(0, long.MaxValue, 1);
        Assert.Equal(max, clamped.LongLength);
        Assert.Equal(0L, clamped[0]);

        // A clamped stride and first still produce an ascending, non-overflowing
        // list: long.MaxValue anywhere must not wrap into negative frame indices.
        long[] wild = Vintagestory.API.Config.OptimumHeadless.PlanFrames(long.MaxValue, 4, long.MaxValue);
        Assert.Equal(4, wild.Length);
        for (int i = 0; i < wild.Length; i++)
        {
            Assert.True(wild[i] >= 0L, "frame " + i + " overflowed to " + wild[i]);
            if (i > 0) Assert.True(wild[i] > wild[i - 1], "frames stopped ascending at " + i);
        }
    }

    [Fact]
    public void TheRendererRewriteInTheCaptureScriptIsAtomic()
    {
        // The script edits the user's live ModConfig/optimum.json. Truncating it in
        // place leaves a broken config behind if anything dies mid-write, so the new
        // file is written beside it and renamed over it - on both the set and the
        // restore, which share this one function.
        string script = Read("scripts/dev/headless-capture.sh");
        int start = script.IndexOf("set_renderer() {", StringComparison.Ordinal);
        Assert.True(start >= 0, "headless-capture.sh no longer has a set_renderer function");
        int end = script.IndexOf("\n}", start, StringComparison.Ordinal);
        string body = script.Substring(start, end - start);
        Assert.Contains("os.replace(", body);
        Assert.DoesNotContain("open(path, \"w\")", body);
        // And the restore on exit goes through the same function.
        Assert.Contains("set_renderer \"$SAVED_RENDERER\"", script);
    }

    [Fact]
    public void TheHarnessIsDocumentedWhereItWouldBeLookedFor()
    {
        string acceptance = Read("docs/vulkan-acceptance.md");
        int methods = acceptance.IndexOf("## 3. Methods", StringComparison.Ordinal);
        int evidence = acceptance.IndexOf("## 4. Evidence rules", StringComparison.Ordinal);
        int harness = acceptance.IndexOf("### Headless render harness", StringComparison.Ordinal);
        Assert.True(harness > methods && harness < evidence,
            "the headless harness is not documented among the acceptance methods");
        // A shimmer number is only comparable between runs if the method says what is
        // pinned and how a drifted run is rejected.
        Assert.Contains("determinism guard", acceptance, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("rejected, not reported", acceptance);

        string script = Read("scripts/dev/headless-capture.sh");
        // It must refuse to report a capture it cannot attribute to a backend.
        Assert.Contains("[Optimum] Vulkan renderer", script);
        Assert.Contains("[Optimum] OpenGL renderer:", script);
        // And it must never pattern-kill anything itself.
        Assert.Contains("kill-client.sh", script);
        Assert.DoesNotContain("pkill -f", script);
    }

    [Fact]
    public void AFrameListAndACadenceBothSelectFramesAndBothEndOnTheLastOne()
    {
        long[] list = Vintagestory.API.Config.OptimumHeadless.ParseFrameList("30, 10,10;  20 , -5, oops");
        Assert.Equal(new long[] { 10, 20, 30 }, list);
        Assert.Empty(Vintagestory.API.Config.OptimumHeadless.ParseFrameList("  "));

        long[] cadence = Vintagestory.API.Config.OptimumHeadless.PlanFrames(31, 4, 15);
        Assert.Equal(new long[] { 31, 46, 61, 76 }, cadence);
        // A nonsense stride still produces consecutive frames rather than nothing.
        Assert.Equal(new long[] { 5, 6, 7 }, Vintagestory.API.Config.OptimumHeadless.PlanFrames(5, 3, 0));
        Assert.Empty(Vintagestory.API.Config.OptimumHeadless.PlanFrames(0, 0, 1));
        Assert.Empty(Vintagestory.API.Config.OptimumHeadless.PlanFrames(0, -5, 1));

        foreach (long[] frames in new[] { list, cadence })
        {
            Assert.True(Vintagestory.API.Config.OptimumHeadless.ShouldCapture(frames, frames[0]));
            Assert.True(Vintagestory.API.Config.OptimumHeadless.ShouldCapture(frames, frames[^1]));
            Assert.False(Vintagestory.API.Config.OptimumHeadless.ShouldCapture(frames, frames[0] - 1));
            Assert.False(Vintagestory.API.Config.OptimumHeadless.ShouldCapture(frames, frames[^1] + 1));
            Assert.False(Vintagestory.API.Config.OptimumHeadless.CaptureFinished(frames, frames[^1] - 1));
            Assert.True(Vintagestory.API.Config.OptimumHeadless.CaptureFinished(frames, frames[^1]));
        }

        // Six digits, zero padded: two captures of the same list pair by file name
        // under scripts/dev/ssim.py.
        Assert.Equal("frame-000000.ppm", Vintagestory.API.Config.OptimumHeadless.FrameFileName(0));
        Assert.Equal("frame-000123.ppm", Vintagestory.API.Config.OptimumHeadless.FrameFileName(123));
    }

    [Fact]
    public void TheCommandScriptDropsBlanksAndCommentsAndSurvivesAMissingFile()
    {
        string path = Path.Combine(Path.GetTempPath(), "optimum-headless-commands-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(path, "# the fixed scene\n/time set 10\n\n   \n.cam load 1,2,3\n  .cam play 20  \n");
            Assert.Equal(new[] { "/time set 10", ".cam load 1,2,3", ".cam play 20" },
                Vintagestory.API.Config.OptimumHeadless.ReadCommands(path));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Empty(Vintagestory.API.Config.OptimumHeadless.ReadCommands(path));
        Assert.Empty(Vintagestory.API.Config.OptimumHeadless.ReadCommands(null!));
    }

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "not found: " + signature);
        int open = source.IndexOf('{', start);
        Assert.True(open >= 0, "no body: " + signature);
        int depth = 0;
        for (int offset = open; offset < source.Length; offset++)
        {
            if (source[offset] == '{') depth++;
            else if (source[offset] == '}' && --depth == 0) return source.Substring(start, offset - start + 1);
        }
        Assert.Fail("unbalanced body: " + signature);
        return string.Empty;
    }

    // ---- the harness makes no sound ----------------------------------------

    [Fact]
    public void TheMixerIsCreatedMutedWhileHeadless()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        int start = platform.IndexOf("public void StartAudio()", StringComparison.Ordinal);
        Assert.True(start > 0, "StartAudio must exist");
        string body = platform.Substring(start, 700);
        int created = body.IndexOf("audio = new AudioOpenAl(logger);", StringComparison.Ordinal);
        int muted = body.IndexOf("audio.MasterSoundLevel = 0f;", StringComparison.Ordinal);
        Assert.True(created > 0 && muted > created,
            "the mixer must be created and then muted while the headless harness runs");
        Assert.Contains("Vintagestory.API.Config.OptimumHeadless.Enabled", body);
    }

    [Fact]
    public void RestoringTheVolumeIsAnsweredWithSilenceWhileHeadless()
    {
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        Assert.Contains(
            "audio.MasterSoundLevel = (Vintagestory.API.Config.OptimumHeadless.Enabled ? 0f : value);",
            platform);
    }

    [Fact]
    public void ThePersistedSoundSettingsAreNeverWritten()
    {
        // Only the running mixer is silenced: nothing in the headless path may assign
        // ClientSettings' sound levels, or a capture would change what the user hears
        // the next time they play.
        string platform = Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs");
        int start = platform.IndexOf("public void StartAudio()", StringComparison.Ordinal);
        string body = platform.Substring(start, 700);
        Assert.DoesNotContain("ClientSettings.MasterSoundLevel =", body);
        Assert.DoesNotContain("ClientSettings.SoundLevel =", body);
    }

    [Fact]
    public void ThePatcherCarriesBothBodies()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"StartAudio\", 0", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"set_MasterSoundLevel\", 1", patcher);
    }

    /// <summary>
    /// The harness closes the client itself, and it does it from the render thread.
    ///
    /// <para>A never-mapped window cannot be sent a close event, so before this the
    /// only way to stop a capture was SIGTERM - and its handler calls WindowExit,
    /// and therefore Close(), from a signal thread while the render thread is still
    /// inside a frame. Every headless run ended in a crash report (2026-09-12:
    /// ShaderProgramBase.Use on a program whose graphics were already gone), which
    /// is what makes a harness useless as evidence: nobody can tell that crash from
    /// a real one. The exit therefore has to sit in the per-frame tick, and it has
    /// to wait for both artefacts - the parity dump's frame can be later than the
    /// last captured one.</para>
    /// </summary>
    [Fact]
    public void TheHarnessClosesItselfFromTheRenderThreadAndOnlyOnceBothArtefactsAreWritten()
    {
        string platform = ReadPatchedOrSource(PlatformPatch, PlatformSource);

        // The exit is reached from the per-frame tick, which runs on the render
        // thread inside window_RenderFrame - not from a signal handler.
        Assert.Contains("OptimumHeadlessExitIfDone();", platform);
        Assert.Contains("private void OptimumHeadlessExitIfDone()", platform);
        Assert.Contains("WindowExit(\"headless capture finished\", EnumExitMode.SoftExit)", platform);

        // Opt-in, and it fires once.
        Assert.Contains("if (!OptimumHeadless.ExitWhenDone || optimumHeadlessExitRequested)", platform);
        Assert.Contains("optimumHeadlessExitRequested = true;", platform);

        // Both artefacts gate it, and a run that asked for neither never exits here.
        Assert.Contains("if (OptimumHeadless.CaptureEnabled && !optimumHeadlessCaptureDone)", platform);
        Assert.Contains(
            "if (Vintagestory.API.Config.OptimumParityDump.Enabled && !optimumParityDumpDone)", platform);
        Assert.Contains(
            "if (!OptimumHeadless.CaptureEnabled && !Vintagestory.API.Config.OptimumParityDump.Enabled)",
            platform);

        // The flag exists on the API side and is read from the environment.
        string api = Read("sources/VintagestoryApi/Client/optimum-render-device.cs");
        Assert.Contains("ExitWhenDone = ResolveFlag(\"OPTIMUM_HEADLESS_EXIT_WHEN_DONE\")", api);

        // The patcher carries the new members, or the transplant drops them silently.
        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"optimumHeadlessExitRequested\"", patcher);
        Assert.Contains("\"OptimumHeadlessExitIfDone\"", patcher);

        // The capture script asks for it, waits for the clean exit before signalling,
        // and says which of the two happened.
        string script = Read("scripts/dev/headless-capture.sh");
        Assert.Contains("export OPTIMUM_HEADLESS_EXIT_WHEN_DONE=1", script);
        Assert.Contains("if wait_for_exit 60; then", script);
        Assert.Contains("CLOSE_HOW=\"closed itself\"", script);
        Assert.Contains("CLOSE_HOW=\"signalled\"", script);
        // And it reports a crash report rather than leaving it in the log.
        Assert.Contains("Critical error occurred", script);
    }

    private static string ReadPatchedOrSource(string patchPath, string sourcePath)
    {
        string? resolvedPatch = TryFind(patchPath);
        return resolvedPatch != null ? PatchReader.ReadPatchedContent(resolvedPatch) : Read(sourcePath);
    }

    private static string Read(string relativePath)
    {
        return File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
    }

    private static string? TryFind(string relativePath)
    {
        try
        {
            return PatchReader.FindRepositoryFile(relativePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}
