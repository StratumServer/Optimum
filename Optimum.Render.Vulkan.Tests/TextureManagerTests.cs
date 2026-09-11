using System;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Covers the texture handle table, the sampler cache, and a real upload and
/// readback through the GPU.
///
/// The handle table matters more than it looks: the game's public API exposes raw
/// texture ids as fields that mods read and hand back
/// (<c>LoadedTexture.TextureId</c>, <c>FrameBufferRef.ColorTextureIds</c>), so
/// ids have to behave like GL names including reuse after deletion.
/// </summary>
public class TextureManagerTests
{
    [Theory]
    [InlineData(short.MinValue, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 2)]
    [InlineData(16384, 32769)]
    [InlineData(short.MaxValue, ushort.MaxValue)]
    public void SignedShortTextureInputIsNormalizedBeforeUnsignedStorage(short source, int expected)
    {
        Assert.Equal((ushort)expected, TextureManager.ShortToUnorm16(source));
    }

    private readonly ITestOutputHelper _output;

    public TextureManagerTests(ITestOutputHelper output) => _output = output;

    private static bool TryCreateContext(ITestOutputHelper output, out VulkanContext? context)
    {
        var options = new VulkanContextOptions { Headless = true, EnableValidation = true };
        bool created = VulkanContext.TryCreate(options, out context, out string? failureReason);
        if (!created) output.WriteLine("Vulkan unavailable: " + failureReason);
        return created;
    }

    [SkippableFact]
    public void TextureIdsBehaveLikeGlNamesIncludingReuse()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);

            int first = textures.Create(16, 16, Format.R8G8B8A8Unorm);
            int second = textures.Create(16, 16, Format.R8G8B8A8Unorm);

            // Zero is never a real texture, as in GL.
            Assert.True(first > 0);
            Assert.NotEqual(first, second);
            Assert.Null(textures.Get(0));
            Assert.NotNull(textures.Get(first));

            textures.Delete(first);
            Assert.Null(textures.Get(first));

            // A freed name is handed out again rather than growing the table.
            int third = textures.Create(8, 8, Format.R8G8B8A8Unorm);
            Assert.Equal(first, third);
            Assert.Equal(2, textures.Count);
        }
    }

    /// <summary>
    /// glTexParameter changes state on the texture and touches no GPU object.
    /// The sampler only materialises when the texture is bound.
    /// </summary>
    [SkippableFact]
    public void TextureParametersUpdateSamplerStateWithoutCreatingObjects()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);

            int id = textures.Create(16, 16, Format.R8G8B8A8Unorm);

            textures.SetParameter(id, GlEnums.TextureMagFilter, 0x2601);   // GL_LINEAR
            textures.SetParameter(id, GlEnums.TextureMinFilter, 0x2703);   // LINEAR_MIPMAP_LINEAR
            textures.SetParameter(id, GlEnums.TextureWrapS, 0x812F);       // CLAMP_TO_EDGE
            textures.SetParameter(id, GlEnums.TextureLodBias, -0.5f);
            textures.SetParameter(id, GlEnums.TextureCompareMode, GlEnums.TextureCompareRefToTexture);

            SamplerState state = textures.Get(id)!.State;
            Assert.Equal(Filter.Linear, state.MagFilter);
            Assert.Equal(Filter.Linear, state.MinFilter);
            Assert.Equal(SamplerMipmapMode.Linear, state.MipmapMode);
            Assert.Equal(SamplerAddressMode.ClampToEdge, state.AddressU);
            Assert.Equal(SamplerAddressMode.Repeat, state.AddressV);
            Assert.Equal(-0.5f, state.LodBias);
            Assert.True(state.CompareEnable);

            // Nothing has been bound, so no sampler exists yet.
            Assert.Equal(0, textures.Samplers.Count);
        }
    }

    /// <summary>
    /// A GL min filter decides whether the mip chain is sampled at all, and the
    /// sampler's LOD clamp is the only place Vulkan can say so.
    ///
    /// GL_LINEAR and GL_NEAREST read level 0 however many levels the image owns;
    /// only the four MIPMAP filters descend the chain, and GL_TEXTURE_MAX_LEVEL
    /// then caps how far. Vulkan has no non-mipmapping filter - a sampler always
    /// picks a level out of [MinLod, MaxLod] - so leaving MaxLod unclamped lets
    /// a texture that merely owns a chain be minified through it. On the block
    /// atlas that puts unrelated block textures onto every surface that turns
    /// away from the camera, while whatever is drawn flat stays correct.
    /// </summary>
    [SkippableFact]
    public void OnlyAMipmappingFilterLetsTheSamplerLeaveLevelZero()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);

            int id = textures.Create(16, 16, Format.R8G8B8A8Unorm, generateMipmaps: true);
            Assert.True(textures.Get(id)!.MipLevels > 1, "the texture should own a chain to sample");

            // What the atlas upload sets: linear, and so level 0 only.
            textures.SetParameter(id, GlEnums.TextureMinFilter, 0x2601);   // GL_LINEAR
            SamplerState linear = textures.Get(id)!.State;
            Assert.False(linear.Mipmapped);
            Assert.True(linear.LodCeiling < 1f,
                $"a non-mipmapping filter must confine sampling to level 0, got {linear.LodCeiling}");

            // What BuildMipMaps sets once a chain exists, capped to the setting.
            textures.SetParameter(id, GlEnums.TextureMinFilter, 0x2702);   // NEAREST_MIPMAP_LINEAR
            textures.SetParameter(id, GlEnums.TextureMaxLevel, 3);
            SamplerState mipmapped = textures.Get(id)!.State;
            Assert.True(mipmapped.Mipmapped);
            Assert.Equal(3, mipmapped.MaxLevel);
            Assert.Equal(3f, mipmapped.LodCeiling);

            // Uncapped stays uncapped.
            textures.SetParameter(id, GlEnums.TextureMaxLevel, -1);
            Assert.Equal(Vk.LodClampNone, textures.Get(id)!.State.LodCeiling);
        }
    }

    /// <summary>
    /// Optimum's FSR path sets a negative LOD bias on the terrain samplers, so a
    /// bias change has to produce a genuinely different sampler rather than
    /// reusing one that ignores it.
    /// </summary>
    [SkippableFact]
    public void SamplersInternByStateAndDistinguishLodBias()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var cache = new SamplerCache(context!);

            SamplerState nearest = SamplerState.Default;
            SamplerState alsoNearest = SamplerState.Default;
            SamplerState biased = SamplerState.Default with { LodBias = -0.58f };
            SamplerState linear = SamplerState.Default with { MagFilter = Filter.Linear };

            Assert.Equal(cache.Get(nearest).Handle, cache.Get(alsoNearest).Handle);
            Assert.NotEqual(cache.Get(nearest).Handle, cache.Get(biased).Handle);
            Assert.NotEqual(cache.Get(nearest).Handle, cache.Get(linear).Handle);
            Assert.Equal(3, cache.Count);

            // Repeating the same requests adds nothing.
            for (int i = 0; i < 100; i++) cache.Get(nearest);
            Assert.Equal(3, cache.Count);
        }
    }

    /// <summary>
    /// The end-to-end check for the texture path: bytes uploaded from the CPU come
    /// back byte-identical off the GPU.
    /// </summary>
    [SkippableFact]
    public unsafe void UploadedPixelsSurviveARoundTripThroughTheGpu()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);

            const uint size = 8;
            int id = textures.Create(size, size, Format.R8G8B8A8Unorm);

            var source = new byte[size * size * 4];
            for (int i = 0; i < source.Length; i++) source[i] = (byte)(i * 7 % 251);

            fixed (byte* pixels = source)
            {
                textures.Upload(id, 0, 0, 0, size, size, (IntPtr)pixels, bytesPerPixel: 4);
            }

            VulkanTexture texture = textures.Get(id)!;
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, texture.Layout);

            using var readback = new VulkanBuffer(context!, (ulong)source.Length,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);

            commands.SubmitAndWait(commandBuffer =>
            {
                textures.TransitionTexture(commandBuffer, texture, ImageLayout.TransferSrcOptimal);

                var region = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                    ImageExtent = new Extent3D(size, size, 1),
                };
                context!.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
                    ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);
            });

            var result = new byte[source.Length];
            Marshal.Copy(readback.Mapped, result, 0, result.Length);

            Assert.Equal(source, result);
        }
    }

    /// <summary>
    /// The block atlas is mipmapped, and the chain is built by successive blits.
    /// A wrong barrier here shows up as validation errors, not wrong pixels.
    /// </summary>
    [SkippableFact]
    public unsafe void MipmapGenerationBuildsTheWholeChainCleanly()
    {
        var messages = new System.Collections.Generic.List<string>();
        var options = new VulkanContextOptions
        {
            Headless = true,
            EnableValidation = true,
            DebugCallback = messages.Add,
        };
        Skip.IfNot(VulkanContext.TryCreate(options, out VulkanContext? context, out string? reason), reason ?? "");

        using (context)
        {
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);

            const uint size = 64;
            int id = textures.Create(size, size, Format.R8G8B8A8Unorm, generateMipmaps: true);

            VulkanTexture texture = textures.Get(id)!;
            Assert.Equal(TextureManager.MipLevelsFor(size, size), texture.MipLevels);
            Assert.Equal(7u, texture.MipLevels);   // 64, 32, 16, 8, 4, 2, 1

            var source = new byte[size * size * 4];
            Array.Fill(source, (byte)200);
            fixed (byte* pixels = source)
            {
                textures.Upload(id, 0, 0, 0, size, size, (IntPtr)pixels, bytesPerPixel: 4);
            }

            textures.GenerateMipmaps(id);
            Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, texture.Layout);

            ValidationAssert.NoErrors(messages);
        }
    }

    [Theory]
    [InlineData(1u, 1u, 1u)]
    [InlineData(2u, 2u, 2u)]
    [InlineData(64u, 64u, 7u)]
    [InlineData(1024u, 512u, 11u)]
    [InlineData(4096u, 4096u, 13u)]
    public void MipLevelCountMatchesTheGlRule(uint width, uint height, uint expected)
    {
        Assert.Equal(expected, TextureManager.MipLevelsFor(width, height));
    }

    [SkippableFact]
    public void CubeAndArrayTexturesReportTheirLayers()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);

            int cube = textures.Create(32, 32, Format.R8G8B8A8Unorm, cube: true);
            Assert.Equal(6u, textures.Get(cube)!.Layers);

            // The OIT accumulation target is a three-layer array.
            int array = textures.Create(32, 32, Format.R16G16B16A16Sfloat, layers: 3);
            Assert.Equal(3u, textures.Get(array)!.Layers);
        }
    }

    [SkippableFact]
    public void DepthFormatsGetADepthAspectAndAttachmentUsage()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);

            int depth = textures.Create(64, 64, Format.D32Sfloat);
            Assert.Equal(ImageAspectFlags.DepthBit, textures.Get(depth)!.Aspect);

            int color = textures.Create(64, 64, Format.R8G8B8A8Unorm);
            Assert.Equal(ImageAspectFlags.ColorBit, textures.Get(color)!.Aspect);
        }
    }

    /// <summary>
    /// Vulkan has four fixed border colours where GL takes any value. The SSAO
    /// targets clamp to opaque white, which has to survive the rounding.
    /// </summary>
    [SkippableFact]
    public void BorderColoursRoundToTheNearestFixedVulkanValue()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);

            int id = textures.Create(4, 4, Format.R8G8B8A8Unorm);

            textures.SetBorderColor(id, 1f, 1f, 1f, 1f);
            Assert.Equal(BorderColor.FloatOpaqueWhite, textures.Get(id)!.State.BorderColor);

            textures.SetBorderColor(id, 0f, 0f, 0f, 1f);
            Assert.Equal(BorderColor.FloatOpaqueBlack, textures.Get(id)!.State.BorderColor);

            textures.SetBorderColor(id, 0f, 0f, 0f, 0f);
            Assert.Equal(BorderColor.FloatTransparentBlack, textures.Get(id)!.State.BorderColor);
        }
    }

    /// <summary>
    /// A deleted texture must outlive any frame that might still reference it,
    /// so deletion routes through the frame ring rather than freeing immediately.
    /// </summary>
    [SkippableFact]
    public void DeletionThroughTheFrameRingIsDeferred()
    {
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);
            using var ring = new FrameRing(context!, framesInFlight: 2, uniformRingSize: 1 << 20);

            int id = textures.Create(16, 16, Format.R8G8B8A8Unorm);
            textures.Delete(id, ring);

            // Gone from the table straight away, but not yet destroyed.
            Assert.Null(textures.Get(id));
            Assert.Equal(1, ring.PendingDeletionCount);

            for (int frame = 0; frame < 4; frame++)
            {
                ring.BeginFrame();
                ring.EndFrame();
            }
            Assert.Equal(0, ring.PendingDeletionCount);

            context!.Api.DeviceWaitIdle(context.Device);
        }
    }
}
