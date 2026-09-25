using System;
using System.Linq;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

public class TextureTransferTests(ITestOutputHelper output)
{
    private const string Triangle = """
        #version 330 core
        void main() { gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2), -1 + ((gl_VertexID & 2) << 1), 0, 1); }
        """;

    private VulkanDevice Open() => GpuTest.CreateDevice(output);

    private static int Target(VulkanDevice device, int width, int height, params int[] textures)
    {
        int target = device.CreateFramebuffer(width, height);
        for (int i = 0; i < textures.Length; i++)
            device.AttachTexture(target, (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + i), textures[i], 0);
        device.SetDrawBuffers(target, (1 << textures.Length) - 1);
        return target;
    }

    private static int Texture(VulkanDevice device, int width, int height) => device.CreateTexture2D(width, height,
        EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

    private static void Draw(VulkanDevice device, int target, int program, int size)
    {
        device.BindFramebuffer(target); device.SetViewport(0, 0, size, size);
        device.SetDepthTest(false); device.SetCullFace(false);
        device.SetBlend(false, EnumBlendMode.Standard); device.UseProgram(program);
        device.DrawFullscreenTriangle();
    }

    private static void Color(byte[] pixels, byte r, byte g, byte b, byte a = 255)
    {
        Assert.NotEmpty(pixels);
        Assert.Equal(0, pixels.Length % 4);
        uint expected = BitConverter.ToUInt32(new byte[] { r, g, b, a });
        Assert.Equal(-1, MemoryMarshal.Cast<byte, uint>(pixels).IndexOfAnyExcept(expected));
    }

    private static unsafe byte[] Read(VulkanDevice device, int target, int size)
    {
        var pixels = new byte[size * size * 4];
        device.BindFramebuffer(target);
        fixed (byte* pointer = pixels) device.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)pointer);
        return pixels;
    }

    [SkippableFact]
    public unsafe void MixedTexelFormatsAndSignedInputRoundTripInOneFrame()
    {
        var device = Open();
        try
        {
            byte[] rgba = { 11, 22, 33, 44 };
            float[] floats = Enumerable.Range(0, 16).Select(i => i * 0.25f - 1.5f).ToArray();
            float[] historyDepths = { 12f, 128f };
            int small, wide, historyDepth;
            fixed (byte* pointer = rgba) small = device.CreateTexture2D(1, 1, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, (IntPtr)pointer, false);
            fixed (float* pointer = floats) wide = device.CreateTexture2DRaw(2, 2, 0x8814, (IntPtr)pointer, 16); // RGBA32F
            fixed (float* pointer = historyDepths) historyDepth = device.CreateTexture2DRaw(2, 1, 0x822E, (IntPtr)pointer, 4); // R32F
            int normalized = device.CreateTexture2DRaw(2, 1, 0x805B, IntPtr.Zero, 8); // RGBA16
            device.UploadTexture2DNormalizedShorts(normalized, 0, 0, 0, 2, 1,
                new short[] { short.MinValue, -1, 0, 1, 16384, short.MaxValue, 8192, 0 });
            device.BeginFrame();
            // A four-byte result followed by sixteen-byte texels exercises arena alignment.
            Assert.Equal(rgba, device.ReadBackLevel0ForTests(small));
            Assert.Equal(MemoryMarshal.AsBytes(floats.AsSpan()).ToArray(), device.ReadBackLevel0ForTests(wide));
            Assert.Equal(MemoryMarshal.AsBytes(historyDepths.AsSpan()).ToArray(), device.ReadBackLevel0ForTests(historyDepth));
            ushort[] expected = { 0, 0, 0, 2, 32769, 65535, 16384, 0 };
            Assert.Equal(MemoryMarshal.AsBytes(expected.AsSpan()).ToArray(), device.ReadBackLevel0ForTests(normalized));
            Assert.Equal(rgba, device.ReadBackLevel0ForTests(small));
            device.Present();
            ulong oldLifetime = device.TexturesForTests.Get(small)!.Id;
            device.DeleteTexture(small);
            Assert.Null(device.TexturesForTests.Get(small));
            int reused = Texture(device, 1, 1);
            Assert.Equal(small, reused);
            Assert.NotEqual(oldLifetime, device.TexturesForTests.Get(reused)!.Id);
            Assert.Null(device.TexturesForTests.Get(0));
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public unsafe void ChangingAnAlreadyBoundSamplerBiasChangesTheNextDraw()
    {
        using var device = Open();
        int source = device.CreateTexture2D(4, 4, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, true);
        for (int level = 0; level < 3; level++)
        {
            int side = 4 >> level;
            byte[] pixels = new byte[side * side * 4];
            for (int i = 0; i < pixels.Length; i += 4)
            {
                pixels[i + level] = 255;
                pixels[i + 3] = 255;
            }
            fixed (byte* pointer = pixels)
                device.UploadTexture2D(source, level, 0, 0, side, side,
                    EnumTexturePixelFormat.Rgba, (IntPtr)pointer);
        }

        int target = Target(device, 4, 4, Texture(device, 4, 4));
        int program = GpuTest.LinkProgram(device, Triangle, """
            #version 330 core
            uniform sampler2D source;
            out vec4 color;
            void main() { color = texture(source, gl_FragCoord.xy / 4.0); }
            """, "live-sampler-bias");
        device.SetSamplerUnit(program, "source", 0);
        device.BindTexture(0, source);
        int sampler = device.CreateSampler(false);
        device.BindSampler(0, sampler);

        foreach (int level in new[] { 0, 1, 2, 0 })
        {
            device.SetSamplerParameter(sampler, GlEnums.TextureLodBias, (float)level);
            device.BeginFrame();
            Draw(device, target, program, 4);
            byte[] pixels = Read(device, target, 4);
            Color(pixels, level == 0 ? (byte)255 : (byte)0,
                level == 1 ? (byte)255 : (byte)0,
                level == 2 ? (byte)255 : (byte)0);
            device.Present();
        }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public unsafe void TextureFiltersClampMipSamplingAndSamplerStateIsInterned()
    {
        var device = Open();
        try
        {
            int texture = device.CreateTexture2D(8, 8, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, true);
            for (int level = 0; level < 4; level++)
            {
                int size = 8 >> level;
                var pixels = new byte[size * size * 4];
                for (int i = 0; i < pixels.Length; i += 4)
                { pixels[i] = (byte)(30 + level * 50); pixels[i + 1] = 60; pixels[i + 2] = 90; pixels[i + 3] = 255; }
                fixed (byte* pointer = pixels) device.UploadTexture2D(texture, level, 0, 0, size, size, EnumTexturePixelFormat.Rgba, (IntPtr)pointer);
            }
            int target = Target(device, 4, 4, Texture(device, 4, 4));
            int program = GpuTest.LinkProgram(device, Triangle, """
                #version 330 core
                uniform sampler2D source;
                out vec4 color;
                void main() { color = textureLod(source, vec2(0.5), 3); }
                """, "texture-mip-clamp");
            device.SetSamplerUnit(program, "source", 0);
            byte[] uniform = Enumerable.Repeat((byte)127, 9 * 5 * 4).ToArray();
            int generated;
            fixed (byte* pointer = uniform) generated = device.CreateTexture2D(9, 5, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, (IntPtr)pointer, true);
            device.BeginFrame();
            Assert.Equal(4u, device.TexturesForTests.Get(generated)!.MipLevels);
            for (uint level = 0; level < 4; level++)
            {
                byte[] mip = device.ReadBackLevelForTests(generated, level);
                Assert.Equal(Math.Max(1, 9 >> (int)level) * Math.Max(1, 5 >> (int)level) * 4, mip.Length);
                Assert.Equal(-1, mip.AsSpan().IndexOfAnyExcept((byte)127));
            }
            foreach (var state in new (int Filter, int Max, int Red)[] { (0x2600, -1, 30), (0x2700, 1, 80), (0x2700, -1, 180), (0x2601, -1, 30) })
            {
                device.SetTextureParameter(texture, GlEnums.TextureMinFilter, state.Filter);
                device.SetTextureParameter(texture, GlEnums.TextureMaxLevel, state.Max);
                device.BindTexture(0, texture);
                Draw(device, target, program, 4);
                Color(Read(device, target, 4), (byte)state.Red, 60, 90);
            }
            device.Present();
            var cache = device.TexturesForTests.Samplers;
            var original = device.TexturesForTests.Get(texture)!.State;
            var first = cache.Get(original);
            Assert.Equal(first.Handle, cache.Get(original).Handle);
            Assert.NotEqual(first.Handle, cache.Get(original with { LodBias = -0.5f }).Handle);
            foreach (var border in new (float Value, float Alpha, Silk.NET.Vulkan.BorderColor Color)[] { (1f, 1f, Silk.NET.Vulkan.BorderColor.FloatOpaqueWhite),
                         (0f, 1f, Silk.NET.Vulkan.BorderColor.FloatOpaqueBlack), (0f, 0f, Silk.NET.Vulkan.BorderColor.FloatTransparentBlack) })
            {
                device.SetTextureBorderColor(texture, border.Value, border.Value, border.Value, border.Alpha);
                Assert.Equal(border.Color, device.TexturesForTests.Get(texture)!.State.BorderColor);
            }
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public unsafe void PartialReadbackPreservesUniformSnapshotsAndFrameIdentity()
    {
        var device = Open();
        try
        {
            int targetA = Target(device, 4, 4, Texture(device, 4, 4)), targetB = Target(device, 4, 4, Texture(device, 4, 4));
            int program = GpuTest.LinkProgram(device, Triangle, """
                #version 330 core
                layout(std140) uniform Tint { vec4 tint; };
                out vec4 color;
                void main() { color = tint; }
                """, "readback-uniform-snapshot");
            int ubo = device.CreateUniformBuffer(program, 0, "Tint", 16);
            device.BindUniformBuffer(ubo);
            void Tint(byte red)
            {
                float[] data = { red / 255f, 60 / 255f, 90 / 255f, 1 };
                fixed (float* pointer = data) device.UpdateUniformBuffer(ubo, (IntPtr)pointer, 0, 16);
            }
            device.BeginFrame(); device.Present();
            long pacing = VulkanStats.WaitCount(WaitSite.FramePacing), idle = VulkanStats.WaitCount(WaitSite.DeviceWaitIdle);
            long flush = VulkanStats.WaitCount(WaitSite.FlushFrame), reads = VulkanStats.WaitCount(WaitSite.Readback);
            long submits = VulkanStats.WaitCount(WaitSite.QueueSubmit);
            for (int frame = 0; frame < 8; frame++)
            {
                device.BeginFrame();
                ulong frameId = device.LatencyFrameId;
                Tint((byte)(20 + frame)); Draw(device, targetA, program, 4);
                Color(Read(device, targetA, 4), (byte)(20 + frame), 60, 90);
                Assert.Equal(frameId, device.LatencyFrameId);
                // The unchanged block's snapshot must remain live across the partial submission.
                Draw(device, targetB, program, 4);
                Tint((byte)(100 + frame)); Draw(device, targetA, program, 4);
                device.Present();
                device.BeginFrame();
                Color(Read(device, targetA, 4), (byte)(100 + frame), 60, 90);
                Color(Read(device, targetB, 4), (byte)(20 + frame), 60, 90);
                device.Present();
            }
            Assert.Equal(16, VulkanStats.WaitCount(WaitSite.FramePacing) - pacing);
            Assert.Equal(24, VulkanStats.WaitCount(WaitSite.Readback) - reads);
            Assert.Equal(40, VulkanStats.WaitCount(WaitSite.QueueSubmit) - submits);
            Assert.Equal(idle, VulkanStats.WaitCount(WaitSite.DeviceWaitIdle));
            Assert.Equal(flush, VulkanStats.WaitCount(WaitSite.FlushFrame));
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public void ReadbackArenaGrowthKeepsEveryPixelAndClearInOrder()
    {
        var device = Open();
        try
        {
            const int size = 1024;
            int target = Target(device, size, size, Texture(device, size, size));
            device.BeginFrame();
            for (int i = 0; i < 3; i++)
            {
                device.BindFramebuffer(target);
                device.ClearColor(0, (30 + i * 60) / 255f, 60 / 255f, 90 / 255f, 1);
                Color(Read(device, target, size), (byte)(30 + i * 60), 60, 90);
            }
            device.Present();
            for (int i = 0; i < 3; i++) { device.BeginFrame(); device.Present(); }
            device.BeginFrame();
            Color(Read(device, target, size), 150, 60, 90);
            device.Present();
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableTheory]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(31)]
    public unsafe void SparseFragmentOutputsOnlyChangeEnabledAttachments(int mask)
    {
        var device = Open();
        try
        {
            int[] textures = Enumerable.Range(0, 5).Select(_ => Texture(device, 4, 4)).ToArray();
            int target = Target(device, 4, 4, textures);
            int program = GpuTest.LinkProgram(device, Triangle, """
                #version 330 core
                layout(location = 0) out vec4 color;
                layout(location = 4) out vec4 motion;
                void main() { color = vec4(1, 0, 0, 1); motion = vec4(0, 1, 0, 1); }
                """, "sparse-attachments");
            device.BeginFrame(); device.BindFramebuffer(target);
            for (int i = 0; i < textures.Length; i++) device.ClearColor(i, (20 + i * 20) / 255f, 60 / 255f, 90 / 255f, 1);
            device.SetDrawBuffers(target, mask);
            Draw(device, target, program, 4);
            for (int i = 0; i < textures.Length; i++)
            {
                byte[] pixels = device.ReadBackLevel0ForTests(textures[i]);
                if (i == 0) Color(pixels, 255, 0, 0);
                else if (i == 4 && (mask & 16) != 0) Color(pixels, 0, 255, 0);
                else Color(pixels, (byte)(20 + i * 20), 60, 90);
            }
            int blend = GpuTest.LinkProgram(device, Triangle, """
                #version 330 core
                layout(location = 0) out vec4 color;
                layout(location = 4) out vec4 motion;
                void main() { color = vec4(0.8, 0.8, 0.8, 0); motion = color; }
                """, "independent-attachment-blend");
            device.SetDrawBuffers(target, 17); device.BindFramebuffer(target); device.UseProgram(blend);
            device.DeclarePass(new Optimum.Render.Vulkan.Graph.PassDeclaration {
                Name = "sparse-compose", FramebufferId = target, ColorSlots = 17,
            });
            device.SetBlend(true, EnumBlendMode.Standard);
            device.SetBlendFuncSeparate(4, 1, 0, 1, 0);
            device.DrawFullscreenTriangle();
            Color(device.ReadBackLevel0ForTests(textures[0]), 255, 0, 0);
            Color(device.ReadBackLevel0ForTests(textures[4]), 204, 204, 204, 0);
            device.Present();
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public unsafe void ThreeOitOutputsReachThreeDifferentArrayLayers()
    {
        using var device = Open();
        int layers = device.CreateTexture2DArray(4, 4, 3,
            EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba);
        int accumulation = device.CreateFramebuffer(4, 4);
        for (int layer = 0; layer < 3; layer++)
            device.AttachTexture(accumulation,
                (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + 3 + layer),
                layers, layer);
        device.SetDrawBuffers(accumulation, 0x38);
        int write = GpuTest.LinkProgram(device, Triangle, """
            #version 330 core
            layout(location = 3) out vec4 red;
            layout(location = 4) out vec4 green;
            layout(location = 5) out vec4 blue;
            void main() {
                red = vec4(1, 0, 0, 1);
                green = vec4(0, 1, 0, 1);
                blue = vec4(0, 0, 1, 1);
            }
            """, "oit-array-write");
        int inspect = GpuTest.LinkProgram(device, Triangle, """
            #version 330 core
            uniform sampler2DArray source;
            out vec4 color;
            void main() {
                int layer = gl_FragCoord.x < 1.0 ? 0 : (gl_FragCoord.x < 2.0 ? 1 : 2);
                color = texelFetch(source, ivec3(0, 0, layer), 0);
            }
            """, "oit-array-inspect");
        int result = Target(device, 3, 1, Texture(device, 3, 1));
        device.SetSamplerUnit(inspect, "source", 0);
        device.BeginFrame();
        Draw(device, accumulation, write, 4);
        device.BindTexture(0, layers);
        device.BindFramebuffer(result); device.SetViewport(0, 0, 3, 1);
        device.SetDepthTest(false); device.SetCullFace(false);
        device.SetBlend(false, EnumBlendMode.Standard); device.UseProgram(inspect);
        device.DrawFullscreenTriangle();
        var pixels = new byte[12];
        fixed (byte* pointer = pixels) device.ReadDefaultFramebuffer(0, 0, 3, 1, (IntPtr)pointer);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255 }, pixels);
        device.Present();
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public void BoundDepthCanBeSampledAfterAWriteWithoutChangingItsContents()
    {
        var device = Open();
        try
        {
            int color = Texture(device, 4, 4), depth = device.CreateTexture2DRaw(4, 4, 0x8CAC, IntPtr.Zero, 4);
            int target = Target(device, 4, 4, color);
            device.AttachTexture(target, EnumFramebufferAttachment.DepthAttachment, depth, 0);
            int write = GpuTest.LinkProgram(device, """
                #version 330 core
                void main() { gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2), -1 + ((gl_VertexID & 2) << 1), 0.5, 1); }
                """, """
                #version 330 core
                out vec4 color;
                void main() { color = vec4(1); }
                """, "depth-write");
            int sample = GpuTest.LinkProgram(device, Triangle, """
                #version 330 core
                uniform sampler2D depthTex;
                out vec4 color;
                void main() { color = vec4(texelFetch(depthTex, ivec2(gl_FragCoord.xy), 0).r, 0, 0, 1); }
                """, "depth-read-only");
            device.SetSamplerUnit(sample, "depthTex", 0);
            device.BeginFrame(); device.BindFramebuffer(target); device.SetViewport(0, 0, 4, 4);
            device.SetCullFace(false); device.SetBlend(false, EnumBlendMode.Standard);
            device.SetDepthTest(true); device.SetDepthMask(true); device.SetDepthFunc(0x207);
            device.ClearDepth(1); device.UseProgram(write); device.DrawFullscreenTriangle();
            device.SetDepthMask(false); device.BindTexture(0, depth); device.UseProgram(sample);
            device.DrawFullscreenTriangle();
            Color(Read(device, target, 4), 191, 0, 0);
            float[] values = MemoryMarshal.Cast<byte, float>(device.ReadBackLevel0ForTests(depth)).ToArray();
            Assert.All(values, value => Assert.Equal(0.75f, value));
            device.Present();
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }
    [Fact]
    public unsafe void PoisonFillsPartialHostWordsAndRequiresAnEnabledSetting()
    {
        foreach (string? disabled in new string?[] { null, "", "0" }) Assert.False(VulkanContext.PoisonRequested(disabled));
        foreach (string enabled in new[] { "1", " 1 " }) Assert.True(VulkanContext.PoisonRequested(enabled));
        var bytes = new byte[11];
        fixed (byte* pointer = bytes) VulkanPoison.FillHostMemory((IntPtr)pointer, (ulong)bytes.Length);
        Assert.Equal(new byte[] { 0xEF, 0xBE, 0xAD, 0xDE, 0xEF, 0xBE, 0xAD, 0xDE, 0xEF, 0xBE, 0xAD }, bytes);
    }

    [SkippableFact]
    public unsafe void PoisonSurvivesAttachmentLoadsAndIsReplacedByClears()
    {
        var device = GpuTest.CreateDevice(output, d => {
            var configure = d.ConfigureContextOptions;
            d.ConfigureContextOptions = options => { configure?.Invoke(options); options.Poison = true; };
        });
        try
        {
            Assert.True(device.ContextForTests.PoisonFreshResources);
            using (var buffer = new VulkanBuffer(device.ContextForTests, 68, BufferUsageFlags.TransferDstBit,
                       MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit))
            {
                var words = new ReadOnlySpan<uint>((void*)buffer.Mapped, 17);
                Assert.Equal(-1, words.IndexOfAnyExcept(0xDEADBEEFu));
            }
            Format[] formats = { Format.R8G8B8A8Unorm, Format.R8G8B8A8Srgb, Format.R16G16B16A16Sfloat,
                Format.R32Sfloat, Format.R32Uint, Format.D32Sfloat };
            int[] textures = formats.Select(format => device.TexturesForTests.Create(8, 8, format)).ToArray();
            int target = Target(device, 8, 8, textures[..5]);
            device.AttachTexture(target, EnumFramebufferAttachment.DepthAttachment, textures[5], 0);
            int program = GpuTest.LinkProgram(device, Triangle, "#version 330 core\nvoid main() {}", "poison-load");
            device.BeginFrame(); device.SetDepthMask(false); Draw(device, target, program, 8);
            for (int kind = 0; kind < textures.Length; kind++)
            {
                byte[] bytes = device.ReadBackLevel0ForTests(textures[kind]);
                Assert.Equal(8 * 8 * (kind == 2 ? 8 : 4), bytes.Length);
                if (kind < 2) { Color(bytes, 255, 0, 255); continue; }
                if (kind == 2)
                {
                    foreach (Half value in MemoryMarshal.Cast<byte, Half>(bytes)) Assert.True(Half.IsNaN(value));
                }
                else if (kind == 3)
                {
                    foreach (float value in MemoryMarshal.Cast<byte, float>(bytes)) Assert.True(float.IsNaN(value));
                }
                else if (kind == 4) Assert.Equal(-1, MemoryMarshal.Cast<byte, uint>(bytes).IndexOfAnyExcept(0xDEADBEEFu));
                else Assert.Equal(-1, MemoryMarshal.Cast<byte, float>(bytes).IndexOfAnyExcept(0.5f));
            }
            device.BindFramebuffer(target); device.SetDepthMask(true);
            device.ClearColor(0, 0.25f, 0.2f, 0.75f, 1); device.ClearDepth(1);
            Color(device.ReadBackLevel0ForTests(textures[0]), 64, 51, 191);
            Assert.Equal(-1, MemoryMarshal.Cast<byte, float>(device.ReadBackLevel0ForTests(textures[5])).IndexOfAnyExcept(1f));
            device.Present();
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

}
