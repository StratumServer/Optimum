using System;
using System.Runtime.InteropServices;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.ClientNative;
using Vintagestory.Client.NoObf;

namespace Optimum.Render.Vulkan.Platform;

// Vulkan-native plan, Phase 1A step 4: texture creation, upload and mipmapping. Each body is
// the device branch that opened the same ClientPlatformWindows method, moved unchanged,
// behind the same main-thread check.
public partial class VulkanClientPlatform
{
    private const string MainThreadOnly = "Texture uploads must happen in the main thread. We only have one OpenGL context.";

    /// <summary>
    /// Cairo hands over premultiplied BGRA bytes, which is why the GL body asks for GL_BGRA
    /// rather than GL_RGBA; CreateTexture2DRaw takes the same GL internal format token so
    /// the device makes the identical image.
    /// </summary>
    public override int LoadCairoTexture(ImageSurface surface, bool linearMag)
    {
        if (Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId)
        {
            throw new InvalidOperationException(MainThreadOnly);
        }
        int optimumTextureId = device.CreateTexture2DRaw(surface.Width, surface.Height,
            OptimumGlConstants.Bgra, surface.DataPtr, 4);
        device.SetTextureParameter(optimumTextureId, OptimumGlConstants.TextureMinFilter, 9729);
        device.SetTextureParameter(optimumTextureId, OptimumGlConstants.TextureMagFilter, linearMag ? 9729 : 9728);
        return optimumTextureId;
    }

    public override void GenTexture(RawTexture tex)
    {
        if (Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId)
        {
            throw new InvalidOperationException(MainThreadOnly);
        }
        int optimumTextureId = device.CreateTexture2D(tex.Width, tex.Height,
            tex.PixelInternalFormat, tex.PixelFormat, IntPtr.Zero, false);
        device.SetTextureParameter(optimumTextureId, OptimumGlConstants.TextureMinFilter, (int)tex.MinFilter);
        device.SetTextureParameter(optimumTextureId, OptimumGlConstants.TextureMagFilter, (int)tex.MagFilter);
        device.SetTextureParameter(optimumTextureId, OptimumGlConstants.TextureWrapS, (int)tex.WrapS);
        device.SetTextureParameter(optimumTextureId, OptimumGlConstants.TextureWrapT, (int)tex.WrapT);
        tex.TextureId = optimumTextureId;
    }

    public override void LoadOrUpdateCairoTexture(ImageSurface surface, bool linearMag, ref LoadedTexture intoTexture)
    {
        if (Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId)
        {
            throw new InvalidOperationException(MainThreadOnly);
        }
        if (intoTexture.TextureId == 0 || intoTexture.Width != surface.Width || intoTexture.Height != surface.Height)
        {
            if (intoTexture.TextureId != 0)
            {
                device.DeleteTexture(intoTexture.TextureId);
            }
            intoTexture.TextureId = device.CreateTexture2DRaw(surface.Width, surface.Height,
                OptimumGlConstants.Bgra, surface.DataPtr, 4);
            intoTexture.Width = surface.Width;
            intoTexture.Height = surface.Height;
            device.SetTextureParameter(intoTexture.TextureId, OptimumGlConstants.TextureMinFilter, 9729);
            device.SetTextureParameter(intoTexture.TextureId, OptimumGlConstants.TextureMagFilter, linearMag ? 9729 : 9728);
        }
        else
        {
            // The image is BGRA-ordered; the upload is a byte copy at four
            // bytes per pixel, which is what Rgba selects here.
            device.UploadTexture2D(intoTexture.TextureId, 0, 0, 0,
                surface.Width, surface.Height, EnumTexturePixelFormat.Rgba, surface.DataPtr);
        }
        CheckGlError("LoadOrUpdateCairoTexture");
    }

    public override unsafe void LoadIntoTexture(IBitmap srcBmp, int targetTextureId, int destX, int destY, bool generateMipmaps = false)
    {
        if (srcBmp is BitmapExternal optimumExternal)
        {
            device.UploadTexture2D(targetTextureId, 0, destX, destY,
                srcBmp.Width, srcBmp.Height, EnumTexturePixelFormat.Rgba,
                (IntPtr)optimumExternal.PixelsPtrAndLock);
        }
        else
        {
            // A managed pixel array has to be pinned before the device can
            // read it; the GL body relied on the overload doing that.
            GCHandle optimumPin = GCHandle.Alloc(srcBmp.Pixels, GCHandleType.Pinned);
            try
            {
                device.UploadTexture2D(targetTextureId, 0, destX, destY,
                    srcBmp.Width, srcBmp.Height, EnumTexturePixelFormat.Rgba,
                    optimumPin.AddrOfPinnedObject());
            }
            finally
            {
                optimumPin.Free();
            }
        }
        if (ENABLE_MIPMAPS && generateMipmaps)
        {
            BuildMipMaps(targetTextureId);
        }
    }

    /// <summary>
    /// The GL body uploads BGRA bytes into a GL_RGBA image; the device gets a BGRA-ordered
    /// image instead, which samples the same way without a per-pixel swizzle. Anisotropy is
    /// a sampler property the device sets from its own limit, so there is nothing to query.
    /// </summary>
    public override unsafe int LoadTexture(IBitmap bmp, bool linearMag = false, int clampMode = 0, bool generateMipmaps = false)
    {
        if (Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId)
        {
            throw new InvalidOperationException(MainThreadOnly);
        }
        int optimumTextureId;
        if (bmp is BitmapExternal optimumExternal)
        {
            optimumTextureId = device.CreateTexture2DRaw(bmp.Width, bmp.Height,
                OptimumGlConstants.Bgra, (IntPtr)optimumExternal.PixelsPtrAndLock, 4,
                ENABLE_MIPMAPS && generateMipmaps);
        }
        else
        {
            GCHandle optimumPin = GCHandle.Alloc(bmp.Pixels, GCHandleType.Pinned);
            try
            {
                optimumTextureId = device.CreateTexture2DRaw(bmp.Width, bmp.Height,
                    OptimumGlConstants.Bgra, optimumPin.AddrOfPinnedObject(), 4,
                    ENABLE_MIPMAPS && generateMipmaps);
            }
            finally
            {
                optimumPin.Free();
            }
        }
        switch (clampMode)
        {
        case 1:
            device.SetTextureParameter(optimumTextureId, OptimumGlConstants.TextureWrapS, 33071);
            device.SetTextureParameter(optimumTextureId, OptimumGlConstants.TextureWrapT, 33071);
            break;
        case 2:
            device.SetTextureParameter(optimumTextureId, OptimumGlConstants.TextureWrapS, 10497);
            device.SetTextureParameter(optimumTextureId, OptimumGlConstants.TextureWrapT, 10497);
            break;
        }
        device.SetTextureParameter(optimumTextureId, OptimumGlConstants.TextureMinFilter, 9729);
        device.SetTextureParameter(optimumTextureId, OptimumGlConstants.TextureMagFilter, linearMag ? 9729 : 9728);
        if (ENABLE_MIPMAPS && generateMipmaps)
        {
            BuildMipMaps(optimumTextureId);
        }
        return optimumTextureId;
    }

    public override void LoadOrUpdateTextureFromBgra_DeferMipMap(int[] rgbaPixels, bool linearMag, int clampMode, ref LoadedTexture intoTexture)
    {
        LoadOrUpdateTextureFromPixels(rgbaPixels, linearMag, clampMode, ref intoTexture, bgra: true, makeMipMap: false);
    }

    public override void LoadOrUpdateTextureFromBgra(int[] rgbaPixels, bool linearMag, int clampMode, ref LoadedTexture intoTexture)
    {
        LoadOrUpdateTextureFromPixels(rgbaPixels, linearMag, clampMode, ref intoTexture, bgra: true, makeMipMap: true);
    }

    public override void LoadOrUpdateTextureFromRgba(int[] rgbaPixels, bool linearMag, int clampMode, ref LoadedTexture intoTexture)
    {
        LoadOrUpdateTextureFromPixels(rgbaPixels, linearMag, clampMode, ref intoTexture, bgra: false, makeMipMap: true);
    }

    /// <summary>
    /// The texture atlas upload path: TextureAtlas.Upload reaches it through
    /// LoadOrUpdateTextureFromBgra_DeferMipMap, which is why it only runs once a world
    /// starts loading and never on the menu. The pixels are BGRA when <paramref name="bgra" />
    /// says so (the GL body's PixelFormat 32993) and RGBA otherwise, matching the wrappers.
    /// </summary>
    private void LoadOrUpdateTextureFromPixels(int[] rgbaPixels, bool linearMag, int clampMode, ref LoadedTexture intoTexture, bool bgra, bool makeMipMap)
    {
        if (Environment.CurrentManagedThreadId != RuntimeEnv.MainThreadId)
        {
            throw new InvalidOperationException(MainThreadOnly);
        }
        int optimumGlFormat = bgra
            ? Vintagestory.API.Config.OptimumGlConstants.Bgra
            : Vintagestory.API.Config.OptimumGlConstants.Rgba8;

        GCHandle optimumPin = GCHandle.Alloc(rgbaPixels, GCHandleType.Pinned);
        try
        {
            if (intoTexture.TextureId == 0 || intoTexture.Width * intoTexture.Height != rgbaPixels.Length)
            {
                if (intoTexture.TextureId != 0)
                {
                    device.DeleteTexture(intoTexture.TextureId);
                }
                // The mip chain has to be requested at creation; asking for
                // mipmaps afterwards on a one-level image does nothing. GL
                // can grow one at any time, which is what the deferred
                // variant relies on: it uploads with makeMipMap false and
                // the atlas manager calls BuildMipMaps later, in StageB. So
                // the chain is sized whenever mipmapping is on at all, and
                // makeMipMap only decides whether to fill it here.
                intoTexture.TextureId = device.CreateTexture2DRaw(
                    intoTexture.Width, intoTexture.Height, optimumGlFormat,
                    optimumPin.AddrOfPinnedObject(), 4, ENABLE_MIPMAPS);

                if (clampMode == 1)
                {
                    device.SetTextureParameter(intoTexture.TextureId,
                        Vintagestory.API.Config.OptimumGlConstants.TextureWrapS, 33071);
                    device.SetTextureParameter(intoTexture.TextureId,
                        Vintagestory.API.Config.OptimumGlConstants.TextureWrapT, 33071);
                }
                device.SetTextureParameter(intoTexture.TextureId,
                    Vintagestory.API.Config.OptimumGlConstants.TextureMinFilter, 9729);
                device.SetTextureParameter(intoTexture.TextureId,
                    Vintagestory.API.Config.OptimumGlConstants.TextureMagFilter, linearMag ? 9729 : 9728);

                if (makeMipMap)
                {
                    BuildMipMaps(intoTexture.TextureId);
                }
            }
            else
            {
                device.UploadTexture2D(intoTexture.TextureId, 0, 0, 0,
                    intoTexture.Width, intoTexture.Height,
                    EnumTexturePixelFormat.Rgba, optimumPin.AddrOfPinnedObject());
            }
        }
        finally
        {
            optimumPin.Free();
        }
    }

    /// <summary>
    /// The device sizes the mip chain when the image is created, so the generate carries
    /// over as it is. The two glTexParameter calls carry over as well, and they are not
    /// decoration: GL_LINEAR means "level 0 only" whatever the chain holds, so a texture
    /// that is never moved to a MIPMAP filter is never minified through one. Vulkan has no
    /// such filter, and the device turns these two into the sampler's LOD clamp.
    /// </summary>
    public override void BuildMipMaps(int textureId)
    {
        if (ENABLE_MIPMAPS)
        {
            device.GenerateMipmaps(textureId);
            device.SetTextureParameter(textureId,
                Vintagestory.API.Config.OptimumGlConstants.TextureMinFilter, 9986);
            device.SetTextureParameter(textureId,
                Vintagestory.API.Config.OptimumGlConstants.TextureMaxLevel, ClientSettings.MipMapLevel);
        }
    }

    /// <summary>
    /// The skybox cubemap. CreateTextureCube takes all six faces at once, so the per-side
    /// helper the GL body calls has no counterpart here.
    /// </summary>
    public override unsafe int Load3DTextureCube(BitmapRef[] bmps)
    {
        IntPtr[] optimumFaces = new IntPtr[6];
        int optimumSize = 0;
        for (int k = 0; k < 6; k++)
        {
            BitmapExternal optimumFace = (BitmapExternal)bmps[k];
            optimumSize = optimumFace.Width;
            optimumFaces[k] = (IntPtr)optimumFace.PixelsPtrAndLock;
        }
        // BGRA like the other bitmap uploads, so the raw overload rather than
        // the EnumTextureInternalFormat one.
        int optimumCubeId = device.CreateTextureCubeRaw(optimumSize,
            OptimumGlConstants.Bgra, optimumFaces, 4);
        device.SetTextureParameter(optimumCubeId, OptimumGlConstants.TextureMinFilter, 9729);
        device.SetTextureParameter(optimumCubeId, OptimumGlConstants.TextureMagFilter, 9729);
        device.SetTextureParameter(optimumCubeId, OptimumGlConstants.TextureWrapS, 33071);
        device.SetTextureParameter(optimumCubeId, OptimumGlConstants.TextureWrapT, 33071);
        return optimumCubeId;
    }

    public override void GLDeleteTexture(int id)
    {
        // The device defers the destruction until the GPU is finished with
        // the frame that referenced it; GL left that to the driver.
        device.DeleteTexture(id);
    }
}
