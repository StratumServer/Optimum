using System;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Drives the backend the way the client will: through
/// <see cref="VulkanDevice" /> and nothing else.
///
/// Every other test in this project reaches past the seam into a specific
/// manager. This one deliberately does not, because the seam is the contract that
/// has to hold - <c>ClientPlatformWindows</c> will only ever see these methods,
/// in this order, with GL's semantics assumed.
/// </summary>
public class VulkanDeviceIntegrationTests
{
    private readonly ITestOutputHelper _output;

    public VulkanDeviceIntegrationTests(ITestOutputHelper output) => _output = output;

    private static bool TryCreateDevice(ITestOutputHelper output, out VulkanDevice? device) =>
        GpuTest.TryCreateDevice(output, out device);

    // Unlike the lower-level TaaResolveTests, allocate through the same raw GL
    // format API as ClientPlatformWindows.CreateOptimumHistoryTarget. A missing
    // GL_R32F mapping used to clamp linear history depth to 1 in an RGBA8 target,
    // so every world surface rejected history even though the shader tests passed.
    [SkippableTheory]
    [InlineData(12f, false)]
    [InlineData(128f, false)]
    [InlineData(12f, true)]
    public unsafe void TaaRetainsDistantHistoryAndRejectsDisocclusionThroughTheSeam(
        float distance, bool disoccluded) => RunTaaResolve(distance, disoccluded, 2, false);

    [SkippableTheory]
    [InlineData(2)]
    [InlineData(4)]
    public void TaaAccumulatesAfterClearingDirtyMaskedMotion(int motionAttachmentIndex) =>
        RunTaaResolve(12f, false, motionAttachmentIndex, true);

    [SkippableFact]
    public unsafe void TemporalHistorySurvivesFramesInFlightWithoutIntermediateReadbacks()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 64, frames = 16;
            string vertex = ShaderCorpus.LoadShaderFiles()["taa-resolve.vsh"];
            int accumulate = LinkProgram(seam, vertex, """
                #version 330 core
                uniform sampler2D historyColor;
                uniform sampler2D historyAux;
                uniform sampler2D historyDepth;
                uniform float increment;
                layout(location = 0) out vec4 color;
                layout(location = 1) out vec4 aux;
                layout(location = 2) out vec4 depth;
                void main() {
                    ivec2 p = ivec2(gl_FragCoord.xy);
                    color = texelFetch(historyColor, p, 0) + vec4(increment);
                    aux = texelFetch(historyAux, p, 0) + vec4(8.0 / 255.0);
                    depth = vec4(texelFetch(historyDepth, p, 0).r + increment);
                    // Keep work in flight while the CPU submits the next frame.
                    // The bound is deliberately data dependent, preventing the
                    // compiler from precomputing the loop for the whole draw.
                    float busy = 0;
                    for (int i = 0; i < 4096 + int(gl_FragCoord.y); ++i)
                        busy += sin(float(i) + gl_FragCoord.x);
                    if (busy > 1e30) color = vec4(busy);
                }
                """);
            int inspect = LinkProgram(seam, vertex, """
                #version 330 core
                uniform sampler2D historyColor;
                uniform sampler2D historyAux;
                uniform sampler2D historyDepth;
                out vec4 color;
                void main() {
                    ivec2 p = ivec2(gl_FragCoord.xy);
                    color = vec4(texelFetch(historyColor, p, 0).r / 8.0,
                                 texelFetch(historyDepth, p, 0).r / 8.0,
                                 texelFetch(historyAux, p, 0).r, 1.0);
                }
                """);

            int Texture(EnumTextureInternalFormat format) => seam.CreateTexture2D(
                size, size, format, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int Target(int[] colors)
            {
                int target = seam.CreateFramebuffer(size, size);
                for (int i = 0; i < colors.Length; ++i)
                    seam.AttachTexture(target, (EnumFramebufferAttachment)(36064 + i), colors[i], 0);
                seam.SetDrawBuffers(target, (1 << colors.Length) - 1);
                return target;
            }
            void BindHistory(int program, int[] textures)
            {
                string[] names = { "historyColor", "historyAux", "historyDepth" };
                for (int i = 0; i < names.Length; ++i)
                {
                    seam.SetSamplerUnit(program, names[i], i + 4);
                    seam.BindTexture(i + 4, textures[i]);
                }
            }

            var histories = new int[2][];
            var targets = new int[2];
            for (int i = 0; i < 2; ++i)
            {
                histories[i] = new[] { Texture(EnumTextureInternalFormat.Rgba16f),
                    Texture(EnumTextureInternalFormat.Rgba8),
                    seam.CreateTexture2DRaw(size, size, 0x822E, IntPtr.Zero, 4) };
                targets[i] = Target(histories[i]);
            }
            int readbackTarget = Target(new[] { Texture(EnumTextureInternalFormat.Rgba8) });
            seam.SetViewport(0, 0, size, size);
            seam.SetDepthTest(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);

            for (int frame = 0; frame < frames; ++frame)
            {
                seam.BeginFrame();
                if (frame == 0)
                {
                    seam.BindFramebuffer(targets[0]);
                    for (int attachment = 0; attachment < 3; ++attachment)
                        seam.ClearColor(attachment, 0, 0, 0, 0);
                }
                seam.BindFramebuffer(targets[(frame + 1) & 1]);
                seam.UseProgram(accumulate);
                BindHistory(accumulate, histories[frame & 1]);
                seam.SetUniform(accumulate, seam.GetUniformLocation(accumulate, "increment"), (frame + 1) / 32f);
                seam.DrawFullscreenTriangle();
                seam.Present();
                // No readback, upload or explicit wait here: these would flush
                // the graphics queue and mask broken history synchronization.
            }

            seam.BeginFrame();
            seam.BindFramebuffer(readbackTarget);
            seam.UseProgram(inspect);
            BindHistory(inspect, histories[frames & 1]);
            seam.DrawFullscreenTriangle();
            byte[] pixels = new byte[size * size * 4];
            fixed (byte* data = pixels)
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)data);
            seam.Present();
            // Sum(1..16)/32 = 4.25 in both float histories; the RGBA8 aux
            // accumulates exactly eight byte values per frame. A stale cached
            // descriptor, missing frame, or overwritten uniform changes these.
            for (int i = 0; i < pixels.Length; i += 4)
            {
                Assert.InRange(pixels[i], 135, 136);
                Assert.InRange(pixels[i + 1], 135, 136);
                Assert.Equal(128, pixels[i + 2]);
                Assert.Equal(255, pixels[i + 3]);
            }
            AssertClean(seam);
        }
    }

    private unsafe void RunTaaResolve(float distance, bool disoccluded,
        int motionAttachmentIndex, bool poisonMotion)
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 8;
            var files = ShaderCorpus.LoadShaderFiles();
            int resolve = LinkProgram(seam, files["taa-resolve.vsh"], files["taa-resolve.fsh"], "taa-resolve");
            int inspect = LinkProgram(seam, files["taa-resolve.vsh"], """
                #version 330 core
                uniform sampler2D colorTex;
                uniform sampler2D linearDepthTex;
                uniform sampler2D motionTex;
                out vec4 color;
                void main() {
                    ivec2 p = ivec2(gl_FragCoord.xy);
                    color = vec4(texelFetch(linearDepthTex, p, 0).r / 256.0,
                                 texelFetch(colorTex, p, 0).r,
                                 any(notEqual(texelFetch(motionTex, p, 0), vec4(0))) ? 1.0 : 0.0, 1.0);
                }
                """);

            int Texture(EnumTextureInternalFormat format) => seam.CreateTexture2D(
                size, size, format, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            void Filter(int texture, int filter)
            {
                seam.SetTextureParameter(texture, OptimumGlConstants.TextureMinFilter, filter);
                seam.SetTextureParameter(texture, OptimumGlConstants.TextureMagFilter, filter);
                seam.SetTextureParameter(texture, OptimumGlConstants.TextureWrapS, 33071);
                seam.SetTextureParameter(texture, OptimumGlConstants.TextureWrapT, 33071);
            }
            int Target(params int[] colors)
            {
                int fbo = seam.CreateFramebuffer(size, size);
                for (int i = 0; i < colors.Length; i++)
                    seam.AttachTexture(fbo, (EnumFramebufferAttachment)(36064 + i), colors[i], 0);
                seam.SetDrawBuffers(fbo, (1 << colors.Length) - 1);
                Assert.True(seam.CheckFramebufferComplete(fbo, out string status), status);
                return fbo;
            }
            void Bind(int program, string name, int texture, int unit)
            {
                seam.SetSamplerUnit(program, name, unit);
                seam.BindTexture(unit, texture);
            }
            int Loc(string name) => seam.GetUniformLocation(resolve, name);
            float[] Identity() => new float[] { 1,0,0,0, 0,1,0,0, 0,0,1,0, 0,0,0,1 };

            int scene = Texture(EnumTextureInternalFormat.Rgba8);
            int glow = Texture(EnumTextureInternalFormat.Rgba8);
            int motion = Texture(EnumTextureInternalFormat.Rgba16f);
            int depth = Texture(EnumTextureInternalFormat.DepthComponent32);
            var primaryColors = new int[motionAttachmentIndex + 1];
            primaryColors[0] = scene;
            primaryColors[1] = glow;
            for (int i = 2; i < motionAttachmentIndex; i++)
                primaryColors[i] = Texture(EnumTextureInternalFormat.Rgba16f);
            primaryColors[motionAttachmentIndex] = motion;
            int primary = Target(primaryColors);
            seam.AttachTexture(primary, EnumFramebufferAttachment.DepthAttachment, depth, 0);
            Filter(depth, 9728);
            var history = new int[2][];
            var framebuffers = new int[2];
            for (int i = 0; i < 2; i++)
            {
                history[i] = new[] { Texture(EnumTextureInternalFormat.Rgba16f),
                    Texture(EnumTextureInternalFormat.Rgba8),
                    seam.CreateTexture2DRaw(size, size, 0x822E, IntPtr.Zero, 4) };
                for (int j = 0; j < 3; j++) Filter(history[i][j], j == 2 ? 9728 : 9729);
                framebuffers[i] = Target(history[i]);
            }
            int readback = Target(Texture(EnumTextureInternalFormat.Rgba8));
            var pixels = new byte[size * size * 4];

            // First frame seeds history. Later frames invert the checkerboard;
            // both colours remain in the neighbourhood clipping box. Retention
            // must blend across the two slots, not simply return current colour.
            for (int frame = 0; frame < 3; frame++)
            {
                for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    byte value = (byte)(((x + y + frame) % 2 == 0) ? 64 : 192);
                    int at = (y * size + x) * 4;
                    pixels[at] = pixels[at + 1] = pixels[at + 2] = value;
                    pixels[at + 3] = 255;
                }
                fixed (byte* data = pixels)
                    seam.UploadTexture2D(scene, 0, 0, 0, size, size, EnumTexturePixelFormat.Rgba, (IntPtr)data);

                seam.BeginFrame();
                seam.BindFramebuffer(primary);
                seam.SetViewport(0, 0, size, size);
                seam.SetDepthMask(true);
                seam.ClearDepth(0.5f);
                seam.ClearColor(1, 0, 0, 0, 0);
                if (poisonMotion)
                {
                    // Recycled GPU memory may contain plausible motion/depth and
                    // full reactivity. Never let zero-filled fresh allocations
                    // hide a skipped clear. P2 keeps this attachment masked out.
                    seam.SetDrawBuffers(primary, (1 << (motionAttachmentIndex + 1)) - 1);
                    seam.ClearColor(motionAttachmentIndex, 16, 8, 1, 1);
                    seam.SetDrawBuffers(primary, (1 << motionAttachmentIndex) - 1);
                }
                // Match ClearFrameBuffer(Primary): the clear obeys the mask,
                // then world draws must again exclude unwritten motion output.
                seam.SetDrawBuffers(primary, (1 << (motionAttachmentIndex + 1)) - 1);
                seam.ClearColor(motionAttachmentIndex, 0, 0, 0, 0);
                seam.SetDrawBuffers(primary, (1 << motionAttachmentIndex) - 1);
                seam.SetDepthTest(false);
                seam.SetCullFace(false);
                seam.SetBlend(false, EnumBlendMode.Standard);
                seam.BindFramebuffer(framebuffers[frame & 1]);
                seam.UseProgram(resolve);
                Bind(resolve, "sceneTex", scene, 0);
                Bind(resolve, "glowTex", glow, 1);
                Bind(resolve, "motionTex", motion, 2);
                Bind(resolve, "depthTex", depth, 3);
                int[] previous = history[(frame + 1) & 1];
                Bind(resolve, "historyColor", previous[0], 4);
                Bind(resolve, "historyGlow", previous[1], 5);
                Bind(resolve, "historyDepth", previous[2], 6);
                seam.SetUniform(resolve, Loc("renderSize"), (float)size, (float)size);
                // Orthographic reprojection with a known linear depth, and a
                // changing subpixel jitter that cancels in static camera motion.
                float jitter = frame % 2 == 0 ? 0.25f : -0.25f;
                seam.SetUniform(resolve, Loc("jitterPx"), jitter, 0f);
                float currentDistance = disoccluded ? distance * (frame + 1) : distance;
                float[] inverse = Identity();
                inverse[12] = -2 * jitter / size;
                inverse[14] = -currentDistance;
                seam.SetUniformMatrix(resolve, Loc("invViewProjJittered"), inverse);
                seam.SetUniformMatrix(resolve, Loc("prevViewProj"), Identity());
                seam.SetUniformMatrix(resolve, Loc("viewMatrix"), Identity());
                seam.SetUniform(resolve, Loc("cameraDelta"), 0f, 0f, 0f);
                seam.SetUniform(resolve, Loc("resetHistory"), frame == 0 ? 1 : 0);
                seam.SetUniform(resolve, Loc("blendAlpha"), 0.1f);
                seam.SetUniform(resolve, Loc("varianceGamma"), 1.25f);
                seam.DrawFullscreenTriangle();

                seam.BindFramebuffer(readback);
                seam.UseProgram(inspect);
                Bind(inspect, "colorTex", history[frame & 1][0], 0);
                Bind(inspect, "linearDepthTex", history[frame & 1][2], 1);
                Bind(inspect, "motionTex", motion, 2);
                seam.DrawFullscreenTriangle();
                fixed (byte* data = pixels)
                    seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)data);
                seam.Present();
                int centre = (4 * size + 4) * 4; // raw RGBA8 attachment readback
                Assert.Equal(0, pixels[centre + 2]); // all four motion channels cleared
                Assert.InRange(pixels[centre], (int)(currentDistance * 255 / 256) - 1,
                    (int)(currentDistance * 255 / 256) + 1);
                if (frame == 1)
                    Assert.InRange(pixels[centre + 1], disoccluded ? 175 : 60, disoccluded ? 195 : 100);
                if (frame == 2)
                    Assert.InRange(pixels[centre + 1], 60, 100);
            }
            AssertClean(seam);
        }
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void TerrainSamplerUsesNearestTexelsAndBlendsMipLevels(bool linear)
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            int program = LinkProgram(seam, """
                #version 330 core
                void main() {
                    gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2),
                                       -1 + ((gl_VertexID & 2) << 1), 0, 1);
                }
                """, """
                #version 330 core
                uniform sampler2D source;
                out vec4 color;
                void main() {
                    float lod = gl_FragCoord.x < 1.0 ? 1.0 : 1.5;
                    color = textureLod(source, vec2(0.625, 0.25), lod);
                }
                """);
            int source = seam.CreateTexture2D(4, 4, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, true);
            // Base level red; mip 1 alternates green/blue, mip 2 is white.
            // Sampling base level, filtering within mip 1, or rounding the LOD
            // produces a different colour from the GL_NEAREST_MIPMAP_LINEAR result.
            byte[] basePixels = new byte[64];
            for (int i = 0; i < basePixels.Length; i += 4)
            {
                basePixels[i] = 255;
                basePixels[i + 3] = 255;
            }
            byte[] mip1 = { 0,255,0,255, 0,0,255,255, 0,255,0,255, 0,0,255,255 };
            byte[] mip2 = { 255,255,255,255 };
            fixed (byte* data = basePixels)
                seam.UploadTexture2D(source, 0, 0, 0, 4, 4, EnumTexturePixelFormat.Rgba, (IntPtr)data);
            fixed (byte* data = mip1)
                seam.UploadTexture2D(source, 1, 0, 0, 2, 2, EnumTexturePixelFormat.Rgba, (IntPtr)data);
            fixed (byte* data = mip2)
                seam.UploadTexture2D(source, 2, 0, 0, 1, 1, EnumTexturePixelFormat.Rgba, (IntPtr)data);
            int target = seam.CreateTexture2D(2, 1, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(2, 1);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(framebuffer, 1);
            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.SetViewport(0, 0, 2, 1);
            seam.SetDepthTest(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.UseProgram(program);
            seam.SetSamplerUnit(program, "source", 0);
            seam.BindTexture(0, source);
            seam.BindSampler(0, seam.CreateSampler(linear));
            seam.DrawFullscreenTriangle();
            seam.Present();
            byte[] pixels = new byte[8];
            fixed (byte* data = pixels)
                seam.ReadDefaultFramebuffer(0, 0, 2, 1, (IntPtr)data);
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, pixels[..4]);
            Assert.InRange(pixels[4], 127, 128);
            Assert.InRange(pixels[5], 127, 128);
            Assert.Equal(255, pixels[6]);
            Assert.Equal(255, pixels[7]);

            // GL_TEXTURE_MAX_LEVEL is a texture property, not sampler state.
            // It must still clamp an override, and must not blend in level 2.
            seam.SetTextureParameter(source, OptimumGlConstants.TextureMaxLevel, 1);
            seam.SetTextureParameter(source, OptimumGlConstants.TextureMinFilter, 0x2702);
            for (int pass = 0; pass < 2; pass++)
            {
                if (pass == 1) seam.BindSampler(0, 0);
                seam.BeginFrame();
                seam.BindFramebuffer(framebuffer);
                seam.DrawFullscreenTriangle();
                seam.Present();
                fixed (byte* data = pixels)
                    seam.ReadDefaultFramebuffer(0, 0, 2, 1, (IntPtr)data);
                Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 0, 255, 255 }, pixels);
            }
            AssertClean(seam);
        }
    }

    /// <summary>
    /// TAA P5 review: the mip-bias row applies live, which means a LOD bias
    /// written into a sampler object that is ALREADY bound to a unit has to reach
    /// the next draw without anything rebinding it.
    ///
    /// That is not obvious on this backend. GL keeps sampler state in the object
    /// the driver dereferences at draw time; here the bias is a field of an
    /// immutable SamplerState that interns into a VkSampler, and the descriptor
    /// set is cached. If the unit's binding were resolved once at BindSampler
    /// time, or the descriptor keyed on the sampler id rather than the resolved
    /// VkSampler, the slider would move OptimumConfig and change nothing on
    /// screen until the next shader reload - the exact failure this pass fixes on
    /// the engine side.
    ///
    /// The source is a 4x4 mip chain with one flat colour per level and the quad
    /// is drawn at exactly one texel per pixel, so lambda is 0 and the level the
    /// GPU reads is the bias alone.
    /// </summary>
    [SkippableFact]
    public unsafe void ALodBiasWrittenToAnAlreadyBoundSamplerChangesTheMipTheGpuReads()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 4;
            int program = LinkProgram(seam, """
                #version 330 core
                void main() {
                    gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2),
                                       -1 + ((gl_VertexID & 2) << 1), 0, 1);
                }
                """, """
                #version 330 core
                uniform sampler2D source;
                out vec4 color;
                void main() {
                    // One texel per pixel on a 4x4 source drawn into a 4x4 target:
                    // the implicit derivative gives lambda = 0, so every level the
                    // readback sees comes from the sampler's LOD bias.
                    color = texture(source, gl_FragCoord.xy / 4.0);
                }
                """);
            int source = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, true);
            byte[] level0 = new byte[size * size * 4];
            byte[] level1 = new byte[2 * 2 * 4];
            byte[] level2 = new byte[4];
            for (int i = 0; i < level0.Length; i += 4) { level0[i] = 255; level0[i + 3] = 255; }
            for (int i = 0; i < level1.Length; i += 4) { level1[i + 1] = 255; level1[i + 3] = 255; }
            level2[2] = 255; level2[3] = 255;
            fixed (byte* data = level0)
                seam.UploadTexture2D(source, 0, 0, 0, size, size, EnumTexturePixelFormat.Rgba, (IntPtr)data);
            fixed (byte* data = level1)
                seam.UploadTexture2D(source, 1, 0, 0, 2, 2, EnumTexturePixelFormat.Rgba, (IntPtr)data);
            fixed (byte* data = level2)
                seam.UploadTexture2D(source, 2, 0, 0, 1, 1, EnumTexturePixelFormat.Rgba, (IntPtr)data);

            int target = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(framebuffer, 1);

            // GenSampler's own state, as ShaderRegistry.SetCustomSampler creates
            // it for terrainTex; bound once and never rebound below.
            int sampler = seam.CreateSampler(linear: false);

            byte[] pixels = new byte[size * size * 4];
            byte[] Draw()
            {
                seam.BeginFrame();
                seam.BindFramebuffer(framebuffer);
                seam.SetViewport(0, 0, size, size);
                seam.SetDepthTest(false);
                seam.SetCullFace(false);
                seam.SetBlend(false, EnumBlendMode.Standard);
                seam.UseProgram(program);
                seam.SetSamplerUnit(program, "source", 0);
                seam.BindTexture(0, source);
                seam.DrawFullscreenTriangle();
                seam.Present();
                fixed (byte* data = pixels)
                    seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)data);
                return pixels;
            }

            seam.BindSampler(0, sampler);
            byte[] unbiased = (byte[])Draw().Clone();
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, unbiased[..4]);

            // The slider's move: the sampler object is already bound to unit 0
            // and nothing rebinds it.
            seam.SetSamplerParameter(sampler, OptimumGlConstants.TextureLodBias, 1f);
            byte[] biased = (byte[])Draw().Clone();
            Assert.Equal(new byte[] { 0, 255, 0, 255 }, biased[..4]);

            // And back, so the effect is the bias and not a one-way cache miss.
            seam.SetSamplerParameter(sampler, OptimumGlConstants.TextureLodBias, 0f);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, Draw()[..4]);

            AssertClean(seam);
        }
    }

    [SkippableTheory]
    [InlineData(EnumDrawMode.Lines)]
    [InlineData(EnumDrawMode.LineStrip)]
    public unsafe void IndexedLineMeshesDrawOnlyEdgesAndRestoreTriangleTopology(EnumDrawMode mode)
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 32;
            int program = LinkProgram(seam, """
                #version 330 core
                layout(location = 0) in vec3 position;
                void main() { gl_Position = vec4(position, 1); }
                """, """
                #version 330 core
                out vec4 color;
                void main() { color = vec4(1); }
                """);
            // Deliberately shuffled vertices: drawing without these indices
            // introduces diagonals through the otherwise empty box interior.
            var data = new MeshData(4, 8) {
                xyz = new float[] { -.75f, -.75f, 0, .75f, .75f, 0,
                                    .75f, -.75f, 0, -.75f, .75f, 0 },
                VerticesCount = 4,
                Indices = mode == EnumDrawMode.Lines
                    ? new[] { 0, 2, 2, 1, 1, 3, 3, 0 }
                    : new[] { 0, 2, 1, 3, 0 },
                IndicesCount = mode == EnumDrawMode.Lines ? 8 : 5,
                mode = mode
            };
            int mesh = seam.CreateMesh(data, true);
            int texture = seam.CreateTexture2D(size, size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
            seam.SetDrawBuffers(framebuffer, 1);
            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.UseProgram(program);
            seam.SetViewport(0, 0, size, size);
            seam.SetDepthTest(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.SetLineWidth(1);
            seam.ClearColor(0, 0, 0, 0, 1);
            seam.DrawMeshInstanced(mesh, 1);
            seam.Present();
            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            int lit = 0;
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                byte red = pixels[(y * size + x) * 4];
                if (red != 0) lit++;
                if (x >= 8 && x < 24 && y >= 8 && y < 24) Assert.Equal(0, red);
            }
            Assert.InRange(lit, 80, 112);

            // A following triangle mesh must change topology class again.
            data.mode = EnumDrawMode.Triangles;
            data.Indices = new[] { 0, 2, 1, 0, 1, 3 };
            data.IndicesCount = 6;
            int triangles = seam.CreateMesh(data, true);
            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.DrawMesh(triangles);
            seam.Present();
            fixed (byte* destination = pixels)
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            Assert.Equal(255, pixels[(16 * size + 16) * 4]);
            AssertClean(seam);
        }
    }

    [SkippableFact]
    public unsafe void CloudMapShortUploadsKeepFullDensityAndBrightness()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            int program = LinkProgram(seam, """
                #version 330 core
                void main() {
                    gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2),
                                       -1 + ((gl_VertexID & 2) << 1), 0, 1);
                }
                """, """
                #version 330 core
                uniform sampler2D cloudData;
                out vec4 color;
                void main() {
                    // Check all 16 bits before the UNORM8 render target can round
                    // half density to either 127 or 128 (both legal in Vulkan).
                    // A raw signed-short upload, wrong scale, or missing negative
                    // clamp must still fail its channel, independently of the GPU.
                    uvec4 stored = uvec4(round(texelFetch(cloudData, ivec2(0), 0) * 65535.0));
                    color = vec4(equal(stored, uvec4(65535, 32769, 0, 0)));
                }
                """);
            int texture = seam.CreateTexture2DRaw(1, 1, 0x805B, IntPtr.Zero, 0); // GL_RGBA16
            short[] source = { short.MaxValue, 16384, 0, short.MinValue };
            seam.UploadTexture2DNormalizedShorts(texture, 0, 0, 0, 1, 1, source);
            Assert.Equal(new short[] { short.MaxValue, 16384, 0, short.MinValue }, source);

            int target = seam.CreateTexture2D(1, 1, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(1, 1);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(framebuffer, 1);
            seam.SetSamplerUnit(program, "cloudData", 0);
            seam.BindTexture(0, texture);
            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.UseProgram(program);
            seam.SetViewport(0, 0, 1, 1);
            seam.SetDepthTest(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.DrawFullscreenTriangle();
            seam.Present();
            var output = new byte[4];
            fixed (byte* destination = output)
                seam.ReadDefaultFramebuffer(0, 0, 1, 1, (IntPtr)destination);
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, output);
            AssertClean(seam);
        }
    }

    [SkippableFact]
    public unsafe void AtlasCopiesWithinTheSameTextureReadTheContentsBeforeEachDraw()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            int program = LinkProgram(seam, """
                #version 330 core
                void main() {
                    gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2),
                                       -1 + ((gl_VertexID & 2) << 1), 0, 1);
                }
                """, """
                #version 330 core
                uniform sampler2D atlas;
                out vec4 color;
                void main() {
                    color = texelFetch(atlas, ivec2(gl_FragCoord.x < 1.0 ? 1 : 0, 0), 0);
                }
                """, "atlas-self-copy");
            byte[] original = { 255, 0, 0, 255, 0, 255, 0, 255 };
            byte[] swapped = { 0, 255, 0, 255, 255, 0, 0, 255 };
            int texture;
            fixed (byte* pixels = original)
                texture = seam.CreateTexture2D(2, 1, EnumTextureInternalFormat.Rgba8,
                    EnumTexturePixelFormat.Rgba, (IntPtr)pixels, false);
            int framebuffer = seam.CreateFramebuffer(2, 1);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
            seam.SetSamplerUnit(program, "atlas", 0);
            seam.BindTexture(0, texture);

            // The first draw starts with a shader-readable upload; later draws
            // start with a colour attachment. Refreshing the snapshot and leaving
            // the client's texture binding intact must both hold across frames.
            for (int frame = 0; frame < 4; frame++)
            {
                seam.BeginFrame();
                seam.BindFramebuffer(framebuffer);
                if (frame == 0)
                {
                    var before = new byte[8];
                    fixed (byte* destination = before)
                        seam.ReadDefaultFramebuffer(0, 0, 2, 1, (IntPtr)destination);
                    Assert.Equal(original, before);
                }
                seam.UseProgram(program);
                seam.SetViewport(0, 0, 2, 1);
                seam.SetDepthTest(false);
                seam.SetCullFace(false);
                seam.SetBlend(false, EnumBlendMode.Standard);
                seam.DrawFullscreenTriangle();
                seam.Present();
                var output = new byte[8];
                fixed (byte* destination = output)
                    seam.ReadDefaultFramebuffer(0, 0, 2, 1, (IntPtr)destination);
                _output.WriteLine("frame " + frame + ": " + string.Join(", ", output));
                AssertClean(seam);
                Assert.Equal(frame % 2 == 0 ? swapped : original, output);
            }
            seam.DeleteFramebuffer(framebuffer);
            seam.DeleteTexture(texture);
            AssertClean(seam);
        }
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

    [SkippableFact]
    public void TheDeviceReportsItsCapabilitiesThroughTheSeam()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;

            _output.WriteLine($"backend  : {seam.BackendName}");
            _output.WriteLine($"renderer : {seam.RendererString}");
            _output.WriteLine($"vendor   : {seam.VendorString}");
            _output.WriteLine($"version  : {seam.VersionString}");
            _output.WriteLine($"shaders  : {seam.ShaderVersionString}");
            _output.WriteLine($"max tex  : {seam.MaxTextureSize}");

            Assert.Equal("Vulkan", seam.BackendName);
            Assert.True(seam.MaxTextureSize >= 4096);
            Assert.True(seam.SupportsSSBOs);

            // The client parses this to decide whether a shader's #version is
            // supported, so it has to read as a GLSL version number.
            Assert.Matches(@"^\d\.\d+$", seam.ShaderVersionString);
        }
    }

    /// <summary>
    /// The whole path, driven only through the seam: compile, link, create a
    /// target, set state, draw, read back.
    /// </summary>
    [SkippableFact]
    public unsafe void AFrameCanBeRenderedEntirelyThroughTheSeam()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 32;

            int programId = LinkProgram(seam, """
                #version 330 core
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                }
                """, """
                #version 330 core
                uniform vec4 tint;
                out vec4 outColor;
                void main(void) { outColor = tint; }
                """);

            int texture = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
            seam.SetDrawBuffers(framebuffer, 0b1);
            Assert.True(seam.CheckFramebufferComplete(framebuffer, out _));

            // The uniform reaches the shader through the generated block.
            int tint = seam.GetUniformLocation(programId, "tint");
            Assert.True(tint >= 0);

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.ClearColor(0, 0f, 0f, 0f, 1f);

            seam.UseProgram(programId);
            seam.SetUniform(programId, tint, 0.25f, 0.5f, 0.75f, 1f);
            seam.SetViewport(0, 0, size, size);
            seam.SetDepthTest(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.DrawFullscreenTriangle();

            seam.Present();

            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(framebuffer);
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }

            int centre = (size / 2 * size + size / 2) * 4;
            _output.WriteLine($"centre RGBA = {pixels[centre]}, {pixels[centre + 1]}, " +
                              $"{pixels[centre + 2]}, {pixels[centre + 3]}");

            // 0.25, 0.5, 0.75 in 8-bit, within rounding.
            Assert.InRange(pixels[centre + 0], 60, 68);
            Assert.InRange(pixels[centre + 1], 124, 132);
            Assert.InRange(pixels[centre + 2], 187, 195);
            Assert.Equal(255, pixels[centre + 3]);

            AssertClean(seam);
        }
    }

    /// <summary>
    /// Uniforms set at any point before a draw have to persist for the life of
    /// the program, which is what GL promises and what every render system
    /// assumes when it sets a uniform once and draws many times.
    /// </summary>
    [SkippableFact]
    public unsafe void UniformsPersistAcrossDrawsAndFrames()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 16;

            int programId = LinkProgram(seam, """
                #version 330 core
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                }
                """, """
                #version 330 core
                uniform float level;
                out vec4 outColor;
                void main(void) { outColor = vec4(level, level, level, 1.0); }
                """);

            int texture = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
            seam.SetDrawBuffers(framebuffer, 0b1);

            int level = seam.GetUniformLocation(programId, "level");
            seam.UseProgram(programId);
            seam.SetUniform(programId, level, 1.0f);

            // Two frames, with the uniform set only in the first.
            for (int frame = 0; frame < 2; frame++)
            {
                seam.BeginFrame();
                seam.BindFramebuffer(framebuffer);
                seam.SetViewport(0, 0, size, size);
                seam.UseProgram(programId);
                seam.DrawFullscreenTriangle();
                seam.Present();
            }

            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(framebuffer);
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }

            int centre = (size / 2 * size + size / 2) * 4;
            Assert.Equal(255, pixels[centre]);

            AssertClean(seam);
        }
    }

    /// <summary>
    /// Texture ids are public API surface - mods read
    /// <c>LoadedTexture.TextureId</c> and hand it back - so they have to behave
    /// like GL names, including being reused after deletion.
    /// </summary>
    [SkippableFact]
    public void TextureAndFramebufferIdsBehaveLikeGlNames()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;

            int first = seam.CreateTexture2D(8, 8,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int second = seam.CreateTexture2D(8, 8,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

            Assert.True(first > 0);
            Assert.NotEqual(first, second);

            int framebuffer = seam.CreateFramebuffer(8, 8);
            Assert.True(framebuffer > 0);

            seam.DeleteTexture(first);
            seam.DeleteFramebuffer(framebuffer);
        }
    }

    /// <summary>
    /// Sampler uniforms are pointed at texture units, and a texture bound to that
    /// unit has to reach the shader. This is the path every textured draw takes.
    /// </summary>
    [SkippableFact]
    public unsafe void ATextureBoundToAUnitIsSampledByTheShader()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 16;

            int programId = LinkProgram(seam, """
                #version 330 core
                out vec2 uv;
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                    uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
                }
                """, """
                #version 330 core
                uniform sampler2D source;
                in vec2 uv;
                out vec4 outColor;
                void main(void) { outColor = texture(source, uv); }
                """);

            // A source texture filled with a known colour.
            var sourcePixels = new byte[size * size * 4];
            for (int i = 0; i < sourcePixels.Length; i += 4)
            {
                sourcePixels[i + 0] = 10;
                sourcePixels[i + 1] = 200;
                sourcePixels[i + 2] = 30;
                sourcePixels[i + 3] = 255;
            }

            int source;
            fixed (byte* data = sourcePixels)
            {
                source = seam.CreateTexture2D(size, size,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)data, false);
            }

            int target = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(framebuffer, 0b1);

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.UseProgram(programId);
            seam.SetSamplerUnit(programId, "source", 0);
            seam.BindTexture(0, source);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();
            seam.Present();

            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(framebuffer);
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }

            int centre = (size / 2 * size + size / 2) * 4;
            Assert.Equal(10, pixels[centre + 0]);
            Assert.Equal(200, pixels[centre + 1]);
            Assert.Equal(30, pixels[centre + 2]);

            AssertClean(seam);
        }
    }

    /// <summary>
    /// The multi-pass case the whole renderer is built out of: one pass renders
    /// into a texture, a later pass in the same frame samples it.
    ///
    /// In GL that needs nothing at all. In Vulkan the texture is left in the
    /// colour-attachment layout by the first pass and a shader read of it in that
    /// layout is invalid - the driver is entitled to abandon the work, and on this
    /// machine it did, losing the device partway through the first world load.
    /// The device has to notice and transition it before the second draw.
    /// </summary>
    [SkippableFact]
    public unsafe void ATextureRenderedIntoIsSampledCorrectlyByALaterPass()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 16;

            const string fullscreenVertex = """
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

            int writeProgram = LinkProgram(seam, fullscreenVertex, """
                #version 330 core
                in vec2 uv;
                out vec4 outColor;
                void main(void) { outColor = vec4(40.0 / 255.0, 90.0 / 255.0, 160.0 / 255.0, 1.0); }
                """);

            int copyProgram = LinkProgram(seam, fullscreenVertex, """
                #version 330 core
                uniform sampler2D source;
                in vec2 uv;
                out vec4 outColor;
                void main(void) { outColor = texture(source, uv); }
                """);

            int intermediate = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int final = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

            int firstPass = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(firstPass, EnumFramebufferAttachment.ColorAttachment0, intermediate, 0);
            seam.SetDrawBuffers(firstPass, 0b1);

            int secondPass = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(secondPass, EnumFramebufferAttachment.ColorAttachment0, final, 0);
            seam.SetDrawBuffers(secondPass, 0b1);

            seam.BeginFrame();

            // Pass one leaves `intermediate` as a colour attachment.
            seam.BindFramebuffer(firstPass);
            seam.UseProgram(writeProgram);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();

            // Pass two samples it. Nothing here announces the change of role.
            seam.BindFramebuffer(secondPass);
            seam.UseProgram(copyProgram);
            seam.SetSamplerUnit(copyProgram, "source", 0);
            seam.BindTexture(0, intermediate);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();

            seam.Present();

            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(secondPass);
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }

            int centre = (size / 2 * size + size / 2) * 4;
            Assert.Equal(40, pixels[centre + 0]);
            Assert.Equal(90, pixels[centre + 1]);
            Assert.Equal(160, pixels[centre + 2]);

            AssertClean(seam);
        }
    }

    /// <summary>
    /// The composition case: render into attachment 0 of a framebuffer while
    /// sampling its attachment 1, which glDrawBuffers has masked off.
    ///
    /// The masked slot is not part of the rendering scope, so it must be readable
    /// rather than held in the colour-attachment layout the previous pass left it
    /// in. It is also the one slot a scope-bounded transition loop never reaches,
    /// because it sits above the highest enabled attachment.
    /// </summary>
    [SkippableFact]
    public unsafe void AnAttachmentMaskedOutOfTheDrawCanBeSampledByIt()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 16;

            const string fullscreenVertex = """
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

            // Pass one writes both attachments, leaving both as colour attachments.
            int fillProgram = LinkProgram(seam, fullscreenVertex, """
                #version 330 core
                in vec2 uv;
                layout(location = 0) out vec4 outColor;
                layout(location = 1) out vec4 outGlow;
                void main(void)
                {
                    outColor = vec4(0.0, 0.0, 0.0, 1.0);
                    outGlow = vec4(20.0 / 255.0, 130.0 / 255.0, 240.0 / 255.0, 1.0);
                }
                """, "fill");

            // Pass two writes attachment 0 only, reading attachment 1.
            int composeProgram = LinkProgram(seam, fullscreenVertex, """
                #version 330 core
                uniform sampler2D glow;
                in vec2 uv;
                layout(location = 0) out vec4 outColor;
                void main(void) { outColor = texture(glow, uv); }
                """, "compose");

            int color = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int glowTexture = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, color, 0);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment1, glowTexture, 0);

            seam.BeginFrame();

            seam.BindFramebuffer(framebuffer);
            seam.SetDrawBuffers(framebuffer, 0b11);
            seam.UseProgram(fillProgram);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();

            // Attachment 1 drops out of the scope and becomes an input.
            seam.SetDrawBuffers(framebuffer, 0b01);
            seam.UseProgram(composeProgram);
            seam.SetSamplerUnit(composeProgram, "glow", 0);
            seam.BindTexture(0, glowTexture);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();

            seam.Present();

            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(framebuffer);
                seam.SetDrawBuffers(framebuffer, 0b01);
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }

            int centre = (size / 2 * size + size / 2) * 4;
            Assert.Equal(20, pixels[centre + 0]);
            Assert.Equal(130, pixels[centre + 1]);
            Assert.Equal(240, pixels[centre + 2]);

            AssertClean(seam);
        }
    }

    /// <summary>
    /// A uniform block the shader declares itself, fed by the client's own UBO.
    ///
    /// The client creates one of these per program and updates it directly; the
    /// device has to route it into the descriptor set for the block of that name,
    /// or the binding is read without ever having been written.
    /// </summary>
    [SkippableFact]
    public unsafe void AClientUniformBufferSuppliesTheBlockTheShaderDeclares()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 16;

            int programId = LinkProgram(seam, """
                #version 330 core
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                }
                """, """
                #version 330 core
                layout(std140) uniform Tint { vec4 tint; };
                out vec4 outColor;
                void main(void) { outColor = tint; }
                """);

            int target = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(framebuffer, 0b1);

            int ubo = seam.CreateUniformBuffer(programId, 0, "Tint", sizeof(float) * 4);
            Assert.True(ubo > 0);

            var tint = new float[] { 60f / 255f, 120f / 255f, 180f / 255f, 1f };
            fixed (float* values = tint)
            {
                seam.UpdateUniformBuffer(ubo, (IntPtr)values, 0, sizeof(float) * 4);
            }
            seam.BindUniformBuffer(ubo);

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.UseProgram(programId);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();
            seam.Present();

            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(framebuffer);
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }

            int centre = (size / 2 * size + size / 2) * 4;
            Assert.Equal(60, pixels[centre + 0]);
            Assert.Equal(120, pixels[centre + 1]);
            Assert.Equal(180, pixels[centre + 2]);

            AssertClean(seam);
        }
    }

    /// <summary>
    /// The per-entity uniform bug. The client keeps one UBO per named block and
    /// re-uploads it immediately before each draw - EntityShapeRenderer does this
    /// with the "Animation" block, once per entity - but a draw is only recorded
    /// when it is issued, not executed. A backend that wrote the client's buffer
    /// in place and bound that buffer would give every entity in the frame the
    /// last entity's transforms, because all of those draws execute after the
    /// last upload.
    ///
    /// Two quads, two uploads, one frame: each quad has to come out the colour
    /// that was in the block when it was drawn.
    /// </summary>
    [SkippableFact]
    public unsafe void TwoDrawsInOneFrameEachSeeTheBlockContentsTheyWereGiven()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 16;

            int program = LinkProgram(seam, """
                #version 330 core
                layout(location = 0) in vec3 position;
                void main(void) { gl_Position = vec4(position, 1.0); }
                """, """
                #version 330 core
                layout(std140) uniform Tint { vec4 tint; };
                out vec4 outColor;
                void main(void) { outColor = tint; }
                """);

            int left = HalfScreenQuad(seam, -1f, 0f);
            int right = HalfScreenQuad(seam, 0f, 1f);

            int target = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(framebuffer, 0b1);

            int ubo = seam.CreateUniformBuffer(program, 0, "Tint", sizeof(float) * 4);
            Assert.True(ubo > 0);
            seam.BindUniformBuffer(ubo);

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.UseProgram(program);
            seam.SetViewport(0, 0, size, size);
            seam.SetDepthTest(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.ClearColor(0, 0, 0, 0, 1);

            SetTint(seam, ubo, 60, 120, 180);
            seam.DrawMesh(left);

            // The same block, rewritten between two draws of the same frame.
            SetTint(seam, ubo, 200, 40, 90);
            seam.DrawMesh(right);
            seam.Present();

            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(framebuffer);
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }

            int leftPixel = (size / 2 * size + size / 4) * 4;
            int rightPixel = (size / 2 * size + size * 3 / 4) * 4;

            Assert.Equal(60, pixels[leftPixel + 0]);
            Assert.Equal(120, pixels[leftPixel + 1]);
            Assert.Equal(180, pixels[leftPixel + 2]);

            Assert.Equal(200, pixels[rightPixel + 0]);
            Assert.Equal(40, pixels[rightPixel + 1]);
            Assert.Equal(90, pixels[rightPixel + 2]);

            AssertNoValidationErrors(seam);
        }
    }

    /// <summary>
    /// The same hazard across the frames-in-flight boundary. The frame the GPU is
    /// still executing must not see the block the frame being recorded uploaded,
    /// and the descriptor set has to stay the same set: a per-frame snapshot that
    /// changed the set contents would grow the cache without bound.
    /// </summary>
    [SkippableFact]
    public unsafe void ConsecutiveFramesEachSeeTheirOwnBlockContents()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            // Big enough, with a long enough fragment loop, that the first frame
            // is still running on the GPU while the second is recorded: that is
            // the window the buffer-per-block design got wrong.
            const int size = 512;

            int program = LinkProgram(seam, """
                #version 330 core
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                }
                """, """
                #version 330 core
                layout(std140) uniform Tint { vec4 tint; };
                out vec4 outColor;
                void main(void)
                {
                    // Busywork whose result is never actually reached, but which
                    // the compiler cannot drop: the trip count and the branch both
                    // depend on the fragment. The colour written is the tint,
                    // untouched, so the assertion stays exact.
                    float busy = 0.0;
                    int n = 8192 + int(gl_FragCoord.x);
                    for (int i = 0; i < n; i++) busy += sin(float(i) + gl_FragCoord.y);
                    outColor = busy > 1e30 ? vec4(0.0) : tint;
                }
                """);

            var framebuffers = new int[2];
            for (int i = 0; i < framebuffers.Length; i++)
            {
                int texture = seam.CreateTexture2D(size, size,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                framebuffers[i] = seam.CreateFramebuffer(size, size);
                seam.AttachTexture(framebuffers[i], EnumFramebufferAttachment.ColorAttachment0, texture, 0);
                seam.SetDrawBuffers(framebuffers[i], 0b1);
            }

            int ubo = seam.CreateUniformBuffer(program, 0, "Tint", sizeof(float) * 4);
            seam.BindUniformBuffer(ubo);

            var colours = new[]
            {
                new byte[] { 25, 75, 125 },
                new byte[] { 210, 15, 45 },
            };

            // Neither frame is read back between the two, so the first is still
            // submitted - and with two frames in flight, possibly still running -
            // when the second overwrites the block.
            for (int frame = 0; frame < framebuffers.Length; frame++)
            {
                SetTint(seam, ubo, colours[frame][0], colours[frame][1], colours[frame][2]);

                seam.BeginFrame();
                seam.BindFramebuffer(framebuffers[frame]);
                seam.UseProgram(program);
                seam.SetViewport(0, 0, size, size);
                seam.SetDepthTest(false);
                for (int i = 0; i < 8; i++) seam.DrawFullscreenTriangle();
                seam.Present();
            }

            int cachedAfterTwoFrames = device!.CachedDescriptorSets;

            var pixels = new byte[size * size * 4];
            for (int frame = 0; frame < framebuffers.Length; frame++)
            {
                // Inside a frame: binding a target is what a frame records, so a
                // read between frames would report whatever was bound last.
                seam.BeginFrame();
                seam.BindFramebuffer(framebuffers[frame]);
                fixed (byte* destination = pixels)
                {
                    seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
                }
                seam.Present();

                int centre = (size / 2 * size + size / 2) * 4;
                Assert.Equal(colours[frame][0], pixels[centre + 0]);
                Assert.Equal(colours[frame][1], pixels[centre + 1]);
                Assert.Equal(colours[frame][2], pixels[centre + 2]);
            }

            // The snapshot travels as a dynamic offset, so the set naming the
            // ring is written once and reused; a set per frame would mean the
            // cache grew with every one of these.
            for (int i = 0; i < 4; i++)
            {
                SetTint(seam, ubo, (byte)(10 + i), 20, 30);
                seam.BeginFrame();
                seam.BindFramebuffer(framebuffers[0]);
                seam.UseProgram(program);
                seam.SetViewport(0, 0, size, size);
                seam.DrawFullscreenTriangle();
                seam.Present();
            }
            Assert.Equal(cachedAfterTwoFrames, device.CachedDescriptorSets);

            AssertNoValidationErrors(seam);
        }
    }

    /// <summary>A quad spanning the full height between two x coordinates.</summary>
    private static int HalfScreenQuad(VulkanDevice device, float x0, float x1)
    {
        var data = new MeshData(4, 6)
        {
            xyz = new[] { x0, -1f, 0f, x1, -1f, 0f, x1, 1f, 0f, x0, 1f, 0f },
            VerticesCount = 4,
            Indices = new[] { 0, 1, 2, 0, 2, 3 },
            IndicesCount = 6,
            mode = EnumDrawMode.Triangles,
        };
        return device.CreateMesh(data, true);
    }

    private static unsafe void SetTint(VulkanDevice device, int ubo, byte r, byte g, byte b)
    {
        var tint = new[] { r / 255f, g / 255f, b / 255f, 1f };
        fixed (float* values = tint)
        {
            device.UpdateUniformBuffer(ubo, (IntPtr)values, 0, sizeof(float) * 4);
        }
    }

    /// <summary>
    /// Drains the device's diagnostics and fails on anything the layers reported
    /// at error severity, or on an unpinned synchronization hazard.
    /// </summary>
    private static void AssertNoValidationErrors(VulkanDevice device) => GpuTest.AssertClean(device);

    /// <summary>
    /// The loading-screen crash. A texture is deleted and a new one takes its
    /// place; the driver may give the new image view the very handle value the
    /// old one had. A set cache keyed by handle then serves the stale set and the
    /// GPU reads freed memory. The cache must drop a deleted texture's sets and
    /// serve a successor its own, and the deferred free must land only after
    /// every frame that could have bound the old set has finished - which the
    /// validation layer checks for us.
    /// </summary>
    [SkippableFact]
    public unsafe void ADeletedTextureTakesItsDescriptorSetsWithIt()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 8;

            int program = LinkProgram(seam, """
                #version 330 core
                out vec2 uv;
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                    uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
                }
                """, """
                #version 330 core
                uniform sampler2D source;
                in vec2 uv;
                out vec4 outColor;
                void main(void) { outColor = texture(source, uv); }
                """);

            int target = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(size, size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(framebuffer, 0b1);

            // This test is about the long-lived cache's eviction; a texture made a
            // moment ago would otherwise get its set from the per-slot arena.
            device!.ShortLivedFramesForTests = 0;

            int first = SolidTexture(seam, size, 10, 20, 30);

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.UseProgram(program);
            seam.SetSamplerUnit(program, "source", 0);
            seam.BindTexture(0, first);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();
            seam.Present();

            int cachedWhileAlive = device!.CachedDescriptorSets;
            Assert.True(cachedWhileAlive >= 1, "the draw should have cached a sampler set");

            seam.DeleteTexture(first);

            // The next frame evicts the set; two more let the deferred free run
            // once the frame that bound it has signalled its fence.
            for (int i = 0; i < 3; i++)
            {
                seam.BeginFrame();
                seam.Present();
            }
            Assert.Equal(cachedWhileAlive - 1, device.CachedDescriptorSets);

            int second = SolidTexture(seam, size, 200, 100, 50);

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.UseProgram(program);
            seam.SetSamplerUnit(program, "source", 0);
            seam.BindTexture(0, second);
            seam.SetViewport(0, 0, size, size);
            seam.DrawFullscreenTriangle();
            seam.Present();

            var pixels = new byte[size * size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(framebuffer);
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }

            int centre = (size / 2 * size + size / 2) * 4;
            Assert.Equal(200, pixels[centre + 0]);
            Assert.Equal(100, pixels[centre + 1]);
            Assert.Equal(50, pixels[centre + 2]);

            AssertClean(seam);
        }
    }

    private static unsafe int SolidTexture(VulkanDevice seam, int size, byte r, byte g, byte b)
    {
        var pixels = new byte[size * size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i + 0] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }

        fixed (byte* data = pixels)
        {
            return seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)data, false);
        }
    }

    /// <summary>
    /// The pooled-chunk case. One mesh holds many chunks; each is written with
    /// the byte offset of its own slice in every part, exactly as GL's
    /// glBufferSubData destination offset works.
    ///
    /// Writing them all at zero is not a subtle corruption - every chunk in the
    /// world lands on top of the first, which renders as no terrain at all.
    /// </summary>
    [SkippableFact]
    public unsafe void AMeshUpdateWritesEachPartAtItsOwnDestinationOffset()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int verticesPerSlice = 3;
            const int slices = 4;

            int meshId = seam.CreateEmptyMesh(
                xyzSize: slices * verticesPerSlice * 3 * sizeof(float),
                normalsSize: 0,
                uvSize: slices * verticesPerSlice * 2 * sizeof(float),
                rgbaSize: slices * verticesPerSlice * 4,
                flagsSize: 0,
                indicesSize: slices * verticesPerSlice * sizeof(int),
                customFloats: null, customShorts: null, customBytes: null, customInts: null,
                drawMode: EnumDrawMode.Triangles, staticDraw: false, ssbo: false);
            Assert.True(meshId > 0);

            // Each slice writes a value identifying itself, at its own offset.
            for (int slice = 0; slice < slices; slice++)
            {
                var xyz = new float[verticesPerSlice * 3];
                for (int i = 0; i < xyz.Length; i++) xyz[i] = slice * 100 + i;

                // XyzCount is derived from VerticesCount, so only the offset and
                // the vertex count need setting.
                var data = new MeshData(verticesPerSlice, verticesPerSlice)
                {
                    xyz = xyz,
                    XyzOffset = slice * verticesPerSlice * 3 * sizeof(float),
                    VerticesCount = verticesPerSlice,
                };

                seam.UpdateMesh(meshId, data);
            }

            // Read the whole buffer back and confirm each slice kept its place.
            IntPtr mapped = seam.GetMappedPointer(meshId, EnumMeshBufferPart.Xyz);
            Assert.NotEqual(IntPtr.Zero, mapped);

            var actual = new float[slices * verticesPerSlice * 3];
            fixed (float* destination = actual)
            {
                System.Buffer.MemoryCopy((void*)mapped, destination,
                    actual.Length * sizeof(float), actual.Length * sizeof(float));
            }

            for (int slice = 0; slice < slices; slice++)
            {
                int at = slice * verticesPerSlice * 3;
                Assert.Equal(slice * 100f, actual[at]);
                Assert.Equal(slice * 100f + 1, actual[at + 1]);
            }

            seam.DeleteMesh(meshId);
            AssertClean(seam);
        }
    }

    /// <summary>
    /// Primary and Transparent share one depth texture, so DisposeFrameBuffers
    /// used to hand the same id to DeleteTexture twice. The second delete must
    /// be a no-op: no validation error, and no second tick of the deleted
    /// counter, which otherwise reported more textures freed than ever existed.
    /// </summary>
    [SkippableFact]
    public void DeletingTheSameTextureTwiceCountsAndFreesItOnce()
    {
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int size = 8;

            int texture = seam.CreateTexture2D(size, size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);

            long before = Optimum.Render.Vulkan.Core.VulkanStats.TexturesDeleted;
            seam.DeleteTexture(texture);
            long afterFirst = Optimum.Render.Vulkan.Core.VulkanStats.TexturesDeleted;
            seam.DeleteTexture(texture);
            long afterSecond = Optimum.Render.Vulkan.Core.VulkanStats.TexturesDeleted;

            Assert.Equal(before + 1, afterFirst);
            Assert.Equal(afterFirst, afterSecond);

            // The device stays usable, and the layers saw nothing wrong.
            seam.BeginFrame();
            seam.Present();
            AssertClean(seam);
        }
    }

    private static void AssertClean(VulkanDevice device) => GpuTest.AssertClean(device);
}
