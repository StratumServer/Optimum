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
        EnumDrawMode drawMode, bool staticDraw, bool ssbo)
    {
        var mesh = new VulkanMesh
        {
            DrawMode = drawMode,
            Persistent = !staticDraw,
            Ssbo = ssbo,
        };

        var builder = new VertexLayoutBuilder();

        // The order here is the order the GL allocator assigns attribute slots.
        AddDedicated(mesh, builder, BufferXyz, xyzSize, 3, GlFloat, normalized: false, integer: false, ssbo);
        AddDedicated(mesh, builder, BufferNormals, normalsSize, 4, GlInt2101010Rev, normalized: true, integer: false, ssbo);
        AddDedicated(mesh, builder, BufferUv, uvSize, 2, GlFloat, normalized: false, integer: false, ssbo);
        AddDedicated(mesh, builder, BufferRgba, rgbaSize, 4, GlUnsignedByte, normalized: true, integer: false, ssbo);
        AddDedicated(mesh, builder, BufferFlags, flagsSize, 1, GlInt, normalized: false, integer: true, ssbo);

        AddCustom(mesh, builder, BufferCustomFloat, customFloats?.AllocationSize * 4 ?? 0,
            customFloats?.InterleaveSizes, customFloats?.InterleaveOffsets,
            customFloats?.InterleaveStride ?? 0, GlFloat, false, false,
            customFloats?.Instanced ?? false);

        AddCustom(mesh, builder, BufferCustomShort, customShorts?.AllocationSize * 2 ?? 0,
            customShorts?.InterleaveSizes, customShorts?.InterleaveOffsets,
            customShorts?.InterleaveStride ?? 0, GlShort,
            customShorts?.Conversion == DataConversion.NormalizedFloat,
            customShorts?.Conversion == DataConversion.Integer,
            customShorts?.Instanced ?? false);

        AddCustom(mesh, builder, BufferCustomInt, customInts?.AllocationSize * 4 ?? 0,
            customInts?.InterleaveSizes, customInts?.InterleaveOffsets,
            customInts?.InterleaveStride ?? 0, GlInt,
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
        }

        mesh.Layout = builder.Build();
        mesh.LayoutId = _layouts.Intern(mesh.Layout);

        return Register(mesh);
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

    /// <summary>The persistently mapped pointer for a part, or zero.</summary>
    public IntPtr MappedPointer(int meshId, int slot)
    {
        VulkanMesh? mesh = Get(meshId);
        if (mesh == null) return IntPtr.Zero;

        if (slot < 0) return mesh.Indices?.Mapped ?? IntPtr.Zero;
        return slot < MaxBuffers ? mesh.Buffers[slot]?.Mapped ?? IntPtr.Zero : IntPtr.Zero;
    }

    /// <summary>Writes bytes into a mesh buffer through its mapping.</summary>
    public void Write(int meshId, int slot, int byteOffset, IntPtr source, int byteCount)
    {
        VulkanMesh? mesh = Get(meshId);
        if (mesh == null || source == IntPtr.Zero || byteCount <= 0) return;

        VulkanBuffer? buffer = slot < 0 ? mesh.Indices : mesh.Buffers[slot];
        if (buffer?.Mapped is null or 0) return;
        if ((ulong)(byteOffset + byteCount) > buffer.Size) return;

        System.Buffer.MemoryCopy(
            (void*)source, (void*)(buffer.Mapped + byteOffset), byteCount, byteCount);
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
        int[] indicesStarts, int[] indicesSizes, int groupCount, VulkanBuffer indirectScratch)
    {
        VulkanMesh? mesh = Get(meshId);
        if (mesh == null || groupCount <= 0) return;

        Bind(commandBuffer, mesh);

        var commands = (DrawIndexedIndirectCommand*)indirectScratch.Mapped;
        if (commands == null) return;

        int capacity = (int)(indirectScratch.Size / (ulong)sizeof(DrawIndexedIndirectCommand));
        int count = Math.Min(groupCount, capacity);

        for (int i = 0; i < count; i++)
        {
            commands[i] = new DrawIndexedIndirectCommand
            {
                IndexCount = (uint)indicesSizes[i],
                InstanceCount = 1,
                // GL takes a byte offset; Vulkan takes an index count.
                FirstIndex = (uint)(indicesStarts[i] / sizeof(int)),
                VertexOffset = 0,
                FirstInstance = 0,
            };
        }

        _context.Api.CmdDrawIndexedIndirect(commandBuffer, indirectScratch.Handle, 0, (uint)count,
            (uint)sizeof(DrawIndexedIndirectCommand));
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
