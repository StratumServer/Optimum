using System;
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

public class ShaderBindingTests(ITestOutputHelper output)
{
    private const string Triangle = """
        #version 330 core
        void main() { gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2), -1 + ((gl_VertexID & 2) << 1), 0, 1); }
        """;
    private sealed class Clock : ITimelineClock
    {
        public ulong FrameRecorded { get; set; }
        public ulong FrameCompleted { get; set; }
        public ulong TransferRecorded { get; set; }
        public ulong TransferCompleted { get; set; }
    }

    [Fact]
    public void BindlessSlotsRemainReservedUntilTheirRetirementCompletes()
    {
        var clock = new Clock { FrameRecorded = 7, FrameCompleted = 5 };
        var book = new BindlessSlotBook(clock, Enumerable.Repeat(3u, BindlessKinds.Count).ToArray());
        BindlessSlotKey Key(ulong id) => new(id, TextureKind.Texture2D, SamplerState.Default, ImageLayout.ShaderReadOnlyOptimal);
        uint first = book.Acquire(Key(1), out bool created);
        Assert.True(created); Assert.NotEqual(0u, first);
        Assert.Equal(first, book.Acquire(Key(1), out created)); Assert.False(created);
        Assert.Equal(1, book.Release(1));
        uint second = book.Acquire(Key(2), out _);
        Assert.NotEqual(first, second); Assert.NotEqual(0u, second);
        Assert.Equal(0u, book.Acquire(Key(3), out _)); Assert.Equal(1, book.Exhausted);
        var freed = new List<(TextureKind, uint)>();
        clock.FrameCompleted = 6; Assert.Equal(0, book.Collect(freed));
        clock.FrameCompleted = 7; Assert.Equal(1, book.Collect(freed));
        Assert.Equal(new[] { (TextureKind.Texture2D, first) }, freed);
        Assert.Equal(first, book.Acquire(Key(3), out _));
    }

    [Fact]
    public void SamplerVariantsAreDistinctAndEvictTheLeastRecentlyUsedVariant()
    {
        var book = new BindlessSlotBook(new Clock { FrameRecorded = 1 }, Enumerable.Repeat(32u, BindlessKinds.Count).ToArray());
        BindlessSlotKey Key(int bias) => new(9, TextureKind.Texture2D, SamplerState.Default with { LodBias = bias }, ImageLayout.ShaderReadOnlyOptimal);
        uint first = book.Acquire(Key(0), out _);
        var slots = new HashSet<uint> { first };
        for (int i = 1; i < BindlessSlotBook.MaxVariantsPerTexture; i++) Assert.True(slots.Add(book.Acquire(Key(i), out _)));
        Assert.Equal(first, book.Acquire(Key(0), out _));
        book.Acquire(Key(BindlessSlotBook.MaxVariantsPerTexture), out _);
        Assert.Equal(1, book.PendingRetirements);
        Assert.Equal(first, book.Acquire(Key(0), out bool created)); Assert.False(created);
        book.Acquire(Key(1), out created); Assert.True(created);
    }

    private VulkanDevice Open(bool poison = false) => GpuTest.CreateDevice(output, device => {
        var configure = device.ConfigureContextOptions;
        device.ConfigureContextOptions = options => { configure?.Invoke(options); options.Poison = poison; };
    });

    private static unsafe int Texture(VulkanDevice device, byte r, byte g, byte b)
    {
        byte[] pixel = { r, g, b, 255 };
        fixed (byte* pointer = pixel) return device.CreateTexture2DRaw(1, 1, 0x8058, (IntPtr)pointer, 4);
    }

    private static (int Image, int Target) Target(VulkanDevice device)
    {
        int image = device.CreateTexture2DRaw(4, 4, 0x8058, IntPtr.Zero, 4), target = device.CreateFramebuffer(4, 4);
        device.AttachTexture(target, EnumFramebufferAttachment.ColorAttachment0, image, 0);
        device.SetDrawBuffers(target, 1);
        return (image, target);
    }

    private static void Draw(VulkanDevice device, int program, int target)
    {
        device.BindFramebuffer(target); device.UseProgram(program); device.SetViewport(0, 0, 4, 4);
        device.SetDepthTest(false); device.SetDepthMask(false); device.SetCullFace(false);
        device.SetBlend(false, EnumBlendMode.Standard); device.DrawFullscreenTriangle();
    }

    private static void Pixels(VulkanDevice device, int image, byte red, byte green, byte blue)
    {
        byte[] pixels = device.ReadBackLevel0ForTests(image);
        Assert.Equal(4 * 4 * 4, pixels.Length);
        byte[] expected = { red, green, blue, 255 };
        for (int i = 0; i < pixels.Length; i++) Assert.InRange((int)pixels[i], Math.Max(0, expected[i % 4] - 1), Math.Min(255, expected[i % 4] + 1));
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextureReplacementAndWrongTypesUseTheCorrectBindlessSlots(bool poison)
    {
        var device = Open(poison);
        try
        {
            int program = GpuTest.LinkProgram(device, Triangle, """
                #version 330 core
                uniform sampler2D image;
                out vec4 color;
                void main() { color = texture(image, vec2(0.5)); }
                """, "binding-lifetime");
            device.SetSamplerUnit(program, "image", 0);
            var first = Target(device); var replacement = Target(device); var placeholder = Target(device);
            var table = device.BindlessForTests;
            int red = Texture(device, 255, 0, 0);
            device.BeginFrame();
            uint retiredSlot = table.Resolve(red, TextureKind.Texture2D, SamplerState.Default);
            device.BindTexture(0, red); Draw(device, program, first.Target);
            device.DeleteTexture(red);
            int green = Texture(device, 0, 255, 0);
            Assert.Equal(red, green);
            uint newSlot = table.Resolve(green, TextureKind.Texture2D, SamplerState.Default);
            Assert.NotEqual(retiredSlot, newSlot); Assert.NotEqual(0u, newSlot);
            device.BindTexture(0, green); Draw(device, program, replacement.Target);
            device.BindTexture(0, 0); Draw(device, program, placeholder.Target);
            device.Present();
            for (int i = 0; i < 4; i++) { device.BeginFrame(); device.Present(); }
            Assert.Equal(0, table.PendingRetirements);
            device.BeginFrame();
            Pixels(device, first.Image, 255, 0, 0); Pixels(device, replacement.Image, 0, 255, 0);
            Pixels(device, placeholder.Image, poison ? (byte)255 : (byte)0, 0, poison ? (byte)255 : (byte)0);
            foreach (var wrong in new[] { TextureKind.Shadow2D, TextureKind.Texture2DArray, TextureKind.TextureCube,
                         TextureKind.Texture3D, TextureKind.SignedTexture2D, TextureKind.UnsignedTexture2D })
                Assert.Equal(0u, table.Resolve(green, wrong, SamplerState.Default));
            int blue = Texture(device, 0, 0, 255);
            Assert.Equal(retiredSlot, table.Resolve(blue, TextureKind.Texture2D, SamplerState.Default));
            device.BindTexture(0, blue); Draw(device, program, first.Target); Pixels(device, first.Image, 0, 0, 255);
            var held = device.TexturesForTests.Get(blue)!;
            device.DeleteTexture(blue);
            Assert.Equal(0u, table.Resolve(held, TextureKind.Texture2D, SamplerState.Default));
            device.Present();
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public void SwitchingProgramsKeepsSamplerAssignmentsAndDrawSnapshots()
    {
        var device = Open();
        try
        {
            int single = GpuTest.LinkProgram(device, Triangle, """
                #version 330 core
                uniform sampler2D colour;
                out vec4 color;
                void main() { color = texture(colour, vec2(0.5)); }
                """, "single-sampler");
            int pair = GpuTest.LinkProgram(device, Triangle, """
                #version 330 core
                uniform sampler2D first;
                uniform sampler2D second;
                uniform float weight;
                out vec4 color;
                void main() { color = mix(texture(first, vec2(0.5)), texture(second, vec2(0.5)), weight); }
                """, "paired-samplers");
            device.SetSamplerUnit(single, "colour", 0);
            device.SetSamplerUnit(pair, "first", 1); device.SetSamplerUnit(pair, "second", 2);
            device.SetUniform(pair, device.GetUniformLocation(pair, "weight"), 1f);
            int red = Texture(device, 255, 0, 0), green = Texture(device, 0, 255, 0), blue = Texture(device, 0, 0, 255);
            var targets = Enumerable.Range(0, 4).Select(_ => Target(device)).ToArray();
            device.BeginFrame();
            var textureBinds = device.TextureSetBindsForTests;
            var frameBinds = device.FrameSetBindsForTests;
            device.BindTexture(0, red); device.BindTexture(1, green); device.BindTexture(2, blue);
            Draw(device, single, targets[0].Target); Draw(device, pair, targets[1].Target);
            device.BindTexture(0, green); Draw(device, single, targets[2].Target);
            device.BindTexture(2, red); Draw(device, pair, targets[3].Target);
            Assert.InRange(device.TextureSetBindsForTests - textureBinds, 0, 1);
            Assert.Equal(frameBinds, device.FrameSetBindsForTests);
            Pixels(device, targets[0].Image, 255, 0, 0); Pixels(device, targets[1].Image, 0, 0, 255);
            Pixels(device, targets[2].Image, 0, 255, 0); Pixels(device, targets[3].Image, 255, 0, 0);
            device.Present();
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public unsafe void ShadowSamplingUsesComparisonAndTheFarPlanePlaceholder()
    {
        var device = Open();
        try
        {
            float depth = 0.5f;
            int texture = device.CreateTexture2DRaw(1, 1, 0x8CAC, (IntPtr)(&depth), 4);
            int program = GpuTest.LinkProgram(device, Triangle, """
                #version 330 core
                uniform sampler2DShadow probeDepth;
                uniform float referenceDepth;
                out vec4 color;
                void main() { color = vec4(texture(probeDepth, vec3(0.5, 0.5, referenceDepth)), 0, 0, 1); }
                """, "shadow-slot");
            device.SetSamplerUnit(program, "probeDepth", 0);
            int reference = device.GetUniformLocation(program, "referenceDepth");
            var targets = Enumerable.Range(0, 3).Select(_ => Target(device)).ToArray();
            device.BeginFrame();
            for (int i = 0; i < 3; i++)
            {
                device.BindTexture(0, i == 2 ? 0 : texture);
                device.SetUniform(program, reference, i == 0 ? 0.25f : 0.75f);
                Draw(device, program, targets[i].Target);
            }
            for (int i = 0; i < 3; i++) Pixels(device, targets[i].Image, i == 1 ? (byte)0 : (byte)255, 0, 0);
            device.Present();
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public void FrameGlobalsAreSharedOnlyByProgramsWithTheirOwningInclude()
    {
        var device = Open();
        try
        {
            int Link(bool shared)
            {
                var vertex = new Shader(EnumShaderType.VertexShader, Triangle, "globals.vsh");
                var fragment = new Shader(EnumShaderType.FragmentShader, """
                    #version 330 core
                    uniform float zNear;
                    uniform float tint;
                    out vec4 color;
                    void main() { color = vec4(zNear, tint, 0, 1); }
                    """, "globals.fsh");
                Assert.True(device.CompileShader(vertex)); Assert.True(device.CompileShader(fragment));
                var program = new ShaderProgram { PassName = "binding-globals", VertexShader = vertex, FragmentShader = fragment };
                if (shared) program.includes.Add("fogandlight.fsh");
                int id = device.LinkProgram(program); Assert.True(id > 0, device.GetError()); return id;
            }
            int writer = Link(true), reader = Link(true), isolated = Link(false);
            device.SetUniform(writer, device.GetUniformLocation(writer, "zNear"), 0.25f);
            device.SetUniform(reader, device.GetUniformLocation(reader, "tint"), 0.75f);
            device.SetUniform(isolated, device.GetUniformLocation(isolated, "zNear"), 0.5f);
            device.SetUniform(isolated, device.GetUniformLocation(isolated, "tint"), 0.25f);
            var targets = Enumerable.Range(0, 3).Select(_ => Target(device)).ToArray();
            device.BeginFrame();
            Draw(device, reader, targets[0].Target); Draw(device, isolated, targets[1].Target);
            device.SetUniform(writer, device.GetUniformLocation(writer, "zNear"), 0.75f);
            Draw(device, reader, targets[2].Target);
            Pixels(device, targets[0].Image, 64, 191, 0); Pixels(device, targets[1].Image, 128, 64, 0);
            Pixels(device, targets[2].Image, 191, 191, 0); device.Present();
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public unsafe void AnimationStorageKeepsEachDrawsSnapshot()
    {
        var device = Open();
        try
        {
            int program = GpuTest.LinkProgram(device, """
                #version 330 core
                layout(std140) uniform Animation { mat4 values[4]; } bones;
                out vec4 tint;
                void main() {
                    gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2), -1 + ((gl_VertexID & 2) << 1), 0, 1);
                    tint = bones.values[2][3];
                }
                """, """
                #version 330 core
                in vec4 tint;
                out vec4 color;
                void main() { color = tint; }
                """, "animation-snapshots");
            int block = device.CreateUniformBuffer(program, 0, "Animation", 256);
            var targets = Enumerable.Range(0, 4).Select(_ => Target(device)).ToArray();
            device.BeginFrame();
            for (int i = 0; i < targets.Length; i++)
            {
                var values = new float[64]; values[44] = (20 + i * 60) / 255f; values[45] = 1; values[47] = 1;
                fixed (float* pointer = values) device.UpdateUniformBuffer(block, (IntPtr)pointer, 0, 256);
                Draw(device, program, targets[i].Target);
            }
            for (int i = 0; i < targets.Length; i++) Pixels(device, targets[i].Image, (byte)(20 + i * 60), 255, 0);
            device.Present();
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableFact]
    public unsafe void RetainedFrameTextureIsReadableAfterBeingAnAttachment()
    {
        var device = Open();
        try
        {
            float initial = 0.25f;
            int depth = device.CreateTexture2DRaw(1, 1, 0x8CAC, (IntPtr)(&initial), 4);
            var target = Target(device);
            int depthTarget = device.CreateFramebuffer(1, 1);
            device.AttachTexture(depthTarget, EnumFramebufferAttachment.DepthAttachment, depth, 0);
            int reader = GpuTest.LinkProgram(device, Triangle, """
                #version 330 core
                uniform sampler2D liquidDepth;
                out vec4 color;
                void main() { color = vec4(texture(liquidDepth, vec2(0.5)).r, 0, 0, 1); }
                """, "retained-frame-texture");
            int writer = GpuTest.LinkProgram(device, Triangle, "#version 330 core\nvoid main() {}", "depth-only");
            device.SetSamplerUnit(reader, "liquidDepth", 0);
            device.BeginFrame(); device.BindTexture(0, depth); Draw(device, reader, target.Target);
            device.BindTexture(0, 0); device.BindFramebuffer(depthTarget); device.UseProgram(writer);
            device.SetViewport(0, 0, 1, 1); device.SetDepthTest(true); device.SetDepthMask(true); device.SetDepthFunc(0x207);
            device.DrawFullscreenTriangle();
            var pipeline = device.RequestNativePipeline(new NativePipelineDescription {
                ProgramId = reader, Blend = new[] { AttachmentBlend.For(false, EnumBlendMode.Standard) },
                Targets = device.NativeTargetFormats(target.Target, 1)!,
            }, out string error);
            Assert.NotNull(pipeline);
            Assert.True(device.BeginNativePass(new NativePassDescription { Name = "retained-frame-read", FramebufferId = target.Target }));
            Assert.True(device.DrawNativeFullscreen(pipeline!, ReadOnlySpan<NativeTexture>.Empty), error);
            device.EndNativePass(); Pixels(device, target.Image, 128, 0, 0); device.Present();
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }
}
