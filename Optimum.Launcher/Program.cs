using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Optimum.Launcher;

/// <summary>
/// Optimum runtime launcher. Applies Cecil patches on first launch (cached
/// thereafter), then invokes Vintagestory's ClientProgram.Main via reflection.
/// </summary>
public static class Program
{
    private const string ValidateOnlyArgument = "--validate-only";

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
            if (!Logger.IsInitialized)
                Logger.Init(AppContext.BaseDirectory);
            Logger.LogError($"[Optimum] Fatal error: {ex.Message}");
            Logger.LogError(ex.StackTrace ?? "(no stack trace)");
            return 1;
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

        ShaderCompatibilityReport shaderCompatibility;
        try
        {
            shaderCompatibility = ShaderCompatibilityScanner.Scan(dataPath, gameDir, Version);
            ShaderCompatibilityScanner.SaveReport(dataPath, shaderCompatibility);
        }
        catch (Exception ex)
        {
            shaderCompatibility = ShaderCompatibilityScanner.CreateConservativeReport(Version, ex.Message);
            try { ShaderCompatibilityScanner.SaveReport(dataPath, shaderCompatibility); } catch { }
        }
        Logger.Log($"[Optimum] Shader compatibility scan: sources={shaderCompatibility.Sources.Count}, " +
            $"shaders={shaderCompatibility.ShaderOwners.Count}, conflicts={shaderCompatibility.Conflicts.Count}, " +
            $"failed={shaderCompatibility.ScanFailed}, fingerprint={shaderCompatibility.Fingerprint}");
        foreach (var conflict in shaderCompatibility.Conflicts)
            Logger.Log($"[Optimum] Shader owner: {conflict.Shader} <- {string.Join(", ", conflict.Owners)} ({conflict.Reason})");
        foreach (var feature in shaderCompatibility.DisabledFeatures.Order(StringComparer.OrdinalIgnoreCase))
        {
            string reason = shaderCompatibility.FeatureReasons.TryGetValue(feature, out var reasons)
                ? string.Join("; ", reasons)
                : "compatibility report";
            Logger.Log($"[Optimum] Shader feature disabled: {feature} ({reason})");
        }
        using var launchLock = AcquireLaunchLock(dataPath, gameDir);
        var cacheDir = Path.Combine(dataPath, CacheDirName, CacheSubDir);
        var donorDir = Path.Combine(gameDir, CacheDirName, "donors");

        var cache = new CacheManager(gameDir, cacheDir, donorDir, Version);
        bool validateOnly = HasArgument(args, ValidateOnlyArgument);

        // --- Cache validation ---
        var requiredAssemblies = PatchTargets.Select(target => target.AssemblyPath).ToArray();
        CacheManifest? manifest;
        try
        {
            manifest = validateOnly
                ? null
                : cache.ValidateCache(requiredAssemblies);

            if (validateOnly)
            {
                Logger.Log("[Optimum] Validation mode: rebuilding the patched runtime.");
                cache.Invalidate();
            }
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Optimum] Cache validation failed: {ex.Message}");
            return AbortLaunch(gameDir, cache, ex.Message);
        }

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
            int? patchExitCode;
            try
            {
                patchExitCode = RunPatching(
                    gameDir, donorDir, cacheDir, cache, sw, showSplash: !validateOnly);
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Optimum] Runtime patch setup failed: {ex.Message}");
                return AbortLaunch(gameDir, cache, ex.Message);
            }
            if (patchExitCode is int code)
            {
                return code;
            }
        }

        if (validateOnly)
        {
            try
            {
                ValidatePatchedRuntime(cacheDir, gameDir);
                Logger.Log("[Optimum] Runtime patch validation succeeded.");
                return 0;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[Optimum] Runtime validation failed: {ex.Message}");
                return AbortLaunch(gameDir, cache, ex.Message);
            }
        }

        try
        {
            DeployPatchedMods(cacheDir, gameDir);
            return LaunchGame(args, cacheDir, gameDir, cache);
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Optimum] Runtime startup failed: {ex.Message}");
            return AbortLaunch(gameDir, cache, ex.Message);
        }
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
    /// loop. Returns null on success or a nonzero exit code after a failure.
    /// </summary>
    private static int? RunPatching(
        string gameDir,
        string donorDir,
        string cacheDir,
        CacheManager cache,
        Stopwatch sw,
        bool showSplash)
    {
        Logger.Log($"[Optimum] Optimum v{Version}");
        Logger.Log($"[Optimum] Applying optimizations...");
        Logger.Log();

        cache.Invalidate();

        PatchSplashScreen? splash = null;
        if (showSplash)
        {
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
        }

        var patchedTargets = new List<PatchedTarget>();
        string? failureReason = null;

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
                        failureReason = $"Missing: {target.AssemblyPath}";
                        return;
                    }

                    if (!File.Exists(donorPath))
                    {
                        Logger.LogError($"[Optimum] Donor DLL not found: {donorPath}");
                        Logger.LogError($"[Optimum] Expected at: {donorPath}");
                        failureReason = $"Missing donor: {target.DonorDll}";
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
                        failureReason = ex.Message;
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                // Safety net: this runs on a background thread, so an
                // exception here would otherwise take down the whole
                // process via the default unhandled-exception handler.
                Logger.LogError($"[Optimum] Unexpected error while patching: {ex.Message}");
                failureReason ??= ex.Message;
            }
        }

        if (!showSplash)
        {
            RunPatchLoop();
        }
        else if (splash is null)
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

        if (failureReason is not null)
        {
            return AbortLaunch(gameDir, cache, failureReason);
        }

        cache.CreateManifest(patchedTargets);
        Logger.Log();
        Logger.Log($"[Optimum] Done in {sw.ElapsedMilliseconds}ms. Cached for next launch.");
        Logger.Log();
        return null;
    }

    private static int LaunchGame(string[] args, string cacheDir, string gameDir, CacheManager cache)
    {
        // --- Load patched assemblies and launch the game ---
        using var loader = new AssemblyLoader(cacheDir, gameDir);
        loader.Register();

        // Load every patched assembly before the game can resolve a vanilla
        // copy from the normal probing paths.
        var patchedAssemblies = LoadAllPatchedAssemblies(loader);
        var vintagestoryLib = patchedAssemblies[0];

        // Find and invoke ClientProgram.Main
        var clientProgramType = vintagestoryLib.GetType("Vintagestory.Client.ClientProgram");
        if (clientProgramType is null)
        {
            return AbortLaunch(
                gameDir,
                cache,
                "ClientProgram type not found in the patched assembly.");
        }

        var mainMethod = clientProgramType.GetMethod("Main", BindingFlags.Public | BindingFlags.Static);
        if (mainMethod is null)
        {
            return AbortLaunch(
                gameDir,
                cache,
                "ClientProgram.Main not found in the patched assembly.");
        }

        // Invoke the game - this blocks until the game exits
        mainMethod.Invoke(null, [args]);
        return 0;
    }

    private static void ValidatePatchedRuntime(string cacheDir, string gameDir)
    {
        using var loader = new AssemblyLoader(cacheDir, gameDir);
        loader.Register();

        var patchedAssemblies = LoadAllPatchedAssemblies(loader);
        for (int i = 0; i < PatchTargets.Length; i++)
        {
            var target = PatchTargets[i];
            var assembly = patchedAssemblies[i];
            var expectedName = Path.GetFileNameWithoutExtension(target.AssemblyPath);
            if (!string.Equals(assembly.GetName().Name, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Patched assembly identity mismatch for {target.AssemblyPath}: " +
                    $"loaded {assembly.GetName().Name ?? "(unnamed)"}.");
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                string details = string.Join(
                    " | ",
                    ex.LoaderExceptions
                        .Where(error => error is not null)
                        .Select(error => error!.Message));
                throw new InvalidOperationException(
                    $"Could not load all types from {target.AssemblyPath}: {details}", ex);
            }

            PreparePatchedMethods(target.AssemblyPath, types);

        }

        var clientProgramType = patchedAssemblies[0].GetType("Vintagestory.Client.ClientProgram")
            ?? throw new InvalidOperationException(
                "ClientProgram type not found in the patched VintagestoryLib.dll.");
        _ = clientProgramType.GetMethod("Main", BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "ClientProgram.Main not found in the patched VintagestoryLib.dll.");

        Logger.Log($"[Optimum] Loaded and reflected {patchedAssemblies.Length} patched assemblies.");
    }

    private static void PreparePatchedMethods(string assemblyPath, IReadOnlyCollection<Type> types)
    {
        int prepared = 0;
        foreach (var type in types)
        {
            var methods = type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.Static | BindingFlags.DeclaredOnly);
            foreach (var method in methods)
            {
                PrepareMethod(assemblyPath, method, ref prepared);
            }

            var constructors = type.GetConstructors(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                BindingFlags.Static);
            foreach (var constructor in constructors)
            {
                PrepareMethod(assemblyPath, constructor, ref prepared);
            }
        }

        Logger.Log($"[Optimum] JIT-validated {prepared} patched methods in {assemblyPath}.");
    }

    private static void PrepareMethod(string assemblyPath, MethodBase method, ref int prepared)
    {
        if (method.IsAbstract ||
            (method.Attributes & MethodAttributes.PinvokeImpl) != 0 ||
            (method.GetMethodImplementationFlags() & MethodImplAttributes.Runtime) != 0 ||
            method.ContainsGenericParameters)
            return;

        try
        {
            RuntimeHelpers.PrepareMethod(method.MethodHandle);
            prepared++;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"JIT validation failed for {assemblyPath}::{method.DeclaringType?.FullName}::{method.Name}: " +
                $"{ex.GetType().Name}: {ex.Message}", ex);
        }
    }

    private static Assembly[] LoadAllPatchedAssemblies(AssemblyLoader loader)
    {
        return PatchTargets
            .Select(target => loader.LoadEntryAssembly(target.AssemblyPath))
            .ToArray();
    }

    private static int AbortLaunch(string gameDir, CacheManager cache, string reason)
    {
        if (!cache.TryInvalidate(out var invalidationError))
        {
            Logger.LogError($"[Optimum] Could not invalidate the cache: {invalidationError}");
        }
        Logger.LogError();
        Logger.LogError("[Optimum] Launch aborted. Optimum did not produce a complete patched runtime.");
        Logger.LogError($"[Optimum] Reason: {reason}");
        TryRestoreVanillaMods(gameDir);
        Logger.LogError();
        return 1;
    }

    private static void TryRestoreVanillaMods(string gameDir)
    {
        try
        {
            RestoreVanillaMods(gameDir);
            return;
        }
        catch (Exception ex)
        {
            Logger.LogError($"[Optimum] Could not restore vanilla built-in mods: {ex.Message}");
        }
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
        var plans = new List<RestorePlan>();
        foreach (var target in PatchTargets)
        {
            if (target.Mode != PatchMode.Mod || target.VanillaDll == target.AssemblyPath) continue;

            var backupDll = Path.Combine(gameDir, target.VanillaDll);
            var gameDll = Path.Combine(gameDir, target.AssemblyPath);
            if (!File.Exists(backupDll))
            {
                throw new FileNotFoundException(
                    $"Vanilla backup is missing for {target.AssemblyPath}.", backupDll);
            }

            var backupPdb = Path.ChangeExtension(backupDll, ".pdb");
            plans.Add(new RestorePlan(
                backupDll,
                gameDll,
                File.Exists(backupPdb) ? backupPdb : null,
                Path.ChangeExtension(gameDll, ".pdb")));
        }

        var committed = new List<RestorePlan>();
        try
        {
            foreach (var plan in plans)
            {
                plan.TempDll = $"{plan.GameDll}.restore-{Guid.NewGuid():N}.tmp";
                File.Copy(plan.BackupDll, plan.TempDll, false);
                if (plan.BackupPdb is not null)
                {
                    plan.TempPdb = $"{plan.GamePdb}.restore-{Guid.NewGuid():N}.tmp";
                    File.Copy(plan.BackupPdb, plan.TempPdb, false);
                }
            }

            foreach (var plan in plans)
            {
                File.Move(plan.TempDll!, plan.GameDll, true);
                committed.Add(plan);
                if (plan.TempPdb is not null)
                    File.Move(plan.TempPdb, plan.GamePdb, true);
                else if (File.Exists(plan.GamePdb))
                    File.Delete(plan.GamePdb);
            }
        }
        catch (Exception ex)
        {
            foreach (var plan in committed)
            {
                try
                {
                    File.Copy(plan.BackupDll, plan.GameDll, true);
                    if (plan.BackupPdb is not null)
                        File.Copy(plan.BackupPdb, plan.GamePdb, true);
                    else if (File.Exists(plan.GamePdb))
                        File.Delete(plan.GamePdb);
                }
                catch (Exception rollbackError)
                {
                    throw new InvalidOperationException(
                        $"Vanilla mod restoration failed and rollback failed for {plan.GameDll}: " +
                        rollbackError.Message,
                        ex);
                }
            }

            throw new InvalidOperationException(
                "Vanilla built-in mod restoration failed before all files were restored.", ex);
        }
        finally
        {
            foreach (var plan in plans)
            {
                if (plan.TempDll is not null && File.Exists(plan.TempDll))
                    File.Delete(plan.TempDll);
                if (plan.TempPdb is not null && File.Exists(plan.TempPdb))
                    File.Delete(plan.TempPdb);
            }
        }
    }

    private static IDisposable AcquireLaunchLock(string dataPath, string gameDir)
    {
        string dataLockPath = Path.Combine(Path.GetFullPath(dataPath), CacheDirName, "launcher.lock");
        string gameLockPath = Path.Combine(Path.GetFullPath(gameDir), CacheDirName, "game.lock");
        var lockPaths = new[] { dataLockPath, gameLockPath }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var locks = new List<FileStream>(lockPaths.Length);
        try
        {
            foreach (var lockPath in lockPaths)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
                locks.Add(new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None));
            }

            return new LaunchLock(locks);
        }
        catch (IOException ex)
        {
            foreach (var handle in locks)
                handle.Dispose();
            throw new InvalidOperationException(
                "Another Optimum launcher instance already owns the game or data path lock.", ex);
        }
    }

    private sealed class LaunchLock(IReadOnlyList<FileStream> handles) : IDisposable
    {
        public void Dispose()
        {
            for (int i = handles.Count - 1; i >= 0; i--)
                handles[i].Dispose();
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

    private static bool HasArgument(string[] args, string expected)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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

internal sealed class RestorePlan(
    string backupDll,
    string gameDll,
    string? backupPdb,
    string gamePdb)
{
    public string BackupDll { get; } = backupDll;
    public string GameDll { get; } = gameDll;
    public string? BackupPdb { get; } = backupPdb;
    public string GamePdb { get; } = gamePdb;
    public string? TempDll { get; set; }
    public string? TempPdb { get; set; }
}
