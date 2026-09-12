using System;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// The one channel-order conversion the backend owes the client.
///
/// <para>The client's readback seam is <c>ClientPlatformAbstract.ReadDefaultFramebuffer</c>,
/// and the OpenGL body of it is
/// <c>glReadPixels(..., GL_BGRA, GL_UNSIGNED_BYTE, ...)</c>. Its vanilla caller,
/// <c>Screenshot.GrabScreenshot</c> - behind the in-game screenshot key and the AVI
/// recorder - hands that straight to an <c>SKBitmap</c> declared
/// <c>SKColorType.Bgra8888</c>, and Optimum's headless harness writes its PPMs from the
/// same call. The Vulkan device's default colour target is <c>R8G8B8A8_UNORM</c> and its
/// readback copies texels untouched, so without this conversion every Vulkan screenshot,
/// recording and captured frame came out with red and blue exchanged (wave-1 review,
/// 2026-09-12).</para>
///
/// <para>It lives here, one level above <c>VulkanDevice.ReadDefaultFramebuffer</c>, on
/// purpose: that method is also the backend's general "read the bound target back"
/// operation, which the GPU tests use to inspect attachments in their stored order.
/// Converting there would have changed what every one of those reads means.</para>
/// </summary>
internal static class PixelOrder
{
    /// <summary>
    /// Exchanges the first and third byte of each four-byte texel in place: R G B A
    /// becomes B G R A, and back again. Does nothing for a null pointer or a
    /// non-positive count.
    /// </summary>
    public static unsafe void SwapRedAndBlue(IntPtr texels, long count)
    {
        if (texels == IntPtr.Zero || count <= 0L) return;
        byte* bytes = (byte*)texels;
        for (long i = 0; i < count; i++)
        {
            byte* texel = bytes + i * 4;
            byte first = texel[0];
            texel[0] = texel[2];
            texel[2] = first;
        }
    }
}
