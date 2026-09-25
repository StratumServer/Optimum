using System;

namespace Optimum.Render.Vulkan.Core;

/// <summary>What a GPU checkpoint marker stands for.</summary>
internal enum CheckpointKind : byte
{
    None = 0,
    FrameBegin = 1,
    Draw = 2,
    DrawMulti = 3,
    Fullscreen = 4,
    Upload = 5,
    Mipmaps = 6,
    PresentBlit = 7,
}

/// <summary>
/// Packs what a GPU checkpoint refers to into the pointer-sized marker the
/// driver records, and reads it back after a device loss.
///
/// VK_NV_device_diagnostic_checkpoints stores the marker value and hands it
/// straight back; nobody dereferences it. So it can carry data rather than point
/// at any, which is what makes a checkpoint per draw affordable: nothing is
/// allocated, and nothing has to be kept alive for a loss that may never come.
///
/// Layout, high to low: 4 bits of kind, 28 bits of "a", 32 bits of "b". The
/// kind is never zero, so neither is the marker, which keeps "no checkpoint"
/// distinguishable from a real one.
/// </summary>
internal static class CheckpointMarker
{
    private const int KindShift = 60;
    private const int AShift = 32;
    private const ulong AMask = (1UL << 28) - 1;
    private const ulong BMask = 0xFFFFFFFFUL;

    public static nint Pack(CheckpointKind kind, uint a, uint b) =>
        (nint)(long)(((ulong)kind << KindShift) | (((ulong)a & AMask) << AShift) | ((ulong)b & BMask));

    public static CheckpointKind KindOf(nint marker) => (CheckpointKind)((ulong)(long)marker >> KindShift);
    public static uint AOf(nint marker) => (uint)(((ulong)(long)marker >> AShift) & AMask);
    public static uint BOf(nint marker) => (uint)((ulong)(long)marker & BMask);

    public static nint FrameBegin(uint frame) => Pack(CheckpointKind.FrameBegin, 0, frame);

    public static nint Draw(CheckpointKind kind, int program, int target, int mesh) =>
        Pack(kind, ((uint)Math.Clamp(target, 0, 0xFFF) << 16) | ((uint)program & 0xFFFF), (uint)mesh);

    public static nint Upload(int texture, uint width, uint height) =>
        Pack(CheckpointKind.Upload, (uint)texture, (Math.Min(width, 0xFFFFu) << 16) | Math.Min(height, 0xFFFFu));

    public static nint Mipmaps(int texture, uint levels) => Pack(CheckpointKind.Mipmaps, (uint)texture, levels);

    public static nint PresentBlit(uint image, uint frame) => Pack(CheckpointKind.PresentBlit, image, frame);

    /// <summary>Renders a marker for a person, naming the program where one is known.</summary>
    public static string Describe(nint marker, Func<int, string?>? programName = null)
    {
        CheckpointKind kind = KindOf(marker);
        uint a = AOf(marker);
        uint b = BOf(marker);

        switch (kind)
        {
            case CheckpointKind.FrameBegin:
                return "frame " + b + " begins";

            case CheckpointKind.Draw:
            case CheckpointKind.DrawMulti:
            case CheckpointKind.Fullscreen:
            {
                int program = (int)(a & 0xFFFF);
                int target = (int)(a >> 16);
                string name = programName?.Invoke(program) is { } known ? " '" + known + "'" : "";
                string what = kind switch
                {
                    CheckpointKind.DrawMulti => "multi-draw",
                    CheckpointKind.Fullscreen => "fullscreen draw",
                    _ => "draw",
                };
                string mesh = kind == CheckpointKind.Fullscreen ? "" : " mesh " + b;
                return what + " with program " + program + name + mesh + " into framebuffer " + target;
            }

            case CheckpointKind.Upload:
                return "upload of " + (b >> 16) + "x" + (b & 0xFFFF) + " texels into texture " + a;

            case CheckpointKind.Mipmaps:
                return "mipmap generation for texture " + a + " (" + b + " levels)";

            case CheckpointKind.PresentBlit:
                return "blit of frame " + b + " into swapchain image " + a;

            default:
                return "unknown marker 0x" + ((ulong)(long)marker).ToString("x");
        }
    }
}
