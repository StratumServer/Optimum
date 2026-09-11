using System;
using System.IO;
using System.Reflection;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Vulkan-native plan, Phase 1A step 4: ClientPlatformWindows keeps only the GL path, and
/// VulkanClientPlatform overrides every graphics member with device calls. These drive the
/// moved overrides through the platform with no GL context: an override that is missing
/// falls into a GL call and throws, one that reaches the wrong device call changes the pixels.
/// </summary>
public class PlatformDeviceRoutingTests
{
    private readonly ITestOutputHelper _output;

    public PlatformDeviceRoutingTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// The runtime self-check (VerifyHost) has to cover every override whose base member
    /// only exists in the patched lib - an injected ClientPlatformAbstract virtual or a
    /// member virtualized in place on ClientPlatformWindows - or an unpatched lib would
    /// bypass it mid-frame instead of failing the install.
    /// </summary>
    [Fact]
    public void EveryOverrideOfAPatchedVirtualIsInTheSelfCheck()
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        int checkedOverrides = 0;
        foreach (MethodInfo method in typeof(VulkanClientPlatform).GetMethods(flags))
        {
            MethodInfo definition = method.GetBaseDefinition();
            if (definition == method || method.IsSpecialName) continue;
            Type owner = definition.DeclaringType!;
            bool onAbstract = owner == typeof(ClientPlatformAbstract);
            if (!(onAbstract && !definition.IsAbstract) && owner != typeof(ClientPlatformWindows)) continue;

            ParameterInfo[] parameters = method.GetParameters();
            bool listed = false;
            foreach (VulkanClientPlatform.ExpectedVirtual expected in VulkanClientPlatform.ExpectedVirtuals)
            {
                if (expected.OnAbstract != onAbstract || expected.Name != method.Name || expected.ParameterTypeNames.Length != parameters.Length) continue;
                bool same = true;
                for (int i = 0; i < parameters.Length && same; i++)
                    same = expected.ParameterTypeNames[i] == parameters[i].ParameterType.Name;
                listed |= same;
            }
            Assert.True(listed, owner.Name + "." + method.Name + " is overridden but not in VulkanClientPlatform.ExpectedVirtuals");
            checkedOverrides++;
        }
        _output.WriteLine("patched virtuals overridden: " + checkedOverrides);
        Assert.True(checkedOverrides >= 40);
    }

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
    /// Frame 1: CreateFramebuffer, the CurrentFrameBuffer setter (BindCurrentFrameBuffer),
    /// the state setters and ClearFrameBuffer (ClearBoundFrameBuffer) through the platform.
    /// Frame 2: a program drawn with RenderFullscreenTriangle under GlScissor/GlScissorFlag
    /// over the left half only. Frame 3 reads back: left half the draw colour, right half the
    /// clear colour. BeginFrame/EndFrame are the platform's bracket; DisposeFrameBuffer and
    /// GLDeleteTexture release everything, and validation (sync, best) stays clean.
    /// </summary>
    [SkippableFact]
    public unsafe void FramebufferClearScissorAndDrawReachTheDeviceThroughThePlatform()
    {
        string dataPath = Path.Combine(Path.GetTempPath(), "optimum-device-routing-test-" + Guid.NewGuid().ToString("N"));
        var platform = new VulkanClientPlatform(null!)
        {
            DeviceFactory = GpuTest.NewDevice,
            CrashMarkerDataPath = dataPath,
        };
        try
        {
            bool installed = platform.InitializeGraphics(IntPtr.Zero, 0, 0, out string reason);
            if (!installed) _output.WriteLine("Vulkan unavailable: " + reason);
            Skip.IfNot(installed, "No usable Vulkan device.");
            IOptimumGraphicsDevice seam = OptimumRender.Device!;
            Assert.Same(seam, platform.GraphicsDevice);
            const int size = 16;

            var attrs = new FramebufferAttrs("routed", size, size)
            {
                Attachments = new[]
                {
                    new FramebufferAttrsAttachment
                    {
                        AttachmentType = EnumFramebufferAttachment.ColorAttachment0,
                        Texture = new RawTexture
                        {
                            Width = size,
                            Height = size,
                            PixelInternalFormat = EnumTextureInternalFormat.Rgba8,
                            PixelFormat = EnumTexturePixelFormat.Rgba,
                            MinFilter = EnumTextureFilter.Nearest,
                            MagFilter = EnumTextureFilter.Nearest,
                        },
                    },
                },
            };
            int program = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
                #version 330 core
                out vec4 outColor;
                void main(void) { outColor = vec4(200.0 / 255.0, 40.0 / 255.0, 90.0 / 255.0, 1.0); }
                """, "routed-draw");

            platform.BeginFrame();
            FrameBufferRef target = platform.CreateFramebuffer(attrs);
            Assert.True(target.FboId > 0);
            Assert.Single(target.ColorTextureIds);
            platform.CurrentFrameBuffer = target;
            Assert.Same(target, platform.CurrentFrameBuffer);
            platform.GlDisableDepthTest();
            platform.GlDisableCullFace();
            platform.GlToggleBlend(false);
            platform.ClearFrameBuffer(target, new[] { 20f / 255f, 140f / 255f, 220f / 255f, 1f }, clearDepthBuffer: false);
            platform.EndFrame();

            platform.BeginFrame();
            platform.CurrentFrameBuffer = target;
            platform.GlDisableDepthTest();
            platform.GlDisableCullFace();
            platform.GlToggleBlend(false);
            platform.UseShaderProgram(program);
            platform.GlScissor(0, 0, size / 2, size);
            platform.GlScissorFlag(true);
            Assert.True(platform.GlScissorFlagEnabled);
            platform.RenderFullscreenTriangle(null!);
            platform.GlScissorFlag(false);
            Assert.False(platform.GlScissorFlagEnabled);
            platform.UseShaderProgram(0);
            platform.EndFrame();

            var pixels = new byte[size * size * 4];
            platform.BeginFrame();
            platform.CurrentFrameBuffer = target;
            fixed (byte* destination = pixels)
            {
                seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
            }
            platform.EndFrame();

            int row = size / 2 * size;
            int left = (row + 2) * 4;
            int right = (row + size - 3) * 4;
            _output.WriteLine($"left RGBA = {pixels[left]}, {pixels[left + 1]}, {pixels[left + 2]}, {pixels[left + 3]}");
            _output.WriteLine($"right RGBA = {pixels[right]}, {pixels[right + 1]}, {pixels[right + 2]}, {pixels[right + 3]}");
            Assert.Equal(new byte[] { 200, 40, 90, 255 }, pixels[left..(left + 4)]);
            Assert.Equal(new byte[] { 20, 140, 220, 255 }, pixels[right..(right + 4)]);

            platform.DisposeFrameBuffer(target);
            // Linked on the device directly above, so freed there too.
            seam.DeleteProgram(program);
            GpuTest.AssertClean(seam);
        }
        finally
        {
            platform.ShutdownGraphics();
            try
            {
                Directory.Delete(dataPath, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }
}
