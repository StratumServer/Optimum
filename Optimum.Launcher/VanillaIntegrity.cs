using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Optimum.Bootstrap.Core.Acquisition;

namespace Optimum.Launcher;

/// <summary>
/// Optimum patches the game's VintagestoryAPI.dll and VintagestoryLib.dll from the folder it runs in, so those
/// two files must be the official build this release was made for. Patching a modified game, or a file that
/// Optimum already patched, used to fail deep inside the patcher with a message about an IL call count.
/// The pins are chosen by the version of the game in the folder, so a package built against another version
/// that has no pins is not refused.
/// </summary>
internal static class VanillaIntegrity
{
    /// <summary>Set to 1 to skip the check, for example when testing against another game build.</summary>
    internal const string SkipVariable = "OPTIMUM_SKIP_VANILLA_CHECK";

    /// <summary>The problem with the game files in <paramref name="gameDir"/>, or null when they are the official build.</summary>
    internal static string? FindProblem(string gameDir)
    {
        if (Environment.GetEnvironmentVariable(SkipVariable) == "1")
            return null;

        using Stream? forks = typeof(VanillaIntegrity).Assembly.GetManifestResourceStream("forks.json");
        if (forks is null)
        {
            if (Logger.IsInitialized)
                Logger.LogError("[Optimum] The pinned Vintage Story checksums are missing from this build, so the game files are not verified.");
            return null;
        }

        using var reader = new StreamReader(forks);
        return FindProblem(gameDir, reader.ReadToEnd());
    }

    internal static string? FindProblem(string gameDir, string forksJson)
    {
        string? version = GameVersion(gameDir);
        IReadOnlyDictionary<string, string> expected;
        try
        {
            using JsonDocument forks = JsonDocument.Parse(forksJson);
            version ??= forks.RootElement.GetProperty("vintageStoryVersion").GetString();
            expected = VanillaArchivePins.FromJson(forksJson).FindExtractedFiles(version!);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or InvalidDataException)
        {
            return null;
        }

        // The game ships VintagestoryLib.dll; the pins name its pristine copy VintagestoryLib.vanilla.dll.
        var files = new (string Name, string? Sha256)[]
        {
            ("VintagestoryAPI.dll", expected.GetValueOrDefault("VintagestoryAPI.dll")),
            ("VintagestoryLib.dll", expected.GetValueOrDefault("VintagestoryLib.vanilla.dll")),
        };
        foreach ((string name, string? sha256) in files)
        {
            string path = Path.Combine(gameDir, name);
            if (sha256 is null || !File.Exists(path))
                continue; // A version without pins is not checked, and a missing file is reported by the patch loop.

            using FileStream stream = File.OpenRead(path);
            string actual = Convert.ToHexString(SHA256.HashData(stream));
            if (!string.Equals(sha256, actual, StringComparison.OrdinalIgnoreCase))
            {
                return $"{name} in {gameDir} is not the official Vintage Story {version} build this Optimum release " +
                       $"patches (SHA-256 {actual.ToLowerInvariant()}, expected {sha256}). Optimum cannot patch a modified " +
                       "game, a different game version, or files it has patched already. Reinstall Optimum with its " +
                       "installer, which downloads and verifies the official client. Developers working in a tree where make " +
                       $"deploy replaced the game files can set {SkipVariable}=1 to skip this check.";
            }
        }

        return null;
    }

    /// <summary>The version in the game's own <c>assets/version-x.y.z.txt</c> marker, or null when there is none.</summary>
    private static string? GameVersion(string gameDir)
    {
        string assets = Path.Combine(gameDir, "assets");
        if (!Directory.Exists(assets))
            return null;

        foreach (string marker in Directory.EnumerateFiles(assets, "version-*.txt"))
        {
            string name = Path.GetFileNameWithoutExtension(marker);
            return name["version-".Length..];
        }

        return null;
    }
}
