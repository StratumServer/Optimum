#nullable enable
using System;
using Vintagestory.API.Client;

namespace Vintagestory.GameContent;

/// <summary>
/// Lazy facade owned by OptimumStatusModSystem. Keeps zero active profiler state, zero UI dialogs,
/// zero EventListeners, and zero GL queries until explicitly invoked via .optimum profiler, Settings,
/// EventBus ("optimum:profiler:open"), or OPTIMUM_PROFILER_SMOKE.
/// </summary>
public sealed class OptimumProfilerService : IDisposable
{
    private readonly ICoreClientAPI capi;
    private OptimumProfilerSession? session;
    private HudOptimumProfilerOverlay? hudOverlay;
    private GuiDialogOptimumProfiler? panelDialog;
    private bool disposed;

    public OptimumProfilerService(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public bool IsSessionRunning => session?.IsRunning ?? false;
    public bool IsUiOpen => session?.IsUiOpen ?? false;
    public bool IsHudOpen => session?.IsHudOpen ?? false;
    public bool IsRecording => session?.IsRecording ?? false;

    private OptimumProfilerSession EnsureSession()
    {
        session ??= new OptimumProfilerSession(capi);
        return session;
    }

    public bool ToggleUi()
    {
        if (disposed) return false;
        OptimumProfilerSession s = EnsureSession();
        if (s.IsUiOpen && panelDialog != null && panelDialog.IsOpened())
        {
            panelDialog.TryClose();
            s.SetUiOpen(false);
            return false;
        }
        OpenUi();
        return true;
    }

    public void OpenUi()
    {
        if (disposed) return;
        OptimumProfilerSession s = EnsureSession();
        s.SetUiOpen(true);
        panelDialog ??= new GuiDialogOptimumProfiler(capi, this, s);
        if (!panelDialog.IsOpened())
        {
            panelDialog.TryOpen();
        }
    }

    public void OnDialogClosed()
    {
        session?.SetDetailsWanted(false);
        session?.SetUiOpen(false);
    }

    public void OnHudClosed()
    {
        session?.SetHudOpen(false);
    }

    public bool ToggleHud()
    {
        if (disposed) return false;
        OptimumProfilerSession s = EnsureSession();
        bool next = !s.IsHudOpen;
        s.SetHudOpen(next);
        if (next)
        {
            hudOverlay ??= new HudOptimumProfilerOverlay(capi, this, s);
            if (!hudOverlay.IsOpened())
            {
                hudOverlay.TryOpen();
            }
        }
        else if (hudOverlay != null && hudOverlay.IsOpened())
        {
            hudOverlay.TryClose();
        }
        return next;
    }

    public bool StartRecording(int seconds, string? outDirOverride = null)
    {
        if (disposed) return false;
        int clamped = Math.Clamp(seconds, 1, 120);
        if (!EnsureSession().StartRecording(clamped, outDirOverride, OnBundleSaved))
        {
            return false;
        }
        capi.Logger.Notification("[Optimum] Starting profiler recording ({0}s)", clamped);
        return true;
    }

    /// <summary>Exports the live rolling window; returns the bundle directory, or "" when the profiler is not running.</summary>
    public string ExportSnapshot(string? outDirOverride = null)
    {
        if (disposed) return "";
        string? path = session?.ExportSnapshotNow(outDirOverride, OnBundleSaved);
        if (path == null)
        {
            capi.ShowChatMessage("[Optimum] Open the profiler UI or HUD first; a snapshot exports their live frames.");
            return "";
        }
        return path;
    }

    private void OnBundleSaved(string dir)
    {
        capi.Logger.Notification("[Optimum] Profiler bundle exported to {0}", dir);
        capi.ShowChatMessage($"[Optimum] Profiler bundle saved: {dir}");
    }

    public bool ToggleGlCounters()
    {
        if (disposed) return false;
        OptimumProfilerSession s = EnsureSession();
        bool next = !s.GlCountersEnabled;
        s.SetGlCountersEnabled(next);
        return next;
    }

    public void ResetCounters()
    {
        session?.ResetCounters();
    }

    public void StopAll()
    {
        if (panelDialog != null && panelDialog.IsOpened())
        {
            panelDialog.TryClose();
        }
        if (hudOverlay != null && hudOverlay.IsOpened())
        {
            hudOverlay.TryClose();
        }
        session?.StopAll();
    }

    public void RunEnvSmokeSpec(string spec)
    {
        if (disposed || string.IsNullOrWhiteSpace(spec))
        {
            return;
        }

        string[] parts = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string part in parts)
        {
            if (part.Equals("hud", StringComparison.OrdinalIgnoreCase))
            {
                if (!IsHudOpen) ToggleHud();
            }
            else if (part.Equals("ui", StringComparison.OrdinalIgnoreCase))
            {
                OpenUi();
            }
            else if (part.Equals("glcounters", StringComparison.OrdinalIgnoreCase))
            {
                if (!EnsureSession().GlCountersEnabled) ToggleGlCounters();
            }
            else if (part.StartsWith("record:", StringComparison.OrdinalIgnoreCase) &&
                     int.TryParse(part.Substring("record:".Length), out int secs))
            {
                StartRecording(secs);
            }
            else if (part.Equals("record", StringComparison.OrdinalIgnoreCase))
            {
                StartRecording(5);
            }
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        StopAll();
        panelDialog?.Dispose();
        panelDialog = null;
        hudOverlay?.Dispose();
        hudOverlay = null;
        session?.Dispose();
        session = null;
    }
}
