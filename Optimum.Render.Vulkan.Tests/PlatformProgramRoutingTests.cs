using System;
using System.IO;
using System.Runtime.InteropServices;
using Optimum.Render.Vulkan.Platform;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Vulkan-native plan, Phase 1A step 3: ShaderProgramBase and UBO no longer talk to the
/// device or to GL; they call ScreenManager.Platform. These drive the lib's own classes
/// (the donor the Cecil patch transplants) through a VulkanClientPlatform installed as
/// the client's platform, and read the pixels back, so a virtual that stops reaching the
/// device shows up as a wrong colour rather than as a missing call.
/// </summary>
public class PlatformProgramRoutingTests
{
    private readonly ITestOutputHelper _output;

    public PlatformProgramRoutingTests(ITestOutputHelper output) => _output = output;

    private sealed class RoutedProgram : ShaderProgramBase
    {
        public override bool Compile() => true;
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

    /// <summary>The installed platform, the platform it replaced and the crash-marker directory.</summary>
    private sealed class Session : IDisposable
    {
        private readonly ClientPlatformAbstract? _previous;
        private readonly string _dataPath;

        public VulkanClientPlatform Platform { get; }
        public IOptimumGraphicsDevice Seam => OptimumRender.Device!;

        private Session(VulkanClientPlatform platform, ClientPlatformAbstract? previous, string dataPath)
        {
            Platform = platform;
            _previous = previous;
            _dataPath = dataPath;
        }

        public static Session? TryOpen(ITestOutputHelper output)
        {
            string dataPath = Path.Combine(Path.GetTempPath(), "optimum-routing-test-" + Guid.NewGuid().ToString("N"));
            var platform = new VulkanClientPlatform(null!)
            {
                DeviceFactory = GpuTest.NewDevice,
                CrashMarkerDataPath = dataPath,
            };
            if (!platform.InitializeGraphics(IntPtr.Zero, 0, 0, out string reason))
            {
                output.WriteLine("Vulkan unavailable: " + reason);
                platform.ShutdownGraphics();
                TryDelete(dataPath);
                return null;
            }

            var session = new Session(platform, ScreenManager.Platform, dataPath);
            ScreenManager.Platform = platform;
            return session;
        }

        public void Dispose()
        {
            ShaderProgramBase.CurrentShaderProgram = null;
            ScreenManager.Platform = _previous!;
            Platform.ShutdownGraphics();
            TryDelete(_dataPath);
        }

        private static void TryDelete(string path)
        {
            try
            {
                Directory.Delete(path, true);
            }
            catch (DirectoryNotFoundException)
            {
            }
        }
    }

    private static int ColourTarget(IOptimumGraphicsDevice seam, int size)
    {
        int texture = seam.CreateTexture2D(size, size,
            EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        int framebuffer = seam.CreateFramebuffer(size, size);
        seam.AttachTexture(framebuffer, EnumFramebufferAttachment.ColorAttachment0, texture, 0);
        seam.SetDrawBuffers(framebuffer, 0b1);
        return framebuffer;
    }

    private static void BeginDraw(IOptimumGraphicsDevice seam, int framebuffer, int size)
    {
        seam.BeginFrame();
        seam.BindFramebuffer(framebuffer);
        seam.ClearColor(0, 0f, 0f, 0f, 1f);
        seam.SetViewport(0, 0, size, size);
        seam.SetDepthTest(false);
        seam.SetCullFace(false);
        seam.SetBlend(false, EnumBlendMode.Standard);
    }

    private static unsafe byte[] ReadCentre(IOptimumGraphicsDevice seam, int framebuffer, int size, bool openFrame)
    {
        var pixels = new byte[size * size * 4];
        if (openFrame) seam.BeginFrame();
        seam.BindFramebuffer(framebuffer);
        fixed (byte* destination = pixels)
        {
            seam.ReadDefaultFramebuffer(0, 0, size, size, (IntPtr)destination);
        }
        if (openFrame) seam.Present();
        int centre = (size / 2 * size + size / 2) * 4;
        return new[] { pixels[centre], pixels[centre + 1], pixels[centre + 2], pixels[centre + 3] };
    }

    /// <summary>
    /// Use, the scalar/vector/integer-vector/matrix setters, Stop and Dispose, each
    /// called on the lib's ShaderProgramBase. Every uniform contributes to the colour,
    /// so any one of them failing to reach the device changes the pixel.
    /// </summary>
    [SkippableFact]
    public void UniformsSetOnAShaderProgramReachTheShaderThroughThePlatform()
    {
        using Session? session = Session.TryOpen(_output);
        Skip.If(session == null, "No usable Vulkan device.");
        IOptimumGraphicsDevice seam = session!.Seam;
        const int size = 16;

        var program = new RoutedProgram { PassName = "routed-uniforms" };
        program.ProgramId = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            uniform float red;
            uniform vec2 greenBlue;
            uniform int alphaOn;
            uniform ivec3 offsets;
            uniform mat4 transform;
            out vec4 outColor;
            void main(void)
            {
                vec4 moved = transform * vec4(0.0, 0.0, 0.0, 1.0);
                outColor = vec4(red,
                                greenBlue.x + float(offsets.y) / 255.0,
                                greenBlue.y + moved.x,
                                alphaOn == 1 ? 1.0 : 0.0);
            }
            """);
        foreach (string name in new[] { "red", "greenBlue", "alphaOn", "offsets", "transform" })
        {
            int location = seam.GetUniformLocation(program.ProgramId, name);
            Assert.True(location >= 0, name + " has no location");
            program.uniformLocations[name] = location;
        }

        int framebuffer = ColourTarget(seam, size);
        var transform = new float[16];
        transform[0] = transform[5] = transform[10] = transform[15] = 1f;
        transform[12] = 30f / 255f; // column-major translation x

        BeginDraw(seam, framebuffer, size);
        program.Use();
        Assert.Same(program, ShaderProgramBase.CurrentShaderProgram);
        program.Uniform("red", 60f / 255f);
        program.Uniform("greenBlue", new Vec2f(100f / 255f, 150f / 255f));
        program.Uniform("alphaOn", 1);
        program.Uniform("offsets", new Vec3i(0, 20, 0));
        program.UniformMatrix("transform", transform);
        seam.DrawFullscreenTriangle();
        program.Stop();
        Assert.Null(ShaderProgramBase.CurrentShaderProgram);
        seam.Present();

        byte[] pixel = ReadCentre(seam, framebuffer, size, openFrame: false);
        _output.WriteLine($"centre RGBA = {pixel[0]}, {pixel[1]}, {pixel[2]}, {pixel[3]}");
        Assert.Equal(new byte[] { 60, 120, 180, 255 }, pixel);

        program.Dispose();
        Assert.True(program.Disposed);
        GpuTest.AssertClean(seam);
    }

    /// <summary>
    /// CreateUBO, then the object-range UBO update in three consecutive frames, each bound
    /// by ShaderProgramBase.Use and unbound by Stop, with no readback between the frames;
    /// then Dispose through the platform.
    /// </summary>
    [SkippableFact]
    public void EveryUboUpdatePathReachesTheBlockThroughThePlatform()
    {
        using Session? session = Session.TryOpen(_output);
        Skip.If(session == null, "No usable Vulkan device.");
        IOptimumGraphicsDevice seam = session!.Seam;
        const int size = 16;

        var program = new RoutedProgram { PassName = "routed-ubo" };
        program.ProgramId = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            layout(std140) uniform Tint { vec4 tint; };
            out vec4 outColor;
            void main(void) { outColor = tint; }
            """);

        var ubo = Assert.IsType<UBO>(session.Platform.CreateUBO(program.ProgramId, 0, "Tint", sizeof(float) * 4));
        Assert.True(ubo.Handle > 0);
        Assert.Equal("Tint", ubo.BlockName);
        program.ubos["Tint"] = ubo;

        var colours = new[]
        {
            new byte[] { 25, 75, 125 },
            new byte[] { 210, 15, 45 },
            new byte[] { 90, 160, 230 },
        };
        var framebuffers = new int[colours.Length];
        for (int frame = 0; frame < colours.Length; frame++)
        {
            framebuffers[frame] = ColourTarget(seam, size);
            byte[] c = colours[frame];
            // The object-range overload is the one every caller uses (the entity
            // renderers' bone upload). The generic Update<T> overloads are not
            // driven here: vanilla pins through GCHandleProvider.Pointer, which is
            // GCHandle.ToIntPtr (the handle value, not the data address), so they
            // upload garbage on GL and on the device alike, before and after this move.
            ubo.Update(new[] { c[0] / 255f, c[1] / 255f, c[2] / 255f, 1f }, 0, sizeof(float) * 4);

            BeginDraw(seam, framebuffers[frame], size);
            program.Use();
            seam.DrawFullscreenTriangle();
            program.Stop();
            seam.Present();
        }

        for (int frame = 0; frame < colours.Length; frame++)
        {
            byte[] pixel = ReadCentre(seam, framebuffers[frame], size, openFrame: true);
            Assert.Equal(colours[frame][0], pixel[0]);
            Assert.Equal(colours[frame][1], pixel[1]);
            Assert.Equal(colours[frame][2], pixel[2]);
        }

        ubo.Dispose();
        program.Dispose();
        GpuTest.AssertClean(seam);
    }

    /// <summary>
    /// BindTexture2D with a custom sampler: the program aims the sampler at the unit
    /// through the platform (the device never reads uniformLocations for it), Stop clears
    /// the sampler override, and Dispose deletes sampler and program.
    /// </summary>
    [SkippableFact]
    public unsafe void ATextureBoundOnAShaderProgramIsSampledThroughThePlatform()
    {
        using Session? session = Session.TryOpen(_output);
        Skip.If(session == null, "No usable Vulkan device.");
        IOptimumGraphicsDevice seam = session!.Seam;
        const int size = 16;

        var program = new RoutedProgram { PassName = "routed-texture" };
        program.ProgramId = VulkanDeviceIntegrationTests.LinkProgram(seam, FullscreenVertex, """
            #version 330 core
            uniform sampler2D source;
            out vec4 outColor;
            void main(void) { outColor = texture(source, vec2(0.5, 0.5)); }
            """);
        program.SetCustomSampler("source", false);
        Assert.True(program.customSamplers["source"] > 0);

        var texel = new byte[] { 10, 200, 90, 255 };
        int texture;
        fixed (byte* data = texel)
        {
            texture = seam.CreateTexture2D(1, 1,
                EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, (IntPtr)data, false);
        }
        int framebuffer = ColourTarget(seam, size);

        BeginDraw(seam, framebuffer, size);
        program.Use();
        program.BindTexture2D("source", texture, 0);
        seam.DrawFullscreenTriangle();
        program.Stop();
        seam.Present();

        byte[] pixel = ReadCentre(seam, framebuffer, size, openFrame: false);
        _output.WriteLine($"centre RGBA = {pixel[0]}, {pixel[1]}, {pixel[2]}, {pixel[3]}");
        Assert.Equal(new byte[] { 10, 200, 90, 255 }, pixel);

        program.Dispose();
        GpuTest.AssertClean(seam);
    }
}
