using System;
using System.Net.Http;
using System.Threading;

namespace Optimum;

/// <summary>
/// Checks the GitHub releases API for a newer Optimum version.
/// Runs once on client startup, non-blocking. The result is read
/// by GuiCompositeMainMenuLeft to show an update notice.
/// </summary>
public static class OptimumUpdateChecker
{
    private static readonly string ReleasesUrl =
        "https://api.github.com/repos/Zaldaryon/Optimum/releases/latest";

    /// <summary>Null until the check completes. Empty string means up-to-date.</summary>
    public static string LatestVersion { get; private set; }

    /// <summary>True when a newer release exists on GitHub.</summary>
    public static bool UpdateAvailable { get; private set; }

    /// <summary>Direct URL to the release page (for the link).</summary>
    public static string ReleaseUrl { get; private set; } = OptimumInfo.Url + "/releases/latest";

    private static int _started;

    /// <summary>
    /// Fire-and-forget. Safe to call multiple times; only the first call runs.
    /// Swallows all exceptions (network, parse, timeout).
    /// </summary>
    public static void CheckAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        Thread thread = new Thread(new ThreadStart(CheckForUpdates));
        thread.IsBackground = true;
        thread.Name = "Optimum update checker";
        thread.Start();
    }

    private static void CheckForUpdates()
    {
        try
        {
            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Add("User-Agent", "Optimum/" + OptimumInfo.Version);
            var json = client.GetStringAsync(ReleasesUrl).GetAwaiter().GetResult();

            var tagName = ExtractTagName(json);
            if (tagName == null) return;

            LatestVersion = tagName.TrimStart('v', 'V');
            ReleaseUrl = OptimumInfo.Url + "/releases/tag/" + tagName;

            if (CompareVersions(LatestVersion, OptimumInfo.Version) > 0)
            {
                UpdateAvailable = true;
            }
        }
        catch
        {
            // Network failure, timeout, parse error: silent. No update badge.
        }
    }

    /// <summary>
    /// Extract "tag_name" value from the JSON response without pulling in a JSON library.
    /// The GitHub API response has "tag_name": "v0.2.5" near the top.
    /// </summary>
    private static string ExtractTagName(string json)
    {
        const string key = "\"tag_name\"";
        int idx = json.IndexOf(key, StringComparison.Ordinal);
        if (idx < 0) return null;
        int colon = json.IndexOf(':', idx + key.Length);
        if (colon < 0) return null;
        int quote1 = json.IndexOf('"', colon + 1);
        if (quote1 < 0) return null;
        int quote2 = json.IndexOf('"', quote1 + 1);
        if (quote2 < 0) return null;
        return json.Substring(quote1 + 1, quote2 - quote1 - 1);
    }

    /// <summary>
    /// Compare two semver strings (major.minor.patch). Returns positive if a > b.
    /// </summary>
    private static int CompareVersions(string a, string b)
    {
        var pa = ParseVersion(a);
        var pb = ParseVersion(b);
        int cmp = pa.major.CompareTo(pb.major);
        if (cmp != 0) return cmp;
        cmp = pa.minor.CompareTo(pb.minor);
        if (cmp != 0) return cmp;
        return pa.patch.CompareTo(pb.patch);
    }

    private static (int major, int minor, int patch) ParseVersion(string v)
    {
        var parts = v.Split('.');
        int major = parts.Length > 0 && int.TryParse(parts[0], out var m) ? m : 0;
        int minor = parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0;
        int patch = parts.Length > 2 && int.TryParse(parts[2], out var p) ? p : 0;
        return (major, minor, patch);
    }
}
