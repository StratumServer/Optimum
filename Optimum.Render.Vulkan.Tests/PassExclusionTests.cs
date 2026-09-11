using System;
using Optimum.Render.Vulkan.Graph;
using Vintagestory.API.Client;
using Xunit;
using Xunit.Abstractions;
using static Optimum.Render.Vulkan.Tests.GpuTest;

namespace Optimum.Render.Vulkan.Tests;

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

        int constant = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            layout(location = 0) out vec4 outColor;
            void main(void) { outColor = vec4(1.0, 0.0, 0.0, 1.0); }
            """, "px-constant");
        int copy = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
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
