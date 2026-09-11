using System;
using System.Runtime.InteropServices;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native plan, Phase 1A step 5: the leaf operations the render systems outside the
// platform used to route through the (now deleted) static device seam - ScreenManager's GUI depth clear,
// ClientMain's depth range, mesh handle deletion, ChunkRenderer's and ShaderRegistry's LOD
// bias, the framebuffer debug overlay's depth compare, SvgLoader's upload,
// InventoryItemRenderer's atlas slot clear, the OIT targets and pass state, the sun
// occlusion probe, Screenshot's readback and the backend name. Each body is the device
// branch the call site had, moved unchanged; ClientPlatformWindows holds the GL lines.
public partial class VulkanClientPlatform
{
    /// <summary>
    /// glDepthRange clamps both bounds to [0, 1], and every caller passes a range that
    /// clamps to the default (0, 20000) or restates it (0, 1). The device's viewport
    /// depth range is that default, so there is nothing to do.
    /// </summary>
    public override void SetDepthRange(float near, float far)
    {
    }

    /// <summary>glClearBuffer clamps a depth clear value to [0, 1]; the device takes the clamped value.</summary>
    public override void ClearDefaultDepth(float depth)
    {
        device.ClearDepth(Math.Clamp(depth, 0f, 1f));
    }

    /// <summary>The shared index buffer is a device mesh handle on this path.</summary>
    public override void DeleteMeshHandle(int bufferId)
    {
        device.DeleteMesh(bufferId);
    }

    /// <summary>
    /// On this path VaoId is the device's mesh handle and the per-attribute buffer fields
    /// are zero: <see cref="DeleteMesh" /> released the mesh through the device's deferred
    /// deletion before disposing the VAO, so there is nothing left to free here. VAO.Dispose
    /// can also run from a finalizer, which is why nothing is destroyed inline.
    /// </summary>
    public override void DeleteVertexArrayHandles(VAO vao)
    {
    }

    /// <summary>The device addresses each texture directly; nothing to bind or restore.</summary>
    public override void SetTextureLodBias(int[] textureIds, float bias)
    {
        for (int k = 0; k < textureIds.Length; k++)
        {
            device.SetTextureParameter(textureIds[k], OptimumGlConstants.TextureLodBias, bias);
        }
    }

    public override void SetSamplerLodBias(int samplerId, float bias)
    {
        device.SetSamplerParameter(samplerId, OptimumGlConstants.TextureLodBias, bias);
    }

    /// <summary>The device takes the texture itself rather than whatever is bound.</summary>
    public override void SetTextureDepthCompare(int textureId, int mode)
    {
        device.SetTextureParameter(textureId, OptimumGlConstants.TextureCompareMode, mode);
    }

    /// <summary>
    /// The device takes the atlas texture by id. The pixels the caller passes are all
    /// zero, so channel order does not matter.
    /// </summary>
    public override void ClearTextureRegion(int textureId, int x, int y, int width, int height, int[] pixels)
    {
        GCHandle pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            device.UploadTexture2D(textureId, 0, x, y, width, height, EnumTexturePixelFormat.Rgba, pin.AddrOfPinnedObject());
        }
        finally
        {
            pin.Free();
        }
    }

    public override int LoadTextureFromRgbaPointer(int width, int height, IntPtr pixels)
    {
        int textureId = device.CreateTexture2DRaw(width, height, OptimumGlConstants.Rgba8, pixels, 4);
        device.SetTextureParameter(textureId, OptimumGlConstants.TextureMinFilter, 9729);
        device.SetTextureParameter(textureId, OptimumGlConstants.TextureMagFilter, 9729);
        return textureId;
    }

    public override void SetProgramSamplerUnit(int programId, string samplerName, int unit)
    {
        device.SetSamplerUnit(programId, samplerName, unit);
    }

    /// <summary>
    /// The reveal target and the accumulation array, one layer per OIT weight bucket,
    /// attached layer by layer at colour attachments 3, 4 and 5. The device addresses
    /// textures directly, so there is no framebuffer or texture to bind first.
    /// </summary>
    public override void CreateOitTargets(FrameBufferRef transparent, int layers, out int revealTexture, out int accumTexture)
    {
        int width = transparent.Width;
        int height = transparent.Height;
        revealTexture = device.CreateTexture2DRaw(width, height, OptimumGlConstants.Rgba8, IntPtr.Zero, 0);
        SetOitSampling(revealTexture);

        accumTexture = device.CreateTexture2DArray(width, height, layers,
            EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba);
        SetOitSampling(accumTexture);

        device.AttachTexture(transparent.FboId, EnumFramebufferAttachment.ColorAttachment0, revealTexture, 0);
        device.AttachTexture(transparent.FboId, EnumFramebufferAttachment.ColorAttachment3, accumTexture, 0);
        device.AttachTexture(transparent.FboId, EnumFramebufferAttachment.ColorAttachment4, accumTexture, 1);
        device.AttachTexture(transparent.FboId, (EnumFramebufferAttachment)36069, accumTexture, 2);
    }

    /// <summary>
    /// Nearest filtering and clamped wrapping: both OIT targets are read back per fragment
    /// at exactly the coordinate that produced them.
    /// </summary>
    private void SetOitSampling(int textureId)
    {
        device.SetTextureParameter(textureId, OptimumGlConstants.TextureMinFilter, 9728);
        device.SetTextureParameter(textureId, OptimumGlConstants.TextureMagFilter, 9728);
        device.SetTextureParameter(textureId, OptimumGlConstants.TextureWrapS, 33071);
        device.SetTextureParameter(textureId, OptimumGlConstants.TextureWrapT, 33071);
    }

    /// <summary>
    /// Attachments 0 and 1 hold revealage and multiply down from one; 3, 4 and 5
    /// accumulate additively from zero. Attachment 2 is written by the pass itself and
    /// keeps vanilla blending.
    /// </summary>
    public override void BeginOitAccumulation(FrameBufferRef transparent)
    {
        device.SetDrawBuffers(transparent.FboId, 0x3F);
        device.SetBlendFuncSeparate(0, 774, 0, 774, 0);
        device.SetBlendFuncSeparate(1, 774, 0, 774, 0);
        device.SetBlendFuncSeparate(3, 1, 1, 1, 1);
        device.SetBlendFuncSeparate(4, 1, 1, 1, 1);
        device.SetBlendFuncSeparate(5, 1, 1, 1, 1);
        device.ClearColor(0, 1f, 1f, 1f, 1f);
        device.ClearColor(1, 1f, 1f, 1f, 1f);
        device.ClearColor(3, 0f, 0f, 0f, 0f);
        device.ClearColor(4, 0f, 0f, 0f, 0f);
        device.ClearColor(5, 0f, 0f, 0f, 0f);
    }

    /// <summary>Units 6 and 7; the device binds by unit whatever the texture's dimensionality.</summary>
    public override void BindOitTextures(int revealTexture, int accumTexture)
    {
        device.BindTexture(6, revealTexture);
        device.BindTexture(7, accumTexture);
    }

    public override int GenOcclusionQuery()
    {
        return device.CreateOcclusionQuery();
    }

    public override void BeginOcclusionQuery(int queryId)
    {
        device.BeginOcclusionQuery(queryId);
    }

    public override void EndOcclusionQuery(int queryId)
    {
        device.EndOcclusionQuery(queryId);
    }

    /// <summary>
    /// GL's form polls availability and reads the sample count only when it is there; the
    /// device's reads the same way (Phase 1B replaces the flush inside GetQueryResult).
    /// </summary>
    public override bool TryGetOcclusionQueryResult(int queryId, out int samples)
    {
        if (device.IsQueryResultAvailable(queryId))
        {
            samples = device.GetQueryResult(queryId);
            return true;
        }
        samples = 0;
        return false;
    }

    public override void DeleteOcclusionQuery(int queryId)
    {
        device.DeleteQuery(queryId);
    }

    /// <summary>
    /// The device reads back the colour target it has bound, which is the same image GL
    /// would read from the bound framebuffer and in the same orientation - the one flip
    /// happens at present, after this.
    /// </summary>
    public override void ReadDefaultFramebuffer(int x, int y, int width, int height, IntPtr destination)
    {
        device.ReadDefaultFramebuffer(x, y, width, height, destination);
    }

    public override string GraphicsBackendName => device.BackendName;
}
