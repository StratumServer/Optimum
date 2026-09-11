using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The OPTIMUM_FPS_LOG line has one producer (ClientMain.OptimumLogFrameTime) and
/// three readers: perf-capture.sh, pacing-gate.sh and the acceptance document.
/// These tests render the producer's own format string and make every reader
/// agree with it, so a field added on one side cannot silently fall off another
/// (perf-capture.sh's Vulkan-stats regex had already drifted that way once).
/// </summary>
public class PacingLogFormatCoverageTests
{
    private static readonly string[] FpsKeys = { "window", "frames", "mean", "min", "max", "p99", "stddev" };

    private static string ClientMain() => ReadPatchedOrSource(
        "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch",
        "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");

    private static string FpsFormatString()
    {
        Match match = Regex.Match(ClientMain(), "\"(\\[Optimum\\] fps window=[^\"]*)\"");
        Assert.True(match.Success, "no [Optimum] fps format string in ClientMain");
        return match.Groups[1].Value;
    }

    private static string ScriptFpsRegex(string script)
    {
        Match match = Regex.Match(Read(script), "FPS_LINE_RE = re\\.compile\\(r\"(.*)\"\\)");
        Assert.True(match.Success, "no single-line FPS_LINE_RE in " + script);
        // Python spells a named group (?P<name>...); .NET spells it (?<name>...).
        return match.Groups[1].Value.Replace("(?P<", "(?<", StringComparison.Ordinal);
    }

    private static List<string> Keys(string text, string pattern)
    {
        var keys = new List<string>();
        foreach (Match match in Regex.Matches(text, pattern)) keys.Add(match.Groups[1].Value);
        return keys;
    }

    [Fact]
    public void FpsLineFormatAppendsStddevAfterP99()
    {
        string format = FpsFormatString();
        Assert.Equal(
            "[Optimum] fps window={0:F3} frames={1} mean={2:F3} min={3:F3} max={4:F3} p99={5:F3} stddev={6:F3}",
            format);
        Assert.Equal(FpsKeys, Keys(format, @"(\w+)=\{"));
    }

    [Fact]
    public void ClientStddevIsComputedOverTheSameWindowWithoutHelperTypes()
    {
        string clientMain = ClientMain();
        Assert.Contains("double stddev = Math.Sqrt(squares / (double)sampled);", clientMain);
        // Seven loose arguments would bind string.Format's params ReadOnlySpan
        // overload, which lowers into an InlineArray helper type the Cecil
        // transplant cannot carry.
        Assert.Contains("object[] fields = new object[7];", clientMain);
        Assert.Contains("fields[6] = stddev;", clientMain);

        string patcher = Read("Optimum.Patcher/Program.cs");
        Assert.Contains("\"OptimumLogFrameTime\"", patcher);
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"MainRenderLoop\", 1", patcher);
    }

    [Fact]
    public void BothScriptsParseTheRenderedLineAndOldLogs()
    {
        string format = FpsFormatString();
        string rendered = string.Format(CultureInfo.InvariantCulture, format,
            1.004, 120, 8.367, 7.912f, 11.204f, 10.811f, 0.612);
        string old = rendered.Substring(0, rendered.IndexOf(" stddev=", StringComparison.Ordinal));

        string capture = ScriptFpsRegex("scripts/dev/perf-capture.sh");
        string gate = ScriptFpsRegex("scripts/dev/pacing-gate.sh");
        Assert.Equal(capture, gate);
        Assert.Equal(FpsKeys, Keys(gate, @"\(\?<(\w+)>"));

        Match match = Regex.Match("2026-09-11 " + rendered, gate);
        Assert.True(match.Success, rendered);
        Assert.Equal("1.004", match.Groups["window"].Value);
        Assert.Equal("120", match.Groups["frames"].Value);
        Assert.Equal("10.811", match.Groups["p99"].Value);
        Assert.Equal("0.612", match.Groups["stddev"].Value);

        Match oldMatch = Regex.Match(old, gate);
        Assert.True(oldMatch.Success, old);
        Assert.False(oldMatch.Groups["stddev"].Success);
    }

    [Fact]
    public void AcceptanceDocumentShowsTheSameFieldsInTheSameOrder()
    {
        string doc = Read("docs/taa-acceptance.md");
        string? template = null;
        foreach (string line in doc.Split('\n'))
        {
            if (line.Contains("[Optimum] fps window=<", StringComparison.Ordinal))
            {
                template = line;
                break;
            }
        }
        Assert.NotNull(template);
        Assert.Equal(FpsKeys, Keys(template!, @"(\w+)=<"));

        Assert.Contains("scripts/dev/pacing-gate.sh", doc);
        foreach (string rule in new[]
                 { "blocking_uploads", "stddev_vs_baseline", "p99_vs_mean", "dropped_mesh_writes", "uniform_overflows" })
        {
            Assert.Contains("`" + rule + "`", doc);
        }
    }

    [Fact]
    public void PerfCapturePrintsStddevAndReadsTheRealStatsText()
    {
        string script = Read("scripts/dev/perf-capture.sh");
        Assert.Contains("frame stddev", script);
        Assert.Contains("stddev_ms", script);
        // VulkanStats writes "N frames (M ms/frame)"; there never was a frameMs token.
        Assert.Contains(@"ms/frame\)", script);
        Assert.DoesNotContain("frameMs[= ]", script);
        Assert.Contains("scripts/dev/pacing-gate.sh", script);
    }

    [Fact]
    public void PacingGateSelfTestPasses()
    {
        (int code, string output) = RunGate("--self-test");
        Assert.True(code == 0, output);
        Assert.Contains("cases passed", output);
        Assert.DoesNotContain("FAIL ", output);
    }

    [Fact]
    public void PacingGateAcceptsTheClientLineOnAnOpenGlRunWithoutStats()
    {
        string format = FpsFormatString();
        string dir = Directory.CreateTempSubdirectory("optimum-pacing-").FullName;
        try
        {
            string fps = Path.Combine(dir, "fps.log");
            string line = string.Format(CultureInfo.InvariantCulture, format,
                1.004, 120, 8.367, 7.912f, 11.204f, 10.811f, 0.612);
            File.WriteAllText(fps, line + "\n" + line + "\n" + line + "\n");

            (int code, string output) = RunGate("--renderer", "opengl", "--fps", fps, "--baseline", fps);
            Assert.True(code == 0, output);
            Assert.Contains("median stddev 0.612 ms", output);
            Assert.Matches(new Regex(@"stddev_vs_baseline\s+0\.612 ms.*PASS"), output);
            Assert.Matches(new Regex(@"blocking_uploads\s+-\s+no --stats\s+SKIP"), output);

            // A Vulkan run is not gateable without its stats file.
            (code, output) = RunGate("--renderer", "vulkan", "--fps", fps);
            Assert.True(code == 2, output);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PacingGateNeverPatternKills()
    {
        string script = Read("scripts/dev/pacing-gate.sh");
        Assert.DoesNotContain("pkill", script);
        Assert.DoesNotContain("pgrep", script);
    }

    private static (int Code, string Output) RunGate(params string[] arguments)
    {
        string script = PatchReader.FindRepositoryFile("scripts/dev/pacing-gate.sh");
        var start = new ProcessStartInfo("bash")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(script);
        foreach (string argument in arguments) start.ArgumentList.Add(argument);

        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout + stderr);
    }

    private static string ReadPatchedOrSource(string patchPath, string sourcePath)
    {
        string? resolvedPatch = TryFind(patchPath);
        return resolvedPatch != null ? PatchReader.ReadPatchedContent(resolvedPatch) : Read(sourcePath);
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));

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
