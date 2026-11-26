#nullable enable
using System;
using Cairo;
using Vintagestory.API.Client;

namespace Vintagestory.GameContent;

/// <summary>
/// Compact non-focusable Unreal stat-unit style HUD overlay displaying live FPS, 1% low,
/// frametime sparkline (240 frames), CPU vs GPU span, GC/locks, and bottleneck verdict.
/// Redraws its Cairo canvas at 6 Hz so the overlay itself does not distort profiling.
/// </summary>
public sealed class HudOptimumProfilerOverlay : HudElement
{
    private const double RedrawIntervalSec = 1.0 / 6.0;
    private readonly OptimumProfilerSession session;
    private readonly CairoFont labelFont;
    private readonly CairoFont smallFont;
    private double accumSec;
    private OptimumProfilerSnapshot? latestSnapshot;

    public HudOptimumProfilerOverlay(ICoreClientAPI capi, OptimumProfilerSession session) : base(capi)
    {
        this.session = session;
        this.labelFont = CairoFont.WhiteDetailText().WithFontSize(12f);
        this.smallFont = CairoFont.WhiteDetailText().WithFontSize(10.5f);
    }

    public override bool Focusable => false;
    public override double DrawOrder => 0.85;

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        Compose();
        accumSec = RedrawIntervalSec;
    }

    private void Compose()
    {
        ElementBounds drawBounds = ElementBounds.Fixed(0, 0, 344, 176);
        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(8);
        bgBounds.BothSizing = ElementSizing.FitToChildren;
        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.RightTop)
            .WithFixedAlignmentOffset(-12, 12);

        SingleComposer = capi.Gui.CreateCompo("optimumprofilerhud", dialogBounds)
            .AddShadedDialogBG(bgBounds, withTitleBar: false, alpha: 0.78f)
            .BeginChildElements(bgBounds)
                .AddDynamicCustomDraw(drawBounds, OnDrawCanvas, "hudCanvas")
            .EndChildElements()
            .Compose();
    }

    public override void OnRenderGUI(float deltaTime)
    {
        if (!IsOpened())
        {
            return;
        }

        accumSec += deltaTime;
        if (accumSec >= RedrawIntervalSec)
        {
            accumSec = 0;
            latestSnapshot = session.BuildSnapshot();
            SingleComposer?.GetCustomDraw("hudCanvas")?.Redraw();
        }

        base.OnRenderGUI(deltaTime);
    }

    private void OnDrawCanvas(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        double w = bounds.InnerWidth;
        double h = bounds.InnerHeight;
        ctx.Operator = Operator.Clear;
        ctx.Paint();
        ctx.Operator = Operator.Over;

        OptimumProfilerSnapshot s = latestSnapshot ?? session.BuildSnapshot();

        double pad = GuiElement.scaled(6);
        double y = GuiElement.scaled(14);

        // Header line: FPS + 1% Low + Bottleneck badge
        string header = $"{s.AvgFps:F1} FPS  (1% {s.Low1Fps:F1})";
        DrawText(ctx, labelFont, header, pad, y, 0.95, 0.97, 1.0, 1.0);

        (double br, double bg, double bb) = s.Verdict.Category switch
        {
            "GPU-BOUND" => (0.95, 0.45, 0.35),
            "CPU-BOUND" => (0.35, 0.78, 0.98),
            "GC/LOCK STALL" => (0.98, 0.76, 0.25),
            _ => (0.45, 0.92, 0.52),
        };
        DrawText(ctx, smallFont, s.Verdict.Category, w - GuiElement.scaled(96), y, br, bg, bb, 1.0);

        y += GuiElement.scaled(17);
        string sub = $"Frame {s.FrameDist.Mean:F1}ms (P95 {s.FrameDist.P95:F1}) | GPU {s.GpuSpanDist.Mean:F1}ms";
        DrawText(ctx, smallFont, sub, pad, y, 0.85, 0.88, 0.92, 1.0);

        y += GuiElement.scaled(15);
        string mem = $"Alloc {s.AllocMbPerSec:F1} MB/s | GC {s.Gen0Count}/{s.Gen1Count}/{s.Gen2Count} | Locks {s.LockContentionsPerSec:F0}/s";
        DrawText(ctx, smallFont, mem, pad, y, 0.80, 0.85, 0.90, 1.0);

        if (s.IsRecording)
        {
            y += GuiElement.scaled(14);
            string rec = $"REC {s.RecordingElapsedSec:F1}s / {s.RecordingTargetSec:F0}s";
            DrawText(ctx, smallFont, rec, pad, y, 0.98, 0.35, 0.35, 1.0);
        }

        // Sparkline box
        double gx = pad;
        double gy = y + GuiElement.scaled(10);
        double gw = Math.Max(40, w - pad * 2);
        double gh = Math.Max(36, h - gy - pad);

        ctx.SetSourceRGBA(0.06, 0.08, 0.11, 0.85);
        ctx.Rectangle(gx, gy, gw, gh);
        ctx.Fill();

        double maxMs = 40.0;
        for (int i = 0; i < s.RecentFrameMs.Length; i++)
        {
            if (s.RecentFrameMs[i] > maxMs) maxMs = Math.Min(120.0, s.RecentFrameMs[i]);
        }

        // 16.6ms (60 FPS) and 33.3ms (30 FPS) reference lines
        DrawHorizontalRef(ctx, gx, gy, gw, gh, 16.67, maxMs, 0.25, 0.78, 0.45, 0.55);
        DrawHorizontalRef(ctx, gx, gy, gw, gh, 33.33, maxMs, 0.95, 0.72, 0.22, 0.55);

        double[] frames = s.RecentFrameMs;
        if (frames.Length >= 2)
        {
            ctx.LineWidth = Math.Max(1.0, GuiElement.scaled(1.4));
            ctx.SetSourceRGBA(0.35, 0.85, 0.98, 0.95);
            for (int i = 0; i < frames.Length; i++)
            {
                double x = gx + (i / (double)Math.Max(1, frames.Length - 1)) * gw;
                double clamped = Math.Clamp(frames[i], 0.0, maxMs);
                double py = gy + gh - (clamped / maxMs) * gh;
                if (i == 0) ctx.MoveTo(x, py);
                else ctx.LineTo(x, py);
            }
            ctx.Stroke();
        }
    }

    private static void DrawHorizontalRef(Context ctx, double gx, double gy, double gw, double gh, double targetMs, double maxMs, double r, double g, double b, double a)
    {
        if (targetMs > maxMs) return;
        double py = gy + gh - (targetMs / maxMs) * gh;
        ctx.LineWidth = 1.0;
        ctx.SetSourceRGBA(r, g, b, a);
        ctx.MoveTo(gx, py);
        ctx.LineTo(gx + gw, py);
        ctx.Stroke();
    }

    private static void DrawText(Context ctx, CairoFont font, string text, double x, double y, double r, double g, double b, double a)
    {
        font.SetupContext(ctx);
        ctx.SetSourceRGBA(r, g, b, a);
        ctx.MoveTo(x, y);
        ctx.ShowText(text);
    }
}
