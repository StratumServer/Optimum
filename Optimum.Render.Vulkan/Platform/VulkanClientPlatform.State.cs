using Optimum.Render.Vulkan.Core;
using Silk.NET.Vulkan;
using System;
using System.Runtime.InteropServices;
using Cairo;
using OpenTK.Audio.OpenAL;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;
using Vintagestory.Common.Convert;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native plan, Phase 1A step 4: fixed-function state, diagnostics and capability
// reporting. Each body is the device branch that used to open the same method in
// ClientPlatformWindows, moved unchanged; ClientPlatformWindows keeps the GL body.
public partial class VulkanClientPlatform
{
    // The device takes these as call arguments and keeps no queryable state, so the
    // platform remembers what the GL driver would have: the debug flag for its getter,
    // the scissor flag the runtime atlas upload reads back, and the texture last bound to
    // unit 0 for the argument-less GlGenerateTex2DMipmaps.
    private bool debugMode;
    private bool scissorEnabled;
    private int boundTexture2d;

    public override bool GlDebugMode
    {
        get
        {
            return debugMode;
        }
        set
        {
            // The device's equivalent is the validation layer, which it enables
            // itself; the supportsGlDebugMode check does not apply.
            device.DebugMode = value;
            debugMode = value;
        }
    }

    public override bool GlScissorFlagEnabled
    {
        get
        {
            return scissorEnabled;
        }
    }

    public override string GetGraphicsCardRenderer()
    {
        return device.RendererString;
    }

    /// <summary>
    /// The same facts the GL body logs, from the device. The GL extension test has no
    /// meaning here: the device already refused to initialize if it lacked what it needs,
    /// and supportsGlDebugMode/supportsPersistentMapping stay false because only GL
    /// bodies read them.
    /// </summary>
    public override void LogAndTestHardwareInfosStage2()
    {
        Logger.Notification("Graphics Backend: " + device.BackendName);
        Logger.Notification("Graphics Card Vendor: " + device.VendorString);
        Logger.Notification("Graphics Card Version: " + device.VersionString);
        Logger.Notification("Graphics Card Renderer: " + device.RendererString);
        Logger.Notification("Graphics Card ShadingLanguageVersion: " + device.ShaderVersionString);
        Logger.Notification("Max texture size: " + device.MaxTextureSize);
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // ClientPlatformWindows.LogFrameworkVersions, which is private.
            Logger.Notification("C# Framework: " + GetFrameworkInfos());
            Logger.Notification("Cairo Graphics Version: " + CairoAPI.VersionString);
        }
        Logger.Notification("OpenAL Version: " + AL.Get((ALGetString)45058));
        Logger.Notification("Zstd Version: " + ZstdNative.Version);
        CheckGlError("loghwinfo");
    }

    /// <summary>
    /// This text goes into crash reports, so it names the backend too - a crash on
    /// Vulkan reads very differently from the same crash on GL.
    /// </summary>
    public override string GetGraphicCardInfos()
    {
        return "GC Backend: " + device.BackendName + "\nGC Vendor: " + device.VendorString + "\nGC Version: " + device.VersionString + "\nGC Renderer: " + device.RendererString + "\nGC ShaderVersion: " + device.ShaderVersionString;
    }

    /// <summary>The device drains validation-layer messages instead of a GL error code.</summary>
    public override void CheckGlError(string errmsg = null)
    {
        if (GlErrorChecking)
        {
            string optimumError = device.GetError();
            if (optimumError != null)
            {
                throw new Exception(((errmsg == null) ? "" : (errmsg + " ")) + "- the graphics backend reported: " + optimumError);
            }
        }
    }

    public override void CheckGlErrorAlways(string errmsg = null)
    {
        string optimumError = device.GetError();
        if (optimumError != null)
        {
            Logger.Error(((errmsg == null) ? "" : (errmsg + " ")) + "- the graphics backend reported: " + optimumError);
        }
    }

    public override string GlGetError()
    {
        return device.GetError();
    }

    public override string GetGLShaderVersionString()
    {
        // The client parses this to decide whether a shader's #version is
        // supported, so it has to keep reading as a GLSL version number.
        return device.ShaderVersionString;
    }

    public override int GenSampler(bool linear)
    {
        return device.CreateSampler(linear);
    }

    public override void GLWireframes(bool toggle)
    {
        stated.Wireframe = toggle;
    }

    public override void GlViewport(int x, int y, int width, int height)
    {
        stated.Viewport = new Rect2D(new Offset2D(x, y), new Extent2D((uint)Math.Max(0, width), (uint)Math.Max(0, height)));
    }

    public override void GlScissor(int x, int y, int width, int height)
    {
        // Clipped to the positive quadrant (Vulkan rejects a negative offset; GL keeps the visible
        // remainder), kept as client state for native passes.
        int clippedX = Math.Max(0, x);
        int clippedY = Math.Max(0, y);
        statedScissor = new Rect2D(new Offset2D(clippedX, clippedY),
            new Extent2D((uint)Math.Max(0, width - (clippedX - x)), (uint)Math.Max(0, height - (clippedY - y))));
        stated.Scissor = statedScissor;
    }

    // The fixed state the client last stated through this platform's own virtuals. A native pass
    // that draws "with whatever the caller set" (the GUI quads) reads these - client statements,
    // recorded where they are made - never the device's GL state tracker (Phase 3b decision 3).
    private bool statedBlendOn;
    private EnumBlendMode statedBlendMode = EnumBlendMode.Standard;
    private bool statedDepthTest;
    private bool statedDepthWrite = true;
    private int statedDepthFunc = 513; // GL_LESS
    private Rect2D statedScissor;
    private bool statedCull;
    private bool statedCullBack = true;
    private float statedLineWidth = 1f;

    public override void GlScissorFlag(bool enable)
    {
        scissorEnabled = enable;
        stated.ScissorEnabled = enable;
    }

    public override void GlEnableDepthTest()
    {
        statedDepthTest = true;
        stated.DepthTest = true;
    }

    public override void GlDisableDepthTest()
    {
        statedDepthTest = false;
        stated.DepthTest = false;
    }

    public override void BindTexture2d(int texture)
    {
        // The GL body activates unit 0 first, so this binds to unit 0 too.
        stated.BindTexture(0, texture);
        // Remembered for GlGenerateTex2DMipmaps, whose GL form acts on
        // whatever is bound and so has no argument to route.
        boundTexture2d = texture;
    }

    public override void BindTextureCubeMap(int texture)
    {
        stated.BindTexture(0, texture);
    }

    public override void UnBindTextureCubeMap()
    {
        // Mirrors BindTextureCubeMap above, which binds to unit 0.
        stated.BindTexture(0, 0);
    }

    public override void GlToggleBlend(bool on, EnumBlendMode blendMode = EnumBlendMode.Standard)
    {
        statedBlendOn = on;
        statedBlendMode = blendMode;
        // GL: glEnable(GL_BLEND) and the mode's functions on every draw buffer when on,
        // glDisable alone - the functions stay - when off (ClientPlatformWindows.GlToggleBlend).
        stated.SetBlendEnabled(on);
        if (on)
        {
            stated.SetBlendMode(blendMode);
            if (blendMode == EnumBlendMode.Standard && OptimumRenderSsao)
            {
                stated.SetSlotBlend(2, 32774, 1, 0, 1, 0);
                stated.SetSlotBlend(3, 32774, 1, 0, 1, 0);
            }
        }
        // Optimum TAA (P3): the motion attachment never blends. A blended
        // motion vector averages two surfaces' displacements and belongs to
        // neither; the per-attachment override has to be re-applied after
        // every global blend change, exactly like the SSAO one above.
        if (on)
        {
            ApplyOptimumMotionBlendState();
        }
    }

    public override void GlDisableCullFace()
    {
        statedCull = false;
        stated.CullEnabled = false;
    }

    public override void GlEnableCullFace()
    {
        statedCull = true;
        stated.CullEnabled = true;
    }

    public override void GLLineWidth(float width)
    {
        statedLineWidth = width;
        stated.LineWidth = width;
    }

    /// <summary>
    /// GL_LINE_SMOOTH has no Vulkan equivalent - smooth lines there are a
    /// rasterization-mode on the pipeline, not toggleable state - and it is
    /// purely cosmetic, so the device path ignores it rather than pretending.
    /// </summary>
    public override void SmoothLines(bool on)
    {
    }

    public override void GlDepthMask(bool flag)
    {
        statedDepthWrite = flag;
        stated.DepthWrite = flag;
    }

    public override void GlDepthFunc(EnumDepthFunction depthFunc)
    {
        // EnumDepthFunction's values are the GL constants, which is the form
        // the seam takes: it cannot reference this enum, since it lives in
        // VintagestoryLib and the contracts assembly does not depend on it.
        statedDepthFunc = (int)depthFunc;
        stated.DepthCompare = GlEnums.CompareOpFrom((int)depthFunc);
    }

    public override void GlCullFaceBack()
    {
        statedCullBack = true;
        stated.CullBack = true;
    }

    public override void GlCullFaceFront()
    {
        statedCullBack = false;
        stated.CullBack = false;
    }

    public override void GlEnableStencilTest()
    {
        stated.StencilTest = true;
    }

    public override void GlDisableStencilTest()
    {
        stated.StencilTest = false;
    }

    public override void GlStencilMask(int mask)
    {
    }

    public override void GlStencilFunc(int func, int refVal, int mask)
    {
    }

    public override void GlStencilOp(int sfail, int dpfail, int dppass)
    {
    }

    /// <summary>
    /// The colour channels the client last masked off with GlColorMask (None = all written),
    /// stated for native draws: the sun's occlusion probe draws with every channel off.
    /// </summary>
    private ColorComponentFlags statedColorMaskOff;

    public override void GlColorMask(bool r, bool g, bool b, bool a)
    {
        statedColorMaskOff = (r ? 0 : ColorComponentFlags.RBit) | (g ? 0 : ColorComponentFlags.GBit) |
                             (b ? 0 : ColorComponentFlags.BBit) | (a ? 0 : ColorComponentFlags.ABit);
        stated.SetColorMask(r, g, b, a);
    }

    public override void GlClearStencil()
    {
    }

    public override void GlGenerateTex2DMipmaps()
    {
        if (boundTexture2d != 0)
        {
            device.GenerateMipmaps(boundTexture2d);
        }
    }

    public override int GlGetMaxTextureSize()
    {
        return device.MaxTextureSize;
    }
}
