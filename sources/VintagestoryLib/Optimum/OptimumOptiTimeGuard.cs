using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Vintagestory.API.Config;

namespace Optimum;

public static class OptimumOptiTimeGuard
{
    private const string DisableSuffix = ".disabled-by-optimum";
    private static readonly List<string> DisabledFiles = new();
    private static string? _customLeaseFilePath;
    private static string? _customModsPath;

    internal sealed class OptiTimeLeaseManifest
    {
        public int ProcessId { get; set; }
        public DateTime TimestampUtc { get; set; }
        public List<OptiTimeRenameEntry> Entries { get; set; } = new();
    }

    internal sealed class OptiTimeRenameEntry
    {
        public string OriginalPath { get; set; } = string.Empty;
        public string DisabledPath { get; set; } = string.Empty;
    }

    internal static string GetDefaultLeaseFilePath()
    {
        string root = GamePaths.DataPath ?? AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(root, ".optimum", "optitime-lease.json");
    }

    internal static string LeaseFilePath => _customLeaseFilePath ?? GetDefaultLeaseFilePath();
    internal static string TargetModsPath => _customModsPath ?? GamePaths.DataPathMods;

    internal static void SetTestOverrides(string? modsPath, string? leaseFilePath)
    {
        _customModsPath = modsPath;
        _customLeaseFilePath = leaseFilePath;
    }

    public static void DisableOptiTimeIfPresent(Action<string> logger)
    {
        try
        {
            RestoreAll(logger);

            string modsPath = TargetModsPath;
            if (string.IsNullOrEmpty(modsPath) || !Directory.Exists(modsPath))
            {
                return;
            }

            List<OptiTimeRenameEntry> candidates = new();
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
                candidates.Add(new OptiTimeRenameEntry
                {
                    OriginalPath = path,
                    DisabledPath = disabledPath
                });
            }

            if (candidates.Count == 0)
            {
                return;
            }

            WriteLeaseManifest(candidates, logger);

            foreach (var entry in candidates)
            {
                if (File.Exists(entry.OriginalPath))
                {
                    File.Move(entry.OriginalPath, entry.DisabledPath);
                    DisabledFiles.Add(entry.DisabledPath);
                    logger?.Invoke($"[Optimum] Temporarily disabled OptiTime: {Path.GetFileName(entry.OriginalPath)}");
                }
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
            string root = GamePaths.DataPath ?? AppDomain.CurrentDomain.BaseDirectory;
            string modConfigPath = Path.Combine(root, "ModConfig");
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

    public static void RestoreAll(Action<string> logger)
    {
        try
        {
            RestoreFromLease(logger);

            string modsPath = TargetModsPath;
            if (string.IsNullOrEmpty(modsPath) || !Directory.Exists(modsPath))
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
            // Restoration is best effort during process shutdown or crash recovery.
        }

        DisabledFiles.Clear();
    }

    private static void WriteLeaseManifest(List<OptiTimeRenameEntry> entries, Action<string> logger)
    {
        try
        {
            string leasePath = LeaseFilePath;
            string leaseDir = Path.GetDirectoryName(leasePath)!;
            if (!string.IsNullOrEmpty(leaseDir))
            {
                Directory.CreateDirectory(leaseDir);
            }

            OptiTimeLeaseManifest manifest = new()
            {
                ProcessId = Environment.ProcessId,
                TimestampUtc = DateTime.UtcNow,
                Entries = entries
            };

            string json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
            string tempPath = leasePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, leasePath, true);
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[Optimum] Warning: failed to write OptiTime lease manifest: {ex.Message}");
        }
    }

    private static void RestoreFromLease(Action<string> logger)
    {
        string leasePath = LeaseFilePath;
        if (!File.Exists(leasePath))
        {
            return;
        }

        try
        {
            string json = File.ReadAllText(leasePath);
            OptiTimeLeaseManifest? manifest = JsonSerializer.Deserialize<OptiTimeLeaseManifest>(json);
            if (manifest?.Entries != null)
            {
                foreach (var entry in manifest.Entries)
                {
                    if (File.Exists(entry.DisabledPath))
                    {
                        if (File.Exists(entry.OriginalPath))
                        {
                            File.Delete(entry.DisabledPath);
                        }
                        else
                        {
                            File.Move(entry.DisabledPath, entry.OriginalPath);
                            logger?.Invoke($"[Optimum] Restored OptiTime file from prior session lease: {Path.GetFileName(entry.OriginalPath)}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger?.Invoke($"[Optimum] Warning: failed to parse OptiTime lease manifest during recovery: {ex.Message}");
        }
        finally
        {
            try
            {
                File.Delete(leasePath);
            }
            catch
            {
                // Best effort delete
            }
        }
    }
}
