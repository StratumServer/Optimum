using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Cairo;
using OpenTK.Graphics.OpenGL;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using OpenTK.Windowing.GraphicsLibraryFramework;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

/// <summary>
/// The Skia GPU (Ganesh) GUI route, run as shipped on a real OpenGL context. The headless GUI job provides
/// xvfb and llvmpipe and sets OPTIMUM_REQUIRE_GL_TESTS=1; the software renderer is allowed for these tests
/// only, through an internal test seam, because the route normally refuses software renderers.
/// </summary>
[Collection("OptimumConfig")]
public sealed class GuiSkiaGpuUploadGlTests
{
    private const int Width = 7;
    private const int Height = 5;
    private const BindingFlags HiddenStatic = BindingFlags.NonPublic | BindingFlags.Static;

    private sealed class Route : IDisposable
    {
        internal readonly Type Backend;
        internal readonly MethodInfo Upload;
        private readonly bool originalGpuEnabled = OptimumConfig.GuiGpuRenderEnabled;
        private readonly string originalBackend = OptimumConfig.GuiRenderBackend;

        internal Route()
        {
            string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            string libPath = PatchReader.FindRepositoryFile($"build/VintagestoryLib/bin/{configuration}/net10.0/VintagestoryLib.dll");
            Assembly lib = Assembly.LoadFrom(libPath);
            Assert.Equal(System.IO.Path.GetFullPath(libPath), System.IO.Path.GetFullPath(lib.Location));
            Backend = lib.GetType("Vintagestory.Client.NoObf.OptimumGuiGpuProbeBackend", throwOnError: true)!;
            Upload = Backend.GetMethod("TryCreateCandidateTexture")!;
            GLFWProvider.CheckForMainThread = false; // xunit runs tests off the main thread
        }

        internal static NativeWindow OpenWindow()
        {
            var window = new NativeWindow(new NativeWindowSettings
            {
                StartVisible = false,
                APIVersion = new Version(3, 3),
                Profile = ContextProfile.Core,
                Flags = ContextFlags.ForwardCompatible,
                ClientSize = new OpenTK.Mathematics.Vector2i(64, 64),
            });
            window.MakeCurrent();
            while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }
            return window;
        }

        internal void Static(string name, object value) => Backend.GetField(name, HiddenStatic)!.SetValue(null, value);

        /// <summary>Starts from a clean backend: no cached context, no probe result, software renderers allowed.</summary>
        internal void Reset()
        {
            Backend.GetMethod("AcknowledgeContextRecreated")!.Invoke(null, null);
            Backend.GetMethod("ResetRenderingDiagnostics")!.Invoke(null, null);
            Static("attempted", 0);
            Static("AllowSoftwareRendererForTest", true);
            Static("ForceGlErrorAfterCandidateForTest", false);
        }

        internal void UseGpu()
        {
            OptimumConfig.GuiGpuRenderEnabled = true;
            OptimumConfig.GuiRenderBackend = OptimumConfig.GuiRenderBackendSkiaGpu;
        }

        internal void UseCpu()
        {
            OptimumConfig.GuiGpuRenderEnabled = false;
            OptimumConfig.GuiRenderBackend = OptimumConfig.GuiRenderBackendSkiaCpu;
        }

        internal (bool ok, int texture, string reason) Run(ImageSurface surface)
        {
            object?[] arguments = [surface, true, Environment.CurrentManagedThreadId, 0, null, 0];
            bool ok = (bool)Upload.Invoke(null, arguments)!;
            return (ok, (int)arguments[3]!, (string?)arguments[4] ?? string.Empty);
        }

        public void Dispose()
        {
            Static("AllowSoftwareRendererForTest", false);
            Static("ForceGlErrorAfterCandidateForTest", false);
            Backend.GetMethod("AcknowledgeContextRecreated")!.Invoke(null, null);
            OptimumConfig.GuiGpuRenderEnabled = originalGpuEnabled;
            OptimumConfig.GuiRenderBackend = originalBackend;
        }
    }

    private static ImageSurface Record(int width = Width, int height = Height)
    {
        ImageSurface surface = ImageSurface.CreateForRecordedGui(Cairo.Format.Argb32, width, height);
        using var context = new Context(surface);
        context.Antialias = Antialias.None;
        context.SetSourceRGBA(1, 0.1, 0.1, 1);
        context.Rectangle(1, 1, 5, 2);
        context.Fill();
        return surface;
    }

    /// <summary>Uploads a 7x5 surface and checks the result is the GPU render, not a fallback.</summary>
    private static int UploadGpu(Route route, string context)
    {
        using ImageSurface surface = Record();
        var result = route.Run(surface);
        Assert.True(result.ok, $"{context}: the upload failed: {result.reason}");
        Assert.True(result.reason.Length == 0, $"{context}: expected the Ganesh render, got: {result.reason}");
        Assert.NotEqual(0, result.texture);
        return result.texture;
    }

    [GlContextFact]
    public void ZeroAreaSurface_IsPublishedWithoutDisablingTheGpuRoute()
    {
        using var route = new Route();
        using var window = Route.OpenWindow();
        route.Reset();
        route.UseGpu();
        var textures = new List<int>();
        Surface.ConfigureAutomaticRecording(true);
        try
        {
            // Vanilla's scrollbar handle is a 20x0 surface. Zero height cannot be a complete attachment.
            using ImageSurface empty = new ImageSurface(Cairo.Format.Argb32, 20, 0);
            Assert.Equal(SurfaceRecordingState.Recording, empty.RecordingState);
            var result = route.Run(empty);
            Assert.True(result.ok, "The empty surface was not published: " + result.reason);
            Assert.NotEqual(0, result.texture);
            textures.Add(result.texture);
            Assert.StartsWith("skia-cpu-fallback:gpu-preflight=empty-surface", result.reason, StringComparison.Ordinal);
            Assert.Equal(OptimumConfig.GuiRenderBackendSkiaGpu, OptimumConfig.GuiRenderBackend);
            Assert.True(OptimumConfig.GuiGpuRenderEnabled, "An empty surface must not disable the GPU route.");

            // The next real surface still goes through Ganesh.
            textures.Add(UploadGpu(route, "after the empty surface"));
        }
        finally
        {
            Surface.ConfigureAutomaticRecording(false);
            foreach (int texture in textures) GL.DeleteTexture(texture);
        }
    }

    [GlContextFact]
    public void GpuRoute_RestoresTheWholeCallerState_AndMatchesTheCpuPixels()
    {
        using var route = new Route();
        using var window = Route.OpenWindow();
        route.Reset();
        var textures = new List<int>();
        var objects = new HostileObjects();
        try
        {
            route.UseCpu();
            using (ImageSurface cpuSurface = Record())
            {
                var reference = route.Run(cpuSurface);
                Assert.True(reference.ok, reference.reason);
                textures.Add(reference.texture);
            }
            byte[] expected = ReadPixels(textures[0]);
            int band = (1 * Width + 3) * 4; // BGRA, premultiplied
            Assert.Equal(255, expected[band + 2]);
            Assert.Equal(255, expected[band + 3]);
            Assert.Equal(0, expected[3]);

            route.UseGpu();
            objects.Apply();
            int[] hostile = GlState.Read(objects);
            int texture = UploadGpu(route, "hostile caller state");
            textures.Add(texture);
            int[] after = GlState.Read(objects);
            Assert.True(hostile.AsSpan().SequenceEqual(after), GlState.Describe(hostile, after));
            Assert.Equal(OpenTK.Graphics.OpenGL.ErrorCode.NoError, GL.GetError());
            Assert.Equal(expected, ReadPixels(texture));
        }
        finally
        {
            objects.Release();
            foreach (int texture in textures) GL.DeleteTexture(texture);
        }
    }

    [GlContextFact]
    public void GlErrorAfterTheRender_FallsBackToTheCpuRouteAndPublishesATexture()
    {
        using var route = new Route();
        using var window = Route.OpenWindow();
        route.Reset();
        var textures = new List<int>();
        try
        {
            route.UseCpu();
            using (ImageSurface cpuSurface = Record())
            {
                var reference = route.Run(cpuSurface);
                Assert.True(reference.ok, reference.reason);
                textures.Add(reference.texture);
            }
            byte[] expected = ReadPixels(textures[0]);

            route.UseGpu();
            route.Static("ForceGlErrorAfterCandidateForTest", true);
            using ImageSurface surface = Record();
            var result = route.Run(surface);
            // The render already sealed the display list; the recovery must reopen it before the CPU route replays it.
            Assert.True(result.ok, "The CPU recovery did not publish a texture: " + result.reason);
            Assert.StartsWith("skia-cpu-fallback:ganesh=gl-error-after-candidate", result.reason, StringComparison.Ordinal);
            textures.Add(result.texture);
            Assert.Equal(expected, ReadPixels(result.texture));
            Assert.Equal(OpenTK.Graphics.OpenGL.ErrorCode.NoError, GL.GetError());
        }
        finally
        {
            foreach (int texture in textures) GL.DeleteTexture(texture);
        }
    }

    [GlContextFact]
    public void ANewContext_DoesNotReuseTheFramebufferNamesOfThePreviousOne()
    {
        using var route = new Route();
        using var first = Route.OpenWindow();
        route.Reset();
        route.UseGpu();
        int firstTexture = UploadGpu(route, "first context");
        // Framebuffer and vertex array names are per context: the name the first context generated for the
        // scratch framebuffer does not exist in a new one. A core profile context raises INVALID_OPERATION for it.
        using var second = Route.OpenWindow();
        int secondTexture = 0;
        try
        {
            secondTexture = UploadGpu(route, "second context");
            Assert.Equal(OpenTK.Graphics.OpenGL.ErrorCode.NoError, GL.GetError());
        }
        finally
        {
            if (secondTexture != 0) GL.DeleteTexture(secondTexture);
            first.MakeCurrent();
            GL.DeleteTexture(firstTexture);
        }
    }

    private static byte[] ReadPixels(int texture)
    {
        // Read with a tight pack layout, then put the caller's pack state and binding back.
        int[] names = [0x0D05, 0x0D02, 0x0D03, 0x0D04];
        int[] saved = new int[names.Length];
        GL.GetInteger(GetPName.TextureBinding2D, out int bound);
        GL.GetInteger((GetPName)0x88ED, out int packBuffer);
        for (int i = 0; i < names.Length; i++)
        {
            GL.GetInteger((GetPName)names[i], out saved[i]);
            GL.PixelStore((PixelStoreParameter)names[i], i == 0 ? 1 : 0);
        }
        GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
        GL.BindTexture(TextureTarget.Texture2D, texture);
        byte[] pixels = new byte[Width * Height * 4];
        GL.GetTexImage(TextureTarget.Texture2D, 0, OpenTK.Graphics.OpenGL.PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
        GL.BindTexture(TextureTarget.Texture2D, bound);
        GL.BindBuffer(BufferTarget.PixelPackBuffer, packBuffer);
        for (int i = 0; i < names.Length; i++) GL.PixelStore((PixelStoreParameter)names[i], saved[i]);
        return pixels;
    }

    /// <summary>GL objects the hostile caller state refers to.</summary>
    private sealed class HostileObjects
    {
        internal int ReadFramebuffer, DrawFramebuffer, VertexArray, ArrayBuffer, UnpackBuffer, UnitZero, UnitFive, Sampler;

        internal void Apply()
        {
            ReadFramebuffer = GL.GenFramebuffer();
            DrawFramebuffer = GL.GenFramebuffer();
            VertexArray = GL.GenVertexArray();
            ArrayBuffer = GL.GenBuffer();
            UnpackBuffer = GL.GenBuffer();
            UnitZero = GL.GenTexture();
            UnitFive = GL.GenTexture();
            Sampler = GL.GenSampler();
            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, ReadFramebuffer);
            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, DrawFramebuffer);
            GL.BindVertexArray(VertexArray);
            GL.BindBuffer(BufferTarget.ArrayBuffer, ArrayBuffer);
            GL.BufferData(BufferTarget.ArrayBuffer, 64, IntPtr.Zero, BufferUsageHint.StaticDraw);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, UnpackBuffer);
            GL.BufferData(BufferTarget.PixelUnpackBuffer, 4096, IntPtr.Zero, BufferUsageHint.StaticDraw);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, UnitZero);
            GL.ActiveTexture(TextureUnit.Texture0 + 5);
            GL.BindTexture(TextureTarget.Texture2D, UnitFive);
            GL.BindSampler(5, Sampler);
            GL.PixelStore((PixelStoreParameter)0x0D05, 8);
            GL.PixelStore((PixelStoreParameter)0x0D02, 37);
            GL.PixelStore((PixelStoreParameter)0x0CF5, 8);
            GL.PixelStore((PixelStoreParameter)0x0CF2, 3);
            GL.PixelStore((PixelStoreParameter)0x0CF3, 1);
            GL.PixelStore((PixelStoreParameter)0x0CF4, 2);
            GL.Viewport(7, 9, 37, 41);
            GL.Scissor(3, 4, 11, 13);
            GL.Enable(EnableCap.ScissorTest);
            GL.Enable(EnableCap.Blend);
            GL.BlendFuncSeparate(BlendingFactorSrc.SrcColor, BlendingFactorDest.DstColor, BlendingFactorSrc.One, BlendingFactorDest.Zero);
            GL.BlendEquationSeparate(BlendEquationMode.FuncReverseSubtract, BlendEquationMode.FuncAdd);
            GL.Enable(EnableCap.DepthTest);
            GL.DepthMask(false);
            GL.Enable(EnableCap.StencilTest);
            GL.StencilFunc(StencilFunction.Notequal, 3, 0x0F);
            GL.StencilOp(StencilOp.Incr, StencilOp.Decr, StencilOp.Invert);
            GL.StencilMask(0x3C);
            GL.ClearStencil(5);
            GL.Enable(EnableCap.CullFace);
            GL.FrontFace(FrontFaceDirection.Cw);
            GL.ColorMask(true, false, true, false);
            GL.ClearColor(0.25f, 0.5f, 0.75f, 1f);
            // The state Ganesh's context reset changes and the engine can depend on.
            GL.Enable((EnableCap)0x8037);  // POLYGON_OFFSET_FILL
            GL.Disable((EnableCap)0x809D); // MULTISAMPLE
            GL.Disable((EnableCap)0x8642); // VERTEX_PROGRAM_POINT_SIZE
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Line);
        }

        internal void Release()
        {
            GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.BindVertexArray(0);
            GL.BindBuffer(BufferTarget.ArrayBuffer, 0);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, 0);
            GL.ActiveTexture(TextureUnit.Texture0 + 5);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            GL.BindSampler(5, 0);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, 0);
            if (ReadFramebuffer != 0) GL.DeleteFramebuffer(ReadFramebuffer);
            if (DrawFramebuffer != 0) GL.DeleteFramebuffer(DrawFramebuffer);
            if (VertexArray != 0) GL.DeleteVertexArray(VertexArray);
            if (ArrayBuffer != 0) GL.DeleteBuffer(ArrayBuffer);
            if (UnpackBuffer != 0) GL.DeleteBuffer(UnpackBuffer);
            if (UnitZero != 0) GL.DeleteTexture(UnitZero);
            if (UnitFive != 0) GL.DeleteTexture(UnitFive);
            if (Sampler != 0) GL.DeleteSampler(Sampler);
        }
    }

    /// <summary>
    /// A wide reading of the context state: everything the full snapshot covers, plus the state Ganesh's
    /// context reset changes. Values are flattened to integers (floats by their bit pattern).
    /// </summary>
    private static class GlState
    {
        private static readonly int[] Integers =
        [
            0x8CAA, 0x8CA6, 0x8CA7, 0x84E0, 0x8B8D, 0x85B5, 0x8894, 0x88ED, 0x88EF,
            0x0D05, 0x0CF5, 0x0D02, 0x0D03, 0x0D04, 0x0CF2, 0x0CF3, 0x0CF4,
            0x0B92, 0x0B97, 0x0B93, 0x0B98, 0x0B94, 0x0B95, 0x0B96, 0x8800, 0x8CA3, 0x8CA4, 0x8CA5, 0x8801, 0x8802, 0x8803,
            0x80C9, 0x80C8, 0x80CB, 0x80CA, 0x8009, 0x883D, 0x0B46, 0x0B91,
        ];
        private static readonly int[] Enables = [0x0C11, 0x0BE2, 0x0B71, 0x0B90, 0x0B44, 0x8DB9, 0x8C89, 0x8037, 0x0BD0, 0x809D, 0x8642];

        internal static int[] Read(HostileObjects objects)
        {
            var values = new List<int>();
            foreach (int name in Integers) { GL.GetInteger((GetPName)name, out int value); values.Add(value); }
            int[] pair = new int[4];
            GL.GetInteger(GetPName.Viewport, pair); values.AddRange(pair);
            GL.GetInteger(GetPName.ScissorBox, pair); values.AddRange(pair);
            int[] polygonMode = new int[2];
            GL.GetInteger((GetPName)0x0B40, polygonMode); values.AddRange(polygonMode);
            bool[] colorMask = new bool[4];
            GL.GetBoolean((GetPName)0x0C23, colorMask);
            foreach (bool value in colorMask) values.Add(value ? 1 : 0);
            values.Add(GL.GetBoolean((GetPName)0x0B72) ? 1 : 0);
            float[] clearColor = new float[4];
            GL.GetFloat((GetPName)0x0C22, clearColor);
            foreach (float value in clearColor) values.Add(BitConverter.SingleToInt32Bits(value));
            GL.GetFloat((GetPName)0x0B21, out float lineWidth);
            values.Add(BitConverter.SingleToInt32Bits(lineWidth));
            foreach (int cap in Enables) values.Add(GL.IsEnabled((EnableCap)cap) ? 1 : 0);
            GL.GetInteger(GetPName.ActiveTexture, out int active);
            foreach (int unit in new[] { 0, 1, 5 })
            {
                GL.ActiveTexture(TextureUnit.Texture0 + unit);
                GL.GetInteger(GetPName.TextureBinding2D, out int texture);
                GL.GetInteger((GetPName)0x8919, out int sampler);
                values.Add(texture);
                values.Add(sampler);
            }
            GL.ActiveTexture((TextureUnit)active);
            return values.ToArray();
        }

        internal static string Describe(int[] before, int[] after)
        {
            var differences = new List<string>();
            for (int i = 0; i < Math.Min(before.Length, after.Length); i++)
                if (before[i] != after[i]) differences.Add($"[{i}] {before[i]} -> {after[i]}");
            return "The caller GL state changed at index: " + string.Join(", ", differences);
        }
    }
}
