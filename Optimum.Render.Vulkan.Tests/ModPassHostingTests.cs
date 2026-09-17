using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Graph;
using Optimum.Render.Vulkan.Platform;
using Optimum.Render.Vulkan.Tests.Fixtures;
using Optimum.Render.Vulkan.Tests.Fixtures.ModPassFixture;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>The mod pass registry and the motion hooks are process statics: these tests run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ModPassCollection
{
    public const string Name = "Optimum mod passes (process statics)";
}

/// <summary>
/// Vulkan-native plan, Phase 5: mod-declared passes and motion writers. The fixture mod
/// (Fixtures/ModPassFixture) registers one AfterOIT pass that samples Primary's glow, writes
/// Primary's colour and depth and is a motion writer, plus one renderer motion writer. On Vulkan
/// the platform runs the pass at the end of the AfterOIT bracket and nowhere else, with glow out of
/// the scope and shader-readable, colour, depth and motion attached, and the motion window open
/// around the draw; the result reads back after a multi-frame run with zero validation messages.
/// On OpenGL nothing of it runs.
/// </summary>
[Collection(ModPassCollection.Name)]
public class ModPassHostingTests
{
    private readonly ITestOutputHelper _output;

    public ModPassHostingTests(ITestOutputHelper output) => _output = output;

    private const int Size = 8;
    private static readonly float[] GlowClear = { 0.8f, 0.4f, 0.2f, 1f };

    private const string FullscreenVertex = """
        #version 330 core
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
        }
        """;

    private const string TintFragment = """
        #version 330 core
        uniform sampler2D glowTex;
        layout(location = 0) out vec4 outColor;
        layout(location = 2) out vec4 outMotion;
        void main(void)
        {
            vec4 glow = texelFetch(glowTex, ivec2(gl_FragCoord.xy), 0);
            outColor = vec4(glow.rgb * 0.5, 1.0);
            outMotion = vec4(1.5, -2.5, 0.25, gl_FragCoord.z);
        }
        """;

    private static ClientMain HeadlessGame(ClientPlatformAbstract platform)
    {
        ScreenManager.FrameProfiler ??= new FrameProfilerUtil(static (string _) => { });
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        game.Platform = platform;
        return game;
    }

    private sealed class DrawRecord
    {
        public EnumRenderStage Stage;
        public bool InStage;
        public bool MotionWindow;
        public string? PassName;
        public ImageLayout Colour, Glow, Motion, Depth;
        public long UndeclaredSplits;
    }

    [SkippableFact]
    public void TheFixturePassRunsAtItsSlotWithTheDeclaredAttachmentStatesAndNoValidationMessages()
    {
        string dataPath = Path.Combine(Path.GetTempPath(), "optimum-mod-pass-test-" + Guid.NewGuid().ToString("N"));
        var platform = new VulkanClientPlatform(null!)
        {
            DeviceFactory = GpuTest.NewDevice,
            CrashMarkerDataPath = dataPath,
        };
        (ICoreClientAPI api, ClientApiStub stub) = ClientApiStub.Create();
        var fixture = new ModPassFixtureSystem();
        bool taa = OptimumConfig.Taa;
        try
        {
            bool installed = platform.InitializeGraphics(IntPtr.Zero, 0, 0, out string reason);
            if (!installed) _output.WriteLine("Vulkan unavailable: " + reason);
            Skip.IfNot(installed, "No usable Vulkan device.");
            VulkanDevice seam = platform.GraphicsDevice!;
            Assert.NotNull(OptimumModPasses.MotionBeginHook);

            OptimumConfig.Taa = true;
            Assert.True(OptimumConfig.EffectiveTaa, "TAA is explicitly disabled by a launcher scan on this machine");
            FrameBufferRef primary = CreatePrimary(seam);
            InstallFrameBuffers(platform, primary);

            int program = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, TintFragment, "mod-pass-fixture");
            seam.SetSamplerUnit(program, "glowTex", 0);

            fixture.StartClientSide(api);
            Assert.Single(OptimumModPasses.ForSlot(EnumOptimumPass.AfterOIT));
            Assert.Single(stub.LeaveWorldHandlers);

            var draws = new List<DrawRecord>();
            FrameGraph graph = seam.FrameGraphForTests;
            fixture.Drawer = _ =>
            {
                seam.SetViewport(0, 0, Size, Size);
                platform.GlEnableDepthTest();
                platform.GlDepthMask(true);
                platform.GlDisableCullFace();
                platform.GlToggleBlend(false);
                platform.UseShaderProgram(program);
                seam.BindTexture(0, primary.ColorTextureIds[1]);
                seam.DrawFullscreenTriangle();
                draws.Add(new DrawRecord
                {
                    Stage = platform.CurrentRenderStage,
                    InStage = platform.InRenderStage,
                    MotionWindow = platform.OptimumMotionWriteActive,
                    PassName = platform.CurrentModPass,
                    Colour = seam.TextureLayoutForTests(primary.ColorTextureIds[0]),
                    Glow = seam.TextureLayoutForTests(primary.ColorTextureIds[1]),
                    Motion = seam.TextureLayoutForTests(primary.ColorTextureIds[2]),
                    Depth = seam.TextureLayoutForTests(primary.DepthTextureId),
                    UndeclaredSplits = graph.UndeclaredSplits,
                });
            };

            ClientMain game = HeadlessGame(platform);
            const int frames = 4;
            long declaredBefore = graph.DeclaredPasses;
            OptimumTemporal.Frame.JitterActive = true;
            for (int frame = 0; frame < frames; frame++)
            {
                platform.BeginFrame();
                platform.CurrentFrameBuffer = primary;
                seam.SetDrawBuffers(primary.FboId, 0b111);
                seam.ClearColor(0, 0f, 0f, 0f, 1f);
                seam.ClearColor(1, GlowClear[0], GlowClear[1], GlowClear[2], GlowClear[3]);
                seam.ClearColor(2, 0f, 0f, 0f, 0f);
                seam.ClearDepth(1f);
                seam.SetDrawBuffers(primary.FboId, 0b011);

                game.TriggerRenderStage(EnumRenderStage.Before, 0.016f);
                game.TriggerRenderStage(EnumRenderStage.Opaque, 0.016f);
                game.TriggerRenderStage(EnumRenderStage.OIT, 0.016f);
                game.TriggerRenderStage(EnumRenderStage.AfterOIT, 0.016f);
                Assert.Same(primary, platform.CurrentFrameBuffer);
                Assert.False(platform.OptimumMotionWriteActive, "the window closes with the pass");

                // A renderer's registered writer opens the window inside Opaque on Primary only.
                platform.BeginRenderStage(EnumRenderStage.Opaque);
                platform.CurrentFrameBuffer = primary;
                Assert.False(OptimumModPasses.BeginMotionWriter(new OptimumMotionWriterDecl { Name = "unregistered" }));
                Assert.True(OptimumModPasses.BeginMotionWriter(fixture.Writer!));
                Assert.True(platform.OptimumMotionWriteActive);
                OptimumModPasses.EndMotionWriter();
                Assert.False(platform.OptimumMotionWriteActive);
                platform.EndRenderStage(EnumRenderStage.Opaque);

                OptimumTemporal.Frame.JitterActive = false;
                game.TriggerRenderStage(EnumRenderStage.AfterPostProcessing, 0.016f);
                platform.BeginRenderStage(EnumRenderStage.AfterFinalComposition);
                platform.CurrentFrameBuffer = primary;
                Assert.False(OptimumModPasses.BeginMotionWriter(fixture.Writer!), "no motion window outside the temporal window");
                platform.EndRenderStage(EnumRenderStage.AfterFinalComposition);
                game.TriggerRenderStage(EnumRenderStage.Ortho, 0.016f);
                OptimumTemporal.Frame.JitterActive = true;
                platform.EndFrame();
            }
            OptimumTemporal.Frame.JitterActive = false;

            Assert.Equal(frames, draws.Count);
            foreach (DrawRecord draw in draws)
            {
                Assert.Equal(EnumRenderStage.AfterOIT, draw.Stage);
                Assert.True(draw.InStage);
                Assert.True(draw.MotionWindow, "the pass is a motion writer");
                Assert.Equal("Mod/" + fixture.ModId + "/" + ModPassFixtureSystem.PassName + "/0", draw.PassName);
                Assert.Equal(ImageLayout.ColorAttachmentOptimal, draw.Colour);
                Assert.Equal(ImageLayout.ShaderReadOnlyOptimal, draw.Glow);
                Assert.Equal(ImageLayout.ColorAttachmentOptimal, draw.Motion);
                Assert.Equal(ImageLayout.DepthAttachmentOptimal, draw.Depth);
                Assert.Equal(0, draw.UndeclaredSplits);
            }
            Assert.Equal(frames, platform.ModPassesRun);
            Assert.Equal(0, platform.ModPassesSkipped);
            Assert.True(graph.DeclaredPasses > declaredBefore);
            Assert.Equal(0, graph.UndeclaredSplits);

            seam.BeginFrame();
            byte[] colour = seam.ReadBackLevel0ForTests(primary.ColorTextureIds[0]);
            byte[] glow = seam.ReadBackLevel0ForTests(primary.ColorTextureIds[1]);
            byte[] motion = seam.ReadBackLevel0ForTests(primary.ColorTextureIds[2]);
            byte[] depth = seam.ReadBackLevel0ForTests(primary.DepthTextureId);
            seam.Present();

            for (int p = 0; p < Size * Size; p++)
            {
                for (int c = 0; c < 3; c++)
                {
                    double glowByte = Math.Round(GlowClear[c] * 255);
                    Assert.InRange((double)glow[p * 4 + c], glowByte - 0.5, glowByte + 0.5);
                    Assert.InRange((double)colour[p * 4 + c], glowByte * 0.5 - 1.1, glowByte * 0.5 + 1.1);
                }
                Assert.Equal(255, colour[p * 4 + 3]);
                Assert.Equal(1.5f, (float)BitConverter.ToHalf(motion, p * 8));
                Assert.Equal(-2.5f, (float)BitConverter.ToHalf(motion, p * 8 + 2));
                Assert.Equal(0.25f, (float)BitConverter.ToHalf(motion, p * 8 + 4));
            }
            float[] depths = MemoryMarshal.Cast<byte, float>(depth).ToArray();
            for (int p = 0; p < Size * Size; p++)
            {
                Assert.Equal(0.5f, depths[p]);
                Assert.InRange((float)BitConverter.ToHalf(motion, p * 8 + 6), 0.4995f, 0.5005f);
            }

            // Zero validation errors and zero synchronization messages (sync,best on). What remains
            // are the device-wide best-practices advisories every GPU test's device reports
            // (vendor memory-priority, D32 format, push-constant range); anything else fails.
            GpuTest.AssertClean(seam);
            foreach (string message in ValidationAssert.Snapshot(GpuTest.MessagesOf(seam)))
            {
                Assert.True(message.StartsWith("[warning] [BestPractices-", StringComparison.Ordinal), "validation message: " + message);
                Assert.DoesNotContain("SYNC-", message);
            }

            // Leaving the world unloads the mod: its registrations go with it.
            stub.LeaveWorld();
            Assert.Empty(OptimumModPasses.ForSlot(EnumOptimumPass.AfterOIT));
            Assert.Empty(stub.LeaveWorldHandlers);
        }
        finally
        {
            OptimumTemporal.Frame.JitterActive = false;
            OptimumConfig.Taa = taa;
            fixture.Dispose();
            platform.ShutdownGraphics();
            try
            {
                Directory.Delete(dataPath, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
        Assert.Null(OptimumModPasses.MotionBeginHook);
    }

    [Fact]
    public void TheOpenGlPlatformIgnoresRegistrations()
    {
        (ICoreClientAPI api, ClientApiStub _) = ClientApiStub.Create();
        var fixture = new ModPassFixtureSystem();
        int draws = 0;
        fixture.Drawer = _ => draws++;
        try
        {
            fixture.StartClientSide(api);
            Assert.Single(OptimumModPasses.ForSlot(EnumOptimumPass.AfterOIT));

            var platform = new ClientPlatformWindows(null!);
            ClientMain game = HeadlessGame(platform);
            foreach (EnumRenderStage stage in (EnumRenderStage[])Enum.GetValues(typeof(EnumRenderStage)))
                game.TriggerRenderStage(stage, 0.016f);

            Assert.Equal(0, draws);
            Assert.Null(OptimumModPasses.MotionBeginHook);
            Assert.False(OptimumModPasses.BeginMotionWriter(fixture.Writer!));
            OptimumModPasses.EndMotionWriter();
            Assert.False(platform.OptimumMotionWriteActive);

            // No member of the GL platform names the registry.
            foreach (MethodInfo method in typeof(ClientPlatformWindows).GetMethods(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                    Assert.NotEqual(typeof(OptimumPassDecl), parameter.ParameterType);
            }
        }
        finally
        {
            fixture.Dispose();
        }
        Assert.Empty(OptimumModPasses.ForSlot(EnumOptimumPass.AfterOIT));
    }

    [Fact]
    public void AHeadlessVulkanPlatformWithoutADeviceRunsNoModPass()
    {
        (ICoreClientAPI api, ClientApiStub _) = ClientApiStub.Create();
        var fixture = new ModPassFixtureSystem();
        int draws = 0;
        fixture.Drawer = _ => draws++;
        try
        {
            fixture.StartClientSide(api);
            var platform = new VulkanClientPlatform(null!);
            ClientMain game = HeadlessGame(platform);
            game.TriggerRenderStage(EnumRenderStage.AfterOIT, 0.016f);
            Assert.Equal(0, draws);
            Assert.Equal(0, platform.ModPassesRun);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private static FrameBufferRef CreatePrimary(VulkanDevice seam)
    {
        var primary = new FrameBufferRef
        {
            Width = Size,
            Height = Size,
            FboId = seam.CreateFramebuffer(Size, Size),
            ColorTextureIds = new[]
            {
                seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
                seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
                seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false),
            },
            DepthTextureId = seam.CreateTexture2D(Size, Size,
                EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false),
        };
        for (int slot = 0; slot < primary.ColorTextureIds.Length; slot++)
            seam.AttachTexture(primary.FboId, (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + slot),
                primary.ColorTextureIds[slot], 0);
        seam.AttachTexture(primary.FboId, EnumFramebufferAttachment.DepthAttachment, primary.DepthTextureId, 0);
        seam.SetDrawBuffers(primary.FboId, 0b011);
        Assert.True(seam.CheckFramebufferComplete(primary.FboId, out string status), status);
        return primary;
    }

    /// <summary>Primary at slot 0 with its motion attachment at 2 (no SSAO G-buffer), TAA targets ready.</summary>
    private static void InstallFrameBuffers(VulkanClientPlatform platform, FrameBufferRef primary)
    {
        var list = new List<FrameBufferRef>();
        for (int i = 0; i <= 24; i++) list.Add(null!);
        list[0] = primary;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(ClientPlatformWindows).GetField("frameBuffers", flags)!.SetValue(platform, list);
        platform.SetOptimumMotionAttachmentIndex(2);
        typeof(ClientPlatformWindows).GetField("optimumTaaTargetsReady", flags)!.SetValue(platform, true);
        Assert.True(platform.TaaTargetsReady);
    }
}
