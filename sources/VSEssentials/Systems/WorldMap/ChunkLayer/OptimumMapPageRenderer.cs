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
    private const int FloatsPerInstance = 5;

    private readonly ICoreClientAPI _capi;
    private readonly OptimumMapTextureArray _texArray;
    private IShaderProgram _shader;

    // The quad and its per-instance stream travel as one mesh through the
    // engine's own mesh API, so the platform routes it to whichever backend
    // is live. Position is a vec3 at location 0; the custom floats are one
    // interleaved, instanced part: vec4 rect at 1 and float layer at 2.
    private MeshRef _mesh;
    private readonly MeshData _instanceUpdate;
    private bool _disposed;

    // Instance data buffer (reused each frame)
    // Each instance = 5 floats: posX, posY, sizeX, sizeY, layer
    private float[] _instanceData;
    private int _instanceCount;

    public bool Ready => _shader != null && !_shader.Disposed && _mesh != null && _texArray.TextureId != 0;

    public OptimumMapPageRenderer(ICoreClientAPI capi, OptimumMapTextureArray texArray)
    {
        _capi = capi;
        _texArray = texArray;
        _instanceData = new float[OptimumConfig.MapPageCacheMaxLayers * FloatsPerInstance];
        _instanceUpdate = new MeshData(0, 0, false, false, false, false);

        CreateShader();
        CreateQuadMesh();
        _capi.Event.ReloadShader += OnReloadShader;
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

        int offset = _instanceCount * FloatsPerInstance;
        if (offset + FloatsPerInstance > _instanceData.Length)
        {
            // Grow the CPU buffer; the GPU buffer is re-created to match.
            Array.Resize(ref _instanceData, _instanceData.Length * 2);
            RecreateQuadMesh();
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

        IOptimumGraphicsDevice optimumDevice = OptimumRender.Device;

        // The map renders inside the GUI pass which uses the 'gui' engine shader.
        // Entity/player/waypoint layers call GetEngineShader(Gui) and set uniforms
        // WITHOUT calling Use() - they assume it's already the active program.
        // We must stop it, run our shader, then explicitly re-bind GUI afterward.
        IShaderProgram guiShader = _capi.Render.GetEngineShader(EnumShaderProgram.Gui);
        IShaderProgram currentShader = _capi.Render.CurrentActiveShader;

        // The GUI pass runs with depth testing on; only GL can be asked, so the
        // device path restores that known state rather than querying it.
        bool depthTestWasOn = optimumDevice != null || GL.IsEnabled(EnableCap.DepthTest);

        currentShader?.Stop();

        _shader.Use();

        _shader.Uniform("screenSize", viewportWidth, viewportHeight);

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
        _shader.Uniform("zValue", ndcZ);

        // Bind the texture array to unit 0. The engine's BindTexture2D helper
        // binds GL_TEXTURE_2D, so the array target needs its own bind here.
        if (optimumDevice != null)
        {
            optimumDevice.BindTexture(0, _texArray.TextureId);
        }
        else
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture((TextureTarget)GL_TEXTURE_2D_ARRAY, _texArray.TextureId);
        }
        _shader.Uniform("mapPages", 0);

        // Upload this frame's instances.
        _instanceUpdate.CustomFloats.Count = _instanceCount * FloatsPerInstance;
        _capi.Render.UpdateMesh(_mesh, _instanceUpdate);

        // Disable depth writes: the page terrain sits behind everything else
        // on the map (icons, waypoints, player markers). Writing to the depth
        // buffer would reject those layers when they render at the same Z.
        _capi.Render.GLDepthMask(false);

        // Disable depth test: the map uses painter's algorithm (render order).
        // ChunkMapLayer draws first (position 0), then player/entity icons
        // (position 0.5), then waypoints (position 1). Icons draw OVER terrain
        // by virtue of rendering later. Depth test interferes because icons use
        // Z=60 while pages use Z=50.01 (closer in VS ortho = lower Z wins),
        // causing pages to occlude icons.
        _capi.Render.GLDisableDepthTest();

        _capi.Render.RenderMeshInstanced(_mesh, _instanceCount);

        // Restore the depth-test enable state we found on entry rather than
        // forcing it on: the GUI pass owns this state and later map layers
        // (player/entity/waypoint icons) render under whatever it was.
        if (depthTestWasOn) _capi.Render.GLEnableDepthTest();
        _capi.Render.GLDepthMask(true);

        _shader.Stop();

        // Unbind the texture array from unit 0 so the GUI shader finds its
        // expected 2D texture on that unit.
        if (optimumDevice != null)
        {
            optimumDevice.BindTexture(0, 0);
        }
        else
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture((TextureTarget)GL_TEXTURE_2D_ARRAY, 0);
        }

        // Re-bind the GUI shader. Entity/player/waypoint layers call
        // GetEngineShader(Gui).Uniform(...) without Use() - they rely on
        // the GUI program being the active program when their Render runs.
        guiShader?.Use();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _capi.Event.ReloadShader -= OnReloadShader;

        _mesh?.Dispose();
        _mesh = null;
        _shader?.Dispose();
        _shader = null;
    }

    private bool OnReloadShader()
    {
        if (_disposed) return true;
        _shader?.Dispose();
        CreateShader();
        return true;
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
    }

    private void RecreateQuadMesh()
    {
        _mesh?.Dispose();
        _mesh = null;
        CreateQuadMesh();
    }

    private void CreateQuadMesh()
    {
        // Quad vertices: two triangles forming a unit square [0,1]x[0,1].
        var mesh = new MeshData(6, 6, false, false, false, false);
        mesh.xyz = new float[]
        {
            0f, 0f, 0f,
            1f, 0f, 0f,
            1f, 1f, 0f,
            0f, 0f, 0f,
            1f, 1f, 0f,
            0f, 1f, 0f,
        };
        mesh.VerticesCount = 6;
        mesh.Indices = new int[] { 0, 1, 2, 3, 4, 5 };
        mesh.IndicesCount = 6;

        // Per-instance stream, sized to the CPU buffer and filled each frame.
        mesh.CustomFloats = NewInstancePart();
        mesh.CustomFloats.Values = new float[_instanceData.Length];
        // Upload the full, zeroed stream so the buffer is sized for every layer.
        mesh.CustomFloats.Count = _instanceData.Length;

        _mesh = _capi.Render.UploadMesh(mesh);

        // The frame update shares the instance array so no copy is needed.
        _instanceUpdate.CustomFloats = NewInstancePart();
        _instanceUpdate.CustomFloats.Values = _instanceData;
    }

    private static CustomMeshDataPartFloat NewInstancePart()
    {
        return new CustomMeshDataPartFloat
        {
            InterleaveSizes = new[] { 4, 1 },
            InterleaveStride = FloatsPerInstance * sizeof(float),
            InterleaveOffsets = new[] { 0, 4 * sizeof(float) },
            Instanced = true,
            StaticDraw = false,
        };
    }

    // Embedded shader source (avoids asset-path dependency at this stage)
    private const string VertexShaderSource = @"#version 330 core
layout(location = 0) in vec3 vertexPos;
layout(location = 1) in vec4 instanceRect;
layout(location = 2) in float instanceLayer;

uniform vec2 screenSize;
uniform float zValue;

out vec2 texCoord;
flat out float layerIndex;

void main(void)
{
    vec2 screenPos = instanceRect.xy + vertexPos.xy * instanceRect.zw;
    vec2 ndc = (screenPos / screenSize) * 2.0 - 1.0;
    ndc.y = -ndc.y;
    gl_Position = vec4(ndc, zValue, 1.0);
    texCoord = vertexPos.xy;
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
