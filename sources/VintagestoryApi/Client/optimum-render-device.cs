using System;
using Vintagestory.API.Client;

namespace Vintagestory.API.Config;

/// <summary>
/// The graphics backend seam.
///
/// Vintage Story renders through OpenGL. Every upscaler and frame-generation SDK
/// worth shipping speaks D3D12 or Vulkan, and frame generation has to own
/// presentation, so Optimum grows a second renderer rather than a bridge. This
/// interface is that seam: <c>ClientPlatformWindows</c> and the handful of render
/// systems that call GL directly route through it when a device is installed, and
/// execute their untouched vanilla GL bodies when one is not.
///
/// The contract is deliberately GL-shaped. The game and its mods were written
/// against an immediate-mode state machine - set state, set named uniforms on the
/// active program, bind textures to units, draw a MeshRef - and reproducing that
/// protocol is what lets every render system and every mod keep working unchanged.
/// An implementation is expected to record state and resolve it at draw time, the
/// way Zink and ANGLE do, not to execute it eagerly.
///
/// Handles are <c>int</c> throughout because the game stores raw GL ids in public
/// API surface it cannot change: <see cref="LoadedTexture.TextureId" />,
/// <see cref="FrameBufferRef.FboId" />, <see cref="UBORef.Handle" />. An
/// implementation hands out opaque dense indices into its own tables; mods that
/// pass those ids back through the API never notice the difference.
///
/// UI toolkit types stay out. Cairo surfaces and Skia bitmaps arrive as raw pixel
/// pointers so this assembly, and any implementation of it, depends on nothing but
/// the game API.
///
/// Threading: every member must be called on the render thread, the same thread
/// that owns the GL context today. Deletions are the one exception - they may
/// arrive from a finalizer thread, and implementations must defer them.
/// </summary>
public interface IOptimumGraphicsDevice : IDisposable
{
    // ---------------------------------------------------------------- lifecycle

    /// <summary>
    /// Brings the device up against an already-created window. Returns false when
    /// the device cannot run here (missing API version, feature or presentable
    /// queue); the caller then falls back to OpenGL for the session. Must not
    /// throw for an ordinary unsupported-hardware outcome.
    /// </summary>
    bool Initialize(IntPtr windowHandle, int width, int height, out string failureReason);

    /// <summary>Human-readable backend name for logs and the settings screen.</summary>
    string BackendName { get; }

    string RendererString { get; }
    string VendorString { get; }
    string VersionString { get; }
    string ShaderVersionString { get; }

    int MaxTextureSize { get; }
    bool SupportsThickLines { get; }
    bool SupportsSSBOs { get; }

    /// <summary>Enables validation/debug reporting if the implementation has it.</summary>
    bool DebugMode { get; set; }

    /// <summary>
    /// Drains queued diagnostics. Returns null when clean, mirroring the
    /// contract of <c>ClientPlatformAbstract.GlGetError</c>.
    /// </summary>
    string GetError();

    // -------------------------------------------------------------------- frame

    /// <summary>Starts a frame: resets per-frame pools and acquires a target.</summary>
    void BeginFrame();

    /// <summary>
    /// Ends the frame and presents. This is the single point in the system where
    /// the image is flipped for scanout; see the coordinate note on
    /// <see cref="SetViewport" />.
    /// </summary>
    void Present();

    void Resize(int width, int height);
    void SetVSync(bool enabled);

    // ---------------------------------------------------------- immediate state

    /// <summary>
    /// Sets the viewport. Coordinates are GL's, unmodified: the implementation
    /// must not flip Y here. OpenGL and Vulkan differ only in what they *call*
    /// the origin - the memory relationship between clip space, framebuffer rows
    /// and texture coordinates is identical in both - so a layer that flips
    /// nothing reproduces GL's results bit for bit through every render-to-texture
    /// round trip. Only scanout differs, and that is handled once in
    /// <see cref="Present" />.
    /// </summary>
    void SetViewport(int x, int y, int width, int height);

    void SetScissor(int x, int y, int width, int height);
    void SetScissorEnabled(bool enabled);
    bool ScissorEnabled { get; }

    void SetDepthTest(bool enabled);
    void SetDepthMask(bool enabled);
    /// <summary>
    /// GL comparison constant (GL_LESS 513, GL_LEQUAL 515, ...). The game's own
    /// <c>EnumDepthFunction</c> lives in VintagestoryLib, which this assembly does
    /// not reference, and the caller already holds the constant.
    /// </summary>
    void SetDepthFunc(int func);

    void SetCullFace(bool enabled);
    /// <summary>true = cull back faces, false = cull front faces.</summary>
    void SetCullFaceMode(bool back);

    void SetBlend(bool enabled, EnumBlendMode mode);
    /// <summary>Per-attachment blend, as used by the OIT and SSAO passes.</summary>
    void SetBlendFuncSeparate(int attachment, int srcColor, int dstColor, int srcAlpha, int dstAlpha);
    void SetBlendEquation(int attachment, int mode);

    void SetColorMask(bool r, bool g, bool b, bool a);

    void SetStencilTest(bool enabled);
    void SetStencilMask(int mask);
    void SetStencilFunc(int func, int refValue, int mask);
    void SetStencilOp(int sfail, int dpfail, int dppass);

    void SetWireframe(bool enabled);
    void SetLineWidth(float width);

    // ------------------------------------------------------------------ shaders

    /// <summary>
    /// Prepares one stage. An implementation that links across stages (Vulkan
    /// must, because GL resolves uniforms by name across the whole program) does
    /// preprocessing and declaration analysis here and defers code generation to
    /// <see cref="LinkProgram" />. Errors are logged by the implementation and
    /// reported as false, matching the GL path.
    /// </summary>
    bool CompileShader(IShader shader);

    /// <summary>
    /// Links the staged stages into a usable program and returns its id, or 0 if
    /// linking failed.
    ///
    /// The id comes back rather than being written through the interface because
    /// <see cref="IShaderProgram.ProgramId" /> is read-only there; the caller
    /// assigns it, exactly as the GL path assigns what glCreateProgram returned.
    /// </summary>
    int LinkProgram(IShaderProgram program);

    void DeleteProgram(int programId);
    void UseProgram(int programId);

    /// <summary>
    /// Resolves a uniform name to an implementation-defined location, or -1 when
    /// the program does not use it. Callers treat this as opaque, exactly as they
    /// treat a GL uniform location.
    /// </summary>
    int GetUniformLocation(int programId, string name);

    void SetUniform(int programId, int location, float value);
    void SetUniform(int programId, int location, int value);
    void SetUniform(int programId, int location, float x, float y);
    void SetUniform(int programId, int location, float x, float y, float z);
    void SetUniform(int programId, int location, float x, float y, float z, float w);
    void SetUniformArray1(int programId, int location, int count, float[] values);
    void SetUniformArray2(int programId, int location, int count, float[] values);
    void SetUniformArray3(int programId, int location, int count, float[] values);
    void SetUniformArray4(int programId, int location, int count, float[] values);
    void SetUniformMatrix(int programId, int location, float[] matrix);
    void SetUniformMatrices(int programId, int location, int count, float[] matrices);
    void SetUniformMatrices4x3(int programId, int location, int count, float[] matrices);

    /// <summary>
    /// Points a sampler uniform at a texture unit. In GL this is just
    /// <c>Uniform1i</c>; here it is distinct so the implementation can bind the
    /// right descriptor at draw time.
    /// </summary>
    void SetSamplerUnit(int programId, string samplerName, int unit);

    // --------------------------------------------------------- uniform  buffers

    int CreateUniformBuffer(int programId, int bindingPoint, string blockName, int size);
    void UpdateUniformBuffer(int handle, IntPtr data, int offset, int size);
    void BindUniformBuffer(int handle);
    void UnbindUniformBuffer(int handle);
    void DeleteUniformBuffer(int handle);

    // ----------------------------------------------------------------- textures

    int CreateTexture2D(int width, int height, EnumTextureInternalFormat internalFormat,
        EnumTexturePixelFormat pixelFormat, IntPtr pixels, bool generateMipmaps);

    /// <summary>
    /// Creates a texture from a raw GL internal format.
    ///
    /// <see cref="EnumTextureInternalFormat" /> names only the four formats the
    /// public API exposes, but the client's own framebuffer setup uses several
    /// more - GL_RGB for the SSAO occlusion target, GL_RGBA32F for its noise
    /// texture. Taking the constant directly avoids widening a vanilla enum, and
    /// matches how the rest of this seam already accepts GL constants.
    /// </summary>
    /// <param name="generateMipmaps">
    /// Whether the image gets a full mip chain. It has to be decided here: an
    /// image is created with a fixed number of mip levels, so a later
    /// <c>GenerateMipmaps</c> on a single-level image has nowhere to write and
    /// silently does nothing. GL let the two be separate calls, which is why the
    /// block atlas ended up with no mipmaps at all.
    /// </param>
    int CreateTexture2DRaw(int width, int height, int glInternalFormat, IntPtr pixels, int bytesPerPixel,
        bool generateMipmaps = false);

    /// <summary>Six-layer cube map, faces in GL's +X -X +Y -Y +Z -Z order.</summary>
    /// <summary>
    /// Creates a cubemap from a raw GL internal format, the cube counterpart of
    /// <see cref="CreateTexture2DRaw" />. The skybox faces arrive as BGRA bytes,
    /// which <see cref="EnumTextureInternalFormat" /> cannot name.
    /// </summary>
    int CreateTextureCubeRaw(int size, int glInternalFormat, IntPtr[] facePixels, int bytesPerPixel);

    int CreateTextureCube(int size, EnumTextureInternalFormat internalFormat,
        EnumTexturePixelFormat pixelFormat, IntPtr[] facePixels);

    /// <summary>Array texture, as used for the OIT accumulation attachments.</summary>
    int CreateTexture2DArray(int width, int height, int layers,
        EnumTextureInternalFormat internalFormat, EnumTexturePixelFormat pixelFormat);

    void UploadTexture2D(int textureId, int level, int x, int y, int width, int height,
        EnumTexturePixelFormat pixelFormat, IntPtr pixels);

    void GenerateMipmaps(int textureId);
    void DeleteTexture(int textureId);

    /// <summary>
    /// Per-texture sampler state, keyed by the GL parameter name the caller would
    /// have passed to <c>glTexParameteri</c>. Kept GL-shaped because 107 call
    /// sites across the client and its mods pass these constants directly.
    /// </summary>
    void SetTextureParameter(int textureId, int parameterName, int value);
    void SetTextureParameter(int textureId, int parameterName, float value);
    int GetTextureParameter(int textureId, int parameterName);

    void BindTexture(int unit, int textureId);
    void BindTextureCube(int unit, int textureId);

    int CreateSampler(bool linear);
    void SetSamplerParameter(int samplerId, int parameterName, float value);
    /// <summary>Overrides the texture's own state on this unit; 0 clears.</summary>
    void BindSampler(int unit, int samplerId);
    void DeleteSampler(int samplerId);

    // ------------------------------------------------------------- framebuffers

    int CreateFramebuffer(int width, int height);
    void AttachTexture(int framebufferId, EnumFramebufferAttachment attachment, int textureId, int layer);

    /// <summary>
    /// Selects which colour attachments are written, as <c>glDrawBuffers</c> does.
    /// This is attachment *selection*, not a write mask: the final composition
    /// pass renders into attachment 0 while sampling attachment 1, which is legal
    /// only because attachment 1 is not part of the rendering scope.
    /// </summary>
    void SetDrawBuffers(int framebufferId, int attachmentMask);

    bool CheckFramebufferComplete(int framebufferId, out string status);
    void BindFramebuffer(int framebufferId);
    /// <summary>Binds the window's own target; the GL default framebuffer.</summary>
    void BindDefaultFramebuffer();
    void DeleteFramebuffer(int framebufferId);

    void ClearColor(int attachment, float r, float g, float b, float a);
    void ClearDepth(float depth);
    void ClearStencil();

    // ------------------------------------------------------------------- meshes

    int CreateMesh(MeshData data, bool staticDraw);

    int CreateEmptyMesh(int xyzSize, int normalsSize, int uvSize, int rgbaSize, int flagsSize,
        int indicesSize, CustomMeshDataPartFloat customFloats, CustomMeshDataPartShort customShorts,
        CustomMeshDataPartByte customBytes, CustomMeshDataPartInt customInts,
        EnumDrawMode drawMode, bool staticDraw, bool ssbo);

    void UpdateMesh(int meshId, MeshData data);

    /// <summary>
    /// Persistently mapped pointer for a dynamic mesh part, or
    /// <see cref="IntPtr.Zero" /> when that part is not mapped. The game writes
    /// straight through these while the GPU may still be reading, exactly as it
    /// does under GL; implementations reproduce the semantics rather than adding
    /// synchronisation the game does not expect.
    /// </summary>
    IntPtr GetMappedPointer(int meshId, EnumMeshBufferPart part);

    void DeleteMesh(int meshId);

    void DrawMesh(int meshId);
    void DrawMeshInstanced(int meshId, int instanceCount);
    void DrawMeshMulti(int meshId, int[] indicesStarts, int[] indicesSizes, int groupCount, bool ssbo);

    /// <summary>
    /// Writes packed face records into a mesh's storage buffer.
    ///
    /// The SSBO chunk path does not upload vertex attributes at all: it packs
    /// four vertices into one 16-byte face record and the vertex shader expands
    /// them from gl_VertexIndex. That leaves nothing for
    /// <see cref="UpdateMesh" /> to map onto, so the raw write is its own entry
    /// point. <paramref name="byteOffset" /> is into the buffer, not a vertex
    /// index.
    /// </summary>
    void UpdateMeshStorageBuffer(int meshId, IntPtr data, int byteOffset, int byteSize);
    /// <summary>The three-vertex fullscreen triangle every post pass draws.</summary>
    void DrawFullscreenTriangle();

    // ------------------------------------------------------------------ queries

    int CreateOcclusionQuery();
    void BeginOcclusionQuery(int queryId);
    void EndOcclusionQuery(int queryId);
    bool IsQueryResultAvailable(int queryId);
    int GetQueryResult(int queryId);
    void DeleteQuery(int queryId);

    // ----------------------------------------------------------------- readback

    /// <summary>
    /// Reads the default framebuffer back as BGRA8. Rows come back bottom-up,
    /// which is what <c>glReadPixels</c> produces and therefore what the existing
    /// screenshot and AVI paths already expect.
    /// </summary>
    void ReadDefaultFramebuffer(int x, int y, int width, int height, IntPtr destination);
}

/// <summary>
/// The OpenGL constants the routed client code passes across the seam.
///
/// The seam speaks raw GL constants for texture and comparison parameters,
/// because that is what the client and its mods already hold - a hundred-odd call
/// sites pass them straight to glTexParameter. Naming them here keeps the patched
/// bodies readable without inventing an enum that would have to be translated
/// back at the boundary.
/// </summary>
public static class OptimumGlConstants
{
    public const int TextureMagFilter = 0x2800;
    public const int TextureMinFilter = 0x2801;
    public const int TextureWrapS = 0x2802;
    public const int TextureWrapT = 0x2803;
    public const int TextureCompareMode = 0x884C;
    public const int TextureLodBias = 0x8501;

    public const int Nearest = 0x2600;
    public const int Linear = 0x2601;
    public const int Repeat = 0x2901;
    public const int ClampToEdge = 0x812F;

    public const int CompareModeNone = 0;
    public const int CompareRefToTexture = 0x884E;

    // Sized internal formats for CreateTexture2DRaw, which takes the GL token
    // rather than EnumTextureInternalFormat so call sites that already hold a
    // raw format - Cairo surfaces and the GUI's BGRA uploads - pass it through.
    public const int Rgba8 = 0x8058;

    // GL_BGRA. Not a sized internal format in GL - the GL bodies pass it as the
    // source pixel format alongside an RGBA8 internal format - but across this
    // seam it selects a BGRA-ordered image, which is the same result with one
    // fewer argument.
    public const int Bgra = 0x80E1;
}

/// <summary>Which buffer of a mesh a persistent mapping refers to.</summary>
public enum EnumMeshBufferPart
{
    Xyz,
    Normals,
    Uv,
    Rgba,
    Flags,
    CustomFloats,
    CustomShorts,
    CustomInts,
    CustomBytes,
    Indices
}

/// <summary>Which renderer the client runs.</summary>
public enum EnumRenderBackend
{
    /// <summary>Vanilla OpenGL. The default, and the fallback for everything.</summary>
    OpenGL,
    Vulkan,
    /// <summary>Vulkan where the vendor and driver are known good, else OpenGL.</summary>
    Auto
}

/// <summary>
/// Where the client finds its graphics device.
///
/// <see cref="Device" /> is null on the OpenGL path, and every routed call site
/// checks it before doing anything, so an OpenGL session executes the vanilla
/// body with one null check in front of it. That is the same shape the greedy-mesh
/// and FSR features use: off is vanilla, exactly.
///
/// The device is created reflectively by the launcher so that VintagestoryLib
/// never carries an assembly reference to a renderer implementation.
/// </summary>
public static class OptimumRender
{
    /// <summary>The active device, or null when running on OpenGL.</summary>
    public static IOptimumGraphicsDevice Device;

    /// <summary>Which backend was actually selected, after fallbacks.</summary>
    public static EnumRenderBackend ActiveBackend = EnumRenderBackend.OpenGL;

    /// <summary>
    /// Why the requested backend was not used, or null when it was. Shown in the
    /// settings screen and written to the client log.
    /// </summary>
    public static string FallbackReason;

    public static bool IsVulkan => Device != null;

    /// <summary>
    /// Set before the window is created when the backend decision was "not
    /// OpenGL", so the window opens with no graphics API at all.
    ///
    /// <see cref="Device" /> cannot answer this question: the device is created
    /// against an existing window, so during window construction it is still
    /// null even on the Vulkan path. Vanilla code that issues GL calls while the
    /// window comes up - GameWindowNative's clear-and-swap, for one - has to test
    /// this flag instead, because with ContextAPI.NoAPI there is no GL binding
    /// loaded and every GL entry point throws.
    /// </summary>
    public static bool NoGraphicsApiWindow;

    /// <summary>
    /// Records a fallback to OpenGL. Idempotent in the sense that the first
    /// reason wins: the earliest cause is the useful one to report.
    /// </summary>
    public static void FallBackToOpenGL(string reason)
    {
        Device = null;
        ActiveBackend = EnumRenderBackend.OpenGL;
        // The caller reopens the window for OpenGL after this, so GL calls during
        // window construction are live again.
        NoGraphicsApiWindow = false;
        if (FallbackReason == null)
        {
            FallbackReason = reason;
        }
    }
}
