using System;
using System.Globalization;
using System.IO;
using Silk.NET.Vulkan;

namespace Optimum.Render.Vulkan.Core;

/// <summary>
/// A trace of what the device was actually asked to draw, for the cases where
/// the frame is legal - the validation layer says nothing - but wrong.
///
/// Off unless OPTIMUM_RENDER_TRACE names a file, so it costs one static bool
/// check in a release build and never appears in a normal session. It is a
/// debugging aid rather than diagnostics the game consumes: the client's own
/// error channel carries validation messages already.
/// </summary>
internal static class RenderTrace
{
    private static readonly object Gate = new();
    private static readonly string? Path = Environment.GetEnvironmentVariable("OPTIMUM_RENDER_TRACE");

    public static bool Enabled => Path != null;

    public static void Write(string line)
    {
        if (Path == null) return;
        lock (Gate)
        {
            File.AppendAllText(Path, line + "\n");
        }
    }

    /// <summary>
    /// Records a texture upload along with a checksum of its first rows, which
    /// is what distinguishes "the image never got the pixels" from "the image is
    /// correct but never sampled".
    /// </summary>
    public static unsafe void TextureCreated(
        int id, int width, int height, Format format, IntPtr pixels, int bytesPerPixel)
    {
        if (Path == null) return;

        long sum = 0;
        int nonZero = 0;
        if (pixels != IntPtr.Zero && bytesPerPixel > 0)
        {
            int sampled = Math.Min(width * height * bytesPerPixel, 64 * 1024);
            byte* bytes = (byte*)pixels;
            for (int i = 0; i < sampled; i++)
            {
                sum += bytes[i];
                if (bytes[i] != 0) nonZero++;
            }
        }

        Write(string.Format(CultureInfo.InvariantCulture,
            "tex create id={0} {1}x{2} format={3} bpp={4} bytesum={5} nonzero={6}",
            id, width, height, format, bytesPerPixel, sum, nonZero));
    }

    /// <summary>
    /// Dumps one named uniform out of a program's shadow buffer, which is what
    /// separates "the CPU wrote nonsense" from "the CPU was right and the GPU
    /// read it from the wrong place".
    /// </summary>
    public static void Uniforms(Shaders.ProgramInterfaceLayout layout, byte[] shadow, string name)
    {
        if (Path == null) return;
        if (!layout.MembersByName.TryGetValue(name, out Shaders.UniformMember? member)) return;

        int floats = Math.Min(member.Size / sizeof(float), 16);
        var text = new System.Text.StringBuilder();
        text.Append("  ").Append(name).Append(" @").Append(member.Offset).Append(" =");
        for (int i = 0; i < floats; i++)
        {
            text.Append(' ').Append(
                BitConverter.ToSingle(shadow, member.Offset + i * sizeof(float))
                    .ToString("0.###", CultureInfo.InvariantCulture));
        }
        Write(text.ToString());
    }

    public static void UniformInt(Shaders.ProgramInterfaceLayout layout, byte[] shadow, string name)
    {
        if (Path == null) return;
        if (!layout.MembersByName.TryGetValue(name, out Shaders.UniformMember? member)) return;

        Write("  " + name + " @" + member.Offset + " = " + BitConverter.ToInt32(shadow, member.Offset));
    }

    /// <summary>
    /// Writes each stage's rewritten GLSL beside the trace file, so the source
    /// the driver actually compiled can be read rather than reconstructed.
    /// </summary>
    public static void DumpProgramSources(string passName, Shaders.TranslatedProgram translated)
    {
        if (Path == null) return;

        string directory = System.IO.Path.GetDirectoryName(Path) ?? ".";
        // The hardcoded minimal-GUI program has no pass name at all.
        string safeName = string.IsNullOrWhiteSpace(passName)
            ? "unnamed"
            : string.Join("_", passName.Split(System.IO.Path.GetInvalidFileNameChars()));

        foreach (var stage in translated.RewrittenSource)
        {
            string file = System.IO.Path.Combine(directory, "shader-" + safeName + "-" + stage.Key + ".glsl");
            try
            {
                File.WriteAllText(file, stage.Value);
            }
            catch (IOException)
            {
                // Losing a debug dump must not disturb the run.
            }
        }
    }

    public static void Draw(int meshId, int programId, int indexCount, bool depthTest, bool blend, float depthRangeHint)
    {
        if (Path == null) return;

        Write(string.Format(CultureInfo.InvariantCulture,
            "draw mesh={0} program={1} indices={2} depthTest={3} blend={4} z={5}",
            meshId, programId, indexCount, depthTest, blend, depthRangeHint));
    }
}
