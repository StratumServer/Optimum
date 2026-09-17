using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
/// decides where samplers, the record and storage buffers live in the shared
/// pipeline layout; nothing validates that decision until a driver is asked to
/// build a pipeline from the SPIR-V that assumes it against that layout.
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
            SharedPipelineLayout shared = program.StandaloneLayout!;
            foreach (DescriptorSetLayout layout in new[] { shared.FrameSetLayout, shared.TextureSetLayout, shared.StorageSetLayout })
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
    /// The chunk program is the one with a storage buffer, declared at binding 3 in
    /// the shader - the record's binding under the shared layout. The rewriter moves it
    /// to FaceData's binding in set 2, where the draw path binds the mesh buffer.
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
            Assert.Equal(SetConvention.StorageSet, storage.Set);
            Assert.Equal(SetConvention.FaceDataBinding, storage.Binding);

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

            var tracker = new PipelineKeyState();
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
            var tracker = new PipelineKeyState();

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

    /// <summary>
    /// The launch-to-launch round trip on a real driver: the blob the driver hands out
    /// carries its own header for this device, survives the file wrapper, and seeds a
    /// new cache that then builds the same pipeline without complaint.
    /// </summary>
    [SkippableFact]
    public void ASavedPipelineCacheSeedsTheNextCacheOnTheSameDevice()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context, messages), "No usable Vulkan device.");

        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optimum-pipeline-cache-" + Guid.NewGuid().ToString("N"));
        using (context)
        {
            try
            {
                using var compiler = new ShaderCompiler();
                TranslatedProgram translated = TranslateVanilla("blit", compiler);
                Assert.True(translated.Success, string.Join("; ", translated.Errors));
                using var program = new ShaderProgramResources(context!, programId: 7, translated);

                var tracker = new PipelineKeyState();
                tracker.SetProgram(7);
                var targets = new RenderTargetFormats(new[] { Format.R8G8B8A8Unorm }, Format.Undefined);
                int targetId = tracker.InternTargetFormats(targets);
                GraphicsPipelineCache.PipelineRequest Request() => new()
                {
                    Program = program,
                    VertexLayout = VertexLayoutDescription.Empty,
                    Targets = targets,
                    Blend = new[] { tracker.BlendFor(0) },
                    PolygonMode = tracker.PolygonMode,
                    Topology = tracker.Topology,
                };

                PipelineCacheIdentity identity = PipelineCacheIdentity.Of(context!.Capabilities);
                string path = PipelineCacheFile.PathFor(root, identity);
                byte[] blob;
                using (var first = new GraphicsPipelineCache(context!))
                {
                    Assert.False(first.SeedAccepted);
                    first.Get(tracker.BuildKey(0, targetId, 1), Request());
                    blob = first.SerializeDriverCache();
                }

                Assert.True(PipelineCacheFile.HasMatchingVulkanHeader(blob, identity),
                    "the driver's blob does not name the device its properties report");
                Assert.True(PipelineCacheFile.Save(path, blob, identity));
                byte[]? loaded = PipelineCacheFile.Load(path, identity);
                Assert.Equal(blob, loaded);

                using (var second = new GraphicsPipelineCache(context!, loaded))
                {
                    Assert.True(second.SeedAccepted);
                    Assert.NotEqual(0ul, second.Get(tracker.BuildKey(0, targetId, 1), Request()).Handle);
                }

                _output.WriteLine($"pipeline cache blob {blob.Length} bytes at {path}");
                ValidationAssert.NoErrors(messages);
                ValidationAssert.NoSyncHazards(messages);
            }
            finally
            {
                try
                {
                    System.IO.Directory.Delete(root, recursive: true);
                }
                catch (System.IO.DirectoryNotFoundException)
                {
                }
            }
        }
    }

    // ------------------------------------------------------ growth-triggered save

    [Fact]
    public void TheGrowthTriggerSamplesAtMostOncePerIntervalAndFiresOnThreshold()
    {
        const long mib = 1024 * 1024;
        long second = Stopwatch.Frequency;
        var trigger = new PipelineCacheGrowthTrigger(8 * mib, TimeSpan.FromSeconds(10), baselineBytes: 2 * mib);

        // The first call arms the clock; then one sample per interval, never two.
        Assert.False(trigger.SampleDue(100 * second));
        Assert.False(trigger.SampleDue(105 * second));
        Assert.True(trigger.SampleDue(110 * second));
        Assert.False(trigger.SampleDue(119 * second));
        Assert.True(trigger.SampleDue(121 * second));

        // Growth is measured from what is on disk: the seed first, then the last save.
        Assert.False(trigger.GrewEnough(9 * mib));
        Assert.True(trigger.GrewEnough(10 * mib));
        trigger.NoteSaved(10 * mib);
        Assert.Equal(10 * mib, trigger.BaselineBytes);
        Assert.False(trigger.GrewEnough(17 * mib));
        Assert.True(trigger.GrewEnough(18 * mib));
    }

    [Fact]
    public void SynchronousPipelinesComeFromTheSettingOrTheEnvironment()
    {
        Assert.False(VulkanDevice.ResolveSynchronousPipelines(null, null));
        Assert.False(VulkanDevice.ResolveSynchronousPipelines(null, "0"));
        Assert.True(VulkanDevice.ResolveSynchronousPipelines(null, "1"));
        Assert.True(VulkanDevice.ResolveSynchronousPipelines(null, " true "));
        Assert.True(VulkanDevice.ResolveSynchronousPipelines(true, "0"));
        Assert.False(VulkanDevice.ResolveSynchronousPipelines(false, "1"));
    }

    /// <summary>
    /// A frame written to disk must hold every draw, and OPTIMUM_PARITY_DUMP and
    /// OPTIMUM_HEADLESS_FRAMES are documented as standalone switches: set without the capture
    /// scripts (which also export OPTIMUM_VULKAN_SYNC_PIPELINES), they still force blocking
    /// creation. Explicit settings keep the last word.
    /// </summary>
    [Fact]
    public void AFrameCaptureForcesBlockingPipelinesWithoutTheScripts()
    {
        Assert.True(VulkanDevice.ResolveSynchronousPipelines(null, null, parityDump: "/tmp/dump", headlessFrames: null));
        Assert.True(VulkanDevice.ResolveSynchronousPipelines(null, null, parityDump: null, headlessFrames: "/tmp/frames"));
        Assert.True(VulkanDevice.ResolveSynchronousPipelines(null, "0", parityDump: "/tmp/dump", headlessFrames: null));
        // The capture code ignores a relative or blank directory, so the pipelines do too.
        Assert.False(VulkanDevice.ResolveSynchronousPipelines(null, null, parityDump: "relative", headlessFrames: " "));
        Assert.False(VulkanDevice.ResolveSynchronousPipelines(false, null, parityDump: "/tmp/dump", headlessFrames: "/tmp/frames"));
    }

    /// <summary>
    /// The opportunistic save on a real driver: once the cache has grown past the threshold,
    /// a due sample writes the file from a worker, and the file seeds a new cache.
    /// </summary>
    [SkippableFact]
    public void AGrownPipelineCacheIsSavedFromAWorkerBeforeShutdown()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        var messages = new List<string>();
        Skip.IfNot(TryCreateContext(_output, out VulkanContext? context, messages), "No usable Vulkan device.");

        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optimum-pipeline-growth-" + Guid.NewGuid().ToString("N"));
        using (context)
        {
            try
            {
                using var compiler = new ShaderCompiler();
                TranslatedProgram translated = TranslateVanilla("blit", compiler);
                Assert.True(translated.Success, string.Join("; ", translated.Errors));
                using var program = new ShaderProgramResources(context!, programId: 9, translated);

                var tracker = new PipelineKeyState();
                tracker.SetProgram(9);
                var targets = new RenderTargetFormats(new[] { Format.R8G8B8A8Unorm }, Format.Undefined);
                int targetId = tracker.InternTargetFormats(targets);

                PipelineCacheIdentity identity = PipelineCacheIdentity.Of(context!.Capabilities);
                PipelineCachePersistence persistence = PipelineCachePersistence.Open(root, identity, out byte[]? seed,
                    thresholdBytes: 1, interval: TimeSpan.FromSeconds(1));
                Assert.Null(seed);

                using var cache = new GraphicsPipelineCache(context!) { KeyLog = persistence.KeyLog };
                cache.Get(tracker.BuildKey(0, targetId, 1), new GraphicsPipelineCache.PipelineRequest
                {
                    Program = program,
                    VertexLayout = VertexLayoutDescription.Empty,
                    Targets = targets,
                    Blend = new[] { tracker.BlendFor(0) },
                    PolygonMode = tracker.PolygonMode,
                    Topology = tracker.Topology,
                });
                Assert.True(persistence.KeyLog.HasUnsavedChanges);

                long second = Stopwatch.Frequency;
                Assert.False(persistence.Tick(cache, 10 * second), "the first tick only arms the clock");
                Assert.False(persistence.Tick(cache, 10 * second + second / 2), "sampled inside the interval");
                Assert.True(persistence.Tick(cache, 11 * second));
                persistence.WaitForPendingSave();

                Assert.Equal(1, persistence.Saves);
                Assert.False(persistence.KeyLog.HasUnsavedChanges);
                byte[]? saved = PipelineCacheFile.Load(persistence.CachePath, identity);
                Assert.NotNull(saved);
                Assert.Equal(1, PipelineKeyLog.Load(persistence.KeyLogPath).Count);
                using (var seeded = new GraphicsPipelineCache(context!, saved))
                {
                    Assert.True(seeded.SeedAccepted);
                }

                // The shutdown save still writes both files.
                persistence.SaveAtShutdown(cache);
                Assert.Equal(2, persistence.Saves);

                ValidationAssert.NoErrors(messages);
                ValidationAssert.NoSyncHazards(messages);
            }
            finally
            {
                try
                {
                    System.IO.Directory.Delete(root, recursive: true);
                }
                catch (System.IO.DirectoryNotFoundException)
                {
                }
            }
        }
    }

    // ---------------------------------------------------------- background compiles

    private const string FullscreenVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    /// <summary>
    /// A colour no earlier run used: its constants are in the SPIR-V, so neither our cache nor
    /// the driver's implicit one can hold the pipeline and the first creation must compile.
    /// </summary>
    private static byte[] UniqueColour()
    {
        var bytes = new byte[3];
        Random.Shared.NextBytes(bytes);
        for (int i = 0; i < 3; i++) bytes[i] = (byte)(1 + bytes[i] % 254);
        return new byte[] { bytes[0], bytes[1], bytes[2], 255 };
    }

    private static string SolidFragment(byte[] colour) => string.Format(CultureInfo.InvariantCulture, """
        #version 330 core
        out vec4 outColor;
        void main(void)
        {{
            outColor = vec4({0:F1} / 255.0, {1:F1} / 255.0, {2:F1} / 255.0, 1.0);
        }}
        """, colour[0], colour[1], colour[2]);

    /// <summary>A device from GpuTest with the given pipeline mode and cache root, or null (the reason is logged).</summary>
    private VulkanDevice? OpenDevice(bool synchronousPipelines, string? cacheRoot = null)
    {
        VulkanDevice device = GpuTest.NewDevice();
        device.SynchronousPipelines = synchronousPipelines;
        device.ShaderCacheDirectory = cacheRoot;
        if (device.Initialize(IntPtr.Zero, 0, 0, out string reason)) return device;
        _output.WriteLine("Vulkan unavailable: " + reason);
        device.Dispose();
        return null;
    }

    private static int ColourTarget(VulkanDevice device, int size)
    {
        int texture = device.CreateTexture2D(size, size,
            EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = device.CreateFramebuffer(size, size);
        device.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
        device.SetDrawBuffers(framebuffer, 0b1);
        return framebuffer;
    }

    private static void BeginDraw(VulkanDevice device, int framebuffer, int size, int program)
    {
        device.BeginFrame();
        device.BindFramebuffer(framebuffer);
        device.ClearColor(0, 0f, 0f, 0f, 1f);
        device.SetViewport(0, 0, size, size);
        device.SetDepthTest(false);
        device.SetCullFace(false);
        device.SetBlend(false, EnumBlendMode.Standard);
        device.UseProgram(program);
    }

    /// <summary>The centre pixel of the frame just presented.</summary>
    private static unsafe byte[] ReadCentre(VulkanDevice device, int framebuffer, int size)
    {
        var pixels = new byte[size * size * 4];
        device.BindFramebuffer(framebuffer);
        fixed (byte* destination = pixels)
        {
            device.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
        }
        int centre = (size / 2 * size + size / 2) * 4;
        return new[] { pixels[centre], pixels[centre + 1], pixels[centre + 2], pixels[centre + 3] };
    }

    /// <summary>
    /// The background path end to end: a pipeline the driver has never seen is not ready on the
    /// frame that asks for it (the draw is skipped, the target keeps its clear), the worker
    /// compiles it, the next frame start publishes it, and that frame's draw renders with it.
    /// </summary>
    [SkippableFact]
    public void AnAsyncPipelineIsNotReadyUntilTheWorkerFinishesAndThenRenders()
    {
        using VulkanDevice? device = OpenDevice(synchronousPipelines: false);
        Skip.If(device == null, "No usable Vulkan device.");
        GraphicsPipelineCache pipelines = device!.PipelinesForTests;
        Skip.IfNot(pipelines.AsyncCompiles, "The device lacks pipelineCreationCacheControl.");
        const int size = 8;

        byte[] colour = UniqueColour();
        int program = VulkanDeviceIntegrationTests.LinkProgram(device, FullscreenVertex, SolidFragment(colour));
        int framebuffer = ColourTarget(device, size);

        BeginDraw(device, framebuffer, size, program);
        device.DrawFullscreenTriangle();
        Assert.Equal(1, pipelines.DrawsSkipped);
        Assert.Equal(1, pipelines.QueuedCompiles);
        Assert.Equal(0, pipelines.CompiledSync);
        Assert.Equal(0, pipelines.Count);
        device.Present();
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, ReadCentre(device, framebuffer, size));

        Assert.True(pipelines.WaitForBackgroundCompiles(TimeSpan.FromSeconds(60)), "the worker did not finish");
        Assert.Equal(1, pipelines.CompiledAsync);
        // Finished but not yet visible: publishing waits for the frame start.
        Assert.Equal(0, pipelines.Count);

        BeginDraw(device, framebuffer, size, program);
        Assert.Equal(1, pipelines.Count);
        device.DrawFullscreenTriangle();
        device.Present();
        byte[] pixel = ReadCentre(device, framebuffer, size);
        _output.WriteLine($"expected {string.Join(",", colour)}, centre {string.Join(",", pixel)}");

        Assert.Equal(colour, pixel);
        Assert.Equal(1, pipelines.DrawsSkipped);
        Assert.Equal(0, pipelines.CompiledSync);
        Assert.Equal(1, pipelines.Hits);
        GpuTest.AssertClean(device);
    }

    /// <summary>
    /// A key asked for again while its compile is queued or running is not queued again, and
    /// a second key for the same pipeline waits on the same compile.
    /// </summary>
    [SkippableFact]
    public void TheSameKeyRequestedWhileCompilingCompilesOnce()
    {
        using VulkanDevice? device = OpenDevice(synchronousPipelines: false);
        Skip.If(device == null, "No usable Vulkan device.");
        GraphicsPipelineCache pipelines = device!.PipelinesForTests;
        Skip.IfNot(pipelines.AsyncCompiles, "The device lacks pipelineCreationCacheControl.");
        const int size = 8;

        byte[] colour = UniqueColour();
        int program = VulkanDeviceIntegrationTests.LinkProgram(device, FullscreenVertex, SolidFragment(colour));
        int framebuffer = ColourTarget(device, size);

        BeginDraw(device, framebuffer, size, program);
        device.DrawFullscreenTriangle();
        device.DrawFullscreenTriangle();
        Assert.Equal(2, pipelines.DrawsSkipped);
        device.Present();

        // Asked for again on the next frame, whether or not the worker is done by then.
        BeginDraw(device, framebuffer, size, program);
        device.DrawFullscreenTriangle();
        device.Present();

        Assert.True(pipelines.WaitForBackgroundCompiles(TimeSpan.FromSeconds(60)), "the worker did not finish");
        Assert.Equal(1, pipelines.QueuedCompiles);
        Assert.Equal(1, pipelines.CompiledAsync);

        BeginDraw(device, framebuffer, size, program);
        device.DrawFullscreenTriangle();
        device.DrawFullscreenTriangle();
        device.Present();

        Assert.Equal(colour, ReadCentre(device, framebuffer, size));
        Assert.Equal(1, pipelines.Count);
        Assert.Equal(1, pipelines.QueuedCompiles);
        Assert.Equal(1, pipelines.CompiledAsync);
        Assert.Equal(0, pipelines.CompiledSync);
        Assert.Equal(0, pipelines.PendingCompiles);
        GpuTest.AssertClean(device);
    }

    /// <summary>
    /// The launch-to-launch prewarm: a session records the pipelines it used in the key log;
    /// the next session builds them on the worker as soon as their program links, and the
    /// first draw that asks is served without a compile or a skipped frame.
    /// </summary>
    [SkippableFact]
    public void APrewarmFromTheKeyLogServesTheFirstUseWithoutACompile()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optimum-pipeline-prewarm-" + Guid.NewGuid().ToString("N"));
        const int size = 8;
        byte[] colour = UniqueColour();
        string fragment = SolidFragment(colour);

        try
        {
            PipelineCacheIdentity identity;
            using (VulkanDevice? first = OpenDevice(synchronousPipelines: true, cacheRoot: root))
            {
                Skip.If(first == null, "No usable Vulkan device.");
                Skip.IfNot(first!.ContextForTests.Capabilities.PipelineCreationCacheControl,
                    "The device lacks pipelineCreationCacheControl.");
                identity = PipelineCacheIdentity.Of(first.ContextForTests.Capabilities);

                int program = VulkanDeviceIntegrationTests.LinkProgram(first, FullscreenVertex, fragment);
                int framebuffer = ColourTarget(first, size);
                BeginDraw(first, framebuffer, size, program);
                first.DrawFullscreenTriangle();
                first.Present();
                Assert.Equal(colour, ReadCentre(first, framebuffer, size));
                Assert.Equal(1, first.PipelinesForTests.CompiledSync);
                GpuTest.AssertClean(first);
            }

            Assert.True(System.IO.File.Exists(PipelineKeyLog.PathFor(root, identity)), "no key log was written at shutdown");
            Assert.Equal(1, PipelineKeyLog.Load(PipelineKeyLog.PathFor(root, identity)).Count);

            using VulkanDevice? second = OpenDevice(synchronousPipelines: false, cacheRoot: root);
            Skip.If(second == null, "No usable Vulkan device.");
            GraphicsPipelineCache pipelines = second!.PipelinesForTests;
            Assert.True(pipelines.AsyncCompiles);

            int relinked = VulkanDeviceIntegrationTests.LinkProgram(second, FullscreenVertex, fragment);
            Assert.Equal(1, pipelines.PendingCompiles);
            Assert.True(pipelines.WaitForBackgroundCompiles(TimeSpan.FromSeconds(60)), "the prewarm did not finish");
            Assert.Equal(1, pipelines.Prewarmed);

            int target = ColourTarget(second, size);
            BeginDraw(second, target, size, relinked);
            Assert.Equal(1, pipelines.PrewarmedWaiting);
            second.DrawFullscreenTriangle();
            second.Present();

            Assert.Equal(colour, ReadCentre(second, target, size));
            Assert.Equal(1, pipelines.PrewarmHits);
            Assert.Equal(0, pipelines.DrawsSkipped);
            Assert.Equal(0, pipelines.CompiledSync);
            Assert.Equal(0, pipelines.CompiledAsync);
            Assert.Equal(0, pipelines.QueuedCompiles);
            _output.WriteLine($"prewarmed {pipelines.Prewarmed}, of them from the seeded driver cache {pipelines.Warm}");
            GpuTest.AssertClean(second);
        }
        finally
        {
            try
            {
                System.IO.Directory.Delete(root, recursive: true);
            }
            catch (System.IO.DirectoryNotFoundException)
            {
            }
        }
    }

    /// <summary>
    /// One blocking session under <paramref name="root" /> that draws <paramref name="fragment" />
    /// once, so the root then holds a key log entry and a driver cache that contain that
    /// pipeline. False when there is no device with pipelineCreationCacheControl.
    /// </summary>
    private bool RecordSession(string root, string fragment, byte[] colour, int size)
    {
        using VulkanDevice? first = OpenDevice(synchronousPipelines: true, cacheRoot: root);
        if (first == null || !first.ContextForTests.Capabilities.PipelineCreationCacheControl) return false;
        int program = VulkanDeviceIntegrationTests.LinkProgram(first, FullscreenVertex, fragment);
        int framebuffer = ColourTarget(first, size);
        BeginDraw(first, framebuffer, size, program);
        first.DrawFullscreenTriangle();
        first.Present();
        Assert.Equal(colour, ReadCentre(first, framebuffer, size));
        GpuTest.AssertClean(first);
        return true;
    }

    private static void DeleteRoot(string root)
    {
        try
        {
            System.IO.Directory.Delete(root, recursive: true);
        }
        catch (System.IO.DirectoryNotFoundException)
        {
        }
    }

    /// <summary>
    /// A prewarm job still waiting in its queue must not cost a draw the driver cache can serve
    /// on the spot: on a warm start (or a shader reload with unchanged sources) every program
    /// queues its prewarm when it links, and the workers reach the last of them seconds later.
    /// The lookup tries the driver cache itself, takes the pipeline, and the queued job is
    /// dropped rather than built a second time.
    /// </summary>
    [SkippableFact]
    public void AQueuedPrewarmDoesNotSkipADrawTheDriverCacheCanServe()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optimum-pipeline-queued-prewarm-" + Guid.NewGuid().ToString("N"));
        const int size = 8;
        byte[] colour = UniqueColour();
        string fragment = SolidFragment(colour);
        try
        {
            Skip.IfNot(RecordSession(root, fragment, colour, size), "No device with pipelineCreationCacheControl.");

            using VulkanDevice? second = OpenDevice(synchronousPipelines: false, cacheRoot: root);
            Skip.If(second == null, "No usable Vulkan device.");
            GraphicsPipelineCache pipelines = second!.PipelinesForTests;
            Skip.IfNot(pipelines.SeedAccepted, "The driver rejected its own saved cache.");
            pipelines.HoldBackgroundCompilesForTests = true;

            int program = VulkanDeviceIntegrationTests.LinkProgram(second, FullscreenVertex, fragment);
            Assert.Equal(1, pipelines.PendingCompiles);

            int target = ColourTarget(second, size);
            BeginDraw(second, target, size, program);
            second.DrawFullscreenTriangle();
            second.Present();

            Assert.Equal(colour, ReadCentre(second, target, size));
            Assert.Equal(0, pipelines.DrawsSkipped);
            Assert.Equal(0, pipelines.CompiledSync);
            Assert.Equal(1, pipelines.Warm);

            pipelines.HoldBackgroundCompilesForTests = false;
            Assert.True(pipelines.WaitForBackgroundCompiles(TimeSpan.FromSeconds(60)), "the workers did not drain");
            BeginDraw(second, target, size, program);
            second.DrawFullscreenTriangle();
            second.Present();

            Assert.Equal(0, pipelines.PendingCompiles);
            Assert.Equal(0, pipelines.PrewarmedWaiting);
            Assert.Equal(0, pipelines.Prewarmed);
            Assert.Equal(1, pipelines.Count);
            GpuTest.AssertClean(second);
        }
        finally
        {
            DeleteRoot(root);
        }
    }

    /// <summary>
    /// A compile that finished but was not yet published when its program was deleted (the
    /// window between the worker's completion and the next frame start) belongs to the
    /// deleted program like a queued or running one: it is destroyed at publication, never
    /// parked as a prewarmed pipeline no lookup can claim.
    /// </summary>
    [SkippableFact]
    public void AFinishedCompileOfADeletedProgramIsDestroyedNotPublished()
    {
        string root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "optimum-pipeline-deleted-program-" + Guid.NewGuid().ToString("N"));
        const int size = 8;
        byte[] colour = UniqueColour();
        string fragment = SolidFragment(colour);
        try
        {
            Skip.IfNot(RecordSession(root, fragment, colour, size), "No device with pipelineCreationCacheControl.");

            using VulkanDevice? second = OpenDevice(synchronousPipelines: false, cacheRoot: root);
            Skip.If(second == null, "No usable Vulkan device.");
            GraphicsPipelineCache pipelines = second!.PipelinesForTests;

            int program = VulkanDeviceIntegrationTests.LinkProgram(second, FullscreenVertex, fragment);
            Assert.Equal(1, pipelines.PendingCompiles);
            Assert.True(pipelines.WaitForBackgroundCompiles(TimeSpan.FromSeconds(60)), "the prewarm did not finish");

            second.DeleteProgram(program);
            int target = ColourTarget(second, size);
            second.BeginFrame();
            second.BindFramebuffer(target);
            second.ClearColor(0, 0f, 0f, 0f, 1f);
            second.Present();

            Assert.Equal(0, pipelines.PendingCompiles);
            Assert.Equal(0, pipelines.PrewarmedWaiting);
            GpuTest.AssertClean(second);
        }
        finally
        {
            DeleteRoot(root);
        }
    }
}
