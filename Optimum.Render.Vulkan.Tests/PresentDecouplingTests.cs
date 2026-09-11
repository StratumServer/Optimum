using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Phase 1B step 4: the CPU blocks on the acquire only after the frame is in
/// flight. An injected acquire delay (a compositor holding images back) must not
/// lengthen frame recording, the frame must reach the queue before the acquire
/// starts, and the GPU must be able to finish the frame while the CPU still
/// waits for the image.
/// </summary>
public class PresentDecouplingTests
{
    private const int Width = 256;
    private const int Height = 192;
    private const int Frames = 24;
    private const int Warmup = 4;
    private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(60);

    private readonly ITestOutputHelper _output;

    public PresentDecouplingTests(ITestOutputHelper output) => _output = output;

    private sealed class Measurements
    {
        public readonly List<double> RecordingMs = new();
        public readonly List<double> SubmitAfterPresentEntryMs = new();
        public readonly List<double> AcquireAfterSubmitMs = new();
        public int RenderCompletedAtAcquire;
        public int Presented;
        public int Samples;
        public long DeviceIdleWaits;
        public PipelineStageFlags AcquireStage;
    }

    [SkippableFact]
    public unsafe void RecordingTimeDoesNotGrowWithTheInjectedAcquireDelay()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window), "No usable window system.");

        try
        {
            Measurements baseline = Run((IntPtr)window, TimeSpan.Zero);
            Measurements delayed = Run((IntPtr)window, Delay);

            double baselineRecording = Median(baseline.RecordingMs);
            double delayedRecording = Median(delayed.RecordingMs);
            _output.WriteLine($"recording median: no delay {baselineRecording:F2} ms, {Delay.TotalMilliseconds} ms delay {delayedRecording:F2} ms");
            _output.WriteLine($"acquire after frame submit median: no delay {Median(baseline.AcquireAfterSubmitMs):F2} ms, delayed {Median(delayed.AcquireAfterSubmitMs):F2} ms");
            _output.WriteLine($"frame finished on the GPU before the acquire returned: {delayed.RenderCompletedAtAcquire}/{delayed.Samples}");

            Assert.True(delayedRecording < baselineRecording + Delay.TotalMilliseconds / 4,
                $"recording grew with the acquire delay: {baselineRecording:F2} -> {delayedRecording:F2} ms");

            foreach (Measurements run in new[] { baseline, delayed })
            {
                Assert.Equal(run.Samples, run.Presented);
                Assert.Equal(0, run.DeviceIdleWaits);
                Assert.Equal(PipelineStageFlags.TransferBit, run.AcquireStage);
                Assert.NotEqual(PipelineStageFlags.AllCommandsBit, run.AcquireStage);
            }

            // The frame is submitted before the acquire starts, so the delay lands
            // between the two, never before the frame submission.
            foreach (double ms in delayed.SubmitAfterPresentEntryMs)
            {
                Assert.True(ms < Delay.TotalMilliseconds / 3, $"the frame reached the queue {ms:F2} ms into Present");
            }
            foreach (double ms in delayed.AcquireAfterSubmitMs)
            {
                Assert.True(ms >= Delay.TotalMilliseconds * 0.9, $"the acquire returned {ms:F2} ms after the frame submit");
            }

            // With the frame already queued, a 256x192 frame finishes well inside
            // the delay. Two stragglers are tolerated for a busy machine.
            Assert.True(delayed.RenderCompletedAtAcquire >= delayed.Samples - 2,
                $"the GPU finished the frame during the acquire in only {delayed.RenderCompletedAtAcquire} of {delayed.Samples} frames");
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    private Measurements Run(IntPtr window, TimeSpan delay)
    {
        VulkanDevice device = GpuTest.NewDevice();
        Action<VulkanContextOptions>? suite = device.ConfigureContextOptions;
        device.ConfigureContextOptions = options =>
        {
            suite?.Invoke(options);
            options.AcquireDelayForTests = delay;
        };

        if (!device.Initialize(window, Width, Height, out string failureReason))
        {
            device.Dispose();
            Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
        }

        var result = new Measurements();
        using (device)
        {
            IOptimumGraphicsDevice seam = device;
            int programId = SwapchainTests.LinkFullscreenProgram(seam);
            result.AcquireStage = device.PresentAcquireWaitStageForTests;
            long idleBefore = VulkanStats.WaitCount(WaitSite.DeviceWaitIdle);

            for (int frame = 0; frame < Frames; frame++)
            {
                // Recording: from the start of BeginFrame (its pacing wait included)
                // to the moment Present is called.
                long frameStart = Stopwatch.GetTimestamp();
                seam.BeginFrame();
                seam.BindDefaultFramebuffer();
                seam.ClearColor(0, 0.1f, 0.2f, 0.3f, 1f);
                seam.UseProgram(programId);
                seam.SetViewport(0, 0, Width, Height);
                seam.SetDepthTest(false);
                seam.SetCullFace(false);
                seam.DrawFullscreenTriangle();
                long recorded = Stopwatch.GetTimestamp();
                seam.Present();

                if (frame < Warmup) continue;
                VulkanDevice.PresentTimings timings = device.LastPresentTimingsForTests;
                result.Samples++;
                result.RecordingMs.Add(Ms(recorded - frameStart));
                result.SubmitAfterPresentEntryMs.Add(Ms(timings.FrameSubmitted - timings.PresentEntry));
                result.AcquireAfterSubmitMs.Add(Ms(timings.AcquireReturned - timings.FrameSubmitted));
                if (timings.RenderCompletedAtAcquire) result.RenderCompletedAtAcquire++;
                if (timings.Presented)
                {
                    result.Presented++;
                    Assert.True(timings.PresentValue > timings.RenderValue,
                        "the present submission must carry a newer Frame value than the frame it waits on");
                }
            }

            result.DeviceIdleWaits = VulkanStats.WaitCount(WaitSite.DeviceWaitIdle) - idleBefore;
            GpuTest.AssertClean(seam);
        }
        return result;
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static double Median(List<double> values)
    {
        var sorted = new List<double>(values);
        sorted.Sort();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }
}
