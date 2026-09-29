using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

public class PipelineCacheTests(ITestOutputHelper output)
{
    private const string Vertex = """
        #version 330 core
        void main() { gl_Position = vec4(-1 + ((gl_VertexID & 1) << 2), -1 + ((gl_VertexID & 2) << 1), 0, 1); }
        """;

    [SkippableFact]
    public void GamePipelinesReuseStateAndPersistForTheSameDriver()
    {
        Skip.If(ShaderCorpus.AssetRoot == null, "No bootstrapped game assets.");
        var messages = new List<string>();
        var context = GpuTest.CreateContext(output, messages);
        string root = Path.Combine(Path.GetTempPath(), "optimum-cache-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var compiler = new ShaderCompiler();
            var identity = PipelineCacheIdentity.Of(context.Capabilities);
            var persistence = PipelineCachePersistence.Open(root, identity, out var seed,
                thresholdBytes: 1, interval: TimeSpan.FromSeconds(1));
            Assert.Null(seed);
            using var cache = new GraphicsPipelineCache(context) { KeyLog = persistence.KeyLog };
            var files = ShaderCorpus.LoadShaderFiles(); var includes = ShaderCorpus.LoadIncludes();
            var variant = ShaderCorpus.Variants().Single(v => v.Name == "everything-on");
            string[] names = { "blit", "final", "luma", "findbright", "godrays", "chunkopaque" };
            var programs = new List<ShaderProgramResources>();
            try
            {
                for (int i = 0; i < names.Length; i++)
                {
                    var translated = ShaderTranslator.Translate(ShaderCorpus.BuildProgram(names[i], files, includes, variant), compiler);
                    Assert.True(translated.Success, names[i] + ": " + string.Join("; ", translated.Errors));
                    var program = new ShaderProgramResources(context, i + 1, translated);
                    programs.Add(program);
                    Assert.NotEqual(0ul, program.PipelineLayout.Handle);
                    var shared = program.StandaloneLayout!;
                    foreach (var layout in new[] { shared.FrameSetLayout, shared.TextureSetLayout, shared.StorageSetLayout })
                        Assert.NotEqual(0ul, layout.Handle);
                    if (names[i] == "chunkopaque")
                    {
                        var storage = Assert.Single(translated.Layout.StorageBlocks);
                        Assert.Equal(SetConvention.StorageSet, storage.Set);
                        Assert.Equal(SetConvention.FaceDataBinding, storage.Binding);
                        continue; // Its vertex and face-data drawing path is covered by terrain acceptance.
                    }
                    if (names[i] == "final")
                    {
                        Assert.True(translated.Layout.Samplers.Count >= 4);
                        Assert.True(translated.Layout.BlockSize > 0);
                    }
                    var targets = new RenderTargetFormats(new[] { Format.R8G8B8A8Unorm }, Format.Undefined);
                    var key = new PipelineKey(i + 1, 0, 0, 0, PolygonMode.Fill, 2);
                    AttachmentBlend blend = AttachmentBlend.Default;
                    GraphicsPipelineCache.PipelineRequest Request() => new() {
                        Program = program, VertexLayout = VertexLayoutDescription.Empty, Targets = targets,
                        Blend = new[] { blend }, PolygonMode = PolygonMode.Fill, Topology = PrimitiveTopology.TriangleList,
                    };
                    var first = cache.Get(key, Request());
                    Assert.NotEqual(0ul, first.Handle);
                    Assert.Equal(first.Handle, cache.Get(key, Request()).Handle);
                    Assert.Equal(first.Handle, cache.Get(key, Request()).Handle);
                    blend = AttachmentBlend.For(true, EnumBlendMode.Glow);
                    key = key with { BlendId = 1 };
                    Assert.NotEqual(first.Handle, cache.Get(key, Request()).Handle);
                }
                Assert.Equal(10, cache.Count); Assert.Equal(10, cache.Misses); Assert.Equal(10, cache.Hits);
                long second = Stopwatch.Frequency;
                Assert.False(persistence.Tick(cache, 10 * second));
                Assert.False(persistence.Tick(cache, 10 * second + second / 2));
                Assert.True(persistence.Tick(cache, 11 * second)); persistence.WaitForPendingSave();
                Assert.Equal(1, persistence.Saves); Assert.False(persistence.KeyLog.HasUnsavedChanges);
                Assert.Equal(10, PipelineKeyLog.Load(persistence.KeyLogPath).Count);
                var saved = PipelineCacheFile.Load(persistence.CachePath, identity);
                Assert.NotNull(saved); Assert.True(PipelineCacheFile.HasMatchingVulkanHeader(saved, identity));
                using (var next = new GraphicsPipelineCache(context, saved)) Assert.True(next.SeedAccepted);
                persistence.SaveAtShutdown(cache); Assert.Equal(2, persistence.Saves);
            }
            finally { foreach (var program in programs) program.Dispose(); }
        }
        finally { context.Dispose(); DeleteRoot(root); }
        ValidationAssert.NoErrors(messages);
    }

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
    /// A parity frame written to disk must hold every draw. The setting works
    /// without a capture script and an explicit pipeline policy still wins.
    /// </summary>
    [Fact]
    public void AFrameCaptureForcesBlockingPipelinesWithoutTheScripts()
    {
        Assert.True(VulkanDevice.ResolveSynchronousPipelines(null, null, parityDump: "/tmp/dump"));
        Assert.True(VulkanDevice.ResolveSynchronousPipelines(null, "0", parityDump: "/tmp/dump"));
        // The capture code ignores a relative or blank directory, so the pipelines do too.
        Assert.False(VulkanDevice.ResolveSynchronousPipelines(null, null, parityDump: "relative"));
        Assert.False(VulkanDevice.ResolveSynchronousPipelines(false, null, parityDump: "/tmp/dump"));
    }


    private VulkanDevice Open(bool synchronous, string? root = null) => GpuTest.CreateDevice(output, device => {
        device.SynchronousPipelines = synchronous; device.ShaderCacheDirectory = root;
    });

    // Vary shader constants to avoid routinely reusing an earlier run's implicit driver cache.
    private static (string Fragment, byte[] Color) FreshShader()
    {
        var id = Guid.NewGuid().ToByteArray();
        byte[] color = { (byte)(1 + id[0] % 254), (byte)(1 + id[1] % 254), (byte)(1 + id[2] % 254), 255 };
        string fragment = string.Format(CultureInfo.InvariantCulture, """
            #version 330 core
            out vec4 color;
            void main() {{ color = vec4({0}.0 / 255.0, {1}.0 / 255.0, {2}.0 / 255.0, 1); }}
            """, color[0], color[1], color[2]);
        return (fragment, color);
    }

    private static (int Image, int Framebuffer) Target(VulkanDevice device)
    {
        int image = device.CreateTexture2DRaw(8, 8, 0x8058, IntPtr.Zero, 4);
        int target = device.CreateFramebuffer(8, 8);
        device.AttachTexture(target, EnumFramebufferAttachment.ColorAttachment0, image, 0);
        device.SetDrawBuffers(target, 1);
        return (image, target);
    }

    private static void Begin(VulkanDevice device, int target, int program)
    {
        device.BeginFrame(); device.BindFramebuffer(target); device.ClearColor(0, 0, 0, 0, 1);
        device.SetViewport(0, 0, 8, 8); device.SetDepthTest(false); device.SetDepthMask(false);
        device.SetCullFace(false); device.SetBlend(false, EnumBlendMode.Standard); device.UseProgram(program);
    }

    private static void Pixels(VulkanDevice device, int image, byte[] color)
    {
        byte[] pixels = device.ReadBackLevel0ForTests(image);
        Assert.Equal(8 * 8 * 4, pixels.Length);
        for (int i = 0; i < pixels.Length; i++) Assert.Equal(color[i % 4], pixels[i]);
    }

    private static void Drain(GraphicsPipelineCache cache) =>
        Assert.True(cache.WaitForBackgroundCompiles(TimeSpan.FromSeconds(60)), "Pipeline workers did not finish.");

    [SkippableFact]
    public void ColdRequestsCompileOnceAndPublishAtTheNextFrame()
    {
        var device = Open(false);
        try
        {
            var cache = device.PipelinesForTests;
            Skip.IfNot(cache.AsyncCompiles, "Device lacks pipelineCreationCacheControl.");
            var shader = FreshShader(); int program = GpuTest.LinkProgram(device, Vertex, shader.Fragment);
            var target = Target(device);
            Begin(device, target.Framebuffer, program);
            device.DrawFullscreenTriangle(); device.DrawFullscreenTriangle();
            Assert.Equal(2, cache.DrawsSkipped); Assert.Equal(1, cache.QueuedCompiles);
            Assert.Equal(0, cache.Count); Assert.Equal(0, cache.CompiledSync);
            device.Present(); Pixels(device, target.Image, new byte[] { 0, 0, 0, 255 });
            Drain(cache); Assert.Equal(1, cache.CompiledAsync); Assert.Equal(0, cache.Count);
            Begin(device, target.Framebuffer, program); Assert.Equal(1, cache.Count);
            device.DrawFullscreenTriangle(); device.DrawFullscreenTriangle();
            device.Present(); Pixels(device, target.Image, shader.Color);
            Assert.Equal(2, cache.Hits); Assert.Equal(2, cache.DrawsSkipped);
            Assert.Equal(1, cache.QueuedCompiles); Assert.Equal(0, cache.PendingCompiles);
        }
        finally { device.Dispose(); }
        GpuTest.AssertClean(device);
    }

    [SkippableTheory]
    [InlineData("completed")]
    [InlineData("queued")]
    [InlineData("deleted")]
    public void PersistedProgramsPrewarmWithoutLosingDrawsOrPublishingDeletedPrograms(string state)
    {
        string root = Path.Combine(Path.GetTempPath(), "optimum-prewarm-" + Guid.NewGuid().ToString("N"));
        var shader = FreshShader();
        try
        {
            var first = Open(true, root);
            PipelineCacheIdentity identity;
            try
            {
                Skip.IfNot(first.ContextForTests.Capabilities.PipelineCreationCacheControl, "Device lacks pipelineCreationCacheControl.");
                identity = PipelineCacheIdentity.Of(first.ContextForTests.Capabilities);
                int program = GpuTest.LinkProgram(first, Vertex, shader.Fragment);
                var target = Target(first); Begin(first, target.Framebuffer, program);
                first.DrawFullscreenTriangle(); first.Present(); Pixels(first, target.Image, shader.Color);
                Assert.Equal(1, first.PipelinesForTests.CompiledSync);
            }
            finally { first.Dispose(); }
            GpuTest.AssertClean(first);
            Assert.Equal(1, PipelineKeyLog.Load(PipelineKeyLog.PathFor(root, identity)).Count);
            var second = Open(false, root);
            try
            {
                var cache = second.PipelinesForTests;
                Assert.True(cache.AsyncCompiles); Assert.True(cache.SeedAccepted);
                cache.HoldBackgroundCompilesForTests = state == "queued";
                int program = GpuTest.LinkProgram(second, Vertex, shader.Fragment);
                Assert.Equal(1, cache.PendingCompiles);
                if (state != "queued") Drain(cache);
                if (state == "deleted") second.DeleteProgram(program);
                var target = Target(second);
                Begin(second, target.Framebuffer, state == "deleted" ? 0 : program);
                if (state != "deleted") second.DrawFullscreenTriangle();
                second.Present();
                Pixels(second, target.Image, state == "deleted" ? new byte[] { 0, 0, 0, 255 } : shader.Color);
                Assert.Equal(0, cache.DrawsSkipped); Assert.Equal(0, cache.CompiledSync);
                Assert.Equal(0, cache.CompiledAsync); Assert.Equal(0, cache.QueuedCompiles);
                if (state == "completed") Assert.Equal(1, cache.PrewarmHits);
                if (state == "queued") Assert.Equal(1, cache.Warm);
                cache.HoldBackgroundCompilesForTests = false; Drain(cache);
                second.BeginFrame(); second.Present();
                Assert.Equal(0, cache.PendingCompiles); Assert.Equal(0, cache.PrewarmedWaiting);
                Assert.Equal(state == "deleted" ? 0 : 1, cache.Count);
                if (state == "queued") Assert.Equal(0, cache.Prewarmed);
            }
            finally { second.PipelinesForTests.HoldBackgroundCompilesForTests = false; second.Dispose(); }
            GpuTest.AssertClean(second);
        }
        finally { DeleteRoot(root); }
    }

    private static void DeleteRoot(string root)
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
