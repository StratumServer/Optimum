using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

public class TemporalFilterTests(ITestOutputHelper output)
{
    private const int Size = 32;
    private const string Triangle = """
        #version 330 core
        void main() { gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2), -1 + ((gl_VertexID & 2) << 1), 0, 1); }
        """;

    private static int Target(VulkanDevice device, int texture)
    {
        int target = device.CreateFramebuffer(Size, Size);
        device.AttachTexture(target, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
        device.SetDrawBuffers(target, 1); return target;
    }

    private static unsafe int Texture(VulkanDevice device, Half[]? values = null)
    {
        fixed (Half* pointer = values) return device.CreateTexture2DRaw(Size, Size, 0x881A, (IntPtr)pointer, 8);
    }

    private static void Draw(VulkanDevice device, int program, int target)
    {
        device.BindFramebuffer(target); device.SetViewport(0, 0, Size, Size);
        device.SetDepthTest(false); device.SetDepthMask(false); device.SetCullFace(false);
        device.SetBlend(false, EnumBlendMode.Standard); device.UseProgram(program); device.DrawFullscreenTriangle();
    }

    [SkippableFact]
    public void MotionWriterPreservesReactiveAndRejectsBehindCameraPositions()
    {
        var device = GpuTest.CreateDevice(output);
        try
        {
            string fragment = "#version 330 core\n" + NativeShaderTree.Read("motion.glsl") + """
                uniform float previousW;
                uniform vec2 jitter;
                uniform int reactiveOnly;
                out vec4 color;
                void main() {
                    vec2 previousNdc = ((gl_FragCoord.xy + vec2(3, -2)) / 32.0) * 2.0 - 1.0;
                    vec4 previous = vec4(previousNdc * previousW, 0.5 * previousW, previousW);
                    color = reactiveOnly != 0 ? optimumWriteReactiveOnly(0.7)
                        : optimumWriteMotion(previous, vec2(32), jitter, 0.3, 0.625);
                }
                """;
            int program = GpuTest.LinkProgram(device, Triangle, fragment, "motion-contract");
            int image = Texture(device), target = Target(device, image);
            (float W, float X, float Y, int ReactiveOnly)[] cases = {
                (2f, 0f, 0f, 0), (2f, 0.25f, -0.375f, 0), (-1f, 0.25f, -0.375f, 0),
                (0f, -0.5f, 0.125f, 0), (1e-6f, 0.25f, -0.375f, 0), (2f, -0.5f, 0.125f, 0), (2f, 0f, 0f, 1),
            };
            foreach (var c in cases)
            {
                device.BeginFrame(); device.BindFramebuffer(target); device.ClearColor(0, 0.75f, 0.75f, 0.75f, 0.75f);
                device.SetUniform(program, device.GetUniformLocation(program, "previousW"), c.W);
                device.SetUniform(program, device.GetUniformLocation(program, "jitter"), c.X, c.Y);
                device.SetUniform(program, device.GetUniformLocation(program, "reactiveOnly"), c.ReactiveOnly);
                Draw(device, program, target);
                var values = MemoryMarshal.Cast<byte, Half>(device.ReadBackLevel0ForTests(image));
                Assert.Equal(Size * Size * 4, values.Length);
                bool valid = c.W > 1e-6f && c.ReactiveOnly == 0;
                float[] expected = { valid ? 3 + c.X : 0, valid ? -2 + c.Y : 0,
                    c.ReactiveOnly == 0 ? 0.3f : 0.7f, valid ? 0.625f : 0 };
                for (int i = 0; i < values.Length; i++)
                    Assert.InRange((float)values[i], expected[i % 4] - 0.002f, expected[i % 4] + 0.002f);
                device.Present();
            }
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharpenPreservesHdrBypassAndBoundsEdgeAndNoiseAmplification(bool native)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        string root = Path.Combine(Path.GetTempPath(), "optimum-temporal-filter-" + Guid.NewGuid().ToString("N"));
        VulkanDevice? device = null;
        try
        {
            if (native)
            {
                using var compiler = new ShaderCompiler();
                var build = new NativeShaderBuilder(compiler).Build(Path.Combine(ShaderCorpus.RepositoryRoot, "sources", "shaders-vk"), "taa-sharpen");
                Assert.True(build.Success, string.Join("\n", build.Errors)); NativeShaderBuilder.Write(build, root);
            }
            device = GpuTest.CreateDevice(output, d => {
                d.NativeShadersEnabled = native; d.IgnoreModShaderScan = true;
                d.NativeShaderDirectory = Path.Combine(root, NativeShaderManifest.DirectoryName);
            });
            var source = ShaderCorpus.BuildProgram("taa-sharpen", ShaderCorpus.LoadShaderFiles(), ShaderCorpus.LoadIncludes(), ShaderCorpus.Variants().First());
            var linked = new GpuTest.TestProgram { PassName = "taa-sharpen" };
            foreach (var stage in source)
            {
                var shader = new GpuTest.TestShader { Type = stage.Stage, Code = stage.Code, PrefixCode = stage.PrefixCode };
                Assert.True(device.CompileShader(shader), device.GetError());
                if (stage.Stage == EnumShaderType.VertexShader) linked.VertexShader = shader;
                else if (stage.Stage == EnumShaderType.FragmentShader) linked.FragmentShader = shader;
            }
            int program = device.LinkProgram(linked); Assert.True(program > 0, device.GetError());
            Assert.Equal(native, device.IsNativeProgram(program));
            device.SetSamplerUnit(program, "inputScene", 0);
            device.SetUniform(program, device.GetUniformLocation(program, "inputTexelSize"), 1f / Size, 1f / Size);
            int sharpness = device.GetUniformLocation(program, "sharpness"); Assert.True(sharpness >= 0);
            int image = Texture(device), target = Target(device, image);
            Half[] Pattern(Func<int, int, int, float> value) => Enumerable.Range(0, Size * Size * 4)
                .Select(i => (Half)value(i / 4 % Size, i / 4 / Size, i % 4)).ToArray();
            byte[] Run(Half[] values, float strength)
            {
                int input = Texture(device, values);
                device.SetTextureParameter(input, 0x2801, 9729); device.SetTextureParameter(input, 0x2800, 9729);
                device.SetTextureParameter(input, 0x2802, 33071); device.SetTextureParameter(input, 0x2803, 33071);
                device.BeginFrame(); device.BindTexture(0, input); device.SetUniform(program, sharpness, strength);
                Draw(device, program, target); byte[] result = device.ReadBackLevel0ForTests(image);
                device.Present(); device.DeleteTexture(input); Assert.Equal(Size * Size * 8, result.Length); return result;
            }
            float Pixel(byte[] bytes, int x, int y, int channel = 0) => (float)BitConverter.ToHalf(bytes, ((y * Size + x) * 4 + channel) * 2);
            var hdr = Pattern((x, y, c) => c switch { 0 => x / (float)Size, 1 => y / (float)Size,
                2 => (x + y) % 8 == 0 ? 3.5f : 0.125f, _ => 0.25f });
            Assert.Equal(MemoryMarshal.AsBytes(hdr.AsSpan()).ToArray(), Run(hdr, 0));
            var edge = Pattern((x, y, c) => c == 3 ? 1 : x < 16 ? 0.2f : 0.8f);
            float previousStep = 0;
            foreach (float strength in new[] { 0f, 0.5f, 1f })
            {
                byte[] pixels = Run(edge, strength);
                float dark = Pixel(pixels, 15, 16), bright = Pixel(pixels, 16, 16), step = bright - dark;
                if (strength == 0) Assert.InRange(step, 0.595f, 0.605f);
                else Assert.True(step > previousStep);
                if (strength == 1) { Assert.True(dark < 0.19f); Assert.True(bright > 0.81f); Assert.True(step < 1.2f); }
                for (int y = 4; y < Size - 4; y++)
                {
                    Assert.InRange(Pixel(pixels, 4, y), 0.195f, 0.205f);
                    Assert.InRange(Pixel(pixels, 27, y), 0.795f, 0.805f);
                }
                previousStep = step;
            }
            var noise = Pattern((x, y, c) => c == 3 ? 1 : x == 16 && y == 16 ? 0.6f : 0.3f);
            byte[] filtered = Run(noise, 1);
            Assert.InRange(Pixel(filtered, 16, 16), 0.65f, 0.76f);
            Assert.InRange(Pixel(filtered, 17, 16), 0.22f, 0.3f);
        }
        finally { device?.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        GpuTest.AssertClean(device!);
    }
}
