using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Optimum.Launcher;

/// <summary>
/// Manages .NET multicore background JIT compilation profiles via <see cref="ProfileOptimization"/>
/// inside a fingerprint-keyed cache subdirectory under the active data path.
/// </summary>
internal static class JitProfileManager
{
    internal const string DisableEnvVar = "OPTIMUM_JIT_PROFILE";
    internal const string ProfileSubDirectory = "jit-profiles";
    internal const string ProfileFilePrefix = "jit-";
    internal const string ProfileFileExtension = ".profile";
    internal const string LockFileExtension = ".lock";
    internal const int MaxRetainedProfiles = 4;

    private const string SchemaVersion = "jit-profile-v1";
    private const string CliOptionPrefix = "--optimum-jit-profile=";
    private const string CliDisableFlag = "--optimum-no-jit-profile";

    private static readonly string[] CoreGameAssemblies =
    [
        "Vintagestory.dll",
        "VintagestoryLib.dll",
        "VintagestoryAPI.dll",
        "Optimum.Api.Contracts.dll",
    ];

    /// <summary>
    /// Determines whether multicore JIT profile optimization is enabled for this launch.
    /// Defaults to true unless disabled by environment variable or launcher CLI flag.
    /// </summary>
    internal static bool IsEnabled(string[]? args, Func<string, string?>? getEnvironmentVariable = null)
    {
        bool enabled = true;
        string? envValue = (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(DisableEnvVar);
        if (TryParseToggle(envValue, out bool envEnabled))
            enabled = envEnabled;

        if (args is null)
            return enabled;

        bool utilityExitOnly = false;
        foreach (string arg in args)
        {
            if (string.Equals(arg, "-v", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--version", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "-h", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(arg, "--help", StringComparison.OrdinalIgnoreCase))
            {
                utilityExitOnly = true;
                continue;
            }

            if (string.Equals(arg, CliDisableFlag, StringComparison.OrdinalIgnoreCase))
            {
                enabled = false;
                continue;
            }

            if (arg.StartsWith(CliOptionPrefix, StringComparison.OrdinalIgnoreCase))
            {
                string rawValue = arg[CliOptionPrefix.Length..];
                if (TryParseToggle(rawValue, out bool flagEnabled))
                    enabled = flagEnabled;
            }
        }

        // Utility info commands exit immediately before running the engine; starting
        // ProfileOptimization with them would overwrite the gameplay profile on exit.
        return enabled && !utilityExitOnly;
    }

    internal static string ResolveProfileRoot(string dataPath)
    {
        return Path.Combine(dataPath, ".optimum", ProfileSubDirectory);
    }

    /// <summary>
    /// Computes a deterministic profile filename keyed by runtime version, Optimum version,
    /// game version, patched assembly hashes, OS/architecture, and active mod files.
    /// </summary>
    internal static string ComputeProfileName(
        string gameDir,
        string dataPath,
        string optimumVersion,
        CacheManifest? manifest,
        string[]? args,
        Func<string?>? runtimeIdentifierProvider = null,
        Func<Architecture>? architectureProvider = null,
        Func<Version>? clrVersionProvider = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        AppendField(hash, SchemaVersion);
        AppendField(hash, (runtimeIdentifierProvider ?? (() => RuntimeInformation.RuntimeIdentifier))() ?? "");
        AppendField(hash, (architectureProvider ?? (() => RuntimeInformation.ProcessArchitecture))().ToString());
        AppendField(hash, (clrVersionProvider ?? (() => Environment.Version))().ToString());
        AppendField(hash, manifest?.OptimumVersion ?? optimumVersion ?? "");
        AppendField(hash, manifest?.GameVersion ?? DetectGameVersion(gameDir) ?? "");

        AppendField(hash, "targets");
        if (manifest?.Targets is { Count: > 0 })
        {
            var sortedTargets = new List<CacheTarget>(manifest.Targets);
            sortedTargets.Sort(static (left, right) =>
                StringComparer.OrdinalIgnoreCase.Compare(left.Assembly, right.Assembly));
            foreach (CacheTarget target in sortedTargets)
            {
                AppendField(hash, target.Assembly);
                AppendField(hash, target.CachedHash);
                AppendField(hash, target.DonorHash);
                AppendField(hash, target.VanillaHash);
            }
        }
        else
        {
            foreach (string assemblyName in CoreGameAssemblies)
                AppendFileMetadata(hash, "core", assemblyName, Path.Combine(gameDir, assemblyName));
        }

        AppendField(hash, "mods");
        AppendModDirectoryMetadata(hash, "game-mods", Path.Combine(gameDir, "Mods"));
        AppendModDirectoryMetadata(hash, "data-mods", Path.Combine(dataPath, "Mods"));

        List<string> extraModPaths = ExtractAddModPaths(args);
        for (int index = 0; index < extraModPaths.Count; index++)
            AppendModDirectoryMetadata(hash, $"extra-mods-{index}", extraModPaths[index]);

        string hexDigest = Convert.ToHexStringLower(hash.GetHashAndReset());
        return $"{ProfileFilePrefix}{hexDigest[..16]}{ProfileFileExtension}";
    }

    /// <summary>
    /// Starts multicore JIT profiling if enabled, multi-core, writable, and unlocked.
    /// Any I/O, permission, or platform issue degrades cleanly without interrupting launch.
    /// </summary>
    internal static JitProfileHandle? TryStart(
        string gameDir,
        string dataPath,
        string optimumVersion,
        CacheManifest? manifest,
        string[]? args,
        Action<string>? setProfileRoot = null,
        Action<string?>? startProfile = null,
        int? processorCountOverride = null,
        Func<string, string?>? getEnvironmentVariable = null,
        Action<string>? log = null)
    {
        Action<string> writeLog = log ?? Logger.Log;

        if (!IsEnabled(args, getEnvironmentVariable))
        {
            writeLog("[Optimum] Multicore JIT profile disabled by configuration.");
            return null;
        }

        int processorCount = processorCountOverride ?? Environment.ProcessorCount;
        if (processorCount < 2)
        {
            writeLog($"[Optimum] Multicore JIT profile skipped (processorCount={processorCount}).");
            return null;
        }

        FileStream? profileLock = null;
        try
        {
            string optimumDir = Path.Combine(dataPath, ".optimum");
            string profileRoot = ResolveProfileRoot(dataPath);

            EnsureSafeDirectory(optimumDir);
            EnsureSafeDirectory(profileRoot);

            string profileName = ComputeProfileName(gameDir, dataPath, optimumVersion, manifest, args);
            string profilePath = Path.Combine(profileRoot, profileName);
            string lockPath = profilePath + LockFileExtension;

            try
            {
                profileLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                writeLog($"[Optimum] Multicore JIT profile skipped (profile in use: {ex.Message}).");
                return null;
            }

            PruneStaleProfiles(profileRoot, profileName);

            bool warmProfile = false;
            if (File.Exists(profilePath))
            {
                var info = new FileInfo(profilePath);
                warmProfile = info.Length > 0;
            }

            Action<string> applyRoot = setProfileRoot ?? ProfileOptimization.SetProfileRoot;
            Action<string?> applyProfile = startProfile ?? ProfileOptimization.StartProfile;

            applyRoot(profileRoot);
            applyProfile(profileName);

            writeLog(warmProfile
                ? $"[Optimum] Multicore JIT profile active (warm): {profileName}"
                : $"[Optimum] Multicore JIT profile recording (cold): {profileName}");

            return new JitProfileHandle(profileRoot, profileName, warmProfile, profileLock, applyProfile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or PlatformNotSupportedException)
        {
            profileLock?.Dispose();
            writeLog($"[Optimum] Note: multicore JIT profiling skipped: {ex.Message}");
            return null;
        }
    }

    internal static void PruneStaleProfiles(string profileRoot, string activeProfileName, int maxRetained = MaxRetainedProfiles)
    {
        if (!Directory.Exists(profileRoot))
            return;

        int keepOtherCount = Math.Max(0, maxRetained - 1);
        var candidates = new List<(string Path, DateTime LastWriteUtc)>();

        foreach (string path in Directory.EnumerateFiles(profileRoot, $"{ProfileFilePrefix}*{ProfileFileExtension}", SearchOption.TopDirectoryOnly))
        {
            if (!path.EndsWith(ProfileFileExtension, StringComparison.OrdinalIgnoreCase))
                continue;

            string fileName = Path.GetFileName(path);
            if (string.Equals(fileName, activeProfileName, StringComparison.OrdinalIgnoreCase))
                continue;

            try
            {
                candidates.Add((path, File.GetLastWriteTimeUtc(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ignore transient metadata access errors during best-effort pruning.
            }
        }

        if (candidates.Count <= keepOtherCount)
            return;

        candidates.Sort(static (left, right) => right.LastWriteUtc.CompareTo(left.LastWriteUtc));

        for (int index = keepOtherCount; index < candidates.Count; index++)
        {
            string staleProfilePath = candidates[index].Path;
            string staleLockPath = staleProfilePath + LockFileExtension;
            FileStream? staleLock = null;
            try
            {
                staleLock = new FileStream(staleLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                File.Delete(staleProfilePath);
                staleLock.Dispose();
                staleLock = null;
                File.Delete(staleLockPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Active concurrent processes hold their lock file; leave in place.
            }
            finally
            {
                staleLock?.Dispose();
            }
        }
    }

    internal static List<string> ExtractAddModPaths(string[]? args)
    {
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (args is null)
            return [];

        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            string? value = null;
            if (string.Equals(arg, "--addModPath", StringComparison.OrdinalIgnoreCase))
            {
                if (index + 1 < args.Length)
                {
                    value = args[index + 1];
                    index++;
                }
            }
            else if (arg.StartsWith("--addModPath=", StringComparison.OrdinalIgnoreCase))
            {
                value = arg["--addModPath=".Length..];
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                try
                {
                    paths.Add(Path.GetFullPath(value));
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException or IOException)
                {
                    paths.Add(value);
                }
            }
        }

        var sorted = new List<string>(paths);
        sorted.Sort(StringComparer.Ordinal);
        return sorted;
    }

    private static void EnsureSafeDirectory(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        if ((File.GetAttributes(directoryPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException($"Directory '{directoryPath}' is a reparse point or symlink.");
    }

    private static void AppendModDirectoryMetadata(IncrementalHash hash, string scope, string directoryPath)
    {
        AppendField(hash, scope);
        if (!Directory.Exists(directoryPath))
        {
            AppendField(hash, "missing");
            return;
        }

        try
        {
            if ((File.GetAttributes(directoryPath) & FileAttributes.ReparsePoint) != 0)
            {
                AppendField(hash, "reparse-point");
                return;
            }

            var modFiles = new List<(string RelativePath, string FullPath)>();
            var pendingDirs = new Stack<string>();
            pendingDirs.Push(directoryPath);
            while (pendingDirs.Count > 0)
            {
                string currentDir = pendingDirs.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(currentDir))
                {
                    FileAttributes attributes = File.GetAttributes(entry);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        continue;

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pendingDirs.Push(entry);
                        continue;
                    }

                    string extension = Path.GetExtension(entry);
                    if (!IsTrackableModExtension(extension))
                        continue;

                    string relativePath = Path.GetRelativePath(directoryPath, entry).Replace('\\', '/');
                    modFiles.Add((relativePath, entry));
                }
            }

            modFiles.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
            foreach (var modFile in modFiles)
                AppendFileMetadata(hash, scope, modFile.RelativePath, modFile.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendField(hash, $"error:{ex.GetType().Name}");
        }
    }

    private static bool IsTrackableModExtension(string extension)
    {
        return string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(extension, ".cs", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendFileMetadata(IncrementalHash hash, string scope, string label, string fullPath)
    {
        AppendField(hash, scope);
        AppendField(hash, label);
        try
        {
            if (!File.Exists(fullPath))
            {
                AppendField(hash, "missing");
                return;
            }

            var info = new FileInfo(fullPath);
            AppendField(hash, info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AppendField(hash, info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppendField(hash, $"unreadable:{ex.GetType().Name}");
        }
    }

    private static string? DetectGameVersion(string gameDir)
    {
        string versionFile = Path.Combine(gameDir, "version.txt");
        if (File.Exists(versionFile))
        {
            try
            {
                return File.ReadAllText(versionFile).Trim();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        string assetsDir = Path.Combine(gameDir, "assets");
        if (!Directory.Exists(assetsDir))
            return null;

        try
        {
            foreach (string path in Directory.EnumerateFiles(assetsDir, "version-*.txt", SearchOption.TopDirectoryOnly))
                return Path.GetFileNameWithoutExtension(path)["version-".Length..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return null;
    }

    private static bool TryParseToggle(string? rawValue, out bool enabled)
    {
        enabled = true;
        if (string.IsNullOrWhiteSpace(rawValue))
            return false;

        string normalized = rawValue.Trim().ToLowerInvariant();
        if (normalized is "0" or "false" or "off" or "disable" or "disabled" or "no")
        {
            enabled = false;
            return true;
        }

        if (normalized is "1" or "true" or "on" or "enable" or "enabled" or "yes")
        {
            enabled = true;
            return true;
        }

        return false;
    }

    private static void AppendField(IncrementalHash hash, string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        Span<byte> lengthPrefix = stackalloc byte[4];
        int networkLength = IPAddress.HostToNetworkOrder(bytes.Length);
        BitConverter.TryWriteBytes(lengthPrefix, networkLength);
        hash.AppendData(lengthPrefix);
        hash.AppendData(bytes);
    }
}

internal sealed class JitProfileHandle : IDisposable
{
    private readonly FileStream lockStream;
    private readonly Action<string?> startProfile;
    private bool disposed;

    internal JitProfileHandle(
        string profileRoot,
        string profileName,
        bool isWarmProfile,
        FileStream lockStream,
        Action<string?> startProfile)
    {
        ProfileRoot = profileRoot;
        ProfileName = profileName;
        IsWarmProfile = isWarmProfile;
        this.lockStream = lockStream;
        this.startProfile = startProfile;
    }

    internal string ProfileRoot { get; }
    internal string ProfileName { get; }
    internal bool IsWarmProfile { get; }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;

        try
        {
            startProfile(null);
        }
        catch
        {
            // Best-effort profile stop/flush on shutdown.
        }
        finally
        {
            lockStream.Dispose();
        }
    }
}
