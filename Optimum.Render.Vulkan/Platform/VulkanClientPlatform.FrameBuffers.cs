using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenTK.Windowing.Desktop;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native plan, Phase 1A step 4: framebuffers and the post chain. The framebuffer
// set, the mod-facing framebuffer factory and disposal are whole overrides (moved from
// ClientPlatformWindows.SetupOptimumFrameBuffers, CreateOptimumFramebuffer and the device
// branches). The binding, clear and pass-state fragments are overrides of the virtuals the
// base's shared post-chain logic calls.
public partial class VulkanClientPlatform
{
    // ClientPlatformWindows' private slot constants; the parity dump and the post chain
    // index FrameBuffers by the same numbers.
    private const int OptimumFsrFramebufferIndex = 18;
    private const int OptimumTaaHistoryIndexA = 19;
    private const int OptimumTaaHistoryIndexB = 20;
    private const int OptimumTaaSharpenIndex = 21;
    private const int OptimumGlR32f = 0x822E;

    // GL keeps the clear colour in driver state and applies it at glClear; the device
    // takes it as an argument, so GlClearColorRgbaf records it here and the Default clear
    // passes it on. All-zero default, which is what GL_COLOR_CLEAR_VALUE starts as.
    private float clearR;
    private float clearG;
    private float clearB;
    private float clearA;

    /// <summary>
    /// Builds the same framebuffer set as the GL path, through the device.
    ///
    /// A separate body rather than routed calls inside the GL one, because that body is
    /// four hundred lines of raw GL with no seam to route through - it generates its own
    /// names and attaches its own textures. Mirroring the layout keeps every index, size
    /// and format identical, which is what the render systems assume when they index
    /// FrameBuffers by EnumFrameBuffer.
    /// </summary>
    public override List<FrameBufferRef> SetupDefaultFrameBuffers()
    {
        OptimumAdoptFrameBufferSettings();
        bool setupSsao = ClientSettings.SSAOQuality > 0;
        List<FrameBufferRef> list = new List<FrameBufferRef>(31);
        for (int i = 0; i <= 24; i++)
        {
            list.Add(null);
        }
        int shadowMapQuality = ClientSettings.ShadowMapQuality;
        float ssaaLevel = ClientSettings.SSAA;

        int width = (int)((float)((NativeWindow)window).ClientSize.X * ssaaLevel);
        int height = (int)((float)((NativeWindow)window).ClientSize.Y * ssaaLevel);
        if (width == 0 || height == 0)
        {
            return list;
        }

        bool taaRequested = OptimumTaaRequested;
        // DLSS plan, Phase 2: the motion attachment serves both temporal consumers,
        // exactly as on the GL path - our resolve, or an upscaler that replaces it.
        // The history slots and the sharpen target below stay TAA's alone.
        bool temporalRequested = OptimumTemporalRequested;
        int motionAttachmentIndex = -1;

        // Primary: depth, colour, glow, and the SSAO position/normal G-buffer.
        FrameBufferRef primary = new FrameBufferRef();
        primary.Width = width;
        primary.Height = height;
        primary.FboId = device.CreateFramebuffer(width, height);
        primary.DepthTextureId = device.CreateTexture2D(width, height,
            EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);
        SetupOptimumTextureSampler(primary.DepthTextureId, 9728, 33071);
        device.AttachTexture(primary.FboId, EnumFramebufferAttachment.DepthAttachment, primary.DepthTextureId, 0);

        int primaryAttachments = (setupSsao ? 4 : 2);
        primary.ColorTextureIds = new int[primaryAttachments];
        primary.ColorTextureIds[0] = device.CreateTexture2D(width, height,
            EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        primary.ColorTextureIds[1] = device.CreateTexture2D(width, height,
            EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        if (setupSsao)
        {
            primary.ColorTextureIds[2] = device.CreateTexture2D(width, height,
                EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
            primary.ColorTextureIds[3] = device.CreateTexture2D(width, height,
                EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        }
        // Match the GL Primary filters, including linear G-buffer sampling and
        // the white border used when SSAO projects a sample off screen.
        for (int attachment = 0; attachment < primaryAttachments; attachment++)
        {
            int textureId = primary.ColorTextureIds[attachment];
            SetupOptimumTextureSampler(textureId,
                attachment >= 2 || ssaaLevel > 1f ? 9729 : 9728, attachment >= 2 ? 33069 : 10497);
            if (attachment >= 2) device.SetTextureBorderColor(textureId, 1f, 1f, 1f, 1f);
        }
        if (temporalRequested)
        {
            // Optimum: TAA motion attachment, appended after the SSAO G-buffer
            // so every existing attachment index is unchanged. Deliberately not
            // folded into the draw-buffer mask below - it stays out of every
            // pass's output set until a writer opts in (P3+).
            try
            {
                int motionTextureId = device.CreateTexture2D(width, height,
                    EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
                int[] extendedColorIds = new int[primary.ColorTextureIds.Length + 1];
                Array.Copy(primary.ColorTextureIds, extendedColorIds, primary.ColorTextureIds.Length);
                motionAttachmentIndex = primary.ColorTextureIds.Length;
                extendedColorIds[motionAttachmentIndex] = motionTextureId;
                primary.ColorTextureIds = extendedColorIds;
            }
            catch (Exception error)
            {
                // Both consumers lose their vectors, so both stand down.
                DisableOptimumTaa("Primary motion attachment (device): " + error.Message);
                DisableOptimumUpscaler("Primary motion attachment (device): " + error.Message);
                motionAttachmentIndex = -1;
            }
        }
        for (int attachment = 0; attachment < primary.ColorTextureIds.Length; attachment++)
        {
            device.AttachTexture(primary.FboId,
                (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + attachment),
                primary.ColorTextureIds[attachment], 0);
        }
        device.SetDrawBuffers(primary.FboId, (1 << primaryAttachments) - 1);
        list[0] = primary;
        SetOptimumMotionAttachmentIndex(motionAttachmentIndex);

        // Transparent: OIT accumulation, revealage, glow. Shares Primary's depth.
        FrameBufferRef transparent = new FrameBufferRef();
        transparent.Width = width;
        transparent.Height = height;
        transparent.FboId = device.CreateFramebuffer(width, height);
        transparent.ColorTextureIds = new int[3];
        transparent.ColorTextureIds[0] = device.CreateTexture2D(width, height,
            EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        transparent.ColorTextureIds[1] = device.CreateTexture2D(width, height,
            EnumTextureInternalFormat.R16f, EnumTexturePixelFormat.Red, IntPtr.Zero, false);
        transparent.ColorTextureIds[2] = device.CreateTexture2D(width, height,
            EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        for (int attachment = 0; attachment < 3; attachment++)
        {
            SetupOptimumTextureSampler(transparent.ColorTextureIds[attachment], 9729, 10497);
            device.AttachTexture(transparent.FboId,
                (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + attachment),
                transparent.ColorTextureIds[attachment], 0);
        }
        device.AttachTexture(transparent.FboId, EnumFramebufferAttachment.DepthAttachment, primary.DepthTextureId, 0);
        device.SetDrawBuffers(transparent.FboId, 7);
        transparent.DepthTextureId = primary.DepthTextureId;
        list[1] = transparent;

        if (setupSsao)
        {
            int ssaoWidth = (int)((float)width * 0.5f);
            int ssaoHeight = (int)((float)height * 0.5f);

            FrameBufferRef ssao = new FrameBufferRef();
            ssao.Width = ssaoWidth;
            ssao.Height = ssaoHeight;
            ssao.FboId = device.CreateFramebuffer(ssaoWidth, ssaoHeight);
            ssao.ColorTextureIds = new int[2];
            // GL_RGB in the vanilla path; the device promotes it, because RGB is
            // not a guaranteed colour-attachment format in Vulkan.
            // A post-chain transient (Transient pool class); see TransientAllocator.PostChainSlots.
            ssao.ColorTextureIds[0] = device.CreateTransientTexture2DRaw(ssaoWidth, ssaoHeight, 6407, 13);
            device.AttachTexture(ssao.FboId, EnumFramebufferAttachment.ColorAttachment0, ssao.ColorTextureIds[0], 0);
            device.SetDrawBuffers(ssao.FboId, 1);

            // Rotation noise, and the sample kernel that goes with it. Same seed
            // and draw order as the GL path, so the pattern matches exactly.
            Random random = new Random(5);
            int noiseSize = 16;
            float[] noise = BuildOptimumSsaoNoise(random, noiseSize);
            GCHandle noiseHandle = GCHandle.Alloc(noise, GCHandleType.Pinned);
            // GL_RGBA32F, the same internal format the GL path allocates; GL
            // uploads GL_RGB data into it and fills alpha with 1 (see
            // BuildOptimumSsaoNoise), the device copies all four channels as given.
            ssao.ColorTextureIds[1] = device.CreateTexture2DRaw(
                noiseSize, noiseSize, 34836, noiseHandle.AddrOfPinnedObject(), 16);
            noiseHandle.Free();
            device.SetTextureParameter(ssao.ColorTextureIds[1],
                Vintagestory.API.Config.OptimumGlConstants.TextureWrapS,
                Vintagestory.API.Config.OptimumGlConstants.Repeat);
            device.SetTextureParameter(ssao.ColorTextureIds[1],
                Vintagestory.API.Config.OptimumGlConstants.TextureWrapT,
                Vintagestory.API.Config.OptimumGlConstants.Repeat);

            float[] ssaoKernel = OptimumSsaoKernel;
            for (int sample = 0; sample < 64; sample++)
            {
                Vec3f kernel = new Vec3f((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f, (float)random.NextDouble());
                kernel.Normalize();
                kernel *= (float)random.NextDouble();
                float scale = (float)sample / 64f;
                scale = GameMath.Lerp(0.1f, 1f, scale * scale);
                kernel *= scale;
                ssaoKernel[sample * 3] = kernel.X;
                ssaoKernel[sample * 3 + 1] = kernel.Y;
                ssaoKernel[sample * 3 + 2] = kernel.Z;
            }
            list[13] = ssao;

            list[14] = CreateOptimumColorTarget(ssaoWidth, ssaoHeight, EnumTextureInternalFormat.Rgba8);
            list[15] = CreateOptimumColorTarget(ssaoWidth, ssaoHeight, EnumTextureInternalFormat.Rgba8);
        }

        list[2] = CreateOptimumColorTarget(width / 2, height / 2, EnumTextureInternalFormat.Rgba8);
        list[3] = CreateOptimumColorTarget(width / 2, height / 2, EnumTextureInternalFormat.Rgba8);
        list[9] = CreateOptimumColorTarget(width / 4, height / 4, EnumTextureInternalFormat.Rgba8);
        list[8] = CreateOptimumColorTarget(width / 4, height / 4, EnumTextureInternalFormat.Rgba8);
        list[4] = CreateOptimumColorTarget(width, height, EnumTextureInternalFormat.Rgba16f);
        list[7] = CreateOptimumColorTarget(width / 2, height / 2, EnumTextureInternalFormat.Rgba16f);
        list[10] = CreateOptimumColorTarget(width, height, EnumTextureInternalFormat.Rgba16f);

        // Optimum: TAA history, render-resolution like Primary. Two slots so the
        // resolve reads last frame's parity while writing this frame's; never
        // cleared per frame (ClearFrameBuffer(Primary) only touches Primary).
        if (taaRequested)
        {
            try
            {
                list[OptimumTaaHistoryIndexA] = CreateOptimumHistoryTarget(width, height);
                list[OptimumTaaHistoryIndexB] = CreateOptimumHistoryTarget(width, height);
            }
            catch (Exception error)
            {
                DisableOptimumTaa("history targets (device): " + error.Message);
                list[OptimumTaaHistoryIndexA] = null;
                list[OptimumTaaHistoryIndexB] = null;
            }
            // Optimum TAA (P5): the sharpen target. Its own try - a sharpen
            // target that cannot be allocated costs the sharpening, not TAA,
            // so it nulls the slot instead of calling DisableOptimumTaa.
            try
            {
                list[OptimumTaaSharpenIndex] = CreateOptimumColorTarget(width, height,
                    EnumTextureInternalFormat.Rgba16f);
            }
            catch (Exception error)
            {
                Logger.Error("Optimum disabled the TAA sharpen pass: {0}", error.Message);
                list[OptimumTaaSharpenIndex] = null;
            }
        }
        OptimumAdoptTaaTargets(list, taaRequested);

        // FSR renders at a reduced scale and resolves into a native-sized target.
        if (ClientSettings.OptimumRenderScale < 1.0f)
        {
            list[OptimumFsrFramebufferIndex] = CreateOptimumColorTarget(
                ((NativeWindow)window).ClientSize.X, ((NativeWindow)window).ClientSize.Y,
                EnumTextureInternalFormat.Rgba8);
        }

        list[5] = CreateOptimumDepthTarget(width / 4, height / 4);

        // Both shadow slots always hold a FrameBufferRef, exactly as the GL path
        // does: vanilla constructs the objects unconditionally and only allocates
        // their textures when the quality setting reaches each level.
        //
        // The distinction matters because ShaderProgramBase.Use dereferences both
        // FrameBuffers[11] and FrameBuffers[12] whenever shadowmapQuality > 0,
        // and every shader including fogandlight.fsh - sky.fsh among them - takes
        // that branch. Leaving slot 12 null at quality 1 is a null reference on
        // the first sky draw, which is what it was.
        int shadowSize = Math.Max(4, shadowMapQuality + 2) * 1024;
        list[11] = shadowMapQuality > 0
            ? CreateOptimumDepthTarget(shadowSize, shadowSize)
            : CreateOptimumPlaceholderTarget(shadowSize, shadowSize);
        list[12] = shadowMapQuality > 1
            ? CreateOptimumDepthTarget(shadowSize, shadowSize)
            : CreateOptimumPlaceholderTarget(shadowSize, shadowSize);

        for (int shadow = 11; shadow <= 12; shadow++)
        {
            int textureId = list[shadow].DepthTextureId;
            if (textureId == 0) continue;
            SetupOptimumTextureSampler(textureId, 9729, 33069);
            device.SetTextureBorderColor(textureId, 1f, 1f, 1f, 1f);
            device.SetTextureParameter(textureId, OptimumGlConstants.TextureCompareMode, OptimumGlConstants.CompareRefToTexture);
        }

        // The post chain's colour textures are transients; record the slot each one serves.
        foreach (int transientSlot in Graph.TransientAllocator.PostChainSlots)
        {
            FrameBufferRef transientTarget = list[transientSlot];
            if (transientTarget == null || transientTarget.ColorTextureIds == null ||
                transientTarget.ColorTextureIds.Length == 0) continue;
            device.OptInTransient(transientTarget.ColorTextureIds[0], transientSlot);
        }

        OptimumFinishDeviceFrameBufferSetup(list);
        return list;
    }

    /// <summary>
    /// The mod-facing framebuffer factory. Same shape as the GL body: create the target,
    /// create or adopt a texture per attachment, attach it, then select the colour
    /// attachments as draw buffers.
    ///
    /// The GL body interleaves texture creation with framebuffer attachment through the
    /// bound texture unit, and there is no seam to route call-by-call. The attachment order
    /// is preserved because the draw-buffer mask is positional: bit N means
    /// ColorAttachmentN, and the shaders' output locations depend on it.
    /// </summary>
    public override FrameBufferRef CreateFramebuffer(FramebufferAttrs fbAttrs)
    {
        FrameBufferRef target = new FrameBufferRef();
        target.Width = fbAttrs.Width;
        target.Height = fbAttrs.Height;
        target.FboId = device.CreateFramebuffer(fbAttrs.Width, fbAttrs.Height);

        List<int> colorTextureIds = new List<int>();
        int drawBufferMask = 0;
        FramebufferAttrsAttachment[] attachments = fbAttrs.Attachments;
        for (int i = 0; i < attachments.Length; i++)
        {
            FramebufferAttrsAttachment attachment = attachments[i];
            RawTexture texture = attachment.Texture;
            int textureId = texture.TextureId;
            if (textureId == 0)
            {
                textureId = device.CreateTexture2D(texture.Width, texture.Height,
                    texture.PixelInternalFormat, texture.PixelFormat, IntPtr.Zero, false);
                // EnumTextureFilter and EnumTextureWrap carry the GL token values,
                // which is exactly what SetTextureParameter expects.
                device.SetTextureParameter(textureId, OptimumGlConstants.TextureMinFilter, (int)texture.MinFilter);
                device.SetTextureParameter(textureId, OptimumGlConstants.TextureMagFilter, (int)texture.MagFilter);
                device.SetTextureParameter(textureId, OptimumGlConstants.TextureWrapS, (int)texture.WrapS);
                device.SetTextureParameter(textureId, OptimumGlConstants.TextureWrapT, (int)texture.WrapT);
                texture.TextureId = textureId;
            }
            device.AttachTexture(target.FboId, attachment.AttachmentType, textureId, 0);
            if (attachment.AttachmentType == EnumFramebufferAttachment.DepthAttachment)
            {
                target.DepthTextureId = textureId;
            }
            else
            {
                colorTextureIds.Add(textureId);
                drawBufferMask |= 1 << ((int)attachment.AttachmentType - (int)EnumFramebufferAttachment.ColorAttachment0);
            }
        }

        target.ColorTextureIds = colorTextureIds.ToArray();
        device.SetDrawBuffers(target.FboId, drawBufferMask);

        string status;
        if (!device.CheckFramebufferComplete(target.FboId, out status))
        {
            throw new Exception("FBO " + fbAttrs.Name + ": " + status);
        }
        return target;
    }

    /// <summary>Mirror the GL framebuffer texture's filtering and edge policy.</summary>
    private void SetupOptimumTextureSampler(int textureId, int filter, int wrap)
    {
        device.SetTextureParameter(textureId, OptimumGlConstants.TextureMinFilter, filter);
        device.SetTextureParameter(textureId, OptimumGlConstants.TextureMagFilter, filter);
        device.SetTextureParameter(textureId, OptimumGlConstants.TextureWrapS, wrap);
        device.SetTextureParameter(textureId, OptimumGlConstants.TextureWrapT, wrap);
    }

    /// <summary>
    /// A single-colour-attachment target, as the post chain uses. Every caller is a post-chain
    /// slot, so the colour texture is a transient (Transient pool class, registered with the
    /// device's transient allocator; SetupDefaultFrameBuffers tags its slot number).
    /// </summary>
    private FrameBufferRef CreateOptimumColorTarget(int width, int height, EnumTextureInternalFormat format)
    {
        FrameBufferRef target = new FrameBufferRef();
        target.Width = width;
        target.Height = height;
        target.FboId = device.CreateFramebuffer(width, height);
        target.ColorTextureIds = new int[1];
        target.ColorTextureIds[0] = device.CreateTransientTexture2D(width, height, format, -1);
        // setupAttachment uses linear filtering and edge clamping. FXAA and
        // the reduced-resolution blur passes require fractional texel samples.
        SetupOptimumTextureSampler(target.ColorTextureIds[0], 9729, 33071);
        device.AttachTexture(target.FboId, EnumFramebufferAttachment.ColorAttachment0, target.ColorTextureIds[0], 0);
        device.SetDrawBuffers(target.FboId, 1);
        return target;
    }

    /// <summary>A depth-only target, as the shadow maps and liquid depth use.</summary>
    private FrameBufferRef CreateOptimumDepthTarget(int width, int height)
    {
        FrameBufferRef target = new FrameBufferRef();
        target.Width = width;
        target.Height = height;
        target.FboId = device.CreateFramebuffer(width, height);
        target.ColorTextureIds = new int[0];
        target.DepthTextureId = device.CreateTexture2D(width, height,
            EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);
        SetupOptimumTextureSampler(target.DepthTextureId, 9729, 33071);
        device.AttachTexture(target.FboId, EnumFramebufferAttachment.DepthAttachment, target.DepthTextureId, 0);
        device.SetDrawBuffers(target.FboId, 0);
        return target;
    }

    /// <summary>
    /// A TAA history slot - colour (RGBA16F), aux (RGBA8: glow.rg, ssao.b) and linear
    /// depth (R32F), MRT-written by the resolve pass and read back next frame. R32F has no
    /// <see cref="EnumTextureInternalFormat" /> entry, so it goes through
    /// <c>CreateTexture2DRaw</c> with the raw GL token, the same way the SSAO noise
    /// texture does above.
    /// </summary>
    private FrameBufferRef CreateOptimumHistoryTarget(int width, int height)
    {
        FrameBufferRef target = new FrameBufferRef();
        target.Width = width;
        target.Height = height;
        target.FboId = device.CreateFramebuffer(width, height);
        target.ColorTextureIds = new int[3];
        target.ColorTextureIds[0] = device.CreateTexture2D(width, height,
            EnumTextureInternalFormat.Rgba16f, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        target.ColorTextureIds[1] = device.CreateTexture2D(width, height,
            EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        target.ColorTextureIds[2] = device.CreateTexture2DRaw(width, height, OptimumGlR32f, IntPtr.Zero, 4);
        // Optimum TAA: the resolve reprojects the history by a fractional pixel
        // offset, so colour (Catmull-Rom taps) and glow (a plain bilinear fetch)
        // must filter LINEAR; sampling them NEAREST snaps the reprojection to
        // whole pixels and the history never converges. Linear depth stays
        // NEAREST - interpolating across a silhouette invents a depth that is on
        // neither surface and defeats the disocclusion test. Clamp to edge on
        // all three, matching CreateOptimumHistoryTargetGl.
        SetupOptimumTextureSampler(target.ColorTextureIds[0], 9729, 33071);
        SetupOptimumTextureSampler(target.ColorTextureIds[1], 9729, 33071);
        SetupOptimumTextureSampler(target.ColorTextureIds[2], 9728, 33071);
        for (int attachment = 0; attachment < 3; attachment++)
        {
            device.AttachTexture(target.FboId,
                (EnumFramebufferAttachment)((int)EnumFramebufferAttachment.ColorAttachment0 + attachment),
                target.ColorTextureIds[attachment], 0);
        }
        device.SetDrawBuffers(target.FboId, 7);
        if (!device.CheckFramebufferComplete(target.FboId, out string status))
        {
            throw new Exception("Optimum TAA history FBO: " + status);
        }
        return target;
    }

    /// <summary>
    /// A framebuffer slot that exists but owns nothing, for a quality level whose
    /// resources are not allocated.
    ///
    /// The GL path leaves such a slot holding a FrameBufferRef whose ids are zero; callers
    /// read its Width and Height and bind its texture id, and binding zero is a no-op
    /// there. The device treats texture 0 as unbound and substitutes its placeholder, so
    /// the same read is equally harmless here.
    /// </summary>
    private static FrameBufferRef CreateOptimumPlaceholderTarget(int width, int height)
    {
        FrameBufferRef target = new FrameBufferRef();
        target.Width = width;
        target.Height = height;
        target.ColorTextureIds = new int[0];
        return target;
    }

    public override void DisposeFrameBuffer(FrameBufferRef frameBuffer, bool disposeTextures = true)
    {
        if (frameBuffer == null)
        {
            return;
        }
        if (disposeTextures)
        {
            for (int i = 0; i < frameBuffer.ColorTextureIds.Length; i++)
            {
                GLDeleteTexture(frameBuffer.ColorTextureIds[i]);
            }
            if (frameBuffer.DepthTextureId > 0)
            {
                GLDeleteTexture(frameBuffer.DepthTextureId);
            }
        }
        // GLDeleteTexture above already routes, so only the target itself is left.
        device.DeleteFramebuffer(frameBuffer.FboId);
    }

    /// <summary>
    /// SetupDefaultFrameBuffers shares one depth texture between Primary and Transparent,
    /// so the same handle appears in more than one FrameBufferRef. Deleting it twice
    /// double-frees and makes VulkanStats.NoteTextureDeleted over-count, so every handle
    /// is deleted once.
    /// </summary>
    public override void DisposeFrameBuffers(List<FrameBufferRef> buffers)
    {
        HashSet<int> deletedTextures = new HashSet<int>();
        for (int k = 0; k < buffers.Count; k++)
        {
            if (buffers[k] != null)
            {
                device.DeleteFramebuffer(buffers[k].FboId);
                if (deletedTextures.Add(buffers[k].DepthTextureId))
                {
                    device.DeleteTexture(buffers[k].DepthTextureId);
                }
                for (int n = 0; n < buffers[k].ColorTextureIds.Length; n++)
                {
                    if (deletedTextures.Add(buffers[k].ColorTextureIds[n]))
                    {
                        device.DeleteTexture(buffers[k].ColorTextureIds[n]);
                    }
                }
                buffers[k].Disposed = true;
            }
        }
    }

    /// <summary>
    /// Swaps the colour attachment on an already-created target, which mods use to render
    /// into a texture they own.
    /// </summary>
    public override void LoadFrameBuffer(FrameBufferRef frameBuffer, int textureId)
    {
        CurrentFrameBuffer = frameBuffer;
        device.AttachTexture(frameBuffer.FboId, EnumFramebufferAttachment.ColorAttachment0, textureId, 0);
    }

    /// <summary>
    /// GL keeps a clear colour in its state; the device takes it at clear time, so this
    /// only records what the next Default clear should use.
    /// </summary>
    public override void GlClearColorRgbaf(float r, float g, float b, float a)
    {
        clearR = r;
        clearG = g;
        clearB = b;
        clearA = a;
    }

    /// <summary>
    /// FboId carries the device's render-target handle on this path, the same way
    /// VAO.VaoId carries a mesh handle, so FrameBufferRef stays the type mods already hold.
    /// </summary>
    public override void BindCurrentFrameBuffer(FrameBufferRef value)
    {
        if (value == null)
        {
            device.BindDefaultFramebuffer();
            DeclareBoundPass();
            return;
        }
        device.BindFramebuffer(value.FboId);
        device.SetViewport(0, 0, value.Width, value.Height);
        DeclareBoundPass();
    }

    public override void BindCurrentFrameBufferKeepViewport(FrameBufferRef value)
    {
        if (value == null)
        {
            device.BindDefaultFramebuffer();
            DeclareBoundPass();
            return;
        }
        device.BindFramebuffer(value.FboId);
        DeclareBoundPass();
    }

    public override void ClearBoundFrameBuffer(FrameBufferRef framebuffer, float[] clearColor, bool clearDepthBuffer, bool clearColorBuffers)
    {
        if (clearColorBuffers)
        {
            for (int k = 0; k < framebuffer.ColorTextureIds.Length; k++)
            {
                device.ClearColor(k, clearColor[0], clearColor[1], clearColor[2], clearColor[3]);
            }
        }
        if (clearDepthBuffer)
        {
            device.ClearDepth(1f);
        }
    }

    /// <summary>
    /// Same clear values per pass as the GL body. Default clears the swapchain image with
    /// the colour GlClearColorRgbaf recorded, since the device has no GL clear-colour state
    /// of its own.
    /// </summary>
    public override void ClearFrameBufferPass(EnumFrameBuffer framebuffer)
    {
        switch (framebuffer)
        {
        case EnumFrameBuffer.Default:
            device.ClearColor(0, clearR, clearG, clearB, clearA);
            device.ClearDepth(1f);
            break;
        case EnumFrameBuffer.Primary:
            device.ClearColor(0, 0f, 0f, 0f, 1f);
            device.ClearColor(1, 0f, 0f, 0f, 1f);
            if (OptimumRenderSsao)
            {
                device.ClearColor(2, 0f, 0f, 0f, 1f);
                device.ClearColor(3, 0f, 0f, 0f, 1f);
            }
            if (MotionAttachmentIndex >= 0)
            {
                // ClearColor honours the draw-buffer mask on the device too.
                // Motion is excluded until a writer opts in, so temporarily
                // enable it just as the GL branch does. Otherwise stale
                // motion/reactivity survives and can reject all TAA history.
                device.SetDrawBuffers(FrameBuffers[0].FboId, (1 << (MotionAttachmentIndex + 1)) - 1);
                device.ClearColor(MotionAttachmentIndex, 0f, 0f, 0f, 0f);
                device.SetDrawBuffers(FrameBuffers[0].FboId, (1 << MotionAttachmentIndex) - 1);
            }
            device.ClearDepth(1f);
            break;
        case EnumFrameBuffer.LiquidDepth:
        case EnumFrameBuffer.ShadowmapFar:
        case EnumFrameBuffer.ShadowmapNear:
        {
            FrameBufferRef optimumTarget = FrameBuffers[(int)framebuffer];
            device.SetViewport(0, 0, optimumTarget.Width, optimumTarget.Height);
            device.ClearDepth(1f);
            break;
        }
        case EnumFrameBuffer.Transparent:
            // Weighted-blended OIT: accumulation starts at zero, revealage at
            // one, and the third attachment is the opaque-depth copy.
            device.ClearColor(0, 0f, 0f, 0f, 0f);
            device.ClearColor(1, 1f, 0f, 0f, 0f);
            device.ClearColor(2, 0f, 0f, 0f, 0f);
            break;
        }
    }

    /// <summary>
    /// Weighted-blended OIT: accumulation adds, revealage multiplies, and the third
    /// attachment uses ordinary source-alpha blending.
    /// </summary>
    public override void ApplyTransparentPassBlendState()
    {
        device.SetDrawBuffers(FrameBuffers[1].FboId, 7);
        device.SetBlend(true, EnumBlendMode.Standard);
        device.SetBlendEquation(0, 32774);
        device.SetBlendFuncSeparate(0, 1, 1, 1, 1);
        device.SetBlendEquation(1, 32774);
        device.SetBlendFuncSeparate(1, 0, 769, 0, 769);
        device.SetBlendEquation(2, 32774);
        device.SetBlendFuncSeparate(2, 770, 771, 770, 771);
    }

    /// <summary>
    /// Selecting GL_BACK has no device equivalent: binding the default target already
    /// means the swapchain image.
    /// </summary>
    public override void SelectBackDrawBuffer()
    {
    }

    public override void SetBlendEnabled(bool enabled)
    {
        device.SetBlendEnabled(enabled);
    }

    /// <summary>
    /// The OIT merge's three state calls, spelled out rather than routed through
    /// GlToggleBlend, because that helper also overrides the SSAO attachments and this
    /// pass deliberately sets only the global mode.
    /// </summary>
    public override void ApplyTransparentMergeBlendState()
    {
        device.SetDepthTest(false);
        device.SetBlend(true, EnumBlendMode.Standard);
        device.SetBlendFuncSeparate(0, 770, 771, 770, 771);
    }

    /// <summary>
    /// The SSAO rotation noise as RGBA float texels, drawing from <paramref name="random" />
    /// in the GL path's order (two doubles per texel) so the sample kernel drawn after it
    /// matches too.
    ///
    /// Alpha is 1, not 0. The GL path allocates GL_RGBA32F and uploads GL_RGB pixel data;
    /// GL's pixel transfer fills the missing alpha with 1, so the texture holds 1.0 in every
    /// texel (the parity dump reads 1.0 on OpenGL). The device takes the four channels
    /// verbatim, and a padding 0 here left SSAO colour1 alpha at 0.0 on Vulkan.
    /// </summary>
    internal static float[] BuildOptimumSsaoNoise(Random random, int noiseSize)
    {
        float[] noise = new float[noiseSize * noiseSize * 4];
        Vec3f direction = new Vec3f();
        for (int texel = 0; texel < noiseSize * noiseSize; texel++)
        {
            direction.Set((float)random.NextDouble() * 2f - 1f, (float)random.NextDouble() * 2f - 1f, 0f).Normalize();
            noise[texel * 4] = direction.X;
            noise[texel * 4 + 1] = direction.Y;
            noise[texel * 4 + 2] = direction.Z;
            noise[texel * 4 + 3] = 1f;
        }
        return noise;
    }

    public override void ClearSsaoTarget()
    {
        device.ClearColor(0, 1f, 1f, 1f, 1f);
    }

    /// <summary>
    /// The device takes the target explicitly (the bound one, Primary here), and its mask
    /// is positional - bit N selects ColorAttachmentN.
    /// </summary>
    public override void BeginFinalCompositionDrawBuffers()
    {
        DeclareFinalCompositionPass();
        device.SetDrawBuffers(CurrentFrameBuffer != null ? CurrentFrameBuffer.FboId : 0, 1);
        device.SetDepthTest(false);
    }

    public override void RestoreWorldDrawBuffers(bool ssaoAttachments)
    {
        // The attachment-subset pass ends before Primary 1 rejoins the draw buffers.
        device.EndPass();
        if (ssaoAttachments)
        {
            device.SetDrawBuffers(CurrentFrameBuffer != null ? CurrentFrameBuffer.FboId : 0, 15);
        }
        else
        {
            device.SetDrawBuffers(CurrentFrameBuffer != null ? CurrentFrameBuffer.FboId : 0, 3);
        }
    }
}
