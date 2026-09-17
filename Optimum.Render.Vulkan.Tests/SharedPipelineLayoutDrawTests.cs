using System;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;
using static Optimum.Render.Vulkan.Tests.GpuTest;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The device's draw path under the one shared pipeline layout (plan decision 9): every
/// rewritten program's samplers are slot indices in the push block over set 1's bindless
/// arrays, its loose uniforms the record in set 2, its named blocks std140 storage
/// buffers in set 2. Readbacks happen after the frame, through the seam.
/// </summary>
public class SharedPipelineLayoutDrawTests
{
    private const int Size = 8;

    private const string FullscreenVertex = """
        #version 330 core
        out vec2 uv;
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
            uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
        }
        """;

    private readonly ITestOutputHelper _output;

    public SharedPipelineLayoutDrawTests(ITestOutputHelper output) => _output = output;

    private static unsafe int SolidTexture(VulkanDevice seam, byte r, byte g, byte b)
    {
        var pixels = new byte[Size * Size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }
        fixed (byte* data = pixels)
        {
            return seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)data, false);
        }
    }

    private static (int Texture, int Framebuffer) Target(VulkanDevice seam)
    {
        int texture = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
        seam.SetDrawBuffers(framebuffer, 1);
        return (texture, framebuffer);
    }

    /// <summary>The centre texel of a target, read from the texture itself (binding a framebuffer between frames is a no-op).</summary>
    private static byte[] Centre(VulkanDevice seam, int texture)
    {
        byte[] pixels = seam.ReadBackLevel0ForTests(texture);
        int centre = (Size / 2 * Size + Size / 2) * 4;
        return pixels[centre..(centre + 3)];
    }

    private static void Draw(VulkanDevice seam, int program, int framebuffer)
    {
        seam.BindFramebuffer(framebuffer);
        seam.UseProgram(program);
        seam.SetViewport(0, 0, Size, Size);
        seam.SetDepthTest(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.DrawFullscreenTriangle();
    }

    /// <summary>
    /// Two programs with different sampler lists (one sampler; two, the first unused by
    /// the second's output mix) drawn alternately into one pass: each draw samples its own
    /// textures through its own push indices, while set 1 is bound once for the recording
    /// and set 0 is never rebound by a program switch - the point of one layout.
    /// </summary>
    [SkippableFact]
    public void TwoProgramsWithDifferentSamplersAlternateInOnePassWithoutRebindingSetsZeroAndOne()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            int single = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                uniform sampler2D colour;
                in vec2 uv;
                out vec4 outColor;
                void main(void) { outColor = texture(colour, uv); }
                """, "shared-single");
            int pair = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                uniform sampler2D first;
                uniform sampler2D second;
                uniform float weight;
                in vec2 uv;
                out vec4 outColor;
                void main(void) { outColor = mix(texture(first, uv), texelFetch(second, ivec2(0), 0), weight); }
                """, "shared-pair");

            int red = SolidTexture(seam, 255, 0, 0);
            int green = SolidTexture(seam, 0, 255, 0);
            int blue = SolidTexture(seam, 0, 0, 255);
            var targets = new (int Texture, int Framebuffer)[4];
            for (int i = 0; i < targets.Length; i++) targets[i] = Target(seam);

            seam.SetSamplerUnit(single, "colour", 0);
            seam.SetSamplerUnit(pair, "first", 1);
            seam.SetSamplerUnit(pair, "second", 2);
            int weight = seam.GetUniformLocation(pair, "weight");
            Assert.True(weight >= 0);
            seam.SetUniform(pair, weight, 1f);

            seam.BeginFrame();
            long textureBinds = seam.TextureSetBindsForTests;
            long frameBinds = seam.FrameSetBindsForTests;
            seam.BindTexture(0, red);
            seam.BindTexture(1, green);
            seam.BindTexture(2, blue);
            Draw(seam, single, targets[0].Framebuffer);
            Draw(seam, pair, targets[1].Framebuffer);
            seam.BindTexture(0, green);
            Draw(seam, single, targets[2].Framebuffer);
            seam.BindTexture(2, red);
            Draw(seam, pair, targets[3].Framebuffer);
            long textureBindsInFrame = seam.TextureSetBindsForTests - textureBinds;
            long frameBindsInFrame = seam.FrameSetBindsForTests - frameBinds;
            seam.Present();

            Assert.Equal(new byte[] { 255, 0, 0 }, Centre(seam, targets[0].Texture));
            Assert.Equal(new byte[] { 0, 0, 255 }, Centre(seam, targets[1].Texture));
            Assert.Equal(new byte[] { 0, 255, 0 }, Centre(seam, targets[2].Texture));
            Assert.Equal(new byte[] { 255, 0, 0 }, Centre(seam, targets[3].Texture));
            // Scopes reopen per target, but a scope is not a recording: set 1 stays bound.
            Assert.True(textureBindsInFrame <= 1, "set 1 bound " + textureBindsInFrame + " times in one recording");
            Assert.Equal(0, frameBindsInFrame);
            AssertClean(seam);
        }
    }

    /// <summary>
    /// entityanimated's Animation block, read as a std140 storage buffer at set 2's
    /// animation binding: two draws in one frame, each with the bone matrix uploaded
    /// right before it, each come out the colour its own upload encodes.
    /// </summary>
    [SkippableFact]
    public unsafe void AnAnimationBlockProgramReadsEachDrawsOwnUpload()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            int program = VulkanDeviceIntegrationTests.LinkProgram(seam, """
                #version 330 core
                layout (std140) uniform Animation
                {
                    mat4 values[4];
                } ElementTransforms;
                out vec4 tint;
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                    tint = ElementTransforms.values[2][3];
                }
                """, """
                #version 330 core
                in vec4 tint;
                out vec4 outColor;
                void main(void) { outColor = tint; }
                """, "shared-animation");

            const int blockBytes = 4 * 64;
            int ubo = seam.CreateUniformBuffer(program, 0, "Animation", blockBytes);
            var first = Target(seam);
            var second = Target(seam);

            void Upload(float r, float g, float b)
            {
                var block = new float[blockBytes / 4];
                // values[2], column 3 (std140: a mat4 is four 16-byte columns).
                int at = 2 * 16 + 3 * 4;
                block[at] = r;
                block[at + 1] = g;
                block[at + 2] = b;
                block[at + 3] = 1f;
                fixed (float* values = block) seam.UpdateUniformBuffer(ubo, (IntPtr)values, 0, blockBytes);
            }

            seam.BeginFrame();
            Upload(1f, 0f, 0f);
            Draw(seam, program, first.Framebuffer);
            Upload(0f, 0f, 1f);
            Draw(seam, program, second.Framebuffer);
            seam.Present();

            Assert.Equal(new byte[] { 255, 0, 0 }, Centre(seam, first.Texture));
            Assert.Equal(new byte[] { 0, 0, 255 }, Centre(seam, second.Texture));
            AssertClean(seam);
        }
    }

    /// <summary>
    /// A transient rebound onto another transient's physical image, and a feedback draw
    /// that samples its own attachment through a ReadSelf copy, both resolve the bindless
    /// slot of the physical texture the draw reads - not a slot of the GL id.
    /// </summary>
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransientRebindsAndFeedbackCopiesSampleThePhysicalTexture(bool aliasing)
    {
        VulkanDevice seam = NewDevice();
        seam.TransientAliasingOverride = aliasing;
        if (!seam.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            _output.WriteLine("Vulkan unavailable: " + failureReason);
            seam.Dispose();
            Skip.If(true, "No usable Vulkan device.");
        }

        using (seam)
        {
            int fill = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                uniform vec4 colourIn;
                out vec4 outColor;
                void main(void) { outColor = colourIn; }
                """, "shared-fill");
            int copy = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                uniform sampler2D src;
                out vec4 outColor;
                void main(void) { outColor = texelFetch(src, ivec2(gl_FragCoord.xy), 0); }
                """, "shared-copy");
            int feedback = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                uniform sampler2D self;
                out vec4 outColor;
                void main(void) { outColor = texelFetch(self, ivec2(gl_FragCoord.xy), 0).gbra; }
                """, "shared-feedback");
            seam.SetSamplerUnit(copy, "src", 0);
            seam.SetSamplerUnit(feedback, "self", 0);
            int colourIn = seam.GetUniformLocation(fill, "colourIn");

            var transients = new (int Texture, int Framebuffer)[3];
            for (int i = 0; i < transients.Length; i++)
            {
                int texture = seam.CreateTransientTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8, 2 + i);
                int framebuffer = seam.CreateFramebuffer(Size, Size);
                seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
                seam.SetDrawBuffers(framebuffer, 1);
                transients[i] = (texture, framebuffer);
            }
            var output = Target(seam);

            byte[] pixels = Array.Empty<byte>();
            for (int frame = 0; frame < 3; frame++)
            {
                seam.BeginFrame();
                // Lifetimes [0,1], [1,2], [2,3]: with aliasing on, transient 2 takes transient 0's image.
                for (int pass = 0; pass < 3; pass++) seam.BindTransientForFrame(transients[pass].Texture, pass, pass + 1);

                seam.SetUniform(fill, colourIn, 1f, 0f, 0f, 1f);
                Draw(seam, fill, transients[0].Framebuffer);
                seam.BindTexture(0, transients[0].Texture);
                Draw(seam, copy, transients[1].Framebuffer);
                seam.BindTexture(0, transients[1].Texture);
                Draw(seam, copy, transients[2].Framebuffer);
                // Feedback: sample transient 2 while drawing into it (red -> green through .gbra).
                seam.BindTexture(0, transients[2].Texture);
                Draw(seam, feedback, transients[2].Framebuffer);
                seam.BindTexture(0, transients[2].Texture);
                Draw(seam, copy, output.Framebuffer);
                seam.BindTexture(0, 0);
                if (frame == 2) pixels = Centre(seam, output.Texture);
                seam.Present();
            }

            Assert.Equal(new byte[] { 0, 0, 255 }, pixels);
            AssertClean(seam);
        }
    }
}
