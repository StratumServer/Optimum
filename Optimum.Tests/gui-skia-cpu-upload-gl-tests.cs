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
/// Runs a test on a real OpenGL context. The headless GUI job provides one (xvfb plus Mesa llvmpipe) and sets
/// OPTIMUM_REQUIRE_GL_TESTS=1, which turns an unavailable context into a failure instead of a skip. Everywhere
/// else (no display, Windows, macOS) the test reports a visible skip with its reason.
/// </summary>
public sealed class GlContextFactAttribute : FactAttribute
{
    public GlContextFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("OPTIMUM_REQUIRE_GL_TESTS") == "1") return;
        if (!OperatingSystem.IsLinux())
            Skip = "The OpenGL context tests run in the Linux xvfb lane (OPTIMUM_REQUIRE_GL_TESTS=1).";
        else if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            Skip = "No X display is available for an OpenGL context (OPTIMUM_REQUIRE_GL_TESTS=1 makes this a failure).";
    }
}

/// <summary>
/// The Skia CPU GUI upload reads and restores only the six values it can change instead of the full Ganesh GL
/// snapshot. Nothing else may leak into the engine, and the pixels must not depend on the caller's unpack
/// state. This drives the shipped upload on a real context under a hostile caller state and compares both.
/// </summary>
[Collection("OptimumConfig")]
public sealed class GuiSkiaCpuUploadGlStateTests
{
    private const int PixelUnpackBufferBinding = 0x88EF;
    private const int UnpackAlignment = 0x0CF5;
    private const int UnpackRowLength = 0x0CF2;
    private const int UnpackSkipRows = 0x0CF3;
    private const int UnpackSkipPixels = 0x0CF4;
    private const int PackAlignment = 0x0D05;
    private const int PackRowLength = 0x0D02;
    private const int PackSkipRows = 0x0D03;
    private const int PackSkipPixels = 0x0D04;
    private const int PixelPackBufferBinding = 0x88ED;

    // Seven pixels per row make a leaked unpack alignment of 8 read every row at the wrong stride
    // (28 bytes of pixels, 32 with alignment 8).
    private const int Width = 7;
    private const int Height = 5;

    [GlContextFact]
    public void CpuUpload_RestoresCallerUnpackState_AndPublishesIdenticalPixels()
    {
        string configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        string libPath = PatchReader.FindRepositoryFile($"build/VintagestoryLib/bin/{configuration}/net10.0/VintagestoryLib.dll");
        Assembly lib = Assembly.LoadFrom(libPath);
        Assert.Equal(System.IO.Path.GetFullPath(libPath), System.IO.Path.GetFullPath(lib.Location));
        Type backend = lib.GetType("Vintagestory.Client.NoObf.OptimumGuiGpuProbeBackend", throwOnError: true)!;
        MethodInfo upload = backend.GetMethod("TryCreateCandidateTexture")!;

        bool originalGpuEnabled = OptimumConfig.GuiGpuRenderEnabled;
        string originalBackend = OptimumConfig.GuiRenderBackend;
        GLFWProvider.CheckForMainThread = false; // xunit runs tests off the main thread
        using var window = new NativeWindow(new NativeWindowSettings
        {
            StartVisible = false,
            APIVersion = new Version(3, 3),
            Profile = ContextProfile.Core,
            Flags = ContextFlags.ForwardCompatible,
            ClientSize = new OpenTK.Mathematics.Vector2i(64, 64),
        });
        window.MakeCurrent();
        var textures = new List<int>();
        int unitZeroTexture = 0, unitFiveTexture = 0, unpackBuffer = 0;
        try
        {
            // The configured CPU backend reaches the upload without any test hook.
            OptimumConfig.GuiGpuRenderEnabled = false;
            OptimumConfig.GuiRenderBackend = OptimumConfig.GuiRenderBackendSkiaCpu;
            while (GL.GetError() != OpenTK.Graphics.OpenGL.ErrorCode.NoError) { }

            // Reference upload under the caller's own clean state.
            int reference = Upload(upload);
            textures.Add(reference);
            byte[] expected = ReadPixels(reference);
            int band = (1 * Width + 3) * 4; // BGRA, premultiplied
            Assert.Equal(255, expected[band + 2]);
            Assert.InRange((int)expected[band], 25, 27);
            Assert.Equal(255, expected[band + 3]);
            Assert.Equal(0, expected[3]);

            // Hostile caller: unit 5 active with its own texture, an unpack buffer bound, and every unpack
            // parameter away from its default. Unbinding the buffer and resetting the parameters is what the
            // upload needs; handing them back is what the engine needs.
            unitZeroTexture = GL.GenTexture();
            unitFiveTexture = GL.GenTexture();
            unpackBuffer = GL.GenBuffer();
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, unitZeroTexture);
            GL.ActiveTexture(TextureUnit.Texture0 + 5);
            GL.BindTexture(TextureTarget.Texture2D, unitFiveTexture);
            GL.BindBuffer(BufferTarget.PixelUnpackBuffer, unpackBuffer);
            GL.BufferData(BufferTarget.PixelUnpackBuffer, 4096, IntPtr.Zero, BufferUsageHint.StaticDraw);
            GL.PixelStore((PixelStoreParameter)UnpackAlignment, 8);
            GL.PixelStore((PixelStoreParameter)UnpackRowLength, 3);
            GL.PixelStore((PixelStoreParameter)UnpackSkipRows, 1);
            GL.PixelStore((PixelStoreParameter)UnpackSkipPixels, 2);
            int[] hostile = CallerState(unitZeroTexture, unitFiveTexture);
            Assert.Equal((int)TextureUnit.Texture0 + 5, hostile[0]);
            Assert.Equal(unpackBuffer, hostile[2]);
            Assert.Equal(8, hostile[3]);

            int texture = Upload(upload);
            textures.Add(texture);
            Assert.Equal(hostile, CallerState(unitZeroTexture, unitFiveTexture));
            Assert.Equal(OpenTK.Graphics.OpenGL.ErrorCode.NoError, GL.GetError());
            Assert.Equal(expected, ReadPixels(texture));
        }
        finally
        {
            OptimumConfig.GuiGpuRenderEnabled = originalGpuEnabled;
            OptimumConfig.GuiRenderBackend = originalBackend;
            foreach (int texture in textures) GL.DeleteTexture(texture);
            if (unitZeroTexture != 0) GL.DeleteTexture(unitZeroTexture);
            if (unitFiveTexture != 0) GL.DeleteTexture(unitFiveTexture);
            if (unpackBuffer != 0) GL.DeleteBuffer(unpackBuffer);
        }
    }

    private static int Upload(MethodInfo upload)
    {
        using ImageSurface surface = ImageSurface.CreateForRecordedGui(Cairo.Format.Argb32, Width, Height);
        using (var context = new Context(surface))
        {
            context.Antialias = Antialias.None;
            context.SetSourceRGBA(1, 0.1, 0.1, 1);
            context.Rectangle(1, 1, 5, 2);
            context.Fill();
        }
        object?[] arguments = [surface, true, Environment.CurrentManagedThreadId, 0, null, 0];
        bool uploaded = (bool)upload.Invoke(null, arguments)!;
        string reason = (string?)arguments[4] ?? string.Empty;
        Assert.True(uploaded, "The Skia CPU upload did not publish a texture: " + reason);
        Assert.StartsWith("skia-cpu-fallback:", reason, StringComparison.Ordinal);
        int texture = (int)arguments[3]!;
        Assert.NotEqual(0, texture);
        return texture;
    }

    /// <summary>
    /// Active unit, the binding on the active unit, unpack buffer, the four unpack parameters, and the texture
    /// bindings of units 0 and 5. Reading other units changes the active unit, so it is put back afterwards.
    /// </summary>
    private static int[] CallerState(int unitZeroTexture, int unitFiveTexture)
    {
        GL.GetInteger(GetPName.ActiveTexture, out int active);
        GL.GetInteger(GetPName.TextureBinding2D, out int activeBinding);
        GL.GetInteger((GetPName)PixelUnpackBufferBinding, out int buffer);
        GL.GetInteger((GetPName)UnpackAlignment, out int alignment);
        GL.GetInteger((GetPName)UnpackRowLength, out int rowLength);
        GL.GetInteger((GetPName)UnpackSkipRows, out int skipRows);
        GL.GetInteger((GetPName)UnpackSkipPixels, out int skipPixels);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.GetInteger(GetPName.TextureBinding2D, out int unitZero);
        GL.ActiveTexture(TextureUnit.Texture0 + 5);
        GL.GetInteger(GetPName.TextureBinding2D, out int unitFive);
        GL.ActiveTexture((TextureUnit)active);
        Assert.Equal(unitZeroTexture, unitZero);
        Assert.Equal(unitFiveTexture, unitFive);
        return [active, activeBinding, buffer, alignment, rowLength, skipRows, skipPixels, unitZero, unitFive];
    }

    private static byte[] ReadPixels(int texture)
    {
        // Read with a tight pack layout, then put the caller's pack state and binding back.
        int[] names = [PackAlignment, PackRowLength, PackSkipRows, PackSkipPixels];
        int[] saved = new int[names.Length];
        GL.GetInteger(GetPName.TextureBinding2D, out int bound);
        GL.GetInteger((GetPName)PixelPackBufferBinding, out int packBuffer);
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
}
