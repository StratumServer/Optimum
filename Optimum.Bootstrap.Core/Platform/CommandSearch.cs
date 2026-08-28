namespace Optimum.Bootstrap.Core.Platform;

/// <summary>
/// The C# equivalent of <c>command -v</c>: the first <em>executable</em> match on
/// the probe's PATH. A non-executable file of the right name is skipped and the
/// search continues, which is what the shell does and what a broken wrapper on an
/// early PATH entry would otherwise hide.
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
                if (probe.IsExecutable(candidate))
                    return candidate;
            }
        }

        return null;
    }

    public static bool Exists(ISystemProbe probe, string command) => Which(probe, command) is not null;
}
