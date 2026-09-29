// Source: Optimum.Render.Vulkan.Tests/GpuTest.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Config;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// The one place GPU tests get a <see cref="VulkanContext" /> or a
/// <see cref="VulkanDevice" /> from.
///
/// Every context and device comes up with the validation layers and, by
/// default, synchronization validation plus best practices ("sync,best").
/// Plain validation missed the R32F-history and masked-clear bugs that only
/// synchronization validation names (TAA P2 and P4, 2026-09-10/11), so the
/// suite runs with it unless OPTIMUM_TEST_VALIDATION_FEATURES says otherwise;
/// an empty value turns the extra features off.
/// </summary>
internal static class GpuTest
{
    public const string ValidationFeaturesVariable = "OPTIMUM_TEST_VALIDATION_FEATURES";
    public const string DefaultValidationFeatures = "sync,best";
    public const string DeviceIndexVariable = "OPTIMUM_TEST_DEVICE_INDEX";

    public static string ValidationFeatures =>
        Environment.GetEnvironmentVariable(ValidationFeaturesVariable) ?? DefaultValidationFeatures;

    private static int DeviceIndex
    {
        get
        {
            string? value = Environment.GetEnvironmentVariable(DeviceIndexVariable);
            if (value == null) return -1;
            Assert.True(int.TryParse(value, out int index) && index >= 0,
                DeviceIndexVariable + " must be a nonnegative device index.");
            return index;
        }
    }

    private static void CheckContext(VulkanContext context, ITestOutputHelper output)
    {
        output.WriteLine($"device={context.Capabilities.DeviceName}; vendor={context.Capabilities.VendorId:X}; driver={context.Capabilities.DriverVersion}; validation={context.ValidationSettingsApplied}");
        Assert.True(context.ValidationEnabled, "Khronos validation must be installed for GPU acceptance.");
        Assert.DoesNotContain("NOT APPLIED", context.ValidationSettingsApplied);
        if (!string.IsNullOrWhiteSpace(ValidationFeatures))
            Assert.False(string.IsNullOrWhiteSpace(context.ValidationSettingsApplied));
        string? expected = Environment.GetEnvironmentVariable("OPTIMUM_TEST_DEVICE_NAME");
        if (!string.IsNullOrWhiteSpace(expected))
            Assert.Contains(expected, context.Capabilities.DeviceName, StringComparison.OrdinalIgnoreCase);
    }

    private static void CheckUnavailable(string? reason, ITestOutputHelper output)
    {
        output.WriteLine("Vulkan unavailable: " + reason);
        Assert.True(Environment.GetEnvironmentVariable(DeviceIndexVariable) == null
            && Environment.GetEnvironmentVariable("OPTIMUM_TEST_DEVICE_NAME") == null,
            "The explicitly requested GPU could not initialize: " + reason);
    }

    public static VulkanContext CreateContext(ITestOutputHelper output, List<string>? messages = null)
    {
        Skip.IfNot(TryCreateContext(output, messages, out var context), "No usable Vulkan device.");
        return context!;
    }

    public static VulkanDevice CreateDevice(ITestOutputHelper output, Action<VulkanDevice>? configure = null)
    {
        Skip.IfNot(TryCreateDevice(output, out var device, configure), "No usable Vulkan device.");
        return device!;
    }

    /// <summary>Headless, validated options; <paramref name="messages" /> receives every layer message.</summary>
    public static VulkanContextOptions ContextOptions(List<string>? messages = null) => new()
    {
        Headless = true,
        EnableValidation = true,
        ValidationFeatures = ValidationFeatures,
        PreferredDeviceIndex = DeviceIndex,
        DebugCallback = messages == null ? null : Recorder(messages),
    };

    /// <summary>
    /// Appends under the list's own lock: the layers call back from whichever
    /// thread made the Vulkan call, and the asserts snapshot under the same lock.
    /// </summary>
    public static Action<string> Recorder(List<string> messages) => message =>
    {
        lock (messages) messages.Add(message);
    };

    public static bool TryCreateContext(ITestOutputHelper output, List<string>? messages, out VulkanContext? context)
    {
        bool created = VulkanContext.TryCreate(ContextOptions(messages), out context, out string? failureReason);
        if (!created) CheckUnavailable(failureReason, output);
        else
        {
            try { CheckContext(context!, output); }
            catch { context!.Dispose(); throw; }
        }
        return created;
    }

    private static readonly ConditionalWeakTable<VulkanDevice, List<string>> DeviceMessages = new();

    /// <summary>
    /// A device, not yet initialised, whose context will come up with the
    /// suite's validation features and record every layer message for
    /// <see cref="AssertClean" />. The client's own diagnostics channel still
    /// receives them too.
    /// </summary>
    public static VulkanDevice NewDevice()
    {
        var messages = new List<string>();
        // Blocking pipeline creation: these tests read pixels back after one frame, and a
        // background compile would skip that frame's draw. The async path has its own tests
        // (PipelineCacheTests), which set SynchronousPipelines = false before Initialize.
        var device = new VulkanDevice { DebugMode = true, SynchronousPipelines = true };
        device.ConfigureContextOptions = options =>
        {
            options.EnableValidation = true;
            options.ValidationFeatures = ValidationFeatures;
            options.PreferredDeviceIndex = DeviceIndex;
            Action<string>? client = options.DebugCallback;
            options.DebugCallback = message =>
            {
                lock (messages) messages.Add(message);
                client?.Invoke(message);
            };
        };
        DeviceMessages.Add(device, messages);
        return device;
    }

    public static bool TryCreateDevice(ITestOutputHelper output, out VulkanDevice? device, Action<VulkanDevice>? configure = null)
    {
        VulkanDevice created = NewDevice();
        try
        {
            configure?.Invoke(created);
            if (created.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
            {
                CheckContext(created.ContextForTests, output);
                device = created;
                return true;
            }
            CheckUnavailable(failureReason, output);
        }
        catch { created.Dispose(); throw; }
        created.Dispose();
        device = null;
        return false;
    }

    /// <summary>The layer messages a device from <see cref="NewDevice" /> has recorded so far.</summary>
    public static List<string> MessagesOf(VulkanDevice seam) =>
        seam is VulkanDevice device && DeviceMessages.TryGetValue(device, out List<string>? messages)
            ? messages
            : new List<string>();

    /// <summary>
    /// Fails on validation errors, synchronization hazards and device errors
    /// such as rejected shaders or failed Vulkan calls.
    /// </summary>
    public static void AssertClean(VulkanDevice seam)
    {
        List<string> messages = MessagesOf(seam);
        ValidationAssert.NoErrors(messages);

        string? diagnostics = seam.GetError();
        if (string.IsNullOrEmpty(diagnostics)) return;

        // GetError repeats the layer messages (sanitised for the client's
        // string.Format); those were judged above, so only the rest counts here.
        string residual = diagnostics;
        foreach (string message in ValidationAssert.Snapshot(messages))
        {
            residual = residual.Replace(message.Replace('{', '[').Replace('}', ']'), "");
        }

        var remaining = new List<string>();
        foreach (string line in residual.Split('\n'))
        {
            if (line.Trim().Length > 0) remaining.Add(line);
        }
        Assert.True(remaining.Count == 0, "device diagnostics:\n" + string.Join("\n", remaining));
    }
    /// <summary>
    /// A minimal shader stand-in. The client passes its own IShader and
    /// IShaderProgram implementations across the seam, so the device must work
    /// against the interfaces rather than any concrete type.
    /// </summary>
    internal sealed class TestShader : IShader
    {
        public EnumShaderType Type { get; set; }
        public string Code { get; set; } = "";
        public string PrefixCode { get; set; } = "";
        public bool Compile() => true;
    }

    internal sealed class TestProgram : IShaderProgram
    {
        public int ProgramId { get; set; }
        public string AssetDomain { get; set; } = "game";
        public int PassId { get; set; }
        public string PassName { get; set; } = "test";
        public bool ClampTexturesToEdge { get; set; }
        public IShader VertexShader { get; set; } = null!;
        public IShader FragmentShader { get; set; } = null!;
        public IShader GeometryShader { get; set; } = null!;
        public bool Oit { get; set; } = true;
        public bool Disposed => false;
        public bool LoadError => false;
        public Vintagestory.API.Datastructures.OrderedDictionary<string, UBORef> UBOs { get; } = new();

        public void Use() { }
        public void Stop() { }
        public bool Compile() => true;
        public void Dispose() { }
        public void Uniform(string uniformName, float value) { }
        public void Uniform(string uniformName, int value) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec2f value) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec2i value) { }
        public void Uniform(string uniformName, float valueX, float valueY) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec3f value) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ, float valueW) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec4f value) { }
        public void Uniforms4(string uniformName, int count, float[] values) { }
        public void UniformMatrix(string uniformName, float[] matrix) { }
        public void BindTexture2D(string samplerName, int textureId, int textureNumber) { }
        public void BindTextureCube(string samplerName, int textureId, int textureNumber) { }
        public void UniformMatrices(string uniformName, int count, float[] matrix) { }
        public void UniformMatrices4x3(string uniformName, int count, float[] matrix) { }
        public bool HasUniform(string uniformName) => false;
    }

    internal static int LinkProgram(
        VulkanDevice device, string vertexCode, string fragmentCode, string name = "test")
    {
        var vertex = new TestShader { Type = EnumShaderType.VertexShader, Code = vertexCode };
        var fragment = new TestShader { Type = EnumShaderType.FragmentShader, Code = fragmentCode };

        Assert.True(device.CompileShader(vertex));
        Assert.True(device.CompileShader(fragment));

        var program = new TestProgram { PassName = name, VertexShader = vertex, FragmentShader = fragment };
        int programId = device.LinkProgram(program);
        Assert.True(programId > 0, device.GetError() ?? "link failed");
        return programId;
    }

}
}

// Source: Optimum.Render.Vulkan.Tests/SetupQueue.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;

/// <summary>
/// Component tests' stand-in for the synchronous setup submit the renderer no
/// longer has (Phase 1B step 3 deleted VulkanCommands.SubmitAndWait).
///
/// It owns a Transfer timeline and an <see cref="UploadManager" /> for managers
/// used without a frame ring. <see cref="SubmitAndWait" /> appends the test's
/// commands to the open upload batch, after every upload recorded so far, submits
/// the batch on its own and waits for its Transfer value: the order a test wrote
/// its calls in is the order the GPU runs them. Test code only; the renderer
/// itself never waits for an upload.
/// </summary>
internal sealed unsafe class SetupQueue : IDisposable
{
    private readonly VulkanContext _context;
    private bool _disposed;

    public FrameTimeline Timeline { get; }
    public RetireQueue Retired { get; }
    public UploadManager Uploads { get; }

    public SetupQueue(VulkanContext context, ulong stagingPerSlot = 4UL << 20)
    {
        _context = context;
        Timeline = new FrameTimeline(context);
        Retired = new RetireQueue(Timeline);
        Uploads = new UploadManager(context, Timeline, Retired, framesInFlight: 2, stagingPerSlot);
    }

    /// <summary>Records into the open upload batch, submits it and waits for it.</summary>
    public void SubmitAndWait(Action<CommandBuffer> record)
    {
        CommandBuffer commandBuffer = Uploads.BeginRecording(inlineInFrame: false);
        try
        {
            record(commandBuffer);
        }
        finally
        {
            Uploads.EndRecording();
        }

        ulong transferValue = Uploads.SubmitStandalone();
        Timeline.WaitForTransfer(transferValue, WaitSite.Readback);
        Retired.Collect();
    }

    /// <summary>
    /// Moves a standalone image between layouts with a broad synchronization2
    /// barrier (all commands on both sides), for tests that drive raw images.
    /// </summary>
    public void TransitionImage(CommandBuffer commandBuffer, VulkanImage image, ImageLayout target, ImageAspectFlags aspect)
    {
        var barrier = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
            DstStageMask = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            OldLayout = image.Layout,
            NewLayout = target,
            Image = image.Handle,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };

        var dependency = new DependencyInfo
        {
            SType = StructureType.DependencyInfo,
            ImageMemoryBarrierCount = 1,
            PImageMemoryBarriers = &barrier,
        };

        _context.Api.CmdPipelineBarrier2(commandBuffer, &dependency);
        image.Layout = target;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Uploads.Dispose();
        Retired.DisposeAll();
        Timeline.Dispose();
    }
}
}
