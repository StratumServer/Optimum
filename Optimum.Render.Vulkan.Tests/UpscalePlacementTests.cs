using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// DLSS plan, Phase 3: the two things the placement adds on the GPU, neither of which the
/// host-level tests cover.
///
/// <list type="number">
/// <item><b>The target the client really allocates.</b> Slot 22 is
/// <c>R8G8B8A8_UNORM</c> with storage usage - Optimum's scene colour is 8-bit LDR and
/// already perceptually encoded, which is why the feature runs with <c>IsHDR</c> off - not
/// the RGBA16F the earlier evaluate tests wrote into. A format NGX refuses, or one it
/// writes differently, would only show up here.</item>
/// <item><b>The overlays' depth.</b> The AfterFinalComposition stage draws depth-tested 3D
/// world content into the composited image, so it needs a display-resolution depth buffer;
/// it gets one nearest-neighbour upscale of the render-resolution depth per frame. What
/// that costs is a number, and it is measured here rather than asserted in prose.</item>
/// </list>
///
/// Skips, never fails, without the shim, the driver library or the NGX feature libraries
/// (<c>OPTIMUM_NGX_FEATURE_PATH</c>).
/// </summary>
[Collection(NgxCollection.Name)]
public class UpscalePlacementTests
{
    private readonly ITestOutputHelper _output;
    private readonly NgxRuntime _ngx;

    public UpscalePlacementTests(ITestOutputHelper output, NgxRuntime ngx)
    {
        _output = output;
        _ngx = ngx;
    }

    private const int DisplayWidth = 2560;
    private const int DisplayHeight = 1490;

    /// <summary>The pattern's grid, as in the other DLSS tests: 4x4 alternating blocks.</summary>
    private const int Blocks = 4;

    /// <summary>
    /// The real frame's target format, through the real frame's shape: create on the first
    /// frame, evaluate the constant pattern without raster jitter, Present between frames, no readback in the
    /// loop, one readback at the end.
    ///
    /// Asserted: every evaluate on an <c>R8G8B8A8_UNORM</c> storage output succeeds, the
    /// result is display-resolution, the history converged onto the pattern rather than
    /// smearing it, and the layers stay clean with synchronization validation and best
    /// practices on.
    /// </summary>
    [SkippableFact]
    public void TheShippedOutputFormatUpscalesWithTheHistoryAccumulating()
    {
        DlssUpscaler host = Host(out VulkanDevice seam, out int mark);

        Assert.True(host.TryPlan(DisplayWidth, DisplayHeight, "quality", out UpscalePlan plan));
        Log("plan: " + plan);
        Assert.Equal(DisplayWidth, plan.DisplayWidth);
        Assert.True(plan.RenderWidth < plan.DisplayWidth, "the quality preset must really upscale");

        int color = CreateColor(seam, plan);
        int depth = CreateConstantDepth(seam, plan, 0.5f);
        int motion = CreateMotion(seam, plan);
        // Exactly what SetupDefaultFrameBuffers allocates for slot 22.
        int output = seam.CreateUpscaleTexture(
            plan.DisplayWidth, plan.DisplayHeight, Format.R8G8B8A8Unorm, storage: true);

        // One warm-up frame before the run is judged, for the same reason NgxRuntime warms
        // the process up: the first evaluate of a feature whose internal images NGX has
        // just allocated carries NGX's own vkCmdClearColorImage against NGX's own barrier
        // (pinned as a vendor entry in KnownSyncHazards). It is not this test's subject,
        // and absorbing it here keeps the eight frames below a genuinely clean run.
        seam.BeginFrame();
        Assert.True(host.EnsureFeature(plan));
        Assert.Equal(NgxResult.Success, host.Evaluate(color, depth, motion, output,
            new NgxDlssEvaluation { Reset = true, MotionVectorScaleX = 1f, MotionVectorScaleY = 1f }));
        seam.Present();
        mark = _ngx.MessageMark();

        const int frames = 8;
        byte[]? pixels = null;
        for (int index = 0; index < frames; index++)
        {
            seam.BeginFrame();
            Assert.True(host.EnsureFeature(plan), "the feature was refused at frame " + index);

            // This uploaded pattern is not raster-jittered. Report zero jitter;
            // DlssJitterConventionTests covers actual jittered inputs and registration.
            var frame = new NgxDlssEvaluation
            {
                Reset = index == 0,
                MotionVectorScaleX = 1f,
                MotionVectorScaleY = 1f,
            };
            Assert.Equal(NgxResult.Success, host.Evaluate(color, depth, motion, output, frame));

            if (index == frames - 1) pixels = seam.ReadBackLevel0ForTests(output);
            seam.Present();
        }

        Assert.Equal(1, host.FeaturesCreated);
        Assert.NotNull(pixels);
        Assert.Equal(plan.DisplayWidth * plan.DisplayHeight * 4, pixels!.Length);

        BlockReport report = SampleBlocks(pixels, plan.DisplayWidth, plan.DisplayHeight);
        Log("after " + frames + " frames at " + plan.JitterPhaseCount + " jitter phases: " + report);
        Assert.True(report.MinBright > 0.75f, "dimmest bright block " + report.MinBright);
        Assert.True(report.MaxDark < 0.25f, "brightest dark block " + report.MaxDark);

        host.RetireFeature();
        seam.DrainDeferredDeletions();
        host.Dispose();
        GpuTest.AssertCleanSince(seam, mark);
    }

    /// <summary>
    /// The overlays' depth, measured. A render-resolution depth buffer holding a near
    /// object (0.2) on a far background (0.8) is point-upscaled to the display size, read
    /// back, and three things are checked:
    ///
    /// <list type="bullet">
    /// <item>nothing was interpolated - every display texel is one of the two source
    /// values, which is what NEAREST buys and what a filtered upscale would break;</item>
    /// <item>every display texel is the source texel the nearest-neighbour mapping names,
    /// so a depth test at the display size decides exactly as the render-resolution one
    /// does away from an edge;</item>
    /// <item>at an edge it can differ, and by how much: the silhouette in the upscaled
    /// buffer never moves more than one render pixel from where the render-resolution
    /// buffer puts it. That bound is the documented cost of this placement.</item>
    /// </list>
    /// </summary>
    [SkippableFact]
    public void TheOverlayDepthIsAPointUpscaleAndItsEdgeErrorIsOneRenderPixel()
    {
        DlssUpscaler host = Host(out VulkanDevice seam, out int mark);
        Assert.True(host.TryPlan(DisplayWidth, DisplayHeight, "quality", out UpscalePlan plan));

        const float near = 0.2f;
        const float far = 0.8f;
        int renderWidth = plan.RenderWidth, renderHeight = plan.RenderHeight;
        var source = new float[renderWidth * renderHeight];
        for (int y = 0; y < renderHeight; y++)
        {
            for (int x = 0; x < renderWidth; x++)
            {
                // A near quad over the middle third, so there are silhouettes on all sides.
                bool inside = x >= renderWidth / 3 && x < renderWidth * 2 / 3
                    && y >= renderHeight / 3 && y < renderHeight * 2 / 3;
                source[y * renderWidth + x] = inside ? near : far;
            }
        }

        int renderDepth = Upload(seam, renderWidth, renderHeight, Format.D32Sfloat,
            MemoryMarshal.AsBytes<float>(source).ToArray(), 4);
        int displayDepth = seam.CreateUpscaleTexture(
            plan.DisplayWidth, plan.DisplayHeight, Format.D32Sfloat, storage: false);

        seam.BeginFrame();
        Assert.True(seam.UpscaleDepthNearest(renderDepth, displayDepth),
            "this device refused a depth blit, so the overlay depth cannot be produced at all");
        byte[] raw = seam.ReadBackLevel0ForTests(displayDepth);
        seam.Present();

        float[] upscaled = MemoryMarshal.Cast<byte, float>(raw).ToArray();
        Assert.Equal(plan.DisplayWidth * plan.DisplayHeight, upscaled.Length);

        int interpolated = 0;
        int mismatched = 0;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        for (int y = 0; y < plan.DisplayHeight; y++)
        {
            int sourceY = Math.Min(renderHeight - 1, y * renderHeight / plan.DisplayHeight);
            for (int x = 0; x < plan.DisplayWidth; x++)
            {
                float value = upscaled[y * plan.DisplayWidth + x];
                if (value != near && value != far) interpolated++;
                if (value == near)
                {
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }

                int sourceX = Math.Min(renderWidth - 1, x * renderWidth / plan.DisplayWidth);
                if (value != source[sourceY * renderWidth + sourceX]) mismatched++;
            }
        }

        double horizontalScale = (double)plan.DisplayWidth / renderWidth;
        double verticalScale = (double)plan.DisplayHeight / renderHeight;
        double mismatchedFraction = (double)mismatched / upscaled.Length;
        Log("point upscale " + renderWidth + "x" + renderHeight + " -> " +
            plan.DisplayWidth + "x" + plan.DisplayHeight +
            ": interpolated texels " + interpolated +
            ", texels off the floor-mapped source texel " + mismatched +
            " (" + (mismatchedFraction * 100).ToString("0.###") + " %)");
        Log("silhouette in render pixels: x " + (minX / horizontalScale).ToString("0.##") + ".." +
            (maxX / horizontalScale).ToString("0.##") + ", y " + (minY / verticalScale).ToString("0.##") + ".." +
            (maxY / verticalScale).ToString("0.##") + "; the source quad is x " + (renderWidth / 3) + ".." +
            (renderWidth * 2 / 3 - 1) + ", y " + (renderHeight / 3) + ".." + (renderHeight * 2 / 3 - 1));

        // NEAREST: no value that was not in the source.
        Assert.Equal(0, interpolated);

        // Where the silhouette landed, in render pixels: the point upscale may pick the
        // neighbouring source texel where a display pixel centre falls on the boundary,
        // and that - one render pixel - is the whole of the edge error. Anything larger
        // would mean a shifted or resampled buffer, which is what this pins.
        Assert.InRange(minX / horizontalScale, renderWidth / 3 - 1.0, renderWidth / 3 + 1.0);
        Assert.InRange(maxX / horizontalScale, renderWidth * 2 / 3 - 2.0, renderWidth * 2 / 3 + 1.0);
        Assert.InRange(minY / verticalScale, renderHeight / 3 - 1.0, renderHeight / 3 + 1.0);
        Assert.InRange(maxY / verticalScale, renderHeight * 2 / 3 - 2.0, renderHeight * 2 / 3 + 1.0);

        // And how much of the image that edge is: only the silhouette itself, which is a
        // border one render pixel wide around a quad covering a ninth of the image.
        Assert.True(mismatchedFraction < 0.01,
            "too much of the buffer disagrees with the nearest source texel: " + mismatchedFraction);

        // What the overlays see: the near quad covers the same fraction of the image it
        // covers at the render resolution, to within that same edge.
        double renderCoverage = Coverage(source, near);
        double displayCoverage = Coverage(upscaled, near);
        Log("near-quad coverage: render " + renderCoverage.ToString("0.#####") +
            ", display " + displayCoverage.ToString("0.#####"));
        Assert.True(Math.Abs(renderCoverage - displayCoverage) < 0.005,
            "the depth-tested overlay would see a different world: " + renderCoverage + " vs " + displayCoverage);

        host.Dispose();
        GpuTest.AssertCleanSince(seam, mark);
    }

    // ---------------------------------------------------------------- inputs

    private static bool Bright(int x, int y, int width, int height) =>
        (x * Blocks / width + y * Blocks / height) % 2 == 0;

    private static int CreateColor(VulkanDevice seam, in UpscalePlan plan)
    {
        int width = plan.RenderWidth, height = plan.RenderHeight;
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = Bright(x, y, width, height) ? (byte)255 : (byte)0;
                int offset = (y * width + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = 255;
            }
        }
        return Upload(seam, width, height, Format.R8G8B8A8Unorm, pixels, 4);
    }

    private static int CreateConstantDepth(VulkanDevice seam, in UpscalePlan plan, float value)
    {
        var depth = new float[plan.RenderWidth * plan.RenderHeight];
        Array.Fill(depth, value);
        return Upload(seam, plan.RenderWidth, plan.RenderHeight, Format.R32Sfloat,
            MemoryMarshal.AsBytes<float>(depth).ToArray(), 4);
    }

    private static int CreateMotion(VulkanDevice seam, in UpscalePlan plan) => Upload(
        seam, plan.RenderWidth, plan.RenderHeight, Format.R16G16Sfloat,
        new byte[plan.RenderWidth * plan.RenderHeight * 4], 4);

    private static unsafe int Upload(
        VulkanDevice seam, int width, int height, Format format, byte[] pixels, int bytesPerPixel)
    {
        fixed (byte* data = pixels)
        {
            return seam.CreateUpscaleTexture(width, height, format, false, (IntPtr)data, bytesPerPixel);
        }
    }

    // -------------------------------------------------------------- measures

    private static double Coverage(float[] depth, float value)
    {
        int hits = 0;
        for (int i = 0; i < depth.Length; i++)
        {
            if (depth[i] == value) hits++;
        }
        return (double)hits / depth.Length;
    }

    private readonly record struct BlockReport(float MinBright, float MaxDark)
    {
        public override string ToString() =>
            "dimmest bright block " + MinBright.ToString("0.####") +
            ", brightest dark block " + MaxDark.ToString("0.####");
    }

    /// <summary>The mean of a 64x64 patch at the centre of each of the 16 blocks, RGBA8.</summary>
    private static BlockReport SampleBlocks(byte[] pixels, int width, int height)
    {
        const int patch = 64;
        float minBright = float.MaxValue, maxDark = float.MinValue;
        for (int by = 0; by < Blocks; by++)
        {
            for (int bx = 0; bx < Blocks; bx++)
            {
                int centreX = (int)((bx + 0.5) * width / Blocks);
                int centreY = (int)((by + 0.5) * height / Blocks);
                double sum = 0;
                int count = 0;
                for (int y = centreY - patch / 2; y < centreY + patch / 2; y++)
                {
                    for (int x = centreX - patch / 2; x < centreX + patch / 2; x++)
                    {
                        if ((uint)x >= width || (uint)y >= height) continue;
                        sum += pixels[(y * width + x) * 4 + 1] / 255.0;
                        count++;
                    }
                }
                float mean = count == 0 ? 0 : (float)(sum / count);
                if (Bright(centreX, centreY, width, height)) minBright = Math.Min(minBright, mean);
                else maxDark = Math.Max(maxDark, mean);
            }
        }
        return new BlockReport(minBright, maxDark);
    }

    // --------------------------------------------------------------- harness

    /// <summary>
    /// A host on the process's one NGX session, or a skip. The session's lifetime is the
    /// runtime's; this host only plans, creates features and evaluates, exactly as the
    /// client's does.
    /// </summary>
    private DlssUpscaler Host(out VulkanDevice seam, out int mark)
    {
        _ngx.Require();
        foreach (string line in _ngx.Diagnostics) Log(line);
        mark = _ngx.MessageMark();
        seam = _ngx.Device;

        var host = new DlssUpscaler(Log);
        Assert.True(host.AdoptSession(_ngx.Session, seam, _ngx.VkDevice),
            "the host could not adopt the test runtime's NGX session");
        return host;
    }

    private void Log(string line) => _output.WriteLine(line);
}
