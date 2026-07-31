using System;
using OpenTK.Graphics.OpenGL;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SkiaSharp;

namespace Optimum.Launcher;

/// <summary>
/// Shown only while patching a cache-miss (first launch, or after an
/// Optimum update); the cache-hit path never touches this. Styled after
/// the vanilla client's own GuiScreenLoadingGame ("Loading game" /
/// "Loading shaders" screen: a dark overlay with centered status text,
/// same font family/color/size as GuiStyle.StandardFontName /
/// GuiStyle.DialogDefaultTextColor / GuiStyle.NormalFontSize) so the
/// handoff into the real loading screen reads as one continuous screen
/// instead of a console flash followed by a jump cut.
///
/// Uses core-profile GL (shader + VAO/VBO), not the legacy fixed-function
/// pipeline: drivers are free to hand back a core profile even when a
/// compatibility profile is requested (confirmed happening here, on Mesa/
/// Zink), and macOS never exposes a compatibility profile at all for GL
/// 3.2+. Legacy glBegin/glMatrixMode calls fail silently (GL_INVALID_ENUM/
/// GL_INVALID_OPERATION, no exception) under a core context, which read as
/// "nothing draws" rather than a crash - caught by reading back the
/// framebuffer in a throwaway test harness, not by anything visual.
///
/// Must be constructed, pumped, and disposed from the same thread (GLFW
/// requires this, and is strict about it on macOS). Do the actual patch
/// work on a background thread and call <see cref="PumpAndRender"/> in a
/// loop on the thread that owns this instance.
/// </summary>
internal sealed class PatchSplashScreen : IDisposable
{
    private const string TextColorHex = "#e9ddce";
    private const string FontFamily = "sans-serif";
    private const float FontSize = 28f;

    private const string VertexShaderSource = """
        #version 330 core
        layout(location = 0) in vec2 aPos;
        layout(location = 1) in vec2 aTexCoord;
        out vec2 vTexCoord;
        void main()
        {
            gl_Position = vec4(aPos, 0.0, 1.0);
            vTexCoord = aTexCoord;
        }
        """;

    private const string FragmentShaderSource = """
        #version 330 core
        in vec2 vTexCoord;
        out vec4 FragColor;
        uniform sampler2D uTexture;
        void main()
        {
            FragColor = texture(uTexture, vTexCoord);
        }
        """;

    private readonly GameWindow _window;
    private readonly object _sync = new();
    private string _pendingText = "Applying Optimum patches...";
    private string _renderedText = "\0"; // force first render to differ
    private int _shaderProgram;
    private int _vao;
    private int _vbo;
    private int _textureId;
    private int _textureWidth;
    private int _textureHeight;
    private bool _disposed;

    static PatchSplashScreen()
    {
        // OpenTK's default GLFW error callback throws a GLFWException for
        // every GLFW-reported error, including ones that aren't actually
        // fatal to us - e.g. Wayland compositors reject
        // glfwGetWindowPos/SetWindowPos outright (CenterWindow() below
        // needs it), which would otherwise crash the entire game launch
        // over a cosmetic splash. Log and continue instead; this is
        // OpenTK's own documented escape hatch for exactly this situation.
        GLFWProvider.SetErrorCallback((error, description) =>
            Logger.LogError($"[Optimum] GLFW warning ({error}): {description}"));
    }

    public PatchSplashScreen()
    {
        var settings = new NativeWindowSettings
        {
            ClientSize = new Vector2i(640, 220),
            Title = "Optimum",
            WindowBorder = WindowBorder.Hidden,
            StartVisible = false,
            StartFocused = true,
            Vsync = VSyncMode.On,
            Profile = ContextProfile.Core,
            APIVersion = new Version(3, 3),
            NumberOfSamples = 0,
        };

        _window = new GameWindow(GameWindowSettings.Default, settings);
        _window.CenterWindow();

        GL.ClearColor(0.06f, 0.06f, 0.06f, 1f);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);

        _shaderProgram = CompileProgram();

        _vao = GL.GenVertexArray();
        _vbo = GL.GenBuffer();
        GL.BindVertexArray(_vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, 4 * 4 * sizeof(float), IntPtr.Zero, BufferUsageHint.DynamicDraw);
        GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 0);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), 2 * sizeof(float));
        GL.EnableVertexAttribArray(1);
        GL.BindVertexArray(0);

        _window.IsVisible = true;
    }

    private static int CompileProgram()
    {
        int vertex = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vertex, VertexShaderSource);
        GL.CompileShader(vertex);
        CheckShaderCompile(vertex, "vertex");

        int fragment = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fragment, FragmentShaderSource);
        GL.CompileShader(fragment);
        CheckShaderCompile(fragment, "fragment");

        int program = GL.CreateProgram();
        GL.AttachShader(program, vertex);
        GL.AttachShader(program, fragment);
        GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0)
        {
            string log = GL.GetProgramInfoLog(program);
            throw new InvalidOperationException($"Splash shader link failed: {log}");
        }

        GL.DeleteShader(vertex);
        GL.DeleteShader(fragment);
        return program;
    }

    private static void CheckShaderCompile(int shader, string name)
    {
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
        if (compiled == 0)
        {
            string log = GL.GetShaderInfoLog(shader);
            throw new InvalidOperationException($"Splash {name} shader failed to compile: {log}");
        }
    }

    /// <summary>Thread-safe: called from the background thread doing the actual patch work.</summary>
    public void SetStatus(string text)
    {
        lock (_sync)
        {
            _pendingText = text;
        }
    }

    /// <summary>Call in a loop from the thread that constructed this instance.</summary>
    public void PumpAndRender()
    {
        if (_disposed) return;

        _window.ProcessEvents(0);
        if (_window.IsExiting) return;

        string text;
        lock (_sync)
        {
            text = _pendingText;
        }
        if (text != _renderedText)
        {
            UploadTextTexture(text);
            _renderedText = text;
        }

        Render();
        _window.Context.SwapBuffers();
    }

    private void UploadTextTexture(string text)
    {
        using var typeface = SKTypeface.FromFamilyName(
            FontFamily, SKFontStyleWeight.Normal, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        using var font = new SKFont(typeface, FontSize);
        using var paint = new SKPaint { Color = SKColor.Parse(TextColorHex), IsAntialias = true };

        float textWidth = font.MeasureText(text, paint);
        var metrics = font.Metrics;
        int width = Math.Max(1, (int)Math.Ceiling(textWidth) + 4);
        int height = Math.Max(1, (int)Math.Ceiling(metrics.Descent - metrics.Ascent) + 4);

        using var bitmap = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.DrawText(text, 2, 2 - metrics.Ascent, font, paint);
        }

        if (_textureId == 0)
        {
            _textureId = GL.GenTexture();
        }
        GL.BindTexture(TextureTarget.Texture2D, _textureId);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
        GL.TexImage2D(
            TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0,
            PixelFormat.Rgba, PixelType.UnsignedByte, bitmap.GetPixels());

        _textureWidth = width;
        _textureHeight = height;
    }

    private void Render()
    {
        GL.Viewport(0, 0, _window.ClientSize.X, _window.ClientSize.Y);
        GL.Clear(ClearBufferMask.ColorBufferBit);

        if (_textureId == 0 || _textureWidth == 0 || _textureHeight == 0) return;

        // Quad centered in the window, in normalized device coordinates.
        float halfW = _textureWidth / (float)_window.ClientSize.X;
        float halfH = _textureHeight / (float)_window.ClientSize.Y;
        float[] vertices =
        [
            // pos.x,  pos.y, uv.x, uv.y
            -halfW,  halfH, 0f, 0f,
             halfW,  halfH, 1f, 0f,
            -halfW, -halfH, 0f, 1f,
             halfW, -halfH, 1f, 1f,
        ];

        GL.BindBuffer(BufferTarget.ArrayBuffer, _vbo);
        GL.BufferSubData(BufferTarget.ArrayBuffer, IntPtr.Zero, vertices.Length * sizeof(float), vertices);

        GL.UseProgram(_shaderProgram);
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, _textureId);
        GL.Uniform1(GL.GetUniformLocation(_shaderProgram, "uTexture"), 0);

        GL.BindVertexArray(_vao);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
        GL.BindVertexArray(0);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_textureId != 0)
        {
            GL.DeleteTexture(_textureId);
            _textureId = 0;
        }
        if (_vbo != 0) GL.DeleteBuffer(_vbo);
        if (_vao != 0) GL.DeleteVertexArray(_vao);
        if (_shaderProgram != 0) GL.DeleteProgram(_shaderProgram);
        _window.IsVisible = false;
        _window.Dispose();
    }
}
