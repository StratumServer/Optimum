using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Optimum.Render.Vulkan.AmbientOcclusion;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// The GTAO visibility-bitmask pass (docs/vulkan.md#ambient-occlusion section C) on a device,
/// validation with sync and best practices on, against synthetic G-buffers drawn analytically:
/// a camera with a 90-degree square frustum looking down -z over a floor at y = -1, an optional
/// back wall, a one-texel slab and a hand-view rectangle. Depth goes through the real D32
/// attachment, normals and the class channel through an RGBA16F one, exactly as Primary holds
/// them; the three compute passes run through <see cref="GtaoRenderer" /> as the platform does.
///
/// The CPU facts at the bottom pin what the shaders and the C# side must agree on: the
/// reconstruction constants, the Hilbert table, the specialization ids and the push layout.
/// </summary>
public class AmbientOcclusionTests(ITestOutputHelper output)
{
    private const int Size = 64;
    private const float Near = 0.1f;
    private const float Far = 1000f;

    private const string FullscreenVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    // Ray per pixel in GL view space (tan = 1, so the ray is (ndc + jitter, -1)); the
    // nearest analytic surface writes its GL depth, its GL view-space normal and class, and
    // its view distance for the reconstruction check.
    private const string SceneFragment = """
        #version 330 core
        uniform int scene;
        uniform float jitterX;
        uniform float jitterY;
        layout(location = 0) out vec4 outNormal;
        layout(location = 1) out vec4 outViewDepth;
        void main(void)
        {
            const float Near = 0.1;
            const float Far = 1000.0;
            vec2 ndc = gl_FragCoord.xy / 64.0 * 2.0 - 1.0;
            vec3 ray = vec3(ndc.x + jitterX, ndc.y + jitterY, -1.0);
            float t = 1e9;
            vec3 n = vec3(0.0);
            float surfaceClass = 0.0;
            if (scene != 2 && ray.y < 0.0)
            {
                float floorT = -1.0 / ray.y;
                if (floorT < t) { t = floorT; n = vec3(0.0, 1.0, 0.0); surfaceClass = 0.0; }
            }
            if (scene == 1 || scene == 3)
            {
                if (3.0 < t) { t = 3.0; n = vec3(0.0, 0.0, 1.0); surfaceClass = 0.0; }
            }
            if (scene == 2)
            {
                // A wall 4 blocks out and, 0.3 blocks in front of it, a rail one texel tall
                // (a texel is 0.116 blocks at 3.7) flagged thin like foliage.
                t = 4.0; n = vec3(0.0, 0.0, 1.0); surfaceClass = 0.0;
                if (abs(ray.y * 3.7) < 0.058) { t = 3.7; surfaceClass = 1.0; }
            }
            if (scene == 3 && gl_FragCoord.x > 40.0 && gl_FragCoord.x < 56.0 && gl_FragCoord.y > 8.0 && gl_FragCoord.y < 24.0)
            {
                t = 0.5; n = vec3(0.0, 0.0, 1.0); surfaceClass = -1.0;
            }
            if (t > 1e8)
            {
                outNormal = vec4(0.0);
                outViewDepth = vec4(0.0);
                gl_FragDepth = 1.0;
                return;
            }
            float a = -(Far + Near) / (Far - Near);
            float b = -2.0 * Far * Near / (Far - Near);
            float ndcZ = (a * -t + b) / t;
            gl_FragDepth = ndcZ * 0.5 + 0.5;
            outNormal = vec4(n, surfaceClass);
            outViewDepth = vec4(t, 0.0, 0.0, 1.0);
        }
        """;

    private const string CopyFragment = """
        #version 330 core
        uniform sampler2D ao;
        layout(location = 0) out vec4 outColor;
        void main(void) { outColor = vec4(texelFetch(ao, ivec2(gl_FragCoord.xy), 0).rrr, 1.0); }
        """;

    private enum Scene
    {
        OpenFloor = 0,
        Crease = 1,
        Slab = 2,
        Hand = 3,
    }

    private static float[] Projection(float jitterX = 0f, float jitterY = 0f)
    {
        var m = new float[16];
        m[0] = 1f;
        m[5] = 1f;
        m[8] = jitterX;
        m[9] = jitterY;
        m[10] = -(Far + Near) / (Far - Near);
        m[11] = -1f;
        m[14] = -2f * Far * Near / (Far - Near);
        return m;
    }

    /// <summary>The synthetic Primary: D32 depth, RGBA16F gNormal, an R32F view distance.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly VulkanDevice Device;
        public readonly GtaoRenderer Ao;
        public readonly int Program;
        public readonly int Framebuffer;
        public readonly int Depth;
        public readonly int Normal;
        public readonly int ViewDepth;

        public Rig(VulkanDevice device)
        {
            Device = device;
            Program = GpuTest.LinkProgram(device, FullscreenVertex, SceneFragment, "ao-scene");
            Depth = device.CreateTexture2D(Size, Size, EnumTextureInternalFormat.DepthComponent32,
                EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);
            Normal = device.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba,
                IntPtr.Zero, false);
            ViewDepth = device.CreateTexture2DRaw(Size, Size, 0x822E, IntPtr.Zero, 4);
            Framebuffer = device.CreateFramebuffer(Size, Size);
            device.AttachTexture(Framebuffer, EnumFramebufferAttachment.ColorAttachment0, Normal, 0);
            device.AttachTexture(Framebuffer, EnumFramebufferAttachment.ColorAttachment1, ViewDepth, 0);
            device.AttachTexture(Framebuffer, EnumFramebufferAttachment.DepthAttachment, Depth, 0);
            device.SetDrawBuffers(Framebuffer, 3);
            Assert.True(device.CheckFramebufferComplete(Framebuffer, out string status), status);
            Ao = new GtaoRenderer(device);
        }

        public void Draw(Scene scene, float jitterX = 0f, float jitterY = 0f)
        {
            Device.BindFramebuffer(Framebuffer);
            Device.SetViewport(0, 0, Size, Size);
            Device.SetCullFace(false);
            Device.SetBlend(false, EnumBlendMode.Standard);
            Device.SetDepthTest(true);
            Device.SetDepthMask(true);
            Device.SetDepthFunc(0x0207); // GL_ALWAYS
            Device.UseProgram(Program);
            Device.SetUniform(Program, Device.GetUniformLocation(Program, "scene"), (int)scene);
            Device.SetUniform(Program, Device.GetUniformLocation(Program, "jitterX"), jitterX);
            Device.SetUniform(Program, Device.GetUniformLocation(Program, "jitterY"), jitterY);
            Device.DrawFullscreenTriangle();
        }

        public int Render(GtaoSettings settings, uint noiseIndex, float[]? projection = null)
        {
            int texture = Ao.Render(Depth, Normal, projection ?? Projection(), settings, noiseIndex);
            Assert.True(texture != 0, Ao.LastError);
            return texture;
        }

        /// <summary>The first channel of an 8-bit storage texture, in [0, 1], row 0 at the bottom.</summary>
        public float[] ReadUnorm(int texture) => ReadUnormFrom(Device.ReadBackLevel0ForTests(texture), texture);

        public float[] ReadUnormFrom(byte[] bytes, int texture)
        {
            int channels = Device.TextureOf(texture)!.Format switch
            {
                Format.R8Unorm => 1,
                Format.R8G8Unorm => 2,
                _ => 4,
            };
            var values = new float[Size * Size];
            for (int i = 0; i < values.Length; i++) values[i] = bytes[i * channels] / 255f;
            return values;
        }

        public byte ByteAt(byte[] bytes, int texture, int x, int y)
        {
            int channels = Device.TextureOf(texture)!.Format switch
            {
                Format.R8Unorm => 1,
                Format.R8G8Unorm => 2,
                _ => 4,
            };
            return bytes[(y * Size + x) * channels];
        }

        public void Dispose() => Ao.Dispose();
    }

    private static double Mean(float[] values, Func<int, int, bool> inside)
    {
        double sum = 0;
        int count = 0;
        for (int y = 0; y < Size; y++)
        for (int x = 0; x < Size; x++)
        {
            if (!inside(x, y)) continue;
            sum += values[y * Size + x];
            count++;
        }
        Assert.True(count > 0);
        return sum / count;
    }

    private static GtaoSettings Settings(GtaoIntegration integration = GtaoIntegration.BitmaskCosine,
        GtaoPreset preset = GtaoPreset.High) =>
        GtaoSettings.ForPreset(preset, temporal: true) with { Integration = integration };

    [SkippableFact]
    public void AnOpenPlaneIsUnoccludedAndACreaseIsDarker()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        {
            var rig = new Rig(device!);

            device!.BeginFrame();
            rig.Draw(Scene.OpenFloor);
            float[] open = rig.ReadUnorm(rig.Render(Settings(), 0));
            device.Present();

            device.BeginFrame();
            rig.Draw(Scene.Crease);
            float[] crease = rig.ReadUnorm(rig.Render(Settings(), 0));
            device.Present();

            // Floor rows 0..20 are 1.0 to 2.9 blocks away: open ground with nothing above it.
            double openMin = 1.0;
            for (int y = 0; y <= 20; y++)
            for (int x = 0; x < Size; x++)
                openMin = Math.Min(openMin, open[y * Size + x]);
            output.WriteLine("open floor min visibility " + openMin.ToString("F4"));
            Assert.True(openMin >= 0.98, "open floor min visibility " + openMin);

            // The wall meets the floor at row 21.3; the rows just below it sit in the crease.
            double inCrease = Mean(crease, (_, y) => y is >= 17 and <= 20);
            double awayFromCrease = Mean(crease, (_, y) => y is >= 0 and <= 4);
            output.WriteLine("crease " + inCrease.ToString("F4") + ", away " + awayFromCrease.ToString("F4"));
            Assert.True(inCrease < 0.9, "crease visibility " + inCrease);
            Assert.True(inCrease < awayFromCrease - 0.05, "crease " + inCrease + " vs away " + awayFromCrease);

            rig.Dispose();
            GpuTest.AssertClean(device);
        }
    }

    [SkippableFact]
    public void AOneTexelSlabOccludesLessWithTheBitmaskThanWithTheHorizonIntegral()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        {
            var rig = new Rig(device!);
            // The wall rows within the effect radius (about 9 texels) of the rail, the rail excluded.
            bool AroundTheSlab(int x, int y) => x is >= 8 and < 56 && y is >= 26 and <= 38 && y is < 31 or > 33;

            var means = new Dictionary<GtaoIntegration, double>();
            foreach (GtaoIntegration integration in new[] { GtaoIntegration.BitmaskCosine, GtaoIntegration.Horizon })
            {
                device!.BeginFrame();
                rig.Draw(Scene.Slab);
                means[integration] = Mean(rig.ReadUnorm(rig.Render(Settings(integration), 0)), AroundTheSlab);
                device.Present();
                output.WriteLine(integration + " mean visibility around the slab " + means[integration].ToString("F4"));
            }

            // The horizon integral treats the slab as infinitely thick; the bitmask lets light
            // pass behind its thickness.
            Assert.True(means[GtaoIntegration.Horizon] < 0.97, "the slab must visibly occlude the horizon variant");
            Assert.True(means[GtaoIntegration.BitmaskCosine] > means[GtaoIntegration.Horizon] + 0.02,
                "bitmask " + means[GtaoIntegration.BitmaskCosine] + " vs horizon " + means[GtaoIntegration.Horizon]);

            rig.Dispose();
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableFact]
    public void CosineWeightingCountsGrazingOccludersLessThanUniformSectors()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        {
            var rig = new Rig(device!);
            bool NearTheCrease(int _, int y) => y is >= 14 and <= 20;

            var means = new Dictionary<GtaoIntegration, double>();
            foreach (GtaoIntegration integration in new[] { GtaoIntegration.BitmaskCosine, GtaoIntegration.BitmaskUniform })
            {
                device!.BeginFrame();
                rig.Draw(Scene.Crease);
                means[integration] = Mean(rig.ReadUnorm(rig.Render(Settings(integration), 0)), NearTheCrease);
                device.Present();
                output.WriteLine(integration + " mean visibility near the crease " + means[integration].ToString("F4"));
            }

            // A crease occludes the floor from its horizon up; cosine weighting gives the
            // near-horizon sectors less weight ((1 - cos a) / 2 against a / pi of the slice for
            // an occluder up to elevation a), so the documented direction is brighter.
            Assert.True(means[GtaoIntegration.BitmaskUniform] < 0.99);
            Assert.True(means[GtaoIntegration.BitmaskCosine] > means[GtaoIntegration.BitmaskUniform],
                "cosine " + means[GtaoIntegration.BitmaskCosine] + " vs uniform " + means[GtaoIntegration.BitmaskUniform]);

            rig.Dispose();
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableFact]
    public void TheHandClassAndTheSkyReceiveNoOcclusion()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        {
            var rig = new Rig(device!);

            device!.BeginFrame();
            rig.Draw(Scene.Hand);
            int texture = rig.Render(Settings(), 0);
            byte[] hand = device.ReadBackLevel0ForTests(texture);
            byte[] handWorking = device.ReadBackLevel0ForTests(rig.Ao.WorkingTermTexture);
            device.Present();

            int handPixels = 0;
            for (int y = 9; y <= 23; y++)
            for (int x = 41; x <= 55; x++)
            {
                Assert.Equal(255, rig.ByteAt(hand, texture, x, y));
                Assert.Equal(170, rig.ByteAt(handWorking, rig.Ao.WorkingTermTexture, x, y)); // 1 / 1.5
                handPixels++;
            }
            // And the crease around the hand is still occluded: the pass did run.
            Assert.True(Mean(rig.ReadUnormFrom(hand, texture), (x, y) => x < 36 && y is >= 17 and <= 20) < 0.95);

            device.BeginFrame();
            rig.Draw(Scene.OpenFloor);
            texture = rig.Render(Settings(), 0);
            byte[] sky = device.ReadBackLevel0ForTests(texture);
            device.Present();
            for (int y = 32; y < Size; y++)
            for (int x = 0; x < Size; x++)
                Assert.Equal(255, rig.ByteAt(sky, texture, x, y));

            output.WriteLine(handPixels + " hand pixels and " + (Size - 32) * Size + " sky pixels at visibility 1");
            rig.Dispose();
            GpuTest.AssertClean(device);
        }
    }

    [SkippableFact]
    public void TheWorkingDepthIsFiniteAndTheVisibilityStaysInItsRange()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        {
            var rig = new Rig(device!);
            foreach (GtaoIntegration integration in Enum.GetValues<GtaoIntegration>())
            {
                device!.BeginFrame();
                rig.Draw(Scene.Hand);
                float[] visibility = rig.ReadUnorm(rig.Render(Settings(integration), 3));
                for (uint level = 0; level < GtaoRenderer.DepthLevels; level++)
                {
                    float[] depth = MemoryMarshal.Cast<byte, float>(device.ReadBackLevelForTests(rig.Ao.WorkingDepthTexture, level)).ToArray();
                    Assert.Equal((Size >> (int)level) * (Size >> (int)level), depth.Length);
                    foreach (float z in depth) Assert.True(float.IsFinite(z) && z > 0f && z <= Far * 1.001f, "level " + level + " depth " + z);
                }
                // max(0.03, v) survives the UNORM packing; a NaN would have stored 0.
                foreach (float v in visibility) Assert.InRange(v, 0.03f - 1f / 255f, 1f);
                device.Present();
            }
            rig.Dispose();
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableFact]
    public void TheResultIsStableAcrossPresentedFramesWithAFixedNoiseIndex()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        {
            var rig = new Rig(device!);
            const int frames = 6;
            int copy = GpuTest.LinkProgram(device!, FullscreenVertex, CopyFragment, "ao-copy");
            device!.SetSamplerUnit(copy, "ao", 0);
            var colours = new int[frames];
            var targets = new int[frames];
            for (int i = 0; i < frames; i++)
            {
                colours[i] = device.CreateTexture2DRaw(Size, Size, 0x8058, IntPtr.Zero, 4);
                targets[i] = device.CreateFramebuffer(Size, Size);
                device.AttachTexture(targets[i], EnumFramebufferAttachment.ColorAttachment0, colours[i], 0);
                device.SetDrawBuffers(targets[i], 1);
            }

            // No readback in the loop: each frame draws the scene, runs the passes and copies
            // the output into its own target, then presents.
            for (int i = 0; i < frames; i++)
            {
                device.BeginFrame();
                rig.Draw(Scene.Crease);
                int texture = rig.Render(Settings(), 11);
                device.BindFramebuffer(targets[i]);
                device.SetViewport(0, 0, Size, Size);
                device.SetDepthTest(false);
                device.UseProgram(copy);
                device.BindTexture(0, texture);
                device.DrawFullscreenTriangle();
                device.BindTexture(0, 0);
                device.Present();
            }

            device.BeginFrame();
            byte[] first = device.ReadBackLevel0ForTests(colours[0]);
            Assert.Contains(first.Where((_, i) => i % 4 == 0), b => b < 250);
            for (int i = 1; i < frames; i++) Assert.Equal(first, device.ReadBackLevel0ForTests(colours[i]));
            device.Present();

            // The noise is live: another index moves the pre-denoise term.
            device.BeginFrame();
            rig.Draw(Scene.Crease);
            rig.Render(Settings(), 11);
            byte[] still = device.ReadBackLevel0ForTests(rig.Ao.WorkingTermTexture);
            rig.Render(Settings(), 12);
            byte[] moved = device.ReadBackLevel0ForTests(rig.Ao.WorkingTermTexture);
            device.Present();
            Assert.NotEqual(still, moved);

            rig.Dispose();
            GpuTest.AssertClean(device);
        }
    }

    [SkippableFact]
    public void TheWorkingDepthReconstructsTheAnalyticViewDepthWithinATenthOfAPercent()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        {
            var rig = new Rig(device!);
            // A TAA-sized jitter, so the jitter columns of the projection are exercised too.
            const float jitterX = 0.6f / Size, jitterY = -0.35f / Size;

            device!.BeginFrame();
            rig.Draw(Scene.Crease, jitterX, jitterY);
            rig.Render(Settings(), 0, Projection(jitterX, jitterY));
            float[] working = MemoryMarshal.Cast<byte, float>(device.ReadBackLevelForTests(rig.Ao.WorkingDepthTexture, 0)).ToArray();
            float[] analytic = MemoryMarshal.Cast<byte, float>(device.ReadBackLevel0ForTests(rig.ViewDepth)).ToArray();
            device.Present();

            int compared = 0;
            double worst = 0;
            for (int i = 0; i < analytic.Length; i++)
            {
                if (analytic[i] <= 0f || analytic[i] >= 100f) continue;
                double error = Math.Abs(working[i] - analytic[i]) / analytic[i];
                worst = Math.Max(worst, error);
                compared++;
            }
            output.WriteLine(compared + " pixels, worst relative view-depth error " + worst.ToString("E3"));
            Assert.True(compared > Size * Size / 2);
            Assert.True(worst < 1e-3, "worst relative error " + worst);

            rig.Dispose();
            GpuTest.AssertClean(device);
        }
    }

    [SkippableFact]
    public void EveryStorageFormatFallbackCompilesWithMatchingQualifiers()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No Vulkan device");
        using (device)
        {
            // StorageFormats' candidate chains end in RGBA32F for the depth and RGBA8 for the terms.
            foreach (Format depth in new[] { Format.R32Sfloat, Format.R32G32B32A32Sfloat })
            foreach (Format term in new[] { Format.R8Unorm, Format.R8G8Unorm, Format.R8G8B8A8Unorm })
            foreach (string file in new[] { "prefilter.comp", "main.comp", "denoise.comp" })
            {
                string source = GtaoShaderSources.Build(file, depth, term);
                int program = device!.CreateComputeProgram(source, file, Array.Empty<Core.ComputeSlot>(), GtaoSettings.PushConstantBytes);
                Assert.True(program > 0, file + " " + depth + "/" + term + ": " + device.GetError());
                device.DeleteComputeProgram(program);
            }
            GpuTest.AssertClean(device!);
        }
    }

    // ------------------------------------------------------------------ device-free

    [Fact]
    public void TheReconstructionConstantsInvertAJitteredGlProjection()
    {
        float[] m = Projection(0.6f / Size, -0.35f / Size);
        GtaoProjection projection = GtaoProjection.From(m)!.Value;
        var random = new Random(7);
        for (int i = 0; i < 200; i++)
        {
            float z = 0.2f + (float)random.NextDouble() * 150f;
            float x = ((float)random.NextDouble() * 2f - 1f) * z;
            float y = ((float)random.NextDouble() * 2f - 1f) * z;
            // GL: clip = P * (x, y, -z, 1)
            float clipX = m[0] * x + m[8] * -z;
            float clipY = m[5] * y + m[9] * -z;
            float clipZ = m[10] * -z + m[14];
            float clipW = -(-z);
            float u = (clipX / clipW + 1f) / 2f;
            float v = (clipY / clipW + 1f) / 2f;
            float depth = (clipZ / clipW + 1f) / 2f;

            float viewDepth = projection.ViewDepth(depth);
            Assert.True(Math.Abs(viewDepth - z) / z < 1e-3, "depth " + viewDepth + " vs " + z);
            (float rx, float ry, _) = projection.ViewPosition(u, v, z);
            Assert.True(Math.Abs(rx - x) <= 1e-3 * z, "x " + rx + " vs " + x);
            Assert.True(Math.Abs(ry - y) <= 1e-3 * z, "y " + ry + " vs " + y);
        }
        Assert.Null(GtaoProjection.From(new float[16]));
    }

    [Fact]
    public void TheHilbertTableIsAPermutationWithAdjacentNeighbours()
    {
        float[] table = HilbertLut.Build();
        Assert.Equal(4096, table.Length);
        var positions = new (int X, int Y)[4096];
        var seen = new bool[4096];
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            int index = (int)table[y * 64 + x];
            Assert.False(seen[index]);
            seen[index] = true;
            positions[index] = (x, y);
        }
        for (int i = 1; i < 4096; i++)
        {
            Assert.Equal(1, Math.Abs(positions[i].X - positions[i - 1].X) + Math.Abs(positions[i].Y - positions[i - 1].Y));
        }
    }

    [Fact]
    public void TheSpecializationIdsAndThePushBlockAgreeWithTheInclude()
    {
        string include = File.ReadAllText(Path.Combine(ShaderCorpus.RepositoryRoot, "sources", "shaders-vk", "gtao", "common.glsl"));
        var ids = Regex.Matches(include, @"#define GTAO_SPEC_(\w+) (\d+)")
            .ToDictionary(m => m.Groups[1].Value.Replace("_", ""), m => int.Parse(m.Groups[2].Value), StringComparer.OrdinalIgnoreCase);
        foreach (var field in typeof(GtaoSpecialization).GetFields().Where(f => f.IsLiteral && f.Name != "Count"))
        {
            Assert.True(ids.TryGetValue(field.Name, out int id), field.Name + " missing from common.glsl");
            Assert.Equal((int)field.GetValue(null)!, id);
        }
        Assert.Equal(ids.Count, typeof(GtaoSpecialization).GetFields().Count(f => f.IsLiteral && f.Name != "Count"));
        Assert.Equal(ids.Values.Max() + 1, GtaoSpecialization.Count);

        // Every member four bytes wide, in the order GtaoSettings.PushConstants writes them.
        string block = include[include.IndexOf("uniform GtaoConstants", StringComparison.Ordinal)..];
        block = block[..block.IndexOf("} gtao;", StringComparison.Ordinal)];
        string[] members = Regex.Matches(block, @"^\s*(float|uint) (\w+);", RegexOptions.Multiline).Select(m => m.Groups[2].Value).ToArray();
        Assert.Equal(new[]
        {
            "depthUnpackMul", "depthUnpackAdd", "ndcToViewMulX", "ndcToViewMulY", "ndcToViewAddX", "ndcToViewAddY",
            "effectRadius", "effectFalloffRange", "radiusMultiplier", "finalValuePower", "sampleDistributionPower",
            "depthMipSamplingOffset", "thickness", "thicknessThin", "thicknessDistanceScale", "farFadeBias", "farFadeScale",
            "noiseIndex", "denoiseBlurBeta", "reserved",
        }, members);
        Assert.Equal(GtaoSettings.PushConstantBytes, members.Length * 4);

        byte[] push = new GtaoSettings { EffectRadius = 0.9f }.PushConstants(GtaoProjection.From(Projection())!.Value, 42);
        Assert.Equal(GtaoSettings.PushConstantBytes, push.Length);
        Assert.Equal(0.9f, BitConverter.ToSingle(push, 24));
        Assert.Equal(42u, BitConverter.ToUInt32(push, 68));
    }

    [Fact]
    public void PresetsVariantsAndTheAlbedoHookResolveAsDocumented()
    {
        Assert.Equal((1u, 2u, 1u), Counts(GtaoSettings.ForPreset(GtaoPreset.Low, true)));
        Assert.Equal((2u, 2u, 1u), Counts(GtaoSettings.ForPreset(GtaoPreset.Medium, true)));
        Assert.Equal((3u, 3u, 1u), Counts(GtaoSettings.ForPreset(GtaoPreset.High, true)));
        Assert.Equal((9u, 3u, 2u), Counts(GtaoSettings.ForPreset(GtaoPreset.Ultra, true)));
        Assert.Equal((2u, 2u, 2u), Counts(GtaoSettings.ForPreset(GtaoPreset.Medium, false)));
        Assert.Equal((1u, 2u, 2u), Counts(GtaoSettings.ForStableTemporal(GtaoPreset.Low)));
        Assert.Equal((3u, 3u, 2u), Counts(GtaoSettings.ForStableTemporal(GtaoPreset.Medium)));
        Assert.Equal((3u, 3u, 2u), Counts(GtaoSettings.ForStableTemporal(GtaoPreset.High)));
        Assert.Equal((9u, 3u, 2u), Counts(GtaoSettings.ForStableTemporal(GtaoPreset.Ultra)));
        Assert.Equal(GtaoPreset.Medium, GtaoSettings.ParsePreset("nonsense"));
        Assert.Equal(GtaoPreset.Ultra, GtaoSettings.ParsePreset(" Ultra "));

        GtaoSettings defaults = GtaoSettings.ForPreset(GtaoPreset.Medium, true);
        Assert.Equal(GtaoIntegration.BitmaskCosine, defaults.Integration);
        Assert.Equal(GtaoThickness.Random, defaults.Thickness);
        Assert.True(defaults.ClassChannel);
        Assert.Equal(64u, defaults.NoiseCycle);
        Assert.Equal(1.0f, defaults.FinalValuePower); // no FinalValuePower (C.11)

        var environment = new Dictionary<string, string>
        {
            ["OPTIMUM_AO_INTEGRATION"] = "horizon",
            ["OPTIMUM_AO_THICKNESS"] = "const",
            ["OPTIMUM_AO_CLASS_CHANNEL"] = "0",
            ["OPTIMUM_AO_NOISE_CYCLE"] = "61",
            ["OPTIMUM_AO_DENOISE_PASSES"] = "3",
            ["OPTIMUM_AO_NORMAL_EDGES"] = "1",
            ["OPTIMUM_AO_TONE"] = "multibounce",
            ["OPTIMUM_AO_FINAL_POWER"] = "2.2",
        };
        GtaoSettings measured = defaults.WithEnvironment(name => environment.GetValueOrDefault(name));
        uint[] specialization = measured.MainSpecialization();
        Assert.Equal((uint)GtaoIntegration.Horizon, specialization[GtaoSpecialization.Integration]);
        Assert.Equal((uint)GtaoThickness.Constant, specialization[GtaoSpecialization.Thickness]);
        Assert.Equal(0u, specialization[GtaoSpecialization.ClassChannel]);
        Assert.Equal(61u, specialization[GtaoSpecialization.NoiseCycle]);
        Assert.Equal(1u, specialization[GtaoSpecialization.NormalEdges]);
        Assert.Equal(3u, measured.DenoisePasses);
        Assert.Equal(2.2f, measured.FinalValuePower);
        Assert.Equal(defaults, defaults.WithEnvironment(_ => "garbage"));

        // Multi-bounce needs a real albedo; the lit scene colour is not one.
        Assert.Equal(GtaoTone.Linear, measured.EffectiveTone(0, out string? refusal));
        Assert.NotNull(refusal);
        Assert.Equal(GtaoTone.MultiBounce, measured.EffectiveTone(123, out refusal));
        Assert.Null(refusal);
        Assert.Equal(0u, GtaoSettings.DenoiseSpecialization(false)[GtaoSpecialization.FinalApply]);
        Assert.Equal(1u, GtaoSettings.DenoiseSpecialization(true)[GtaoSpecialization.FinalApply]);

        static (uint, uint, uint) Counts(GtaoSettings s) => (s.SliceCount, s.StepsPerSlice, s.DenoisePasses);
    }
}
