// Source: Optimum.Render.Vulkan.Tests/ModPassHostingTests.cs
namespace Optimum.Render.Vulkan.Tests
{
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

            int program = GpuTest.LinkProgram(seam, FullscreenVertex, TintFragment, "mod-pass-fixture");
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
}

// Source: Optimum.Render.Vulkan.Tests/PassExclusionTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using Optimum.Render.Vulkan.Graph;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;
using static Optimum.Render.Vulkan.Tests.GpuTest;

/// <summary>
/// Phase 2 review (2026-09-11): an attachment-subset pass (the final composition leaves
/// Primary 1 out of its scope through <see cref="PassDeclaration.ColorSlots" />) must treat
/// the left-out slot as what it is, a texture outside the scope:
/// <list type="bullet">
/// <item>sampling it with its draw buffer off does not close the pass's open scope (it was
/// never in it, so there is nothing to exclude and no split);</item>
/// <item>sampling it with its draw buffer on is not attachment feedback, so it takes no
/// ReadSelf copy and no split;</item>
/// <item>a clear on it with its draw buffer on is not dropped: GL clears the texture, so
/// the clear is promoted and lands before the next use.</item>
/// </list>
/// The same frame with the frame graph off (the slot then stays in the scope) gives the
/// same pixels.
/// </summary>
public class PassExclusionTests
{
    private readonly ITestOutputHelper _output;

    public PassExclusionTests(ITestOutputHelper output) => _output = output;

    private const int Size = 8;

    private const string FullscreenVertex = """
        #version 330 core
        out vec2 uv;
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.5, 1.0);
            uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
        }
        """;

    public static TheoryData<bool> FrameGraph => new() { true, false };

    [SkippableTheory]
    [MemberData(nameof(FrameGraph))]
    public void ALeftOutSlotIsSampledAndClearedLikeATextureOutsideTheScope(bool frameGraph)
    {
        VulkanDevice created = NewDevice();
        created.FrameGraphEnabled = frameGraph;
        if (!created.Initialize(IntPtr.Zero, 0, 0, out string failureReason))
        {
            created.Dispose();
            Skip.If(true, "Vulkan unavailable: " + failureReason);
        }

        using VulkanDevice seam = created;
        FrameGraph graph = seam.FrameGraphForTests;
        int Texture() => seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int c0 = Texture(), c1 = Texture();
        int target = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(target, EnumFramebufferAttachment.ColorAttachment0, c0, 0);
        seam.AttachTexture(target, EnumFramebufferAttachment.ColorAttachment1, c1, 0);

        int constant = GpuTest.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            layout(location = 0) out vec4 outColor;
            void main(void) { outColor = vec4(1.0, 0.0, 0.0, 1.0); }
            """, "px-constant");
        int copy = GpuTest.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            uniform sampler2D tex;
            in vec2 uv;
            layout(location = 0) out vec4 outColor;
            void main(void) { outColor = texture(tex, uv); }
            """, "px-copy");
        seam.SetSamplerUnit(copy, "tex", 0);

        // Seed: c0 black, c1 grey.
        seam.BeginFrame();
        BaseState(seam);
        seam.DeclarePass(new PassDeclaration { Name = "Seed", FramebufferId = target });
        seam.SetDrawBuffers(target, 0b11);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.ClearColor(1, 0.2f, 0.2f, 0.2f, 1f);
        seam.Present();

        seam.BeginFrame();
        BaseState(seam);
        long scopesBefore = seam.ScopesOpenedForTests;
        long splitsBefore = graph.Splits;
        long feedbackBefore = seam.FeedbackSplitsForTests;
        long copiesBefore = seam.ReadSelfCopiesForTests.Created;

        seam.DeclarePass(new PassDeclaration
        {
            Name = "Compose", FramebufferId = target, ColorSlots = ~(1u << 1), Reads = new[] { c1 },
        });

        // Draw buffer of the left-out slot off: the first draw opens the scope, the second samples the slot.
        seam.SetDrawBuffers(target, 0b01);
        seam.UseProgram(constant);
        seam.DrawFullscreenTriangle();
        seam.UseProgram(copy);
        seam.BindTexture(0, c1);
        seam.DrawFullscreenTriangle();
        long splitsAfterSample = graph.Splits - splitsBefore;

        // Draw buffer on: sampling the left-out slot is still not feedback.
        seam.SetDrawBuffers(target, 0b11);
        seam.DrawFullscreenTriangle();
        seam.BindTexture(0, 0);
        long splitsAfterDrawBufferOn = graph.Splits - splitsBefore;
        long copies = seam.ReadSelfCopiesForTests.Created - copiesBefore;
        long scopes = seam.ScopesOpenedForTests - scopesBefore;
        long feedback = seam.FeedbackSplitsForTests - feedbackBefore;

        // A clear on the left-out slot with its draw buffer on clears it, as GL does.
        seam.ClearColor(1, 0f, 0f, 1f, 1f);
        seam.EndPass();
        seam.SetDrawBuffers(target, 0b01);
        seam.Present();

        seam.BeginFrame();
        byte[] first = seam.ReadBackLevel0ForTests(c0);
        byte[] second = seam.ReadBackLevel0ForTests(c1);
        seam.Present();

        _output.WriteLine($"frameGraph={frameGraph} scopes={scopes} splits_after_sample={splitsAfterSample} " +
                          $"splits_after_draw_buffer_on={splitsAfterDrawBufferOn} feedback_splits={feedback} readself_copies={copies}");

        int centre = (Size / 2 * Size + Size / 2) * 4;
        Assert.Equal(new byte[] { 51, 51, 51, 255 }, first.AsSpan(centre, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, second.AsSpan(centre, 4).ToArray());
        if (frameGraph)
        {
            Assert.Equal(0, splitsAfterSample);
            Assert.Equal(0, splitsAfterDrawBufferOn);
            Assert.Equal(0, feedback);
            Assert.Equal(0, copies);
            Assert.Equal(1, scopes);
        }
        AssertClean(seam);
    }

    private static void BaseState(VulkanDevice seam)
    {
        seam.SetViewport(0, 0, Size, Size);
        seam.SetScissorEnabled(false);
        seam.SetDepthTest(false);
        seam.SetDepthMask(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
        seam.SetColorMask(true, true, true, true);
    }
}
}

// Source: Optimum.Render.Vulkan.Tests/RenderStageHookTests.cs
namespace Optimum.Render.Vulkan.Tests
{
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Optimum.Render.Vulkan.Graph;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Xunit;

/// <summary>
/// Vulkan-native plan, Phase 2 (contract C3): the donor ClientMain.TriggerRenderStage brackets
/// each stage with the platform's BeginRenderStage/EndRenderStage, and VulkanClientPlatform
/// records the stage and forwards the bracket to its listener. Headless: the real
/// TriggerRenderStage runs on an uninitialised ClientMain (no event manager, so no renderers)
/// against a platform with no device; neither touches GL or Vulkan.
/// </summary>
public class RenderStageHookTests
{
    private sealed class RecordingListener : IRenderStageListener
    {
        public readonly List<string> Calls = new();
        public VulkanClientPlatform? Platform;
        public readonly List<string> Faults = new();

        public void OnBeginRenderStage(EnumRenderStage stage)
        {
            Calls.Add("begin " + stage);
            if (Platform != null && (!Platform.InRenderStage || Platform.CurrentRenderStage != stage))
                Faults.Add("begin " + stage + " saw stage " + Platform.CurrentRenderStage + " active=" + Platform.InRenderStage);
        }

        public void OnEndRenderStage(EnumRenderStage stage)
        {
            Calls.Add("end " + stage);
            if (Platform != null && (Platform.InRenderStage || Platform.CurrentRenderStage != stage))
                Faults.Add("end " + stage + " saw stage " + Platform.CurrentRenderStage + " active=" + Platform.InRenderStage);
        }
    }

    /// <summary>Every stage once, in declaration order (the order MainRenderLoop broadly follows).</summary>
    private static readonly EnumRenderStage[] FrameStages = (EnumRenderStage[])Enum.GetValues(typeof(EnumRenderStage));

    private static ClientMain HeadlessGame(ClientPlatformAbstract platform)
    {
        // TriggerRenderStage marks the profiler first; a disabled one returns immediately.
        ScreenManager.FrameProfiler ??= new FrameProfilerUtil(static (string _) => { });
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        game.Platform = platform;
        return game;
    }

    [Fact]
    public void AListenerSeesBeginAndEndForEachStageInOrder()
    {
        var platform = new VulkanClientPlatform(null!);
        var listener = new RecordingListener { Platform = platform };
        platform.RenderStageListener = listener;
        ClientMain game = HeadlessGame(platform);

        for (int frame = 0; frame < 2; frame++)
        {
            foreach (EnumRenderStage stage in FrameStages)
            {
                game.TriggerRenderStage(stage, 0.016f);
                Assert.False(platform.InRenderStage);
                Assert.Equal(stage, platform.CurrentRenderStage);
            }
        }

        var expected = new List<string>();
        for (int frame = 0; frame < 2; frame++)
        {
            foreach (EnumRenderStage stage in FrameStages)
            {
                expected.Add("begin " + stage);
                expected.Add("end " + stage);
            }
        }
        Assert.Equal(expected, listener.Calls);
        Assert.Empty(listener.Faults);
        Assert.True(FrameStages.Length >= 10);
    }

    [Fact]
    public void WithoutAListenerTheBracketOnlyTracksTheStage()
    {
        var platform = new VulkanClientPlatform(null!);
        Assert.Null(platform.RenderStageListener);
        ClientMain game = HeadlessGame(platform);

        game.TriggerRenderStage(EnumRenderStage.Opaque, 0.016f);
        Assert.Equal(EnumRenderStage.Opaque, platform.CurrentRenderStage);
        Assert.False(platform.InRenderStage);

        platform.BeginRenderStage(EnumRenderStage.OIT);
        Assert.True(platform.InRenderStage);
        Assert.Equal(EnumRenderStage.OIT, platform.CurrentRenderStage);
        platform.EndRenderStage(EnumRenderStage.OIT);
        Assert.False(platform.InRenderStage);
    }

    [Fact]
    public void TheOpenGlPlatformKeepsTheNeutralBodies()
    {
        var platform = new ClientPlatformWindows(null!);
        ClientMain game = HeadlessGame(platform);

        foreach (EnumRenderStage stage in FrameStages)
            game.TriggerRenderStage(stage, 0.016f);

        Assert.Equal(typeof(ClientPlatformAbstract),
            typeof(ClientPlatformWindows).GetMethod(nameof(ClientPlatformAbstract.BeginRenderStage))!.DeclaringType);
        Assert.Equal(typeof(ClientPlatformAbstract),
            typeof(ClientPlatformWindows).GetMethod(nameof(ClientPlatformAbstract.EndRenderStage))!.DeclaringType);
    }
}
}
