using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// OPTIMUM_VULKAN_POISON: fresh images and host-visible buffers carry a loud
/// value until something writes them, so content read before it was written
/// shows up as magenta, NaN or 0xDEADBEEF instead of whatever the allocator
/// held.
/// </summary>
public class PoisonModeTests
{
    private readonly ITestOutputHelper _output;

    public PoisonModeTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("1", true)]
    [InlineData(" 1 ", true)]
    public void TheVariableTurnsPoisonOnForAnyValueButEmptyAndZero(string? setting, bool expected)
    {
        Assert.Equal(expected, VulkanContext.PoisonRequested(setting));
    }

    [Fact]
    public unsafe void HostMemoryIsFilledWithDeadBeefWordsIncludingAPartialTail()
    {
        var bytes = new byte[11];
        fixed (byte* data = bytes)
        {
            VulkanPoison.FillHostMemory((IntPtr)data, (ulong)bytes.Length);
        }
        Assert.Equal(new byte[] { 0xEF, 0xBE, 0xAD, 0xDE, 0xEF, 0xBE, 0xAD, 0xDE, 0xEF, 0xBE, 0xAD }, bytes);
    }

    [Theory]
    [InlineData(Format.R8G8B8A8Unorm, false, false)]
    [InlineData(Format.R8G8B8A8Srgb, false, false)]
    [InlineData(Format.R16G16B16A16Sfloat, true, false)]
    [InlineData(Format.B10G11R11UfloatPack32, true, false)]
    [InlineData(Format.R32Uint, false, true)]
    [InlineData(Format.R16Sint, false, true)]
    public void FormatsAreClassifiedByComponentType(Format format, bool isFloat, bool isInteger)
    {
        Assert.Equal(isFloat, VulkanPoison.IsFloat(format));
        Assert.Equal(isInteger, VulkanPoison.IsInteger(format));
    }

    private bool TryCreateContext(bool poison, List<string> messages, out VulkanContext? context)
    {
        VulkanContextOptions options = GpuTest.ContextOptions(messages);
        options.Poison = poison;
        bool created = VulkanContext.TryCreate(options, out context, out string? failureReason);
        if (!created) _output.WriteLine("Vulkan unavailable: " + failureReason);
        return created;
    }

    /// <summary>
    /// A target with every kind of attachment, opened and closed with no clear
    /// and no draw, then read back: each attachment holds its format's poison.
    /// </summary>
    [SkippableFact]
    public void ARenderTargetNeverClearedOrDrawnReadsThePoisonValue()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(true, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            Assert.True(context!.PoisonFreshResources);
            const uint size = 8;
            using var commands = new VulkanCommands(context);
            using var textures = new TextureManager(context, commands);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context, textures, state);

            int unorm = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int srgb = textures.Create(size, size, Format.R8G8B8A8Srgb);
            int half = textures.Create(size, size, Format.R16G16B16A16Sfloat);
            int single = textures.Create(size, size, Format.R32Sfloat);
            int integer = textures.Create(size, size, Format.R32Uint);
            int depth = textures.Create(size, size, Format.D32Sfloat);

            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, unorm);
            targets.Attach(framebuffer, 1, srgb);
            targets.Attach(framebuffer, 2, half);
            targets.Attach(framebuffer, 3, single);
            targets.Attach(framebuffer, 4, integer);
            targets.Attach(framebuffer, -1, depth);
            targets.SetDrawBuffers(framebuffer, 0b11111);

            commands.SubmitAndWait(commandBuffer =>
            {
                targets.Bind(commandBuffer, framebuffer);
                targets.EnsureRendering(commandBuffer);
                targets.EndRendering(commandBuffer);
            });

            byte[] unormBytes = Read(context, commands, textures, unorm, size, 4, ImageAspectFlags.ColorBit);
            byte[] srgbBytes = Read(context, commands, textures, srgb, size, 4, ImageAspectFlags.ColorBit);
            for (int i = 0; i < unormBytes.Length; i += 4)
            {
                Assert.Equal(new byte[] { 255, 0, 255, 255 }, unormBytes[i..(i + 4)]);
                Assert.Equal(new byte[] { 255, 0, 255, 255 }, srgbBytes[i..(i + 4)]);
            }

            byte[] halfBytes = Read(context, commands, textures, half, size, 8, ImageAspectFlags.ColorBit);
            for (int i = 0; i < halfBytes.Length; i += 2)
            {
                Assert.True(Half.IsNaN(BitConverter.ToHalf(halfBytes, i)), "half texel byte " + i + " is not NaN");
            }

            byte[] singleBytes = Read(context, commands, textures, single, size, 4, ImageAspectFlags.ColorBit);
            for (int i = 0; i < singleBytes.Length; i += 4)
            {
                Assert.True(float.IsNaN(BitConverter.ToSingle(singleBytes, i)), "R32F texel byte " + i + " is not NaN");
            }

            byte[] integerBytes = Read(context, commands, textures, integer, size, 4, ImageAspectFlags.ColorBit);
            for (int i = 0; i < integerBytes.Length; i += 4)
            {
                Assert.Equal(VulkanPoison.Word, BitConverter.ToUInt32(integerBytes, i));
            }

            byte[] depthBytes = Read(context, commands, textures, depth, size, 4, ImageAspectFlags.DepthBit);
            for (int i = 0; i < depthBytes.Length; i += 4)
            {
                Assert.Equal(VulkanPoison.Depth, BitConverter.ToSingle(depthBytes, i));
            }

            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>Poison is only what a fresh resource starts with: a clear replaces it completely.</summary>
    [SkippableFact]
    public void AClearedTargetReadsItsClearValueWithPoisonOn()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(true, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 8;
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);

            int color = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int depth = textures.Create(size, size, Format.D32Sfloat);
            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, color);
            targets.Attach(framebuffer, -1, depth);

            commands.SubmitAndWait(commandBuffer =>
            {
                targets.Bind(commandBuffer, framebuffer);
                targets.ClearColor(commandBuffer, 0, 0.25f, 0.5f, 0.75f, 1f);
                targets.ClearDepth(commandBuffer, 1f);
                targets.EndRendering(commandBuffer);
            });

            byte[] colorBytes = Read(context!, commands, textures, color, size, 4, ImageAspectFlags.ColorBit);
            for (int i = 0; i < colorBytes.Length; i += 4)
            {
                Assert.Equal(new byte[] { 64, 128, 191, 255 }, colorBytes[i..(i + 4)]);
            }

            byte[] depthBytes = Read(context!, commands, textures, depth, size, 4, ImageAspectFlags.DepthBit);
            for (int i = 0; i < depthBytes.Length; i += 4)
            {
                Assert.Equal(1f, BitConverter.ToSingle(depthBytes, i));
            }

            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    [SkippableFact]
    public void FreshHostVisibleBuffersHoldDeadBeefOnlyInPoisonMode()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(true, messages, out VulkanContext? poisoned), "No usable Vulkan device.");
        using (poisoned)
        {
            using var buffer = new VulkanBuffer(poisoned!, 64, BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
            var bytes = new byte[64];
            Marshal.Copy(buffer.Mapped, bytes, 0, bytes.Length);
            for (int i = 0; i < bytes.Length; i += 4)
            {
                Assert.Equal(VulkanPoison.Word, BitConverter.ToUInt32(bytes, i));
            }
            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }

        var plainMessages = new List<string>();
        Skip.IfNot(TryCreateContext(false, plainMessages, out VulkanContext? plain), "No usable Vulkan device.");
        using (plain)
        {
            Assert.False(plain!.PoisonFreshResources);
        }
    }

    private static unsafe byte[] Read(
        VulkanContext context, VulkanCommands commands, TextureManager textures,
        int textureId, uint size, int bytesPerTexel, ImageAspectFlags aspect)
    {
        VulkanTexture texture = textures.Get(textureId)!;
        ulong bytes = (ulong)size * size * (ulong)bytesPerTexel;

        using var readback = new VulkanBuffer(context, bytes,
            BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

        commands.SubmitAndWait(commandBuffer =>
        {
            textures.TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);
            var region = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers(aspect, 0, 0, 1),
                ImageExtent = new Extent3D(size, size, 1),
            };
            context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
                ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);
        });

        var result = new byte[(int)bytes];
        Marshal.Copy(readback.Mapped, result, 0, result.Length);
        return result;
    }
}
