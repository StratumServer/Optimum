using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace Optimum.Launcher;

/// <summary>
/// Manages assembly resolution for patched + vanilla assemblies.
/// Registers an AppDomain.AssemblyResolve handler that:
///   1. Checks the cache dir for patched DLLs first.
///   2. Falls back to the game dir for vanilla/unpatched DLLs.
///   3. Falls back to Optimum.exe's own directory for shared deps.
/// </summary>
public sealed class AssemblyLoader : IDisposable
{
    private static readonly HashSet<string> RequiredPatchedAssemblies = new(StringComparer.OrdinalIgnoreCase)
    {
        "VintagestoryLib",
        "VintagestoryAPI",
        "VSEssentials",
        "VSSurvivalMod",
    };

    private readonly string _cacheDir;
    private readonly string _gameDir;
    private readonly string _launcherDir;
    private readonly Dictionary<string, Assembly> _loaded = new(StringComparer.OrdinalIgnoreCase);
    private bool _registered;

    public AssemblyLoader(string cacheDir, string gameDir)
    {
        _cacheDir = cacheDir;
        _gameDir = gameDir;
        _launcherDir = AppContext.BaseDirectory;
    }

    /// <summary>
    /// Registers the AssemblyResolve handler. Must be called before
    /// invoking any code that references VS assemblies.
    /// </summary>
    public void Register()
    {
        if (_registered) return;
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        _registered = true;
    }

    /// <summary>
    /// Loads the primary entry assembly (VintagestoryLib.dll) from cache.
    /// </summary>
    public Assembly LoadEntryAssembly(string assemblyFileName)
    {
        var expectedName = Path.GetFileNameWithoutExtension(assemblyFileName);
        if (_loaded.TryGetValue(expectedName, out var loaded))
            return loaded;

        var cachedPath = Path.Combine(_cacheDir, assemblyFileName);
        if (File.Exists(cachedPath))
        {
            var asm = Assembly.LoadFrom(cachedPath);
            var name = asm.GetName().Name;
            if (!string.Equals(name, expectedName, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Patched assembly identity mismatch: {assemblyFileName} contains {name ?? "(unnamed)"}.");
            }
            if (!string.Equals(
                    Path.GetFullPath(asm.Location),
                    Path.GetFullPath(cachedPath),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Patched assembly was resolved from an unexpected path: {assemblyFileName} " +
                    $"loaded from {asm.Location}, expected {cachedPath}.");
            }
            _loaded[expectedName] = asm;
            return asm;
        }

        throw new FileNotFoundException(
            $"Required patched entry assembly is missing from the cache: {assemblyFileName}",
            cachedPath);
    }

    private Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var asmName = new AssemblyName(args.Name).Name;
        if (asmName is null)
            return null;

        // Already loaded?
        if (_loaded.TryGetValue(asmName, out var cached))
            return cached;

        // Try cache dir first (patched assemblies)
        var cachedPath = Path.Combine(_cacheDir, asmName + ".dll");
        if (File.Exists(cachedPath))
        {
            var asm = Assembly.LoadFrom(cachedPath);
            _loaded[asmName] = asm;
            return asm;
        }

        var cachedModPath = Path.Combine(_cacheDir, "Mods", asmName + ".dll");
        if (File.Exists(cachedModPath))
        {
            var asm = Assembly.LoadFrom(cachedModPath);
            _loaded[asmName] = asm;
            return asm;
        }

        if (RequiredPatchedAssemblies.Contains(asmName))
        {
            throw new FileNotFoundException(
                $"Required patched assembly is missing from the cache: {asmName}.dll",
                cachedPath);
        }

        // Try game dir (vanilla / unpatched assemblies)
        var gamePath = Path.Combine(_gameDir, asmName + ".dll");
        if (File.Exists(gamePath))
        {
            var asm = Assembly.LoadFrom(gamePath);
            _loaded[asmName] = asm;
            return asm;
        }

        var gameModPath = Path.Combine(_gameDir, "Mods", asmName + ".dll");
        if (File.Exists(gameModPath))
        {
            var asm = Assembly.LoadFrom(gameModPath);
            _loaded[asmName] = asm;
            return asm;
        }

        // Try Lib/ subfolder (VS puts some deps there)
        var libPath = Path.Combine(_gameDir, "Lib", asmName + ".dll");
        if (File.Exists(libPath))
        {
            var asm = Assembly.LoadFrom(libPath);
            _loaded[asmName] = asm;
            return asm;
        }

        // Try launcher's own directory (shared deps like Mono.Cecil)
        var launcherPath = Path.Combine(_launcherDir, asmName + ".dll");
        if (File.Exists(launcherPath))
        {
            var asm = Assembly.LoadFrom(launcherPath);
            _loaded[asmName] = asm;
            return asm;
        }

        return null;
    }

    public void Dispose()
    {
        if (_registered)
        {
            AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;
            _registered = false;
        }
    }
}
