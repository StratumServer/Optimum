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
    private const int UpNormalFlags = 7 << 18;

    private static float[] CreateFaceRecord()
    {
        var record = new float[16];
        // unpackNormal normalizes the packed vector. An all-zero flags word
        // would normalize (0,0,0), producing NaNs on some drivers.
        Array.Fill(record, BitConverter.Int32BitsToSingle(UpNormalFlags), 8, 4);
        return record;
    }

    private static bool TryCreateDevice(ITestOutputHelper output, out VulkanDevice? device) =>
        GpuTest.TryCreateDevice(output, out device);

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
                flags: UpNormalFlags);
        }

        foreach (int index in new[] { 0, 1, 2, 0, 2, 3 })
        {
            mesh.AddIndex(index);
        }
        return mesh;
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public unsafe void TheTopsoilShaderSamplesTheGrassTileWithPackedSecondaryUvs(bool ssbo)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            var variant = ShaderCorpus.Variants().First();
            variant.UseSsbo = ssbo ? 1 : 0;
            int program = LinkFromCorpus(seam, ShaderCorpus.BuildProgram("chunktopsoil",
                ShaderCorpus.LoadShaderFiles(), ShaderCorpus.LoadIncludes(), variant), "chunktopsoil");
            int unit = BindEveryDeclaredSampler(device!, seam, program);

            // The top grass texture occupies tile (2,1), one tile to the right
            // of its packed UV origin, as in the real topsoil atlas. Signed
            // normalization doubles the UVs and sends them into a red tile.
            var atlasPixels = new byte[4 * 4 * 4];
            for (int i = 0; i < 16; i++)
            {
                atlasPixels[i * 4] = 255;
                atlasPixels[i * 4 + 3] = 255;
            }
            atlasPixels[6 * 4] = 0;
            atlasPixels[6 * 4 + 1] = 255;
            int atlas;
            fixed (byte* pixels = atlasPixels)
                atlas = seam.CreateTexture2D(4, 4, EnumTextureInternalFormat.Rgba8,
                    EnumTexturePixelFormat.Rgba, (IntPtr)pixels, false);
            foreach (string name in new[] { "terrainTex", "terrainTexLinear" })
            {
                seam.SetSamplerUnit(program, name, unit);
                seam.BindTexture(unit++, atlas);
            }

            MeshData face = BuildBlockFace();
            face.CustomShorts = new CustomMeshDataPartShort(8)
            {
                InterleaveSizes = new[] { 2 }, InterleaveOffsets = new[] { 0 },
                InterleaveStride = 4, Conversion = DataConversion.NormalizedFloat,
            };
            for (int i = 0; i < 4; i++)
                face.CustomShorts.AddPackedUV(0.375f, 0.375f, isU2: false, isV2: false);

            int mesh = seam.CreateEmptyMesh(48, 0, 32, 16, 16, 24,
                null, face.CustomShorts, null, null, EnumDrawMode.Triangles, false, ssbo);
            seam.UpdateMesh(mesh, face);
            if (ssbo)
            {
                var record = CreateFaceRecord();
                record[0] = -0.5f; record[1] = -0.5f;
                record[4] = 0.5f; record[13] = 0.5f;
                fixed (float* pixels = record)
                    seam.UpdateMeshStorageBuffer(mesh, (IntPtr)pixels, 0, 64);
            }

            int target = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
                EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(Size, Size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.SetDrawBuffers(framebuffer, 1);
            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.ClearColor(0, 1f, 0f, 1f, 1f);
            seam.UseProgram(program);
            SetIdentityMatrices(seam, program);
            SetViewUniforms(seam, program);
            seam.SetUniform(program, seam.GetUniformLocation(program, "blockTextureSize"), 0.25f, 0.25f);
            seam.SetViewport(0, 0, Size, Size);
            seam.SetDepthTest(false);
            seam.SetCullFace(true);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.DrawMeshMulti(mesh, new[] { 0, 0 }, new[] { 6 }, 1, ssbo);
            seam.Present();

            byte[] output = ReadTarget(seam, framebuffer);
            int centre = (Size / 2 * Size + Size / 2) * 4;
            Assert.True(output[centre + 1] > 20 && output[centre] < 5 && output[centre + 2] < 5,
                $"Expected grass green; got {output[centre]}, {output[centre + 1]}, {output[centre + 2]}");
            Assert.True(IsClearColour(output, 0), "The pooled face extended outside its geometry.");
            AssertClean(seam);
        }
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
            VulkanDevice seam = device!;

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
            VulkanDevice seam = device!;

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

    /// <summary>
    /// The path the world actually renders through: an SSBO pool, one packed
    /// face record in its storage slot, the fixed quad index pattern, the storage
    /// descriptor set, and culling on. The attribute-variant test above covers
    /// none of that, which is how a world of shards got past the suite.
    ///
    /// The record follows the shader's std430 FaceData - xyz, uv, xyzA, uvSize,
    /// flags[4], xyzB, colormapData, 64 bytes - and the decode is
    /// xyz + ((v+1)&amp;2)*xyzA + (v&amp;2)*xyzB, so xyzA and xyzB are half the
    /// quad's two edges. The flags encode an upward normal; their integer bits
    /// are preserved in the float array used to upload the record.
    /// </summary>
    [SkippableFact]
    public unsafe void TheSsboChunkPathDrawsAFaceFromAPackedRecordWithCullingOn()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            VulkanDevice seam = device!;

            var files = ShaderCorpus.LoadShaderFiles();
            var includes = ShaderCorpus.LoadIncludes();
            // The SSBO variant without greedy meshing: a greedy-meshed quad takes
            // its tile counts from the record's flags. This test exercises the
            // ordinary packed-face path, without greedy tiling.
            ShaderCorpus.ShaderVariant variant =
                ShaderCorpus.Variants().First(v => v.UseSsbo == 1 && v.GreedyMesh == 0);
            List<ShaderStageSource> stages =
                ShaderCorpus.BuildProgram("chunkopaque", files, includes, variant);
            Assert.NotEmpty(stages);

            int programId = LinkFromCorpus(seam, stages, "chunkopaque");
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

            // Four vertices and six indices, sized as the game sizes a pool: the
            // xyz figure is positions, and the device scales it for face records.
            int mesh = seam.CreateEmptyMesh(
                xyzSize: 4 * 12, normalsSize: 0, uvSize: 0, rgbaSize: 4 * 4, flagsSize: 0,
                indicesSize: 6 * sizeof(int), null, null, null, null,
                EnumDrawMode.Triangles, staticDraw: false, ssbo: true);
            Assert.True(mesh > 0, seam.GetError() ?? "SSBO mesh allocation failed");

            // In the game's order: the ordinary vertex data first, then the face
            // records. The MeshData carries a (zero) xyz array like the game's
            // does, which must not reach the storage slot - the device skips it
            // for an SSBO mesh, and this is where that is exercised.
            var colours = new MeshData(4, 6) { Rgba = new byte[16], RgbaOffset = 0, VerticesCount = 4 };
            Array.Fill(colours.Rgba, (byte)255);
            seam.UpdateMesh(mesh, colours);

            // A quad from (-0.5,-0.5) to (0.5,0.5): xyz is the first corner, xyzA
            // half of the edge to the second, xyzB half of the edge to the fourth.
            var record = CreateFaceRecord();
            record[0] = -0.5f; record[1] = -0.5f; record[2] = 0f;   // xyz
            record[4] = 0.5f;  record[5] = 0f;    record[6] = 0f;   // xyzA
            record[12] = 0f;   record[13] = 0.5f; record[14] = 0f;  // xyzB
            fixed (float* bytes = record)
            {
                seam.UpdateMeshStorageBuffer(mesh, (IntPtr)bytes, 0, 64);
            }

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.ClearColor(0, 1f, 0f, 1f, 1f);
            seam.ClearDepth(1f);
            seam.UseProgram(programId);
            SetIdentityMatrices(seam, programId);
            SetViewUniforms(seam, programId);
            seam.SetViewport(0, 0, Size, Size);
            seam.SetDepthTest(true);
            seam.SetDepthFunc(0x203);   // GL_LEQUAL
            // On, as the chunk pass has it. The quad winds counter-clockwise in
            // GL's terms, so it is a front face and must survive.
            seam.SetCullFace(true);
            seam.SetBlend(false, EnumBlendMode.Standard);

            seam.DrawMeshMulti(mesh, new[] { 0, 0 }, new[] { 6 }, 1, ssbo: true);
            seam.Present();

            int centre = (Size / 2 * Size + Size / 2) * 4;
            int corner = (2 * Size + 2) * 4;
            byte[] pixels = ReadTarget(seam, framebuffer);
            _output.WriteLine($"multi-draw, culling on: centre RGBA = {pixels[centre]}, {pixels[centre + 1]}, " +
                              $"{pixels[centre + 2]}, {pixels[centre + 3]}");
            bool rasterised = !IsClearColour(pixels, centre);

            // A failure is only useful if it says which part failed, so the same
            // face is tried again with culling off and through the single-draw
            // path, and the message reports what each of those did.
            string diagnosis = "";
            if (!rasterised)
            {
                seam.BeginFrame();
                seam.BindFramebuffer(framebuffer);
                seam.ClearColor(0, 1f, 0f, 1f, 1f);
                seam.ClearDepth(1f);
                seam.UseProgram(programId);
                seam.SetCullFace(false);
                seam.DrawMeshMulti(mesh, new[] { 0, 0 }, new[] { 6 }, 1, ssbo: true);
                seam.Present();
                bool withoutCulling = !IsClearColour(ReadTarget(seam, framebuffer), centre);

                seam.BeginFrame();
                seam.BindFramebuffer(framebuffer);
                seam.ClearColor(0, 1f, 0f, 1f, 1f);
                seam.ClearDepth(1f);
                seam.UseProgram(programId);
                seam.SetCullFace(true);
                seam.DrawMesh(mesh);
                seam.Present();
                bool singleDraw = !IsClearColour(ReadTarget(seam, framebuffer), centre);

                diagnosis = $" (culling off: {(withoutCulling ? "rasterised" : "nothing")}; " +
                            $"single draw with culling: {(singleDraw ? "rasterised" : "nothing")}; " +
                            $"diagnostics: {seam.GetError() ?? "none"})";
            }

            Assert.True(rasterised, "the face record did not rasterise through the multi-draw path" + diagnosis);
            Assert.True(IsClearColour(pixels, corner), "the face covered the whole target");

            AssertClean(seam);
        }
    }

    /// <summary>
    /// A face record's UV must reach the atlas as the record wrote it.
    ///
    /// The chunk shaders unpack UVs out of the storage record rather than a
    /// vertex attribute: <c>vdata.uv</c> is the origin as 16-bit fixed point and
    /// <c>vdata.uvSize</c> the span, and UnpackUv divides both by 32768. Both are
    /// ints sitting between the record's vec3s, so any disagreement between the
    /// struct C# writes and the one the shader reads lands there first - and
    /// misreading the span for the origin, or the other way round, maps a swathe
    /// of the atlas across a single block face instead of one block texture.
    ///
    /// So this draws the same face twice against a four-texel atlas, once with
    /// the origin on the red texel and once on the green one, with a zero span
    /// both times. Reading back a red face and then a green one is only possible
    /// if the record's origin arrived exactly and the span really was zero.
    /// </summary>
    [SkippableFact]
    public unsafe void AFaceRecordSamplesTheAtlasWhereItsPackedUvPointsTo()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            VulkanDevice seam = device!;

            var files = ShaderCorpus.LoadShaderFiles();
            var includes = ShaderCorpus.LoadIncludes();
            ShaderCorpus.ShaderVariant variant =
                ShaderCorpus.Variants().First(v => v.UseSsbo == 1 && v.GreedyMesh == 0);
            List<ShaderStageSource> stages =
                ShaderCorpus.BuildProgram("chunkopaque", files, includes, variant);
            Assert.NotEmpty(stages);

            int programId = LinkFromCorpus(seam, stages, "chunkopaque");
            int nextUnit = BindEveryDeclaredSampler(device!, seam, programId);

            // A two-by-two atlas: red, green on the bottom row, blue and white on
            // the top. Nearest filtering, so a UV inside a texel is that texel
            // and nothing is blended in from its neighbours.
            var atlasPixels = new byte[]
            {
                255, 0, 0, 255,      0, 255, 0, 255,
                0, 0, 255, 255,      255, 255, 255, 255,
            };
            int atlas;
            fixed (byte* source = atlasPixels)
            {
                atlas = seam.CreateTexture2D(2, 2,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)source, false);
            }
            seam.SetTextureParameter(atlas, OptimumGlConstants.TextureMinFilter, 9728);
            seam.SetTextureParameter(atlas, OptimumGlConstants.TextureMagFilter, 9728);

            // Both terrain samplers: the base texture and the one the colormap
            // include samples through.
            foreach (string samplerName in new[] { "terrainTex", "terrainTexLinear" })
            {
                seam.SetSamplerUnit(programId, samplerName, nextUnit);
                seam.BindTexture(nextUnit, atlas);
                nextUnit++;
            }

            int target = seam.CreateTexture2D(Size, Size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int depth = seam.CreateTexture2D(Size, Size,
                EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent,
                IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(Size, Size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.DepthAttachment, depth, 0);
            seam.SetDrawBuffers(framebuffer, 0b1);

            int mesh = seam.CreateEmptyMesh(
                xyzSize: 4 * 12, normalsSize: 0, uvSize: 0, rgbaSize: 4 * 4, flagsSize: 0,
                indicesSize: 6 * sizeof(int), null, null, null, null,
                EnumDrawMode.Triangles, staticDraw: false, ssbo: true);
            Assert.True(mesh > 0, seam.GetError() ?? "SSBO mesh allocation failed");

            var colours = new MeshData(4, 6) { Rgba = new byte[16], RgbaOffset = 0, VerticesCount = 4 };
            Array.Fill(colours.Rgba, (byte)255);
            seam.UpdateMesh(mesh, colours);

            // The record as FaceData writes it: xyz then the packed origin at
            // offset 12, the two half-edges, and the packed span at offset 28.
            byte[] DrawAt(float u, float v)
            {
                var record = CreateFaceRecord();
                record[0] = -0.5f; record[1] = -0.5f; record[2] = 0f;   // xyz
                record[4] = 0.5f;  record[5] = 0f;    record[6] = 0f;   // xyzA
                record[12] = 0f;   record[13] = 0.5f; record[14] = 0f;  // xyzB

                int packedUv = (int)(u * 32768f + 0.5f) + ((int)(v * 32768f + 0.5f) << 16);
                var record32 = new int[16];
                Buffer.BlockCopy(record, 0, record32, 0, 64);
                record32[3] = packedUv;   // uv
                record32[7] = 0;          // uvSize: no span, so every corner samples the origin

                fixed (int* bytes = record32)
                {
                    seam.UpdateMeshStorageBuffer(mesh, (IntPtr)bytes, 0, 64);
                }

                seam.BeginFrame();
                seam.BindFramebuffer(framebuffer);
                seam.ClearColor(0, 0f, 0f, 0f, 1f);
                seam.ClearDepth(1f);
                seam.UseProgram(programId);
                SetIdentityMatrices(seam, programId);
                SetViewUniforms(seam, programId);
                SetFloat(seam, programId, "subpixelPaddingX", 0f);
                SetFloat(seam, programId, "subpixelPaddingY", 0f);
                seam.SetViewport(0, 0, Size, Size);
                seam.SetDepthTest(true);
                seam.SetDepthFunc(0x203);
                seam.SetCullFace(false);
                seam.SetBlend(false, EnumBlendMode.Standard);
                seam.DrawMeshMulti(mesh, new[] { 0, 0 }, new[] { 6 }, 1, ssbo: true);
                seam.Present();
                return ReadTarget(seam, framebuffer);
            }

            int centre = (Size / 2 * Size + Size / 2) * 4;

            // The centre of the bottom-left texel, then of the bottom-right one.
            byte[] onRed = DrawAt(0.25f, 0.25f);
            byte[] onGreen = DrawAt(0.75f, 0.25f);

            _output.WriteLine($"origin on red   -> {onRed[centre]}, {onRed[centre + 1]}, {onRed[centre + 2]}");
            _output.WriteLine($"origin on green -> {onGreen[centre]}, {onGreen[centre + 1]}, {onGreen[centre + 2]}");

            // Lighting scales the sampled colour, so the check is which channel
            // came through, not how bright it is. Anything drawn at all rules out
            // a face that never rasterised.
            Assert.True(onRed[centre] > 0 || onGreen[centre + 1] > 0,
                "the face did not rasterise, so nothing was sampled");
            Assert.True(onRed[centre] > onRed[centre + 1],
                $"a UV origin on the red texel sampled elsewhere: " +
                $"{onRed[centre]}, {onRed[centre + 1]}, {onRed[centre + 2]}");
            Assert.True(onGreen[centre + 1] > onGreen[centre],
                $"a UV origin on the green texel sampled elsewhere: " +
                $"{onGreen[centre]}, {onGreen[centre + 1]}, {onGreen[centre + 2]}");

            AssertClean(seam);
        }
    }

    /// <summary>
    /// Most real chunk faces have a negative UV span, and the shader recovers it
    /// by sign extension rather than by reading a signed field.
    ///
    /// FaceData packs the span as two 15-bit fields and turns a negative delta
    /// into its positive complement first - a du of -1/128 is stored as 32512 -
    /// so UnpackUv subtracts 32768 back off whenever the field's top bit is set:
    /// <c>(uvs &amp; 0x7FFF) - ((uvs &amp; 0x4000) &lt;&lt; 1)</c> for u, and the same
    /// for v out of the high half. Lose either of those and the span flips from
    /// a fraction of a block texture to very nearly the whole atlas, which maps
    /// a swathe of unrelated block textures across every face.
    ///
    /// The packed values here are the ones a real world produced, read back out
    /// of the storage buffer at draw time.
    /// </summary>
    [SkippableFact]
    public unsafe void AFaceRecordWithANegativeUvSpanStaysOnItsOwnBlockTexture()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            VulkanDevice seam = device!;

            var files = ShaderCorpus.LoadShaderFiles();
            var includes = ShaderCorpus.LoadIncludes();
            ShaderCorpus.ShaderVariant variant =
                ShaderCorpus.Variants().First(v => v.UseSsbo == 1 && v.GreedyMesh == 0);
            List<ShaderStageSource> stages =
                ShaderCorpus.BuildProgram("chunkopaque", files, includes, variant);
            Assert.NotEmpty(stages);

            int programId = LinkFromCorpus(seam, stages, "chunkopaque");
            int nextUnit = BindEveryDeclaredSampler(device!, seam, programId);

            // An eight-by-eight atlas that is green everywhere except the one
            // texel this face's UVs fall in, which is red. A correct unpack sees
            // only that texel; a span that lost its sign sweeps most of the
            // atlas and drags the green in.
            const int atlasSize = 8;
            var atlasPixels = new byte[atlasSize * atlasSize * 4];
            for (int i = 0; i < atlasSize * atlasSize; i++)
            {
                atlasPixels[i * 4] = 0;
                atlasPixels[i * 4 + 1] = 255;
                atlasPixels[i * 4 + 2] = 0;
                atlasPixels[i * 4 + 3] = 255;
            }

            // uv origin (2048, 15488) / 32768 = (0.0625, 0.4727): column 0, row 3.
            int redTexel = (3 * atlasSize + 0) * 4;
            atlasPixels[redTexel] = 255;
            atlasPixels[redTexel + 1] = 0;

            int atlas;
            fixed (byte* source = atlasPixels)
            {
                atlas = seam.CreateTexture2D(atlasSize, atlasSize,
                    EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)source, false);
            }
            seam.SetTextureParameter(atlas, OptimumGlConstants.TextureMinFilter, 9728);
            seam.SetTextureParameter(atlas, OptimumGlConstants.TextureMagFilter, 9728);

            foreach (string samplerName in new[] { "terrainTex", "terrainTexLinear" })
            {
                seam.SetSamplerUnit(programId, samplerName, nextUnit);
                seam.BindTexture(nextUnit, atlas);
                nextUnit++;
            }

            int target = seam.CreateTexture2D(Size, Size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int depth = seam.CreateTexture2D(Size, Size,
                EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent,
                IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(Size, Size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.DepthAttachment, depth, 0);
            seam.SetDrawBuffers(framebuffer, 0b1);

            int mesh = seam.CreateEmptyMesh(
                xyzSize: 4 * 12, normalsSize: 0, uvSize: 0, rgbaSize: 4 * 4, flagsSize: 0,
                indicesSize: 6 * sizeof(int), null, null, null, null,
                EnumDrawMode.Triangles, staticDraw: false, ssbo: true);
            Assert.True(mesh > 0, seam.GetError() ?? "SSBO mesh allocation failed");

            var colours = new MeshData(4, 6) { Rgba = new byte[16], RgbaOffset = 0, VerticesCount = 4 };
            Array.Fill(colours.Rgba, (byte)255);
            seam.UpdateMesh(mesh, colours);

            var record = CreateFaceRecord();
            record[0] = -0.5f; record[1] = -0.5f; record[2] = 0f;
            record[4] = 0.5f;  record[5] = 0f;    record[6] = 0f;
            record[12] = 0f;   record[13] = 0.5f; record[14] = 0f;

            var record32 = new int[16];
            Buffer.BlockCopy(record, 0, record32, 0, 64);
            record32[3] = unchecked((int)0x3C800800);   // uv:     2048, 15488
            record32[7] = unchecked((int)0x7E007F00);   // uvSize: -256, -512

            fixed (int* bytes = record32)
            {
                seam.UpdateMeshStorageBuffer(mesh, (IntPtr)bytes, 0, 64);
            }

            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.ClearColor(0, 0f, 0f, 1f, 1f);
            seam.ClearDepth(1f);
            seam.UseProgram(programId);
            SetIdentityMatrices(seam, programId);
            SetViewUniforms(seam, programId);
            SetFloat(seam, programId, "subpixelPaddingX", 0f);
            SetFloat(seam, programId, "subpixelPaddingY", 0f);
            seam.SetViewport(0, 0, Size, Size);
            seam.SetDepthTest(true);
            seam.SetDepthFunc(0x203);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);
            seam.DrawMeshMulti(mesh, new[] { 0, 0 }, new[] { 6 }, 1, ssbo: true);
            seam.Present();

            byte[] pixels = ReadTarget(seam, framebuffer);

            // Four points spread across the face: with a span of a fraction of a
            // texel every one of them is the red texel, whereas a lost sign puts
            // three of the corners somewhere else entirely.
            foreach ((int x, int y) in new[] { (Size / 2, Size / 2), (24, 24), (40, 24), (24, 40) })
            {
                int at = (y * Size + x) * 4;
                _output.WriteLine($"({x},{y}) -> {pixels[at]}, {pixels[at + 1]}, {pixels[at + 2]}");
                Assert.True(pixels[at] > pixels[at + 1],
                    $"the face sampled off its own block texture at ({x},{y}): " +
                    $"{pixels[at]}, {pixels[at + 1]}, {pixels[at + 2]}");
            }

            AssertClean(seam);
        }
    }

    /// <summary>
    /// Rebinding a sampler's texture between two draws of the same mesh in one
    /// frame has to reach the second draw.
    ///
    /// This is the shape of the chunk pass: the renderer walks the atlas pages,
    /// binds page i to terrainTex and terrainTexLinear, draws the pools that
    /// belong to that page, and moves on - all within one frame and, for a mesh
    /// that spans pages, on the same mesh. A descriptor set cached per program
    /// and unit rather than per texture would serve the first page's atlas to
    /// every later draw, which puts real block textures on blocks they do not
    /// belong to.
    /// </summary>
    [SkippableFact]
    public unsafe void RebindingAnAtlasBetweenDrawsChangesWhatTheSecondDrawSamples()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        Skip.IfNot(TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");

        using (device)
        {
            VulkanDevice seam = device!;

            var files = ShaderCorpus.LoadShaderFiles();
            var includes = ShaderCorpus.LoadIncludes();
            ShaderCorpus.ShaderVariant variant =
                ShaderCorpus.Variants().First(v => v.UseSsbo == 1 && v.GreedyMesh == 0);
            List<ShaderStageSource> stages =
                ShaderCorpus.BuildProgram("chunkopaque", files, includes, variant);
            Assert.NotEmpty(stages);

            int programId = LinkFromCorpus(seam, stages, "chunkopaque");
            int nextUnit = BindEveryDeclaredSampler(device!, seam, programId);

            int SolidPage(byte r, byte g, byte b)
            {
                var texels = new byte[] { r, g, b, 255 };
                fixed (byte* source = texels)
                {
                    int id = seam.CreateTexture2D(1, 1,
                        EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)source, false);
                    seam.SetTextureParameter(id, OptimumGlConstants.TextureMinFilter, 9728);
                    seam.SetTextureParameter(id, OptimumGlConstants.TextureMagFilter, 9728);
                    return id;
                }
            }

            int firstPage = SolidPage(255, 0, 0);
            int secondPage = SolidPage(0, 255, 0);

            int terrainUnit = nextUnit;
            int linearUnit = nextUnit + 1;
            seam.SetSamplerUnit(programId, "terrainTex", terrainUnit);
            seam.SetSamplerUnit(programId, "terrainTexLinear", linearUnit);

            int target = seam.CreateTexture2D(Size, Size,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            int depth = seam.CreateTexture2D(Size, Size,
                EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent,
                IntPtr.Zero, false);
            int framebuffer = seam.CreateFramebuffer(Size, Size);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, target, 0);
            seam.AttachTexture(framebuffer, EnumFramebufferAttachment.DepthAttachment, depth, 0);
            seam.SetDrawBuffers(framebuffer, 0b1);

            int mesh = seam.CreateEmptyMesh(
                xyzSize: 4 * 12, normalsSize: 0, uvSize: 0, rgbaSize: 4 * 4, flagsSize: 0,
                indicesSize: 6 * sizeof(int), null, null, null, null,
                EnumDrawMode.Triangles, staticDraw: false, ssbo: true);
            Assert.True(mesh > 0, seam.GetError() ?? "SSBO mesh allocation failed");

            var colours = new MeshData(4, 6) { Rgba = new byte[16], RgbaOffset = 0, VerticesCount = 4 };
            Array.Fill(colours.Rgba, (byte)255);
            seam.UpdateMesh(mesh, colours);

            var record = CreateFaceRecord();
            record[0] = -0.5f; record[1] = -0.5f; record[2] = 0f;
            record[4] = 0.5f;  record[5] = 0f;    record[6] = 0f;
            record[12] = 0f;   record[13] = 0.5f; record[14] = 0f;
            fixed (float* bytes = record)
            {
                seam.UpdateMeshStorageBuffer(mesh, (IntPtr)bytes, 0, 64);
            }

            // Both pages drawn in one frame, exactly as the chunk pass does it.
            seam.BeginFrame();
            seam.BindFramebuffer(framebuffer);
            seam.ClearColor(0, 0f, 0f, 1f, 1f);
            seam.ClearDepth(1f);
            seam.UseProgram(programId);
            SetIdentityMatrices(seam, programId);
            SetViewUniforms(seam, programId);
            SetFloat(seam, programId, "subpixelPaddingX", 0f);
            SetFloat(seam, programId, "subpixelPaddingY", 0f);
            seam.SetViewport(0, 0, Size, Size);
            seam.SetDepthTest(false);
            seam.SetCullFace(false);
            seam.SetBlend(false, EnumBlendMode.Standard);

            seam.BindTexture(terrainUnit, firstPage);
            seam.BindTexture(linearUnit, firstPage);
            seam.DrawMeshMulti(mesh, new[] { 0, 0 }, new[] { 6 }, 1, ssbo: true);

            seam.BindTexture(terrainUnit, secondPage);
            seam.BindTexture(linearUnit, secondPage);
            seam.DrawMeshMulti(mesh, new[] { 0, 0 }, new[] { 6 }, 1, ssbo: true);

            seam.Present();

            byte[] pixels = ReadTarget(seam, framebuffer);
            int centre = (Size / 2 * Size + Size / 2) * 4;
            _output.WriteLine($"after rebinding to the green page -> " +
                              $"{pixels[centre]}, {pixels[centre + 1]}, {pixels[centre + 2]}");

            Assert.True(pixels[centre + 1] > pixels[centre],
                "the second draw kept sampling the first page's atlas: " +
                $"{pixels[centre]}, {pixels[centre + 1]}, {pixels[centre + 2]}");

            AssertClean(seam);
        }
    }

    private static unsafe byte[] ReadTarget(VulkanDevice seam, int framebuffer)
    {
        var pixels = new byte[Size * Size * 4];
        fixed (byte* destination = pixels)
        {
            seam.BindFramebuffer(framebuffer);
            seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
        }
        return pixels;
    }

    private static bool IsClearColour(byte[] pixels, int offset) =>
        pixels[offset] >= 250 && pixels[offset + 1] <= 5 && pixels[offset + 2] >= 250;

    private static int LinkFromCorpus(
        VulkanDevice seam, List<ShaderStageSource> stages, string name)
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
    /// <returns>The first texture unit the program did not claim.</returns>
    private static unsafe int BindEveryDeclaredSampler(
        VulkanDevice device, VulkanDevice seam, int programId)
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

        return unit;
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
    private static void SetViewUniforms(VulkanDevice seam, int programId)
    {
        SetFloat(seam, programId, "viewDistance", 1024f);
        SetFloat(seam, programId, "viewDistanceLod0", 1024f);
        SetFloat(seam, programId, "alphaTest", 0.001f);
        SetFloat(seam, programId, "zNear", 0.1f);
        SetFloat(seam, programId, "zFar", 1024f);
        SetFloat(seam, programId, "shadowRangeFar", 1024f);
        SetFloat(seam, programId, "shadowRangeNear", 64f);
        SetFloat(seam, programId, "shadowMapWidthInv", 1f);
        SetFloat(seam, programId, "shadowMapHeightInv", 1f);
        int ambient = seam.GetUniformLocation(programId, "rgbaAmbientIn");
        if (ambient >= 0) seam.SetUniform(programId, ambient, 1f, 1f, 1f);
        // The underwater include samples at gl_FragCoord / frameSize even in
        // an air scene. Leaving this at zero gives undefined texture reads.
        int frameSize = seam.GetUniformLocation(programId, "frameSize");
        if (frameSize >= 0) seam.SetUniform(programId, frameSize, (float)Size, (float)Size);
    }

    private static void SetFloat(VulkanDevice seam, int programId, string name, float value)
    {
        int location = seam.GetUniformLocation(programId, name);
        if (location >= 0) seam.SetUniform(programId, location, value);
    }

    /// <summary>
    /// The matrices every chunk program multiplies by. Identity leaves the mesh's
    /// clip-space positions alone, which is what makes the output checkable.
    /// </summary>
    private static void SetIdentityMatrices(VulkanDevice seam, int programId)
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

    private static void AssertClean(VulkanDevice seam) => GpuTest.AssertClean(seam);
}
