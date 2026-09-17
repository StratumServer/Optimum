using System;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Optimum.Render.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Xunit;
using Xunit.Abstractions;

namespace Optimum.Render.Vulkan.Tests;

/// <summary>
/// Exercises the presentation path against a real window.
///
/// This is the one part of the backend that cannot be tested headlessly: a
/// swapchain needs a surface, and a surface needs a window. The window is created
/// with <c>ClientApi.NoApi</c>, which is exactly the change the client needs -
/// GLFW must not create an OpenGL context alongside the Vulkan surface.
///
/// Skips where there is no display or no Vulkan-capable window system, so a
/// headless CI machine reports these as skipped rather than failing.
/// </summary>
public class SwapchainTests
{
    private readonly ITestOutputHelper _output;

    public SwapchainTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// Creates a hidden window with no graphics API attached, the way the
    /// patched client will.
    /// </summary>
    internal static unsafe bool TryCreateWindow(
        ITestOutputHelper output, int width, int height, out Window* window)
    {
        window = null;
        try
        {
            if (!GLFW.Init())
            {
                output.WriteLine("GLFW could not initialise; no display?");
                return false;
            }

            if (!GLFW.VulkanSupported())
            {
                output.WriteLine("GLFW reports no Vulkan support on this window system.");
                return false;
            }

            GLFW.WindowHint(WindowHintClientApi.ClientApi, ClientApi.NoApi);
            GLFW.WindowHint(WindowHintBool.Visible, false);

            window = GLFW.CreateWindow(width, height, "Optimum swapchain test", null, null);
            if (window == null)
            {
                output.WriteLine("GLFW could not create a window.");
                return false;
            }
            return true;
        }
        catch (Exception error)
        {
            output.WriteLine("Windowing unavailable: " + error.Message);
            return false;
        }
    }

    [SkippableFact]
    public unsafe void ADeviceComesUpAgainstARealWindowAndPresentsFrames()
    {
        const int width = 320;
        const int height = 240;

        Skip.IfNot(TryCreateWindow(_output, width, height, out Window* window), "No usable window system.");

        try
        {
            var device = GpuTest.NewDevice();
            if (!device.Initialize((IntPtr)window, width, height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
                return;
            }

            using (device)
            {
                VulkanDevice seam = device;
                _output.WriteLine($"presenting on {seam.RendererString}");

                int programId = LinkFullscreenProgram(seam);

                // Several frames, so the ring rotates and the swapchain cycles
                // through more than one image.
                for (int frame = 0; frame < 8; frame++)
                {
                    seam.BeginFrame();
                    seam.BindDefaultFramebuffer();
                    seam.ClearColor(0, 0.1f, 0.2f, 0.3f, 1f);

                    seam.UseProgram(programId);
                    seam.SetViewport(0, 0, width, height);
                    seam.SetDepthTest(false);
                    seam.SetCullFace(false);
                    seam.DrawFullscreenTriangle();

                    seam.Present();
                }

                AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    /// <summary>
    /// A resize has to rebuild both the swapchain and the offscreen target the
    /// client renders into, and keep presenting afterwards.
    /// </summary>
    [SkippableFact]
    public unsafe void ResizingRebuildsTheChainAndKeepsPresenting()
    {
        const int width = 256;
        const int height = 192;

        Skip.IfNot(TryCreateWindow(_output, width, height, out Window* window), "No usable window system.");

        try
        {
            var device = GpuTest.NewDevice();
            if (!device.Initialize((IntPtr)window, width, height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
                return;
            }

            using (device)
            {
                VulkanDevice seam = device;
                int programId = LinkFullscreenProgram(seam);

                void RenderFrames(int count, int w, int h)
                {
                    for (int frame = 0; frame < count; frame++)
                    {
                        seam.BeginFrame();
                        seam.BindDefaultFramebuffer();
                        seam.ClearColor(0, 0.2f, 0.4f, 0.6f, 1f);
                        seam.UseProgram(programId);
                        seam.SetViewport(0, 0, w, h);
                        seam.DrawFullscreenTriangle();
                        seam.Present();
                    }
                }

                RenderFrames(4, width, height);

                seam.Resize(width * 2, height * 2);
                RenderFrames(4, width * 2, height * 2);

                seam.Resize(width, height);
                RenderFrames(4, width, height);

                AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    /// <summary>
    /// Toggling vsync swaps the present mode, which means rebuilding the chain
    /// while frames are in flight.
    /// </summary>
    [SkippableFact]
    public unsafe void TogglingVsyncRebuildsTheChainCleanly()
    {
        const int width = 256;
        const int height = 192;

        Skip.IfNot(TryCreateWindow(_output, width, height, out Window* window), "No usable window system.");

        try
        {
            var device = GpuTest.NewDevice();
            if (!device.Initialize((IntPtr)window, width, height, out string failureReason))
            {
                device.Dispose();
                Skip.If(true, "Vulkan presentation unavailable: " + failureReason);
                return;
            }

            using (device)
            {
                VulkanDevice seam = device;
                int programId = LinkFullscreenProgram(seam);

                foreach (bool vsync in new[] { false, true, false })
                {
                    seam.SetVSync(vsync);
                    for (int frame = 0; frame < 3; frame++)
                    {
                        seam.BeginFrame();
                        seam.BindDefaultFramebuffer();
                        seam.ClearColor(0, 0f, 0f, 0f, 1f);
                        seam.UseProgram(programId);
                        seam.SetViewport(0, 0, width, height);
                        seam.DrawFullscreenTriangle();
                        seam.Present();
                    }
                }

                AssertClean(seam);
            }
        }
        finally
        {
            GLFW.DestroyWindow(window);
            GLFW.Terminate();
        }
    }

    private sealed class TestShader : IShader
    {
        public EnumShaderType Type { get; set; }
        public string Code { get; set; } = "";
        public string PrefixCode { get; set; } = "";
        public bool Compile() => true;
    }

    internal static int LinkFullscreenProgram(VulkanDevice device)
    {
        var vertex = new TestShader
        {
            Type = EnumShaderType.VertexShader,
            Code = """
                #version 330 core
                out vec2 uv;
                void main(void)
                {
                    float x = -1.0 + float((gl_VertexID & 1) << 2);
                    float y = -1.0 + float((gl_VertexID & 2) << 1);
                    gl_Position = vec4(x, y, 0.0, 1.0);
                    uv = vec2((x + 1.0) * 0.5, (y + 1.0) * 0.5);
                }
                """,
        };
        var fragment = new TestShader
        {
            Type = EnumShaderType.FragmentShader,
            Code = """
                #version 330 core
                in vec2 uv;
                out vec4 outColor;
                void main(void) { outColor = vec4(uv, 0.5, 1.0); }
                """,
        };

        Assert.True(device.CompileShader(vertex));
        Assert.True(device.CompileShader(fragment));

        var program = new SeamProgram { VertexShader = vertex, FragmentShader = fragment };
        int programId = device.LinkProgram(program);
        Assert.True(programId > 0, device.GetError() ?? "link failed");
        return programId;
    }

    /// <summary>Minimal IShaderProgram; the device only reads the stage properties.</summary>
    private sealed class SeamProgram : IShaderProgram
    {
        public int ProgramId { get; set; }
        public string AssetDomain { get; set; } = "game";
        public int PassId => 0;
        public string PassName => "swapchain-test";
        public bool ClampTexturesToEdge { get; set; }
        public IShader VertexShader { get; set; } = null!;
        public IShader FragmentShader { get; set; } = null!;
        public IShader GeometryShader { get; set; } = null!;
        public bool Oit { get; set; } = true;
        public bool Disposed => false;
        public bool LoadError => false;
        public Vintagestory.API.Datastructures.OrderedDictionary<string, UBORef> UBOs { get; } = new();

        public void Use() { }
        public void Stop() { }
        public bool Compile() => true;
        public void Dispose() { }
        public void Uniform(string uniformName, float value) { }
        public void Uniform(string uniformName, int value) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec2f value) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec2i value) { }
        public void Uniform(string uniformName, float valueX, float valueY) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec3f value) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ) { }
        public void Uniform(string uniformName, float valueX, float valueY, float valueZ, float valueW) { }
        public void Uniform(string uniformName, Vintagestory.API.MathTools.Vec4f value) { }
        public void Uniforms4(string uniformName, int count, float[] values) { }
        public void UniformMatrix(string uniformName, float[] matrix) { }
        public void BindTexture2D(string samplerName, int textureId, int textureNumber) { }
        public void BindTextureCube(string samplerName, int textureId, int textureNumber) { }
        public void UniformMatrices(string uniformName, int count, float[] matrix) { }
        public void UniformMatrices4x3(string uniformName, int count, float[] matrix) { }
        public bool HasUniform(string uniformName) => false;
    }

    private static void AssertClean(VulkanDevice device) => GpuTest.AssertClean(device);
}
