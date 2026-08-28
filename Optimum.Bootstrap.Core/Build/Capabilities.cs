using System.Text.Json;
using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Build;

public sealed record EngineCapabilities(
    string PinnedVersion,
    IReadOnlyList<string> SupportedVersions,
    IReadOnlyList<string> PatchSets);

/// <summary>
/// What <c>optimum capabilities</c> reports so a caller can gate the UI before a
/// 570 MB download: the pinned Vintage Story version from <c>forks.json</c>, the
/// alternate versions that have a <c>patches-&lt;version&gt;-bridge/</c> set, and the
/// top-level patch set ids under <c>patches/</c>.
/// </summary>
public static class Capabilities
{
    public static EngineCapabilities Read(ISystemProbe probe, string repoRoot)
    {
        string pinned = ReadPinnedVersion(probe, repoRoot);

        var supported = new List<string> { pinned };
        foreach (string dir in probe.EnumerateDirectories(repoRoot, "patches-*-bridge"))
        {
            string name = Path.GetFileName(dir);
            string version = name["patches-".Length..^"-bridge".Length];
            if (version.Length > 0 && !supported.Contains(version))
                supported.Add(version);
        }

        var patchSets = probe.EnumerateDirectories(Path.Combine(repoRoot, "patches"), "*")
            .Select(Path.GetFileName)
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        return new EngineCapabilities(pinned, supported, patchSets);
    }

    private static string ReadPinnedVersion(ISystemProbe probe, string repoRoot)
    {
        const string fallback = "1.22.7";
        string? json = probe.ReadText(Path.Combine(repoRoot, "forks.json"));
        if (json is null)
            return fallback;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("vintageStoryVersion", out var v)
                && v.GetString() is { Length: > 0 } version)
                return version;
        }
        catch (JsonException) { /* fall through */ }

        return fallback;
    }
}
