using System;
using System.IO;
using System.Text.RegularExpressions;
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

        // The one pacing block in the frame: the vanilla FPS limiter, now conditional.
        Match cap = Regex.Match(body, @"if \(([^)]*)ClientSettings\.VsyncMode != 1 &&[^)]*effectiveMaxFps[^)]*\)");
        Assert.True(cap.Success, "the FPS cap block is gone or was reshaped:\n" + body);
        Assert.Contains("!LatencyOwnsFrameCap &&", cap.Value);

        // Exactly one pacing block and exactly one read of the flag: a second one would
        // mean a second place that sleeps, which is the bug this seam exists to prevent.
        Assert.Single(Regex.Matches(body, @"ClientSettings\.VsyncMode != 1"));
        Assert.Single(Regex.Matches(body, @"LatencyOwnsFrameCap"));
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

        // The OpenGL platform calls both but declares neither, so vanilla pacing is untouched.
        string windows = VulkanPlatformSource.ReadClientPlatformWindows();
        Assert.DoesNotContain("override void LatencySleep", windows);
        Assert.DoesNotContain("override bool LatencyOwnsFrameCap", windows);
    }

    [Fact]
    public void ThePatcherShipsBothMembers()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        string injected = Block(patcher, "[\"Vintagestory.Client.NoObf.ClientPlatformAbstract\"] = new()", "},");
        Assert.Contains("\"LatencySleep\",", injected);
        Assert.Contains("\"LatencyOwnsFrameCap\",", injected);

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

        string frame = Read("Optimum.Render.Vulkan/Platform/VulkanClientPlatform.Frame.cs");
        string sleep = Body(frame, "public override void LatencySleep()");
        int sleepCall = sleep.IndexOf("backend.Sleep(frameId);", StringComparison.Ordinal);
        int input = sleep.IndexOf("LatencyMarker.InputSample", StringComparison.Ordinal);
        int simulation = sleep.IndexOf("LatencyMarker.SimulationStart", StringComparison.Ordinal);
        Assert.True(sleepCall >= 0, "the override does not call the backend's sleep:\n" + sleep);
        Assert.True(input > sleepCall, "InputSample is not stamped after the sleep:\n" + sleep);
        Assert.True(simulation > input, "SimulationStart is not stamped after InputSample:\n" + sleep);
        Assert.Contains("backend.OwnsFrameCap", Body(frame, "public override bool LatencyOwnsFrameCap"));
        // Stage C owns the counter and made it public, because the lib hook is the
        // one site outside the renderer that opens a frame (seam S2).
        Assert.Contains("public ulong BeginLatencyFrame()", Read("Optimum.Render.Vulkan/VulkanDevice.cs"));
    }

    [Fact]
    public void TheLatencyModeSettingIsPersistedAndClamped()
    {
        string config = Read("VintagestoryApi/Config/OptimumConfig.cs");
        Assert.Contains("public static string LatencyMode = \"off\";", config);
        Assert.Contains("public string LatencyMode { get; set; } = \"off\";", config);
        Assert.Contains("(nameof(OptimumConfigData.LatencyMode), LatencyMode),", config);
        Assert.Contains("LatencyMode = LatencyMode,", config);
        // Unrecognised values degrade to off rather than failing the parse.
        Assert.Contains("string requestedLatencyMode = data.LatencyMode?.Trim() ?? \"\";", config);
        Assert.Contains("\"off\";", config);
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
