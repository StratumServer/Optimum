using System;
using System.Collections.Generic;
using System.IO;
using Vintagestory.API.Config;

namespace Optimum;

public static class OptimumOptiTimeGuard
{
    private const string DisableSuffix = ".disabled-by-optimum";
    private static readonly List<string> DisabledFiles = new();

    public static void DisableOptiTimeIfPresent(Action<string> logger)
    {
        try
        {
            RestoreAll(logger);

            string modsPath = GamePaths.DataPathMods;
            if (!Directory.Exists(modsPath))
            {
                return;
            }

            foreach (string path in Directory.EnumerateFiles(modsPath, "*", SearchOption.TopDirectoryOnly))
            {
                string extension = Path.GetExtension(path);
                bool supportedExtension = extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                    extension.Equals(".cs", StringComparison.OrdinalIgnoreCase);
                if (!Path.GetFileNameWithoutExtension(path).Contains("optitime", StringComparison.OrdinalIgnoreCase) ||
                    !supportedExtension)
                {
                    continue;
                }

                string disabledPath = path + DisableSuffix;
                File.Move(path, disabledPath);
                DisabledFiles.Add(disabledPath);
                logger?.Invoke($"[Optimum] Temporarily disabled OptiTime: {Path.GetFileName(path)}");
            }

            if (DisabledFiles.Count == 0)
            {
                return;
            }

            logger?.Invoke("[Optimum] OptiTime is disabled for this session because Optimum includes its optimizations.");
            AppDomain.CurrentDomain.ProcessExit += new EventHandler(OnProcessExit);
        }
        catch (Exception exception)
        {
            logger?.Invoke($"[Optimum] Warning: could not disable OptiTime: {exception.Message}");
        }
    }

    public static string GetMigrationMessage()
    {
        try
        {
            string modConfigPath = Path.Combine(GamePaths.DataPath, "ModConfig");
            if (!Directory.Exists(modConfigPath))
            {
                return null;
            }

            foreach (string path in Directory.EnumerateFiles(modConfigPath, "*", SearchOption.TopDirectoryOnly))
            {
                if (Path.GetFileNameWithoutExtension(path).Contains("optitime", StringComparison.OrdinalIgnoreCase) &&
                    Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    return "OptiTime config detected. Optimum includes all OptiTime optimizations natively. " +
                           "You can delete ModConfig/optitime*.json after confirming Optimum is active.";
                }
            }
        }
        catch
        {
            // Migration guidance is non-critical.
        }

        return null;
    }

    private static void OnProcessExit(object sender, EventArgs eventArgs)
    {
        RestoreAll(null);
    }

    private static void RestoreAll(Action<string> logger)
    {
        try
        {
            string modsPath = GamePaths.DataPathMods;
            if (!Directory.Exists(modsPath))
            {
                return;
            }

            foreach (string disabledPath in Directory.EnumerateFiles(modsPath, "*" + DisableSuffix, SearchOption.TopDirectoryOnly))
            {
                string originalPath = disabledPath[..^DisableSuffix.Length];
                if (File.Exists(originalPath))
                {
                    File.Delete(disabledPath);
                }
                else
                {
                    File.Move(disabledPath, originalPath);
                    logger?.Invoke($"[Optimum] Restored OptiTime file: {Path.GetFileName(originalPath)}");
                }
            }
        }
        catch
        {
            // Restoration is best effort during process shutdown.
        }

        DisabledFiles.Clear();
    }
}
