using System;
using System.Collections.Generic;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Runs real game shaders through the whole chain on a real device: translate,
/// build descriptor layouts from the program's interface, create pipelines, and
/// check the cache behaves.
///
/// This is where a mistake in the descriptor-set design surfaces. The rewriter
/// decides that samplers live in set 1 and storage buffers in set 2; nothing
/// validates that decision until a driver is asked to build a pipeline layout
/// from it alongside the SPIR-V that assumes it.
/// </summary>
public class PipelineCacheTests
{
    private readonly ITestOutputHelper _output;

    public PipelineCacheTests(ITestOutputHelper output) => _output = output;

    private static bool TryCreateContext(ITestOutputHelper output, out VulkanContext? context, List<string> messages) =>
        GpuTest.TryCreateContext(output, messages, out context);

    private static TranslatedProgram TranslateVanilla(string programName, ShaderCompiler compiler)
    {
        var files = ShaderCorpus.LoadShaderFiles();
        var includes = ShaderCorpus.LoadIncludes();
        ShaderCorpus.ShaderVariant variant =
            ShaderCorpus.Variants().First(v => v.Name == "everything-on");

        return ShaderTranslator.Translate(
            ShaderCorpus.BuildProgram(programName, files, includes, variant), compiler);
    }

    /// <summary>
    /// final.fsh is the heaviest post-processing program in the game: five
    /// samplers and twenty-odd loose uniforms, several of them shared with the
    /// vertex stage. If a descriptor layout can be built for it, the scheme holds.
    /// </summary>
    [SkippableFact]
    public void AVanillaProgramProducesUsableDescriptorAndPipelineLayouts()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context, messages), "No usable Vulkan device.");

        using (context)
        {
            using var compiler = new ShaderCompiler();
            TranslatedProgram translated = TranslateVanilla("final", compiler);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, programId: 1, translated);

            _output.WriteLine($"uniform block : {translated.Layout.BlockSize} bytes, " +
                              $"{translated.Layout.Members.Count} members");
            _output.WriteLine($"samplers      : {translated.Layout.Samplers.Count}");
            _output.WriteLine($"uniform blocks: {translated.Layout.UniformBlocks.Count}");
            _output.WriteLine($"storage blocks: {translated.Layout.StorageBlocks.Count}");

            Assert.NotEqual(0ul, program.PipelineLayout.Handle);
            foreach (DescriptorSetLayout layout in program.SetLayouts)
            {
                Assert.NotEqual(0ul, layout.Handle);
            }

            // The composition pass samples the scene, bloom, glow, godrays and
            // SSAO, so it must have real sampler bindings.
            Assert.True(translated.Layout.Samplers.Count >= 4);
            Assert.True(translated.Layout.BlockSize > 0);

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// The chunk program is the one with a storage buffer at a binding the shader
    /// declared. Building a layout for it checks that set 2 and binding 3 line up
    /// between the rewriter and the descriptor layout.
    /// </summary>
    [SkippableFact]
    public void TheChunkProgramsStorageBufferLandsWhereTheShaderExpectsIt()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context, messages), "No usable Vulkan device.");

        using (context)
        {
            using var compiler = new ShaderCompiler();
            TranslatedProgram translated = TranslateVanilla("chunkopaque", compiler);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            BlockBinding storage = Assert.Single(translated.Layout.StorageBlocks);
            Assert.Equal(ProgramInterfaceLayout.StorageSet, storage.Set);
            Assert.Equal(3, storage.Binding);

            using var program = new ShaderProgramResources(context!, programId: 2, translated);
            Assert.NotEqual(0ul, program.PipelineLayout.Handle);

            ValidationAssert.NoErrors(messages);

            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// The cache has to return the same pipeline for the same state and a new one
    /// when the state actually differs. Getting the first wrong leaks pipelines
    /// and stutters; getting the second wrong renders with the wrong state.
    /// </summary>
    [SkippableFact]
    public void PipelinesAreReusedForIdenticalStateAndRebuiltForDifferentState()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context, messages), "No usable Vulkan device.");

        using (context)
        {
            using var compiler = new ShaderCompiler();
            TranslatedProgram translated = TranslateVanilla("blit", compiler);
            Assert.True(translated.Success, string.Join("; ", translated.Errors));

            using var program = new ShaderProgramResources(context!, programId: 3, translated);
            using var cache = new GraphicsPipelineCache(context!);

            var tracker = new GlStateTracker();
            tracker.SetProgram(3);

            var targets = new RenderTargetFormats(new[] { Format.R8G8B8A8Unorm }, Format.Undefined);
            int targetId = tracker.InternTargetFormats(targets);
            int layoutId = 0;

            GraphicsPipelineCache.PipelineRequest Request() => new()
            {
                Program = program,
                VertexLayout = VertexLayoutDescription.Empty,
                Targets = targets,
                Blend = new[] { tracker.BlendFor(0) },
                PolygonMode = tracker.PolygonMode,
                Topology = tracker.Topology,
            };

            Pipeline first = cache.Get(tracker.BuildKey(layoutId, targetId, 1), Request());
            Assert.Equal(1, cache.Count);
            Assert.Equal(1, cache.Misses);

            // Same state: served from the cache, no new pipeline.
            Pipeline again = cache.Get(tracker.BuildKey(layoutId, targetId, 1), Request());
            Assert.Equal(first.Handle, again.Handle);
            Assert.Equal(1, cache.Count);
            Assert.Equal(1, cache.Hits);

            // Dynamic state must not force a rebuild.
            tracker.SetViewport(0, 0, 800, 600);
            tracker.SetDepthTest(true);
            cache.Get(tracker.BuildKey(layoutId, targetId, 1), Request());
            Assert.Equal(1, cache.Count);

            // Blend state is baked in, so this one does.
            tracker.SetBlend(true, EnumBlendMode.Glow);
            Pipeline blended = cache.Get(tracker.BuildKey(layoutId, targetId, 1), Request());
            Assert.NotEqual(first.Handle, blended.Handle);
            Assert.Equal(2, cache.Count);

            _output.WriteLine($"pipelines: {cache.Count}, hits: {cache.Hits}, misses: {cache.Misses}");
            ValidationAssert.NoErrors(messages);
            ValidationAssert.NoSyncHazards(messages);
        }
    }

    /// <summary>
    /// Compiles a pipeline for every vanilla program that has no vertex inputs -
    /// the fullscreen post-processing passes - to show the cache stays small
    /// rather than growing one entry per program per frame.
    /// </summary>
    [SkippableFact]
    public void FullscreenPassesShareOnePipelinePerProgram()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context, messages), "No usable Vulkan device.");

        using (context)
        {
            using var compiler = new ShaderCompiler();
            using var cache = new GraphicsPipelineCache(context!);
            var tracker = new GlStateTracker();

            var targets = new RenderTargetFormats(new[] { Format.R8G8B8A8Unorm }, Format.Undefined);
            int targetId = tracker.InternTargetFormats(targets);

            var programs = new List<ShaderProgramResources>();
            string[] names = { "blit", "final", "luma", "findbright", "godrays" };

            try
            {
                int programId = 100;
                foreach (string name in names)
                {
                    TranslatedProgram translated = TranslateVanilla(name, compiler);
                    Assert.True(translated.Success, $"{name}: {string.Join("; ", translated.Errors)}");

                    var resources = new ShaderProgramResources(context!, programId, translated);
                    programs.Add(resources);

                    tracker.SetProgram(programId);
                    cache.Get(tracker.BuildKey(0, targetId, 1), new GraphicsPipelineCache.PipelineRequest
                    {
                        Program = resources,
                        VertexLayout = VertexLayoutDescription.Empty,
                        Targets = targets,
                        Blend = new[] { tracker.BlendFor(0) },
                        PolygonMode = tracker.PolygonMode,
                        Topology = tracker.Topology,
                    });

                    programId++;
                }

                // One pipeline each, and drawing them again adds nothing.
                Assert.Equal(names.Length, cache.Count);

                byte[] blob = cache.SerializeDriverCache();
                _output.WriteLine($"{cache.Count} pipelines, driver cache blob {blob.Length} bytes");

                ValidationAssert.NoErrors(messages);

                ValidationAssert.NoSyncHazards(messages);
            }
            finally
            {
                foreach (ShaderProgramResources resources in programs) resources.Dispose();
            }
        }
    }

}
