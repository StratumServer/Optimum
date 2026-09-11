using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// TAA P5 acceptance + performance harness. Mechanical coverage only: the capture
/// script and the acceptance document exist, the document names every row of the
/// P5 acceptance matrix as TAA-PLAN.md words it, and the client's per-second
/// frame-time log is wired and shipped by the Cecil patcher.
/// </summary>
public class TaaAcceptanceHarnessCoverageTests
{
    /// <summary>
    /// The P5 acceptance matrix, verbatim from TAA-PLAN.md. Both the plan and
    /// docs/taa-acceptance.md must name every one of these.
    /// </summary>
    private static readonly string[] MatrixRows =
    {
        "moving silhouettes on contrast",
        "transparent foreground and background motion",
        "thin fences",
        "hand/world FOV",
        "quern/gear",
        "dropped items",
        "fire",
        "rain",
        "clouds",
        "aurora",
        "underwater transitions",
        "camera modes (shake, third person, mounted)",
        "reference rebase",
        "chunk replacement",
        "shader reload",
        "missing-resource fallback",
        "normal/scaled/mega screenshots",
    };

    [Fact]
    public void AcceptanceDocumentNamesEveryMatrixRowThePlanLists()
    {
        string plan = Collapse(Read("TAA-PLAN.md"));
        string doc = Collapse(Read("docs/taa-acceptance.md"));

        foreach (string row in MatrixRows)
        {
            Assert.Contains(row, plan, StringComparison.Ordinal);
            Assert.Contains(row, doc, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void EveryMatrixRowIsANumberedChecklistRowWithItsFourParts()
    {
        string doc = Read("docs/taa-acceptance.md");

        for (int i = 0; i < MatrixRows.Length; i++)
        {
            string heading = "### A" + (i + 1) + ". " + MatrixRows[i];
            Assert.Contains(heading, doc, StringComparison.Ordinal);

            // Each row carries the scene, the commands, the pass criterion and the
            // measurement to record - the four parts the task asks a row to have.
            string body = Section(doc, heading);
            Assert.Contains("- Scene:", body);
            Assert.Contains("- Commands:", body);
            Assert.Contains("- Pass:", body);
            Assert.Contains("- Record:", body);
        }

        // The two non-visual rows the plan also demands, plus the byte-identical check.
        Assert.Contains("### A18. TAA off is byte-identical", doc);
        Assert.Contains("### P1. Performance on the Arc 140V", doc);
        Assert.Contains("### P2. Memory at 1080p", doc);
    }

    [Fact]
    public void AcceptanceDocumentFixesTheMeasurementAndTheFrozenScene()
    {
        string doc = Read("docs/taa-acceptance.md");

        // Still-frame luminance diff, from the parity skill: centre 60% crop, seven
        // pairs, compare medians.
        Assert.Contains("centre 60% crop", doc);
        Assert.Contains("seven pairs", doc);
        Assert.Contains("medians", doc);
        Assert.Contains("scripts/dev/luma-diff.py", doc);

        // Wind stilled, storms off, creative - otherwise the scene moves on its own.
        Assert.Contains("/weather setw still", doc);
        Assert.Contains("/weather setprecip -1", doc);
        Assert.Contains("/gamemode creative", doc);

        // A launch is not a verification.
        Assert.Contains("scripts/dev/client-renderer.sh", doc);
        Assert.Contains("scripts/dev/perf-capture.sh", doc);
    }

    [Fact]
    public void PerfCaptureScriptDrivesTheDocumentedRunThroughTheDevScripts()
    {
        string script = Read("scripts/dev/perf-capture.sh");

        Assert.Contains("scripts/dev/run-client.sh", script);
        Assert.Contains("scripts/dev/kill-client.sh", script);
        Assert.Contains("RENDERER=\"$RENDERER_ARG\"", script);
        Assert.Contains("[Client Chat] Welcome", script);
        Assert.Contains("OPTIMUM_FPS_LOG", script);
        Assert.Contains("OPTIMUM_VULKAN_STATS", script);
        Assert.Contains("SECONDS_WINDOW=30", script);
        Assert.Contains("WARMUP=8", script);
        Assert.Contains("data[\"Taa\"] = value", script);
        Assert.Contains("mean frame time", script);
        Assert.Contains("1%% low frame time", script);

        // The renderer is read back from the log, never assumed from the argument.
        Assert.Contains("(Vulkan|OpenGL) renderer", script);
        Assert.Contains("refusing to report numbers", script);

        // Rule 5: pattern-killing belongs to kill-client.sh alone.
        Assert.DoesNotContain("pkill", script);
        Assert.DoesNotContain("pgrep", script);
    }

    [Fact]
    public void ClientLogsFrameTimesPerSecondOnlyWhenTheEnvVarIsSet()
    {
        string clientMain = ReadPatchedOrSource(
            "patches/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs.patch",
            "build/VintagestoryLib/Vintagestory.Client.NoObf/ClientMain.cs");

        Assert.Contains("OptimumLogFrameTime(dt);", clientMain);
        Assert.Contains("Environment.GetEnvironmentVariable(\"OPTIMUM_FPS_LOG\")", clientMain);
        Assert.Contains("\"[Optimum] fps window={0:F3} frames={1} mean={2:F3} min={3:F3} max={4:F3} p99={5:F3}\"", clientMain);
        // Off by default: no path, no work beyond the null check, so TAA off (and
        // every ordinary run) is unchanged.
        Assert.Contains("if (optimumFpsLogPath == null", clientMain);
        // One line per second, not per frame.
        Assert.Contains("if (optimumFpsLogSeconds < 1.0)", clientMain);
    }

    [Fact]
    public void CecilPatcherShipsTheFrameTimeLogMembers()
    {
        string patcher = Read("Optimum.Patcher/Program.cs");

        Assert.Contains("\"optimumFpsLogPath\"", patcher);
        Assert.Contains("\"optimumFpsLogResolved\"", patcher);
        Assert.Contains("\"optimumFpsLogSamples\"", patcher);
        Assert.Contains("\"optimumFpsLogFrames\"", patcher);
        Assert.Contains("\"optimumFpsLogSeconds\"", patcher);
        Assert.Contains("\"OptimumLogFrameTime\"", patcher);
        // The caller is an existing transplant target; without it the injected
        // method would never run.
        Assert.Contains("\"Vintagestory.Client.NoObf.ClientMain\", \"MainRenderLoop\", 1", patcher);
    }

    private static string Section(string document, string heading)
    {
        int start = document.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing heading: " + heading);
        int end = document.IndexOf("\n###", start + heading.Length, StringComparison.Ordinal);
        return end < 0 ? document.Substring(start) : document.Substring(start, end - start);
    }

    private static string Collapse(string value)
    {
        return Regex.Replace(value, "\\s+", " ");
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
