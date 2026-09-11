using System;
using System.Collections.Generic;
using Silk.NET.Vulkan;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

using Buffer = Silk.NET.Vulkan.Buffer;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// A mesh: one buffer per attribute, plus indices.
///
/// The per-attribute layout is not a choice - it is how the game allocates. Its
/// mesh allocator makes a separate GL buffer for positions, normals, UVs,
/// colours and flags, and only the four "custom" parts are interleaved. Matching
/// that exactly is what lets the existing upload paths, including the
/// persistently mapped writes the chunk tesselator does, work unchanged.
/// </summary>
internal sealed class VulkanMesh : IDisposable
{
    public VulkanBuffer?[] Buffers { get; } = new VulkanBuffer?[MeshManager.MaxBuffers];
    public VulkanBuffer? Indices { get; set; }

    public int IndexCount { get; set; }
    public EnumDrawMode DrawMode { get; set; } = EnumDrawMode.Triangles;
    public bool Persistent { get; set; }
    public bool Ssbo { get; set; }

    public VertexLayoutDescription Layout { get; set; } = VertexLayoutDescription.Empty;
    public int LayoutId { get; set; } = -1;

    /// <summary>Which buffers actually feed vertex bindings, in binding order.</summary>
    public List<int> BindingOrder { get; } = new();

    public void Dispose()
    {
        foreach (VulkanBuffer? buffer in Buffers) buffer?.Dispose();
        Array.Clear(Buffers);
        Indices?.Dispose();
        Indices = null;
    }
}

/// <summary>
/// Owns meshes and hands out integer ids, mirroring the GL VAO the game's
/// <c>MeshRef</c> wraps.
/// </summary>
internal sealed unsafe class MeshManager : IDisposable
{
    /// <summary>xyz, normals, uv, rgba, flags, then the four custom parts.</summary>
    public const int MaxBuffers = 9;

    public const int BufferXyz = 0;
    public const int BufferNormals = 1;
    public const int BufferUv = 2;
    public const int BufferRgba = 3;
    public const int BufferFlags = 4;
    public const int BufferCustomFloat = 5;
    public const int BufferCustomShort = 6;
    public const int BufferCustomInt = 7;
    public const int BufferCustomByte = 8;

    private const int GlUnsignedByte = 0x1401;
    private const int GlShort = 0x1402;
    private const int GlUnsignedShort = 0x1403;
    private const int GlUnsignedInt = 0x1405;

    /// <summary>
    /// The flags and custom-int attributes are fed to shader inputs declared
    /// <c>in int</c>. GL let an unsigned pointer feed a signed input - it
    /// reinterprets - but Vulkan requires the attribute format's numeric type to
    /// match the shader's exactly, so these are signed here.
    /// </summary>
    private const int GlInt = 0x1404;

    private const int GlFloat = 0x1406;
    private const int GlInt2101010Rev = 0x8D9F;

    private readonly VulkanContext _context;
    private readonly GlStateTracker _state;
    private readonly Interner<VertexLayoutDescription> _layouts = new();
    private readonly List<VulkanMesh?> _meshes = new();
    private readonly Stack<int> _freeIds = new();
    private bool _disposed;

    /// <summary>
    /// The layout of a pass with no vertex buffers, reserved as id 0.
    ///
    /// The fullscreen post-processing passes generate their vertices from
    /// gl_VertexIndex and bind nothing, but they still need a layout id for the
    /// pipeline key. Interning the empty layout first guarantees the id exists
    /// even before any mesh has been created.
    /// </summary>
    public const int EmptyLayoutId = 0;

    public MeshManager(VulkanContext context, GlStateTracker state)
    {
        _context = context;
        _state = state;
        _meshes.Add(null);   // 0 is never a real mesh

        int emptyId = _layouts.Intern(VertexLayoutDescription.Empty);
        if (emptyId != EmptyLayoutId)
        {
            throw new InvalidOperationException("the empty vertex layout must intern first");
        }
    }

    public VulkanMesh? Get(int id) => id > 0 && id < _meshes.Count ? _meshes[id] : null;

    public int Count
    {
        get
        {
            int live = 0;
            foreach (VulkanMesh? mesh in _meshes)
            {
                if (mesh != null) live++;
            }
            return live;
        }
    }

    /// <summary>
    /// Creates a mesh with the given per-part byte sizes, matching the shape of
    /// the game's AllocateEmptyMesh. A part with size 0 is absent, and absent
    /// parts do not consume an attribute location - which is what makes the chunk
    /// shaders' location numbering line up without any per-shader knowledge here.
    /// </summary>
    public int CreateEmpty(
        int xyzSize, int normalsSize, int uvSize, int rgbaSize, int flagsSize, int indicesSize,
        CustomMeshDataPartFloat? customFloats, CustomMeshDataPartShort? customShorts,
        CustomMeshDataPartByte? customBytes, CustomMeshDataPartInt? customInts,
        EnumDrawMode drawMode, bool staticDraw, bool ssbo, bool signedCustomShorts = false)
    {
        var mesh = new VulkanMesh
        {
            DrawMode = drawMode,
            Persistent = !staticDraw,
            Ssbo = ssbo,
        };

        var builder = new VertexLayoutBuilder();

        // The order here is the order the GL allocator assigns attribute slots.
        // With SSBO vertex fetch the xyz slot holds packed face records rather
        // than positions: one 64-byte record per four vertices, so 16 bytes per
        // vertex where a position is 12. GL sizes that buffer as xyzSize / 12 * 16
        // and so must this, or the last quarter of every pool is out of range -
        // which robust buffer access reads back as zeros, collapsing those faces
        // onto the origin and stretching their neighbours across the screen.
        int xyzSlotSize = ssbo ? xyzSize / 12 * 16 : xyzSize;
        AddDedicated(mesh, builder, BufferXyz, xyzSlotSize, 3, GlFloat, normalized: false, integer: false, ssbo);

        // Normals, uv and flags have no vertex binding on the SSBO path: their
        // contents ride in the packed face records instead, and GL's SSBO
        // allocator creates neither a buffer nor an attribute pointer for them.
        // Adding one here would push rgba off location 0, so the shader's
        // rgbaLightIn would read the uv stream - block light taken from atlas
        // coordinates, which tints the terrain by texture position.
        AddDedicated(mesh, builder, BufferNormals, ssbo ? 0 : normalsSize, 4, GlInt2101010Rev, normalized: true, integer: false, ssbo);
        AddDedicated(mesh, builder, BufferUv, ssbo ? 0 : uvSize, 2, GlFloat, normalized: false, integer: false, ssbo);
        AddDedicated(mesh, builder, BufferRgba, rgbaSize, 4, GlUnsignedByte, normalized: true, integer: false, ssbo);
        AddDedicated(mesh, builder, BufferFlags, ssbo ? 0 : flagsSize, 1, GlInt, normalized: false, integer: true, ssbo);

        AddCustom(mesh, builder, BufferCustomFloat, customFloats?.AllocationSize * 4 ?? 0,
            customFloats?.InterleaveSizes, customFloats?.InterleaveOffsets,
            customFloats?.InterleaveStride ?? 0, GlFloat, false, false,
            customFloats?.Instanced ?? false);

        // AllocateEmptyMesh/AddCustoms uses GL_UNSIGNED_SHORT for float
        // inputs, including the packed secondary UVs of topsoil. UploadMesh
        // uses GL_SHORT instead (legacy clouds rely on signed offsets).
        // Integer inputs use GL_SHORT on both paths.
        int shortType = signedCustomShorts || customShorts?.Conversion == DataConversion.Integer
            ? GlShort : GlUnsignedShort;
        AddCustom(mesh, builder, BufferCustomShort, customShorts?.AllocationSize * 2 ?? 0,
            customShorts?.InterleaveSizes, customShorts?.InterleaveOffsets,
            customShorts?.InterleaveStride ?? 0, shortType,
            customShorts?.Conversion == DataConversion.NormalizedFloat,
            customShorts?.Conversion == DataConversion.Integer,
            customShorts?.Instanced ?? false);

        (int intBytes, int[]? intSizes, int[]? intOffsets, int intStride) =
            PruneCustomInts(customInts, ssbo);

        AddCustom(mesh, builder, BufferCustomInt, intBytes, intSizes, intOffsets, intStride, GlInt,
            customInts?.Conversion == DataConversion.NormalizedFloat,
            customInts?.Conversion == DataConversion.Integer,
            customInts?.Instanced ?? false);

        AddCustom(mesh, builder, BufferCustomByte, customBytes?.AllocationSize ?? 0,
            customBytes?.InterleaveSizes, customBytes?.InterleaveOffsets,
            customBytes?.InterleaveStride ?? 0, GlUnsignedByte,
            customBytes?.Conversion == DataConversion.NormalizedFloat,
            customBytes?.Conversion == DataConversion.Integer,
            customBytes?.Instanced ?? false);

        if (indicesSize > 0)
        {
            mesh.Indices = CreateBuffer(indicesSize, BufferUsageFlags.IndexBufferBit, mesh.Persistent);
            mesh.IndexCount = indicesSize / sizeof(int);
            if (ssbo) FillQuadIndices(mesh.Indices);
        }

        mesh.Layout = builder.Build();
        mesh.LayoutId = _layouts.Intern(mesh.Layout);

        return Register(mesh);
    }

    /// <summary>
    /// The custom-int part as the SSBO path sees it. GL prunes it there: the
    /// first interleaved member is the colormap data, which the face record now
    /// carries, so it is dropped, the rest tighten onto half the stride and the
    /// buffer halves with them. A part that only ever had that one member is
    /// dropped entirely. Off the SSBO path the part passes through unchanged.
    /// </summary>
    private static (int Bytes, int[]? Sizes, int[]? Offsets, int Stride) PruneCustomInts(
        CustomMeshDataPartInt? customInts, bool ssbo)
    {
        if (customInts == null) return (0, null, null, 0);

        int bytes = customInts.AllocationSize * 4;
        int[]? sizes = customInts.InterleaveSizes;
        int[]? offsets = customInts.InterleaveOffsets;
        int stride = customInts.InterleaveStride;

        if (!ssbo) return (bytes, sizes, offsets, stride);

        if (stride <= 4 || sizes == null || sizes.Length < 2) return (0, null, null, 0);

        // Member k reads what member k - 1 used to, because dropping the first
        // one shifts every remaining offset down a slot.
        return (bytes / 2, sizes[1..], offsets?[..^1], stride / 2);
    }

    private void AddDedicated(
        VulkanMesh mesh, VertexLayoutBuilder builder, int slot, int byteSize,
        int components, int glType, bool normalized, bool integer, bool ssbo)
    {
        if (byteSize <= 0) return;

        // The SSBO path reads positions through a storage buffer rather than the
        // vertex input, so that buffer needs the extra usage bit.
        BufferUsageFlags usage = BufferUsageFlags.VertexBufferBit;
        if (ssbo && slot == BufferXyz) usage |= BufferUsageFlags.StorageBufferBit;

        mesh.Buffers[slot] = CreateBuffer(byteSize, usage, mesh.Persistent);

        // With SSBO vertex fetch the position buffer is not a vertex binding.
        if (ssbo && slot == BufferXyz) return;

        mesh.BindingOrder.Add(slot);
        builder.AddDedicated(
            VertexLayoutBuilder.FormatFor(components, glType, normalized, integer),
            VertexLayoutBuilder.SizeOf(components, glType));
    }

    private void AddCustom(
        VulkanMesh mesh, VertexLayoutBuilder builder, int slot, int byteSize,
        int[]? interleaveSizes, int[]? interleaveOffsets, int stride,
        int glType, bool normalized, bool integer, bool instanced)
    {
        // Presence of the part decides whether it takes an attribute location,
        // not how much data it currently holds. The GL allocator does the same:
        // it adds the attribute pointers whenever the part is non-null, and
        // AllocationSize returns Count, which is zero for a part that will be
        // filled after allocation. Gating on size here would shift every later
        // location and silently misfeed the shader.
        if (interleaveSizes == null || interleaveSizes.Length == 0) return;

        // Vulkan rejects a zero-sized buffer, so an empty part still gets a
        // minimal allocation to keep the binding valid.
        mesh.Buffers[slot] = CreateBuffer(
            Math.Max(byteSize, 4), BufferUsageFlags.VertexBufferBit, mesh.Persistent);
        mesh.BindingOrder.Add(slot);

        var members = new (Format, uint)[interleaveSizes.Length];
        uint packedStride = 0;
        for (int i = 0; i < interleaveSizes.Length; i++)
        {
            uint offset = interleaveOffsets != null && i < interleaveOffsets.Length
                ? (uint)interleaveOffsets[i]
                : packedStride;

            members[i] = (VertexLayoutBuilder.FormatFor(interleaveSizes[i], glType, normalized, integer), offset);
            packedStride += VertexLayoutBuilder.SizeOf(interleaveSizes[i], glType);
        }

        builder.AddInterleaved(members, stride > 0 ? (uint)stride : packedStride, instanced);
    }

    private VulkanBuffer CreateBuffer(int byteSize, BufferUsageFlags usage, bool persistent)
    {
        // A dynamic mesh is host visible and stays mapped, because the game
        // writes straight through the pointer while the GPU may still be
        // reading - the same lack of synchronisation GL allowed and the chunk
        // tesselator relies on.
        MemoryPropertyFlags properties = persistent
            ? MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit
            : MemoryPropertyFlags.DeviceLocalBit | MemoryPropertyFlags.HostVisibleBit
              | MemoryPropertyFlags.HostCoherentBit;

        try
        {
            return new VulkanBuffer(_context, (ulong)byteSize, usage | BufferUsageFlags.TransferDstBit, properties);
        }
        catch (InvalidOperationException)
        {
            // No resizable BAR: fall back to a plain host-visible allocation.
            if (!persistent) VulkanStats.NoteRebarFallback();
            return new VulkanBuffer(_context, (ulong)byteSize, usage | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit);
        }
    }

    private int Register(VulkanMesh mesh)
    {
        if (_freeIds.Count > 0)
        {
            int reused = _freeIds.Pop();
            _meshes[reused] = mesh;
            return reused;
        }

        _meshes.Add(mesh);
        return _meshes.Count - 1;
    }

    /// <summary>Whether the mesh fetches its vertices through a storage buffer.</summary>
    public bool IsSsbo(int meshId) => Get(meshId)?.Ssbo ?? false;

    /// <summary>
    /// Fills an SSBO mesh's index buffer with the fixed quad pattern.
    ///
    /// GL keeps one shared static index buffer for every SSBO mesh, written once
    /// with this pattern: each four consecutive vertices are a quad, drawn as the
    /// triangles (0,1,2) and (0,2,3). Its chunk update path never uploads indices
    /// on that route, so this fill is the only source of them here, as it is
    /// there.
    /// </summary>
    private static void FillQuadIndices(VulkanBuffer indices)
    {
        if (indices.Mapped == IntPtr.Zero) return;

        int count = (int)(indices.Size / sizeof(int));
        int* destination = (int*)indices.Mapped;
        for (int i = 0; i + 5 < count; i += 6)
        {
            int quad = i / 6 * 4;
            destination[i] = quad;
            destination[i + 1] = quad + 1;
            destination[i + 2] = quad + 2;
            destination[i + 3] = quad;
            destination[i + 4] = quad + 2;
            destination[i + 5] = quad + 3;
        }
    }

    public VulkanBuffer? BufferOf(int meshId, int slot)
    {
        VulkanMesh? mesh = Get(meshId);
        if (mesh == null) return null;
        return slot < 0 ? mesh.Indices : mesh.Buffers[slot];
    }

    public IntPtr MappedPointer(int meshId, int slot)
    {
        VulkanMesh? mesh = Get(meshId);
        if (mesh == null) return IntPtr.Zero;

        if (slot < 0) return mesh.Indices?.Mapped ?? IntPtr.Zero;
        return slot < MaxBuffers ? mesh.Buffers[slot]?.Mapped ?? IntPtr.Zero : IntPtr.Zero;
    }

    /// <summary>Writes bytes into a mesh buffer through its mapping.</summary>
    /// <summary>
    /// Copies data into one of a mesh's buffers at a byte offset.
    ///
    /// A write that cannot land - no such buffer, not mapped, or past the end -
    /// is counted and traced rather than dropped in silence. GL would raise
    /// GL_INVALID_VALUE for the same glBufferSubData; a quiet return here turned
    /// a sizing mistake into "the terrain is simply not there", with nothing in
    /// any log to say why.
    /// </summary>
    public void Write(int meshId, int slot, int byteOffset, IntPtr source, int byteCount)
    {
        if (source == IntPtr.Zero || byteCount <= 0) return;

        VulkanMesh? mesh = Get(meshId);
        VulkanBuffer? buffer = mesh == null ? null : slot < 0 ? mesh.Indices : mesh.Buffers[slot];

        string? problem =
            mesh == null ? "no such mesh" :
            buffer == null ? "mesh has no buffer in that slot" :
            buffer.Mapped == IntPtr.Zero ? "buffer is not host mapped" :
            byteOffset < 0 ? "negative offset" :
            (ulong)byteOffset + (ulong)byteCount > buffer.Size
                ? "write ends past the buffer (" + buffer.Size + " bytes)"
                : null;

        if (problem != null)
        {
            VulkanStats.NoteDroppedMeshWrite();
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write("mesh write dropped: mesh " + meshId + " slot " + slot +
                    " offset " + byteOffset + " bytes " + byteCount + ": " + problem);
            }
            return;
        }

        System.Buffer.MemoryCopy(
            (void*)source, (void*)(buffer!.Mapped + byteOffset), byteCount, byteCount);
    }

    public void Delete(int meshId, FrameRing? ring = null)
    {
        VulkanMesh? mesh = Get(meshId);
        if (mesh == null) return;

        _meshes[meshId] = null;
        _freeIds.Push(meshId);

        if (ring != null) ring.DeferDeletion(mesh);
        else mesh.Dispose();
    }

    // --------------------------------------------------------------------- draw

    /// <summary>Binds the mesh's vertex and index buffers.</summary>
    public void Bind(CommandBuffer commandBuffer, VulkanMesh mesh)
    {
        Vk api = _context.Api;

        if (mesh.BindingOrder.Count > 0)
        {
            var buffers = new Buffer[mesh.BindingOrder.Count];
            var offsets = new ulong[mesh.BindingOrder.Count];
            for (int i = 0; i < mesh.BindingOrder.Count; i++)
            {
                buffers[i] = mesh.Buffers[mesh.BindingOrder[i]]!.Handle;
            }

            fixed (Buffer* buffersPtr = buffers)
            fixed (ulong* offsetsPtr = offsets)
            {
                api.CmdBindVertexBuffers(commandBuffer, 0, (uint)buffers.Length, buffersPtr, offsetsPtr);
            }
        }

        if (mesh.Indices != null)
        {
            // Always 32-bit: the game's index arrays are int[].
            api.CmdBindIndexBuffer(commandBuffer, mesh.Indices.Handle, 0, IndexType.Uint32);
        }
    }

    public void Draw(CommandBuffer commandBuffer, int meshId, int instanceCount = 1)
    {
        VulkanMesh? mesh = Get(meshId);
        if (mesh == null || mesh.IndexCount == 0) return;

        Bind(commandBuffer, mesh);
        _context.Api.CmdDrawIndexed(commandBuffer, (uint)mesh.IndexCount, (uint)instanceCount, 0, 0, 0);
    }

    /// <summary>
    /// The multidraw the chunk renderer issues once per pool, replacing
    /// glMultiDrawElements. Records go through an indirect buffer.
    /// </summary>
    public void DrawMulti(
        CommandBuffer commandBuffer, int meshId,
        int[] indicesStarts, int[] indicesSizes, int groupCount, VulkanBuffer indirectScratch,
        ulong indirectOffset)
    {
        VulkanMesh? mesh = Get(meshId);
        if (mesh == null || groupCount <= 0) return;

        Bind(commandBuffer, mesh);

        if (indirectScratch.Mapped == IntPtr.Zero || indirectOffset >= indirectScratch.Size) return;
        var commands = (DrawIndexedIndirectCommand*)(indirectScratch.Mapped + (nint)indirectOffset);

        int capacity = (int)((indirectScratch.Size - indirectOffset) / (ulong)sizeof(DrawIndexedIndirectCommand));
        int count = Math.Min(groupCount, capacity);

        if (count < groupCount && RenderTrace.Enabled)
        {
            RenderTrace.Write("mesh indirect draw clamped: mesh " + meshId + " groupCount " + groupCount +
                " capacity " + capacity);
        }

        WriteIndirectCommands(new Span<DrawIndexedIndirectCommand>(commands, count), indicesStarts, indicesSizes);

        _context.Api.CmdDrawIndexedIndirect(commandBuffer, indirectScratch.Handle, indirectOffset, (uint)count,
            (uint)sizeof(DrawIndexedIndirectCommand));
    }

    internal static void WriteIndirectCommands(
        Span<DrawIndexedIndirectCommand> commands, ReadOnlySpan<int> indicesStarts, ReadOnlySpan<int> indicesSizes)
    {
        for (int i = 0; i < commands.Length; i++)
        {
            // MeshDataPool passes GL's 64-bit pointer array in an int[]. Each
            // offset occupies two words, unlike the tightly packed counts.
            ulong byteOffset = (uint)indicesStarts[i * 2] | ((ulong)(uint)indicesStarts[i * 2 + 1] << 32);
            commands[i] = new DrawIndexedIndirectCommand
            {
                IndexCount = (uint)indicesSizes[i],
                InstanceCount = 1,
                // GL takes a byte offset; Vulkan takes an index count.
                FirstIndex = checked((uint)(byteOffset / sizeof(int))),
                VertexOffset = 0,
                FirstInstance = 0,
            };
        }
    }

    public int LayoutIdOf(int meshId) => Get(meshId)?.LayoutId ?? -1;
    public VertexLayoutDescription LayoutOf(int layoutId) => _layouts.Get(layoutId);
    public int LayoutCount => _layouts.Count;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (VulkanMesh? mesh in _meshes) mesh?.Dispose();
        _meshes.Clear();
    }
}
