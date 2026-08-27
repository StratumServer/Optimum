namespace Optimum.Bootstrap.Core.Platform;

/// <summary>
/// The C# equivalent of <c>command -v</c>: look for an executable on the probe's
/// PATH. On Windows it also tries the usual executable extensions.
/// </summary>
public static class CommandSearch
{
    public static string? Which(ISystemProbe probe, string command)
    {
        string[] names = probe.Os == OsKind.Windows
            ? [command, command + ".exe", command + ".cmd", command + ".bat"]
            : [command];

        foreach (string dir in probe.PathDirectories)
        {
            foreach (string name in names)
            {
                string candidate = Path.Combine(dir, name);
                if (probe.FileExists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    public static bool Exists(ISystemProbe probe, string command) => Which(probe, command) is not null;
}
