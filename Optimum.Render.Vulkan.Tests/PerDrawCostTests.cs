using System;
using Optimum.Render.Vulkan.Core;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Phase 1B step 6 against a real device, under the suite's sync,best validation:
/// dirty-masked dynamic state (fewer commands, same pixels, re-emitted for every
/// new recording), the per-slot descriptor arena (reset per frame, sets move to
/// the cache once their resources age), and the per-slot indirect ring (overflow
/// within a frame, growth at the boundary, every multi-draw's ranges intact).
/// </summary>
public class PerDrawCostTests
{
    private readonly ITestOutputHelper _output;

    public PerDrawCostTests(ITestOutputHelper output) => _output = output;

    private const int Size = 16;

    private const string FullscreenVertex = """
        #version 330 core
        out vec2 uv;
        void main(void)
        {
            float x = -1.0 + float((gl_VertexID & 1) << 2);
            float y = -1.0 + float((gl_VertexID & 2) << 1);
            gl_Position = vec4(x, y, 0.0, 1.0);
            uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
        }
        """;

    private static string SolidFragment(string colour) => """
        #version 330 core
        out vec4 outColor;
        void main(void) { outColor = vec4(
        """ + colour + "); }\n";

    private const string SampleFragment = """
        #version 330 core
        uniform sampler2D source;
        in vec2 uv;
        out vec4 outColor;
        void main(void) { outColor = texture(source, uv); }
        """;

    private const string MeshVertex = """
        #version 330 core
        layout(location = 0) in vec3 position;
        void main() { gl_Position = vec4(position, 1); }
        """;

    private static int CreateTarget(VulkanDevice seam)
    {
        int texture = seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8,
            EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(Size, Size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
        seam.SetDrawBuffers(framebuffer, 1);
        return framebuffer;
    }

    private static unsafe byte[] Read(VulkanDevice seam, int framebuffer)
    {
        var pixels = new byte[Size * Size * 4];
        fixed (byte* destination = pixels)
        {
            seam.BindFramebuffer(framebuffer);
            seam.ReadDefaultFramebuffer(0, 0, Size, Size, (IntPtr)destination);
        }
        return pixels;
    }

    private static unsafe int SolidTexture(VulkanDevice seam, byte r, byte g, byte b)
    {
        var pixels = new byte[Size * Size * 4];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = r;
            pixels[i + 1] = g;
            pixels[i + 2] = b;
            pixels[i + 3] = 255;
        }
        fixed (byte* data = pixels)
        {
            return seam.CreateTexture2D(Size, Size, EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba,
                (IntPtr)data, false);
        }
    }

    private static void BaseState(VulkanDevice seam)
    {
        seam.SetViewport(0, 0, Size, Size);
        seam.SetScissorEnabled(false);
        seam.SetDepthTest(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
    }

    // ------------------------------------------------------------ dynamic state

    [SkippableFact]
    public void RepeatedIdenticalStateRecordsDynamicStateOncePerRecording()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            int red = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, SolidFragment("1.0, 0.0, 0.0, 1.0"), "dyn-red");
            int a = CreateTarget(seam);
            int b = CreateTarget(seam);
            int all = VulkanStats.DynamicStateCommandsPerDraw;

            seam.BeginFrame();
            seam.BindFramebuffer(a);
            seam.ClearColor(0, 0f, 0f, 0f, 1f);
            seam.UseProgram(red);
            BaseState(seam);

            long before = device!.DynamicStateCommandsForTests;
            seam.DrawFullscreenTriangle();
            Assert.Equal(all, device.DynamicStateCommandsForTests - before);

            for (int i = 0; i < 9; i++) seam.DrawFullscreenTriangle();
            Assert.Equal(all, device.DynamicStateCommandsForTests - before);

            // One changed value, one command.
            seam.SetViewport(0, 0, Size / 2, Size);
            seam.DrawFullscreenTriangle();
            Assert.Equal(all + 1, device.DynamicStateCommandsForTests - before);

            // A new rendering scope in the same command buffer keeps the state.
            seam.BindFramebuffer(b);
            seam.DrawFullscreenTriangle();
            Assert.Equal(all + 1, device.DynamicStateCommandsForTests - before);

            // A readback submits the frame so far and continues in a new command
            // buffer, whose state starts undefined: everything again.
            byte[] mid = Read(seam, a);
            Assert.Equal(255, mid[0]);
            seam.BindFramebuffer(a);
            seam.DrawFullscreenTriangle();
            Assert.Equal(2 * all + 1, device.DynamicStateCommandsForTests - before);
            seam.Present();

            // A new frame is a new recording too.
            seam.BeginFrame();
            seam.BindFramebuffer(a);
            before = device.DynamicStateCommandsForTests;
            for (int i = 0; i < 5; i++) seam.DrawFullscreenTriangle();
            Assert.Equal(all, device.DynamicStateCommandsForTests - before);
            seam.Present();

            // Masking off is the old behaviour: every command on every draw.
            device.DynamicStateMaskingForTests = false;
            seam.BeginFrame();
            seam.BindFramebuffer(a);
            before = device.DynamicStateCommandsForTests;
            for (int i = 0; i < 5; i++) seam.DrawFullscreenTriangle();
            Assert.Equal(5 * all, device.DynamicStateCommandsForTests - before);
            seam.Present();

            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// The same state-changing sequence (viewports, scissor on and off, cull,
    /// program and target switches, two frames with Present between) renders the
    /// same pixels with masking on and off.
    /// </summary>
    [SkippableFact]
    public void MaskedDynamicStateDrawsThePixelsUnmaskedStateDraws()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            int red = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, SolidFragment("1.0, 0.0, 0.0, 1.0"), "dyn-red");
            int green = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, SolidFragment("0.0, 1.0, 0.0, 1.0"), "dyn-green");

            var targets = new int[4];
            for (int i = 0; i < targets.Length; i++) targets[i] = CreateTarget(seam);

            // Masked into targets 0 and 1, unmasked into 2 and 3.
            for (int run = 0; run < 2; run++)
            {
                device!.DynamicStateMaskingForTests = run == 0;
                int a = targets[run * 2];
                int b = targets[run * 2 + 1];
                for (int frame = 0; frame < 2; frame++)
                {
                    seam.BeginFrame();
                    Scene(seam, red, green, a, b, frame);
                    seam.Present();
                }
            }

            device!.DynamicStateMaskingForTests = true;
            seam.BeginFrame();
            byte[] maskedA = Read(seam, targets[0]);
            byte[] maskedB = Read(seam, targets[1]);
            byte[] plainA = Read(seam, targets[2]);
            byte[] plainB = Read(seam, targets[3]);
            seam.Present();

            Assert.Equal(plainA, maskedA);
            Assert.Equal(plainB, maskedB);

            int lit = 0;
            for (int i = 0; i < maskedA.Length; i += 4) if (maskedA[i] > 0 || maskedA[i + 1] > 0) lit++;
            Assert.InRange(lit, 1, Size * Size - 1); // the sequence actually drew something partial

            GpuTest.AssertClean(seam);
        }
    }

    private static void Scene(VulkanDevice seam, int red, int green, int a, int b, int frame)
    {
        seam.BindFramebuffer(a);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.UseProgram(red);
        BaseState(seam);
        seam.SetViewport(0, 0, Size / 2, Size);
        for (int i = 0; i < 3; i++) seam.DrawFullscreenTriangle();

        seam.BindFramebuffer(b);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.UseProgram(green);
        seam.SetViewport(Size / 2, 0, Size / 2, Size);
        seam.DrawFullscreenTriangle();
        seam.DrawFullscreenTriangle();

        seam.BindFramebuffer(a);
        seam.SetViewport(0, 0, Size, Size);
        seam.SetScissorEnabled(true);
        seam.SetScissor(Size / 2, Size / 2, Size / 2, Size / 2);
        seam.UseProgram(green);
        seam.DrawFullscreenTriangle();

        seam.SetScissorEnabled(false);
        seam.SetViewport(0, Size / 2, Size / 4, Size / 2);
        seam.UseProgram(frame == 0 ? red : green);
        seam.DrawFullscreenTriangle();

        seam.SetCullFace(true);
        seam.SetCullFaceMode(frame == 0);
        seam.SetViewport(Size / 4, 0, Size / 4, Size / 4);
        seam.DrawFullscreenTriangle();
        seam.SetCullFace(false);

        seam.BindFramebuffer(b);
        seam.SetViewport(0, 0, Size / 4, Size / 4);
        seam.DrawFullscreenTriangle();
    }

    // -------------------------------------------------------- descriptor arena

    [SkippableFact]
    public void TheDescriptorArenaResetsEveryFrameAndAgedSetsMoveToTheCache()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            const int window = 4;
            device!.ShortLivedFramesForTests = window;

            int program = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, SampleFragment, "arena-sample");
            int target = CreateTarget(seam);

            // Frames pass so the texture below is the only young resource.
            for (int i = 0; i < window + 1; i++)
            {
                seam.BeginFrame();
                seam.Present();
            }

            int texture = SolidTexture(seam, 40, 120, 200);
            int cachedBefore = device.CachedDescriptorSets;
            long[] allocationsBefore = { device.DescriptorArenaForTests(0).Allocations, device.DescriptorArenaForTests(1).Allocations };
            int[] poolsAfterFirstUse = { -1, -1 };

            for (int frame = 0; frame < window + 4; frame++)
            {
                seam.BeginFrame();
                int slot = device.CurrentSlotForTests;
                DescriptorArena arena = device.DescriptorArenaForTests(slot);
                Assert.Equal(0, arena.SetsThisFrame); // reset at the slot's frame start

                seam.BindFramebuffer(target);
                seam.ClearColor(0, 0f, 0f, 0f, 1f);
                seam.UseProgram(program);
                seam.SetSamplerUnit(program, "source", 0);
                seam.BindTexture(0, texture);
                BaseState(seam);
                seam.DrawFullscreenTriangle();
                seam.DrawFullscreenTriangle();

                // Whatever else the program's first draw cached (a set naming only
                // permanent resources) is counted from frame 0 on.
                if (frame == 0) cachedBefore = device.CachedDescriptorSets;

                if (frame < window - 1)
                {
                    // Young: one set in the arena, written once and shared by both draws.
                    Assert.Equal(1, arena.SetsThisFrame);
                    Assert.Equal(cachedBefore, device.CachedDescriptorSets);
                    if (poolsAfterFirstUse[slot] < 0) poolsAfterFirstUse[slot] = arena.PoolCount;
                    Assert.Equal(poolsAfterFirstUse[slot], arena.PoolCount);
                }
                else if (frame >= window)
                {
                    // Aged out: the long-lived cache holds it now, the arena nothing.
                    Assert.Equal(0, arena.SetsThisFrame);
                    Assert.Equal(cachedBefore + 1, device.CachedDescriptorSets);
                }

                if (frame == 0 || frame == window + 3)
                {
                    byte[] pixels = Read(seam, target);
                    int centre = (Size / 2 * Size + Size / 2) * 4;
                    Assert.Equal(40, pixels[centre]);
                    Assert.Equal(120, pixels[centre + 1]);
                    Assert.Equal(200, pixels[centre + 2]);
                }
                seam.Present();
            }

            // Per-frame allocation, not accumulation: each slot wrote its set once
            // per young frame and the pools never grew.
            long written = device.DescriptorArenaForTests(0).Allocations - allocationsBefore[0] +
                           device.DescriptorArenaForTests(1).Allocations - allocationsBefore[1];
            Assert.InRange(written, window - 1, window);

            GpuTest.AssertClean(seam);
        }
    }

    /// <summary>
    /// A texture deleted and replaced while its sets live in the arena: the next
    /// frame's reset drops them, the successor gets its own, and the old image is
    /// destroyed only after the frame that bound it (validation checks that).
    /// </summary>
    [SkippableFact]
    public void AnArenaSetNamingADeletedTextureNeverReachesALaterFrame()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            int program = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, SampleFragment, "arena-delete");
            int target = CreateTarget(seam);

            byte[] last = Array.Empty<byte>();
            for (int frame = 0; frame < 6; frame++)
            {
                int texture = SolidTexture(seam, (byte)(30 * frame), 90, 10);
                seam.BeginFrame();
                seam.BindFramebuffer(target);
                seam.UseProgram(program);
                seam.SetSamplerUnit(program, "source", 0);
                seam.BindTexture(0, texture);
                BaseState(seam);
                seam.DrawFullscreenTriangle();
                Assert.True(device!.DescriptorArenaForTests(device.CurrentSlotForTests).SetsThisFrame >= 1);
                seam.DeleteTexture(texture);
                if (frame == 5) last = Read(seam, target);
                seam.Present();
            }

            int centre = (Size / 2 * Size + Size / 2) * 4;
            Assert.Equal(150, last[centre]);
            Assert.Equal(90, last[centre + 1]);
            GpuTest.AssertClean(seam);
        }
    }

    // ------------------------------------------------------------ indirect ring

    /// <summary>Four quads side by side in one mesh, one quarter of the width each.</summary>
    private static MeshData Strips()
    {
        var xyz = new float[4 * 4 * 3];
        var indices = new int[4 * 6];
        for (int strip = 0; strip < 4; strip++)
        {
            float x0 = -1f + strip * 0.5f;
            float x1 = x0 + 0.5f;
            float[] corners = { x0, -1f, 0f, x1, -1f, 0f, x1, 1f, 0f, x0, 1f, 0f };
            Array.Copy(corners, 0, xyz, strip * 12, 12);
            int v = strip * 4;
            int[] quad = { v, v + 1, v + 2, v, v + 2, v + 3 };
            Array.Copy(quad, 0, indices, strip * 6, 6);
        }
        return new MeshData(16, 24)
        {
            xyz = xyz,
            VerticesCount = 16,
            Indices = indices,
            IndicesCount = 24,
        };
    }

    private static void DrawStrips(VulkanDevice seam, int target, int program, int mesh)
    {
        seam.BindFramebuffer(target);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.UseProgram(program);
        BaseState(seam);
        // One multi-draw per strip, each with its own indirect region: a region
        // overwritten or shared shows up as a missing strip.
        for (int strip = 0; strip < 4; strip++)
        {
            // Starts are GL's 64-bit byte offsets, two ints per group (low, high).
            seam.DrawMeshMulti(mesh, new[] { strip * 6 * sizeof(int), 0 }, new[] { 6 }, 1, false);
        }
    }

    [SkippableFact]
    public void AFrameOutgrowingItsSlotOverflowsThenTheSlotGrowsAtItsNextFrame()
    {
        Skip.IfNot(GpuTest.TryCreateDevice(_output, out VulkanDevice? device), "No usable Vulkan device.");
        using (device)
        {
            VulkanDevice seam = device!;
            // Two indirect commands per slot buffer to start with.
            device!.IndirectMinimumCapacityForTests = 40;

            int program = VulkanDeviceIntegrationTests.LinkProgram(seam, MeshVertex, SolidFragment("1.0, 1.0, 1.0, 1.0"), "indirect-strips");
            int mesh = seam.CreateMesh(Strips(), false);
            int first = CreateTarget(seam);
            int second = CreateTarget(seam);

            // Frame 1 (slot 0): two regions fit, two overflow.
            seam.BeginFrame();
            int slot0 = device.CurrentSlotForTests;
            DrawStrips(seam, first, program, mesh);
            Assert.Equal(2, device.IndirectOverflowsForTests);
            Assert.Equal(40UL, device.IndirectRingForTests.CapacityOf(slot0));
            seam.Present();

            // Frame 2 (slot 1): its buffer is created at the busiest frame's size.
            seam.BeginFrame();
            DrawStrips(seam, second, program, mesh);
            Assert.Equal(2, device.IndirectOverflowsForTests);
            seam.Present();

            // Frame 3 (slot 0 again): grown at the boundary, nothing overflows.
            seam.BeginFrame();
            Assert.Equal(slot0, device.CurrentSlotForTests);
            Assert.Equal(1, device.IndirectGrowthsForTests);
            Assert.True(device.IndirectRingForTests.CapacityOf(slot0) >= 80);
            DrawStrips(seam, second, program, mesh);
            Assert.Equal(2, device.IndirectOverflowsForTests);
            seam.Present();

            // No readback until now: the first target still holds frame 1's
            // overflowed draws, the second frame 3's.
            seam.BeginFrame();
            foreach (int target in new[] { first, second })
            {
                byte[] pixels = Read(seam, target);
                for (int i = 0; i < pixels.Length; i += 4)
                {
                    Assert.True(pixels[i] == 255, "target " + target + " pixel " + i / 4 + " is " + pixels[i]);
                }
            }
            seam.Present();

            seam.DeleteMesh(mesh);
            GpuTest.AssertClean(seam);
        }
    }
}
