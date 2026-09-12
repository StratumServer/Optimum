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

    /// <summary>
    /// Writes one whole frame as a binary PPM (P6) - the encoding
    /// <c>scripts/dev/ssim.py</c> reads, so two captures pair by file name.
    ///
    /// <paramref name="pixels" /> is four bytes per texel in GL row order
    /// (bottom-up, the first row in the file is GL row 0), exactly as
    /// <c>ReadDefaultFramebuffer</c> hands it back. <paramref name="bgra" /> says
    /// which order those four are in: the OpenGL path reads <c>GL_BGRA</c>, the
    /// Vulkan device's default colour target is <c>R8G8B8A8_UNORM</c> and its
    /// readback hands the texels back untouched, so the two backends differ here
    /// and the written file must not.
    ///
    /// Returns false when the arguments do not describe a frame; it never throws
    /// for that reason alone.
    /// </summary>
    public static bool WriteFrame(string path, int width, int height, byte[] pixels, bool bgra)
    {
        if (path == null || pixels == null || width <= 0 || height <= 0) return false;
        if (pixels.LongLength < (long)width * height * 4) return false;
        string directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);
        // BGRA: start at B's neighbour R (index 2) and walk backwards, so the file
        // gets R, G, B either way.
        WriteNetpbm(path, width, height, pixels, bgra ? 2 : 0, 3, bgra ? -1 : 1);
        return true;
    }

    private static void WriteNetpbm(string path, int width, int height, byte[] rgba, int firstChannel, int channels,
        int step = 1)
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
                    row[x * channels + c] = rgba[source + x * 4 + firstChannel + c * step];
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
/// The headless render harness: the real client and the real renderer, no visible
/// window, frames on disk.
///
/// Every knob is an environment variable, read once, so a run that sets none of
/// them pays one static bool check per frame and behaves exactly as before. The
/// frame loop is not changed by any of this - the harness only makes the window
/// invisible, types the chat commands a human would have typed, and reads the
/// presented image back on the frames it was asked for.
///
/// <list type="bullet">
/// <item><c>OPTIMUM_HEADLESS=1</c> - the window is created with
/// <c>StartVisible=false</c> and <c>StartFocused=false</c>. It is still a real
/// window with a real surface and a real swapchain (there is no surfaceless GL
/// path in this client, so this is the only offscreen mode that is symmetric
/// across both backends), it is simply never mapped. Because it is also never
/// focused, the existing background FPS limiter caps the run at 30 FPS, which is
/// what keeps it usable while the machine is in use.</item>
/// <item><c>OPTIMUM_HEADLESS_COMMANDS=&lt;file&gt;</c> - a newline-separated list of
/// chat commands, dispatched once, on in-world frame
/// <c>OPTIMUM_HEADLESS_COMMAND_FRAME</c> (default 30). Blank lines and lines
/// starting with <c>#</c> are ignored. A line starting with the client command
/// prefix (<c>.</c>) runs locally - that is how the scripted camera
/// (<c>.cam load &lt;points&gt;</c>, <c>.cam play &lt;seconds&gt;</c>) is driven -
/// and anything else is sent to the server as chat, which is how the fixed scene
/// (<c>/time set</c>, <c>/weather</c>, <c>/gamemode</c>) is set.</item>
/// <item><c>OPTIMUM_HEADLESS_FIXED_DT=&lt;seconds&gt;</c> - pins
/// <c>ClientMain.DeltaTimeLimiter</c>, the field vanilla's own cinematic recorder
/// sets, so every simulated frame advances by the same amount regardless of how
/// long it actually took. This is what makes a sequence repeatable; it is not
/// bit-exact (chunk streaming, particle and mob RNG are not pinned by it).</item>
/// <item><c>OPTIMUM_HEADLESS_FRAMES=&lt;abs dir&gt;</c> plus either
/// <c>OPTIMUM_HEADLESS_FRAME_LIST=0,30,60</c> (an explicit list of in-world frame
/// indices) or <c>OPTIMUM_HEADLESS_FRAME_COUNT=&lt;n&gt;</c> with
/// <c>OPTIMUM_HEADLESS_FRAME_STRIDE=&lt;s&gt;</c> (default 1) and
/// <c>OPTIMUM_HEADLESS_FIRST_FRAME=&lt;f&gt;</c> (default: the frame after the
/// command script runs) - a cadence. Each selected frame is written as
/// <c>frame-NNNNNN.ppm</c>, so two captures of the same list pair by name under
/// <c>scripts/dev/ssim.py</c>.</item>
/// </list>
///
/// What this does not cover: an X server (real, nested or Xvfb) still has to be
/// there - GLFW queries the screen size before any window exists and Vulkan needs
/// a WSI surface - and the per-attachment dump that
/// <c>scripts/dev/taa-rejection.py</c> reads is still <c>OPTIMUM_PARITY_DUMP</c>,
/// which composes with this rather than being replaced by it.
/// </summary>
public static class OptimumHeadless
{
    /// <summary>True when <c>OPTIMUM_HEADLESS</c> asks for an invisible window.</summary>
    public static readonly bool Enabled = ResolveFlag("OPTIMUM_HEADLESS");

    /// <summary>The chat-command script, or null when there is none.</summary>
    public static readonly string CommandScriptPath = ResolveExistingFile("OPTIMUM_HEADLESS_COMMANDS");

    /// <summary>The in-world frame the command script is dispatched on, counted from 0.</summary>
    public static readonly long CommandFrame = ResolveLong("OPTIMUM_HEADLESS_COMMAND_FRAME", 30L);

    /// <summary>Seconds per simulated frame, or 0 when the wall clock keeps driving it.</summary>
    public static readonly float FixedDeltaTime = ResolveFixedDeltaTime();

    /// <summary>The absolute directory frames are written to, or null when no frames are wanted.</summary>
    public static readonly string FrameDirectory = ResolveDirectory("OPTIMUM_HEADLESS_FRAMES");

    /// <summary>The in-world frames to write, ascending and without duplicates. Never null.</summary>
    public static readonly long[] Frames = ResolveFrames();

    /// <summary>True when frames will be written.</summary>
    public static readonly bool CaptureEnabled = FrameDirectory != null && Frames.Length > 0;

    /// <summary>
    /// True when the client has to do anything at all per frame for the harness.
    /// The single test the render loop makes.
    /// </summary>
    public static readonly bool Active = Enabled || CaptureEnabled || CommandScriptPath != null
        || FixedDeltaTime > 0f;

    private static bool ResolveFlag(string name)
    {
        string value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return false;
        value = value.Trim();
        return value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveExistingFile(string name)
    {
        string value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value)) return null;
        string full = System.IO.Path.GetFullPath(value);
        return System.IO.File.Exists(full) ? full : null;
    }

    private static string ResolveDirectory(string name)
    {
        string value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value) || !System.IO.Path.IsPathRooted(value)) return null;
        return System.IO.Path.GetFullPath(value);
    }

    private static long ResolveLong(string name, long fallback)
    {
        string value = Environment.GetEnvironmentVariable(name);
        return long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out long parsed) && parsed >= 0 ? parsed : fallback;
    }

    private static float ResolveFixedDeltaTime()
    {
        string value = Environment.GetEnvironmentVariable("OPTIMUM_HEADLESS_FIXED_DT");
        if (!float.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out float seconds)) return 0f;
        // A negative or absurd step would make the simulation meaningless rather
        // than reproducible; one second per frame is already far past useful.
        return seconds > 0f && seconds <= 1f ? seconds : 0f;
    }

    private static long[] ResolveFrames()
    {
        string list = Environment.GetEnvironmentVariable("OPTIMUM_HEADLESS_FRAME_LIST");
        if (!string.IsNullOrWhiteSpace(list)) return ParseFrameList(list);

        long count = ResolveLong("OPTIMUM_HEADLESS_FRAME_COUNT", 0L);
        if (count <= 0L) return new long[0];
        // Default: the first frame after the command script has run, so a camera
        // started by the script is already moving on frame one of the capture.
        return PlanFrames(
            ResolveLong("OPTIMUM_HEADLESS_FIRST_FRAME", CommandScriptPath != null ? CommandFrame + 1L : 0L),
            count,
            ResolveLong("OPTIMUM_HEADLESS_FRAME_STRIDE", 1L));
    }

    /// <summary>A cadence: <paramref name="count" /> frames from <paramref name="first" />, every <paramref name="stride" />.</summary>
    public static long[] PlanFrames(long first, long count, long stride)
    {
        if (count <= 0L) return new long[0];
        if (stride <= 0L) stride = 1L;
        if (first < 0L) first = 0L;
        long[] frames = new long[count];
        for (long i = 0; i < count; i++) frames[i] = first + i * stride;
        return frames;
    }

    /// <summary>
    /// An explicit frame list: comma, space or semicolon separated, negatives and
    /// unparsable entries dropped, ascending and without duplicates.
    /// </summary>
    public static long[] ParseFrameList(string list)
    {
        if (string.IsNullOrWhiteSpace(list)) return new long[0];
        string[] parts = list.Split(new char[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
        var frames = new System.Collections.Generic.List<long>(parts.Length);
        for (int i = 0; i < parts.Length; i++)
        {
            if (long.TryParse(parts[i].Trim(), System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out long frame) && frame >= 0
                && !frames.Contains(frame))
            {
                frames.Add(frame);
            }
        }
        frames.Sort();
        return frames.ToArray();
    }

    /// <summary>True when this in-world frame is one of the frames to write.</summary>
    public static bool ShouldCapture(long worldFrame) => ShouldCapture(Frames, worldFrame);

    /// <summary>True when this in-world frame is one of <paramref name="frames" /> (ascending).</summary>
    public static bool ShouldCapture(long[] frames, long worldFrame)
    {
        if (frames == null) return false;
        for (int i = 0; i < frames.Length; i++)
        {
            if (frames[i] == worldFrame) return true;
            if (frames[i] > worldFrame) return false;
        }
        return false;
    }

    /// <summary>True once this in-world frame is at or past the last frame to write.</summary>
    public static bool CaptureFinished(long worldFrame) => CaptureFinished(Frames, worldFrame);

    /// <summary>True once this in-world frame is at or past the last of <paramref name="frames" />.</summary>
    public static bool CaptureFinished(long[] frames, long worldFrame)
    {
        return frames == null || frames.Length == 0 || worldFrame >= frames[frames.Length - 1];
    }

    /// <summary>The one file name both backends write, so two captures pair by name.</summary>
    public static string FrameFileName(long worldFrame)
    {
        return "frame-" + worldFrame.ToString("D6", System.Globalization.CultureInfo.InvariantCulture) + ".ppm";
    }

    /// <summary>
    /// The command script's lines, blank lines and <c>#</c> comments removed.
    /// Empty when there is no script or it cannot be read - a capture that loses
    /// its scene is worth a warning, not a crashed client.
    /// </summary>
    public static string[] ReadCommands() => ReadCommands(CommandScriptPath);

    /// <summary>The command lines of one script file; empty when it cannot be read.</summary>
    public static string[] ReadCommands(string path)
    {
        if (path == null) return new string[0];
        string[] lines;
        try
        {
            lines = System.IO.File.ReadAllLines(path);
        }
        catch (Exception)
        {
            return new string[0];
        }
        var commands = new System.Collections.Generic.List<string>(lines.Length);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            commands.Add(line);
        }
        return commands.ToArray();
    }

    /// <summary>
    /// Writes one presented frame into <see cref="FrameDirectory" />. Returns
    /// false when nothing was written.
    /// </summary>
    public static bool WriteFrame(long worldFrame, int width, int height, byte[] pixels, bool bgra)
    {
        if (FrameDirectory == null) return false;
        return OptimumParityDump.WriteFrame(
            System.IO.Path.Combine(FrameDirectory, FrameFileName(worldFrame)), width, height, pixels, bgra);
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
