using System;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace Vintagestory.API.Config;

/// <summary>
/// Issue #85: Detects and provides compatibility guards for the third-party Harmony mod Komet.
/// 
/// Architectural context:
/// - Optimum is a client fork (host application): it replaces the engine and core assemblies
///   with an ahead-of-time compiled, .NET 10-native runtime featuring GPU-driven indirect draw
///   (glMultiDrawElementsIndirect) and SIMD frustum culling.
/// - Komet is a guest mod: loaded from Mods/, it uses Harmony 2.x cancelling prefixes
///   (return false) engineered against vanilla Anego bytecode.
///
/// When Komet is active, its prefixes override Optimum's native .NET 10 MDI/SIMD render pipeline.
/// This guard detects Komet, issues diagnostic advisories to logs and in-game chat, and
/// safely yields conflicting subsystems so the client does not crash or corrupt render state.
/// </summary>
public static class OptimumKometGuard
{
    public const string ModId = "komet";

    public const string AdvisoryWarning =
        "[Optimum] Advisory: Komet mod detected. Komet is a third-party guest mod engineered for vanilla Vintage Story. " +
        "Its Harmony prefixes override Optimum's native .NET 10 render pipeline (including glMultiDrawElementsIndirect " +
        "and SIMD frustum culling), which degrades performance and may cause instability. Running both simultaneously is not recommended.";

    public const string PlayerJoinNotification =
        "[Optimum] Advisory: Komet mod detected. Komet overrides Optimum's native GPU indirect draw and SIMD culling pipelines. " +
        "Type '.optimum status' for details.";

    public static bool IsDetected => OptimumConfig.KometDetected;

    public static string? DetectedVersion { get; private set; }

    public static string? DetectionSource { get; private set; }

    public static bool AdvisoryLogged { get; private set; }

    public static bool WorldJoinAdvisorySent { get; private set; }

    /// <summary>
    /// Checks the ModLoader for the presence of the Komet mod.
    /// </summary>
    public static bool Detect(IModLoader? modLoader, Action<string>? logger = null)
    {
        if (modLoader == null) return false;

        try
        {
            if (modLoader.IsModEnabled(ModId))
            {
                OptimumConfig.KometDetected = true;
                DetectionSource = "ModLoader";
                var mod = modLoader.GetMod(ModId);
                DetectedVersion = mod?.Info?.Version ?? "unknown";
                LogAdvisoryOnce(logger);
                return true;
            }

            // Fallback: check all mods if modid differs in casing or naming
            if (modLoader.Mods != null)
            {
                foreach (var mod in modLoader.Mods)
                {
                    if (mod?.Info == null) continue;
                    if (string.Equals(mod.Info.ModID, ModId, StringComparison.OrdinalIgnoreCase) ||
                        (mod.Info.Name != null && mod.Info.Name.IndexOf("komet", StringComparison.OrdinalIgnoreCase) >= 0))
                    {
                        OptimumConfig.KometDetected = true;
                        DetectionSource = "ModLoader";
                        DetectedVersion = mod.Info.Version ?? "unknown";
                        LogAdvisoryOnce(logger);
                        return true;
                    }
                }
            }
        }
        catch (Exception)
        {
            // Defensive: detection must never crash startup
        }

        return false;
    }

    /// <summary>
    /// Scans loaded assemblies in the AppDomain for Komet types or assemblies.
    /// </summary>
    public static bool DetectFromAssemblies(Action<string>? logger = null)
    {
        try
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string? name = asm.GetName().Name;
                if (name != null && string.Equals(name, ModId, StringComparison.OrdinalIgnoreCase))
                {
                    OptimumConfig.KometDetected = true;
                    DetectionSource = "AppDomain";
                    DetectedVersion = asm.GetName().Version?.ToString() ?? "unknown";
                    LogAdvisoryOnce(logger);
                    return true;
                }

                Type? kometType = asm.GetType("Komet.KometModSystem");
                if (kometType != null)
                {
                    OptimumConfig.KometDetected = true;
                    DetectionSource = "AppDomain";
                    DetectedVersion = asm.GetName().Version?.ToString() ?? "unknown";
                    LogAdvisoryOnce(logger);
                    return true;
                }
            }
        }
        catch (Exception)
        {
            // Defensive: reflection scan must never crash startup
        }

        return false;
    }

    /// <summary>
    /// Runs full detection pipeline against the game API and loaded runtime.
    /// </summary>
    public static bool RunDetection(ICoreAPI? api)
    {
        Action<string>? logger = api?.Logger != null ? msg => api.Logger.Warning(msg) : null;
        if (Detect(api?.ModLoader, logger))
        {
            return true;
        }

        return DetectFromAssemblies(logger);
    }

    /// <summary>
    /// Emits in-game chat advisory when player joins the world.
    /// </summary>
    public static void NotifyPlayerOnJoin(ICoreClientAPI? api)
    {
        if (api == null) return;
        NotifyPlayerOnJoin(api.ShowChatMessage);
    }

    /// <summary>
    /// Emits in-game chat advisory to the provided sink when player joins the world.
    /// </summary>
    public static void NotifyPlayerOnJoin(Action<string>? showChat)
    {
        if (showChat == null) return;
        if (OptimumConfig.KometDetected && OptimumConfig.KometGuardEnabled && !WorldJoinAdvisorySent)
        {
            showChat(PlayerJoinNotification);
            WorldJoinAdvisorySent = true;
        }
    }

    /// <summary>
    /// Formats the single-line status for .optimum status and diagnostics.
    /// </summary>
    public static string GetStatusLine()
    {
        if (!OptimumConfig.KometDetected)
        {
            return "none detected";
        }

        string ver = !string.IsNullOrEmpty(DetectedVersion) ? $" v{DetectedVersion}" : "";
        string guardState = OptimumConfig.KometGuardEnabled ? "ACTIVE (safely yielding MDI/SIMD)" : "UNGUARDED (MDI/SIMD force-enabled)";
        return $"Komet{ver} ({guardState})";
    }

    public static void ResetSession()
    {
        WorldJoinAdvisorySent = false;
    }

    public static void ResetForTests()
    {
        OptimumConfig.KometDetected = false;
        OptimumConfig.KometGuardEnabled = true;
        DetectedVersion = null;
        DetectionSource = null;
        AdvisoryLogged = false;
        WorldJoinAdvisorySent = false;
    }

    private static void LogAdvisoryOnce(Action<string>? logger)
    {
        if (logger != null && !AdvisoryLogged)
        {
            logger(AdvisoryWarning);
            AdvisoryLogged = true;
        }
    }
}
