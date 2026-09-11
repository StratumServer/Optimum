using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// Source coverage for the 2026-09-11 distant-foliage fix in taa-resolve.fsh
/// (TAA-PLAN.md "Follow-up 2026-09-11: distant foliage jitter was the resolve").
///
/// Root cause: a single-sample disocclusion test rejected history on ~3.7% of
/// distant leaf pixels per frame on both backends (a sub-pixel leaf hits the leaf
/// in one jitter phase and the far background in the next), and a fixed current
/// weight let the clip box drag the history. The 3x3 nearest-depth test and the
/// anti-flicker weight took it to ~1.1%; the user confirmed the flicker gone on
/// Vulkan. These tests pin the shader text, the "do not revert" guard, the
/// documents that record the finding and the rejection-rate script. The numbers
/// are proven by Optimum.Render.Vulkan.Tests/TaaResolveTests.
/// </summary>
public class TaaAntiFlickerCoverageTests
{
    private static readonly string[] GpuTests =
    {
        "AntiFlickerWeightsFollowTheLuminanceDifference",
        "FlippingSubPixelLeafKeepsItsHistory",
        "DisocclusionLargerThanTheNeighbourhoodStillResets",
        "MotionComesFromTheNearestDepthTapAtAnEdge",
    };

    [Fact]
    public void TheCurrentWeightFollowsTheLuminanceDifferenceForKeptPixelsOnly()
    {
        string resolve = Read("sources/shaders/taa-resolve.fsh");

        // Every rejection path marks the pixel, and only unmarked pixels are reweighted.
        Assert.Contains("bool rejected = resetHistory != 0 || offscreen;", resolve);
        int nanReset = resolve.IndexOf("historyLinear = linearDepth;", StringComparison.Ordinal);
        Assert.True(nanReset >= 0);
        Assert.True(resolve.IndexOf("rejected = true;", nanReset, StringComparison.Ordinal)
                    < resolve.IndexOf("float historyNearest", StringComparison.Ordinal),
            "the NaN-history reset no longer marks the pixel as rejected");

        const string weight = "alpha = mix(blendAlpha * 1.2, blendAlpha * 0.3, unbiasedWeight * unbiasedWeight);";
        string[] ordered =
        {
            "vec3 histYcc = clipToBox(clipMin, clipMax, rgbToYCoCg(history.rgb), clipKeep);",
            "vec3 curYcc = rgbToYCoCg(current.rgb);",
            "if (!rejected)",
            "float lumCur = max(curYcc.x, 0.0);",
            "float lumHist = max(histYcc.x, 0.0);",
            "float unbiasedDiff = abs(lumCur - lumHist) / max(lumCur, max(lumHist, 0.2));",
            "float unbiasedWeight = 1.0 - unbiasedDiff;",
            weight,
            "alpha = max(alpha, reactive);",
            "float wCur = alpha / (1.0 + curYcc.x);",
        };
        AssertInOrder(resolve, ordered);

        // Reactive is applied once, after the weighting, never before it.
        Assert.Equal(1, Count(resolve, "alpha = max(alpha, reactive);"));
        Assert.Equal(1, Count(resolve, weight));
    }

    [Fact]
    public void TheDisocclusionTestComparesTheNearestDepthOnBothSides()
    {
        string resolve = Read("sources/shaders/taa-resolve.fsh");

        // Current side: the nearest window depth of the 3x3, its tap and its linear depth.
        AssertInOrder(resolve, new[]
        {
            "float closestDepth = 2.0;",
            "ivec2 closestPixel = pixel;",
            "for (int y = -1; y <= 1; y++)",
            "float tapDepth = texelFetch(depthTex, p, 0).r;",
            "if (tapDepth < closestDepth) { closestDepth = tapDepth; closestPixel = p; }",
            "vec4 current = filteredWeight > 1e-4 ? filtered / filteredWeight : centreSample;",
        });
        Assert.Contains("vec4 closestH = invViewProjJittered * vec4(closestNdc, closestDepth * 2.0 - 1.0, 1.0);", resolve);
        Assert.Contains("float closestLinearDepth = -(viewMatrix * vec4(closestWorld, 1.0)).z;", resolve);

        // History side: the nearest finite depth of the 3x3 around the reprojected point.
        AssertInOrder(resolve, new[]
        {
            "float historyNearest = historyLinear;",
            "for (int hx = -1; hx <= 1; hx++)",
            "float h = texture(historyDepth, historyUv + vec2(hx, hy) * invSize).r;",
            "if (!isnan(h) && !isinf(h)) historyNearest = min(historyNearest, h);",
            "float depthTolerance = 0.5 + 0.08 * closestLinearDepth;",
            "if (abs(historyNearest - closestLinearDepth) > depthTolerance) { alpha = 1.0; rejected = true; }",
        });

        // Not the single-sample test it replaced.
        Assert.DoesNotContain("float depthTolerance = 0.5 + 0.08 * linearDepth;", resolve);
        Assert.DoesNotContain("abs(historyLinear - linearDepth)", resolve);

        // The motion vector comes from the same tap; the lookup anchor, reactive and the
        // stored history depth stay this pixel's own (contract v1 unchanged).
        Assert.Contains("vec4 motion = texelFetch(motionTex, closestPixel, 0);", resolve);
        Assert.Contains("vec2 currentUnjittered = closestCentre - jitterPx;", resolve);
        Assert.Contains("prevViewProj * vec4(closestWorld + cameraDelta, 1.0)", resolve);
        Assert.Contains("vec2 historyUv = (pixelCentre + mv) * invSize;", resolve);
        Assert.Contains("float reactive = clamp(texelFetch(motionTex, pixel, 0).b, 0.0, 1.0);", resolve);
        Assert.Equal(2, Count(resolve, "outDepth = vec4(linearDepth);"));
        Assert.DoesNotContain("outDepth = vec4(closestLinearDepth)", resolve);
    }

    [Fact]
    public void TheShaderSaysNeverToRevertAndNamesTheTestsThatExist()
    {
        string resolve = Read("sources/shaders/taa-resolve.fsh");
        string gpu = Read("Optimum.Render.Vulkan.Tests/TaaResolveTests.cs");

        Assert.Contains("2026-09-11: distant foliage jitter was THIS test", resolve);
        Assert.Contains("DO NOT REVERT to a single-sample depth test.", resolve);
        Assert.Contains("DO NOT REVERT to a fixed blend weight.", resolve);
        Assert.Contains("~3.7%", resolve);
        Assert.Contains("~1.1%", resolve);
        Assert.Contains("scripts/dev/taa-rejection.py", resolve);
        Assert.Contains("TaaAntiFlickerCoverageTests", resolve);

        foreach (string test in GpuTests)
        {
            Assert.Contains("TaaResolveTests." + test, resolve);
            Assert.Contains("public void " + test + "()", gpu);
        }
    }

    [Fact]
    public void TheFindingIsRecordedInThePlanTheContractAndBothAcceptanceDocuments()
    {
        string contract = Read("docs/temporal-frame-contract.md");
        string section4 = Between(contract, "## 4. The resolve's own inputs", "## 5. Reset");
        Assert.Contains("Note (2026-09-11): anti-flicker weighting and nearest-depth disocclusion.", section4);
        Assert.Contains("the contract stays **v1**", section4);
        Assert.Contains("~3.7%", section4);
        Assert.Contains("~1.1%", section4);
        Assert.Contains("**Never revert**", section4);
        Assert.Contains("`alpha = mix(blendAlpha * 1.2, blendAlpha * 0.3, w * w)`", section4);
        Assert.Contains("`0.5 + 0.08 * closestLinearDepth`", section4);
        Assert.Contains("scripts/dev/taa-rejection.py", section4);
        foreach (string test in GpuTests)
        {
            Assert.Contains(test, section4);
        }

        string plan = Read("TAA-PLAN.md");
        string followUp = Between(plan, "## Follow-up 2026-09-11: distant foliage jitter was the resolve", "## Follow-up (not part of this plan)");
        foreach (string needle in new[] { "3.7%", "1.1%", "scripts/dev/taa-rejection.py", "Do not revert" })
        {
            Assert.Contains(needle, followUp);
        }
        foreach (string test in GpuTests)
        {
            Assert.Contains(test, followUp);
        }

        string taaAcceptance = Read("docs/taa-acceptance.md");
        string row = Between(taaAcceptance, "### A19. distant foliage stability", "## 3. Performance");
        Assert.Contains("- Scene:", row);
        Assert.Contains("- Commands:", row);
        Assert.Contains("- Pass:", row);
        Assert.Contains("- Record:", row);
        Assert.Contains("scripts/dev/parity-capture.sh", row);
        Assert.Contains("python3 scripts/dev/taa-rejection.py", row);
        Assert.Contains("<= 1.5 percent", row);
        Assert.Contains("default two frames in flight", row);

        string vulkanAcceptance = Read("docs/vulkan-acceptance.md");
        string m17 = Between(vulkanAcceptance, "#### M1.7 TAA still-frame stability", "#### M1.8");
        Assert.Contains("python3 scripts/dev/taa-rejection.py", m17);
        Assert.Contains("**required**", m17);
    }

    [Fact]
    public void TaaRejectionSelfTestPasses()
    {
        string script = PatchReader.FindRepositoryFile("scripts/dev/taa-rejection.py");
        string root = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(script)))!;
        var start = new ProcessStartInfo("python3")
        {
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("scripts/dev/taa-rejection.py");
        start.ArgumentList.Add("--self-test");
        using Process process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(180_000), "taa-rejection.py --self-test did not finish");
        Assert.True(process.ExitCode == 0, "taa-rejection.py --self-test exited " + process.ExitCode + ": " + stdout + stderr);
        Assert.Contains("taa-rejection.py self-test: ok", stdout);
    }

    // ------------------------------------------------------------------ helpers

    private static void AssertInOrder(string source, string[] needles)
    {
        int last = -1;
        foreach (string needle in needles)
        {
            int index = source.IndexOf(needle, last + 1, StringComparison.Ordinal);
            Assert.True(index > last, "missing or out of order in taa-resolve.fsh: " + needle);
            last = index;
        }
    }

    private static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "missing: " + start);
        int to = source.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to > from, "missing after '" + start + "': " + end);
        return source.Substring(from, to - from);
    }

    private static int Count(string source, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = source.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }
        return count;
    }

    private static string Read(string relativePath) =>
        File.ReadAllText(PatchReader.FindRepositoryFile(relativePath));
}
