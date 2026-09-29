using System;
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

public class DeviceValidationTests
{
    private readonly ITestOutputHelper _output;
    public DeviceValidationTests(ITestOutputHelper output) => _output = output;

    private VulkanContext Open(List<string> messages)
    {
        var options = GpuTest.ContextOptions(messages);
        options.ValidationFeatures = "sync,best";
        bool created = VulkanContext.TryCreate(options, out VulkanContext? context, out string? reason);
        // An explicitly requested device must fail visibly if it cannot be used.
        if (Environment.GetEnvironmentVariable(GpuTest.DeviceIndexVariable) != null)
            Assert.True(created, reason);
        Skip.IfNot(created, "No usable Vulkan device: " + reason);
        try
        {
            _output.WriteLine($"device={context!.Capabilities.DeviceName}; vendor={context.Capabilities.VendorId:X}; driver={context.Capabilities.DriverVersion}; API={context.Capabilities.ApiVersion}");
            _output.WriteLine("validation=" + context.ValidationSettingsApplied);
            Assert.True(context.ValidationEnabled, "Khronos validation must be installed for GPU acceptance.");
            Assert.DoesNotContain("NOT APPLIED", context.ValidationSettingsApplied);
            Assert.False(string.IsNullOrWhiteSpace(context.ValidationSettingsApplied));
            string? expected = Environment.GetEnvironmentVariable("OPTIMUM_TEST_DEVICE_NAME");
            if (!string.IsNullOrWhiteSpace(expected))
                Assert.Contains(expected, context.Capabilities.DeviceName, StringComparison.OrdinalIgnoreCase);
            return context;
        }
        catch { context!.Dispose(); throw; }
    }

    [Fact]
    public void ColorWriteOverridesNeverRequireUnsupportedFeatures()
    {
        foreach (bool enable in new[] { false, true })
            foreach (bool mask in new[] { false, true })
                foreach (string forced in new[] { "enable", "mask", "pipeline" })
                {
                    var requested = DeviceCaps.ParseColorWriteTier(forced);
                    string actual = DeviceCaps.Token(DeviceCaps.SelectColorWriteTier(enable, mask, requested));
                    string expected = forced == "pipeline" ? "pipeline"
                        : forced == "enable" && enable ? "enable" : mask ? "mask" : "pipeline";
                    Assert.Equal(expected, actual);
                }
        Assert.Null(DeviceCaps.ParseColorWriteTier("unknown"));
        Assert.Equal(DeviceCaps.ParseColorWriteTier("mask"), DeviceCaps.ParseColorWriteTier(" MASK "));
    }

    [SkippableFact]
    public void SelectedDeviceCreatesTheActualSharedPipelineLayout()
    {
        var messages = new List<string>();
        using (var context = Open(messages))
        {
            Assert.Empty(DescriptorIndexingFloor.Missing(context.Capabilities.DescriptorIndexing));
            using var layout = SharedPipelineLayout.CreateStandalone(context);
            Assert.NotEqual(0UL, layout.Layout.Handle);
            Assert.NotEqual(0UL, layout.TextureSetLayout.Handle);
            if (context.CheckpointsAvailable) Assert.NotNull(context.ReadQueueCheckpoints());
        }
        // Include destruction in the validation boundary.
        ValidationAssert.NoErrors(messages);
    }

    [SkippableFact]
    public void TranslatedFullscreenDrawUsesGlFramebufferCoordinates()
    {
        var device = GpuTest.CreateDevice(_output);
        try
        {
            var caps = device.ContextForTests.Capabilities;
            Assert.True(caps.ApiVersion >= VulkanContext.MinimumApiVersion);
            Assert.True(caps.MultiDrawIndirect);
            Assert.True(caps.MaxBoundDescriptorSets >= 3);
            Assert.True(caps.MaxSamplerLodBias >= 2f);
            Assert.True(caps.MaxImageDimension2D >= 4096);
            int program = GpuTest.LinkProgram(device, """
                #version 330 core
                out vec2 uv;
                void main() {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                    uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
                }
                """, """
                #version 330 core
                in vec2 uv;
                out vec4 color;
                void main() { color = vec4(uv, 0, 1); }
                """, "coordinate-convention");
            const int size = 32;
            int image = device.CreateTexture2DRaw(size, size, 0x8058, IntPtr.Zero, 4);
            int target = device.CreateFramebuffer(size, size);
            device.AttachTexture(target, EnumFramebufferAttachment.ColorAttachment0, image, 0);
            device.SetDrawBuffers(target, 1);
            device.BeginFrame(); device.BindFramebuffer(target); device.UseProgram(program);
            device.SetViewport(0, 0, size, size); device.SetDepthTest(false); device.SetCullFace(false);
            device.SetBlend(false, EnumBlendMode.Standard); device.DrawFullscreenTriangle();
            byte[] pixels = device.ReadBackLevel0ForTests(image);
            Assert.Equal(size * size * 4, pixels.Length);
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                int i = (y * size + x) * 4;
                int expectedX = (int)Math.Round((x + 0.5) * 255.0 / size);
                int expectedY = (int)Math.Round((y + 0.5) * 255.0 / size);
                Assert.InRange((int)pixels[i], expectedX - 1, expectedX + 1);
                Assert.InRange((int)pixels[i + 1], expectedY - 1, expectedY + 1);
                Assert.Equal(0, pixels[i + 2]); Assert.Equal(255, pixels[i + 3]);
            }
            device.Present();
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public unsafe void SynchronizationValidationDetectsAnActualMissingImageBarrier()
    {
        var messages = new List<string>();
        using (var context = Open(messages))
        using (var commands = new SetupQueue(context))
        using (var textures = new TextureManager(context, commands.Uploads))
        {
            var source = textures.Get(textures.Create(4, 4, Format.R8G8B8A8Unorm))!;
            var target = textures.Get(textures.Create(4, 4, Format.R8G8B8A8Unorm))!;
            commands.SubmitAndWait(commandBuffer =>
            {
                textures.TransitionTexture(commandBuffer, source, ImageLayout.TransferSrcOptimal);
                textures.TransitionTexture(commandBuffer, target, ImageLayout.TransferDstOptimal);
                var region = new ImageCopy
                {
                    SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    Extent = new Extent3D(4, 4, 1),
                };
                for (int i = 0; i < 2; i++)
                    context.Api.CmdCopyImage(commandBuffer, source.Image, ImageLayout.TransferSrcOptimal,
                        target.Image, ImageLayout.TransferDstOptimal, 1, &region);
            });
        }
        var snapshot = ValidationAssert.Snapshot(messages);
        foreach (string message in snapshot) _output.WriteLine(message);
        Assert.Contains(snapshot, m => m.Contains("SYNC-HAZARD-WRITE-AFTER-WRITE", StringComparison.Ordinal));
        // This one deliberate hazard is the control, not an allowance for renderer errors.
        ValidationAssert.NoErrors(snapshot.Where(m => !m.Contains("SYNC-HAZARD-WRITE-AFTER-WRITE", StringComparison.Ordinal)).ToArray());
    }
}

internal static class ValidationAssert
{
    public static List<string> Snapshot(IReadOnlyCollection<string> messages)
    {
        lock (messages) return new List<string>(messages);
    }

    public static bool IsSynchronization(string message) =>
        message.Contains("[SYNC-", StringComparison.Ordinal);

    public static void NoErrors(IReadOnlyCollection<string> messages)
    {
        string[] errors = Snapshot(messages).Where(m =>
            m.StartsWith(VulkanContext.ErrorPrefix, StringComparison.Ordinal) || IsSynchronization(m)).ToArray();
        Assert.True(errors.Length == 0, "validation errors:\n" + string.Join("\n", errors));
    }

    // Retained for existing call sites while the feature suite is replaced.
    public static void NoSyncHazards(IReadOnlyCollection<string> messages, string callerFile = "")
    {
        string[] hazards = Snapshot(messages).Where(IsSynchronization).ToArray();
        Assert.True(hazards.Length == 0, "synchronization hazards:\n" + string.Join("\n", hazards));
    }
}
