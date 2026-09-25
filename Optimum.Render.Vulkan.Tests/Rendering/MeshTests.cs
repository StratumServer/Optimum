// Source: Optimum.Render.Vulkan.Tests/MeshDrawRangeTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using Xunit;

public class MeshDrawRangeTests
{
    [Fact]
    public void PoolOffsetsArePointerSizedAndStayPairedWithTheirCounts()
    {
        // MeshDataPool allocates two ints per GL pointer and writes the low
        // word at group * 2. Spare capacity is not another group to draw.
        int[] starts = { 48, 0, 28536, 0, 21600, 0, 123456, 0 };
        int[] sizes = { 384, 5400, 144, 999 };
        var commands = new DrawIndexedIndirectCommand[3];

        MeshManager.WriteIndirectCommands(commands, starts, sizes);

        Assert.Equal(new uint[] { 12, 7134, 5400 }, Array.ConvertAll(commands, c => c.FirstIndex));
        Assert.Equal(new uint[] { 384, 5400, 144 }, Array.ConvertAll(commands, c => c.IndexCount));
        Assert.All(commands, command =>
        {
            Assert.Equal(1u, command.InstanceCount);
            Assert.Equal(0, command.VertexOffset);
            Assert.Equal(0u, command.FirstInstance);
        });
    }

    [Fact]
    public void ZeroGroupsNeedNoOffsets()
    {
        MeshManager.WriteIndirectCommands(Span<DrawIndexedIndirectCommand>.Empty,
            ReadOnlySpan<int>.Empty, ReadOnlySpan<int>.Empty);
    }

    [Fact]
    public void LargeByteOffsetsRetainBothWordsBeforeConversionToAnIndex()
    {
        var commands = new DrawIndexedIndirectCommand[2];
        MeshManager.WriteIndirectCommands(commands,
            new[] { unchecked((int)0x80000000), 0, 24, 1 }, new[] { 6, 12 });
        Assert.Equal(0x20000000u, commands[0].FirstIndex);
        Assert.Equal(0x40000006u, commands[1].FirstIndex);
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/MeshManagerTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Covers mesh creation and drawing.
///
/// The layout derivation is the part worth pinning. The game allocates one buffer
/// per attribute and assigns attribute locations by walking the parts in a fixed
/// order, skipping absent ones - so a mesh with positions and colours but no
/// normals or UVs puts colours at location 1. The chunk shaders' explicit
/// locations depend on exactly that, and getting it wrong renders garbage rather
/// than failing.
/// </summary>
public class MeshManagerTests
{
    private readonly ITestOutputHelper _output;

    public MeshManagerTests(ITestOutputHelper output) => _output = output;

    private static bool TryCreateContext(
        ITestOutputHelper output, List<string> messages, out VulkanContext? context) =>
        GpuTest.TryCreateContext(output, messages, out context);

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void PooledTopsoilUsesUnsignedNormalizedShortUvs(bool ssbo)
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var meshes = new MeshManager(context!);
            var uv2 = new CustomMeshDataPartShort(8)
            {
                InterleaveSizes = new[] { 2 },
                InterleaveOffsets = new[] { 0 },
                InterleaveStride = 4,
                Conversion = DataConversion.NormalizedFloat,
            };
            int mesh = meshes.CreateEmpty(48, 0, 32, 16, 16, 24,
                null, uv2, null, null, EnumDrawMode.Triangles, staticDraw: false, ssbo: ssbo);

            VertexLayoutDescription layout = meshes.Get(mesh)!.Layout;
            Assert.Equal(Format.R16G16Unorm, layout.Attributes[^1].Format);
            Assert.Equal(4u, layout.Bindings[^1].Stride);
        }
    }

    [SkippableTheory]
    [InlineData(DataConversion.NormalizedFloat, false, Format.R16G16Unorm)]
    [InlineData(DataConversion.Float, false, Format.R16G16Uscaled)]
    [InlineData(DataConversion.Integer, false, Format.R16G16Sint)]
    [InlineData(DataConversion.NormalizedFloat, true, Format.R16G16SNorm)]
    [InlineData(DataConversion.Float, true, Format.R16G16Sscaled)]
    [InlineData(DataConversion.Integer, true, Format.R16G16Sint)]
    public void ShortSignednessMatchesTheTwoGlAllocationPaths(DataConversion conversion, bool uploaded, Format expected)
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");
        using (context)
        {
            using var meshes = new MeshManager(context!);
            var shorts = new CustomMeshDataPartShort(8)
            {
                InterleaveSizes = new[] { 2 },
                InterleaveOffsets = new[] { 0 },
                InterleaveStride = 4,
                Conversion = conversion,
                Instanced = true,
            };
            int mesh = meshes.CreateEmpty(48, 0, 0, 0, 0, 24,
                null, shorts, null, null, EnumDrawMode.Triangles, staticDraw: false,
                ssbo: false, signedCustomShorts: uploaded);
            VertexLayoutDescription layout = meshes.Get(mesh)!.Layout;
            Assert.Equal(expected, layout.Attributes[^1].Format);
            Assert.True(layout.Bindings[^1].PerInstance);
        }
    }

    [SkippableFact]
    public void AbsentPartsDoNotConsumeAttributeLocations()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            var state = new PipelineKeyState();
            using var meshes = new MeshManager(context!);

            // Positions and colours only: no normals, no UVs, no flags.
            int mesh = meshes.CreateEmpty(
                xyzSize: 4 * 3 * sizeof(float), normalsSize: 0, uvSize: 0,
                rgbaSize: 4 * 4, flagsSize: 0, indicesSize: 6 * sizeof(int),
                null, null, null, null, EnumDrawMode.Triangles, staticDraw: true, ssbo: false);

            VertexLayoutDescription layout = meshes.LayoutOf(meshes.LayoutIdOf(mesh));

            Assert.Equal(2, layout.Bindings.Length);
            Assert.Equal(2, layout.Attributes.Length);

            // Colours take location 1 because normals and UVs were absent.
            Assert.Equal(0u, layout.Attributes[0].Location);
            Assert.Equal(Format.R32G32B32Sfloat, layout.Attributes[0].Format);
            Assert.Equal(1u, layout.Attributes[1].Location);
            Assert.Equal(Format.R8G8B8A8Unorm, layout.Attributes[1].Format);
        }
    }

    /// <summary>
    /// The full chunk-style layout: positions, UVs, colours, flags and a custom
    /// integer part, which is what chunkopaque.vsh declares at locations 0 to 4.
    /// </summary>
    [SkippableFact]
    public void TheChunkVertexLayoutMatchesTheShadersDeclaredLocations()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            var state = new PipelineKeyState();
            using var meshes = new MeshManager(context!);

            // Count stays 0 until values are added, and AllocationSize reports
            // Count - so this is exactly the "declared but not yet filled" case
            // that must still claim location 4.
            var customInts = new CustomMeshDataPartInt(4)
            {
                InterleaveSizes = new[] { 1 },
                InterleaveOffsets = new[] { 0 },
                InterleaveStride = 4,
                Conversion = DataConversion.Integer,
            };
            Assert.Equal(0, customInts.AllocationSize);

            int mesh = meshes.CreateEmpty(
                xyzSize: 4 * 3 * sizeof(float), normalsSize: 0, uvSize: 4 * 2 * sizeof(float),
                rgbaSize: 4 * 4, flagsSize: 4 * sizeof(int), indicesSize: 6 * sizeof(int),
                null, null, null, customInts, EnumDrawMode.Triangles, staticDraw: true, ssbo: false);

            VertexLayoutDescription layout = meshes.LayoutOf(meshes.LayoutIdOf(mesh));
            var byLocation = layout.Attributes.ToDictionary(a => a.Location, a => a.Format);

            Assert.Equal(Format.R32G32B32Sfloat, byLocation[0]);   // xyz
            Assert.Equal(Format.R32G32Sfloat, byLocation[1]);      // uv
            Assert.Equal(Format.R8G8B8A8Unorm, byLocation[2]);     // rgbaLight
            // Signed, because every chunk shader declares these as `in int`.
            // GL let an unsigned attribute pointer feed a signed input by
            // reinterpreting the bits; Vulkan requires the attribute format's
            // numeric type to match the shader's exactly, and a mismatch is
            // undefined behaviour rather than a reinterpretation.
            Assert.Equal(Format.R32Sint, byLocation[3]);           // renderFlags
            Assert.Equal(Format.R32Sint, byLocation[4]);           // colormapData
        }
    }

    /// <summary>
    /// With SSBO vertex fetch the chunk shaders read positions from a storage
    /// buffer keyed on gl_VertexIndex, so the position buffer must leave the
    /// vertex input entirely rather than being bound twice.
    ///
    /// The same goes for normals, UVs and flags: the face record carries all
    /// three, and GL's SSBO allocator creates neither a buffer nor an attribute
    /// pointer for them. Binding one anyway pushes rgba off location 0, and the
    /// chunk shaders declare rgbaLightIn there - so the block light would be
    /// read from the UV stream, tinting terrain by its atlas coordinates.
    /// </summary>
    [SkippableFact]
    public void TheSsboPathBindsOnlyTheColoursTheChunkShadersDeclare()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            var state = new PipelineKeyState();
            using var meshes = new MeshManager(context!);

            // The pool passes its configured sizes whichever path it is on, so
            // normals, UVs and flags all arrive non-zero here.
            int mesh = meshes.CreateEmpty(
                xyzSize: 4 * 3 * sizeof(float), normalsSize: 4 * sizeof(int),
                uvSize: 4 * 2 * sizeof(float), rgbaSize: 4 * 4, flagsSize: 4 * sizeof(int),
                indicesSize: 6 * sizeof(int),
                null, null, null, null, EnumDrawMode.Triangles, staticDraw: true, ssbo: true);

            VulkanMesh created = meshes.Get(mesh)!;
            VertexLayoutDescription layout = meshes.LayoutOf(created.LayoutId);

            // The position buffer still exists, and still carries the records.
            Assert.NotNull(created.Buffers[MeshManager.BufferXyz]);
            Assert.DoesNotContain(MeshManager.BufferXyz, created.BindingOrder);

            // The other three do not exist at all, as in GL.
            Assert.Null(created.Buffers[MeshManager.BufferNormals]);
            Assert.Null(created.Buffers[MeshManager.BufferUv]);
            Assert.Null(created.Buffers[MeshManager.BufferFlags]);

            // Leaving colours alone at location 0.
            Assert.Single(layout.Attributes);
            Assert.Equal(0u, layout.Attributes[0].Location);
            Assert.Equal(Format.R8G8B8A8Unorm, layout.Attributes[0].Format);
        }
    }

    /// <summary>
    /// The chunk meshes carry two custom ints per vertex: the colormap data and
    /// one more. On the SSBO path the record already holds the colormap data, so
    /// GL drops that member, halves the stride and halves the buffer - and the
    /// remaining member reads from the offset its predecessor used.
    /// </summary>
    [SkippableFact]
    public void TheSsboPathDropsTheCustomIntTheFaceRecordAlreadyCarries()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            var state = new PipelineKeyState();
            using var meshes = new MeshManager(context!);

            CustomMeshDataPartInt TwoPerVertex() => new(8)
            {
                InterleaveSizes = new[] { 1, 1 },
                InterleaveOffsets = new[] { 0, 4 },
                InterleaveStride = 8,
                Conversion = DataConversion.Integer,
            };

            int ssboMesh = meshes.CreateEmpty(
                xyzSize: 4 * 3 * sizeof(float), normalsSize: 0, uvSize: 0,
                rgbaSize: 4 * 4, flagsSize: 0, indicesSize: 6 * sizeof(int),
                null, null, null, TwoPerVertex(), EnumDrawMode.Triangles, staticDraw: true, ssbo: true);

            VertexLayoutDescription ssbo = meshes.LayoutOf(meshes.LayoutIdOf(ssboMesh));

            // Colours at 0, then the one surviving int at 1 - reading offset 0
            // on a four-byte stride, which is where the dropped member sat.
            Assert.Equal(2, ssbo.Attributes.Length);
            Assert.Equal(Format.R32Sint, ssbo.Attributes[1].Format);
            Assert.Equal(0u, ssbo.Attributes[1].Offset);
            Assert.Equal(4u, ssbo.Bindings[1].Stride);

            // Off the SSBO path the same part keeps both members and its stride.
            int plainMesh = meshes.CreateEmpty(
                xyzSize: 4 * 3 * sizeof(float), normalsSize: 0, uvSize: 0,
                rgbaSize: 4 * 4, flagsSize: 0, indicesSize: 6 * sizeof(int),
                null, null, null, TwoPerVertex(), EnumDrawMode.Triangles, staticDraw: true, ssbo: false);

            VertexLayoutDescription plain = meshes.LayoutOf(meshes.LayoutIdOf(plainMesh));
            Assert.Equal(4, plain.Attributes.Length);
            Assert.Equal(8u, plain.Bindings[^1].Stride);
        }
    }

    /// <summary>
    /// A part that only ever had the colormap int has nothing left once it is
    /// dropped, so GL binds it nowhere at all on the SSBO path.
    /// </summary>
    [SkippableFact]
    public void TheSsboPathDropsASingleCustomIntPartEntirely()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            var state = new PipelineKeyState();
            using var meshes = new MeshManager(context!);

            var customInts = new CustomMeshDataPartInt(4)
            {
                InterleaveSizes = new[] { 1 },
                InterleaveOffsets = new[] { 0 },
                InterleaveStride = 4,
                Conversion = DataConversion.Integer,
            };

            int mesh = meshes.CreateEmpty(
                xyzSize: 4 * 3 * sizeof(float), normalsSize: 0, uvSize: 0,
                rgbaSize: 4 * 4, flagsSize: 0, indicesSize: 6 * sizeof(int),
                null, null, null, customInts, EnumDrawMode.Triangles, staticDraw: true, ssbo: true);

            VulkanMesh created = meshes.Get(mesh)!;
            Assert.Null(created.Buffers[MeshManager.BufferCustomInt]);
            Assert.Single(meshes.LayoutOf(created.LayoutId).Attributes);
        }
    }

    [SkippableFact]
    public void MeshIdsBehaveLikeGlNamesIncludingReuse()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            var state = new PipelineKeyState();
            using var meshes = new MeshManager(context!);

            int first = meshes.CreateEmpty(48, 0, 0, 16, 0, 24, null, null, null, null,
                EnumDrawMode.Triangles, true, false);
            int second = meshes.CreateEmpty(48, 0, 0, 16, 0, 24, null, null, null, null,
                EnumDrawMode.Triangles, true, false);

            Assert.True(first > 0);
            Assert.NotEqual(first, second);
            Assert.Null(meshes.Get(0));

            meshes.Delete(first);
            Assert.Null(meshes.Get(first));
            Assert.Equal(first, meshes.CreateEmpty(48, 0, 0, 16, 0, 24, null, null, null, null,
                EnumDrawMode.Triangles, true, false));
        }
    }

    /// <summary>
    /// Identical layouts intern to one id so they share a pipeline; a different
    /// set of parts must not.
    /// </summary>
    [SkippableFact]
    public void IdenticalLayoutsShareAnIdAndDifferentOnesDoNot()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            var state = new PipelineKeyState();
            using var meshes = new MeshManager(context!);

            int a = meshes.CreateEmpty(48, 0, 0, 16, 0, 24, null, null, null, null,
                EnumDrawMode.Triangles, true, false);
            int b = meshes.CreateEmpty(96, 0, 0, 32, 0, 48, null, null, null, null,
                EnumDrawMode.Triangles, true, false);
            int c = meshes.CreateEmpty(48, 0, 32, 16, 0, 24, null, null, null, null,
                EnumDrawMode.Triangles, true, false);

            // Same parts, different sizes: one layout.
            Assert.Equal(meshes.LayoutIdOf(a), meshes.LayoutIdOf(b));
            // UVs added: a different layout.
            Assert.NotEqual(meshes.LayoutIdOf(a), meshes.LayoutIdOf(c));
        }
    }

    /// <summary>
    /// The end-to-end mesh check: build a quad, write it through the persistent
    /// mapping the way the tesselator does, draw it indexed, and read the pixels.
    /// </summary>
    [SkippableFact]
    public unsafe void AnIndexedMeshRendersWithItsVertexColours()
    {
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, messages, out VulkanContext? context), "No usable Vulkan device.");

        using (context)
        {
            const uint size = 16;
            using var commands = new SetupQueue(context!);
            using var textures = new TextureManager(context!, commands.Uploads);
            var state = new PipelineKeyState();
            using var targets = new RenderTargetManager(context!, textures);
            using var pipelines = new GraphicsPipelineCache(context!);
            using var meshes = new MeshManager(context!);
            using var compiler = new ShaderCompiler();

            int target = textures.Create(size, size, Format.R8G8B8A8Unorm);
            int framebuffer = targets.Create(size, size);
            targets.Attach(framebuffer, 0, target);

            // A full-target quad: positions plus colours, no normals or UVs, so
            // colours land at location 1.
            int mesh = meshes.CreateEmpty(
                xyzSize: 4 * 3 * sizeof(float), normalsSize: 0, uvSize: 0,
                rgbaSize: 4 * 4, flagsSize: 0, indicesSize: 6 * sizeof(int),
                null, null, null, null, EnumDrawMode.Triangles, staticDraw: false, ssbo: false);

            float[] positions =
            {
                -1f, -1f, 0f,
                 1f, -1f, 0f,
                 1f,  1f, 0f,
                -1f,  1f, 0f,
            };
            byte[] colors =
            {
                255, 0, 0, 255,
                255, 0, 0, 255,
                255, 0, 0, 255,
                255, 0, 0, 255,
            };
            int[] indices = { 0, 1, 2, 0, 2, 3 };

            fixed (float* p = positions) meshes.Write(mesh, MeshManager.BufferXyz, 0, (IntPtr)p, positions.Length * 4);
            fixed (byte* c = colors) meshes.Write(mesh, MeshManager.BufferRgba, 0, (IntPtr)c, colors.Length);
            fixed (int* i = indices) meshes.Write(mesh, -1, 0, (IntPtr)i, indices.Length * 4);

            TranslatedProgram translated = ShaderTranslator.Translate(new[]
            {
                new ShaderStageSource
                {
                    Stage = EnumShaderType.VertexShader,
                    Filename = "mesh.vsh",
                    Code = """
                        #version 330 core
                        layout(location = 0) in vec3 position;
                        layout(location = 1) in vec4 color;
                        out vec4 vertexColor;
                        void main(void)
                        {
                            gl_Position = vec4(position, 1.0);
                            vertexColor = color;
                        }
                        """,
                },
                new ShaderStageSource
                {
                    Stage = EnumShaderType.FragmentShader,
                    Filename = "mesh.fsh",
                    Code = """
                        #version 330 core
                        in vec4 vertexColor;
                        out vec4 outColor;
                        void main(void) { outColor = vertexColor; }
                        """,
                },
            }, compiler);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, 1, translated);
            state.SetProgram(1);

            VulkanFramebuffer bound = targets.Get(framebuffer)!;
            int formatsId = targets.FormatsIdOf(bound);
            RenderTargetFormats formats = targets.FormatsOf(formatsId);

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

                meshes.Draw(commandBuffer, mesh);
                targets.EndRendering(commandBuffer);
            });

            byte[] pixels = ReadTexture(context!, commands, textures, target, size);

            // The quad covers the target, so the middle is the vertex colour.
            int centre = (int)((size / 2 * size + size / 2) * 4);
            Assert.Equal(255, pixels[centre + 0]);
            Assert.Equal(0, pixels[centre + 1]);
            Assert.Equal(255, pixels[centre + 3]);

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    private static void SetDynamicDefaults(Vk api, CommandBuffer commandBuffer)
    {
        api.CmdSetCullMode(commandBuffer, CullModeFlags.None);
        api.CmdSetFrontFace(commandBuffer, PipelineKeyState.FrontFace);
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

    [SkippableFact]
    public void UpdatingSeparateMeshSlicesPreservesEveryDestinationOffset()
    {
        using var device = GpuTest.CreateDevice(_output);
        const int slices = 3, floatsPerSlice = 9;
        int mesh = device.CreateEmptyMesh(slices * floatsPerSlice * sizeof(float), 0, 0, 0, 0, 0,
            null, null, null, null, EnumDrawMode.Triangles, staticDraw: false, ssbo: false);
        for (int slice = slices - 1; slice >= 0; slice--)
        {
            float[] values = Enumerable.Range(0, floatsPerSlice)
                .Select(index => slice * 100f + index).ToArray();
            device.UpdateMesh(mesh, new MeshData(3, 0)
            {
                xyz = values,
                XyzOffset = slice * floatsPerSlice * sizeof(float),
                VerticesCount = 3,
            });
        }
        var actual = new float[slices * floatsPerSlice];
        Marshal.Copy(device.GetMappedPointer(mesh, EnumMeshBufferPart.Xyz), actual, 0, actual.Length);
        for (int slice = 0; slice < slices; slice++)
            for (int index = 0; index < floatsPerSlice; index++)
                Assert.Equal(slice * 100f + index, actual[slice * floatsPerSlice + index]);
        device.DeleteMesh(mesh);
        GpuTest.AssertClean(device);
    }

    private static unsafe byte[] ReadTexture(
        VulkanContext context, SetupQueue commands, TextureManager textures, int textureId, uint size)
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
}

// Source: Optimum.Render.Vulkan.Tests/VertexAttributeDefaultTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;

/// <summary>
/// GL answers a read of a vertex attribute the draw does not supply with the
/// current generic attribute, which defaults to (0, 0, 0, 1). Vulkan has no such
/// thing, and the difference is not academic: the GUI quad carries only positions
/// and UVs while gui.vsh declares six inputs, and gui.fsh discards a fragment
/// based on one of the missing ones. Undefined there meant the entire interface
/// rendered as nothing, with no validation message to say why.
/// </summary>
public class VertexAttributeDefaultTests
{
    private static VertexInputSlot Slot(string name, int location, string type)
    {
        Assert.True(GlslType.TryParse(type, out GlslType parsed), "unknown type " + type);
        return new VertexInputSlot(name, location, parsed);
    }

    /// <summary>The GUI quad's own shape: positions at 0, UVs at 1, nothing else.</summary>
    private static VertexLayoutDescription QuadLayout() => new(
        new[]
        {
            new VertexBinding(0, 12, PerInstance: false),
            new VertexBinding(1, 8, PerInstance: false),
        },
        new[]
        {
            new VertexAttribute(0, 0, Format.R32G32B32Sfloat, 0),
            new VertexAttribute(1, 1, Format.R32G32Sfloat, 0),
        });

    [Fact]
    public void MissingAttributesGetABindingOfTheirOwn()
    {
        var declared = new List<VertexInputSlot>
        {
            Slot("vertexPositionIn", 0, "vec3"),
            Slot("uvIn", 1, "vec2"),
            Slot("colorIn", 2, "vec4"),
            Slot("renderFlagsIn", 3, "int"),
            Slot("damageEffectIn", 4, "float"),
            Slot("jointId", 5, "int"),
        };

        VertexLayoutDescription merged = QuadLayout().WithDefaultsFor(declared);

        Assert.Equal(6, merged.Attributes.Length);
        Assert.Equal(3, merged.Bindings.Length);
        Assert.Equal(VertexLayoutDescription.DefaultAttributeBinding, merged.Bindings[^1].Binding);

        // Stride zero is what makes every vertex read the same constant.
        Assert.Equal(0u, merged.Bindings[^1].Stride);
        Assert.False(merged.Bindings[^1].PerInstance);
    }

    [Fact]
    public void SuppliedAttributesAreLeftOnTheirOwnBinding()
    {
        var declared = new List<VertexInputSlot>
        {
            Slot("vertexPositionIn", 0, "vec3"),
            Slot("uvIn", 1, "vec2"),
            Slot("colorIn", 2, "vec4"),
        };

        VertexLayoutDescription merged = QuadLayout().WithDefaultsFor(declared);

        VertexAttribute position = merged.Attributes.Single(a => a.Location == 0);
        VertexAttribute uv = merged.Attributes.Single(a => a.Location == 1);
        Assert.Equal(0u, position.Binding);
        Assert.Equal(1u, uv.Binding);

        VertexAttribute color = merged.Attributes.Single(a => a.Location == 2);
        Assert.Equal(VertexLayoutDescription.DefaultAttributeBinding, color.Binding);
    }

    /// <summary>
    /// Integer attributes have to read integer zeros, so they take the second
    /// half of the defaults buffer rather than reinterpreting float bits.
    /// </summary>
    [Theory]
    [InlineData("float", Format.R32Sfloat, 0u)]
    [InlineData("vec2", Format.R32G32Sfloat, 0u)]
    [InlineData("vec3", Format.R32G32B32Sfloat, 0u)]
    [InlineData("vec4", Format.R32G32B32A32Sfloat, 0u)]
    [InlineData("int", Format.R32Sint, 16u)]
    [InlineData("ivec4", Format.R32G32B32A32Sint, 16u)]
    [InlineData("uint", Format.R32Uint, 16u)]
    public void DefaultsUseTheFormatAndHalfMatchingTheDeclaredType(
        string type, Format expectedFormat, uint expectedOffset)
    {
        VertexLayoutDescription merged =
            VertexLayoutDescription.Empty.WithDefaultsFor(new[] { Slot("x", 7, type) });

        VertexAttribute attribute = Assert.Single(merged.Attributes);
        Assert.Equal(7u, attribute.Location);
        Assert.Equal(expectedFormat, attribute.Format);
        Assert.Equal(expectedOffset, attribute.Offset);
    }

    [Fact]
    public void UnsignedVectorTypesUseUnsignedFormats()
    {
        VertexLayoutDescription merged =
            VertexLayoutDescription.Empty.WithDefaultsFor(new[] { Slot("x", 7, "uvec3") });

        VertexAttribute attribute = Assert.Single(merged.Attributes);
        Assert.Equal(Format.R32G32B32Uint, attribute.Format);
    }

    /// <summary>
    /// A layout that already covers everything must come back untouched, so no
    /// pipeline gains a binding it will never have a buffer for.
    /// </summary>
    [Fact]
    public void NothingIsAddedWhenTheMeshCoversEveryDeclaredInput()
    {
        var declared = new List<VertexInputSlot>
        {
            Slot("vertexPositionIn", 0, "vec3"),
            Slot("uvIn", 1, "vec2"),
        };

        VertexLayoutDescription layout = QuadLayout();
        VertexLayoutDescription merged = layout.WithDefaultsFor(declared);

        Assert.Same(layout, merged);
    }

    /// <summary>
    /// A program declaring no inputs at all - the fullscreen passes - must not
    /// grow a binding either.
    /// </summary>
    [Fact]
    public void NothingIsAddedForAProgramWithNoDeclaredInputs()
    {
        VertexLayoutDescription layout = QuadLayout();
        Assert.Same(layout, layout.WithDefaultsFor(Array.Empty<VertexInputSlot>()));
    }

    /// <summary>
    /// The layout the parser produces for gui.vsh is the case this all exists
    /// for, so it is pinned against the real shader's declarations rather than a
    /// hand-written list.
    /// </summary>
    [Fact]
    public void TheGuiShaderDeclaresEveryInputItReads()
    {
        const string source = """
            #version 330 core
            layout(location = 0) in vec3 vertexPositionIn;
            layout(location = 1) in vec2 uvIn;
            layout(location = 2) in vec4 colorIn;
            layout(location = 3) in int renderFlagsIn;
            layout(location = 4) in float damageEffectIn;
            layout(location = 5) in int jointId;
            void main() { gl_Position = vec4(vertexPositionIn, 1.0); }
            """;

        ProgramInterfaceLayout layout = ProgramInterfaceLayout.Build(
            new[] { (EnumShaderType.VertexShader, GlslParser.Parse(source)) });

        Assert.Equal(6, layout.VertexInputs.Count);
        Assert.Contains(layout.VertexInputs, s => s.Name == "damageEffectIn" && s.Location == 4);
        Assert.Contains(layout.VertexInputs, s => s.Name == "renderFlagsIn" && s.Location == 3);
    }

    /// <summary>
    /// Sampler locations have to be tellable apart from uniform block offsets,
    /// which start at zero, and from GL's "not found" answer of -1 - otherwise
    /// assigning a sampler its texture unit would write into the uniform block
    /// at some arbitrary offset instead.
    /// </summary>
    [Theory]
    [InlineData(-2, true)]
    [InlineData(-3, true)]
    [InlineData(-100, true)]
    [InlineData(-1, false)]
    [InlineData(0, false)]
    [InlineData(36, false)]
    public void SamplerLocationsAreDisjointFromBlockOffsets(int location, bool isSampler)
    {
        Assert.Equal(isSampler, ShaderProgramResources.IsSamplerLocation(location));
    }

    /// <summary>
    /// Asking for Vulkan by name gets it wherever it runs; the automatic setting
    /// is a default nobody chose, so it only selects the backend on drivers the
    /// backend has been exercised against.
    /// </summary>
    [SkippableFact]
    public void AnExplicitRendererChoiceIgnoresTheAutomaticAllowList()
    {
        Skip.IfNot(VulkanDevice.IsSupported(out string? probeReason), probeReason ?? "No usable Vulkan device.");

        // Explicit: allowed on whatever this machine has.
        Assert.True(VulkanDevice.IsSupported(false, out _, out string driver));
        Assert.False(string.IsNullOrWhiteSpace(driver));

        // A supported software driver can be explicitly selected without being
        // on the automatic allow-list (SwiftShader is one such driver).
        Assert.Equal(VulkanDevice.IsAllowedForAutomaticSelection(driver),
            VulkanDevice.IsSupported(true, out _, out _));
    }

    /// <summary>
    /// The allow-list itself, which is the part with the logic. Only the drivers
    /// the backend is actually run against may be picked automatically; anything
    /// unrecognised stays on OpenGL rather than becoming the first person to try
    /// the backend on that driver.
    /// </summary>
    [Theory]
    [InlineData("NVIDIA", true)]
    [InlineData("Intel open-source Mesa driver", true)]
    [InlineData("Intel Corporation", true)]
    [InlineData("radv", true)]
    [InlineData("AMD proprietary driver", true)]
    [InlineData("Mesa llvmpipe", true)]
    [InlineData("SwiftShader", false)]
    [InlineData("MoltenVK", false)]
    [InlineData("unknown", false)]
    [InlineData("", false)]
    public void AutomaticSelectionOnlyTakesKnownDrivers(string driverName, bool allowed)
    {
        Assert.Equal(allowed, VulkanDevice.IsAllowedForAutomaticSelection(driverName));
    }
}
}
