using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The AMD anti-lag backend against a real <c>vkAntiLagUpdateAMD</c>.
///
/// The whole extension is one entry point, and its whole contract is a pairing
/// rule: the INPUT stage is issued where the frame sleeps (before input is
/// sampled) and the PRESENT stage immediately before <c>vkQueuePresentKHR</c>,
/// both with the same frame index. A dropped or doubled half breaks the driver's
/// attribution silently - nothing fails, the pacing is simply wrong - so the
/// pairing is what these tests assert, over thirty frames, against the calls the
/// backend really made.
///
/// The discrete NVIDIA GPU on this machine does not expose VK_AMD_anti_lag in its
/// own driver; Mesa ships <c>VK_LAYER_MESA_anti_lag</c>, an implicit layer that
/// implements the extension on any device, which is what lets the path run here
/// at all. The suite disables implicit layers process-wide
/// (<see cref="TestEnvironment" />), so these tests re-enable that one layer for
/// the length of the test and put the environment back afterwards. If no physical
/// device advertises the extension and its feature bit, the test skips.
/// </summary>
public class AmdAntiLagBackendTests
{
    private const int Frames = 30;

    /// <summary>The Mesa layer is an implicit layer with an enable_environment, and the suite disables implicit layers.</summary>
    private const string LayerName = "VK_LAYER_MESA_anti_lag";
    private const string LoaderEnableVariable = "VK_LOADER_LAYERS_ENABLE";
    private const string MesaEnableVariable = "ENABLE_LAYER_MESA_ANTI_LAG";

    /// <summary>Physical device indices probed for the extension; a machine with more is unheard of here.</summary>
    private const int MaxDevices = 8;

    private readonly ITestOutputHelper _output;

    public AmdAntiLagBackendTests(ITestOutputHelper output) => _output = output;

    [SkippableFact]
    public void ThirtyFramesPairEveryInputStageWithItsPresentStage()
    {
        var messages = new List<string>();
        using AntiLagDevice found = FindAntiLagDevice(messages);
        Skip.If(found.Context == null, "No physical device advertises " +
            LatencyBackendSelector.AmdAntiLagExtensionName + " (" + found.Why + ").");

        VulkanContext context = found.Context!;
        _output.WriteLine("anti-lag device: " + context.Capabilities.DeviceName +
            " / " + context.Capabilities.DriverName + " (index " + found.DeviceIndex + ")");
        _output.WriteLine("latency summary: " + context.Capabilities.LatencySummary);

        var backend = AmdAntiLagBackend.Create(context.Device, context.AmdAntiLag!, _output.WriteLine);
        using (backend)
        {
            // 60 fps, which the extension takes as maxFPS on every update.
            backend.Apply(new LatencySettings(LatencyMode.On, LatencySettings.IntervalUsForFps(60)));
            Assert.True(backend.OwnsFrameCap);

            for (int frame = 0; frame < Frames; frame++)
            {
                DriveOneFrame(backend, (ulong)(frame + 1));
            }

            Assert.Equal(Frames, backend.InputStageCalls);
            Assert.Equal(Frames, backend.PresentStageCalls);
            Assert.Equal(0, backend.PairingFaults);

            // The mode was set once, at the first sleep, and never again: the
            // per-frame calls carry the mode but must not re-arm it.
            Assert.Equal(1, backend.ModeUpdates);

            AssertStagesAlternatePerFrame(backend.Calls, Frames, expectedMaxFps: 60u);

            // Every frame closed a report, so the stats sample has one per frame.
            Assert.Equal(Frames, backend.TakeReports().Length);
        }

        ValidationAssert.NoErrors(messages);
        ValidationAssert.NoSyncHazards(messages);
    }

    [SkippableFact]
    public void TheModeIsSetOnlyWhenItChanges()
    {
        var messages = new List<string>();
        using AntiLagDevice found = FindAntiLagDevice(messages);
        Skip.If(found.Context == null, "No physical device advertises " +
            LatencyBackendSelector.AmdAntiLagExtensionName + " (" + found.Why + ").");

        VulkanContext context = found.Context!;
        _output.WriteLine("anti-lag device: " + context.Capabilities.DeviceName);

        var backend = AmdAntiLagBackend.Create(context.Device, context.AmdAntiLag!, _output.WriteLine);
        using (backend)
        {
            // Off is a mode too: the first sleep tells the driver once.
            Assert.False(backend.OwnsFrameCap);
            DriveOneFrame(backend, 1);
            Assert.Equal(1, backend.ModeUpdates);
            Assert.Equal(0, backend.InputStageCalls);
            Assert.Equal(0, backend.PresentStageCalls);

            var on = new LatencySettings(LatencyMode.On, LatencySettings.IntervalUsForFps(60));
            backend.Apply(on);
            DriveOneFrame(backend, 2);
            DriveOneFrame(backend, 3);
            Assert.Equal(2, backend.ModeUpdates);

            // The same settings again, and Boost, which AMD has no separate mode
            // for: neither is a change the driver has to hear about.
            backend.Apply(on);
            DriveOneFrame(backend, 4);
            backend.Apply(new LatencySettings(LatencyMode.Boost, LatencySettings.IntervalUsForFps(60)));
            DriveOneFrame(backend, 5);
            Assert.Equal(2, backend.ModeUpdates);

            // A different cap is a change.
            backend.Apply(new LatencySettings(LatencyMode.On, LatencySettings.IntervalUsForFps(30)));
            DriveOneFrame(backend, 6);
            Assert.Equal(3, backend.ModeUpdates);

            // And switching off again.
            backend.Apply(LatencySettings.Disabled);
            DriveOneFrame(backend, 7);
            Assert.Equal(4, backend.ModeUpdates);
            Assert.False(backend.OwnsFrameCap);

            Assert.Equal(0, backend.PairingFaults);
            Assert.Equal(backend.InputStageCalls, backend.PresentStageCalls);

            // maxFPS is the cap, rounded: 1e6/16666 is 60.0024.
            uint[] caps = MaxFpsOfStagedCalls(backend.Calls);
            foreach (uint cap in caps) Assert.True(cap == 60u || cap == 30u, "unexpected maxFPS " + cap);
        }

        ValidationAssert.NoErrors(messages);
        ValidationAssert.NoSyncHazards(messages);
    }

    [SkippableFact]
    public void AFrameWhosePresentStageNeverCameIsCountedAndLoggedOnce()
    {
        var messages = new List<string>();
        var notes = new List<string>();
        using AntiLagDevice found = FindAntiLagDevice(messages);
        Skip.If(found.Context == null, "No physical device advertises " +
            LatencyBackendSelector.AmdAntiLagExtensionName + " (" + found.Why + ").");

        VulkanContext context = found.Context!;
        var backend = AmdAntiLagBackend.Create(context.Device, context.AmdAntiLag!, notes.Add);
        using (backend)
        {
            backend.Apply(new LatencySettings(LatencyMode.On, 0));

            // Frame 1 sleeps and never presents (a failed or skipped present);
            // frame 2's sleep must not let frame 1's INPUT pair with frame 2's
            // PRESENT, which would attribute the wrong interval for ever after.
            backend.Sleep(1);
            backend.Sleep(2);
            backend.Marker(2, LatencyMarker.PresentStart);

            Assert.Equal(1, backend.PairingFaults);
            Assert.Equal(2, backend.InputStageCalls);
            Assert.Equal(1, backend.PresentStageCalls);

            // Reported once, however often it happens.
            backend.Sleep(3);
            backend.Sleep(4);
            Assert.Equal(2, backend.PairingFaults);
            Assert.Single(notes);
            _output.WriteLine(notes[0]);
        }

        ValidationAssert.NoErrors(messages);
        ValidationAssert.NoSyncHazards(messages);
    }

    // --------------------------------------------------------------- driving

    /// <summary>
    /// One frame exactly as the seams place the calls: the sleep before input,
    /// the renderer's markers, PresentStart immediately before the present (which
    /// is where the PRESENT stage goes), then the present itself.
    /// </summary>
    private static void DriveOneFrame(AmdAntiLagBackend backend, ulong frameId)
    {
        backend.Sleep(frameId);
        backend.Marker(frameId, LatencyMarker.InputSample);
        backend.Marker(frameId, LatencyMarker.SimulationStart);
        backend.Marker(frameId, LatencyMarker.SimulationEnd);
        backend.Marker(frameId, LatencyMarker.RenderSubmitStart);
        backend.Marker(frameId, LatencyMarker.RenderSubmitEnd);
        backend.Marker(frameId, LatencyMarker.PresentStart);
        backend.Marker(frameId, LatencyMarker.PresentEnd);
        backend.OnPresent(frameId, frameId);
    }

    /// <summary>
    /// The staged calls, in order, must read INPUT(n), PRESENT(n), INPUT(n+1)...
    /// Mode-only calls are not part of a frame and are skipped.
    /// </summary>
    private static void AssertStagesAlternatePerFrame(AmdAntiLagCall[] calls, int frames, uint expectedMaxFps)
    {
        var staged = new List<AmdAntiLagCall>();
        foreach (AmdAntiLagCall call in calls)
        {
            if (!call.ModeOnly) staged.Add(call);
        }

        Assert.Equal(frames * 2, staged.Count);
        for (int frame = 0; frame < frames; frame++)
        {
            AmdAntiLagCall input = staged[frame * 2];
            AmdAntiLagCall present = staged[frame * 2 + 1];
            var expectedId = (ulong)(frame + 1);

            Assert.Equal(AntiLagStageAMD.InputAmd, input.Stage);
            Assert.Equal(AntiLagStageAMD.PresentAmd, present.Stage);
            Assert.Equal(expectedId, input.FrameId);
            Assert.Equal(expectedId, present.FrameId);
            Assert.Equal(AntiLagModeAMD.OnAmd, input.Mode);
            Assert.Equal(AntiLagModeAMD.OnAmd, present.Mode);
            Assert.Equal(expectedMaxFps, input.MaxFps);
            Assert.Equal(expectedMaxFps, present.MaxFps);
        }
    }

    private static uint[] MaxFpsOfStagedCalls(AmdAntiLagCall[] calls)
    {
        var caps = new List<uint>();
        foreach (AmdAntiLagCall call in calls)
        {
            if (!call.ModeOnly) caps.Add(call.MaxFps);
        }
        return caps.ToArray();
    }

    // -------------------------------------------------------------- discovery

    /// <summary>A context on a device that really has the extension, with the layer environment restored on dispose.</summary>
    private readonly struct AntiLagDevice : IDisposable
    {
        public AntiLagDevice(VulkanContext? context, int deviceIndex, string why, LayerEnvironment environment)
        {
            Context = context;
            DeviceIndex = deviceIndex;
            Why = why;
            Environment = environment;
        }

        public VulkanContext? Context { get; }
        public int DeviceIndex { get; }
        public string Why { get; }
        private LayerEnvironment Environment { get; }

        public void Dispose()
        {
            Context?.Dispose();
            Environment.Restore();
        }
    }

    /// <summary>
    /// Creates a headless, validated context on the first physical device whose
    /// driver (or the Mesa layer standing in for one) advertises VK_AMD_anti_lag
    /// with its feature bit, with the AMD backend pinned. Devices that do not have
    /// it select Native, which is how the absence is detected without a second
    /// enumeration path.
    /// </summary>
    private AntiLagDevice FindAntiLagDevice(List<string> messages)
    {
        LayerEnvironment environment = LayerEnvironment.EnableMesaAntiLag();

        string why = "no Vulkan device";
        for (int index = 0; index < MaxDevices; index++)
        {
            VulkanContextOptions options = GpuTest.ContextOptions(messages);
            options.PreferredDeviceIndex = index;
            options.LatencyBackend = LatencyBackendKind.AmdAntiLag;

            if (!VulkanContext.TryCreate(options, out VulkanContext? context, out string? failureReason))
            {
                why = failureReason ?? "device " + index + " unusable";
                _output.WriteLine("device " + index + ": " + why);
                // Out of range means the enumeration is exhausted.
                if (why.Contains("out of range")) break;
                continue;
            }

            if (context!.Capabilities.AmdAntiLagEnabled && context.AmdAntiLag != null)
            {
                return new AntiLagDevice(context, index, "", environment);
            }

            _output.WriteLine("device " + index + " (" + context.Capabilities.DeviceName + "): " +
                context.Capabilities.LatencySummary);
            why = "the devices present select " + LatencyBackends.Token(context.Capabilities.LatencyBackend);
            context.Dispose();
        }

        environment.Restore();
        return new AntiLagDevice(null, -1, why, LayerEnvironment.Untouched);
    }

    /// <summary>
    /// The two environment variables the Mesa anti-lag layer needs, set through
    /// libc because the Vulkan loader is native and .NET's own
    /// <c>SetEnvironmentVariable</c> only updates the managed copy on Unix (the
    /// same reason <see cref="TestEnvironment" /> uses setenv). The suite runs
    /// serially, so mutating the process environment for the length of one test
    /// cannot race another.
    /// </summary>
    internal readonly struct LayerEnvironment
    {
        private readonly string? _loaderEnable;
        private readonly string? _mesaEnable;
        private readonly bool _touched;

        private LayerEnvironment(string? loaderEnable, string? mesaEnable, bool touched)
        {
            _loaderEnable = loaderEnable;
            _mesaEnable = mesaEnable;
            _touched = touched;
        }

        public static LayerEnvironment Untouched => new(null, null, false);

        public static LayerEnvironment EnableMesaAntiLag()
        {
            string? loaderEnable = Environment.GetEnvironmentVariable(LoaderEnableVariable);
            string? mesaEnable = Environment.GetEnvironmentVariable(MesaEnableVariable);

            // VK_LOADER_LAYERS_ENABLE wins over the suite's
            // VK_LOADER_LAYERS_DISABLE=~implicit~, and the layer's own
            // enable_environment has to be set as well.
            Set(LoaderEnableVariable, loaderEnable == null || loaderEnable.Length == 0
                ? LayerName
                : loaderEnable + "," + LayerName);
            Set(MesaEnableVariable, "1");
            return new LayerEnvironment(loaderEnable, mesaEnable, true);
        }

        public void Restore()
        {
            if (!_touched) return;
            Set(LoaderEnableVariable, _loaderEnable);
            Set(MesaEnableVariable, _mesaEnable);
        }

        private static void Set(string name, string? value)
        {
            Environment.SetEnvironmentVariable(name, value);
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

            try
            {
                if (value == null) UnsetNativeEnvironmentVariable(name);
                else SetNativeEnvironmentVariable(name, value, 1);
            }
            catch (DllNotFoundException)
            {
            }
            catch (EntryPointNotFoundException)
            {
            }
        }

        [DllImport("libc", EntryPoint = "setenv")]
        private static extern int SetNativeEnvironmentVariable(string name, string value, int overwrite);

        [DllImport("libc", EntryPoint = "unsetenv")]
        private static extern int UnsetNativeEnvironmentVariable(string name);
    }
}
