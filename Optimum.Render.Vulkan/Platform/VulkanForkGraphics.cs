using System;
using Vintagestory.API.Client;
using Vintagestory.API.Config;

namespace Optimum.Render.Vulkan.Platform;

/// <summary>
/// The <see cref="OptimumForkGraphics" /> the Vulkan platform publishes while its graphics
/// are up: the handful of operations the forked VSEssentials and VSSurvivalMod renderers
/// need beyond IRenderAPI, forwarded unchanged to the platform's device. The forks
/// reference only the API and the contracts, so this is how they reach the device until
/// Phase 5 ports them.
/// </summary>
internal sealed class VulkanForkGraphics : OptimumForkGraphics
{
    private readonly VulkanDevice device;

    public VulkanForkGraphics(VulkanDevice device)
    {
        this.device = device;
    }

    public override int CreateTexture2DRaw(int width, int height, int glInternalFormat, IntPtr pixels, int bytesPerPixel) =>
        device.CreateTexture2DRaw(width, height, glInternalFormat, pixels, bytesPerPixel);

    public override int CreateTexture2DArray(int width, int height, int layers,
        EnumTextureInternalFormat internalFormat, EnumTexturePixelFormat pixelFormat) =>
        device.CreateTexture2DArray(width, height, layers, internalFormat, pixelFormat);

    public override void UploadTexture2DArrayLayer(int textureId, int layer, int x, int y, int width, int height, IntPtr pixels) =>
        device.UploadTexture2DArrayLayer(textureId, layer, x, y, width, height, pixels);

    public override void UploadTexture2DNormalizedShorts(int textureId, int level, int x, int y, int width, int height, short[] pixels) =>
        device.UploadTexture2DNormalizedShorts(textureId, level, x, y, width, height, pixels);

    public override void SetTextureParameter(int textureId, int parameterName, int value) =>
        device.SetTextureParameter(textureId, parameterName, value);

    public override void BindTexture(int unit, int textureId) => device.BindTexture(unit, textureId);

    public override void DeleteTexture(int textureId) => device.DeleteTexture(textureId);

    public override int CreateFramebuffer(int width, int height) => device.CreateFramebuffer(width, height);

    public override void AttachTexture(int framebufferId, EnumFramebufferAttachment attachment, int textureId, int layer) =>
        device.AttachTexture(framebufferId, attachment, textureId, layer);

    public override void SetDrawBuffers(int framebufferId, int attachmentMask) =>
        device.SetDrawBuffers(framebufferId, attachmentMask);

    public override void BindFramebuffer(int framebufferId) => device.BindFramebuffer(framebufferId);

    public override void BindDefaultFramebuffer() => device.BindDefaultFramebuffer();

    public override void DeleteFramebuffer(int framebufferId) => device.DeleteFramebuffer(framebufferId);

    public override void SetViewport(int x, int y, int width, int height) => device.SetViewport(x, y, width, height);

    public override void SetDepthTest(bool enabled) => device.SetDepthTest(enabled);

    public override void SetBlendEnabled(bool enabled) => device.SetBlendEnabled(enabled);

    public override int GetUniformLocation(int programId, string name) => device.GetUniformLocation(programId, name);

    public override void SetUniformArray3(int programId, int location, int count, float[] values) =>
        device.SetUniformArray3(programId, location, count, values);
}
