using System;
using System.Collections.Generic;
using Optimum.Render.Vulkan.Core;
using Optimum.Render.Vulkan.Graph;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>
/// World/UI separation, a foundation of the Vulkan renderer (owner's call, 2026-09-17: always on,
/// no switch). The frame is rendered HUD-less and the UI is composed onto it at the end - the
/// structure every engine with an upscaler or a frame generator needs, because the UI must never
/// enter an image a reconstruction consumes. OpenGL keeps drawing its GUI straight onto the window.
///
/// Two images, both published by slot the way <c>MotionAttachmentIndex</c> is:
/// <list type="bullet">
/// <item><description><b>SceneNoHud</b> (slot 23, render size, RGBA8): a copy of Primary colour 0
/// taken at the end of <see cref="RenderFinalComposition" /> - the one moment the composited image
/// holds the scene alone. RenderAfterFinalComposition draws selection boxes and work-item guides onto
/// that image next, and the GUI follows after the blit.</description></item>
/// <item><description><b>UI image</b> (slot 24, window size, RGBA8 + depth): everything after the
/// blit - the AfterBlit stage, the main-menu background and the whole Ortho stage - draws into it
/// instead of onto the window, over transparent black, with real coverage in alpha (see
/// <see cref="AttachmentBlend.ForUiImage" />). Its own depth, because the GUI depth-sorts itself over
/// ScreenManager's 0..20000 range.</description></item>
/// </list>
///
/// The UI scope runs from <see cref="OpenUiScope" /> (the end of <see cref="BlitPrimaryToDefault" />)
/// to <see cref="OptimumComposeUiTarget" /> (ClientMain before its Done stage; ScreenManager for the
/// menu screens). While it is open the device resolves Default to the UI image
/// (<see cref="VulkanDevice.RedirectDefaultFramebuffer" />), so no render system needs to know: what
/// has always drawn "onto the window" draws into the UI image, the atlas item renderer's
/// LoadFrameBuffer(Default) included. The compose closes the scope first and is the only draw that
/// writes the window image after the blit. <see cref="BeginFrame" /> and every framebuffer rebuild
/// close a scope a throwing GUI renderer left open.
///
/// The OpenGL bodies this mirrors are the <c>feat/dlss-g</c> ones (design step 3); there the
/// target was gated on frame generation, here it is not gated. Pinned by UiSeparationTests (GPU)
/// and ui-separation-coverage-tests.cs (placement).
/// </summary>
public partial class VulkanClientPlatform
{
    /// <summary>The HUD-less scene snapshot's slot (named in ClientPlatformWindows' parity dump).</summary>
    internal const int OptimumSceneNoHudIndex = 23;

    /// <summary>The UI image's slot (named in ClientPlatformWindows' parity dump).</summary>
    internal const int OptimumUiTargetIndex = 24;

    /// <summary>
    /// The slot holding this frame's HUD-less scene, or -1 when it could not be allocated. Consumers
    /// ask this before they index FrameBuffers.
    /// </summary>
    public int SceneNoHudFrameBufferIndex => sceneNoHudIndex;

    /// <summary>The slot holding this frame's UI image, or -1 when it could not be allocated.</summary>
    public int UiTargetFrameBufferIndex => uiTargetIndex;

    private int sceneNoHudIndex = -1;
    private int uiTargetIndex = -1;

    /// <summary>
    /// Whether the last composition really wrote the snapshot. A consumer that reads the slot when
    /// this is false reads an earlier frame's image.
    /// </summary>
    public bool SceneNoHudCaptured { get; private set; }

    /// <summary>True from <see cref="OpenUiScope" /> to <see cref="OptimumComposeUiTarget" />.</summary>
    internal bool UiScopeOpen => stated.UiImageFramebuffer > 0;

    /// <summary>The snapshot copy: the pass-through program, opaque, render size.</summary>
    private readonly NativeFullscreenPass nativeSceneNoHudCopy = new("ui-compose", Array.Empty<string>(), new[] { "uiTex" });

    /// <summary>The compose: the pass-through program under premultiplied-alpha blending.</summary>
    private readonly NativeFullscreenPass nativeUiCompose = new("ui-compose", Array.Empty<string>(), new[] { "uiTex" });

    private static AttachmentBlend[] PremultipliedColorZero() =>
        new[] { AttachmentBlend.For(true, EnumBlendMode.PremultipliedAlpha) };

    /// <summary>
    /// Allocates both images into <paramref name="list" /> and publishes their slots. A failure costs
    /// that image and nothing else: its slot stays -1, the snapshot is simply not taken, and without a
    /// UI image the GUI draws straight onto the window as it does on OpenGL.
    /// </summary>
    internal void AllocateUiSeparationTargets(List<FrameBufferRef> list, int renderWidth, int renderHeight)
    {
        CloseUiScope();
        sceneNoHudIndex = -1;
        uiTargetIndex = -1;
        SceneNoHudCaptured = false;

        try
        {
            FrameBufferRef snapshot = CreateOptimumOwnedTarget(renderWidth, renderHeight, withDepth: false);
            list[OptimumSceneNoHudIndex] = snapshot;
            sceneNoHudIndex = OptimumSceneNoHudIndex;
        }
        catch (Exception error)
        {
            Logger.Error("Optimum: no HUD-less scene snapshot: {0}", error.Message);
            list[OptimumSceneNoHudIndex] = null;
        }

        // The window's size, not the render size: the GUI has always laid itself out in window
        // pixels, and the compose puts it back over the window one for one.
        Size2i window = OptimumWindowClientSize();
        int windowWidth = window.Width;
        int windowHeight = window.Height;
        try
        {
            FrameBufferRef ui = CreateOptimumOwnedTarget(windowWidth, windowHeight, withDepth: true);
            list[OptimumUiTargetIndex] = ui;
            uiTargetIndex = OptimumUiTargetIndex;
        }
        catch (Exception error)
        {
            Logger.Error("Optimum: no separate UI image, the GUI draws onto the window: {0}", error.Message);
            list[OptimumUiTargetIndex] = null;
        }
    }

    /// <summary>
    /// A persistent single-colour target (RGBA8, nearest, clamped: both images are read one texel for
    /// one), with a depth attachment when asked. Not a transient: its contents outlive the pass that
    /// wrote them.
    /// </summary>
    private FrameBufferRef CreateOptimumOwnedTarget(int width, int height, bool withDepth)
    {
        FrameBufferRef target = new FrameBufferRef();
        target.Width = width;
        target.Height = height;
        target.FboId = device.CreateFramebuffer(width, height);
        target.ColorTextureIds = new int[1];
        target.ColorTextureIds[0] = device.CreateTexture2D(width, height,
            EnumTextureInternalFormat.Rgba8, EnumTexturePixelFormat.Rgba, IntPtr.Zero, false);
        SetupOptimumTextureSampler(target.ColorTextureIds[0], 9728, 33071);
        device.AttachTexture(target.FboId, EnumFramebufferAttachment.ColorAttachment0, target.ColorTextureIds[0], 0);
        if (withDepth)
        {
            target.DepthTextureId = device.CreateTexture2D(width, height,
                EnumTextureInternalFormat.DepthComponent32, EnumTexturePixelFormat.DepthComponent, IntPtr.Zero, false);
            SetupOptimumTextureSampler(target.DepthTextureId, 9728, 33071);
            device.AttachTexture(target.FboId, EnumFramebufferAttachment.DepthAttachment, target.DepthTextureId, 0);
        }
        StateDrawBuffers(target.FboId, 1);
        if (!device.CheckFramebufferComplete(target.FboId, out string status))
        {
            throw new Exception("framebuffer incomplete: " + status);
        }
        return target;
    }

    /// <summary>
    /// Takes the HUD-less snapshot: Primary colour 0 copied texel for texel into slot 23, one native
    /// pass. Called at the very end of <see cref="RenderFinalComposition" />, whichever route drew it.
    /// </summary>
    internal void CaptureSceneNoHud()
    {
        SceneNoHudCaptured = false;
        List<FrameBufferRef> buffers = FrameBuffers;
        if (sceneNoHudIndex < 0 || buffers == null || buffers.Count <= sceneNoHudIndex) return;
        FrameBufferRef snapshot = buffers[sceneNoHudIndex];
        FrameBufferRef primary = buffers[0];
        if (snapshot == null || primary?.ColorTextureIds == null || primary.ColorTextureIds.Length == 0) return;
        ShaderProgram copy = ShaderPrograms.UiCompose;
        if (copy == null || copy.LoadError || copy.ProgramId <= 0) return;

        int scene = primary.ColorTextureIds[0];
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        NativePipeline? pipeline = NativePipelineFor(nativeSceneNoHudCopy, copy, snapshot.FboId);
        if (pipeline != null &&
            BeginNativeBlitPass("SceneNoHud/" + OptimumSceneNoHudIndex, snapshot.FboId,
                snapshot.Width, snapshot.Height, new[] { scene }))
        {
            SceneNoHudCaptured = device.DrawNativeFullscreen(pipeline, new[]
            {
                new NativeTexture(nativeSceneNoHudCopy.Samplers[0], scene),
            });
        }
        device.EndNativePass();
        SetPassContext(outer, outerFlags);
    }

    /// <summary>
    /// Opens the UI scope: the UI image is cleared to transparent black and depth 1, and Default
    /// resolves to it until the compose. Called at the end of <see cref="BlitPrimaryToDefault" /> on
    /// every route out of it, the menu screens' included - the blit is the boundary between the world
    /// and everything ScreenManager draws after it. The client still sees the Default target bound.
    /// </summary>
    internal void OpenUiScope()
    {
        CloseUiScope();
        List<FrameBufferRef> buffers = FrameBuffers;
        if (uiTargetIndex < 0 || buffers == null || buffers.Count <= uiTargetIndex) return;
        FrameBufferRef ui = buffers[uiTargetIndex];
        if (ui == null || ui.ColorTextureIds == null || ui.ColorTextureIds.Length == 0) return;

        // Straight to the device: the image has to start empty whatever colour or depth mask the
        // client stated last, which the stated clears would honour.
        device.ClearNativeColor(ui.FboId, 0, 0f, 0f, 0f, 0f);
        device.ClearNativeDepth(ui.FboId, 1f);
        device.RedirectDefaultFramebuffer(ui.FboId);
        stated.UiImageFramebuffer = ui.FboId;
    }

    /// <summary>Ends the scope without composing: Default is the window again and Standard is Standard.</summary>
    internal void CloseUiScope()
    {
        stated.UiImageFramebuffer = 0;
        device?.RedirectDefaultFramebuffer(0);
    }

    /// <summary>
    /// Puts the UI image back over the window image: one fullscreen pass under premultiplied-alpha
    /// blending, dst = ui.rgb + dst.rgb * (1 - ui.a). The OpenGL body (<c>feat/dlss-g</c>) is the same
    /// sequence against GL state.
    ///
    /// Where it is called from, and why there: ClientMain.RenderToDefaultFramebuffer after the Ortho
    /// stage and before TriggerRenderStage(Done), where the with-HUD screenshot and the AVI writer are
    /// registered, and ScreenManager.Render for the menu screens, which never reach ClientMain. A
    /// second call in a frame finds the scope closed and returns.
    /// </summary>
    public override void OptimumComposeUiTarget()
    {
        if (!UiScopeOpen) return;
        FrameBufferRef ui = UiTargetFrameBuffer;
        // The scope ends here whatever follows, so a compose that gives up below never leaves Default
        // pointing at the UI image or the separate-alpha rule armed for the rest of the frame.
        CloseUiScope();
        // The window, bound and with its own viewport, whether or not the compose happens: the Done
        // stage and the screenshot read it.
        LoadFrameBuffer(EnumFrameBuffer.Default);
        // ScreenManager's ClearDefaultDepth landed on the UI image, so the window's depth never got
        // this frame's clear; vanilla hands the Done stage a freshly cleared one.
        device.ClearNativeDepth(PassDeclaration.DefaultFramebuffer, 1f);
        GlToggleBlend(true);

        ShaderProgram compose = ShaderPrograms.UiCompose;
        if (ui == null || ui.ColorTextureIds == null || ui.ColorTextureIds.Length == 0) return;
        if (compose == null || compose.LoadError || compose.ProgramId <= 0) return;

        int uiColor = ui.ColorTextureIds[0];
        Size2i client = OptimumWindowClientSize();
        string outer = passContext;
        PassFlags outerFlags = passContextFlags;
        NativePipeline? pipeline = NativePipelineFor(nativeUiCompose, compose, NativeDefaultTarget,
            PremultipliedColorZero());
        if (pipeline != null &&
            BeginNativeBlitPass("UiCompose/Default", NativeDefaultTarget, client.Width, client.Height, new[] { uiColor }))
        {
            device.DrawNativeFullscreen(pipeline, new[]
            {
                new NativeTexture(nativeUiCompose.Samplers[0], uiColor),
            });
        }
        device.EndNativePass();
        SetPassContext(outer, outerFlags);
    }

    /// <summary>The UI image, or null when its slot is not allocated.</summary>
    internal FrameBufferRef? UiTargetFrameBuffer
    {
        get
        {
            List<FrameBufferRef> buffers = FrameBuffers;
            if (uiTargetIndex < 0 || buffers == null || buffers.Count <= uiTargetIndex) return null;
            return buffers[uiTargetIndex];
        }
    }

    /// <summary>The HUD-less scene snapshot, or null when its slot is not allocated.</summary>
    internal FrameBufferRef? SceneNoHudFrameBuffer
    {
        get
        {
            List<FrameBufferRef> buffers = FrameBuffers;
            if (sceneNoHudIndex < 0 || buffers == null || buffers.Count <= sceneNoHudIndex) return null;
            return buffers[sceneNoHudIndex];
        }
    }
}
