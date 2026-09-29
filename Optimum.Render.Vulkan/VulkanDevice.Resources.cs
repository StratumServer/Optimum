using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Shaders;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan;

public sealed unsafe partial class VulkanDevice
{
    // -------------------------------------------------------------------- textures

    public int CreateTexture2D(
        int width, int height, EnumTextureInternalFormat internalFormat,
        EnumTexturePixelFormat pixelFormat, IntPtr pixels, bool generateMipmaps)
    {
        int id = _textures.Create((uint)width, (uint)height,
            GlEnums.TextureFormatFrom(internalFormat), generateMipmaps: generateMipmaps);
        RecordGlInternalFormat(id, (int)internalFormat);

        if (pixels != IntPtr.Zero)
        {
            _textures.Upload(id, 0, 0, 0, (uint)width, (uint)height, pixels, BytesPerPixel(internalFormat));
            if (generateMipmaps) _textures.GenerateMipmaps(id);
        }
        return id;
    }

    public int CreateTexture2DRaw(int width, int height, int glInternalFormat, IntPtr pixels, int bytesPerPixel,
        bool generateMipmaps = false)
    {
        Format format = GlEnums.TextureFormatFromGl(glInternalFormat);
        int id = _textures.Create((uint)width, (uint)height, format, generateMipmaps: generateMipmaps);
        RecordGlInternalFormat(id, glInternalFormat);

        if (pixels != IntPtr.Zero && bytesPerPixel > 0)
        {
            _textures.Upload(id, 0, 0, 0, (uint)width, (uint)height, pixels, bytesPerPixel);

            // A chain that was asked for has to be filled here. GL's texture is
            // complete the moment glGenerateMipmap runs, but an image created
            // with levels and never blitted into keeps whatever its memory held,
            // and every sample above level 0 reads that - which looks like other
            // textures bleeding onto a surface as it turns away from the camera.
            if (generateMipmaps) _textures.GenerateMipmaps(id);
        }
        RenderTrace.TextureCreated(id, width, height, format, pixels, bytesPerPixel);
        return id;
    }

    /// <summary>
    /// A post-chain colour texture (framebuffer slots in
    /// <see cref="Graph.TransientAllocator.PostChainSlots" />): created in the Transient
    /// memory pool class and registered with the transient allocator. Until the frame
    /// graph binds it (<see cref="BindTransientForFrame" />) it behaves like any texture.
    /// </summary>
    public int CreateTransientTexture2D(int width, int height, EnumTextureInternalFormat internalFormat,
        int framebufferSlot)
    {
        int id = _textures.Create((uint)width, (uint)height, GlEnums.TextureFormatFrom(internalFormat),
            poolClass: MemoryPoolClass.Transient);
        RecordGlInternalFormat(id, (int)internalFormat);
        _transients.OptIn(id, framebufferSlot);
        return id;
    }

    /// <summary>Registers (or re-tags) a texture as the transient of client framebuffer slot <paramref name="framebufferSlot" />.</summary>
    public void OptInTransient(int textureId, int framebufferSlot) => _transients.OptIn(textureId, framebufferSlot);

    /// <summary><see cref="CreateTransientTexture2D" /> with a raw GL internal format token, no pixels.</summary>
    public int CreateTransientTexture2DRaw(int width, int height, int glInternalFormat, int framebufferSlot)
    {
        Format format = GlEnums.TextureFormatFromGl(glInternalFormat);
        int id = _textures.Create((uint)width, (uint)height, format, poolClass: MemoryPoolClass.Transient);
        RecordGlInternalFormat(id, glInternalFormat);
        RenderTrace.TextureCreated(id, width, height, format, IntPtr.Zero, 0);
        _transients.OptIn(id, framebufferSlot);
        return id;
    }

    /// <summary>
    /// Serves a texture for passes [<paramref name="firstPass" />, <paramref name="lastPass" />]
    /// of the current frame through the transient allocator and returns the texture id
    /// that backs it (itself unless aliasing is on). Call after BeginFrame, in pass order.
    /// </summary>
    public int BindTransientForFrame(int textureId, int firstPass, int lastPass) =>
        _transients.Bind(textureId, firstPass, lastPass);

    /// <summary>The transient allocator the frame graph acquires physical images from.</summary>
    internal Graph.TransientAllocator Transients => _transients;

    /// <summary>The ReadSelf copy pool. Tests only.</summary>
    internal Graph.FeedbackCopyPool ReadSelfCopiesForTests => _readSelfCopies;

    /// <summary>Forces transient aliasing on or off before Initialize (default: <c>OPTIMUM_VULKAN_ALIAS</c>).</summary>
    internal bool? TransientAliasingOverride { get; set; }

    private int CreateReadSelfCopy(Graph.FeedbackCopyDesc desc) =>
        _textures.Create(desc.Width, desc.Height, desc.Format, layers: desc.Layers, cube: desc.Cube,
            generateMipmaps: desc.MipLevels > 1, poolClass: MemoryPoolClass.Transient);

    /// <summary>Gives the previous draw's ReadSelf copies back to the pool.</summary>
    private void ReleaseReadSelfCopies()
    {
        if (_sampledTextureOverrides.Count == 0) return;
        foreach (int copy in _sampledTextureOverrides.Values) _readSelfCopies.Release(copy);
        _sampledTextureOverrides.Clear();
    }

    public int CreateTextureCubeRaw(int size, int glInternalFormat, IntPtr[] facePixels, int bytesPerPixel)
    {
        Format format = GlEnums.TextureFormatFromGl(glInternalFormat);
        int id = _textures.Create((uint)size, (uint)size, format, cube: true);

        for (uint face = 0; face < 6 && face < facePixels.Length; face++)
        {
            if (facePixels[face] == IntPtr.Zero) continue;
            _textures.Upload(id, 0, 0, 0, (uint)size, (uint)size,
                facePixels[face], bytesPerPixel, face);
        }
        return id;
    }

    public int CreateTextureCube(
        int size, EnumTextureInternalFormat internalFormat,
        EnumTexturePixelFormat pixelFormat, IntPtr[] facePixels)
    {
        Format format = GlEnums.TextureFormatFrom(internalFormat);
        int id = _textures.Create((uint)size, (uint)size, format, cube: true);

        for (uint face = 0; face < 6 && face < facePixels.Length; face++)
        {
            if (facePixels[face] == IntPtr.Zero) continue;
            _textures.Upload(id, 0, 0, 0, (uint)size, (uint)size,
                facePixels[face], BytesPerPixel(internalFormat), face);
        }
        return id;
    }

    public int CreateTexture2DArray(
        int width, int height, int layers,
        EnumTextureInternalFormat internalFormat, EnumTexturePixelFormat pixelFormat) =>
        _textures.Create((uint)width, (uint)height,
            GlEnums.TextureFormatFrom(internalFormat), layers: (uint)layers);

    public void UploadTexture2D(
        int textureId, int level, int x, int y, int width, int height,
        EnumTexturePixelFormat pixelFormat, IntPtr pixels)
    {
        FlushPendingClears(textureId);
        _textures.Upload(textureId, level, x, y, (uint)width, (uint)height, pixels,
            pixelFormat == EnumTexturePixelFormat.Red ? 1 : 4);
    }

    public void UploadTexture2DRaw(
        int textureId, int level, int x, int y, int width, int height, IntPtr pixels, int bytesPerPixel)
    {
        if (bytesPerPixel <= 0) return;
        FlushPendingClears(textureId);
        _textures.Upload(textureId, level, x, y, (uint)width, (uint)height, pixels, bytesPerPixel);
    }

    public void GenerateMipmaps(int textureId)
    {
        FlushPendingClears(textureId);
        _textures.GenerateMipmaps(textureId);
    }

    public void DeleteTexture(int textureId) => ReleaseTexture(textureId);

    /// <summary>
    /// Deletes a texture and evicts every descriptor set that names it.
    ///
    /// The eviction is the important half. The texture itself is destroyed once
    /// the Frame timeline passed every frame that could name it, but a cached set would outlive it and, once the driver
    /// reused the view handle for a new texture, be served to draws of that new
    /// texture - which is a GPU read of freed memory. The GUI re-renders its text
    /// into fresh textures constantly, so this was the loading-screen crash.
    /// </summary>
    private void ReleaseTexture(int textureId)
    {
        if (_sampledTextureOverrides.Remove(textureId, out int copy)) _readSelfCopies?.Release(copy);
        _transients?.Forget(textureId);
        _textures.RestoreBinding(textureId);
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture != null)
        {
            _descriptors.Release(texture.Id);
            _targets.DropPendingClears(texture);
        }
        _textures.Delete(textureId, _frames);
        // Only a delete that found something is a delete. Deleting an id twice
        // (framebuffers share a depth texture) otherwise inflated the counter
        // past the number of textures that ever existed.
        if (texture != null) VulkanStats.NoteTextureDeleted();
    }

    public void SetTextureParameter(int textureId, int parameterName, int value) =>
        _textures.SetParameter(textureId, parameterName, value);

    public void SetTextureParameter(int textureId, int parameterName, float value) =>
        _textures.SetParameter(textureId, parameterName, value);

    public void SetTextureBorderColor(int textureId, float r, float g, float b, float a) =>
        _textures.SetBorderColor(textureId, r, g, b, a);

    public int GetTextureParameter(int textureId, int parameterName)
    {
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture == null) return 0;

        return parameterName == GlEnums.TextureCompareMode
            ? texture.State.CompareEnable ? GlEnums.TextureCompareRefToTexture : GlEnums.TextureCompareModeNone
            : 0;
    }

    public void UploadTexture2DArrayLayer(int textureId, int layer, int x, int y,
        int width, int height, IntPtr pixels)
    {
        FlushPendingClears(textureId);
        _textures.Upload(textureId, 0, x, y, (uint)width, (uint)height, pixels, 4, (uint)layer);
    }

    public void UploadTexture2DNormalizedShorts(int textureId, int level, int x, int y,
        int width, int height, short[] pixels)
    {
        FlushPendingClears(textureId);
        _textures.UploadNormalizedShorts(textureId, level, x, y, width, height, pixels);
    }

    private readonly Dictionary<int, SamplerState> _standaloneSamplers = new();
    private int _nextSamplerId = 1;

    public int CreateSampler(bool linear)
    {
        int id = _nextSamplerId++;
        _standaloneSamplers[id] = SamplerState.Default with
        {
            MagFilter = linear ? Filter.Linear : Filter.Nearest,
            // GenSampler uses GL_NEAREST_MIPMAP_LINEAR for both variants;
            // the flag changes magnification only. Terrain relies on this
            // override retaining the atlas mip chain at a distance.
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Linear,
            Mipmapped = true,
        };
        return id;
    }

    public void SetSamplerParameter(int samplerId, int parameterName, float value)
    {
        if (!_standaloneSamplers.TryGetValue(samplerId, out SamplerState state)) return;

        _standaloneSamplers[samplerId] = parameterName == GlEnums.TextureLodBias
            ? state with { LodBias = value }
            : state;
    }

    public void DeleteSampler(int samplerId) => _standaloneSamplers.Remove(samplerId);

    private static int BytesPerPixel(EnumTextureInternalFormat format) => format switch
    {
        EnumTextureInternalFormat.Rgba8 => 4,
        EnumTextureInternalFormat.Rgba16f => 8,
        EnumTextureInternalFormat.R16f => 2,
        EnumTextureInternalFormat.DepthComponent32 => 4,
        _ => 4,
    };

    // ---------------------------------------------------------------- framebuffers

    public int CreateFramebuffer(int width, int height) => _targets.Create((uint)width, (uint)height);

    public void AttachTexture(int framebufferId, EnumFramebufferAttachment attachment, int textureId, int layer)
    {
        int index = attachment == EnumFramebufferAttachment.DepthAttachment
            ? -1
            : (int)attachment - (int)EnumFramebufferAttachment.ColorAttachment0;

        _targets.Attach(framebufferId, index, textureId, (uint)layer);
    }

    public bool CheckFramebufferComplete(int framebufferId, out string status)
    {
        // Dynamic rendering has no framebuffer object to validate, so
        // completeness reduces to having a target with attachments.
        VulkanFramebuffer? framebuffer = _targets.Get(framebufferId);
        if (framebuffer == null)
        {
            status = "no such framebuffer";
            return false;
        }

        status = "complete";
        return true;
    }

    public void DeleteFramebuffer(int framebufferId)
    {
        _targets.Delete(framebufferId);
        FramebufferDeleted?.Invoke(framebufferId);
    }

    /// <summary>The platform whose graphics this device is; null for a bare device (the GPU tests).</summary>
    internal Platform.VulkanClientPlatform? OwnerPlatform { get; set; }

    /// <summary>
    /// Raised after a framebuffer is deleted. Its id is reused by the next one created, so a
    /// record keyed on it (the stated draw buffers) has to forget it here.
    /// </summary>
    internal Action<int>? FramebufferDeleted;

    /// <summary>Whether the frame graph records this device's frames. Change only between frames.</summary>
    internal bool FrameGraphEnabled
    {
        get => _graph.Enabled;
        set => _graph.Enabled = value;
    }

    /// <summary>The frame graph's totals. Tests only.</summary>
    internal Graph.FrameGraph FrameGraphForTests => _graph;

    /// <summary>The bound render target's id (0 before any bind).</summary>
    internal int BoundFramebufferId => _targets.Bound?.Id ?? 0;

    /// <summary>The render target standing for the default framebuffer (0 when headless).</summary>
    internal int DefaultFramebufferId => _defaultFramebuffer;

    /// <summary>
    /// Ends the pass a render stage left open (closes its scope): a native draw recorded with
    /// keepScope stays in the stage's declaration until here. No-op with the frame graph off.
    /// </summary>
    internal void EndStagePass()
    {
        if (_frameActive) _targets.EndPass(Commands);
    }

    /// <summary>Lands the clears promoted into a texture before it is written some other way.</summary>
    private void FlushPendingClears(int textureId)
    {
        if (!_frameActive || !_graph.HasPendingClears) return;
        VulkanTexture? texture = _textures.Get(textureId);
        if (texture != null) _targets.FlushPendingClears(Commands, texture);
    }

    /// <summary>
    /// A colour clear of one attachment of an explicit target, outside every native pass: the
    /// promoted LOAD_OP_CLEAR of the next pass on it, or an attachment clear inside an open scope.
    /// The caller has applied the draw buffers and colour mask it stated (VulkanClientPlatform).
    /// </summary>
    internal void ClearNativeColor(int framebufferId, int attachment, float r, float g, float b, float a)
    {
        if (!BindForNativeClear(framebufferId)) return;
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("clearColor attachment=" + attachment + " target=" + _targets.Bound!.Id +
                " rgba=" + r + "," + g + "," + b + "," + a);
        }
        _targets.ClearColor(Commands, attachment, r, g, b, a);
    }

    /// <summary>The depth clear of an explicit target; the caller has applied the stated depth mask.</summary>
    internal void ClearNativeDepth(int framebufferId, float depth)
    {
        if (!BindForNativeClear(framebufferId)) return;
        if (RenderTrace.Enabled) RenderTrace.Write("clearDepth target=" + _targets.Bound!.Id + " depth=" + depth);
        _targets.ClearDepth(Commands, depth);
    }

    private bool BindForNativeClear(int framebufferId)
    {
        EndNativePass();
        if (!_frameActive) return false;
        int id = ResolveNativeFramebuffer(framebufferId);
        VulkanFramebuffer? target = _targets.Get(id);
        if (target == null) return false;
        if (!ReferenceEquals(_targets.Bound, target)) _targets.Bind(Commands, id);
        return true;
    }
}

public sealed unsafe partial class VulkanDevice
{
    // --------------------------------------------------------------------- meshes

    public int CreateMesh(MeshData data, bool staticDraw)
    {
        // Sized as GL's UploadMesh sizes them. Every part follows the vertex
        // count except flags, which GL allocates at the array's full length -
        // a mesh that later grows within that capacity updates its flags in
        // place there, and would overflow a vertex-count-sized buffer here.
        int vertices = data.VerticesCount;
        int id = _meshes.CreateEmpty(
            data.xyz != null ? vertices * 3 * sizeof(float) : 0,
            data.Normals != null ? vertices * sizeof(int) : 0,
            data.Uv != null ? vertices * 2 * sizeof(float) : 0,
            data.Rgba != null ? vertices * 4 : 0,
            data.Flags != null ? data.Flags.Length * sizeof(int) : 0,
            data.IndicesCount * sizeof(int),
            data.CustomFloats, data.CustomShorts, data.CustomBytes, data.CustomInts,
            data.mode, staticDraw, ssbo: false, signedCustomShorts: true);

        UpdateMesh(id, data);
        return id;
    }

    public int CreateEmptyMesh(
        int xyzSize, int normalsSize, int uvSize, int rgbaSize, int flagsSize, int indicesSize,
        CustomMeshDataPartFloat customFloats, CustomMeshDataPartShort customShorts,
        CustomMeshDataPartByte customBytes, CustomMeshDataPartInt customInts,
        EnumDrawMode drawMode, bool staticDraw, bool ssbo) =>
        _meshes.CreateEmpty(xyzSize, normalsSize, uvSize, rgbaSize, flagsSize, indicesSize,
            customFloats, customShorts, customBytes, customInts, drawMode, staticDraw, ssbo);

    /// <summary>
    /// Writes a mesh's data, honouring the destination offset each part carries.
    ///
    /// Those offsets are the whole point. The game pools chunk meshes: one large
    /// mesh holds many chunks, and each chunk is handed the same mesh with the
    /// byte offset of its own slice in every part. GL's updateVAO takes that
    /// offset as the destination for a glBufferSubData, so writing it at zero
    /// instead stacks every chunk in the world on top of the first one - which
    /// renders as no terrain at all.
    ///
    /// The counts are per part as well, not VerticesCount: a part can be absent
    /// or shorter than the vertex count, and the custom buffers have no fixed
    /// relationship to it.
    /// </summary>
    public void UpdateMesh(int meshId, MeshData data)
    {
        // An SSBO mesh's xyz slot holds packed face records, written through
        // UpdateMeshStorageBuffer; positions never belong there. The game hands
        // the same MeshData to both calls, so without this the positions would
        // land on top of the records - or, depending on order, under them.
        bool ssbo = _meshes.IsSsbo(meshId);

        if (data.xyz != null && data.XyzCount > 0 && !ssbo)
        {
            fixed (float* source = data.xyz)
            {
                _meshes.Write(meshId, MeshManager.BufferXyz, data.XyzOffset,
                    (IntPtr)source, data.XyzCount * sizeof(float));
            }
        }
        // The normals, uv and flags streams have no buffer on an SSBO mesh - the
        // face records carry what the shader needs from them - so GL's SSBO
        // update path never writes them either.
        if (data.Normals != null && data.VerticesCount > 0 && !ssbo)
        {
            fixed (int* source = data.Normals)
            {
                _meshes.Write(meshId, MeshManager.BufferNormals, data.NormalsOffset,
                    (IntPtr)source, data.VerticesCount * sizeof(int));
            }
        }
        if (data.Uv != null && data.UvCount > 0 && !ssbo)
        {
            fixed (float* source = data.Uv)
            {
                _meshes.Write(meshId, MeshManager.BufferUv, data.UvOffset,
                    (IntPtr)source, data.UvCount * sizeof(float));
            }
        }
        if (data.Rgba != null && data.RgbaCount > 0)
        {
            fixed (byte* source = data.Rgba)
            {
                _meshes.Write(meshId, MeshManager.BufferRgba, data.RgbaOffset,
                    (IntPtr)source, data.RgbaCount);
            }
        }
        if (data.Flags != null && data.FlagsCount > 0 && !ssbo)
        {
            fixed (int* source = data.Flags)
            {
                _meshes.Write(meshId, MeshManager.BufferFlags, data.FlagsOffset,
                    (IntPtr)source, data.FlagsCount * sizeof(int));
            }
        }
        if (data.CustomFloats != null && data.CustomFloats.Count > 0)
        {
            fixed (float* source = data.CustomFloats.Values)
            {
                _meshes.Write(meshId, MeshManager.BufferCustomFloat, data.CustomFloats.BaseOffset,
                    (IntPtr)source, data.CustomFloats.Count * sizeof(float));
            }
        }
        if (data.CustomShorts != null && data.CustomShorts.Count > 0)
        {
            fixed (short* source = data.CustomShorts.Values)
            {
                _meshes.Write(meshId, MeshManager.BufferCustomShort, data.CustomShorts.BaseOffset,
                    (IntPtr)source, data.CustomShorts.Count * sizeof(short));
            }
        }
        if (data.CustomInts != null && data.CustomInts.Count > 0)
        {
            if (ssbo)
            {
                WritePrunedCustomInts(meshId, data.CustomInts);
            }
            else
            {
                fixed (int* source = data.CustomInts.Values)
                {
                    _meshes.Write(meshId, MeshManager.BufferCustomInt, data.CustomInts.BaseOffset,
                        (IntPtr)source, data.CustomInts.Count * sizeof(int));
                }
            }
        }
        if (data.CustomBytes != null && data.CustomBytes.Count > 0)
        {
            fixed (byte* source = data.CustomBytes.Values)
            {
                _meshes.Write(meshId, MeshManager.BufferCustomByte, data.CustomBytes.BaseOffset,
                    (IntPtr)source, data.CustomBytes.Count);
            }
        }
        // An SSBO mesh never takes indices from the data: GL draws every such
        // mesh through one shared index buffer holding the fixed quad pattern,
        // filled once at allocation, and its update path leaves indices alone.
        // The mesh here got the same pattern when it was created.
        if (data.Indices != null && data.IndicesCount > 0 && !ssbo)
        {
            fixed (int* source = data.Indices)
            {
                _meshes.Write(meshId, -1, data.IndicesOffset,
                    (IntPtr)source, data.IndicesCount * sizeof(int));
            }
        }
    }

    /// <summary>
    /// Writes the custom ints as the SSBO path stores them: two per vertex go in
    /// and only the second of each pair is kept, the first being the colormap
    /// data that the face record already carries. The destination offset halves
    /// with the stride. A part with a single int per vertex is not bound at all
    /// on this path, so there is nothing to write.
    /// </summary>
    private void WritePrunedCustomInts(int meshId, CustomMeshDataPartInt customInts)
    {
        if (customInts.InterleaveStride <= 4) return;

        int kept = customInts.Count / 2;
        if (kept <= 0) return;

        if (_prunedCustomInts.Length < kept) _prunedCustomInts = new int[kept];

        int[] values = customInts.Values;
        for (int i = 0; i < kept; i++) _prunedCustomInts[i] = values[i * 2 + 1];

        fixed (int* source = _prunedCustomInts)
        {
            _meshes.Write(meshId, MeshManager.BufferCustomInt, customInts.BaseOffset / 2,
                (IntPtr)source, kept * sizeof(int));
        }
    }

    /// <summary>
    /// The SSBO chunk path packs four vertices into one face record and stores
    /// them in the xyz slot, which CreateEmptyMesh gave StorageBufferBit usage
    /// and no vertex-attribute binding when ssbo was set. Writing it is a plain
    /// buffer write; the shader reads it through gl_VertexIndex.
    /// </summary>
    public void UpdateMeshStorageBuffer(int meshId, IntPtr data, int byteOffset, int byteSize) =>
        _meshes.Write(meshId, MeshManager.BufferXyz, byteOffset, data, byteSize);

    public IntPtr GetMappedPointer(int meshId, EnumMeshBufferPart part) => part switch
    {
        EnumMeshBufferPart.Xyz => _meshes.MappedPointer(meshId, MeshManager.BufferXyz),
        EnumMeshBufferPart.Normals => _meshes.MappedPointer(meshId, MeshManager.BufferNormals),
        EnumMeshBufferPart.Uv => _meshes.MappedPointer(meshId, MeshManager.BufferUv),
        EnumMeshBufferPart.Rgba => _meshes.MappedPointer(meshId, MeshManager.BufferRgba),
        EnumMeshBufferPart.Flags => _meshes.MappedPointer(meshId, MeshManager.BufferFlags),
        EnumMeshBufferPart.CustomFloats => _meshes.MappedPointer(meshId, MeshManager.BufferCustomFloat),
        EnumMeshBufferPart.CustomShorts => _meshes.MappedPointer(meshId, MeshManager.BufferCustomShort),
        EnumMeshBufferPart.CustomInts => _meshes.MappedPointer(meshId, MeshManager.BufferCustomInt),
        EnumMeshBufferPart.CustomBytes => _meshes.MappedPointer(meshId, MeshManager.BufferCustomByte),
        _ => _meshes.MappedPointer(meshId, -1),
    };

    public void DeleteMesh(int meshId) => _meshes.Delete(meshId, _frames);
}

/// <summary>Which draw command a native draw recorded, so the stats separate the kinds.</summary>
internal enum NativeDrawKind : byte
{
    /// <summary>The three-vertex fullscreen triangle a post pass generates in the shader.</summary>
    Fullscreen = 0,
    /// <summary>One indexed or non-indexed draw of one mesh: sky, entities, GUI quads.</summary>
    Mesh = 1,
    /// <summary>One mesh drawn with per-instance attributes: the particle pools.</summary>
    Instanced = 2,
    /// <summary>One indirect multi-draw out of the per-slot indirect ring: chunk pools and decals.</summary>
    Indirect = 3,
}

/// <summary>
/// Mesh draws on the native device API (docs/vulkan.md, decision 4:
/// "fullscreen triangle, mesh, multi-draw or instanced").
///
/// Stage 1 recorded fullscreen draws only. World systems are mesh draws, so these four entry
/// points join <see cref="VulkanDevice.DrawNativeFullscreen" /> on the same preparation
/// (<c>BeginNativeDraw</c>) and swap the draw command for the mesh manager's:
///
/// - the mesh's own vertex and index buffers, bound by <see cref="MeshManager" />, never a
///   second mesh path of this API's own;
/// - the mesh's interned vertex layout, stated on the pipeline
///   (<see cref="NativePipelineDescription.VertexLayoutId" />) and part of the pipeline key, so
///   a mesh pipeline and a fullscreen pipeline are never the same cache entry;
/// - the real mesh id threaded into <c>BindProgramSets</c>, which is what lets a chunk's
///   storage-buffer vertex fetch and an entity's <c>Animation</c> block resolve per draw
///   instead of against the fullscreen path's hardcoded 0;
/// - multi-draw through the existing per-slot indirect ring (<c>AllocateIndirect</c>), the same
///   regions every multi-draw allocates, so the ring's bookkeeping has one owner.
///
/// What pins it: NativeMeshDrawTests (the ring's use and the pipeline-key dimensions) and
/// NativeSkyTests (the first ported system, old route against native route).
/// </summary>
public sealed unsafe partial class VulkanDevice
{
    /// <summary>
    /// The topology a mesh was uploaded with, as the primitive a native pipeline rasterizes it
    /// as. A mesh carries its own <c>EnumDrawMode</c> from the tesselator (triangles for most
    /// geometry, lines for the aiming reticle, a line strip for the camera path), so a native
    /// system states it from the mesh, as GL takes it from the VAO. Triangles for a mesh that
    /// does not exist, so a caller that has already been refused a pipeline sees no surprise.
    /// </summary>
    internal PrimitiveTopology NativeMeshTopology(int meshId) =>
        GlEnums.TopologyFrom(_meshes.Get(meshId)?.DrawMode ?? Vintagestory.API.Client.EnumDrawMode.Triangles);

    /// <summary>Counts one native draw, once in the total and once in its own kind.</summary>
    private void NoteNativeDraw(NativeDrawKind kind)
    {
        VulkanStats.NoteNativeDraw();
        // The generic stated route is counted apart, so the counters below say what the dedicated
        // routes recorded (the differential tests compare the two).
        if (_nativePass is { Generic: true })
        {
            return;
        }
        _nativeDraws++;
        switch (kind)
        {
        case NativeDrawKind.Fullscreen:
            _nativeFullscreenDraws++;
            VulkanStats.NoteNativeFullscreenDraw();
            break;
        case NativeDrawKind.Mesh:
            _nativeMeshDraws++;
            VulkanStats.NoteNativeMeshDraw();
            break;
        case NativeDrawKind.Instanced:
            _nativeInstancedDraws++;
            VulkanStats.NoteNativeInstancedDraw();
            break;
        case NativeDrawKind.Indirect:
            _nativeIndirectDraws++;
            VulkanStats.NoteNativeIndirectDraw();
            break;
        }
    }

    /// <summary>
    /// One indexed draw of one mesh: the sky dome, an entity shape, a GUI quad. The OpenGL body
    /// is <c>ClientPlatformWindows.RenderMesh(MeshRef)</c>.
    /// </summary>
    internal bool DrawNativeMesh(NativePipeline pipeline, int meshId, ReadOnlySpan<NativeTexture> textures) =>
        DrawNativeMeshInstanced(pipeline, meshId, 1, textures);

    /// <summary>
    /// One indexed draw of one mesh with <paramref name="instanceCount" /> instances, the
    /// per-instance attributes coming from the mesh's own instanced bindings (the particle
    /// pools). The OpenGL body is <c>ClientPlatformWindows.RenderMeshInstanced</c>.
    /// </summary>
    internal bool DrawNativeMeshInstanced(NativePipeline pipeline, int meshId, int instanceCount,
        ReadOnlySpan<NativeTexture> textures)
    {
        if (instanceCount <= 0) return false;
        if (!NativeMeshIsDrawable(pipeline, meshId, indexed: true, out VulkanMesh? mesh)) return false;
        if (!BeginNativeDraw(pipeline, textures, meshId, out CommandBuffer commandBuffer, out VulkanFramebuffer? target))
        {
            return false;
        }

        Checkpoint(commandBuffer,
            CheckpointMarker.Draw(CheckpointKind.Draw, pipeline.ProgramId, target!.Id, meshId));
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("native mesh=" + meshId + " program=" + pipeline.ProgramId + " pass='" +
                _nativePass!.Name + "' target=" + target.Id + " indices=" + mesh!.IndexCount +
                " instances=" + instanceCount);
        }

        _meshes.Bind(commandBuffer, mesh!);
        _context.Api.CmdDrawIndexed(commandBuffer, (uint)mesh!.IndexCount, (uint)instanceCount, 0, 0, 0);
        NoteNativeDraw(instanceCount > 1 ? NativeDrawKind.Instanced : NativeDrawKind.Mesh);
        return true;
    }

    /// <summary>
    /// One non-indexed draw of a mesh's vertex buffers: <paramref name="vertexCount" /> vertices,
    /// <paramref name="instanceCount" /> instances, no index buffer. GL's glDrawArrays, for a
    /// system whose geometry carries no index array.
    /// </summary>
    internal bool DrawNativeMeshArrays(NativePipeline pipeline, int meshId, int vertexCount, int instanceCount,
        ReadOnlySpan<NativeTexture> textures)
    {
        if (vertexCount <= 0 || instanceCount <= 0) return false;
        if (!NativeMeshIsDrawable(pipeline, meshId, indexed: false, out VulkanMesh? mesh)) return false;
        if (!BeginNativeDraw(pipeline, textures, meshId, out CommandBuffer commandBuffer, out VulkanFramebuffer? target))
        {
            return false;
        }

        Checkpoint(commandBuffer,
            CheckpointMarker.Draw(CheckpointKind.Draw, pipeline.ProgramId, target!.Id, meshId));
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("native mesh arrays=" + meshId + " program=" + pipeline.ProgramId + " pass='" +
                _nativePass!.Name + "' target=" + target.Id + " vertices=" + vertexCount +
                " instances=" + instanceCount);
        }

        _meshes.Bind(commandBuffer, mesh!);
        _context.Api.CmdDraw(commandBuffer, (uint)vertexCount, (uint)instanceCount, 0, 0);
        NoteNativeDraw(instanceCount > 1 ? NativeDrawKind.Instanced : NativeDrawKind.Mesh);
        return true;
    }

    /// <summary>
    /// The multi-draw one mesh pool issues per pass - every surviving range of a chunk pool or
    /// the decal pool in one command - through the existing per-slot indirect ring. The OpenGL
    /// body is <c>ClientPlatformWindows.RenderMesh(MeshRef, int[], int[], int)</c> (glMultiDrawElements).
    ///
    /// <paramref name="indicesStarts" /> holds GL's 64-bit byte offsets as pairs of ints, as
    /// <c>MeshDataPool</c> passes them; <see cref="MeshManager.WriteIndirectCommands" /> is the
    /// one place that converts them.
    /// </summary>
    internal bool DrawNativeMeshMulti(NativePipeline pipeline, int meshId, int[] indicesStarts, int[] indicesSizes,
        int groupCount, ReadOnlySpan<NativeTexture> textures)
    {
        if (groupCount <= 0 || indicesStarts == null || indicesSizes == null) return false;
        if (!NativeMeshIsDrawable(pipeline, meshId, indexed: true, out VulkanMesh? mesh)) return false;
        if (!BeginNativeDraw(pipeline, textures, meshId, out CommandBuffer commandBuffer, out VulkanFramebuffer? target))
        {
            return false;
        }

        Checkpoint(commandBuffer,
            CheckpointMarker.Draw(CheckpointKind.DrawMulti, pipeline.ProgramId, target!.Id, meshId));

        VulkanBuffer indirect = AllocateIndirect(groupCount, out ulong indirectOffset);
        if (RenderTrace.Enabled)
        {
            RenderTrace.Write("native multidraw mesh=" + meshId + " program=" + pipeline.ProgramId + " pass='" +
                _nativePass!.Name + "' target=" + target.Id + " groups=" + groupCount +
                " indirectOffset=" + indirectOffset);
        }

        _meshes.DrawMulti(commandBuffer, meshId, indicesStarts, indicesSizes, groupCount, indirect, indirectOffset);
        NoteNativeDraw(NativeDrawKind.Indirect);
        return true;
    }

    /// <summary>
    /// Whether the mesh exists, carries what the draw needs, and is the shape the pipeline was
    /// built for. The layout check is the one that matters: a pipeline built for another mesh's
    /// layout would read attributes out of buffers that are not there, which no validation layer
    /// can see because the descriptors are all valid.
    /// </summary>
    private bool NativeMeshIsDrawable(NativePipeline pipeline, int meshId, bool indexed, out VulkanMesh? mesh)
    {
        mesh = _meshes.Get(meshId);
        if (mesh == null)
        {
            if (RenderTrace.Enabled) RenderTrace.Write("native draw skipped: no mesh " + meshId);
            return false;
        }
        if (indexed && (mesh.Indices == null || mesh.IndexCount == 0))
        {
            if (RenderTrace.Enabled) RenderTrace.Write("native draw skipped: mesh " + meshId + " has no indices");
            return false;
        }
        if (mesh.LayoutId != pipeline.Description.VertexLayoutId)
        {
            AddDiagnostic("native draw of mesh " + meshId + " (vertex layout " + mesh.LayoutId +
                ") through a pipeline built for vertex layout " + pipeline.Description.VertexLayoutId);
            return false;
        }
        return true;
    }
}
