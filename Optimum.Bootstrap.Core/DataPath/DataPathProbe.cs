using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.DataPath;

public sealed record DataPathDetection(string? Path, bool HasActiveSession);

/// <summary>
/// Session-aware detection of an existing Vintage Story data folder. Ports
/// <c>prompt_data_path</c> from <c>scripts/install-linux.sh</c> (which Windows and
/// macOS never had) and widens the candidate list per platform: a folder whose
/// <c>clientsettings.json</c> carries a <c>playeruid</c> wins over one that merely
/// exists.
/// </summary>
public static class DataPathProbe
{
    public static DataPathDetection Detect(ISystemProbe probe)
    {
        string[] candidates = Candidates(probe);

        foreach (string dir in candidates)
        {
            string settings = System.IO.Path.Combine(dir, "clientsettings.json");
            string? content = probe.ReadText(settings);
            if (content is not null && content.Contains("\"playeruid\"", StringComparison.Ordinal))
                return new DataPathDetection(dir, HasActiveSession: true);
        }

        foreach (string dir in candidates)
        {
            if (probe.DirectoryExists(dir))
                return new DataPathDetection(dir, HasActiveSession: false);
        }

        return new DataPathDetection(null, HasActiveSession: false);
    }

    private static string[] Candidates(ISystemProbe probe)
    {
        string home = probe.HomeDirectory;
        return probe.Os switch
        {
            OsKind.Windows =>
            [
                Combine(probe.GetEnvironmentVariable("APPDATA"), "VintagestoryData"),
                Combine(probe.GetEnvironmentVariable("APPDATA"), "OptimumData"),
            ],
            OsKind.MacOs =>
            [
                System.IO.Path.Combine(home, "Library", "Application Support", "VintagestoryData"),
                System.IO.Path.Combine(home, "Library", "Application Support", "OptimumVintagestoryData"),
                System.IO.Path.Combine(home, ".config", "VintagestoryData"),
            ],
            _ =>
            [
                System.IO.Path.Combine(home, ".config", "VintagestoryData"),
                System.IO.Path.Combine(home, ".config", "OptimumVintagestoryData"),
                System.IO.Path.Combine(home, "ApplicationData", "vintagestorydata"),
            ],
        };

        static string Combine(string? root, string child) =>
            root is { Length: > 0 } ? System.IO.Path.Combine(root, child) : child;
    }
}
