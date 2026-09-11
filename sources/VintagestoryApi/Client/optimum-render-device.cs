using System;
using Vintagestory.API.Client;

namespace Vintagestory.API.Config;

/// <summary>
/// One texture's level-0 contents in the representation both backends agree on
/// for the parity dump: <c>glGetTexImage(GL_RGBA, GL_UNSIGNED_BYTE)</c> for 8-bit
/// unsigned-normalised formats, <c>glGetTexImage(GL_RGBA, GL_FLOAT)</c> for every
/// other colour format and <c>glGetTexImage(GL_DEPTH_COMPONENT, GL_FLOAT)</c> for
/// depth. A plain data holder, so the patched client can fill it without any
/// generated helper types.
/// </summary>
public sealed class OptimumTextureReadback
{
    /// <summary>The sized GL internal format the texture was created with (Vulkan) or reports (GL).</summary>
    public int GlInternalFormat;

    public int Width;

    public int Height;

    /// <summary>RGBA8, four bytes per texel, rows bottom-up. Null for float and depth formats.</summary>
    public byte[] Bytes;

    /// <summary>
    /// Four floats per texel (RGBA) for colour formats, one per texel for depth,
    /// rows bottom-up. Null for 8-bit unsigned-normalised formats.
    /// </summary>
    public float[] Floats;
}

/// <summary>
/// Per-attachment parity dump, shared by both backends so the two write the same
/// file names in the same encodings.
///
/// Off unless <c>OPTIMUM_PARITY_DUMP</c> names an absolute directory. With it
/// unset the client pays one static bool check per frame and makes no GL or
/// Vulkan call. With it set, the platform counts frames rendered while the player
/// is in the world (the first is frame 0) and on frame <c>OPTIMUM_PARITY_FRAME</c>
/// (default 0), after the post chain and the final FSR or plain blit and before
/// presentation, dumps every attachment of every framebuffer slot once.
///
/// Encodings, rows always bottom-up in GL order (the first row in the file is GL
/// row 0, so a PPM opened in an image viewer appears upside down; PFM is
/// bottom-up by convention already):
/// - 8-bit unsigned-normalised formats: binary PPM (P6) of RGB, plus a PGM (P5)
///   of alpha when the format has alpha. Single-channel 8-bit formats are one PGM.
/// - Every other colour format and depth: PFM, little-endian float32, negative
///   scale. Three and four channel formats write "PF" (RGB) and, with alpha, a
///   second "Pf" file with the extension <c>alpha.pfm</c>; one-channel formats
///   and depth write "Pf".
/// </summary>
public static class OptimumParityDump
{
    /// <summary>The absolute dump directory, or null when the dump is off.</summary>
    public static readonly string Directory = ResolveDirectory();

    /// <summary>True when <c>OPTIMUM_PARITY_DUMP</c> names an absolute directory.</summary>
    public static readonly bool Enabled = Directory != null;

    /// <summary>The in-world frame to dump, counted from 0.</summary>
    public static readonly long Frame = ResolveFrame();

    /// <summary>
    /// The one file-name format both backends use:
    /// <c>&lt;slotIndex&gt;-&lt;slotName&gt;-&lt;color&lt;i&gt;|depth&gt;-&lt;format&gt;.&lt;ext&gt;</c>.
    /// </summary>
    public const string FileNameFormat = "{0}-{1}-{2}-{3}.{4}";

    private static string ResolveDirectory()
    {
        string value = Environment.GetEnvironmentVariable("OPTIMUM_PARITY_DUMP");
        if (string.IsNullOrWhiteSpace(value) || !System.IO.Path.IsPathRooted(value)) return null;
        return System.IO.Path.GetFullPath(value);
    }

    private static long ResolveFrame()
    {
        string value = Environment.GetEnvironmentVariable("OPTIMUM_PARITY_FRAME");
        return long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out long frame) && frame >= 0 ? frame : 0;
    }

    public static string FileName(int slotIndex, string slotName, string attachment, string format, string extension)
    {
        return string.Format(System.Globalization.CultureInfo.InvariantCulture, FileNameFormat,
            slotIndex, slotName, attachment, format, extension);
    }

    /// <summary>
    /// The backend-neutral format name. Aliases that denote the same storage
    /// collapse to one name (GL reports GL_RGB8 for a texture requested as
    /// GL_RGB; the Vulkan device promotes it to RGBA8 storage but reports the
    /// requested token), so the two dumps pair by file name.
    /// </summary>
    public static string FormatName(int glInternalFormat)
    {
        switch (glInternalFormat)
        {
            case 0x8058: case 0x1908: return "rgba8";      // GL_RGBA8, GL_RGBA
            case 0x8051: case 0x1907: return "rgb8";       // GL_RGB8, GL_RGB
            case 0x8229: case 0x1903: return "r8";         // GL_R8, GL_RED
            case 0x881A: return "rgba16f";
            case 0x881B: return "rgb16f";
            case 0x822D: return "r16f";
            case 0x822E: return "r32f";
            case 0x8814: return "rgba32f";
            case 0x8815: return "rgb32f";
            case 0x8C3A: return "r11g11b10f";
            case 0x805B: return "rgba16";
            case 0x1902: case 0x81A5: case 0x81A6: case 0x81A7: case 0x8CAC: return "depth";
            default: return "gl" + glInternalFormat.ToString("x4", System.Globalization.CultureInfo.InvariantCulture);
        }
    }

    public static bool IsDepthFormat(int glInternalFormat) => FormatName(glInternalFormat) == "depth";

    /// <summary>True for the 8-bit unsigned-normalised formats, which dump as PPM/PGM.</summary>
    public static bool IsUnorm8Format(int glInternalFormat)
    {
        string name = FormatName(glInternalFormat);
        return name == "rgba8" || name == "rgb8" || name == "r8";
    }

    /// <summary>Stored channels: 4, 3 or 1.</summary>
    public static int ChannelsOf(int glInternalFormat)
    {
        string name = FormatName(glInternalFormat);
        if (name == "depth" || name == "r8" || name == "r16f" || name == "r32f") return 1;
        if (name.StartsWith("rgba", StringComparison.Ordinal)) return 4;
        if (name.StartsWith("rgb", StringComparison.Ordinal) || name == "r11g11b10f") return 3;
        return 4;
    }

    /// <summary>
    /// Writes one attachment's files and returns how many were written (0 when the
    /// readback is malformed).
    /// </summary>
    public static int Write(string directory, int slotIndex, string slotName, string attachment,
        OptimumTextureReadback readback)
    {
        if (directory == null || readback == null || readback.Width <= 0 || readback.Height <= 0) return 0;
        int texels = readback.Width * readback.Height;
        string format = FormatName(readback.GlInternalFormat);
        int channels = ChannelsOf(readback.GlInternalFormat);
        System.IO.Directory.CreateDirectory(directory);

        if (readback.Bytes != null)
        {
            if (readback.Bytes.Length < texels * 4) return 0;
            if (channels == 1)
            {
                WriteNetpbm(System.IO.Path.Combine(directory, FileName(slotIndex, slotName, attachment, format, "pgm")),
                    readback.Width, readback.Height, readback.Bytes, 0, 1);
                return 1;
            }
            WriteNetpbm(System.IO.Path.Combine(directory, FileName(slotIndex, slotName, attachment, format, "ppm")),
                readback.Width, readback.Height, readback.Bytes, 0, 3);
            if (channels < 4) return 1;
            WriteNetpbm(System.IO.Path.Combine(directory, FileName(slotIndex, slotName, attachment, format, "pgm")),
                readback.Width, readback.Height, readback.Bytes, 3, 1);
            return 2;
        }

        if (readback.Floats == null) return 0;
        int stride = readback.Floats.Length >= texels * 4 ? 4 : 1;
        if (readback.Floats.Length < texels * stride) return 0;
        if (stride == 1 || channels == 1)
        {
            WritePfm(System.IO.Path.Combine(directory, FileName(slotIndex, slotName, attachment, format, "pfm")),
                readback.Width, readback.Height, readback.Floats, stride, 0, 1);
            return 1;
        }
        WritePfm(System.IO.Path.Combine(directory, FileName(slotIndex, slotName, attachment, format, "pfm")),
            readback.Width, readback.Height, readback.Floats, 4, 0, 3);
        if (channels < 4) return 1;
        WritePfm(System.IO.Path.Combine(directory, FileName(slotIndex, slotName, attachment, format, "alpha.pfm")),
            readback.Width, readback.Height, readback.Floats, 4, 3, 1);
        return 2;
    }

    private static void WriteNetpbm(string path, int width, int height, byte[] rgba, int firstChannel, int channels)
    {
        using var file = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        byte[] header = System.Text.Encoding.ASCII.GetBytes(
            (channels == 3 ? "P6\n" : "P5\n") + width + " " + height + "\n255\n");
        file.Write(header, 0, header.Length);
        byte[] row = new byte[width * channels];
        for (int y = 0; y < height; y++)
        {
            int source = y * width * 4;
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    row[x * channels + c] = rgba[source + x * 4 + firstChannel + c];
                }
            }
            file.Write(row, 0, row.Length);
        }
    }

    private static void WritePfm(string path, int width, int height, float[] data, int stride,
        int firstChannel, int channels)
    {
        using var file = new System.IO.FileStream(path, System.IO.FileMode.Create, System.IO.FileAccess.Write);
        byte[] header = System.Text.Encoding.ASCII.GetBytes(
            (channels == 3 ? "PF\n" : "Pf\n") + width + " " + height + "\n-1.0\n");
        file.Write(header, 0, header.Length);
        byte[] row = new byte[width * channels * 4];
        for (int y = 0; y < height; y++)
        {
            int source = y * width * stride;
            for (int x = 0; x < width; x++)
            {
                for (int c = 0; c < channels; c++)
                {
                    System.Buffers.Binary.BinaryPrimitives.WriteSingleLittleEndian(
                        row.AsSpan((x * channels + c) * 4, 4), data[source + x * stride + firstChannel + c]);
                }
            }
            file.Write(row, 0, row.Length);
        }
    }
}

/// <summary>
/// The OpenGL constants the routed client code passes across the seam.
///
/// The seam speaks raw GL constants for texture and comparison parameters,
/// because that is what the client and its mods already hold - a hundred-odd call
/// sites pass them straight to glTexParameter. Naming them here keeps the patched
/// bodies readable without inventing an enum that would have to be translated
/// back at the boundary.
/// </summary>
public static class OptimumGlConstants
{
    public const int TextureMagFilter = 0x2800;
    public const int TextureMinFilter = 0x2801;
    public const int TextureWrapS = 0x2802;
    public const int TextureWrapT = 0x2803;
    public const int TextureCompareMode = 0x884C;
    public const int TextureLodBias = 0x8501;
    public const int TextureMaxLevel = 0x813D;

    public const int Nearest = 0x2600;
    public const int Linear = 0x2601;
    public const int Repeat = 0x2901;
    public const int ClampToEdge = 0x812F;

    public const int CompareModeNone = 0;
    public const int CompareRefToTexture = 0x884E;

    // Sized internal formats for CreateTexture2DRaw, which takes the GL token
    // rather than EnumTextureInternalFormat so call sites that already hold a
    // raw format - Cairo surfaces and the GUI's BGRA uploads - pass it through.
    public const int Rgba8 = 0x8058;

    // GL_BGRA. Not a sized internal format in GL - the GL bodies pass it as the
    // source pixel format alongside an RGBA8 internal format - but across this
    // seam it selects a BGRA-ordered image, which is the same result with one
    // fewer argument.
    public const int Bgra = 0x80E1;
}

/// <summary>Which buffer of a mesh a persistent mapping refers to.</summary>
public enum EnumMeshBufferPart
{
    Xyz,
    Normals,
    Uv,
    Rgba,
    Flags,
    CustomFloats,
    CustomShorts,
    CustomInts,
    CustomBytes,
    Indices
}

/// <summary>Which renderer the client runs.</summary>
public enum EnumRenderBackend
{
    /// <summary>Vanilla OpenGL. The default, and the fallback for everything.</summary>
    OpenGL,
    Vulkan,
    /// <summary>Vulkan where the vendor and driver are known good, else OpenGL.</summary>
    Auto
}

/// <summary>
/// Which backend the client runs, after fallbacks.
///
/// The graphics operations themselves are virtual members of the client platform
/// (Vulkan-native plan, Phase 1A): <c>ClientPlatformWindows</c> implements them with
/// the vanilla OpenGL calls and the renderer's <c>VulkanClientPlatform</c> overrides
/// them, so nothing in the client asks this class which API to call. What remains
/// is the backend decision, which is made before any platform or window exists.
/// </summary>
public static class OptimumRender
{
    /// <summary>Which backend was actually selected, after fallbacks.</summary>
    public static EnumRenderBackend ActiveBackend = EnumRenderBackend.OpenGL;

    /// <summary>
    /// Why the requested backend was not used, or null when it was. Shown in the
    /// settings screen and written to the client log.
    /// </summary>
    public static string FallbackReason;

    /// <summary>True once the Vulkan platform brought its graphics up for the window.</summary>
    public static bool IsVulkan => ActiveBackend == EnumRenderBackend.Vulkan;

    /// <summary>
    /// Set before the window is created when the backend decision was "not
    /// OpenGL", so the window opens with no graphics API at all.
    ///
    /// <see cref="ActiveBackend" /> cannot answer this question: graphics come up
    /// against an existing window, so during window construction the backend is
    /// still OpenGL even on the Vulkan path. Vanilla code that issues GL calls while
    /// the window comes up - GameWindowNative's clear-and-swap, for one - has to test
    /// this flag instead, because with ContextAPI.NoAPI there is no GL binding
    /// loaded and every GL entry point throws.
    /// </summary>
    public static bool NoGraphicsApiWindow;

    /// <summary>
    /// Records a fallback to OpenGL. Idempotent in the sense that the first
    /// reason wins: the earliest cause is the useful one to report.
    /// </summary>
    public static void FallBackToOpenGL(string reason)
    {
        ActiveBackend = EnumRenderBackend.OpenGL;
        // The caller reopens the window for OpenGL after this, so GL calls during
        // window construction are live again.
        NoGraphicsApiWindow = false;
        if (FallbackReason == null)
        {
            FallbackReason = reason;
        }
    }
}

/// <summary>
/// The graphics operations the forked vanilla mods (VSEssentials clouds and world map,
/// VSSurvivalMod's boat water mask) need beyond <c>IRenderAPI</c>, on the Vulkan path.
///
/// The forks reference only the game API and the contracts, so they cannot call the
/// client platform's virtuals; on OpenGL they keep their own GL bodies and this is
/// null. <c>VulkanClientPlatform</c> publishes an implementation while its graphics
/// are up. It is deliberately limited to what those fork files call today and is
/// not a general device seam: the client lib never uses it, and Phase 5 of the
/// Vulkan-native plan ports the forks onto the contracts pass API and removes it.
///
/// Handles and parameters are the same GL-shaped ints the platform uses:
/// texture and framebuffer ids as the API carries them, GL tokens for texture
/// parameters and internal formats, a bit per colour attachment for draw buffers.
/// </summary>
public abstract class OptimumForkGraphics
{
    /// <summary>The implementation while Vulkan graphics are up; null on OpenGL.</summary>
    public static OptimumForkGraphics Active;

    public abstract int CreateTexture2DRaw(int width, int height, int glInternalFormat, IntPtr pixels, int bytesPerPixel);

    public abstract int CreateTexture2DArray(int width, int height, int layers,
        EnumTextureInternalFormat internalFormat, EnumTexturePixelFormat pixelFormat);

    public abstract void UploadTexture2DArrayLayer(int textureId, int layer, int x, int y, int width, int height, IntPtr pixels);

    /// <summary>Signed shorts normalised into RGBA16 storage, as glTexSubImage2D(GL_SHORT) converts them.</summary>
    public abstract void UploadTexture2DNormalizedShorts(int textureId, int level, int x, int y, int width, int height, short[] pixels);

    public abstract void SetTextureParameter(int textureId, int parameterName, int value);

    public abstract void BindTexture(int unit, int textureId);

    public abstract void DeleteTexture(int textureId);

    public abstract int CreateFramebuffer(int width, int height);

    public abstract void AttachTexture(int framebufferId, EnumFramebufferAttachment attachment, int textureId, int layer);

    public abstract void SetDrawBuffers(int framebufferId, int attachmentMask);

    public abstract void BindFramebuffer(int framebufferId);

    public abstract void BindDefaultFramebuffer();

    public abstract void DeleteFramebuffer(int framebufferId);

    public abstract void SetViewport(int x, int y, int width, int height);

    public abstract void SetDepthTest(bool enabled);

    public abstract void SetBlendEnabled(bool enabled);

    public abstract int GetUniformLocation(int programId, string name);

    public abstract void SetUniformArray3(int programId, int location, int count, float[] values);
}
