using System;
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan;
using Optimum.Render.Vulkan.Shaders;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Terrain drawn with the game's own chunk shader, through the seam and nothing
/// else.
///
/// Everything else about chunks in this project stops short of a draw: the corpus
/// proves the shaders become SPIR-V, and ChunkRenderPathTests proves they become
/// pipelines. Neither puts a block on screen. These build the vertex data a
/// tesselated chunk actually carries - positions, UVs, per-vertex colour and the
/// packed render-flags word, each in its own buffer, exactly as
/// AllocateEmptyMesh lays them out - bind the real chunkopaque program with its
/// real samplers and matrices, draw, and read the pixels back.
///
/// A world in the client needs a signed-in account, so this is terrain rendered
/// through the backend without one: real shader, real vertex format, real device
/// path, real pixels.
/// </summary>
public class ChunkTerrainRenderTests
{
    private readonly ITestOutputHelper _output;

    public ChunkTerrainRenderTests(ITestOutputHelper output) => _output = output;

    private const int Size = 64;

    private static bool TryCreateDevice(ITestOutputHelper output, out VulkanDevice? device)
    {
        var created = new VulkanDevice { DebugMode = true };
        if (created.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            device = created;
            return true;
        }

        output.WriteLine("Vulkan unavailable: " + failureReason);
        created.Dispose();
        device = null;
        return false;
    }

    private sealed class Shader : IShader
    {
        public EnumShaderType Type { get; set; }
        public string Code { get; set; } = "";
        public string PrefixCode { get; set; } = "";
        public bool Compile() => true;
    }

    private sealed class Program : IShaderProgram
    {
        public int ProgramId { get; set; }
        public string AssetDomain { get; set; } = "game";
        public int PassId { get; set; }
        public string PassName { get; set; } = "chunkopaque";
        public bool ClampTexturesToEdge { get; set; }
        public IShader VertexShader { get; set; } = null!;
        public IShader FragmentShader { get; set; } = null!;
        public IShader GeometryShader { get; set; } = null!;
        public bool Oit { get; set; } = true;
        public bool Disposed => false;
        public bool LoadError => false;
        public Vintagestory.API.Datastructures.OrderedDictionary<string, UBORef> UBOs { get; } = new();
        public bool Compile() => true;
        public bool HasUniform(string uniformName) => false;
        public void Use() { }
        public void Stop() { }
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
    }

    /// <summary>
    /// One tesselated block face, in the layout the chunk tesselator emits.
    ///
    /// Positions are chunk-local, UVs index the block atlas, the colour carries
    /// baked light, and the flags word packs glow, z-offset, waving bits and the
    /// normal - the field whose undefined value made the whole interface vanish
    /// when vertex-attribute defaults were missing.
    /// </summary>
    private static MeshData BuildBlockFace()
    {
        var mesh = new MeshData(4, 6, withNormals: false, withUv: true, withRgba: true, withFlags: true);

        // A quad covering the middle of the viewport in clip space, so the draw
        // is checkable by reading the centre pixel.
        float[] positions =
        {
            -0.5f, -0.5f, 0f,
             0.5f, -0.5f, 0f,
             0.5f,  0.5f, 0f,
            -0.5f,  0.5f, 0f,
        };
        float[] uvs = { 0f, 0f, 1f, 0f, 1f, 1f, 0f, 1f };

        for (int i = 0; i < 4; i++)
        {
            mesh.AddVertexWithFlags(
                positions[i * 3], positions[i * 3 + 1], positions[i * 3 + 2],
                uvs[i * 2], uvs[i * 2 + 1],
                Vintagestory.API.MathTools.ColorUtil.WhiteArgb,
                // Normal pointing up, no glow, no waving: the flags word a solid
                // top face carries.
                flags: 0);
        }

        foreach (int index in new[] { 0, 1, 2, 0, 2, 3 })
        {
            mesh.AddIndex(index);
        }
        return mesh;
    }

    /// <summary>
    /// The real chunkopaque program, drawing a real tesselated face, checked by
    /// reading the pixels back.
    /// </summary>
    [SkippableFact]
    public unsafe void TheRealChunkShaderDrawsTesselatedTerrain()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            IOptimumGraphicsDevice seam = device!;

            var files = ShaderCorpus.LoadShaderFiles();
            var includes = ShaderCorpus.LoadIncludes();
            ShaderCorpus.ShaderVariant variant = ShaderCorpus.Variants().First();

            List<ShaderStageSource> stages =
                ShaderCorpus.BuildProgram("chunkopaque", files, includes, variant);
            Assert.NotEmpty(stages);

            int programId = LinkFromCorpus(seam, stages, "chunkopaque");

            // Every sampler the program declares needs something bound, or the
            // draw is skipped rather than drawn - the descriptor would be
            // incomplete. A single white texel stands in for the block atlas.
            BindEveryDeclaredSampler(device!, seam, programId);

            int target = seam.CreateTexture2D(Size, Size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int depth = seam.CreateTexture2D(Size, Size,
                EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent,
                IntPtr.Zero, false);

            int framebuffer = seam.CreateFramebuffer(Size, Size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.DepthAttachment, depth, 0);
            seam.SetDrawBuffers(framebuffer, 0b1);
            Assert.True(seam.CheckFramebufferComplete(framebuffer, out string status), status);

            int mesh = seam.CreateMesh(BuildBlockFace(), staticDraw: true);
            Assert.True(mesh > 0, seam.GetError() ?? "mesh upload failed");

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            // Cleared to magenta rather than black, because chunkopaque's output
            // depends on lighting, fog and atlas contents that are all zero here -
            // it legitimately shades to black. Testing "the pixel is lit" would
            // then be indistinguishable from "nothing drew". Testing "the pixel
            // changed" detects rasterisation whatever the shader decides to emit.
            seam.ClearColor(0, 1f, 0f, 1f, 1f);
            seam.ClearDepth(1f);

            seam.UseProgram(programId);
            SetIdentityMatrices(seam, programId);
            SetViewUniforms(seam, programId);

            seam.SetViewport(0, 0, Size, Size);
            seam.SetDepthTest(true);
            seam.SetDepthFunc(0x203);   // GL_LEQUAL
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);

            seam.DrawMesh(mesh);
            seam.Present();

            var pixels = new byte[Size * Size * 4];
            fixed (byte* destination = pixels)
            {
                seam.BindFramebuffer(framebuffer);
                seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
            }

            int centre = (Size / 2 * Size + Size / 2) * 4;
            int corner = (2 * Size + 2) * 4;

            _output.WriteLine($"centre RGBA = {pixels[centre]}, {pixels[centre + 1]}, " +
                              $"{pixels[centre + 2]}, {pixels[centre + 3]}");
            _output.WriteLine($"corner RGBA = {pixels[corner]}, {pixels[corner + 1]}, " +
                              $"{pixels[corner + 2]}, {pixels[corner + 3]}");

            // The face covers the middle and nothing else: geometry actually
            // rasterised, in the right place, and did not cover the whole target.
            bool centreChanged = !IsClearColour(pixels, centre);
            bool cornerUntouched = IsClearColour(pixels, corner);

            Assert.True(centreChanged, "the block face did not rasterise");
            Assert.True(cornerUntouched, "the block face covered the whole target");

            AssertClean(seam);
        }
    }

    /// <summary>
    /// The same face through the shadow-map program, which renders depth only and
    /// is the pass every shadowed chunk goes through first.
    /// </summary>
    [SkippableFact]
    public void TheShadowMapProgramDrawsTerrainIntoADepthOnlyTarget()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            IOptimumGraphicsDevice seam = device!;

            var files = ShaderCorpus.LoadShaderFiles();
            var includes = ShaderCorpus.LoadIncludes();

            List<ShaderStageSource> stages = ShaderCorpus.BuildProgram(
                "chunkshadowmap", files, includes, ShaderCorpus.Variants().First());
            Assert.NotEmpty(stages);

            int programId = LinkFromCorpus(seam, stages, "chunkshadowmap");
            BindEveryDeclaredSampler(device!, seam, programId);

            int depth = seam.CreateTexture2D(Size, Size,
                EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent,
                IntPtr.Zero, false);

            int framebuffer = seam.CreateFramebuffer(Size, Size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.DepthAttachment, depth, 0);
            seam.SetDrawBuffers(framebuffer, 0);

            int mesh = seam.CreateMesh(BuildBlockFace(), staticDraw: true);
            Assert.True(mesh > 0, seam.GetError() ?? "mesh upload failed");

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.ClearDepth(1f);

            seam.UseProgram(programId);
            SetIdentityMatrices(seam, programId);
            SetViewUniforms(seam, programId);

            seam.SetViewport(0, 0, Size, Size);
            seam.SetDepthTest(true);
            seam.SetDepthMask(true);
            seam.SetDepthFunc(0x203);
            seam.SetCullFace(false);

            seam.DrawMesh(mesh);
            seam.Present();

            // A depth-only pass has nothing to read back as colour; what matters
            // is that it recorded and submitted without the device refusing the
            // draw or the validation layer objecting.
            AssertClean(seam);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>The magenta the target was cleared to, within 8-bit rounding.</summary>
    private static bool IsClearColour(byte[] pixels, int offset) =>
        pixels[offset] >= 250 && pixels[offset + 1] <= 5 && pixels[offset + 2] >= 250;

    private static int LinkFromCorpus(
        IOptimumGraphicsDevice seam, List<ShaderStageSource> stages, string name)
    {
        var program = new Program { PassName = name };

        foreach (ShaderStageSource stage in stages)
        {
            var shader = new Shader
            {
                Type = stage.Stage,
                Code = stage.Code,
                PrefixCode = stage.PrefixCode ?? "",
            };
            Assert.True(seam.CompileShader(shader), name + ": " + (seam.GetError() ?? "compile failed"));

            if (stage.Stage == EnumShaderType.VertexShader) program.VertexShader = shader;
            else if (stage.Stage == EnumShaderType.FragmentShader) program.FragmentShader = shader;
            else program.GeometryShader = shader;
        }

        int programId = seam.LinkProgram(program);
        Assert.True(programId > 0, name + ": " + (seam.GetError() ?? "link failed"));
        return programId;
    }

    /// <summary>
    /// Binds a one-texel white texture to every sampler the program declares.
    ///
    /// The device skips a draw whose descriptor set is incomplete, which is the
    /// right behaviour but would make this test pass by not drawing at all. The
    /// real client binds the atlases; here a stand-in is enough to make the draw
    /// legal.
    /// </summary>
    private static unsafe void BindEveryDeclaredSampler(
        VulkanDevice device, IOptimumGraphicsDevice seam, int programId)
    {
        var white = new byte[] { 255, 255, 255, 255 };
        int unit = 0;

        foreach (string samplerName in device.SamplerNamesOf(programId))
        {
            int texture;
            fixed (byte* pixels = white)
            {
                texture = seam.CreateTexture2D(1, 1,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)pixels, false);
            }
            seam.SetSamplerUnit(programId, samplerName, unit);
            seam.BindTexture(unit, texture);
            unit++;
        }
    }

    /// <summary>
    /// The scalar uniforms the chunk passes need in order to draw anything.
    ///
    /// These are not decoration. chunkopaque computes
    /// <c>aTest = outColor.a + ... - lod0Fade</c> and discards when it falls below
    /// alphaTest, and lod0Fade is derived from the view distances - left at zero,
    /// every fragment in the world fades out and the pass draws nothing at all.
    /// The client sets them each frame; a test that does not is testing a
    /// configuration the game never runs.
    /// </summary>
    private static void SetViewUniforms(IOptimumGraphicsDevice seam, int programId)
    {
        SetFloat(seam, programId, "viewDistance", 1024f);
        SetFloat(seam, programId, "viewDistanceLod0", 1024f);
        SetFloat(seam, programId, "alphaTest", 0.001f);
        SetFloat(seam, programId, "zNear", 0.1f);
        SetFloat(seam, programId, "zFar", 1024f);
    }

    private static void SetFloat(IOptimumGraphicsDevice seam, int programId, string name, float value)
    {
        int location = seam.GetUniformLocation(programId, name);
        if (location >= 0) seam.SetUniform(programId, location, value);
    }

    /// <summary>
    /// The matrices every chunk program multiplies by. Identity leaves the mesh's
    /// clip-space positions alone, which is what makes the output checkable.
    /// </summary>
    private static void SetIdentityMatrices(IOptimumGraphicsDevice seam, int programId)
    {
        float[] identity =
        {
            1, 0, 0, 0,
            0, 1, 0, 0,
            0, 0, 1, 0,
            0, 0, 0, 1,
        };

        foreach (string name in new[]
        {
            "projectionMatrix", "modelViewMatrix", "modelMatrix", "mvpMatrix",
            "toShadowMapSpaceMatrixFar", "toShadowMapSpaceMatrixNear",
        })
        {
            int location = seam.GetUniformLocation(programId, name);
            if (location >= 0) seam.SetUniformMatrix(programId, location, identity);
        }
    }

    private static void AssertClean(IOptimumGraphicsDevice seam)
    {
        string? diagnostics = seam.GetError();
        Assert.True(string.IsNullOrEmpty(diagnostics), "device diagnostics:\n" + diagnostics);
    }
}
