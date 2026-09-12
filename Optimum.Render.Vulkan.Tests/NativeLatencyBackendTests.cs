using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Core;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The Native ("completion pacing") latency backend on a real device and a real
/// hidden window, driven the way the frame drives it (the call map on
/// <see cref="ILatencyBackend" />): the sleep in front of the input sample, the
/// markers, and the device's own Present, which stamps PresentStart/PresentEnd
/// and calls OnPresent.
///
/// The four claims of the plan, each checked as a number and not as a feeling:
/// <list type="bullet">
/// <item><description>the sleep returns only after the previous frame's present
/// submission has completed - asserted against the Frame timeline's counter, not
/// against a wall clock;</description></item>
/// <item><description>with a frame cap the release-to-release interval matches
/// the cap within the tolerance documented on
/// <see cref="CapToleranceMs" />;</description></item>
/// <item><description>with the mode Off the sleep is a no-op: no timeline wait,
/// no hold, zero returned;</description></item>
/// <item><description>the ring's own pacing wait
/// (<see cref="WaitSite.FramePacing" />) drops to near zero while the backend is
/// active, which is the plan's acceptance number for this tier.</description></item>
/// </list>
/// </summary>
public class NativeLatencyBackendTests
{
    private const int Width = 256;
    private const int Height = 192;

    /// <summary>Enough frames for the plan's "about 30 frames" and still under two seconds.</summary>
    private const int Frames = 30;

    /// <summary>The first frames have no previous present to wait for and pay first-use costs.</summary>
    private const int Warmup = 5;

    /// <summary>
    /// 25 fps. The cap has to be slower than whatever the presentation engine
    /// does on its own, or it is not the thing being measured: FIFO on a 60 Hz
    /// display releases every 16.7 ms, so a cap below that would be met by the
    /// swapchain rather than by the backend, and one just above it would land on
    /// the next vsync instead of on the cap.
    /// </summary>
    private const ulong CapUs = 40_000;

    /// <summary>
    /// How far a capped release-to-release interval may sit from the cap, in
    /// milliseconds, measured as the median over the samples.
    ///
    /// The hold sleeps to within <see cref="NativeLatencyBackend.SpinTailUs" />
    /// (1 ms) of the target and spins the rest, so the backend's own error is
    /// under a millisecond; the rest of the budget is the frame itself - the
    /// acquire, the present call and one scheduler tick on a machine that is also
    /// running the test host. Under is not allowed beyond a fraction of that: a
    /// cap that releases early is a cap that does not work.
    /// </summary>
    private const double CapToleranceMs = 6.0;

    /// <summary>
    /// What "near zero" means for the ring's pacing wait, in milliseconds per
    /// frame. With the sleep pacing the frame, <c>FrameRing.BeginFrame</c> finds
    /// its value already signalled; the residue is the vkWaitSemaphores call
    /// itself on an already-signalled semaphore.
    /// </summary>
    private const double FramePacingNearZeroMs = 0.5;

    private readonly ITestOutputHelper _output;

    public NativeLatencyBackendTests(ITestOutputHelper output) => _output = output;

    private sealed class Run
    {
        public readonly List<double> ReleaseIntervalsMs = new();
        public int Frames;
        public int SleepsThatWaited;
        public int SleepsThatReturnedZero;
        public double FramePacingMsPerFrame;
        public double FramePacingMs;
        public long FramePacingWaits;
        public double LatencySleepMs;
        public long LatencySleepWaits;
        public ulong LastCompleted;
        public ulong LastWaited;
    }

    [SkippableFact]
    public unsafe void TheSleepWaitsForThePreviousPresentPacesToTheCapAndIsOffWhenOff()
    {
        Skip.IfNot(SwapchainTests.TryCreateWindow(_output, Width, Height, out Window* window),
            "No usable window system.");

        try
        {
            VulkanDevice device = GpuTest.NewDevice();
            Action<VulkanContextOptions>? suite = device.ConfigureContextOptions;
            device.ConfigureContextOptions = options =>
            {
                suite?.Invoke(options);
                // The one wiring line under test: a forced selection of Native has
                // to reach VulkanDevice.CreateLatencyBackend and come back as this
                // backend, bound to the device's own Frame timeline.
                options.LatencyBackend = LatencyBackendKind.Native;
            };

            if (!device.Initialize((IntPtr)window, Width, Height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
            }

            using (device)
            {
                VulkanDevice seam = device;
                Skip.If(device.Latency.Kind != LatencyBackendKind.Native,
                    "the device did not select Native: " + LatencyBackends.Token(device.Latency.Kind));
                var backend = (NativeLatencyBackend)device.Latency;

                // FIFO would pace the frame on its own and hide both the wait and
                // the cap. A driver without IMMEDIATE/MAILBOX stays on FIFO, which
                // is why the cap is slower than any plausible refresh rate.
                device.SetVSync(false);
                int programId = SwapchainTests.LinkFullscreenProgram(seam);

                // 1. Active, uncapped: the completion wait on its own.
                backend.Apply(new LatencySettings(LatencyMode.On, 0));
                Assert.True(backend.OwnsFrameCap, "an enabled Native backend must take the client's FPS cap over");
                Run uncapped = Drive(seam, backend, programId);

                // 2. Active with the cap: release to release.
                backend.Apply(new LatencySettings(LatencyMode.On, CapUs));
                Run capped = Drive(seam, backend, programId);

                // 3. Off: the sleep is a no-op again.
                backend.Apply(LatencySettings.Disabled);
                Assert.False(backend.OwnsFrameCap, "a backend whose mode is Off must leave the client's FPS cap alone");
                Run off = Drive(seam, backend, programId);

                Report("uncapped", uncapped);
                Report("capped", capped);
                Report("off", off);

                // --- the sleep waits for the previous present's completion -----
                // Drive() asserts it per frame against the timeline counter; here
                // only that it really happened on nearly every frame, so a backend
                // that silently stopped waiting cannot pass.
                Assert.True(uncapped.SleepsThatWaited >= uncapped.Frames - 1,
                    $"only {uncapped.SleepsThatWaited} of {uncapped.Frames} sleeps waited on the timeline");
                Assert.True(capped.SleepsThatWaited >= capped.Frames - 1,
                    $"only {capped.SleepsThatWaited} of {capped.Frames} sleeps waited on the timeline");

                // --- the cap -----------------------------------------------------
                double capMs = CapUs / 1000.0;
                double median = Median(capped.ReleaseIntervalsMs);
                Assert.True(Math.Abs(median - capMs) <= CapToleranceMs,
                    $"capped release-to-release median {median:F2} ms is not within {CapToleranceMs:F1} ms of the {capMs:F1} ms cap");
                // A cap that releases early is worse than no cap: the frames it
                // was meant to space out arrive in a burst.
                Assert.True(median >= capMs - 1.0,
                    $"the cap released early: {median:F2} ms against a {capMs:F1} ms cap");
                Assert.True(Median(uncapped.ReleaseIntervalsMs) < capMs - 1.0,
                    "the uncapped run was already slower than the cap, so the cap proves nothing here");

                // --- off is off --------------------------------------------------
                Assert.Equal(off.Frames, off.SleepsThatReturnedZero);
                Assert.Equal(0, off.SleepsThatWaited);
                Assert.Equal(0L, off.LatencySleepWaits);
                Assert.True(Median(off.ReleaseIntervalsMs) < capMs - 1.0,
                    "an Off backend must not pace the frame");

                // --- the acceptance number ---------------------------------------
                Assert.True(uncapped.FramePacingMsPerFrame < FramePacingNearZeroMs,
                    $"FramePacing was {uncapped.FramePacingMsPerFrame:F3} ms/frame with the backend active");
                Assert.True(capped.FramePacingMsPerFrame < FramePacingNearZeroMs,
                    $"FramePacing was {capped.FramePacingMsPerFrame:F3} ms/frame with the cap active");

                GpuTest.AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    /// <summary>
    /// One run of <see cref="Frames" /> frames, driven exactly as the seams drive
    /// it: BeginLatencyFrame and the sleep before input, the two markers this site
    /// owns, then the device's frame - whose Present stamps the rest and calls
    /// OnPresent.
    /// </summary>
    private Run Drive(VulkanDevice seam, NativeLatencyBackend backend, int programId)
    {
        var run = new Run();
        long pacingWaitsBefore = VulkanStats.WaitCount(WaitSite.FramePacing);
        double pacingMsBefore = VulkanStats.WaitMilliseconds(WaitSite.FramePacing);
        long sleepWaitsBefore = VulkanStats.WaitCount(WaitSite.LatencySleep);
        double sleepMsBefore = VulkanStats.WaitMilliseconds(WaitSite.LatencySleep);

        long previousRelease = 0;

        for (int frame = 0; frame < Frames; frame++)
        {
            ulong frameId = seam.BeginLatencyFrame();
            ulong slept = backend.Sleep(frameId);
            long release = Stopwatch.GetTimestamp();

            // The claim, checked against the timeline and nothing else: when the
            // sleep returns, the value it waited for is finished on the GPU.
            ulong waited = backend.LastWaitedValue;
            if (waited != 0)
            {
                ulong completed = backend.TimelineCompleted;
                Assert.True(completed >= waited,
                    $"frame {frameId}: the sleep returned with the Frame timeline at {completed}, " +
                    $"below the {waited} it waited for (frame {backend.LastWaitedFrameId}'s present submission)");
                run.LastCompleted = completed;
                run.LastWaited = waited;
            }

            backend.Marker(frameId, LatencyMarker.InputSample);
            backend.Marker(frameId, LatencyMarker.SimulationStart);

            seam.BeginFrame();
            seam.BindDefaultFramebuffer();
            seam.ClearColor(0, 0.1f, 0.2f, 0.3f, 1f);
            seam.UseProgram(programId);
            seam.SetViewport(0, 0, Width, Height);
            seam.SetDepthTest(false);
            seam.SetCullFace(false);
            seam.DrawFullscreenTriangle();
            // Present stamps RenderSubmitEnd, PresentStart/PresentEnd and calls
            // OnPresent, which is what arms the next frame's wait.
            seam.Present();

            if (frame >= Warmup)
            {
                run.Frames++;
                if (slept == 0) run.SleepsThatReturnedZero++;
                if (waited != 0) run.SleepsThatWaited++;
                if (previousRelease != 0) run.ReleaseIntervalsMs.Add(Ms(release - previousRelease));
            }
            previousRelease = release;
        }

        run.FramePacingWaits = VulkanStats.WaitCount(WaitSite.FramePacing) - pacingWaitsBefore;
        run.FramePacingMs = VulkanStats.WaitMilliseconds(WaitSite.FramePacing) - pacingMsBefore;
        run.FramePacingMsPerFrame = run.FramePacingWaits == 0 ? 0 : run.FramePacingMs / run.FramePacingWaits;
        run.LatencySleepWaits = VulkanStats.WaitCount(WaitSite.LatencySleep) - sleepWaitsBefore;
        run.LatencySleepMs = VulkanStats.WaitMilliseconds(WaitSite.LatencySleep) - sleepMsBefore;
        return run;
    }

    private void Report(string name, Run run)
    {
        _output.WriteLine(
            $"{name}: {run.Frames} sampled frames, release-to-release median {Median(run.ReleaseIntervalsMs):F2} ms, " +
            $"timeline waits {run.SleepsThatWaited}, sleeps returning 0 {run.SleepsThatReturnedZero}");
        _output.WriteLine(
            $"{name}: latency_sleep n={run.LatencySleepWaits} {run.LatencySleepMs:F1} ms, " +
            $"frame_pacing n={run.FramePacingWaits} {run.FramePacingMs:F3} ms " +
            $"({run.FramePacingMsPerFrame:F4} ms/frame), last completed {run.LastCompleted} >= waited {run.LastWaited}");
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        var sorted = new List<double>(values);
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }
}
