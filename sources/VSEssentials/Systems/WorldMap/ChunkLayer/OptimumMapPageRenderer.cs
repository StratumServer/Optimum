using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace Vintagestory.GameContent;

/// <summary>
/// Renders map pages as instanced quads from a GL_TEXTURE_2D_ARRAY.
/// Each visible page becomes one instance: a screen-space rectangle plus a
/// layer index into the texture array. A single draw call renders all pages.
///
/// Lifecycle: created when the map opens (OnMapOpenedClient), disposed on
/// map close (OnMapClosedClient) or shutdown.
/// </summary>
public sealed class OptimumMapPageRenderer : IDisposable
{
    private const int GL_TEXTURE_2D_ARRAY = 35866;
    private const int GL_ARRAY_BUFFER = 34962;
    private const int GL_DYNAMIC_DRAW = 35048;
    private const int GL_FLOAT = 5126;
    private const int GL_TRIANGLES = 4;
    private const int GL_DEPTH_TEST = 2929;

    private readonly ICoreClientAPI _capi;
    private readonly OptimumMapTextureArray _texArray;
    private IShaderProgram _shader;

    // GL resources for the instanced quad
    private int _vaoId;
    private int _quadVboId;
    private int _instanceVboId;
    private bool _disposed;

    // Instance data buffer (reused each frame)
    // Each instance = 5 floats: posX, posY, sizeX, sizeY, layer
    private float[] _instanceData;
    private int _instanceCount;

    // Shader uniform locations
    private int _locScreenSize;
    private int _locMapPages;
    private int _locZValue;

    public bool Ready => _shader != null && _vaoId != 0 && _texArray.TextureId != 0;

    public OptimumMapPageRenderer(ICoreClientAPI capi, OptimumMapTextureArray texArray)
    {
        _capi = capi;
        _texArray = texArray;
        _instanceData = new float[OptimumConfig.MapPageCacheMaxLayers * 5];

        CreateShader();
        CreateQuadVao();
    }

    /// <summary>
    /// Begin a frame: reset the instance buffer.
    /// </summary>
    public void BeginFrame()
    {
        _instanceCount = 0;
    }

    /// <summary>
    /// Add a page to this frame's render batch. Called once per visible page.
    /// </summary>
    public void AddPage(float screenX, float screenY, float screenW, float screenH, int layer)
    {
        if (layer < 0) return;

        int offset = _instanceCount * 5;
        if (offset + 5 > _instanceData.Length)
        {
            // Grow the buffer
            Array.Resize(ref _instanceData, _instanceData.Length * 2);
        }

        _instanceData[offset + 0] = screenX;
        _instanceData[offset + 1] = screenY;
        _instanceData[offset + 2] = screenW;
        _instanceData[offset + 3] = screenH;
        _instanceData[offset + 4] = layer;
        _instanceCount++;
    }

    /// <summary>
    /// Submit the batch: upload instance data and draw all pages in one call.
    /// </summary>
    public void EndFrame(float viewportWidth, float viewportHeight)
    {
        if (_instanceCount == 0 || !Ready) return;

        // The map renders inside the GUI pass which uses the 'gui' engine shader.
        // Entity/player/waypoint layers call GetEngineShader(Gui) and set uniforms
        // WITHOUT calling Use() - they assume it's already the active GL program.
        // We must stop it, run our shader, then explicitly re-bind GUI afterward.
        IShaderProgram guiShader = _capi.Render.GetEngineShader(EnumShaderProgram.Gui);
        IShaderProgram currentShader = _capi.Render.CurrentActiveShader;

        bool depthTestWasOn = GL.IsEnabled(EnableCap.DepthTest);

        currentShader?.Stop();

        _shader.Use();

        // Set uniforms
        GL.Uniform2(_locScreenSize, viewportWidth, viewportHeight);

        // Z depth: vanilla terrain tiles render at Z=50 via GlTranslate.
        // Ortho projection: near=0.4 (NDC -1, front), far=20001 (NDC +1, back).
        // Depth func: GL_LESS (smaller NDC Z = closer, wins the test).
        // We use Z=50.01 which maps to a slightly LARGER NDC Z than vanilla's 50,
        // placing pages behind vanilla components. At page/component overlap
        // (partial coverage boundaries), the vanilla component wins with its
        // fresher pixels. Entity/waypoint layers render at Z < 50 (closer),
        // so they stay in front of both.
        const float orthoNear = 0.4f;
        const float orthoFar = 20001.0f;
        const float renderZ = 50.01f;
        float ndcZ = (2.0f * renderZ - orthoNear - orthoFar) / (orthoFar - orthoNear);
        GL.Uniform1(_locZValue, ndcZ);

        // Bind texture array to unit 0
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture((TextureTarget)GL_TEXTURE_2D_ARRAY, _texArray.TextureId);
        GL.Uniform1(_locMapPages, 0);

        // Upload instance data
        GL.BindBuffer((BufferTarget)GL_ARRAY_BUFFER, _instanceVboId);
        int byteSize = _instanceCount * 5 * sizeof(float);
        GL.BufferData((BufferTarget)GL_ARRAY_BUFFER, byteSize, _instanceData, (BufferUsageHint)GL_DYNAMIC_DRAW);
        GL.BindBuffer((BufferTarget)GL_ARRAY_BUFFER, 0);

        // Disable depth writes: the page terrain sits behind everything else
        // on the map (icons, waypoints, player markers). Writing to the depth
        // buffer would reject those layers when they render at the same Z.
        GL.DepthMask(false);

        // Disable depth test: the map uses painter's algorithm (render order).
        // ChunkMapLayer draws first (position 0), then player/entity icons
        // (position 0.5), then waypoints (position 1). Icons draw OVER terrain
        // by virtue of rendering later. Depth test interferes because icons use
        // Z=60 while pages use Z=50.01 (closer in VS ortho = lower Z wins),
        // causing pages to occlude icons.
        GL.Disable((EnableCap)GL_DEPTH_TEST);

        // Draw instanced
        GL.BindVertexArray(_vaoId);
        GL.DrawArraysInstanced((PrimitiveType)GL_TRIANGLES, 0, 6, _instanceCount);
        GL.BindVertexArray(0);

        // Restore the depth-test enable state we found on entry rather than
        // forcing it on: the GUI pass owns this state and later map layers
        // (player/entity/waypoint icons) render under whatever it was.
        if (depthTestWasOn) GL.Enable((EnableCap)GL_DEPTH_TEST);
        GL.DepthMask(true);

        _shader.Stop();

        // Restore GL state: unbind the texture array from unit 0 so the GUI
        // shader finds its expected 2D texture on that unit.
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture((TextureTarget)GL_TEXTURE_2D_ARRAY, 0);

        // Re-bind the GUI shader. Entity/player/waypoint layers call
        // GetEngineShader(Gui).Uniform(...) without Use() - they rely on
        // the GUI program being the active GL program when their Render runs.
        guiShader?.Use();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_vaoId != 0)
        {
            GL.DeleteVertexArray(_vaoId);
            _vaoId = 0;
        }
        if (_quadVboId != 0)
        {
            GL.DeleteBuffer(_quadVboId);
            _quadVboId = 0;
        }
        if (_instanceVboId != 0)
        {
            GL.DeleteBuffer(_instanceVboId);
            _instanceVboId = 0;
        }
        _shader?.Dispose();
        _shader = null;
    }

    private void CreateShader()
    {
        _shader = _capi.Shader.NewShaderProgram();

        // Load from assets (the shader files go into assets/game/shaders/ via packaging)
        // For now register as a memory shader with embedded source
        _shader.VertexShader = _capi.Shader.NewShader(EnumShaderType.VertexShader);
        _shader.VertexShader.Code = VertexShaderSource;
        _shader.FragmentShader = _capi.Shader.NewShader(EnumShaderType.FragmentShader);
        _shader.FragmentShader.Code = FragmentShaderSource;

        _capi.Shader.RegisterMemoryShaderProgram("optimum-map", _shader);
        _shader.Compile();

        _locScreenSize = GL.GetUniformLocation(_shader.ProgramId, "screenSize");
        _locMapPages = GL.GetUniformLocation(_shader.ProgramId, "mapPages");
        _locZValue = GL.GetUniformLocation(_shader.ProgramId, "zValue");
    }

    private void CreateQuadVao()
    {
        _vaoId = GL.GenVertexArray();
        GL.BindVertexArray(_vaoId);

        // Quad vertices: two triangles forming a unit square [0,1]x[0,1]
        float[] quadVerts = {
            0f, 0f,
            1f, 0f,
            1f, 1f,
            0f, 0f,
            1f, 1f,
            0f, 1f
        };

        _quadVboId = GL.GenBuffer();
        GL.BindBuffer((BufferTarget)GL_ARRAY_BUFFER, _quadVboId);
        GL.BufferData((BufferTarget)GL_ARRAY_BUFFER, quadVerts.Length * sizeof(float), quadVerts, (BufferUsageHint)35044); // GL_STATIC_DRAW
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 2, (VertexAttribPointerType)GL_FLOAT, false, 2 * sizeof(float), 0);

        // Instance VBO (dynamic, uploaded each frame)
        _instanceVboId = GL.GenBuffer();
        GL.BindBuffer((BufferTarget)GL_ARRAY_BUFFER, _instanceVboId);
        // Pre-allocate with null data
        GL.BufferData((BufferTarget)GL_ARRAY_BUFFER, _instanceData.Length * sizeof(float), IntPtr.Zero, (BufferUsageHint)GL_DYNAMIC_DRAW);

        int stride = 5 * sizeof(float);

        // Attribute 1: instanceRect (vec4: posX, posY, sizeX, sizeY)
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 4, (VertexAttribPointerType)GL_FLOAT, false, stride, 0);
        GL.VertexAttribDivisor(1, 1);

        // Attribute 2: instanceLayer (float)
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 1, (VertexAttribPointerType)GL_FLOAT, false, stride, 4 * sizeof(float));
        GL.VertexAttribDivisor(2, 1);

        GL.BindVertexArray(0);
        GL.BindBuffer((BufferTarget)GL_ARRAY_BUFFER, 0);
    }

    // Embedded shader source (avoids asset-path dependency at this stage)
    private const string VertexShaderSource = @"#version 330 core
layout(location = 0) in vec2 vertexPos;
layout(location = 1) in vec4 instanceRect;
layout(location = 2) in float instanceLayer;

uniform vec2 screenSize;
uniform float zValue;

out vec2 texCoord;
flat out float layerIndex;

void main(void)
{
    vec2 screenPos = instanceRect.xy + vertexPos * instanceRect.zw;
    vec2 ndc = (screenPos / screenSize) * 2.0 - 1.0;
    ndc.y = -ndc.y;
    gl_Position = vec4(ndc, zValue, 1.0);
    texCoord = vertexPos;
    layerIndex = instanceLayer;
}";

    private const string FragmentShaderSource = @"#version 330 core
uniform sampler2DArray mapPages;

in vec2 texCoord;
flat in float layerIndex;

out vec4 outColor;

void main(void)
{
    outColor = texture(mapPages, vec3(texCoord, layerIndex));
}";
}
