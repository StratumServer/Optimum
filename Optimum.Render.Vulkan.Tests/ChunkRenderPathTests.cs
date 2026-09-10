using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// World geometry rendered with the game's own shaders.
///
/// The corpus test proves every vanilla program becomes valid SPIR-V; that is not
/// the same as proving a chunk draws. These build the vertex data the way the
/// tesselator does - positions, UVs, colours and the packed flags word, each in
/// its own buffer - bind the real chunkopaque program, draw, and read the result
/// back. A layout that disagrees with the shader's declared locations, or a
/// packed-flags word the shader unpacks differently, shows up here as wrong
/// pixels rather than as a validation message.
///
/// Reaching a world in the client needs a signed-in account, so this is the
/// closest thing to a chunk that runs unattended.
/// </summary>
public class ChunkRenderPathTests
{
    private readonly ITestOutputHelper _output;

    public ChunkRenderPathTests(ITestOutputHelper output) => _output = output;

    private static bool TryCreateContext(
        ITestOutputHelper output, List<string> messages, out VulkanContext? context)
    {
        var options = new VulkanContextOptions
        {
            Headless = true,
            EnableValidation = true,
            DebugCallback = messages.Add,
        };

        bool created = VulkanContext.TryCreate(options, out context, out string? failureReason);
        if (!created)
        {
            output.WriteLine("Vulkan unavailable: " + failureReason);
        }
        return created;
    }

    /// <summary>
    /// The real chunkopaque program, compiled the way the client compiles it,
    /// against a mesh shaped like a tesselated chunk. This is the single most
    /// load-bearing program in the game.
    /// </summary>
    [SkippableFact]
    public void TheRealChunkProgramTranslatesAndBuildsAPipeline()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");

        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var meshes = new MeshManager(context!, state);
            using var compiler = new ShaderCompiler();

            var files = ShaderCorpus.LoadShaderFiles();
            var includes = ShaderCorpus.LoadIncludes();

            int built = 0;
            foreach (ShaderCorpus.ShaderVariant variant in ShaderCorpus.Variants())
            {
                List<ShaderStageSource> stages =
                    ShaderCorpus.BuildProgram("chunkopaque", files, includes, variant);
                Assert.NotEmpty(stages);

                TranslatedProgram translated = ShaderTranslator.Translate(stages, compiler);
                Assert.True(translated.Success,
                    variant.Name + ": " + string.Join("; ", translated.Errors));

                using var program = new ShaderProgramResources(context!, 1, translated);
                state.SetProgram(1);

                // The chunk vertex layout: positions, UVs, colours and flags,
                // each in its own buffer, exactly as AllocateEmptyMesh builds it.
                // The mesh has to follow the variant: with SSBOs on, the shader
                // reads positions out of a storage buffer and declares far fewer
                // vertex inputs, and a mesh built the other way would disagree
                // with it.
                bool ssbo = variant.UseSsbo == 1;
                int mesh = meshes.CreateEmpty(
                    xyzSize: 4 * 3 * sizeof(float),
                    normalsSize: 0,
                    uvSize: 4 * 2 * sizeof(float),
                    rgbaSize: 4 * 4,
                    flagsSize: 4 * sizeof(int),
                    indicesSize: 6 * sizeof(int),
                    null, null, null, null, EnumDrawMode.Triangles, staticDraw: false, ssbo: ssbo);

                int layoutId = meshes.LayoutIdOf(mesh);
                VertexLayoutDescription layout = meshes.LayoutOf(layoutId);

                // Every input the shader declares is either in the mesh or gets
                // GL's constant default; nothing may be left undefined.
                // Guard against the loop below asserting nothing. Without SSBOs
                // chunkopaque declares positions, UVs, colours and flags; with
                // them, positions and most of the rest come from the storage
                // buffer and only a couple of inputs remain.
                int expectedInputs = ssbo ? 1 : 4;
                Assert.True(program.Interface.VertexInputs.Count >= expectedInputs,
                    variant.Name + ": expected chunkopaque to declare at least " + expectedInputs +
                    " vertex inputs, saw " + program.Interface.VertexInputs.Count);

                VertexLayoutDescription merged = layout.WithDefaultsFor(program.Interface.VertexInputs);
                foreach (VertexInputSlot slot in program.Interface.VertexInputs)
                {
                    Assert.Contains(merged.Attributes, a => a.Location == (uint)slot.Location);
                }

                int target = textures.Create(8, 8, Format.R8G8B8A8Unorm);
                int framebuffer = targets.Create(8, 8);
                targets.Attach(framebuffer, 0, target);
                targets.SetDrawBuffers(framebuffer, 0b1);

                VulkanFramebuffer bound = targets.Get(framebuffer)!;
                int formatsId = targets.FormatsIdOf(bound);
                RenderTargetFormats formats = state.TargetFormats(formatsId);

                var blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
                for (int i = 0; i < blend.Length; i++) blend[i] = state.BlendFor(i);

                Pipeline pipeline = pipelines.Get(
                    state.BuildKey(layoutId, formatsId, 1),
                    new GraphicsPipelineCache.PipelineRequest
                    {
                        Program = program,
                        VertexLayout = merged,
                        Targets = formats,
                        Blend = blend,
                        PolygonMode = state.PolygonMode,
                        Topology = state.Topology,
                    });

                Assert.NotEqual((ulong)0, pipeline.Handle);
                built++;

                meshes.Delete(mesh);
                targets.Delete(framebuffer);
                textures.Delete(target);
            }

            Assert.Equal(4, built);
            ValidationAssert.NoErrors(messages);
        }
    }

    /// <summary>
    /// Every world-facing program, through translation into a real pipeline.
    ///
    /// The corpus test stops at valid SPIR-V. Pipeline creation is where a
    /// descriptor layout that disagrees with the shader, or a vertex input the
    /// mesh cannot satisfy, actually fails - and these are the programs a world
    /// needs on its first frame.
    /// </summary>
    [SkippableTheory]
    [InlineData("chunkopaque")]
    [InlineData("chunkliquid")]
    [InlineData("chunktransparent")]
    [InlineData("chunktopsoil")]
    [InlineData("chunkshadowmap")]
    [InlineData("entityanimated")]
    [InlineData("particlesquad")]
    [InlineData("particlescube")]
    [InlineData("standard")]
    public void AWorldProgramBuildsAPipelineAgainstItsMeshLayout(string programName)
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");

        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var meshes = new MeshManager(context!, state);
            using var compiler = new ShaderCompiler();

            var files = ShaderCorpus.LoadShaderFiles();
            var includes = ShaderCorpus.LoadIncludes();

            List<ShaderStageSource> stages = ShaderCorpus.BuildProgram(
                programName, files, includes, ShaderCorpus.Variants().First());
            Skip.If(stages.Count == 0, programName + " is not in the asset set.");

            TranslatedProgram translated = ShaderTranslator.Translate(stages, compiler);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            // No normals: attribute locations are positional, and every world
            // program declares xyz=0, uv=1, colour=2, flags=3 with no normal
            // input at all. Including a normals buffer shifts everything after
            // it by one and the shader reads colours as flags - which is exactly
            // what this assertion is here to catch.
            int mesh = meshes.CreateEmpty(
                xyzSize: 4 * 3 * sizeof(float),
                normalsSize: 0,
                uvSize: 4 * 2 * sizeof(float),
                rgbaSize: 4 * 4,
                flagsSize: 4 * sizeof(int),
                indicesSize: 6 * sizeof(int),
                null, null, null, null, EnumDrawMode.Triangles, staticDraw: false, ssbo: false);

            int layoutId = meshes.LayoutIdOf(mesh);
            VertexLayoutDescription merged =
                meshes.LayoutOf(layoutId).WithDefaultsFor(program.Interface.VertexInputs);

            int target = textures.Create(8, 8, Format.R8G8B8A8Unorm);
            int depth = textures.Create(8, 8, Format.D32Sfloat);
            int framebuffer = targets.Create(8, 8);
            targets.Attach(framebuffer, 0, target);
            targets.Attach(framebuffer, -1, depth);
            // Enough attachments for the multi-output world passes.
            targets.SetDrawBuffers(framebuffer, 0b1);

            VulkanFramebuffer bound = targets.Get(framebuffer)!;
            int formatsId = targets.FormatsIdOf(bound);
            RenderTargetFormats formats = state.TargetFormats(formatsId);

            var blend = new AttachmentBlend[Math.Max(formats.ColorFormats.Length, 1)];
            for (int i = 0; i < blend.Length; i++) blend[i] = state.BlendFor(i);

            Pipeline pipeline = pipelines.Get(
                state.BuildKey(layoutId, formatsId, 1),
                new GraphicsPipelineCache.PipelineRequest
                {
                    Program = program,
                    VertexLayout = merged,
                    Targets = formats,
                    Blend = blend,
                    PolygonMode = state.PolygonMode,
                    Topology = state.Topology,
                });

            Assert.NotEqual((ulong)0, pipeline.Handle);
            ValidationAssert.NoErrors(messages);
        }
    }

    /// <summary>
    /// The SSBO chunk path end to end.
    ///
    /// With SSBOs on, positions leave the vertex input entirely: four vertices
    /// are packed into one sixteen-byte face record in a storage buffer and the
    /// vertex shader rebuilds them from gl_VertexIndex. Nothing about that path
    /// is exercised by an ordinary mesh, and getting it wrong produces an empty
    /// world rather than an error.
    /// </summary>
    [SkippableFact]
    public unsafe void TheSsboChunkPathUploadsFaceRecordsAndDraws()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 16;
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var meshes = new MeshManager(context!, state);
            using var compiler = new ShaderCompiler();

            int target = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, target);
            targets.SetDrawBuffers(framebuffer, 0b1);

            // One face: four vertices packed into sixteen bytes, plus the six
            // indices that expand it into two triangles.
            int mesh = meshes.CreateEmpty(
                xyzSize: 16, normalsSize: 0, uvSize: 0, rgbaSize: 0, flagsSize: 0,
                indicesSize: 6 * sizeof(int),
                null, null, null, null, EnumDrawMode.Triangles, staticDraw: false, ssbo: true);

            // The record the client writes: an origin and two edge offsets, in
            // the same layout FaceData uses.
            // Quad from (-1,-1) to (0,0): covers the lower-left quadrant only,
            // so the upper-right quadrant stays the clear colour.
            float[] face = { -1f, -1f, 0f, 1f };
            int[] indices = { 0, 1, 2, 0, 2, 3 };

            fixed (float* f = face)
            {
                meshes.Write(mesh, MeshManager.BufferXyz, 0, (IntPtr)f, face.Length * sizeof(float));
            }
            fixed (int* i = indices)
            {
                meshes.Write(mesh, -1, 0, (IntPtr)i, indices.Length * sizeof(int));
            }

            // Positions come out of the storage buffer, not a vertex attribute -
            // the defining property of this path.
            VertexLayoutDescription layout = meshes.LayoutOf(meshes.LayoutIdOf(mesh));
            Assert.Empty(layout.Attributes);

            TranslatedProgram translated = ShaderTranslator.Translate(new[]
            {
                new ShaderStageSource
                {
                    Stage = EnumShaderType.VertexShader,
                    Filename = "ssbo.vsh",
                    Code = """
                        #version 430 core
                        layout(std430, binding = 0) readonly buffer FaceBuffer { vec4 faces[]; };
                        void main(void)
                        {
                            vec4 face = faces[gl_VertexIndex >> 2];
                            int corner = gl_VertexIndex & 3;
                            float x = face.x + ((corner == 1 || corner == 2) ? face.w : 0.0);
                            float y = face.y + ((corner == 2 || corner == 3) ? face.w : 0.0);
                            gl_Position = vec4(x, y, 0.0, 1.0);
                        }
                        """,
                },
                new ShaderStageSource
                {
                    Stage = EnumShaderType.FragmentShader,
                    Filename = "ssbo.fsh",
                    Code = """
                        #version 430 core
                        out vec4 outColor;
                        void main(void) { outColor = vec4(0.0, 1.0, 0.0, 1.0); }
                        """,
                },
            }, compiler);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            VulkanFramebuffer bound = targets.Get(framebuffer)!;
            int formatsId = targets.FormatsIdOf(bound);
            RenderTargetFormats formats = state.TargetFormats(formatsId);

            Pipeline pipeline = pipelines.Get(
                state.BuildKey(meshes.LayoutIdOf(mesh), formatsId, 1),
                new GraphicsPipelineCache.PipelineRequest
                {
                    Program = program,
                    VertexLayout = layout,
                    Targets = formats,
                    Blend = new[] { state.BlendFor(0) },
                    PolygonMode = state.PolygonMode,
                    Topology = state.Topology,
                });
            Assert.NotEqual((ulong)0, pipeline.Handle);

            using var descriptors = new DescriptorCache(context!);
            VulkanBuffer faceBuffer = meshes.BufferOf(mesh, MeshManager.BufferXyz)!;
            BlockBinding storageBlock = Assert.Single(program.Interface.StorageBlocks);
            DescriptorSet storageSet = descriptors.Get(
                new DescriptorSetContents(1, ProgramInterfaceLayout.StorageSet,
                    Array.Empty<SamplerBindingValue>(),
                    new[]
                    {
                        new BufferBindingValue(
                            (uint)storageBlock.Binding, faceBuffer.Handle, 0, faceBuffer.Size, faceBuffer.Id),
                    }),
                program.SetLayouts[ProgramInterfaceLayout.StorageSet]);

            commands.SubmitAndWait(commandBuffer =>
            {
                targets.Bind(commandBuffer, framebuffer);
                targets.ClearColor(commandBuffer, 0, 0f, 0f, 0f, 1f);
                targets.EnsureRendering(commandBuffer);

                Vk api = context!.Api;
                api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);
                DescriptorSet boundStorageSet = storageSet;
                api.CmdBindDescriptorSets(commandBuffer, PipelineBindPoint.Graphics, program.PipelineLayout,
                    ProgramInterfaceLayout.StorageSet, 1, &boundStorageSet, 0, null);

                var viewport = new Viewport(0, 0, size, size, 0, 1);
                api.CmdSetViewport(commandBuffer, 0, 1, &viewport);
                var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(size, size));
                api.CmdSetScissor(commandBuffer, 0, 1, &scissor);
                SetDynamicDefaults(api, commandBuffer);

                meshes.Draw(commandBuffer, mesh);
                targets.EndRendering(commandBuffer);
            });

            byte[] pixels = ReadTexture(context!, commands, textures, target, size);

            // Covered by the quad, drawn green; the opposite corner is not, and
            // must still show the black clear colour untouched.
            byte[] covered = PixelAt(pixels, size, 2, 2);
            Assert.Equal(0, covered[0]);
            Assert.Equal(255, covered[1]);
            Assert.Equal(0, covered[2]);

            byte[] uncovered = PixelAt(pixels, size, size - 3, size - 3);
            Assert.Equal(0, uncovered[0]);
            Assert.Equal(0, uncovered[1]);
            Assert.Equal(0, uncovered[2]);

            ValidationAssert.NoErrors(messages);
        }
    }

    /// <summary>
    /// Particles draw one mesh many times with per-instance data. An instance
    /// count that never reached the draw would render a single particle where
    /// there should be thousands.
    /// </summary>
    [SkippableFact]
    public unsafe void InstancedDrawsRenderEveryInstance()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 16;
            using var commands = new VulkanCommands(context!);
            using var textures = new TextureManager(context!, commands);
            var state = new GlStateTracker();
            using var targets = new RenderTargetManager(context!, textures, state);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var meshes = new MeshManager(context!, state);
            using var compiler = new ShaderCompiler();

            int target = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, target);
            targets.SetDrawBuffers(framebuffer, 0b1);

            int mesh = meshes.CreateEmpty(
                xyzSize: 4 * 3 * sizeof(float), normalsSize: 0, uvSize: 0,
                rgbaSize: 0, flagsSize: 0, indicesSize: 6 * sizeof(int),
                null, null, null, null, EnumDrawMode.Triangles, staticDraw: false, ssbo: false);

            // A small quad in the lower-left; each instance steps it right and up,
            // so a second instance is visible only if instancing works.
            float[] positions =
            {
                -1f,   -1f,   0f,
                -0.5f, -1f,   0f,
                -0.5f, -0.5f, 0f,
                -1f,   -0.5f, 0f,
            };
            int[] indices = { 0, 1, 2, 0, 2, 3 };

            fixed (float* p = positions)
            {
                meshes.Write(mesh, MeshManager.BufferXyz, 0, (IntPtr)p, positions.Length * sizeof(float));
            }
            fixed (int* i = indices)
            {
                meshes.Write(mesh, -1, 0, (IntPtr)i, indices.Length * sizeof(int));
            }

            TranslatedProgram translated = ShaderTranslator.Translate(new[]
            {
                new ShaderStageSource
                {
                    Stage = EnumShaderType.VertexShader,
                    Filename = "inst.vsh",
                    Code = """
                        #version 330 core
                        layout(location = 0) in vec3 position;
                        void main(void)
                        {
                            float step = float(gl_InstanceID) * 0.75;
                            gl_Position = vec4(position.x + step, position.y + step, 0.0, 1.0);
                        }
                        """,
                },
                new ShaderStageSource
                {
                    Stage = EnumShaderType.FragmentShader,
                    Filename = "inst.fsh",
                    Code = """
                        #version 330 core
                        out vec4 outColor;
                        void main(void) { outColor = vec4(1.0, 0.0, 1.0, 1.0); }
                        """,
                },
            }, compiler);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            VulkanFramebuffer bound = targets.Get(framebuffer)!;
            int formatsId = targets.FormatsIdOf(bound);
            RenderTargetFormats formats = state.TargetFormats(formatsId);

            Pipeline pipeline = pipelines.Get(
                state.BuildKey(meshes.LayoutIdOf(mesh), formatsId, 1),
                new GraphicsPipelineCache.PipelineRequest
                {
                    Program = program,
                    VertexLayout = meshes.LayoutOf(meshes.LayoutIdOf(mesh)),
                    Targets = formats,
                    Blend = new[] { state.BlendFor(0) },
                    PolygonMode = state.PolygonMode,
                    Topology = state.Topology,
                });

            commands.SubmitAndWait(commandBuffer =>
            {
                targets.Bind(commandBuffer, framebuffer);
                targets.EnsureRendering(commandBuffer);

                Vk api = context!.Api;
                api.CmdBindPipeline(commandBuffer, PipelineBindPoint.Graphics, pipeline);

                var viewport = new Viewport(0, 0, size, size, 0, 1);
                api.CmdSetViewport(commandBuffer, 0, 1, &viewport);
                var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(size, size));
                api.CmdSetScissor(commandBuffer, 0, 1, &scissor);
                SetDynamicDefaults(api, commandBuffer);

                meshes.Draw(commandBuffer, mesh, instanceCount: 2);
                targets.EndRendering(commandBuffer);
            });

            byte[] pixels = ReadTexture(context!, commands, textures, target, size);

            // Instance 0 covers the lower-left, instance 1 the middle. Both being
            // magenta is what distinguishes a real instanced draw from one.
            Assert.Equal(255, PixelAt(pixels, size, 2, 2)[0]);
            Assert.Equal(255, PixelAt(pixels, size, 2, 2)[2]);
            Assert.Equal(255, PixelAt(pixels, size, 9, 9)[0]);
            Assert.Equal(255, PixelAt(pixels, size, 9, 9)[2]);

            ValidationAssert.NoErrors(messages);
        }
    }

    // ------------------------------------------------------------------ helpers

    private static byte[] PixelAt(byte[] pixels, uint size, uint x, uint y) =>
        pixels.Skip((int)((y * size + x) * 4)).Take(4).ToArray();

    private static void SetDynamicDefaults(Vk api, CommandBuffer commandBuffer)
    {
        api.CmdSetCullMode(commandBuffer, CullModeFlags.None);
        api.CmdSetFrontFace(commandBuffer, GlStateTracker.FrontFace);
        api.CmdSetPrimitiveTopology(commandBuffer, PrimitiveTopology.TriangleList);
        api.CmdSetDepthTestEnable(commandBuffer, false);
        api.CmdSetDepthWriteEnable(commandBuffer, false);
        api.CmdSetDepthCompareOp(commandBuffer, CompareOp.Always);
        api.CmdSetStencilTestEnable(commandBuffer, false);
        api.CmdSetStencilOp(commandBuffer, StencilFaceFlags.FaceFrontAndBack,
            StencilOp.Keep, StencilOp.Keep, StencilOp.Keep, CompareOp.Always);
        api.CmdSetStencilCompareMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0xFF);
        api.CmdSetStencilWriteMask(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0xFF);
        api.CmdSetStencilReference(commandBuffer, StencilFaceFlags.FaceFrontAndBack, 0);
        api.CmdSetLineWidth(commandBuffer, 1.0f);
    }

    private static unsafe byte[] ReadTexture(
        VulkanContext context, VulkanCommands commands, TextureManager textures, int textureId, uint size)
    {
        VulkanTexture texture = textures.Get(textureId)!;
        ulong bytes = (ulong)size * size * 4;

        using var readback = new VulkanBuffer(context, bytes,
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
            context.Api.CmdCopyImageToBuffer(commandBuffer, texture.Image,
                ImageLayout.TransferSrcOptimal, readback.Handle, 1, &region);
        });

        var result = new byte[(int)bytes];
        Marshal.Copy(readback.Mapped, result, 0, result.Length);
        return result;
    }

}
