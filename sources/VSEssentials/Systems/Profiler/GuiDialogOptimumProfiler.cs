#nullable enable
using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;

namespace Vintagestory.GameContent;

/// <summary>
/// On-demand tabbed profiler window (Pipeline, Optimum Systems, GC & Locks, World & GPU,
/// Record & Export). Redraws Cairo telemetry at 4 Hz and shuts down the underlying session
/// on close when no HUD overlay or active recording remains.
/// </summary>
public sealed class GuiDialogOptimumProfiler : GuiDialog
{
    private const double RedrawIntervalSec = 0.25;

    private readonly OptimumProfilerService service;
    private readonly OptimumProfilerSession session;
    private readonly CairoFont titleFont;
    private readonly CairoFont bodyFont;
    private readonly CairoFont smallFont;

    private int activeTab;
    private double accumSec;
    private OptimumProfilerSnapshot? latestSnapshot;

    public GuiDialogOptimumProfiler(ICoreClientAPI capi, OptimumProfilerService service, OptimumProfilerSession session)
        : base(capi)
    {
        this.service = service;
        this.session = session;
        this.titleFont = CairoFont.WhiteDetailText().WithFontSize(13.5f);
        this.bodyFont = CairoFont.WhiteDetailText().WithFontSize(11.5f);
        this.smallFont = CairoFont.WhiteDetailText().WithFontSize(10.5f);
    }

    public override string ToggleKeyCombinationCode => null!;

    public override void OnGuiOpened()
    {
        base.OnGuiOpened();
        Compose();
        session.SetDetailsWanted(TabShowsCensusDetails(activeTab));
        accumSec = RedrawIntervalSec;
    }

    public override void OnGuiClosed()
    {
        base.OnGuiClosed();
        service.OnDialogClosed();
    }

    private void Compose()
    {
        // Assigning SingleComposer does not dispose the previous composer or its textures.
        ClearComposers();
        ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
        bgBounds.BothSizing = ElementSizing.FitToChildren;

        ElementBounds container = ElementBounds.Fixed(0, 30, 760, 500);
        bgBounds.WithChild(container);

        ElementBounds tabBounds = ElementBounds.Fixed(0, 30, 760, 28);
        ElementBounds btnBar = ElementBounds.Fixed(0, 64, 760, 28);
        ElementBounds canvasBounds = ElementBounds.Fixed(0, 98, 760, 428);

        GuiTab[] tabs =
        {
            new() { DataInt = 0, Name = "Pipeline" },
            new() { DataInt = 1, Name = "Optimum" },
            new() { DataInt = 2, Name = "GC & Locks" },
            new() { DataInt = 3, Name = "World & GPU" },
            new() { DataInt = 4, Name = "Record & Export" },
        };

        ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
            .WithAlignment(EnumDialogArea.CenterMiddle);

        SingleComposer = capi.Gui.CreateCompo("optimumprofilerwindow", dialogBounds)
            .AddShadedDialogBG(bgBounds)
            .AddDialogTitleBar("Optimum Integrated Profiler", OnTitleBarClose)
            .BeginChildElements(bgBounds)
                .AddHorizontalTabs(tabs, tabBounds, OnTabChanged, CairoFont.WhiteDetailText(), CairoFont.WhiteDetailText(), "tabs")
                .AddSmallButton(session.IsHudOpen ? "HUD: ON" : "HUD: OFF", OnToggleHudClicked, btnBar.FlatCopy().WithFixedWidth(95), EnumButtonStyle.Small, "btnHud")
                .AddSmallButton(session.GlCountersEnabled ? "Overdraw: ON" : "Overdraw: OFF", OnToggleGlCountersClicked, btnBar.FlatCopy().WithFixedPosition(104, 64).WithFixedWidth(122), EnumButtonStyle.Small, "btnGl")
                .AddSmallButton("Record 10s", OnRecord10Clicked, btnBar.FlatCopy().WithFixedPosition(234, 64).WithFixedWidth(105), EnumButtonStyle.Small, "btnRec10")
                .AddSmallButton("Snapshot", OnSnapshotClicked, btnBar.FlatCopy().WithFixedPosition(347, 64).WithFixedWidth(95), EnumButtonStyle.Small, "btnSnap")
                .AddSmallButton("Reset", OnResetClicked, btnBar.FlatCopy().WithFixedPosition(450, 64).WithFixedWidth(75), EnumButtonStyle.Small, "btnReset")
                .AddDynamicCustomDraw(canvasBounds, OnDrawTabCanvas, "tabCanvas")
            .EndChildElements()
            .Compose();
    }

    private void OnTitleBarClose()
    {
        TryClose();
    }

    private void OnTabChanged(int tabIdx)
    {
        activeTab = Math.Clamp(tabIdx, 0, 4);
        session.SetDetailsWanted(TabShowsCensusDetails(activeTab));
        SingleComposer?.GetCustomDraw("tabCanvas")?.Redraw();
    }

    /// <summary>The Optimum and World tabs show the detailed census; the others do not need it.</summary>
    private static bool TabShowsCensusDetails(int tab) => tab is 1 or 3;

    private bool OnToggleHudClicked()
    {
        service.ToggleHud();
        RecomposeAfterClick();
        return true;
    }

    private bool OnToggleGlCountersClicked()
    {
        service.ToggleGlCounters();
        RecomposeAfterClick();
        return true;
    }

    /// <summary>
    /// The click is dispatched while the composer iterates its elements, so disposing it here
    /// would modify that collection; rebuild the button labels on the next main-thread task.
    /// </summary>
    private void RecomposeAfterClick() => capi.Event.EnqueueMainThreadTask(() =>
    {
        if (IsOpened()) Compose();
    }, "optimum-profiler-dialog");

    private bool OnRecord10Clicked()
    {
        service.StartRecording(10);
        return true;
    }

    private bool OnSnapshotClicked()
    {
        service.ExportSnapshot();
        latestSnapshot = session.BuildSnapshot();
        SingleComposer?.GetCustomDraw("tabCanvas")?.Redraw();
        return true;
    }

    private bool OnResetClicked()
    {
        service.ResetCounters();
        latestSnapshot = session.BuildSnapshot();
        SingleComposer?.GetCustomDraw("tabCanvas")?.Redraw();
        return true;
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
            SingleComposer?.GetCustomDraw("tabCanvas")?.Redraw();
        }

        base.OnRenderGUI(deltaTime);
    }

    private void OnDrawTabCanvas(Context ctx, ImageSurface surface, ElementBounds bounds)
    {
        ctx.Operator = Operator.Clear;
        ctx.Paint();
        ctx.Operator = Operator.Over;

        double w = bounds.InnerWidth;
        double h = bounds.InnerHeight;
        ctx.SetSourceRGBA(0.05, 0.07, 0.10, 0.85);
        ctx.Rectangle(0, 0, w, h);
        ctx.Fill();

        OptimumProfilerSnapshot s = latestSnapshot ?? session.BuildSnapshot();
        double pad = GuiElement.scaled(10);
        double y = GuiElement.scaled(18);

        string topLine = $"{s.AvgFps:F1} FPS (1% {s.Low1Fps:F1}) | Frame {s.FrameDist.Mean:F1}ms (P95 {s.FrameDist.P95:F1}) | GPU {s.GpuSpanDist.Mean:F1}ms | {s.Verdict.Category}";
        DrawText(ctx, titleFont, Truncate(topLine, 92), pad, y, 0.96, 0.97, 1.0, 1.0);
        y += GuiElement.scaled(22);

        switch (activeTab)
        {
            case 0:
                DrawPipelineTab(ctx, s, pad, y, w);
                break;
            case 1:
                DrawOptimumTab(ctx, s, pad, y, w);
                break;
            case 2:
                DrawGcLocksTab(ctx, s, pad, y, w);
                break;
            case 3:
                DrawWorldTab(ctx, s, pad, y, w);
                break;
            case 4:
                DrawExportTab(ctx, s, pad, y, w);
                break;
        }
    }

    private void DrawPipelineTab(Context ctx, OptimumProfilerSnapshot s, double x, double y, double w)
    {
        DrawText(ctx, bodyFont, $"Stage Breakdown (GPU ms green / CPU ms blue), CPU render {s.CpuRenderMeanMs:F1}ms of {s.FrameDist.Mean:F1}ms", x, y, 0.78, 0.86, 0.96, 1.0);
        y += GuiElement.scaled(18);

        double maxMs = 1.0;
        for (int i = 0; i < s.StageGpuMeansMs.Length; i++)
        {
            maxMs = Math.Max(maxMs, s.StageGpuMeansMs[i].Value);
            if (i < s.StageCpuMeansMs.Length) maxMs = Math.Max(maxMs, s.StageCpuMeansMs[i].Value);
        }

        double barX = x + GuiElement.scaled(105);
        double maxBarW = Math.Max(80, w - barX - GuiElement.scaled(175));
        double rowStep = GuiElement.scaled(22);

        for (int i = 0; i < s.StageGpuMeansMs.Length; i++)
        {
            string label = s.StageGpuMeansMs[i].Key;
            double gpu = s.StageGpuMeansMs[i].Value;
            double cpu = i < s.StageCpuMeansMs.Length ? s.StageCpuMeansMs[i].Value : 0.0;

            DrawText(ctx, smallFont, label, x, y + GuiElement.scaled(11), 0.90, 0.92, 0.95, 1.0);

            ctx.SetSourceRGBA(0.28, 0.82, 0.45, 0.85);
            ctx.Rectangle(barX, y, (gpu / maxMs) * maxBarW, GuiElement.scaled(7));
            ctx.Fill();

            ctx.SetSourceRGBA(0.32, 0.72, 0.98, 0.85);
            ctx.Rectangle(barX, y + GuiElement.scaled(8), (cpu / maxMs) * maxBarW, GuiElement.scaled(7));
            ctx.Fill();

            DrawText(ctx, smallFont, $"GPU {gpu:F2}ms | CPU {cpu:F2}ms", barX + maxBarW + GuiElement.scaled(8), y + GuiElement.scaled(11), 0.84, 0.86, 0.90, 1.0);
            y += rowStep;
        }

        if (s.GlPrimitivesMean.Length == ProfilerGlCounters.Count)
        {
            y += GuiElement.scaled(10);
            DrawText(ctx, bodyFont, "GL Hardware Primitives / SamplesPassed (Overdraw)", x, y, 0.98, 0.82, 0.35, 1.0);
            y += GuiElement.scaled(16);
            for (int i = 0; i < ProfilerGlCounters.Count; i++)
            {
                DrawText(ctx, smallFont, $"{ProfilerGlCounters.Labels[i],-12}  Prims: {s.GlPrimitivesMean[i],9:N0}   Samples: {s.GlSamplesPassedMean[i],11:N0}", x, y, 0.88, 0.90, 0.94, 1.0);
                y += GuiElement.scaled(16);
            }
        }
    }

    private void DrawOptimumTab(Context ctx, OptimumProfilerSnapshot s, double x, double y, double w)
    {
        DrawText(ctx, bodyFont, $"Optimum v{s.Census.OptimumVersion} Active Counters & Summaries", x, y, 0.78, 0.86, 0.96, 1.0);
        y += GuiElement.scaled(18);

        int count = Math.Min(12, s.Census.OptimumCounters.Count);
        for (int i = 0; i < count; i++)
        {
            ProfilerOptimumCounterStat c = s.Census.OptimumCounters[i];
            DrawText(ctx, smallFont, $"{c.Name,-26} hits={c.Hits,-8} skips={c.Skips,-8} ({c.SkipRatePct:F1}%)", x, y, 0.88, 0.90, 0.94, 1.0);
            y += GuiElement.scaled(15);
        }

        if (!string.IsNullOrEmpty(s.Census.ShadowFarRenderersSummary))
        {
            y += GuiElement.scaled(6);
            DrawText(ctx, smallFont, Truncate($"ShadowFar renderers: {s.Census.ShadowFarRenderersSummary}", 105), x, y, 0.98, 0.78, 0.38, 1.0);
            y += GuiElement.scaled(15);
        }

        int sumMax = Math.Min(5, s.Census.OptimumSummaries.Count);
        for (int i = 0; i < sumMax; i++)
        {
            var kv = s.Census.OptimumSummaries[i];
            DrawText(ctx, smallFont, Truncate($"{kv.Key}: {kv.Value.Replace('\n', ' ')}", 105), x, y, 0.76, 0.84, 0.92, 1.0);
            y += GuiElement.scaled(14);
        }
    }

    private void DrawGcLocksTab(Context ctx, OptimumProfilerSnapshot s, double x, double y, double w)
    {
        ProfilerRuntimeSnapshot rt = s.Runtime;
        DrawText(ctx, bodyFont, $"Sampled allocs SOH {rt.SohBytes / 1048576.0:F1} MB | LOH {rt.LohBytes / 1048576.0:F1} MB | GC pause {s.GcPausePct:F2}% | Locks {rt.ContentionCount} ({rt.ContentionTotalMs:F1}ms, all threads)", x, y, 0.78, 0.86, 0.96, 1.0);
        y += GuiElement.scaled(18);

        int topN = Math.Min(10, rt.TopTypes.Count);
        for (int i = 0; i < topN; i++)
        {
            ProfilerAllocTypeEntry t = rt.TopTypes[i];
            DrawText(ctx, smallFont, Truncate($"{t.SharePct,5:F1}%  {t.Bytes / 1048576.0,6:F2} MB  (LOH {t.LohBytes / 1048576.0:F2} MB)  {t.TypeName}", 98), x, y, 0.90, 0.92, 0.95, 1.0);
            y += GuiElement.scaled(15);
        }

        y += GuiElement.scaled(8);
        DrawText(ctx, bodyFont, "FrameProfiler Top Markers", x, y, 0.98, 0.82, 0.38, 1.0);
        y += GuiElement.scaled(16);

        int mkN = Math.Min(8, s.FrameProfilerMarkers.Count);
        for (int i = 0; i < mkN; i++)
        {
            ProfilerMarkerStat m = s.FrameProfilerMarkers[i];
            DrawText(ctx, smallFont, Truncate($"{m.TotalMs,8:F1} ms  (max {m.MaxMs:F2} ms, {m.Calls}x)  {m.Path}", 98), x, y, 0.80, 0.86, 0.92, 1.0);
            y += GuiElement.scaled(14);
        }
    }

    private void DrawWorldTab(Context ctx, OptimumProfilerSnapshot s, double x, double y, double w)
    {
        ProfilerCensusSnapshot c = s.Census;
        DrawText(ctx, bodyFont, $"GL: {Truncate(c.GlRenderer, 72)} ({c.GlVersion})", x, y, 0.78, 0.86, 0.96, 1.0);
        y += GuiElement.scaled(18);
        DrawText(ctx, smallFont, $"Chunks: total={c.ChunksTotal}, empty={c.ChunksEmpty}, unpacked={c.ChunksUnpacked}, awaitingTess={c.ChunksAwaitingTessellation}, dirtyQueue={c.DirtyQueueCount}, tess/s={c.ChunksTesselatedPerSecond}", x, y, 0.90, 0.92, 0.95, 1.0);
        y += GuiElement.scaled(15);
        DrawText(ctx, smallFont, $"Entities: loaded={c.EntitiesLoaded}, rendered={c.EntitiesRendered}, shadow={c.EntitiesShadowRendered}, animators={c.EntitiesActiveAnimators} | Shaders={c.ShaderCount} (err={c.ShaderLoadErrors}) | GUIs={c.OpenedGuiCount}", x, y, 0.90, 0.92, 0.95, 1.0);
        y += GuiElement.scaled(20);

        DrawText(ctx, bodyFont, "Chunk MeshDataPools", x, y, 0.98, 0.82, 0.38, 1.0);
        y += GuiElement.scaled(16);
        int poolN = Math.Min(7, c.MeshPools.Count);
        for (int i = 0; i < poolN; i++)
        {
            ProfilerMeshPoolPassStat p = c.MeshPools[i];
            DrawText(ctx, smallFont, $"{p.Pass,-16} pools={p.PoolCount,2}  verts={p.UsedVertices,8:N0}/{p.CapacityVertices,8:N0}  renderedTris={p.RenderedTris,8:N0}", x, y, 0.86, 0.90, 0.94, 1.0);
            y += GuiElement.scaled(15);
        }

        if (c.TopBlockEntities.Count > 0)
        {
            y += GuiElement.scaled(8);
            DrawText(ctx, bodyFont, "Top BlockEntities", x, y, 0.98, 0.82, 0.38, 1.0);
            y += GuiElement.scaled(16);
            int beN = Math.Min(6, c.TopBlockEntities.Count);
            for (int i = 0; i < beN; i++)
            {
                var kv = c.TopBlockEntities[i];
                DrawText(ctx, smallFont, $"{kv.Key,-36} {kv.Value}", x, y, 0.84, 0.88, 0.92, 1.0);
                y += GuiElement.scaled(14);
            }
        }
    }

    private void DrawExportTab(Context ctx, OptimumProfilerSnapshot s, double x, double y, double w)
    {
        DrawText(ctx, bodyFont, "Recording & Perfetto Bundle Export", x, y, 0.78, 0.86, 0.96, 1.0);
        y += GuiElement.scaled(22);

        string status = s.IsRecording
            ? $"Recording active: {s.RecordingElapsedSec:F1}s / {s.RecordingTargetSec:F0}s"
            : "Idle (click 'Record 10s' or 'Snapshot', or run .optimum profiler record 15)";
        DrawText(ctx, bodyFont, status, x, y, s.IsRecording ? 0.98 : 0.85, s.IsRecording ? 0.42 : 0.92, s.IsRecording ? 0.42 : 0.88, 1.0);
        y += GuiElement.scaled(22);

        DrawText(ctx, smallFont, "Bundle files written per export:", x, y, 0.88, 0.90, 0.95, 1.0);
        y += GuiElement.scaled(16);
        DrawText(ctx, smallFont, "1) trace.perfetto.json.gz  (open directly in https://ui.perfetto.dev)", x, y, 0.80, 0.88, 0.96, 1.0);
        y += GuiElement.scaled(15);
        DrawText(ctx, smallFont, "2) summary.json             (hardware, Optimum diagnostics, GL/GC/world stats)", x, y, 0.80, 0.88, 0.96, 1.0);
        y += GuiElement.scaled(15);
        DrawText(ctx, smallFont, "3) report.md                (concise issue/PR report)", x, y, 0.80, 0.88, 0.96, 1.0);
        y += GuiElement.scaled(24);

        string last = string.IsNullOrEmpty(s.LastExportPath) ? "(none exported yet this session)" : s.LastExportPath;
        DrawText(ctx, smallFont, Truncate($"Last export: {last}", 105), x, y, 0.55, 0.92, 0.65, 1.0);
    }

    private static void DrawText(Context ctx, CairoFont font, string text, double x, double y, double r, double g, double b, double a)
    {
        font.SetupContext(ctx);
        ctx.SetSourceRGBA(r, g, b, a);
        ctx.MoveTo(x, y);
        ctx.ShowText(text);
    }

    private static string Truncate(string s, int maxLen) => s.Length <= maxLen ? s : s.Substring(0, maxLen - 3) + "...";
}
