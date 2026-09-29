using System;
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Xunit;
using Xunit.Abstractions;
using static Optimum.Render.Vulkan.Tests.MotionFixture;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>GPU contract for the terrain shaders' TAA motion attachment.</summary>
public sealed class TerrainMotionContractTests(ITestOutputHelper output)
{
    private const int Size = 64;
    private static readonly float[] Identity =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    [SkippableTheory]
    [InlineData("chunkopaque")]
    [InlineData("chunktopsoil")]
    public void TerrainWritesPreviousMinusCurrentPixelsAndDepth(string shader)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!, shader);

            // Identity projections map one NDC unit to half the render width.
            // Both axes and both signs matter: a magnitude-only check misses
            // flipped or swapped motion vectors.
            Check(scene.Draw(0, 0, 0), 0, 0);
            Check(scene.Draw(0.25f, 0, 0), 8, 0);
            Check(scene.Draw(0, -0.125f, 0), 0, -4);
            Check(scene.Draw(-0.1875f, 0.0625f, 0), -6, 2);

            GpuTest.AssertClean(device!);
        }
    }

    [SkippableTheory]
    [InlineData("chunkopaque")]
    [InlineData("chunktopsoil")]
    public void PreviousWarpMovesTheVectorWithoutWritingUncoveredPixels(string shader)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!, shader);
            float[] motion = scene.Draw(0, 0, 8);

            // Vertexwarp's phase is zero for this face. Compute its displacement
            // independently of the shader output, then convert NDC to pixels.
            double warp = (Math.Sin(0) + Math.Sin(0.5) + Math.Sin(1) / 3) / 30 * 8;
            float expectedX = (float)(warp * Size / 2);
            Assert.True(expectedX > 1);
            Check(motion, expectedX, 0);
            Assert.InRange(Pixel(motion, 2, 2, 3), 0, 0.001f);

            GpuTest.AssertClean(device!);
        }
    }

    private static void Check(float[] pixels, float expectedX, float expectedY)
    {
        Assert.InRange(Pixel(pixels, Size / 2, Size / 2, 0), expectedX - 0.05f, expectedX + 0.05f);
        Assert.InRange(Pixel(pixels, Size / 2, Size / 2, 1), expectedY - 0.05f, expectedY + 0.05f);
        Assert.InRange(Pixel(pixels, Size / 2, Size / 2, 3), 0.49f, 0.51f);
    }

    private static float Pixel(float[] pixels, int x, int y, int channel) =>
        pixels[(y * Size + x) * 4 + channel];

    private sealed class Scene
    {
        private readonly VulkanDevice device;
        private readonly int program;
        private readonly int framebuffer;
        private readonly int motion;
        private readonly int mesh;

        public Scene(VulkanDevice device, string shader)
        {
            this.device = device;
            var variant = ShaderCorpus.Variants().Single(v => v.Name == "taa-no-ssao");
            Assert.Equal(1, variant.TaaMotion);
            Assert.Equal(2, variant.TaaMotionLocation);
            var stages = ShaderCorpus.BuildProgram(shader, ShaderCorpus.LoadShaderFiles(),
                ShaderCorpus.LoadIncludes(), variant);
            program = LinkFromCorpus(device, stages, shader, oit: true);
            Assert.True(device.GetUniformLocation(program, "taaRenderSize") >= 0,
                shader + " has no motion writer");

            int unit = BindEveryDeclaredSampler(device, device, program);
            int atlas = CreateWhiteTexture(device);
            foreach (string sampler in new[] { "terrainTex", "terrainTexLinear" })
            {
                device.SetSamplerUnit(program, sampler, unit);
                device.BindTexture(unit++, atlas);
            }

            MotionTarget target = CreateMotionTarget(device, Size);
            framebuffer = target.Framebuffer;
            motion = target.MotionTexture;
            mesh = CreateFaceMesh(device);
        }

        public float[] Draw(float cameraX, float cameraY, float previousWarp)
        {
            device.BeginFrame();
            device.BindFramebuffer(framebuffer);
            device.ClearColor(0, 0, 0, 0, 1);
            device.ClearColor(1, 0, 0, 0, 1);
            device.ClearColor(2, 0, 0, 0, 0);
            device.ClearDepth(1);
            device.UseProgram(program);

            SetMatrix(device, program, "projectionMatrix", Identity);
            SetMatrix(device, program, "modelViewMatrix", Identity);
            SetMatrix(device, program, "prevProjectionMatrix", Identity);
            SetMatrix(device, program, "prevModelViewMatrix", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixFar", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixNear", Identity);
            SetFloat3(device, program, "cameraPosDelta", cameraX, cameraY, 0);
            SetFloat2(device, program, "taaRenderSize", Size, Size);
            SetFloat2(device, program, "taaJitterPx", 0, 0);
            SetViewUniforms();
            SetWarpUniforms(previousWarp);

            device.SetViewport(0, 0, Size, Size);
            device.SetDepthTest(true);
            device.SetDepthMask(true);
            device.SetDepthFunc(0x203); // GL_LEQUAL
            device.SetCullFace(false);
            device.SetBlend(false, EnumBlendMode.Standard);
            device.DrawMesh(mesh);

            float[] pixels = ReadMotion(device, motion, Size);
            device.Present();
            return pixels;
        }

        private void SetViewUniforms()
        {
            SetFloat(device, program, "viewDistance", 1024);
            SetFloat(device, program, "viewDistanceLod0", 1024);
            SetFloat(device, program, "alphaTest", 0.001f);
            SetFloat(device, program, "zNear", 0.1f);
            SetFloat(device, program, "zFar", 1024);
            SetFloat(device, program, "shadowRangeFar", 1024);
            SetFloat(device, program, "shadowRangeNear", 64);
            SetFloat(device, program, "shadowMapWidthInv", 1);
            SetFloat(device, program, "shadowMapHeightInv", 1);
            SetFloat2(device, program, "blockTextureSize", 1, 1);
            SetFloat3(device, program, "rgbaAmbientIn", 1, 1, 1);
            SetFloat2(device, program, "frameSize", Size, Size);
        }

        private void SetWarpUniforms(float previousWarp)
        {
            SetInt(device, program, "perceptionEffectId", 1);
            SetInt(device, program, "prevPerceptionEffectId", 1);
            SetFloat(device, program, "windWaveIntensity", 1);
            SetFloat(device, program, "waterWaveIntensity", 1);
            SetFloat(device, program, "prevWindWaveIntensity", 1);
            SetFloat(device, program, "prevWaterWaveIntensity", 1);
            SetFloat(device, program, "globalWarpIntensity", 0);
            SetFloat(device, program, "prevGlobalWarpIntensity", previousWarp);
        }

    }
}

/// <summary>Direct RGBA16F contract for the depth-tested sky motion pass.</summary>
public sealed class SkyMotionContractTests(ITestOutputHelper output)
{
    private const int Size = 64;
    private const float Yaw = 0.1f;

    private static float[] InverseJittered(float x, float y) =>
    [
        1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, -1,
        -2 * x / Size, -2 * y / Size, -1, 1,
    ];

    private static float[] PreviousViewProjection(float yaw)
    {
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        return [c, 0, s, s, 0, 1, 0, 0, s, 0, -c, -c, 0, 0, -1, 0];
    }

    private static (float X, float Y) ExpectedMotion(int x, int y, float yaw, float jx, float jy)
    {
        float fragX = x + 0.5f, fragY = y + 0.5f;
        float nx = fragX / Size * 2 - 1 - 2 * jx / Size;
        float ny = fragY / Size * 2 - 1 - 2 * jy / Size;
        float c = MathF.Cos(yaw), s = MathF.Sin(yaw);
        float denominator = s * nx + c;
        float previousX = ((c * nx - s) / denominator * 0.5f + 0.5f) * Size;
        float previousY = (ny / denominator * 0.5f + 0.5f) * Size;
        return (previousX - (fragX - jx), previousY - (fragY - jy));
    }

    [SkippableTheory]
    [InlineData(1f, 0f, 0f)]
    [InlineData(0f, 0f, 0f)]
    [InlineData(0.5f, 0f, 0f)]
    [InlineData(1f, 0.375f, -0.25f)]
    [InlineData(1f, -0.5f, 0.5f)]
    public void SkyRotationAndCloudCoverageWriteOnlyMotion(float coverage, float jx, float jy)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!);
            (float[] motion, byte[] color) = scene.Draw(coverage, jx, jy);
            foreach ((int x, int y) in new[] { (32, 32), (48, 40), (20, 12), (56, 56) })
            {
                int i = (y * Size + x) * 4;
                (float expectedX, float expectedY) = ExpectedMotion(x, y, Yaw, jx, jy);
                Assert.InRange(motion[i], expectedX - 0.05f, expectedX + 0.05f);
                Assert.InRange(motion[i + 1], expectedY - 0.05f, expectedY + 0.05f);
                Assert.InRange(motion[i + 2], coverage + coverage * (1 - coverage) - 0.01f,
                    coverage + coverage * (1 - coverage) + 0.01f);
                Assert.InRange(motion[i + 3], 0.99f, 1.01f);
            }
            Assert.True(Math.Abs(motion[(32 * Size + 32) * 4]) > 1);
            byte[] expectedColor = [51, 102, 153, 255];
            for (int i = 0; i < color.Length; i++)
                Assert.InRange(color[i], expectedColor[i % 4] - 1, expectedColor[i % 4] + 1);
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableFact]
    public void SkyDepthTestPreservesEarlierSurfaceWriter()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!);
            (float[] motion, _) = scene.Draw(1f, seedLeftHalf: true);
            int covered = (32 * Size + 12) * 4;
            int sky = (32 * Size + 52) * 4;
            Assert.InRange(motion[covered], 7.99f, 8.01f);
            Assert.InRange(motion[covered + 1], -8.01f, -7.99f);
            Assert.InRange(motion[covered + 2], 0.24f, 0.26f);
            Assert.InRange(motion[covered + 3], 0.49f, 0.51f);
            Assert.InRange(motion[sky + 2], 0.99f, 1.01f);
            Assert.InRange(motion[sky + 3], 0.99f, 1.01f);
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableTheory]
    [InlineData(1.7, 0.0)]
    [InlineData(1.7, -0.35)]
    [InlineData(25.0, 0.2)]
    public void StationaryElevatedCameraHasZeroSkyMotion(double height, double pitch)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        const double near = 0.0689, far = 60.0, fov = 70.0 * Math.PI / 180.0;
        double[] projection = Mat4d.Perspective(Mat4d.Create(), fov, 1.0, near, far);
        double[] view = Mat4d.Identity(Mat4d.Create());
        view = Mat4d.RotateX(view, view, pitch);
        view = Mat4d.Translate(view, view, 0, -height, 0);
        double[] vp = Mat4d.Mul(Mat4d.Create(), projection, view);
        double[] inverse = Mat4d.Invert(Mat4d.Create(), vp);
        Assert.NotNull(inverse);
        Assert.True(height / far * (Size / 2.0) / Math.Tan(fov / 2.0) > 0.6);
        using (device)
        {
            var scene = new Scene(device!);
            (float[] motion, _) = scene.Draw(0f,
                inverse: Array.ConvertAll(inverse!, v => (float)v),
                previous: Array.ConvertAll(vp, v => (float)v));
            foreach ((int x, int y) in new[] { (32, 32), (8, 56), (56, 8), (20, 44) })
            {
                int i = (y * Size + x) * 4;
                Assert.InRange(motion[i], -0.05f, 0.05f);
                Assert.InRange(motion[i + 1], -0.05f, 0.05f);
                Assert.InRange(motion[i + 3], 0.99f, 1.01f);
            }
            GpuTest.AssertClean(device!);
        }
    }

    private sealed class Scene
    {
        private readonly VulkanDevice device;
        private readonly MotionTarget target;
        private readonly int program;
        private readonly int reveal;
        private readonly int seedProgram;
        private readonly int seedMesh;

        internal Scene(VulkanDevice device)
        {
            this.device = device;
            ShaderCorpus.ShaderVariant variant = ShaderCorpus.Variants().First(v => v.Name == "taa-no-ssao");
            Assert.Equal(1, variant.TaaMotion);
            Assert.Equal(2, variant.TaaMotionLocation);
            program = LinkFromCorpus(device, ShaderCorpus.BuildProgram("taa-skymotion",
                ShaderCorpus.LoadShaderFiles(), ShaderCorpus.LoadIncludes(), variant), "taa-skymotion", oit: true);
            Assert.True(device.GetUniformLocation(program, "taaRenderSize") >= 0);
            target = CreateMotionTarget(device, Size);
            reveal = device.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            device.SetTextureParameter(reveal, OptimumGlConstants.TextureMinFilter, 9728);
            device.SetTextureParameter(reveal, OptimumGlConstants.TextureMagFilter, 9728);
            int revealTarget = device.CreateFramebuffer(Size, Size);
            device.AttachTexture(revealTarget, EnumFramebufferAttachment.ColorAttachment0, reveal, 0);
            device.SetDrawBuffers(revealTarget, 0b1);
            RevealTarget = revealTarget;
            const string vertex = "#version 330 core\nlayout(location=0) in vec3 xyz; void main() { gl_Position=vec4(xyz,1); }";
            const string fragment = "#version 330 core\nlayout(location=2) out vec4 outMotion; void main() { outMotion=vec4(8,-8,0.25,gl_FragCoord.z); }";
            seedProgram = LinkFromCorpus(device,
            [
                new() { Stage = EnumShaderType.VertexShader, Code = vertex, PrefixCode = "", Filename = "sky-seed.vsh" },
                new() { Stage = EnumShaderType.FragmentShader, Code = fragment, PrefixCode = "", Filename = "sky-seed.fsh" },
            ], "sky-seed", oit: true);
            MeshData left = new(4, 6, withNormals: false, withUv: false, withRgba: false, withFlags: false)
            {
                xyz = [-1, -1, 0, 0, -1, 0, 0, 1, 0, -1, 1, 0],
                VerticesCount = 4,
                Indices = [0, 1, 2, 0, 2, 3],
                IndicesCount = 6,
            };
            seedMesh = device.CreateMesh(left, staticDraw: true);
            Assert.True(seedMesh > 0, device.GetError() ?? "sky seed upload failed");
        }

        private int RevealTarget { get; }

        internal (float[] Motion, byte[] Color) Draw(float coverage, float jx = 0, float jy = 0,
            bool seedLeftHalf = false, float[]? inverse = null, float[]? previous = null)
        {
            device.BeginFrame();
            device.BindFramebuffer(RevealTarget);
            float revealValue = 1 - coverage;
            device.ClearColor(0, revealValue, revealValue, revealValue, 1);
            device.BindFramebuffer(target.Framebuffer);
            device.SetDrawBuffers(target.Framebuffer, 0b111);
            device.ClearColor(0, 0.2f, 0.4f, 0.6f, 1);
            device.ClearColor(1, 0, 0, 0, 1);
            device.ClearColor(2, 0, 0, 0, 0);
            device.ClearDepth(1);
            device.SetViewport(0, 0, Size, Size);
            device.SetCullFace(false);
            device.SetBlend(false, EnumBlendMode.Standard);
            device.SetDepthFunc(0x203);
            device.SetDrawBuffers(target.Framebuffer, 1 << 2);
            if (seedLeftHalf)
            {
                device.SetDepthTest(true);
                device.SetDepthMask(true);
                device.UseProgram(seedProgram);
                device.DrawMesh(seedMesh);
            }
            device.UseProgram(program);
            device.SetSamplerUnit(program, "transparentRevealTex", 14);
            device.BindTexture(14, reveal);
            SetFloat2(device, program, "taaRenderSize", Size, Size);
            SetFloat2(device, program, "taaJitterPx", jx, jy);
            SetMatrix(device, program, "taaInvViewProjJittered", inverse ?? InverseJittered(jx, jy));
            SetMatrix(device, program, "taaPrevViewProj", previous ?? PreviousViewProjection(Yaw));
            SetFloat(device, program, "taaCloudReactive", 1);
            device.SetDepthTest(true);
            device.SetDepthMask(false);
            device.DrawFullscreenTriangle();
            float[] motion = ReadMotion(device, target.MotionTexture, Size);
            byte[] color = device.ReadBackLevel0ForTests(target.ColorTexture);
            device.Present();
            return (motion, color);
        }
    }
}

/// <summary>Previous-object motion written by the actual standard shader.</summary>
public sealed class StandardMotionContractTests(ITestOutputHelper output)
{
    private const int Size = 64;
    private static readonly float[] Identity =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    private static float[] Translation(float x, float y, float z) =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        x, y, z, 1,
    ];

    // At view z = -1 the shear moves the raster position by exactly the
    // requested subpixel jitter; the previous projection remains unjittered.
    private static float[] Projection(float jitterX, float jitterY)
    {
        var projection = new float[16];
        projection[0] = projection[5] = 1;
        projection[8] = -2 * jitterX / Size;
        projection[9] = -2 * jitterY / Size;
        projection[10] = projection[11] = projection[14] = -1;
        return projection;
    }

    [SkippableFact]
    public void PreviousModelAndMissingHistoryProduceIndependentPixelVectors()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!);
            Check(scene.Draw(Identity), 0, 0, 0.5f, 0);
            Check(scene.Draw(Translation(0.25f, 0, 0)), 8, 0, 0.5f, 0);
            Check(scene.Draw(Translation(0, -0.125f, 0)), 0, -4, 0.5f, 0);
            Check(scene.Draw(Translation(-0.1875f, 0.0625f, 0)), -6, 2, 0.5f, 0);

            // A stale object transform must not leak into a draw with no history.
            Check(scene.Draw(Translation(-0.5f, 0.5f, 0), history: false,
                cameraX: 0.25f, cameraY: -0.125f), 8, -4, 0.5f, 1);
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableFact]
    public void WarpOptOutAndBehindCameraKeepTheirMotionContract()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!);
            double offset = (Math.Sin(0) + Math.Sin(0.5) + Math.Sin(1) / 3) / 30 * 8;
            float expected = (float)(offset * Size / 2);
            Assert.True(expected > 1);
            Check(scene.Draw(Identity, previousWarp: 8), expected, 0, 0.5f, 0);
            Check(scene.Draw(Identity, previousWarp: 8, noWarp: true), 0, 0, 0.5f, 0);

            float[] behindProjection =
            [
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, -1,
                0, 0, 0, 0,
            ];
            Check(scene.Draw(Identity, previousView: Translation(0, 0, 1),
                previousProjection: behindProjection, reactive: 0.6f),
                0, 0, 0, 0.6f);
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableFact]
    public void MoverMotionExcludesProjectionJitterAndIgnoresStaleHistory()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!, perspective: true);
            Check(scene.Draw(Identity, noWarp: true, jitterX: 0.37f, jitterY: -0.24f),
                0, 0, 0.5f, 0);
            Check(scene.Draw(Identity, noWarp: true, jitterX: -0.5f, jitterY: 0.5f),
                0, 0, 0.5f, 0);
            Check(scene.Draw(Translation(0.25f, 0, 0), noWarp: true,
                jitterX: 0.5f, jitterY: -0.5f), 8, 0, 0.5f, 0);
            Check(scene.Draw(Translation(0, -0.125f, 0), noWarp: true,
                jitterX: -0.5f, jitterY: 0.5f), 0, -4, 0.5f, 0);

            float[] previous = Translation(-0.1875f, 0.0625f, 0);
            float[] unjittered = scene.Draw(previous, noWarp: true);
            float[] jittered = scene.Draw(previous, noWarp: true,
                jitterX: 0.31f, jitterY: 0.47f);
            Check(jittered, -6, 2, 0.5f, 0);
            int centre = ((Size / 2) * Size + Size / 2) * 4;
            Assert.InRange(jittered[centre], unjittered[centre] - 0.05f,
                unjittered[centre] + 0.05f);
            Assert.InRange(jittered[centre + 1], unjittered[centre + 1] - 0.05f,
                unjittered[centre + 1] + 0.05f);

            Check(scene.Draw(Translation(-0.5f, 0.5f, 0), history: false,
                noWarp: true, cameraX: 0.25f, cameraY: -0.125f,
                jitterX: 0.42f, jitterY: 0.13f), 8, -4, 0.5f, 1);
            GpuTest.AssertClean(device!);
        }
    }

    private static void Check(float[] pixels, float x, float y, float depth, float reactive)
    {
        int centre = ((Size / 2) * Size + Size / 2) * 4;
        Assert.InRange(pixels[centre], x - 0.05f, x + 0.05f);
        Assert.InRange(pixels[centre + 1], y - 0.05f, y + 0.05f);
        Assert.InRange(pixels[centre + 2], reactive - 0.01f, reactive + 0.01f);
        Assert.InRange(pixels[centre + 3], depth - 0.01f, depth + 0.01f);
    }

    private sealed class Scene
    {
        private readonly VulkanDevice device;
        private readonly int program;
        private readonly MotionTarget target;
        private readonly int mesh;
        private readonly bool perspective;

        public Scene(VulkanDevice device, bool perspective = false)
        {
            this.device = device;
            this.perspective = perspective;
            var variant = new ShaderCorpus.ShaderVariant
            {
                Name = "taa-standard",
                TaaMotion = 1,
                TaaMotionLocation = 2,
            };
            var stages = ShaderCorpus.BuildProgram("standard", ShaderCorpus.LoadShaderFiles(),
                ShaderCorpus.LoadIncludes(), variant);
            program = LinkFromCorpus(device, stages, "standard", oit: false);
            foreach (string required in new[] { "taaRenderSize", "taaHistoryValid", "prevModelMatrix" })
                Assert.True(device.GetUniformLocation(program, required) >= 0, required + " is missing");
            BindEveryDeclaredSampler(device, device, program);
            target = CreateMotionTarget(device, Size);
            mesh = CreateFaceMesh(device, perspective ? -1 : 0);
        }

        public float[] Draw(float[] previousModel, bool history = true, bool noWarp = false,
            float cameraX = 0, float cameraY = 0, float previousWarp = 0,
            float[]? previousView = null, float[]? previousProjection = null, float reactive = 0,
            float jitterX = 0, float jitterY = 0)
        {
            device.BeginFrame();
            device.BindFramebuffer(target.Framebuffer);
            device.ClearColor(0, 0, 0, 0, 1);
            device.ClearColor(1, 0, 0, 0, 1);
            device.ClearColor(2, 0, 0, 0, 0);
            device.ClearDepth(1);
            device.UseProgram(program);

            SetMatrix(device, program, "projectionMatrix",
                perspective ? Projection(jitterX, jitterY) : Identity);
            SetMatrix(device, program, "viewMatrix", Identity);
            SetMatrix(device, program, "modelMatrix", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixFar", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixNear", Identity);
            SetMatrix(device, program, "prevProjectionMatrix",
                previousProjection ?? (perspective ? Projection(0, 0) : Identity));
            SetMatrix(device, program, "prevViewMatrix", previousView ?? Identity);
            SetMatrix(device, program, "prevModelMatrix", previousModel);
            SetInt(device, program, "taaHistoryValid", history ? 1 : 0);
            SetFloat(device, program, "taaReactive", history ? reactive : 1);
            SetFloat3(device, program, "cameraPosDelta", cameraX, cameraY, 0);
            SetFloat2(device, program, "taaRenderSize", Size, Size);
            SetFloat2(device, program, "taaJitterPx", jitterX, jitterY);
            SetInt(device, program, "dontWarpVertices", noWarp ? 1 : 0);
            SetFloat(device, program, "alphaTest", -1);
            SetFloat(device, program, "viewDistance", 1024);
            SetFloat(device, program, "viewDistanceLod0", 1024);
            SetFloat(device, program, "zNear", 0.1f);
            SetFloat(device, program, "zFar", 1024);
            SetFloat(device, program, "shadowRangeFar", 1024);
            SetFloat(device, program, "shadowRangeNear", 64);
            SetFloat(device, program, "shadowMapWidthInv", 1);
            SetFloat(device, program, "shadowMapHeightInv", 1);
            SetFloat3(device, program, "rgbaAmbientIn", 1, 1, 1);
            SetFloat4(device, program, "rgbaLightIn", 1, 1, 1, 1);
            SetFloat4(device, program, "rgbaFogIn", 1, 1, 1, 1);
            SetFloat4(device, program, "rgbaTint", 1, 1, 1, 1);
            SetFloat4(device, program, "averageColor", 1, 1, 1, 1);
            SetFloat2(device, program, "frameSize", Size, Size);
            SetInt(device, program, "perceptionEffectId", 1);
            SetInt(device, program, "prevPerceptionEffectId", 1);
            SetFloat(device, program, "windWaveIntensity", 1);
            SetFloat(device, program, "waterWaveIntensity", 1);
            SetFloat(device, program, "prevWindWaveIntensity", 1);
            SetFloat(device, program, "prevWaterWaveIntensity", 1);
            SetFloat(device, program, "globalWarpIntensity", 0);
            SetFloat(device, program, "prevGlobalWarpIntensity", previousWarp);

            device.SetViewport(0, 0, Size, Size);
            device.SetDepthTest(true);
            device.SetDepthMask(true);
            device.SetDepthFunc(0x203); // GL_LEQUAL
            device.SetCullFace(false);
            device.SetBlend(false, EnumBlendMode.Standard);
            device.DrawMesh(mesh);
            float[] pixels = ReadMotion(device, target.MotionTexture, Size);
            device.Present();
            return pixels;
        }
    }
}

/// <summary>The instanced shader must read each draw instance's own history.</summary>
public sealed class InstancedMotionContractTests(ITestOutputHelper output)
{
    private const int Size = 64;
    private static readonly float[] Identity =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    private static float[] Translation(float x, float y, float z) =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        x, y, z, 1,
    ];

    private readonly record struct Instance(float[] Current, float[] Previous,
        bool History = true, float Reactive = 0);

    [SkippableFact]
    public void OneDrawKeepsPreviousTransformPerInstance()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!);
            Check(scene.Draw([new Instance(Identity, Identity)]), 32, 0, 0, 0.5f, 0);
            Check(scene.Draw([new Instance(Identity, Translation(0.25f, 0, 0))]),
                32, 8, 0, 0.5f, 0);
            Check(scene.Draw([new Instance(Identity, Translation(-0.1875f, 0.0625f, 0))]),
                32, -6, 2, 0.5f, 0);

            // Two transforms in one draw prove the previous matrix is sourced
            // from the instance stream rather than a shared uniform.
            float[] pixels = scene.Draw(
            [
                new Instance(Translation(-0.5f, 0, 0), Translation(-0.25f, 0, 0)),
                new Instance(Translation(0.5f, 0, 0), Translation(0.5f, -0.125f, 0)),
            ]);
            Check(pixels, 16, 8, 0, 0.5f, 0);
            Check(pixels, 48, 0, -4, 0.5f, 0);
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableFact]
    public void MissingAndBehindCameraHistoryKeepReactiveInformation()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!);
            Check(scene.Draw([new Instance(Identity, Translation(-0.5f, 0.5f, 0),
                History: false, Reactive: 1)], cameraX: 0.25f, cameraY: -0.125f),
                32, 8, -4, 0.5f, 1);

            float[] behindProjection =
            [
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, -1,
                0, 0, 0, 0,
            ];
            Check(scene.Draw([new Instance(Identity, Identity, Reactive: 0.6f)],
                previousView: Translation(0, 0, 1), previousProjection: behindProjection),
                32, 0, 0, 0, 0.6f);
            GpuTest.AssertClean(device!);
        }
    }

    private static void Check(float[] pixels, int x, float expectedX, float expectedY,
        float depth, float reactive)
    {
        int pixel = ((Size / 2) * Size + x) * 4;
        Assert.InRange(pixels[pixel], expectedX - 0.05f, expectedX + 0.05f);
        Assert.InRange(pixels[pixel + 1], expectedY - 0.05f, expectedY + 0.05f);
        Assert.InRange(pixels[pixel + 2], reactive - 0.01f, reactive + 0.01f);
        Assert.InRange(pixels[pixel + 3], depth - 0.01f, depth + 0.01f);
    }

    private sealed class Scene
    {
        private readonly VulkanDevice device;
        private readonly int program;
        private readonly MotionTarget target;

        public Scene(VulkanDevice device)
        {
            this.device = device;
            var variant = new ShaderCorpus.ShaderVariant
            {
                Name = "taa-instanced",
                TaaMotion = 1,
                TaaMotionLocation = 2,
            };
            var stages = ShaderCorpus.BuildProgram("instanced", ShaderCorpus.LoadShaderFiles(),
                ShaderCorpus.LoadIncludes(), variant);
            program = LinkFromCorpus(device, stages, "instanced", oit: false);
            foreach (string required in new[] { "taaRenderSize", "prevModelViewMatrix" })
                Assert.True(device.GetUniformLocation(program, required) >= 0, required + " is missing");
            BindEveryDeclaredSampler(device, device, program);
            target = CreateMotionTarget(device, Size);
        }

        public float[] Draw(Instance[] instances, float cameraX = 0, float cameraY = 0,
            float[]? previousView = null, float[]? previousProjection = null)
        {
            int mesh = device.CreateMesh(BuildMesh(instances), staticDraw: false);
            Assert.True(mesh > 0, device.GetError() ?? "instanced face upload failed");
            device.BeginFrame();
            device.BindFramebuffer(target.Framebuffer);
            device.ClearColor(0, 0, 0, 0, 1);
            device.ClearColor(1, 0, 0, 0, 1);
            device.ClearColor(2, 0, 0, 0, 0);
            device.ClearDepth(1);
            device.UseProgram(program);
            SetMatrix(device, program, "projectionMatrix", Identity);
            SetMatrix(device, program, "modelViewMatrix", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixFar", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixNear", Identity);
            SetMatrix(device, program, "prevProjectionMatrix", previousProjection ?? Identity);
            SetMatrix(device, program, "prevModelViewMatrix", previousView ?? Identity);
            SetFloat3(device, program, "cameraPosDelta", cameraX, cameraY, 0);
            SetFloat2(device, program, "taaRenderSize", Size, Size);
            SetFloat2(device, program, "taaJitterPx", 0, 0);
            SetFloat(device, program, "alphaTest", -1);
            SetFloat(device, program, "viewDistance", 1024);
            SetFloat(device, program, "viewDistanceLod0", 1024);
            SetFloat(device, program, "zNear", 0.1f);
            SetFloat(device, program, "zFar", 1024);
            SetFloat(device, program, "shadowRangeFar", 1024);
            SetFloat(device, program, "shadowRangeNear", 64);
            SetFloat(device, program, "shadowMapWidthInv", 1);
            SetFloat(device, program, "shadowMapHeightInv", 1);
            SetFloat3(device, program, "rgbaAmbientIn", 1, 1, 1);
            SetFloat4(device, program, "rgbaFogIn", 1, 1, 1, 1);
            SetFloat4(device, program, "averageColor", 1, 1, 1, 1);
            SetFloat2(device, program, "frameSize", Size, Size);

            device.SetViewport(0, 0, Size, Size);
            device.SetDepthTest(true);
            device.SetDepthMask(true);
            device.SetDepthFunc(0x203); // GL_LEQUAL
            device.SetCullFace(false);
            device.SetBlend(false, EnumBlendMode.Standard);
            device.DrawMeshInstanced(mesh, instances.Length);
            float[] pixels = ReadMotion(device, target.MotionTexture, Size);
            device.Present();
            return pixels;
        }

        private static MeshData BuildMesh(Instance[] instances)
        {
            MeshData mesh = CreateFaceData();
            CustomMeshDataPartFloat stream = OptimumInstanceMotion.CreateInstanceFloats(instances.Length);
            for (int i = 0; i < instances.Length; i++)
            {
                int start = i * OptimumInstanceMotion.InstanceFloats;
                for (int channel = 0; channel < 4; channel++)
                    stream.Values[start + OptimumInstanceMotion.LightOffset + channel] = 1;
                Array.Copy(instances[i].Current, 0, stream.Values,
                    start + OptimumInstanceMotion.TransformOffset, 16);
                Array.Copy(instances[i].Previous, 0, stream.Values,
                    start + OptimumInstanceMotion.PrevTransformOffset, 16);
                stream.Values[start + OptimumInstanceMotion.MetaOffset] = instances[i].History ? 1 : 0;
                stream.Values[start + OptimumInstanceMotion.MetaOffset + 1] = instances[i].Reactive;
            }
            stream.Count = instances.Length * OptimumInstanceMotion.InstanceFloats;
            mesh.CustomFloats = stream;
            return mesh;
        }
    }
}

/// <summary>Previous skinning and model motion from the opaque entity shader.</summary>
public sealed class EntityMotionContractTests(ITestOutputHelper output)
{
    private const int Size = 64;
    private const int AnimationUboBytes = 35 * 16 * sizeof(float);
    private static readonly float[] Identity =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];

    private static float[] Translation(float x, float y, float z) =>
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        x, y, z, 1,
    ];

    [SkippableFact]
    public void SeparatePreviousBoneAndModelSourcesProduceExpectedPixels()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            Check(new Scene(device!, Identity).Draw(Identity), 0, 0, 0.5f, 0);
            Check(new Scene(device!, Translation(0.25f, 0, 0)).Draw(Identity),
                8, 0, 0.5f, 0);
            Check(new Scene(device!, Translation(0, -0.125f, 0)).Draw(Identity),
                0, -4, 0.5f, 0);
            Check(new Scene(device!, Translation(-0.1875f, 0.0625f, 0)).Draw(Identity),
                -6, 2, 0.5f, 0);
            Check(new Scene(device!, Identity).Draw(Translation(-0.25f, 0.125f, 0)),
                -8, 4, 0.5f, 0);
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableFact]
    public void InvalidHistoryWarpAndBehindCameraKeepTheirReactiveContract()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            Check(new Scene(device!, Translation(0.75f, 0.75f, 0)).Draw(
                Translation(-0.5f, 0.5f, 0), history: false,
                cameraX: 0.25f, cameraY: -0.125f), 8, -4, 0.5f, 1);

            double offset = (Math.Sin(0) + Math.Sin(0.5) + Math.Sin(1) / 3) / 30 * 8;
            float expectedWarp = (float)(offset * Size / 2);
            Assert.True(expectedWarp > 1);
            Check(new Scene(device!, Identity).Draw(Identity, previousWarp: 8),
                expectedWarp, 0, 0.5f, 0);

            float[] behindProjection =
            [
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, 1, -1,
                0, 0, 0, 0,
            ];
            Check(new Scene(device!, Identity).Draw(Identity,
                previousView: Translation(0, 0, 1),
                previousProjection: behindProjection, reactive: 0.6f),
                0, 0, 0, 0.6f);
            GpuTest.AssertClean(device!);
        }
    }

    private static void Check(float[] pixels, float x, float y, float depth, float reactive)
    {
        int centre = ((Size / 2) * Size + Size / 2) * 4;
        Assert.InRange(pixels[centre], x - 0.05f, x + 0.05f);
        Assert.InRange(pixels[centre + 1], y - 0.05f, y + 0.05f);
        Assert.InRange(pixels[centre + 2], reactive - 0.01f, reactive + 0.01f);
        Assert.InRange(pixels[centre + 3], depth - 0.01f, depth + 0.01f);
    }

    private sealed class Scene
    {
        private readonly VulkanDevice device;
        private readonly int program;
        private readonly MotionTarget target;
        private readonly int mesh;

        public Scene(VulkanDevice device, float[] previousBone)
        {
            this.device = device;
            var variant = new ShaderCorpus.ShaderVariant
            {
                Name = "taa-entity-opaque",
                UseOit = 0,
                TaaMotion = 1,
                TaaMotionLocation = 2,
                MaxAnimatedElements = 35,
            };
            var stages = ShaderCorpus.BuildProgram("entityanimated", ShaderCorpus.LoadShaderFiles(),
                ShaderCorpus.LoadIncludes(), variant);
            program = LinkFromCorpus(device, stages, "entityanimated", oit: false);
            foreach (string required in new[] { "taaRenderSize", "taaHistoryValid" })
                Assert.True(device.GetUniformLocation(program, required) >= 0, required + " is missing");
            int unit = BindEveryDeclaredSampler(device, device, program);
            int atlas = CreateWhiteTexture(device);
            device.SetSamplerUnit(program, "entityTex", unit);
            device.BindTexture(unit, atlas);

            int current = device.CreateUniformBuffer(program, 0, "Animation", AnimationUboBytes);
            int previous = device.CreateUniformBuffer(program, 1, "AnimationPrev", AnimationUboBytes);
            WriteBone(device, current, Identity);
            WriteBone(device, previous, previousBone);
            target = CreateMotionTarget(device, Size);
            mesh = device.CreateMesh(SkinnedFace(), staticDraw: true);
            Assert.True(mesh > 0, device.GetError() ?? "entity face upload failed");
        }

        public float[] Draw(float[] previousModel, bool history = true, float cameraX = 0,
            float cameraY = 0, float previousWarp = 0, float[]? previousView = null,
            float[]? previousProjection = null, float reactive = 0)
        {
            device.BeginFrame();
            device.BindFramebuffer(target.Framebuffer);
            device.ClearColor(0, 0, 0, 0, 1);
            device.ClearColor(1, 0, 0, 0, 1);
            device.ClearColor(2, 0, 0, 0, 0);
            device.ClearDepth(1);
            device.UseProgram(program);
            SetMatrix(device, program, "projectionMatrix", Identity);
            SetMatrix(device, program, "viewMatrix", Identity);
            SetMatrix(device, program, "modelMatrix", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixFar", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixNear", Identity);
            SetMatrix(device, program, "prevProjectionMatrix", previousProjection ?? Identity);
            SetMatrix(device, program, "prevViewMatrix", previousView ?? Identity);
            SetMatrix(device, program, "prevModelMatrix", previousModel);
            SetInt(device, program, "taaHistoryValid", history ? 1 : 0);
            SetFloat(device, program, "taaReactive", history ? reactive : 1);
            SetFloat3(device, program, "cameraPosDelta", cameraX, cameraY, 0);
            SetFloat2(device, program, "taaRenderSize", Size, Size);
            SetFloat2(device, program, "taaJitterPx", 0, 0);
            SetFloat(device, program, "alphaTest", -1);
            SetFloat(device, program, "viewDistance", 1024);
            SetFloat(device, program, "viewDistanceLod0", 1024);
            SetFloat(device, program, "zNear", 0.1f);
            SetFloat(device, program, "zFar", 1024);
            SetFloat(device, program, "shadowRangeFar", 1024);
            SetFloat(device, program, "shadowRangeNear", 64);
            SetFloat(device, program, "shadowMapWidthInv", 1);
            SetFloat(device, program, "shadowMapHeightInv", 1);
            SetInt(device, program, "entityId", 1);
            SetFloat3(device, program, "rgbaAmbientIn", 1, 1, 1);
            SetFloat4(device, program, "rgbaLightIn", 1, 1, 1, 1);
            SetFloat4(device, program, "rgbaFogIn", 1, 1, 1, 1);
            SetFloat4(device, program, "renderColor", 1, 1, 1, 1);
            SetFloat2(device, program, "frameSize", Size, Size);
            SetInt(device, program, "perceptionEffectId", 1);
            SetInt(device, program, "prevPerceptionEffectId", 1);
            SetFloat(device, program, "windWaveIntensity", 1);
            SetFloat(device, program, "waterWaveIntensity", 1);
            SetFloat(device, program, "prevWindWaveIntensity", 1);
            SetFloat(device, program, "prevWaterWaveIntensity", 1);
            SetFloat(device, program, "globalWarpIntensity", 0);
            SetFloat(device, program, "prevGlobalWarpIntensity", previousWarp);

            device.SetViewport(0, 0, Size, Size);
            device.SetDepthTest(true);
            device.SetDepthMask(true);
            device.SetDepthFunc(0x203); // GL_LEQUAL
            device.SetCullFace(false);
            device.SetBlend(false, EnumBlendMode.Standard);
            device.DrawMesh(mesh);
            float[] pixels = ReadMotion(device, target.MotionTexture, Size);
            device.Present();
            return pixels;
        }

        private static unsafe void WriteBone(VulkanDevice device, int buffer, float[] matrix)
        {
            fixed (float* values = matrix)
                device.UpdateUniformBuffer(buffer, (IntPtr)values, 0, 16 * sizeof(float));
        }

        private static MeshData SkinnedFace()
        {
            MeshData mesh = CreateFaceData();
            mesh.CustomFloats = new CustomMeshDataPartFloat(4)
            {
                Count = 4,
                InterleaveSizes = [1],
                InterleaveOffsets = [0],
                InterleaveStride = 4,
            };
            mesh.CustomInts = new CustomMeshDataPartInt(4)
            {
                Count = 4,
                InterleaveSizes = [1],
                InterleaveOffsets = [0],
                InterleaveStride = 4,
            };
            return mesh;
        }
    }
}

/// <summary>The liquid velocity pass writes motion without changing shaded colour.</summary>
public sealed class LiquidMotionContractTests(ITestOutputHelper output)
{
    private const int Size = 64;
    private const float Reactive = 0.3f;
    private const float LiquidClipW = 1.008f;
    private static readonly float[] Identity =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];
    private static readonly float[] Projection =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, -1, -1,
        0, 0, -1, 0,
    ];

    private static float[] Jittered(float x, float y)
    {
        float[] projection = (float[])Projection.Clone();
        projection[8] -= 2 * x / Size;
        projection[9] -= 2 * y / Size;
        return projection;
    }

    [SkippableFact]
    public void CameraMotionAndProjectionJitterHaveIndependentPixelExpectations()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new Scene(device!);
            Check(scene.Draw(), 0, 0, 0.5f, Reactive);
            Check(scene.Draw(cameraX: 0.25f), 8 / LiquidClipW, 0, 0.5f, Reactive);
            Check(scene.Draw(cameraY: -0.125f), 0, -4 / LiquidClipW, 0.5f, Reactive);
            Check(scene.Draw(cameraX: -0.1875f, cameraY: 0.0625f),
                -6 / LiquidClipW, 2 / LiquidClipW, 0.5f, Reactive);
            Check(scene.Draw(cameraX: 0.25f, cameraY: -0.125f,
                jitterX: 0.375f, jitterY: -0.25f),
                8 / LiquidClipW, -4 / LiquidClipW, 0.5f, Reactive);
            Check(scene.Draw(cameraX: 0.25f, cameraY: -0.125f,
                jitterX: -0.5f, jitterY: 0.5f),
                8 / LiquidClipW, -4 / LiquidClipW, 0.5f, Reactive);
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableFact]
    public void VelocityWindowPreservesEveryColorPixelAndUncoveredMotionDepth()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            Result result = new Scene(device!).Draw(cameraX: 0.25f);
            Check(result, 8 / LiquidClipW, 0, 0.5f, Reactive);
            Assert.InRange(Channel(result.Motion, 2, 2, 3), 0, 0.001f);
            byte[] expected = [51, 102, 153, 255];
            Assert.Equal(Size * Size * 4, result.Color.Length);
            for (int pixel = 0; pixel < Size * Size; pixel++)
            for (int channel = 0; channel < 4; channel++)
                Assert.InRange(result.Color[pixel * 4 + channel],
                    Math.Max(0, expected[channel] - 1),
                    Math.Min(255, expected[channel] + 1));
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableFact]
    public void PreviousWaveAndBehindCameraKeepMotionAndReactiveMeaning()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            Result waves = new Scene(device!, waterFlags: 1).Draw(previousWave: 3);
            float[] row = new float[24];
            int moved = 0;
            for (int i = 0; i < row.Length; i++)
            {
                int x = i + 20;
                Assert.InRange(Channel(waves.Motion, x, 32, 3), 0.48f, 0.52f);
                Assert.InRange(Channel(waves.Motion, x, 32, 0), -0.05f, 0.05f);
                row[i] = Channel(waves.Motion, x, 32, 1);
                if (Math.Abs(row[i]) > 0.3f) moved++;
            }
            Assert.True(moved > row.Length / 4, "previous liquid wave wrote no vertical motion");
            Assert.True(row.Max() - row.Min() > 0.3f, "previous liquid wave lost its noise shape");

            float[] mirrorZ =
            [
                1, 0, 0, 0,
                0, 1, 0, 0,
                0, 0, -1, 0,
                0, 0, 0, 1,
            ];
            Check(new Scene(device!).Draw(previousView: mirrorZ), 0, 0, 0, Reactive);
            GpuTest.AssertClean(device!);
        }
    }

    private sealed record Result(float[] Motion, byte[] Color);

    private static float Channel(float[] pixels, int x, int y, int channel) =>
        pixels[(y * Size + x) * 4 + channel];

    private static void Check(Result result, float x, float y, float depth, float reactive)
    {
        Assert.InRange(Channel(result.Motion, 32, 32, 0), x - 0.06f, x + 0.06f);
        Assert.InRange(Channel(result.Motion, 32, 32, 1), y - 0.06f, y + 0.06f);
        Assert.InRange(Channel(result.Motion, 32, 32, 2), reactive - 0.01f, reactive + 0.01f);
        Assert.InRange(Channel(result.Motion, 32, 32, 3), depth - 0.02f, depth + 0.02f);
    }

    private sealed class Scene
    {
        private readonly VulkanDevice device;
        private readonly int program;
        private readonly MotionTarget target;
        private readonly int mesh;

        public Scene(VulkanDevice device, int waterFlags = 0)
        {
            this.device = device;
            var variant = ShaderCorpus.Variants().Single(v => v.Name == "taa-no-ssao");
            Assert.Equal(1, variant.TaaMotion);
            Assert.Equal(2, variant.TaaMotionLocation);
            Assert.Equal(1, variant.WavingStuff);
            var stages = ShaderCorpus.BuildProgram("chunkliquidmotion",
                ShaderCorpus.LoadShaderFiles(), ShaderCorpus.LoadIncludes(), variant);
            program = LinkFromCorpus(device, stages, "chunkliquidmotion", oit: true);
            Assert.True(device.GetUniformLocation(program, "taaRenderSize") >= 0);
            target = CreateMotionTarget(device, Size);
            mesh = device.CreateMesh(LiquidFace(waterFlags), staticDraw: true);
            Assert.True(mesh > 0, device.GetError() ?? "liquid face upload failed");
        }

        public Result Draw(float cameraX = 0, float cameraY = 0, float jitterX = 0,
            float jitterY = 0, float previousWave = 0, float[]? previousView = null)
        {
            device.BeginFrame();
            device.BindFramebuffer(target.Framebuffer);
            device.SetDrawBuffers(target.Framebuffer, 0b111);
            device.ClearColor(0, 0.2f, 0.4f, 0.6f, 1);
            device.ClearColor(1, 0, 0, 0, 1);
            device.ClearColor(2, 0, 0, 0, 0);
            device.ClearDepth(1);
            device.SetDrawBuffers(target.Framebuffer, 1 << 2);
            device.UseProgram(program);
            SetMatrix(device, program, "projectionMatrix", Jittered(jitterX, jitterY));
            SetMatrix(device, program, "modelViewMatrix", Identity);
            SetMatrix(device, program, "prevProjectionMatrix", Projection);
            SetMatrix(device, program, "prevModelViewMatrix", previousView ?? Identity);
            SetFloat3(device, program, "cameraPosDelta", cameraX, cameraY, 0);
            SetFloat2(device, program, "taaRenderSize", Size, Size);
            SetFloat2(device, program, "taaJitterPx", jitterX, jitterY);
            SetFloat(device, program, "taaLiquidReactive", Reactive);
            SetInt(device, program, "perceptionEffectId", 1);
            SetInt(device, program, "prevPerceptionEffectId", 1);
            SetFloat(device, program, "globalWarpIntensity", 0);
            SetFloat(device, program, "prevGlobalWarpIntensity", 0);
            SetFloat(device, program, "windWaveIntensity", 0);
            SetFloat(device, program, "prevWindWaveIntensity", 0);
            SetFloat(device, program, "waterWaveIntensity", 0);
            SetFloat(device, program, "prevWaterWaveIntensity", previousWave);
            SetFloat(device, program, "prevWaterWaveCounter", 1.234f);
            SetFloat3(device, program, "origin", 0, 0, 0);

            device.SetViewport(0, 0, Size, Size);
            device.SetDepthTest(true);
            device.SetDepthMask(true);
            device.SetDepthFunc(0x203); // GL_LEQUAL
            device.SetCullFace(false);
            device.SetBlend(false, EnumBlendMode.Standard);
            device.DrawMesh(mesh);
            float[] motion = ReadMotion(device, target.MotionTexture, Size);
            device.SetDrawBuffers(target.Framebuffer, 0b111);
            byte[] color = device.ReadBackLevel0ForTests(target.ColorTexture);
            device.Present();
            return new Result(motion, color);
        }

        private static MeshData LiquidFace(int waterFlags)
        {
            MeshData face = CreateFaceData(-1, flags: 0);
            face.CustomFloats = new CustomMeshDataPartFloat
            {
                Values = new float[8],
                Count = 8,
                InterleaveOffsets = [0],
                InterleaveSizes = [2],
                InterleaveStride = 8,
            };
            int[] flags = new int[8];
            for (int i = 0; i < 4; i++) flags[i * 2 + 1] = waterFlags;
            face.CustomInts = new CustomMeshDataPartInt
            {
                Values = flags,
                Count = 8,
                InterleaveOffsets = [0, 4],
                InterleaveSizes = [1, 1],
                InterleaveStride = 8,
                Conversion = DataConversion.Integer,
            };
            return face;
        }
    }
}

/// <summary>Opaque cube-particle motion and transparent-merge reactive coverage.</summary>
public sealed class ParticleMotionContractTests(ITestOutputHelper output)
{
    private const int Size = 64;
    private static readonly float[] Identity =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, 1, 0,
        0, 0, 0, 1,
    ];
    private static readonly float[] Projection =
    [
        1, 0, 0, 0,
        0, 1, 0, 0,
        0, 0, -1, -1,
        0, 0, -1, 0,
    ];

    private static float[] Jittered(float x, float y)
    {
        float[] projection = (float[])Projection.Clone();
        projection[8] -= 2 * x / Size;
        projection[9] -= 2 * y / Size;
        return projection;
    }

    [SkippableFact]
    public void CubeParticleWritesCameraMotionReactiveAndColor()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            var scene = new CubeScene(device!);
            Result still = scene.Draw();
            Check(still.Motion, 32, 0, 0, 1, 0.5f);
            int centre = (32 * Size + 32) * 4;
            byte[] clear = [51, 102, 153, 255];
            Assert.True(Enumerable.Range(0, 4).Any(c =>
                Math.Abs(still.Color[centre + c] - clear[c]) > 1),
                "cube particle did not shade color attachment 0");

            Check(scene.Draw(cameraX: 0.25f).Motion, 32, 8, 0, 1, 0.5f);
            Check(scene.Draw(cameraY: -0.125f).Motion, 32, 0, -4, 1, 0.5f);
            Check(scene.Draw(cameraX: -0.1875f, cameraY: 0.0625f).Motion,
                32, -6, 2, 1, 0.5f);
            Check(scene.Draw(cameraX: 0.25f, cameraY: -0.125f,
                jitterX: 0.375f, jitterY: -0.25f).Motion, 32, 8, -4, 1, 0.5f);
            Check(scene.Draw(cameraX: 0.25f, cameraY: -0.125f,
                jitterX: -0.5f, jitterY: 0.5f).Motion, 32, 8, -4, 1, 0.5f);
            Check(scene.Draw().Motion, 2, 0, 0, 0, 0);
            GpuTest.AssertClean(device!);
        }
    }

    [SkippableFact]
    public void TransparentMergeAddsCoverageWithoutReplacingOpaqueVectorOrDepth()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(GpuTest.TryCreateDevice(output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            foreach ((float revealage, float reactive) in new[]
            {
                (1f, 0f), (0.25f, 0.75f), (0f, 1f),
            })
                Check(new MergeScene(device!).Draw(revealage, 0),
                    32, 4, -8, reactive, 0.5f);

            Check(new MergeScene(device!).Draw(1, 1), 32, 4, -8, 1, 0.5f);
            GpuTest.AssertClean(device!);
        }
    }

    private sealed record Result(float[] Motion, byte[] Color);

    private static void Check(float[] pixels, int x, float motionX, float motionY,
        float reactive, float depth)
    {
        int pixel = (32 * Size + x) * 4;
        Assert.InRange(pixels[pixel], motionX - 0.06f, motionX + 0.06f);
        Assert.InRange(pixels[pixel + 1], motionY - 0.06f, motionY + 0.06f);
        Assert.InRange(pixels[pixel + 2], reactive - 0.01f, reactive + 0.01f);
        Assert.InRange(pixels[pixel + 3], depth - 0.02f, depth + 0.02f);
    }

    private sealed class CubeScene
    {
        private readonly VulkanDevice device;
        private readonly int program;
        private readonly MotionTarget target;
        private readonly int mesh;

        public CubeScene(VulkanDevice device)
        {
            this.device = device;
            var variant = ShaderCorpus.Variants().Single(v => v.Name == "taa-no-ssao");
            var stages = ShaderCorpus.BuildProgram("particlescube",
                ShaderCorpus.LoadShaderFiles(), ShaderCorpus.LoadIncludes(), variant);
            program = LinkFromCorpus(device, stages, "particlescube", oit: true);
            Assert.True(device.GetUniformLocation(program, "taaRenderSize") >= 0);
            target = CreateMotionTarget(device, Size);
            mesh = device.CreateMesh(CubeFace(), staticDraw: true);
            Assert.True(mesh > 0, device.GetError() ?? "cube-particle upload failed");
        }

        public Result Draw(float cameraX = 0, float cameraY = 0, float jitterX = 0,
            float jitterY = 0)
        {
            device.BeginFrame();
            device.BindFramebuffer(target.Framebuffer);
            device.SetDrawBuffers(target.Framebuffer, 0b111);
            device.ClearColor(0, 0.2f, 0.4f, 0.6f, 1);
            device.ClearColor(1, 0, 0, 0, 1);
            device.ClearColor(2, 0, 0, 0, 0);
            device.ClearDepth(1);
            device.UseProgram(program);
            SetMatrix(device, program, "projectionMatrix", Jittered(jitterX, jitterY));
            SetMatrix(device, program, "modelViewMatrix", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixFar", Identity);
            SetMatrix(device, program, "toShadowMapSpaceMatrixNear", Identity);
            SetMatrix(device, program, "prevProjectionMatrix", Projection);
            SetMatrix(device, program, "prevModelViewMatrix", Identity);
            SetFloat3(device, program, "cameraPosDelta", cameraX, cameraY, 0);
            SetFloat2(device, program, "taaRenderSize", Size, Size);
            SetFloat2(device, program, "taaJitterPx", jitterX, jitterY);
            SetFloat3(device, program, "rgbaAmbientIn", 1, 1, 1);
            SetInt(device, program, "perceptionEffectId", 1);
            SetInt(device, program, "prevPerceptionEffectId", 1);
            SetFloat(device, program, "globalWarpIntensity", 0);
            SetFloat(device, program, "prevGlobalWarpIntensity", 0);

            device.SetViewport(0, 0, Size, Size);
            device.SetDepthTest(true);
            device.SetDepthMask(true);
            device.SetDepthFunc(0x203); // GL_LEQUAL
            device.SetCullFace(false);
            device.SetBlend(true, EnumBlendMode.Standard);
            device.SetBlendFuncSeparate(0, 770, 771, 770, 771);
            device.SetBlendEquation(2, 32774);
            device.SetBlendFuncSeparate(2, 1, 0, 1, 0);
            device.DrawMeshInstanced(mesh, 1);
            float[] motion = ReadMotion(device, target.MotionTexture, Size);
            byte[] color = device.ReadBackLevel0ForTests(target.ColorTexture);
            device.Present();
            return new Result(motion, color);
        }

        private static MeshData CubeFace()
        {
            var mesh = new MeshData(4, 6, withNormals: true, withUv: true,
                withRgba: false, withFlags: true)
            {
                xyz = [-0.5f, -0.5f, 0, 0.5f, -0.5f, 0,
                    0.5f, 0.5f, 0, -0.5f, 0.5f, 0],
                Uv = [0, 0, 1, 0, 1, 1, 0, 1],
                Normals = new int[4],
                NormalsCount = 4,
                VerticesCount = 4,
                Indices = [0, 1, 2, 0, 2, 3],
                IndicesCount = 6,
            };
            mesh.CustomFloats = new CustomMeshDataPartFloat
            {
                Instanced = true,
                StaticDraw = false,
                Values = [0, 0, -1, 1],
                Count = 4,
                InterleaveSizes = [3, 1],
                InterleaveOffsets = [0, 12],
                InterleaveStride = 16,
            };
            mesh.CustomBytes = new CustomMeshDataPartByte
            {
                Conversion = DataConversion.NormalizedFloat,
                Instanced = true,
                StaticDraw = false,
                Values = Enumerable.Repeat((byte)255, 12).ToArray(),
                Count = 12,
                InterleaveSizes = [4, 4, 4],
                InterleaveOffsets = [0, 4, 8],
                InterleaveStride = 12,
            };
            mesh.Flags = new int[4];
            mesh.FlagsInstanced = true;
            return mesh;
        }
    }

    private sealed class MergeScene
    {
        private readonly VulkanDevice device;
        private readonly int program;
        private readonly int seedProgram;
        private readonly MotionTarget target;
        private readonly int quad;
        private readonly int accumulation;
        private readonly int glow;
        private readonly int oitReveal;
        private readonly int oitAccumulation;

        public MergeScene(VulkanDevice device)
        {
            this.device = device;
            var variant = ShaderCorpus.Variants().Single(v => v.Name == "taa-no-ssao");
            var stages = ShaderCorpus.BuildProgram("transparentcompose",
                ShaderCorpus.LoadShaderFiles(), ShaderCorpus.LoadIncludes(), variant);
            program = LinkFromCorpus(device, stages, "transparentcompose", oit: true);
            seedProgram = SeedProgram(device);
            target = CreateMotionTarget(device, Size);
            quad = FullscreenQuad(device);
            accumulation = SolidTexture(device, 0, 0, 0, 0);
            glow = SolidTexture(device, 0, 0, 0, 1);
            oitReveal = SolidTexture(device, 0, 0, 0, 0);
            oitAccumulation = device.CreateTexture2DArray(Size, Size, 3,
                EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba);
        }

        public float[] Draw(float revealage, float seedReactive)
        {
            int reveal = SolidTexture(device, revealage, 0, 0, 1);
            device.BeginFrame();
            device.BindFramebuffer(target.Framebuffer);
            device.SetDrawBuffers(target.Framebuffer, 0b111);
            device.ClearColor(0, 0.2f, 0.4f, 0.6f, 1);
            device.ClearColor(1, 0, 0, 0, 1);
            device.ClearColor(2, 0, 0, 0, 0);
            device.ClearDepth(1);

            device.SetDrawBuffers(target.Framebuffer, 1 << 2);
            device.UseProgram(seedProgram);
            SetFloat(device, seedProgram, "seedB", seedReactive);
            device.SetViewport(0, 0, Size, Size);
            device.SetDepthTest(false);
            device.SetDepthMask(false);
            device.SetCullFace(false);
            device.SetBlend(false, EnumBlendMode.Standard);
            device.DrawMesh(quad);

            device.SetDrawBuffers(target.Framebuffer, 0b111);
            device.SetBlend(true, EnumBlendMode.Standard);
            device.SetBlendFuncSeparate(0, 770, 771, 770, 771);
            device.SetBlendEquation(2, 32774);
            device.SetBlendFuncSeparate(2, 1, 1, 1, 1);
            device.UseProgram(program);
            Bind("revealage", 10, reveal);
            Bind("accumulation", 11, accumulation);
            Bind("inGlow", 12, glow);
            Bind("OITreveal", 13, oitReveal);
            Bind("OITaccumulation", 14, oitAccumulation);
            device.DrawMesh(quad);
            float[] motion = ReadMotion(device, target.MotionTexture, Size);
            device.Present();
            return motion;
        }

        private void Bind(string sampler, int unit, int texture)
        {
            device.SetSamplerUnit(program, sampler, unit);
            device.BindTexture(unit, texture);
        }

        private static int SeedProgram(VulkanDevice device)
        {
            const string vertex = "#version 330 core\nlayout(location=0) in vec3 xyz;\n"
                + "void main(){gl_Position=vec4(xyz,1);}";
            const string fragment = "#version 330 core\nuniform float seedB;\n"
                + "layout(location=2) out vec4 outMotion;\n"
                + "void main(){outMotion=vec4(4,-8,seedB,0.5);}";
            return LinkFromCorpus(device, new List<ShaderStageSource>
            {
                new() { Stage = EnumShaderType.VertexShader, Code = vertex,
                    Filename = "motion-seed.vsh" },
                new() { Stage = EnumShaderType.FragmentShader, Code = fragment,
                    Filename = "motion-seed.fsh" },
            }, "motion-seed", oit: true);
        }

        private static int FullscreenQuad(VulkanDevice device)
        {
            var quad = new MeshData(4, 6, withNormals: false, withUv: false,
                withRgba: false, withFlags: false)
            {
                xyz = [-1, -1, 0, 1, -1, 0, 1, 1, 0, -1, 1, 0],
                VerticesCount = 4,
                Indices = [0, 1, 2, 0, 2, 3],
                IndicesCount = 6,
            };
            int mesh = device.CreateMesh(quad, staticDraw: true);
            Assert.True(mesh > 0, device.GetError() ?? "full-screen upload failed");
            return mesh;
        }

        private static unsafe int SolidTexture(VulkanDevice device, float r, float g,
            float b, float a)
        {
            byte[] pixels = new byte[Size * Size * 4];
            byte[] value =
            [
                (byte)Math.Clamp((int)MathF.Round(r * 255), 0, 255),
                (byte)Math.Clamp((int)MathF.Round(g * 255), 0, 255),
                (byte)Math.Clamp((int)MathF.Round(b * 255), 0, 255),
                (byte)Math.Clamp((int)MathF.Round(a * 255), 0, 255),
            ];
            for (int pixel = 0; pixel < pixels.Length; pixel += 4)
                value.CopyTo(pixels, pixel);
            fixed (byte* source = pixels)
            {
                int texture = device.CreateTexture2D(Size, Size,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba,
                    (IntPtr)source, false);
                device.SetTextureParameter(texture, OptimumGlConstants.TextureMinFilter, 9728);
                device.SetTextureParameter(texture, OptimumGlConstants.TextureMagFilter, 9728);
                return texture;
            }
        }
    }
}
