using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Optimum.Launcher;

/// <summary>
/// Manages the patched DLL cache. Validates cache freshness using a manifest
/// that records hashes/mtimes of vanilla inputs and the patcher version.
/// </summary>
public sealed class CacheManager
{
    private readonly string _cacheDir;
    private readonly string _donorDir;
    private readonly string _gameDir;
    private readonly string _optimumVersion;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public CacheManager(string gameDir, string cacheDir, string donorDir, string optimumVersion)
    {
        _gameDir = gameDir;
        _cacheDir = cacheDir;
        _donorDir = donorDir;
        _optimumVersion = optimumVersion;
        Directory.CreateDirectory(_cacheDir);
    }

    public string CacheDir => _cacheDir;
    public string ManifestPath => Path.Combine(_cacheDir, "manifest.json");

    /// <summary>
    /// Validates the cache. Returns the manifest if valid, null if a re-patch is needed.
    /// Checks the manifest inputs and the cached assembly hash before launch.
    /// </summary>
    public CacheManifest? ValidateCache(IReadOnlyCollection<string>? requiredAssemblies = null)
    {
        return ValidateCacheCore(requiredAssemblies);
    }

    private CacheManifest? ValidateCacheCore(IReadOnlyCollection<string>? requiredAssemblies)
    {
        if (!File.Exists(ManifestPath))
            return null;

        string json = File.ReadAllText(ManifestPath);
        CacheManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CacheManifest>(json, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }

        if (manifest is null)
            return null;

        if (requiredAssemblies is not null)
        {
            foreach (string requiredAssembly in requiredAssemblies)
            {
                bool listed = manifest.Targets.Exists(target =>
                    string.Equals(target.Assembly, requiredAssembly, StringComparison.OrdinalIgnoreCase));
                if (!listed || !File.Exists(Path.Combine(_cacheDir, requiredAssembly)))
                    return null;
            }
        }

        // Version check
        if (manifest.OptimumVersion != _optimumVersion)
            return null;

        // Validate each target
        foreach (var target in manifest.Targets)
        {
            var vanillaPath = Path.Combine(_gameDir, target.VanillaAssembly ?? target.Assembly);
            if (!File.Exists(vanillaPath))
                return null;

            var cachedPath = Path.Combine(_cacheDir, target.Assembly);
            if (!File.Exists(cachedPath) || string.IsNullOrEmpty(target.CachedHash) || ComputeFileHash(cachedPath) != target.CachedHash)
                return null;

            if (string.Equals(target.Assembly, "VintagestoryAPI.dll", StringComparison.OrdinalIgnoreCase) &&
                !ApiContractValidator.HasTesselationThreadContract(cachedPath))
            {
                return null;
            }

            var donorPath = Path.Combine(_donorDir, target.Donor);
            if (!File.Exists(donorPath) || ComputeFileHash(donorPath) != target.DonorHash)
                return null;

            // Fast-path: check mtime + size
            var info = new FileInfo(vanillaPath);
            if (info.Length != target.VanillaSize ||
                info.LastWriteTimeUtc.Ticks != target.VanillaMtimeTicks)
            {
                // Mtime/size changed - verify with full hash
                var actualHash = ComputeFileHash(vanillaPath);
                if (actualHash != target.VanillaHash)
                    return null;

                // Hash still matches (mtime was touched but content unchanged) - update mtime record
                target.VanillaMtimeTicks = info.LastWriteTimeUtc.Ticks;
                target.VanillaSize = info.Length;
                SaveManifest(manifest);
            }
        }

        return manifest;
    }

    /// <summary>
    /// Creates a new cache manifest after patching completes.
    /// </summary>
    public CacheManifest CreateManifest(IReadOnlyList<PatchedTarget> targets)
    {
        var manifest = new CacheManifest
        {
            OptimumVersion = _optimumVersion,
            GameVersion = DetectGameVersion(),
            CreatedAt = DateTime.UtcNow,
            Targets = new List<CacheTarget>()
        };

        foreach (var t in targets)
        {
            var vanillaPath = Path.Combine(_gameDir, t.VanillaAssemblyName ?? t.AssemblyName);
            var vanillaInfo = new FileInfo(vanillaPath);
            var donorPath = Path.Combine(_donorDir, t.DonorName);
            var cachedPath = Path.Combine(_cacheDir, t.AssemblyName);
            if (!File.Exists(cachedPath) || new FileInfo(cachedPath).Length == 0)
                throw new FileNotFoundException("Patched assembly is missing from the cache.", cachedPath);

            if (string.Equals(t.AssemblyName, "VintagestoryAPI.dll", StringComparison.OrdinalIgnoreCase))
                ApiContractValidator.EnsureTesselationThreadContract(cachedPath);

            manifest.Targets.Add(new CacheTarget
            {
                Assembly = t.AssemblyName,
                VanillaAssembly = t.VanillaAssemblyName,
                Donor = t.DonorName,
                DonorHash = ComputeFileHash(donorPath),
                VanillaHash = ComputeFileHash(vanillaPath),
                VanillaSize = vanillaInfo.Length,
                VanillaMtimeTicks = vanillaInfo.LastWriteTimeUtc.Ticks,
                CachedHash = ComputeFileHash(cachedPath),
                PatchCount = t.PatchCount
            });
        }

        SaveManifest(manifest);
        return manifest;
    }

    /// <summary>
    /// Invalidates the cache by deleting the manifest.
    /// </summary>
    public void Invalidate()
    {
        if (Directory.Exists(ManifestPath))
            throw new IOException($"Cache manifest path is a directory: {ManifestPath}");

        if (File.Exists(ManifestPath))
            File.Delete(ManifestPath);
    }

    public bool TryInvalidate(out string? failureReason)
    {
        try
        {
            Invalidate();
            failureReason = null;
            return true;
        }
        catch (Exception ex)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// Saves the patched assembly bytes + PDB to the cache directory.
    /// </summary>
    public void SavePatchedAssembly(string assemblyName, byte[] dllBytes, byte[]? pdbBytes)
    {
        if (dllBytes is null || dllBytes.Length == 0)
            throw new ArgumentException("The patched assembly cannot be empty.", nameof(dllBytes));

        var outputPath = Path.Combine(_cacheDir, assemblyName);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        WriteBytesAtomically(outputPath, dllBytes);

        var pdbPath = Path.ChangeExtension(outputPath, ".pdb");
        if (pdbBytes is not null && pdbBytes.Length > 0)
        {
            WriteBytesAtomically(pdbPath, pdbBytes);
        }
        else if (File.Exists(pdbPath))
        {
            File.Delete(pdbPath);
        }
    }

    public string GetCachedPath(string assemblyName) =>
        Path.Combine(_cacheDir, assemblyName);

    private void SaveManifest(CacheManifest manifest)
    {
        var json = JsonSerializer.Serialize(manifest, JsonOpts);
        WriteTextAtomically(ManifestPath, json);
    }

    private static void WriteBytesAtomically(string path, byte[] bytes)
    {
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static void WriteTextAtomically(string path, string contents)
    {
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temporaryPath, contents);
            File.Move(temporaryPath, path, true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private string? DetectGameVersion()
    {
        // Try reading from GameVersion const in VintagestoryAPI.dll - but we can't
        // reference it. Fall back to checking a version file if present.
        var versionFile = Path.Combine(_gameDir, "version.txt");
        if (File.Exists(versionFile))
            return File.ReadAllText(versionFile).Trim();
        return null;
    }

    public static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return "sha256:" + Convert.ToHexStringLower(hash);
    }
}

public sealed class CacheManifest
{
    public string OptimumVersion { get; set; } = "";
    public string? GameVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<CacheTarget> Targets { get; set; } = [];
}

public sealed class CacheTarget
{
    public string Assembly { get; set; } = "";
    public string? VanillaAssembly { get; set; }
    public string Donor { get; set; } = "";
    public string DonorHash { get; set; } = "";
    public string VanillaHash { get; set; } = "";
    public long VanillaSize { get; set; }
    public long VanillaMtimeTicks { get; set; }
    public string CachedHash { get; set; } = "";
    public int PatchCount { get; set; }
}

/// <summary>Represents a successfully patched assembly for manifest creation.</summary>
public record PatchedTarget(string AssemblyName, string DonorName, int PatchCount, string? VanillaAssemblyName = null);
