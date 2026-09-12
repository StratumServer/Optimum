using System;
using System.IO;
using System.Text.RegularExpressions;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Vulkan-native plan, "Latency seams" S3: the sleep happens before the client samples input.
///
/// <c>ClientPlatformAbstract</c> declares two neutral virtuals, <c>window_RenderFrame</c> runs
/// the client's own FPS cap only while no backend owns it and calls <c>LatencySleep()</c>
/// immediately before <c>UpdateMousePosition()</c>, the patcher ships both members into the
/// shipped DLL, the OpenGL path keeps the neutral bodies, and VulkanClientPlatform overrides
/// both and self-checks them against the loaded lib.
/// </summary>
public class LatencyHooksCoverageTests
{
    private const string AbstractPath = "Vintagestory.Client.NoObf/ClientPlatformAbstract.cs";
    private const string RenderFrameSignature = "private void window_RenderFrame(FrameEventArgs e)";

    [Fact]
    public void TheFrameCapBlockIsGuardedByLatencyOwnsFrameCap()
    {
        string body = StripComments(Body(VulkanPlatformSource.ReadClientPlatformWindows(), RenderFrameSignature));

        // The effective cap is computed once, under the vanilla limiter's own conditions,
        // and 0 means uncapped - the number both pacing sides read.
        Match compute = Regex.Match(
            body, @"if \(ClientSettings\.VsyncMode != 1 && effectiveMaxFps > 10f && effectiveMaxFps < 241f\)");
        Assert.True(compute.Success, "the effective cap is no longer computed once:\n" + body);
        Assert.Contains("int latencyFrameCap = 0;", body);
        Assert.Contains("latencyFrameCap = (int)effectiveMaxFps;", body);

        // The one pacing block in the frame: the vanilla FPS limiter, now conditional.
        Match cap = Regex.Match(body, @"if \(!LatencyOwnsFrameCap && latencyFrameCap > 0\)");
        Assert.True(cap.Success, "the FPS cap block is gone or was reshaped:\n" + body);

        // Exactly one pacing block and exactly one read of the flag: a second one would
        // mean a second place that sleeps, which is the bug this seam exists to prevent.
        Assert.Single(Regex.Matches(body, @"ClientSettings\.VsyncMode != 1"));
        Assert.Single(Regex.Matches(body, @"LatencyOwnsFrameCap"));
    }

    /// <summary>
    /// Latency review follow-up, 2026-09-12: with the mode on by default the lib's own
    /// limiter stands down, so the cap it computed - the background-window reduction
    /// included - has to reach the pacing backend, exactly once, before the sleep it paces.
    /// Without it an unfocused window would run uncapped and burn the GPU in the background.
    /// </summary>
    [Fact]
    public void TheEffectiveFrameCapIsHandedOverOnceBeforeTheSleep()
    {
        string body = StripComments(Body(VulkanPlatformSource.ReadClientPlatformWindows(), RenderFrameSignature));

        int background = body.IndexOf("effectiveMaxFps = OptimumBgMaxFps;", StringComparison.Ordinal);
        int computed = body.IndexOf("int latencyFrameCap = 0;", StringComparison.Ordinal);
        int handOver = body.IndexOf("SetLatencyFrameCap(latencyFrameCap);", StringComparison.Ordinal);
        int limiter = body.IndexOf("!LatencyOwnsFrameCap", StringComparison.Ordinal);
        int sleep = body.IndexOf("LatencySleep();", StringComparison.Ordinal);

        Assert.True(background >= 0, "the background-window cap is gone:\n" + body);
        Assert.True(computed > background, "the cap is computed before the background reduction:\n" + body);
        Assert.True(handOver > computed, "the backend is handed a cap that was never computed:\n" + body);
        Assert.True(handOver < limiter, "the hand-over is not before the limiter block:\n" + body);
        Assert.True(sleep > handOver, "the cap reaches the backend after the sleep it paces:\n" + body);
        Assert.Single(Regex.Matches(body, @"SetLatencyFrameCap\("));
    }

    [Fact]
    public void LatencySleepIsCalledOnceAndBeforeTheInputSample()
    {
        string body = StripComments(Body(VulkanPlatformSource.ReadClientPlatformWindows(), RenderFrameSignature));

        int sleep = body.IndexOf("LatencySleep();", StringComparison.Ordinal);
        int mouse = body.IndexOf("UpdateMousePosition();", StringComparison.Ordinal);
        int cap = body.IndexOf("ClientSettings.VsyncMode != 1", StringComparison.Ordinal);
        int beginFrame = body.IndexOf("BeginFrame();", StringComparison.Ordinal);

        Assert.True(sleep >= 0, "LatencySleep() is not called in window_RenderFrame:\n" + body);
        Assert.True(mouse > sleep, "the input sample does not follow the sleep:\n" + body);
        Assert.True(sleep > cap, "the sleep runs before the frame cap block:\n" + body);
        Assert.True(beginFrame > mouse, "BeginFrame moved before the input sample:\n" + body);
        Assert.Single(Regex.Matches(body, @"LatencySleep\(\)"));
        Assert.Single(Regex.Matches(body, @"UpdateMousePosition\(\)"));
    }

    [Fact]
    public void WindowRenderFrameStaysCecilSafe()
    {
        string body = StripComments(Body(VulkanPlatformSource.ReadClientPlatformWindows(), RenderFrameSignature));
        Assert.DoesNotContain("=>", body);
        Assert.DoesNotContain("delegate", body);
        Assert.False(Regex.IsMatch(body, @"\.(All|Any|Where|Select|First|Count)\s*\("),
            "LINQ in a transplanted method:\n" + body);
    }

    [Fact]
    public void TheAbstractPlatformDeclaresNeutralVirtualsAndOpenGlDoesNotOverrideThem()
    {
        string platform = ReadLib(AbstractPath);
        Assert.Equal("{ }", Regex.Replace(Body(platform, "public virtual void LatencySleep()"), @"\s+", " ").Trim());
        Assert.Contains("public virtual bool LatencyOwnsFrameCap => false;", platform);
        Assert.Equal("{ }", Regex.Replace(
            Body(platform, "public virtual void SetLatencyFrameCap(int maxFps)"), @"\s+", " ").Trim());

        // The OpenGL platform calls all three but declares none, so vanilla pacing is untouched.
        string windows = VulkanPlatformSource.ReadClientPlatformWindows();
        Assert.DoesNotContain("override void LatencySleep", windows);
        Assert.DoesNotContain("override bool LatencyOwnsFrameCap", windows);
        Assert.DoesNotContain("override void SetLatencyFrameCap", windows);
    }

    [Fact]
    public void ThePatcherShipsBothMembers()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        string injected = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformAbstract\"] = new()", "},");
        Assert.Contains("\"LatencySleep\",", injected);
        Assert.Contains("\"LatencyOwnsFrameCap\",", injected);
        Assert.Contains("\"SetLatencyFrameCap\",", injected);

        // The changed frame body has to be transplanted too, or the calls never ship.
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"window_RenderFrame\", 1),", patcher);
    }

    [Fact]
    public void TheVulkanPlatformSelfChecksAndOverridesBoth()
    {
        string selfCheck = Block(Read(VulkanPlatformSource.MainFile),
            "internal static readonly ExpectedVirtual[] ExpectedVirtuals", "};");
        Assert.Contains("new(true, \"LatencySleep\", Array.Empty<string>()),", selfCheck);
        Assert.Contains("new(true, \"get_LatencyOwnsFrameCap\", Array.Empty<string>()),", selfCheck);
        Assert.Contains("new(true, \"SetLatencyFrameCap\", new[] { \"Int32\" }),", selfCheck);

        string frame = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Frame.cs");
        string sleep = Body(frame, "public override void LatencySleep()");
        int sleepCall = sleep.IndexOf("backend.Sleep(frameId);", StringComparison.Ordinal);
        int input = sleep.IndexOf("LatencyMarker.InputSample", StringComparison.Ordinal);
        int simulation = sleep.IndexOf("LatencyMarker.SimulationStart", StringComparison.Ordinal);
        Assert.True(sleepCall >= 0, "the override does not call the backend's sleep:\n" + sleep);
        Assert.True(input > sleepCall, "InputSample is not stamped after the sleep:\n" + sleep);
        Assert.True(simulation > input, "SimulationStart is not stamped after InputSample:\n" + sleep);
        Assert.Contains("backend.OwnsFrameCap", Body(frame, "public override bool LatencyOwnsFrameCap"));

        // The cap hand-over: FPS in, the backend's MinimumIntervalUs out, applied only
        // when it changed, and never to a backend whose mode is Off.
        string cap = Body(frame, "public override void SetLatencyFrameCap(int maxFps)");
        Assert.Contains("if (current.Mode == LatencyMode.Off) return;", cap);
        Assert.Contains("ulong interval = FrameCapIntervalUs(maxFps);", cap);
        Assert.Contains("if (current.MinimumIntervalUs == interval) return;", cap);
        Assert.Contains("backend.Apply(new LatencySettings(current.Mode, interval));", cap);
        // Stage C owns the counter and made it public, because the lib hook is the
        // one site outside the renderer that opens a frame (seam S2).
        Assert.Contains("public ulong BeginLatencyFrame()", Read("Optimum.Render.Vulkan/VulkanDevice.cs"));
    }

    [Fact]
    public void TheLatencyModeSettingIsPersistedAndClamped()
    {
        // On by default since the 2026-09-12 acceptance runs (input-to-present 7.67 ms
        // off against 1.85 ms on NV Reflex and 1.84 ms on the vendor-neutral Native
        // backend), both in the live value and in the persisted data object, so a
        // config that never mentions the setting comes up on.
        foreach (string config in new[]
        {
            Read("VintagestoryApi/Config/OptimumConfig.cs"),
            Read("sources/VintagestoryApi/Config/OptimumConfig.cs"),
        })
        {
            Assert.Contains("public static string LatencyMode = \"on\";", config);
            Assert.Contains("public string LatencyMode { get; set; } = \"on\";", config);
            Assert.Contains("(nameof(OptimumConfigData.LatencyMode), LatencyMode),", config);
            Assert.Contains("LatencyMode = LatencyMode,", config);
            // Unrecognised values still degrade to a valid mode rather than failing the parse.
            Assert.Contains("string requestedLatencyMode = data.LatencyMode?.Trim() ?? \"\";", config);
            Assert.Contains("\"off\";", config);
        }
    }

    /// <summary>
    /// The round trip itself, through a real optimum.json: the default is on, a
    /// config that never mentions the setting comes up on, an explicit mode
    /// survives load and save, and an unknown one degrades to a valid mode.
    /// </summary>
    [Theory]
    [InlineData("\"on\"", "on")]
    [InlineData("\"boost\"", "boost")]
    [InlineData("\"off\"", "off")]
    [InlineData("\"BOOST\"", "boost")]
    [InlineData("\"  on  \"", "on")]
    [InlineData("\"nonsense\"", "off")]
    [InlineData("\"\"", "off")]
    [InlineData("null", "off")]
    [InlineData(null, "on")]
    public void TheLatencyModeRoundTripsThroughOptimumJson(string? writtenJsonValue, string expected)
    {
        string original = OptimumConfig.LatencyMode;
        string dataPath = Path.Combine(Path.GetTempPath(), "optimum-latency-" + Guid.NewGuid().ToString("N"));
        try
        {
            // SetDataPath re-reads the launcher's shader-compatibility report and treats a
            // missing one as "every shader feature disabled", which is global state the
            // rest of the assembly reads. A report that says the scan succeeded and
            // disabled nothing leaves that state exactly as a fresh process has it.
            Directory.CreateDirectory(Path.Combine(dataPath, ".optimum"));
            File.WriteAllText(Path.Combine(dataPath, ".optimum", "shader-compatibility.json"),
                "{ \"ScanFailed\": false, \"DisabledFeatures\": [] }");

            OptimumConfig.SetDataPath(dataPath);
            string configPath = Path.Combine(dataPath, "ModConfig", "optimum.json");
            // A config with no LatencyMode key at all is the upgrade case: it must come up on.
            File.WriteAllText(configPath, writtenJsonValue == null
                ? "{}"
                : "{ \"LatencyMode\": " + writtenJsonValue + " }");

            OptimumConfig.LatencyMode = "boost";
            OptimumConfig.Load();
            Assert.Equal(expected, OptimumConfig.LatencyMode);

            // Load writes the normalised value back, so the file now says what was loaded.
            Assert.Contains("\"LatencyMode\": \"" + expected + "\"", File.ReadAllText(configPath));

            // And it survives another trip through the file.
            OptimumConfig.LatencyMode = "boost";
            OptimumConfig.Load();
            Assert.Equal(expected, OptimumConfig.LatencyMode);
        }
        finally
        {
            OptimumConfig.LatencyMode = original;
            try { Directory.Delete(dataPath, recursive: true); } catch (IOException) { }
        }
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

    private static string ReadLib(string relativePath)
    {
        try
        {
            return File.ReadAllText(PatchReader.FindRepositoryFile("build/VintagestoryLib/" + relativePath));
        }
        catch (FileNotFoundException)
        {
            return PatchReader.ReadPatchedContent(PatchReader.FindRepositoryFile(
                "patches/VintagestoryLib/" + relativePath + ".patch"));
        }
    }

    private static string StripComments(string source) =>
        Regex.Replace(source, @"//[^\n]*", string.Empty);

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + signature);
        int open = source.IndexOf('{', start + signature.Length);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException("unbalanced body: " + signature);
    }

    private static string Block(string source, string header, string terminator)
    {
        int start = source.IndexOf(header, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing: " + header);
        int end = source.IndexOf(terminator, start, StringComparison.Ordinal);
        Assert.True(end > start);
        return source.Substring(start, end - start);
    }
}
