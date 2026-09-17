using System;
using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan;

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
/// Mesh draws on the native device API (docs/vulkan-native-render-systems.md, decision 4:
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
            _genericDraws++;
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
