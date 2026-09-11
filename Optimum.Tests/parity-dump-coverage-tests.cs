using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Phase 0 per-attachment parity dump (OPTIMUM_PARITY_DUMP): source coverage for
/// the patched platform, the shared API writer, the Vulkan readback, the capture
/// script, ssim.py and the acceptance documents.
/// </summary>
public class ParityDumpCoverageTests
{
    private const string PlatformPatch = "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs.patch";
    private const string PlatformSource = "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformWindows.cs";

    /// <summary>EnumFrameBuffer as vanilla ships it; cross-checked against the API file when present.</summary>
    private static readonly Dictionary<string, int> EnumFrameBufferValues = new()
    {
        ["Primary"] = 0, ["Transparent"] = 1, ["BlurHorizontalMedRes"] = 2, ["BlurVerticalMedRes"] = 3,
        ["FindBright"] = 4, ["LiquidDepth"] = 5, ["GodRays"] = 7, ["BlurVerticalLowRes"] = 8,
        ["BlurHorizontalLowRes"] = 9, ["Luma"] = 10, ["ShadowmapFar"] = 11, ["ShadowmapNear"] = 12,
        ["SSAO"] = 13, ["SSAOBlurVertical"] = 14, ["SSAOBlurHorizontal"] = 15,
        ["SSAOBlurVerticalHalfRes"] = 16, ["SSAOBlurHorizontalHalfRes"] = 17,
    };

    [Fact]
    public void DumpedSlotListMatchesTheFramebuffersBothSetupsCreate()
    {
        string platform = ReadSourceOrPatched(PlatformPatch, PlatformSource);
        Dictionary<string, int> constants = Constants(platform);

        string glBody = MethodBody(platform, "public virtual List<FrameBufferRef> SetupDefaultFrameBuffers()");
        // Phase 1A step 4: the device-path setup is VulkanClientPlatform's SetupDefaultFrameBuffers
        // override, indexing the same slot constants.
        string vulkan = VulkanPlatformSource.Read();
        string deviceBody = MethodBody(vulkan, "public override List<FrameBufferRef> SetupDefaultFrameBuffers()");
        foreach (string slotConstant in new[] { "OptimumFsrFramebufferIndex", "OptimumTaaHistoryIndexA", "OptimumTaaHistoryIndexB", "OptimumTaaSharpenIndex" })
        {
            Assert.Contains("private const int " + slotConstant + " = " + constants[slotConstant] + ";", vulkan);
        }
        string namesBody = MethodBody(platform, "private string OptimumParitySlotName(int slot)");

        SortedSet<int> glSlots = AssignedSlots(glBody, constants);
        SortedSet<int> deviceSlots = AssignedSlots(deviceBody, constants);
        Assert.Equal(glSlots, deviceSlots);

        var named = new SortedDictionary<int, string>();
        foreach (Match match in Regex.Matches(namesBody, @"case\s+(\w+):\s*return\s+""(\w+)"";"))
        {
            int slot = int.TryParse(match.Groups[1].Value, out int literal) ? literal : constants[match.Groups[1].Value];
            named.Add(slot, match.Groups[2].Value);
        }
        Assert.Equal(glSlots, new SortedSet<int>(named.Keys));

        // Vanilla slots are named exactly as EnumFrameBuffer names them.
        Dictionary<string, int> enumValues = EnumValues();
        foreach (KeyValuePair<int, string> slot in named)
        {
            if (enumValues.TryGetValue(slot.Value, out int value)) Assert.Equal(value, slot.Key);
            else Assert.StartsWith("Optimum", slot.Value);
        }
        Assert.Equal("OptimumFsr", named[constants["OptimumFsrFramebufferIndex"]]);
        Assert.Equal("OptimumTaaHistoryA", named[constants["OptimumTaaHistoryIndexA"]]);
        Assert.Equal("OptimumTaaHistoryB", named[constants["OptimumTaaHistoryIndexB"]]);
        Assert.Equal("OptimumTaaSharpen", named[constants["OptimumTaaSharpenIndex"]]);

        // The dump walks the whole framebuffer list, colour and depth, so a slot
        // added to the setups is dumped even before it gets a name.
        string dump = MethodBody(platform, "private void OptimumRunParityDump()");
        Assert.Contains("List<FrameBufferRef> list = frameBuffers;", dump);
        Assert.Contains("slot < list.Count", dump);
        Assert.Contains("frameBuffer.ColorTextureIds.Length", dump);
        Assert.Contains("frameBuffer.DepthTextureId", dump);
        Assert.Contains("\"color\" + attachment", dump);
        Assert.Contains("\"depth\"", dump);
        Assert.Contains("logger.Notification(\"[Optimum] parity dump: \" + attachments + \" attachments -> \" + directory);", dump);
    }

    [Fact]
    public void GlAndVulkanWriteThroughOneFileNameFormat()
    {
        string api = ReadApi();
        Assert.Equal(1, Count(api, "public const string FileNameFormat = \"{0}-{1}-{2}-{3}.{4}\";"));
        Assert.Contains("CultureInfo.InvariantCulture, FileNameFormat,", api);
        Assert.Contains("public virtual Vintagestory.API.Config.OptimumTextureReadback ReadTextureForParity(int textureId)",
            Read("build/VintagestoryLib/Vintagestory.Client.NoObf/ClientPlatformAbstract.cs"));

        string platform = ReadSourceOrPatched(PlatformPatch, PlatformSource);
        string attachment = MethodBody(platform, "private int OptimumParityDumpAttachment(");
        // Phase 1A step 4: the readback is the platform virtual ReadTextureForParity - glGetTexImage
        // in ClientPlatformWindows, the device readback in VulkanClientPlatform.
        int read = attachment.IndexOf("OptimumTextureReadback readback = ReadTextureForParity(textureId);", StringComparison.Ordinal);
        int write = attachment.IndexOf("OptimumParityDump.Write(directory, slot, slotName, attachment, readback)", StringComparison.Ordinal);
        Assert.True(read >= 0 && write > read, "both backends must reach the one shared writer");
        Assert.Contains("return OptimumParityReadTextureGl(textureId);",
            MethodBody(platform, "public override OptimumTextureReadback ReadTextureForParity(int textureId)"));
        string vulkan = VulkanPlatformSource.Read();
        Assert.Contains("return device.ReadTextureForParity(textureId);",
            MethodBody(vulkan, "public override OptimumTextureReadback ReadTextureForParity(int textureId)"));
        Assert.Equal(1, Count(vulkan, "device.ReadTextureForParity("));
        Assert.DoesNotContain("device.ReadTextureForParity(", platform);
        Assert.Equal(1, Count(platform, "OptimumParityDump.Write("));
        Assert.DoesNotContain("FileStream", MethodBody(platform, "private OptimumTextureReadback OptimumParityReadTextureGl(int textureId)"));

        string device = Read("Optimum.Render.Vulkan/VulkanDevice.cs");
        string dump = Read("Optimum.Render.Vulkan/Core/TextureDump.cs");
        Assert.Contains("public OptimumTextureReadback? ReadTextureForParity(int textureId)", device);
        Assert.Contains("TextureDump.ToParityReadback(", device);
        foreach (string source in new[] { device, dump })
        {
            Assert.DoesNotContain("FileNameFormat", source);
            Assert.DoesNotContain("{0}-{1}-{2}-{3}", source);
            Assert.DoesNotContain("OptimumParityDump.Write", source);
        }
    }

    [Fact]
    public void EnvUnsetCostsOneStaticBoolCheckPerFrame()
    {
        string api = ReadApi();
        Assert.Contains("public static readonly bool Enabled = Directory != null;", api);
        Assert.Contains("Environment.GetEnvironmentVariable(\"OPTIMUM_PARITY_DUMP\")", api);
        Assert.Contains("Environment.GetEnvironmentVariable(\"OPTIMUM_PARITY_FRAME\")", api);

        string platform = ReadSourceOrPatched(PlatformPatch, PlatformSource);
        string frame = MethodBody(platform, "private void window_RenderFrame(FrameEventArgs e)");
        const string guard = "if (Vintagestory.API.Config.OptimumParityDump.Enabled)\n\t\t{\n\t\t\tOptimumRunParityDump();";
        string normalized = frame.Replace("\r\n", "\n");
        // Phase 1A step 4: one frame body for both backends, bracketed by the platform.
        Assert.Equal(1, Count(normalized, "OptimumRunParityDump();"));
        Assert.Equal(1, Count(platform, "OptimumRunParityDump();"));

        // After the frame (post chain and final blit), before the platform ends it.
        int begin = normalized.IndexOf("BeginFrame();", StringComparison.Ordinal);
        int frameCall = normalized.IndexOf("frameHandler.OnNewFrame(dt);", StringComparison.Ordinal);
        int guardIndex = normalized.IndexOf(guard, StringComparison.Ordinal);
        int end = normalized.IndexOf("EndFrame();", StringComparison.Ordinal);
        Assert.True(begin >= 0 && frameCall > begin && guardIndex > frameCall && end > guardIndex);

        // EndFrame is SwapBuffers on OpenGL and Present on Vulkan.
        Assert.Contains("((GameWindow)window).SwapBuffers();", MethodBody(platform, "public override void EndFrame()"));
        Assert.Contains("device.Present();", MethodBody(VulkanPlatformSource.Read(), "public override void EndFrame()"));

        // The final blit happens inside OnNewFrame, so the dump sees the finished frame.
        string screenManager = ReadSourceOrPatched(
            "patches/VintagestoryLib/Vintagestory.Client/ScreenManager.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client/ScreenManager.cs");
        Assert.Contains("Platform.BlitPrimaryToDefault();", MethodBody(screenManager, "internal void Render(float dt)"));

        // Frames count only once the player is in the world, and the dump runs once.
        string dump = MethodBody(platform, "private void OptimumRunParityDump()");
        int done = dump.IndexOf("if (optimumParityDumpDone)", StringComparison.Ordinal);
        int inWorld = dump.IndexOf("!runningScreen.runningGame.BlocksReceivedAndLoaded", StringComparison.Ordinal);
        int count = dump.IndexOf("optimumParityWorldFrames = worldFrame + 1;", StringComparison.Ordinal);
        int frameCheck = dump.IndexOf("if (worldFrame != OptimumParityDump.Frame)", StringComparison.Ordinal);
        int latch = dump.IndexOf("optimumParityDumpDone = true;", StringComparison.Ordinal);
        Assert.True(done >= 0 && inWorld > done && count > inWorld && frameCheck > count && latch > frameCheck);
    }

    [Fact]
    public void GlReadbackUnbindsThePackBufferAndReadsTheSharedRepresentation()
    {
        string platform = ReadSourceOrPatched(PlatformPatch, PlatformSource);
        string gl = MethodBody(platform, "private OptimumTextureReadback OptimumParityReadTextureGl(int textureId)");
        Assert.Contains("GL.BindBuffer((BufferTarget)35051, 0);", gl);
        Assert.Contains("GL.BindBuffer((BufferTarget)35051, previousPackBuffer);", gl);
        Assert.Contains("GL.BindTexture((TextureTarget)3553, previousTexture);", gl);
        Assert.Contains("(GetTextureParameter)4099, out internalFormat", gl);
        Assert.Contains("GL.GetTexImage((TextureTarget)3553, 0, (PixelFormat)6402, (PixelType)5126, depth);", gl);
        Assert.Contains("GL.GetTexImage((TextureTarget)3553, 0, (PixelFormat)6408, (PixelType)5121, bytes);", gl);
        Assert.Contains("GL.GetTexImage((TextureTarget)3553, 0, (PixelFormat)6408, (PixelType)5126, floats);", gl);
    }

    [Fact]
    public void ParityMembersAreCecilSafeAndShipped()
    {
        string platform = ReadSourceOrPatched(PlatformPatch, PlatformSource);
        string[] methods =
        {
            "private void OptimumRunParityDump()",
            "private string OptimumParitySlotName(int slot)",
            "private int OptimumParityDumpAttachment(",
            "private OptimumTextureReadback OptimumParityReadTextureGl(int textureId)",
        };
        foreach (string signature in methods)
        {
            string body = MethodBody(platform, signature);
            Assert.DoesNotContain("=>", body);
            Assert.DoesNotContain("$\"", body);
            Assert.DoesNotContain("delegate", body);
            Assert.DoesNotContain("string.Format", body);
            foreach (string line in body.Split('\n'))
            {
                // More than four concatenated operands can lower to span helpers.
                Assert.True(Count(line, "\" + ") + Count(line, " + \"") <= 3, "long concatenation: " + line.Trim());
            }
        }
        Assert.DoesNotContain("optimumParityWorldFrames =", platform.Substring(0, platform.IndexOf("private void OptimumRunParityDump()", StringComparison.Ordinal)));

        string patcher = Read("Optimum.Patcher/Program.cs");
        foreach (string member in new[]
                 {
                     "optimumParityWorldFrames", "optimumParityDumpDone", "OptimumRunParityDump",
                     "OptimumParitySlotName", "OptimumParityDumpAttachment", "OptimumParityReadTextureGl",
                 })
        {
            Assert.Contains("\"" + member + "\",", patcher);
        }
        Assert.Contains("new(\"Vintagestory.Client.NoObf.ClientPlatformWindows\", \"window_RenderFrame\", 1)", patcher);
    }

    [Fact]
    public void SsimSelfTestPasses()
    {
        string script = PatchReader.FindRepositoryFile("scripts/dev/ssim.py");
        string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(script)))!;
        var start = new ProcessStartInfo("python3")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("scripts/dev/ssim.py");
        start.ArgumentList.Add("--self-test");
        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(180_000), "ssim.py --self-test did not finish");
        Assert.True(process.ExitCode == 0, "ssim.py --self-test exited " + process.ExitCode + ": " + stdout + stderr);
        Assert.Contains("ssim.py self-test: ok", stdout);
    }

    [Fact]
    public void CaptureScriptConfirmsRendererRestoresConfigAndNeverPatternKills()
    {
        string script = Read("scripts/dev/parity-capture.sh");
        foreach (string needle in new[]
                 {
                     "--renderer", "--world", "--frame", "--out",
                     "export OPTIMUM_PARITY_DUMP=\"$OUT_DIR\"", "export OPTIMUM_PARITY_FRAME=\"$FRAME\"",
                     "scripts/dev/run-client.sh", "scripts/dev/kill-client.sh",
                     "trap cleanup EXIT", "set_renderer \"$SAVED_RENDERER\"",
                     "wait_for \"[Client Chat] Welcome\"", "wait_for \"[Optimum] parity dump:\"",
                     "[Optimum] Vulkan renderer", "[Optimum] OpenGL renderer:",
                 })
        {
            Assert.Contains(needle, script);
        }
        Assert.True(script.IndexOf("wait_for \"[Client Chat] Welcome\"", StringComparison.Ordinal)
                    < script.IndexOf("wait_for \"[Optimum] parity dump:\"", StringComparison.Ordinal));
        Assert.DoesNotContain("pkill", script);
        Assert.DoesNotContain("pgrep", script);
        Assert.DoesNotContain("xdotool", script); // no chat commands
        Assert.DoesNotContain("sleep \"$", script); // polls, never a blind sleep on a configured delay

        // Errors stop the script (the EXIT trap still restores the config), the restore
        // waits for the client process to be gone (kill-client.sh does not wait), and a
        // client that exited is re-checked at once, not after a delay.
        Assert.Contains("\nset -euo pipefail\n", script);
        string cleanup = script.Substring(script.IndexOf("cleanup() {", StringComparison.Ordinal));
        cleanup = cleanup.Substring(0, cleanup.IndexOf("\n}\n", StringComparison.Ordinal));
        int waitExit = cleanup.IndexOf("wait_for_exit ", StringComparison.Ordinal);
        int restore = cleanup.IndexOf("set_renderer \"$SAVED_RENDERER\"", StringComparison.Ordinal);
        Assert.True(waitExit >= 0 && restore > waitExit, "the config restore must wait for the client to exit");
        Assert.True(script.IndexOf("wait_for_exit() {", StringComparison.Ordinal) < script.IndexOf("cleanup() {", StringComparison.Ordinal));
        Assert.Contains("grep -m1 -F \"[Optimum] parity dump:\" \"$LOG\" || true", script);
        string waitFor = script.Substring(script.IndexOf("wait_for() {", StringComparison.Ordinal));
        waitFor = waitFor.Substring(0, waitFor.IndexOf("\n}\n", StringComparison.Ordinal));
        Assert.DoesNotContain("sleep 1", waitFor);
    }

    [Fact]
    public void AcceptanceDocumentsHaveTheirSections()
    {
        string allowlist = Read("docs/parity-allowlist.md");
        Assert.Contains("| attachment | reason | max accepted deviation |", allowlist);
        Assert.DoesNotMatch(new Regex(@"^\|\s*[^a|\-\s]", RegexOptions.Multiline), allowlist); // table starts empty

        string acceptance = Read("docs/vulkan-acceptance.md");
        string[] sections =
        {
            "## 0. Preconditions", "## 1. Renderer confirmation", "## 2. Acceptance rows",
            "## 3. Methods", "## 4. Evidence rules", "## 5. Decision record", "## 6. Vendor matrix",
        };
        int last = -1;
        foreach (string section in sections)
        {
            int index = acceptance.IndexOf(section, StringComparison.Ordinal);
            Assert.True(index > last, "missing or out of order: " + section);
            last = index;
        }
        foreach (string needle in new[]
                 {
                     "### Phase 0 exit", "GL-vs-GL noise floor", "Pacing baseline, OpenGL", "Pacing baseline, Vulkan",
                     "### Milestone 1", "scripts/dev/pacing-gate.sh", "scripts/dev/ssim.py", "sync,best",
                     "luma-diff", "Screenshot pairs are\n  never evidence", "Intel Arc 140V",
                     "`ScopesOpened == PassCount`", "SSIM >= min(0.98",
                 })
        {
            Assert.Contains(needle, acceptance);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static SortedSet<int> AssignedSlots(string body, Dictionary<string, int> constants)
    {
        var slots = new SortedSet<int>();
        foreach (Match match in Regex.Matches(body, @"list\[([^\]]+)\]\s*=[^=]"))
        {
            string index = match.Groups[1].Value.Trim();
            if (int.TryParse(index, out int literal)) slots.Add(literal);
            else if (constants.TryGetValue(index, out int constant)) slots.Add(constant);
            else if (index == "(int)enumFrameBuffer")
            {
                Match array = Regex.Match(body, @"EnumFrameBuffer\[\]\s+\w+\s*=\s*new\s+EnumFrameBuffer\[\d+\]\s*\{([^}]*)\}");
                Assert.True(array.Success, "the SSAO blur slot array moved");
                foreach (Match name in Regex.Matches(array.Groups[1].Value, @"EnumFrameBuffer\.(\w+)"))
                {
                    slots.Add(EnumFrameBufferValues[name.Groups[1].Value]);
                }
            }
            else Assert.Fail("unrecognised framebuffer slot index: " + index);
        }
        Assert.NotEmpty(slots);
        return slots;
    }

    private static Dictionary<string, int> Constants(string platform)
    {
        var constants = new Dictionary<string, int>();
        foreach (Match match in Regex.Matches(platform, @"const int (\w+) = (\d+);"))
        {
            constants[match.Groups[1].Value] = int.Parse(match.Groups[2].Value);
        }
        return constants;
    }

    private static Dictionary<string, int> EnumValues()
    {
        string? path = TryFind("VintagestoryApi/Client/Render/EnumFrameBuffer.cs");
        if (path != null)
        {
            string source = File.ReadAllText(path);
            foreach (KeyValuePair<string, int> entry in EnumFrameBufferValues)
            {
                Assert.Matches(new Regex(@"\b" + entry.Key + @"\s*=\s*" + entry.Value + @"\b"), source);
            }
        }
        return EnumFrameBufferValues;
    }

    private static string MethodBody(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "method not found: " + signature);
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
        }
        throw new InvalidOperationException("unbalanced body: " + signature);
    }

    private static string ReadApi()
    {
        string? generated = TryFind("sources/VintagestoryApi/Client/optimum-render-device.cs");
        return File.ReadAllText(generated ?? PatchReader.FindRepositoryFile("VintagestoryApi/Client/optimum-render-device.cs"));
    }

    private static int Count(string text, string needle)
    {
        int count = 0;
        for (int index = text.IndexOf(needle, StringComparison.Ordinal); index >= 0;
             index = text.IndexOf(needle, index + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }

    private static string ReadSourceOrPatched(string patchPath, string sourcePath)
    {
        // Whole method bodies are parsed here, so the materialised source (the
        // tree extract-patches.sh generates the patch from) comes first; a patch
        // only carries its hunks, which cut SetupDefaultFrameBuffers short.
        string? resolvedSource = TryFind(sourcePath);
        if (resolvedSource != null) return File.ReadAllText(resolvedSource);
        return PatchReader.ReadPatchedContent(PatchReader.FindRepositoryFile(patchPath));
    }

    private static string Read(string relativePath) => File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

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
