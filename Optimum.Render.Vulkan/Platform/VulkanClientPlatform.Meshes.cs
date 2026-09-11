using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native plan, Phase 1A step 4: mesh allocation, upload, update and draw. Each body
// is the device branch that opened the same ClientPlatformWindows method, moved unchanged,
// plus the API-neutral lines around it the base method ran on both paths (draw-call
// accounting, argument checks, the SSBO face packing).
//
// On this path VAO.VaoId is the device's mesh handle rather than a GL vertex-array name.
// Reusing the field keeps MeshRef - which is public API that mods hold - unchanged.
public partial class VulkanClientPlatform
{
    private bool debugDrawCalls;

    private readonly List<string> drawCallStacks = new List<string>();

    [ThreadStatic]
    private static FaceData[] facedataBuffer;

    public override bool DebugDrawCalls
    {
        get
        {
            return debugDrawCalls;
        }
        set
        {
            debugDrawCalls = value;
            if (!value)
            {
                Logger.Notification("Call stacks:");
                int num = 0;
                foreach (string drawCallStack in drawCallStacks)
                {
                    Logger.Notification("{0}: {1}", num++, drawCallStack.Substring(0, 600));
                }
            }
            drawCallStacks.Clear();
        }
    }

    public override void RenderMesh(MeshRef modelRef)
    {
        RuntimeStats.drawCallsCount++;
        if (debugDrawCalls)
        {
            drawCallStacks.Add(Environment.StackTrace);
        }
        VAO vAO = (VAO)modelRef;
        if (vAO.VaoId == 0 || vAO.Disposed)
        {
            if (vAO.VaoId == 0)
            {
                throw new ArgumentException("Fatal: Trying to render an uninitialized mesh");
            }
            throw new ArgumentException("Fatal: Trying to render a disposed mesh");
        }
        device.DrawMesh(vAO.VaoId);
    }

    public override void RenderFullscreenTriangle(MeshRef modelRef)
    {
        RuntimeStats.drawCallsCount++;
        // The post passes generate their three vertices in the shader, so the
        // mesh carries no buffers and none are bound.
        device.DrawFullscreenTriangle();
    }

    public override void RenderMesh(MeshRef modelRef, int[] indices, int[] indicesSizes, int groupCount, bool useSSBOs)
    {
        RuntimeStats.drawCallsCount++;
        VAO vAO = (VAO)modelRef;
        // The chunk renderer's one multidraw per pool. GL takes byte offsets
        // into the index buffer; the device converts them to index counts and
        // issues a single indirect draw.
        device.DrawMeshMulti(vAO.VaoId, indices, indicesSizes, groupCount, useSSBOs);
    }

    public override void RenderMeshInstanced(MeshRef modelRef, int quantity = 1)
    {
        RuntimeStats.drawCallsCount++;
        VAO vAO = (VAO)modelRef;
        device.DrawMeshInstanced(vAO.VaoId, quantity);
    }

    public override void UpdateMesh(MeshRef modelRef, MeshData data)
    {
        VAO vAO = (VAO)modelRef;
        device.UpdateMesh(vAO.VaoId, data);
    }

    /// <summary>
    /// The device allocates the per-attribute buffers and derives the vertex layout from
    /// which parts are present, matching the slot ordering the GL body assigns.
    /// </summary>
    public override MeshRef AllocateEmptyMesh(int xyzSize, int normalsSize, int uvSize, int rgbaSize, int flagsSize, int indicesSize, CustomMeshDataPartFloat customFloats, CustomMeshDataPartShort customShorts, CustomMeshDataPartByte customBytes, CustomMeshDataPartInt customInts, EnumDrawMode drawMode = EnumDrawMode.Triangles, bool staticDraw = true)
    {
        VAO vAO = new VAO();
        vAO.VaoId = device.CreateEmptyMesh(
            xyzSize, normalsSize, uvSize, rgbaSize, flagsSize, indicesSize,
            customFloats, customShorts, customBytes, customInts,
            drawMode, staticDraw, ssbo: false);
        vAO.IndicesCount = indicesSize;
        vAO.drawMode = DrawModeToPrimiteType(drawMode);
        vAO.Persistent = !staticDraw;
        return vAO;
    }

    /// <summary>The device builds the buffers and returns its own handle.</summary>
    public override MeshRef UploadMesh(MeshData data)
    {
        VAO optimumUploadVao = new VAO();
        optimumUploadVao.VaoId = device.CreateMesh(data, true);
        optimumUploadVao.IndicesCount = data.IndicesCount;
        optimumUploadVao.drawMode = DrawModeToPrimiteType(data.mode);
        return optimumUploadVao;
    }

    public override void DeleteMesh(MeshRef modelref)
    {
        if (modelref != null)
        {
            // The GL body's shape: VAO.Dispose reaches DeleteVertexArrayHandles, which
            // releases the device mesh (deferred until the GPU is done with the frames
            // that drew it). Releasing it here as well would free the id twice.
            ((VAO)modelref).Dispose();
        }
    }

    /// <summary>
    /// The face packing is the GL body's, unchanged; the face records go to the same
    /// storage buffer, reached through the device's mesh handle instead of the buffer name.
    /// </summary>
    public override void UpdateSSBOMesh(MeshRef modelRef, MeshData data)
    {
        if (data.xyz == null)
        {
            return;
        }
        VAO vAO = (VAO)modelRef;
        int verticesCount = data.VerticesCount;
        if (facedataBuffer == null || facedataBuffer.Length < verticesCount / 4)
        {
            facedataBuffer = new FaceData[verticesCount / 4];
        }
        float[] xyz = data.xyz;
        float[] uv = data.Uv;
        int[] flags = data.Flags;
        int[] array = ((data.CustomInts != null && data.CustomInts.Count > 0) ? data.CustomInts.Values : null);
        int num = ((data.CustomInts == null || data.CustomInts.Count <= 0) ? 1 : (data.CustomInts.InterleaveStride / 4));
        FaceData[] array2 = facedataBuffer;
        for (int i = 0; i < verticesCount; i += 4)
        {
            float num2 = uv[i * 2];
            float num3 = uv[i * 2 + 1];
            float num4 = uv[i * 2 + 3];
            float num5 = uv[i * 2 + 4];
            float num6 = uv[i * 2 + 5];
            if (num2 < -1.5E-05f || num2 > 1.000015f || num3 < -1.5E-05f || num3 > 1.000015f)
            {
                num2 = 0f;
                num3 = 0f;
            }
            if (num5 < -1.5E-05f || num5 > 1.000015f || num6 < -1.5E-05f || num6 > 1.000015f)
            {
                num5 = 0f;
                num6 = 0f;
            }
            bool rotateUV;
            if (rotateUV = num3 == num4)
            {
                float num7 = uv[i * 2 + 2];
                if (num5 != num7)
                {
                    rotateUV = false;
                }
            }
            array2[i / 4] = new FaceData(xyz, i * 3, num2, num3, num5 - num2, num6 - num3, flags, i, (array != null) ? array[i * num] : 0, rotateUV);
        }
        int num8 = data.XyzOffset / 12 * 16;
        // Everything the mesh carries as ordinary vertex data goes first, the
        // same call UpdateMesh makes. The packed face records follow, because
        // they occupy the xyz slot and have to be what remains there.
        device.UpdateMesh(vAO.VaoId, data);

        GCHandle optimumFacePin = GCHandle.Alloc(facedataBuffer, GCHandleType.Pinned);
        try
        {
            device.UpdateMeshStorageBuffer(vAO.VaoId,
                optimumFacePin.AddrOfPinnedObject(), num8, 16 * verticesCount);
        }
        finally
        {
            optimumFacePin.Free();
        }
    }

    public override MeshRef AllocateEmptySSBOMesh(int xyzSize, int normalsSize, int uvSize, int rgbaSize, int flagsSize, int indicesSize, CustomMeshDataPartFloat customFloats, CustomMeshDataPartShort customShorts, CustomMeshDataPartByte customBytes, CustomMeshDataPartInt customInts, EnumDrawMode drawMode = EnumDrawMode.Triangles, bool staticDraw = true)
    {
        VAO vAO = new VAO();
        vAO.VaoId = device.CreateEmptyMesh(
            xyzSize, normalsSize, uvSize, rgbaSize, flagsSize, indicesSize,
            customFloats, customShorts, customBytes, customInts,
            drawMode, staticDraw, ssbo: true);
        vAO.IndicesCount = indicesSize;
        vAO.drawMode = DrawModeToPrimiteType(drawMode);
        vAO.Persistent = !staticDraw;
        return vAO;
    }

    /// <summary>ClientPlatformWindows.DrawModeToPrimiteType, which is private.</summary>
    private static PrimitiveType DrawModeToPrimiteType(EnumDrawMode drawmode)
    {
        return (PrimitiveType)(drawmode switch
        {
            EnumDrawMode.Lines => 1,
            EnumDrawMode.LineStrip => 3,
            _ => 4,
        });
    }
}
