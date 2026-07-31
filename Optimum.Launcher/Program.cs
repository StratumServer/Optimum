using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

namespace Optimum.Launcher;

/// <summary>
/// Optimum runtime launcher. Applies Cecil patches on first launch (cached
/// thereafter), then invokes Vintagestory's ClientProgram.Main via reflection.
/// </summary>
public static class Program
{
    private static readonly string Version =
        typeof(Program).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion?.Split('+')[0] ?? "dev";
    private const string CacheDirName = ".optimum";
    private const string CacheSubDir = "cache";

    // Assemblies that get patched by Optimum
    private static readonly PatchTarget[] PatchTargets =
    [
        new("VintagestoryLib.dll", "VintagestoryLib.Donor.dll", PatchMode.Transplant),
        new("VintagestoryAPI.dll", "VintagestoryAPI.Contracts.dll", PatchMode.Api),
        new("Mods/VSEssentials.dll", "VSEssentials.Donor.dll", PatchMode.Mod, "vsessentials", ".optimum/vanilla/Mods/VSEssentials.dll"),
        new("Mods/VSSurvivalMod.dll", "VSSurvivalMod.Donor.dll", PatchMode.Mod, "vssurvivalmod", ".optimum/vanilla/Mods/VSSurvivalMod.dll"),
    ];

    public static int Main(string[] args)
    {
        try
        {
            return Run(args);
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Optimum] Fatal error: {ex.Message}");
            Logger.LogError(ex.StackTrace ?? "(no stack trace)");
            return LaunchVanillaFallback(args, ex.Message);
        }
    }

    private static int Run(string[] args)
    {
        var sw = Stopwatch.StartNew();

        // Resolve game directory (same directory as Optimum.exe)
        var gameDir = AppContext.BaseDirectory;

        // Resolve cache directory: {dataPath}/.optimum/cache/
        // Use the game's own data path resolution if possible, otherwise local.
        var dataPath = ResolveDataPath(args) ?? gameDir;
        Logger.Init(dataPath);
        var cacheDir = Path.Combine(dataPath, CacheDirName, CacheSubDir);
        var donorDir = Path.Combine(gameDir, CacheDirName, "donors");

        var cache = new CacheManager(gameDir, cacheDir, donorDir, Version);

        // --- Cache validation ---
        var manifest = cache.ValidateCache();

        if (manifest is not null)
        {
            // Cache hit - fast path
            var elapsed = sw.ElapsedMilliseconds;
            Logger.Log($"[Optimum] Cache valid ({elapsed}ms). Launching...");
        }
        else
        {
            // Cache miss - patch and save. Shows a splash screen styled after
            // the vanilla client's own loading screen while this runs, since
            // it can take a few seconds (see RunPatching).
            var fallbackExitCode = RunPatching(args, gameDir, donorDir, cacheDir, cache, sw);
            if (fallbackExitCode is int code)
            {
                return code;
            }
        }

        DeployPatchedMods(cacheDir, gameDir);
        return LaunchGame(args, cacheDir, gameDir);
    }

    /// <summary>
    /// Runs the Cecil patch loop for a cache miss. Shows a splash screen
    /// styled after the vanilla client's own GuiScreenLoadingGame while
    /// this runs, since it's the only part of a launch slow enough (a few
    /// seconds) to be worth one - the cache-hit path above never touches
    /// this method at all.
    ///
    /// GLFW requires the window that owns a GL context to be created,
    /// pumped, and destroyed on the same thread (strictly enforced on
    /// macOS), so the actual patch work (file IO, Cecil IL rewriting) runs
    /// on a background thread while this one pumps the splash's window
    /// loop. Returns null on success (continue the normal launch flow), or
    /// an exit code if patching failed and <see cref="LaunchVanillaFallback"/>
    /// already ran.
    /// </summary>
    private static int? RunPatching(
        string[] args, string gameDir, string donorDir, string cacheDir, CacheManager cache, Stopwatch sw)
    {
        Logger.Log($"[Optimum] Optimum v{Version}");
        Logger.Log($"[Optimum] Applying optimizations...");
        Logger.Log();

        cache.Invalidate();

        PatchSplashScreen? splash = null;
        try
        {
            splash = new PatchSplashScreen();
        }
        catch (Exception ex)
        {
            // Cosmetic only - a windowing/GL failure (headless host, no
            // display, unsupported driver) must never block patching.
            Logger.LogError($"[Optimum] Splash screen unavailable, continuing without it: {ex.Message}");
        }

        var patchedTargets = new List<PatchedTarget>();
        string? fallbackReason = null;

        void RunPatchLoop()
        {
            try
            {
                for (int i = 0; i < PatchTargets.Length; i++)
                {
                    var target = PatchTargets[i];
                    var vanillaPath = Path.Combine(gameDir, target.VanillaDll);
                    var donorPath = Path.Combine(donorDir, target.DonorDll);
                    var outputPath = Path.Combine(cacheDir, target.AssemblyPath);

                    if (!File.Exists(vanillaPath))
                    {
                        Logger.LogError($"[Optimum] Vanilla DLL not found: {vanillaPath}");
                        fallbackReason = $"Missing: {target.AssemblyPath}";
                        return;
                    }

                    if (!File.Exists(donorPath))
                    {
                        Logger.LogError($"[Optimum] Donor DLL not found: {donorPath}");
                        Logger.LogError($"[Optimum] Expected at: {donorPath}");
                        fallbackReason = $"Missing donor: {target.DonorDll}";
                        return;
                    }

                    splash?.SetStatus($"Applying Optimum patches: {target.VanillaDll}");
                    var progress = new LoggingProgress(target.VanillaDll, splash);
                    try
                    {
                        var result = PatchEngine.Patch(
                            vanillaPath, donorPath, outputPath, target.Mode, target.ModName, progress);
                        cache.SavePatchedAssembly(target.AssemblyPath, result.DllBytes, result.PdbBytes);
                        patchedTargets.Add(new PatchedTarget(target.AssemblyPath, target.DonorDll, result.PatchCount, target.VanillaDll));
                        Logger.Log($"[Optimum] ✓ {target.AssemblyPath} ({result.PatchCount} patches)");
                    }
                    catch (PatchFailedException ex)
                    {
                        Logger.LogError($"[Optimum] ✗ Patch failed: {ex.Message}");
                        fallbackReason = ex.Message;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                // Safety net: this runs on a background thread, so an
                // exception here would otherwise take down the whole
                // process via the default unhandled-exception handler
                // instead of falling back to vanilla like every other
                // failure path in this method does.
                Logger.LogError($"[Optimum] Unexpected error while patching: {ex.Message}");
                fallbackReason ??= ex.Message;
            }
        }

        if (splash is null)
        {
            RunPatchLoop();
        }
        else
        {
            var patchThread = new Thread(RunPatchLoop) { IsBackground = true, Name = "Optimum-Patcher" };
            patchThread.Start();
            try
            {
                while (patchThread.IsAlive)
                {
                    splash.PumpAndRender();
                    Thread.Sleep(16);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Optimum] Splash screen error, continuing without it: {ex.Message}");
            }
            finally
            {
                patchThread.Join();
                try { splash.Dispose(); } catch { /* best-effort */ }
            }
        }

        if (fallbackReason is not null)
        {
            return LaunchVanillaFallback(args, fallbackReason);
        }

        cache.CreateManifest(patchedTargets);
        Logger.Log();
        Logger.Log($"[Optimum] Done in {sw.ElapsedMilliseconds}ms. Cached for next launch.");
        Logger.Log();
        return null;
    }

    private static int LaunchGame(string[] args, string cacheDir, string gameDir)
    {
        // --- Load patched assemblies and launch the game ---
        using var loader = new AssemblyLoader(cacheDir, gameDir);
        loader.Register();

        // Load the primary game assembly from cache
        var vintagestoryLib = loader.LoadEntryAssembly("VintagestoryLib.dll");

        // Find and invoke ClientProgram.Main
        var clientProgramType = vintagestoryLib.GetType("Vintagestory.Client.ClientProgram");
        if (clientProgramType is null)
        {
            Logger.LogError("[Optimum] Could not find Vintagestory.Client.ClientProgram type.");
            return LaunchVanillaFallback(args, "ClientProgram type not found in patched assembly");
        }

        var mainMethod = clientProgramType.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
        if (mainMethod is null)
        {
            Logger.LogError("[Optimum] Could not find ClientProgram.Main method.");
            return LaunchVanillaFallback(args, "ClientProgram.Main not found");
        }

        // Invoke the game - this blocks until the game exits
        mainMethod.Invoke(null, [args]);
        return 0;
    }

    /// <summary>
    /// Graceful fallback: log the problem, launch vanilla Vintagestory.exe directly.
    /// The user still gets to play - just without optimizations.
    /// </summary>
    private static int LaunchVanillaFallback(string[] args, string reason)
    {
        Logger.LogError();
        Logger.LogError($"[Optimum] ⚠ Falling back to vanilla launch.");
        Logger.LogError($"[Optimum] Reason: {reason}");
        Logger.LogError();

        var gameDir = AppContext.BaseDirectory;
        RestoreVanillaMods(gameDir);
        var vanillaExe = Path.Combine(gameDir, "Vintagestory.exe");

        if (!File.Exists(vanillaExe))
        {
            // Try platform-specific names
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                vanillaExe = Path.Combine(gameDir, "Vintagestory");
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                vanillaExe = Path.Combine(gameDir, "Vintagestory");
        }

        if (File.Exists(vanillaExe))
        {
            var psi = new ProcessStartInfo
            {
                FileName = vanillaExe,
                UseShellExecute = false,
                WorkingDirectory = gameDir
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            try
            {
                using var proc = Process.Start(psi);
                proc?.WaitForExit();
                return proc?.ExitCode ?? 1;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Optimum] Failed to launch vanilla: {ex.Message}");
                return 1;
            }
        }

        Logger.LogError("[Optimum] Could not find Vintagestory.exe for fallback.");
        return 1;
    }

    private static void DeployPatchedMods(string cacheDir, string gameDir)
    {
        foreach (var target in PatchTargets)
        {
            if (target.Mode != PatchMode.Mod) continue;

            var cachedDll = Path.Combine(cacheDir, target.AssemblyPath);
            var gameDll = Path.Combine(gameDir, target.AssemblyPath);
            Directory.CreateDirectory(Path.GetDirectoryName(gameDll)!);
            File.Copy(cachedDll, gameDll, true);

            var cachedPdb = Path.ChangeExtension(cachedDll, ".pdb");
            if (File.Exists(cachedPdb))
                File.Copy(cachedPdb, Path.ChangeExtension(gameDll, ".pdb"), true);
        }
    }

    private static void RestoreVanillaMods(string gameDir)
    {
        foreach (var target in PatchTargets)
        {
            if (target.Mode != PatchMode.Mod || target.VanillaDll == target.AssemblyPath) continue;

            var backupDll = Path.Combine(gameDir, target.VanillaDll);
            var gameDll = Path.Combine(gameDir, target.AssemblyPath);
            if (!File.Exists(backupDll)) continue;

            try
            {
                File.Copy(backupDll, gameDll, true);
                var backupPdb = Path.ChangeExtension(backupDll, ".pdb");
                if (File.Exists(backupPdb))
                    File.Copy(backupPdb, Path.ChangeExtension(gameDll, ".pdb"), true);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Optimum] Could not restore {target.AssemblyPath}: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Extracts --dataPath from CLI args (same as VS does).
    /// </summary>
    private static string? ResolveDataPath(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] is "--dataPath" or "-d")
                return args[i + 1];
        }

        var configPath = Path.Combine(AppContext.BaseDirectory, "datapath.cfg");
        if (File.Exists(configPath))
        {
            var configuredPath = File.ReadAllText(configPath).Trim();
            if (configuredPath.Length > 0)
                return configuredPath;
        }

        return null;
    }

    /// <summary>Progress reporter: forwards to Logger (console + log file) and the splash screen, if any.</summary>
    private sealed class LoggingProgress(string target, PatchSplashScreen? splash) : IProgress<PatchProgress>
    {
        public void Report(PatchProgress value)
        {
            Logger.Log($"[Optimum] {target}: {value.Description}");
            splash?.SetStatus($"{target}: {value.Description}");
        }
    }
}

/// <summary>Defines a target for patching: vanilla DLL + its donor.</summary>
public enum PatchMode
{
    Transplant,
    Api,
    Mod,
}

internal record PatchTarget(
    string AssemblyPath,
    string DonorDll,
    PatchMode Mode,
    string? ModName = null,
    string? VanillaAssemblyPath = null)
{
    public string VanillaDll => VanillaAssemblyPath ?? AssemblyPath;
}
