namespace Optimum.Bootstrap.Core.Platform;

/// <summary>Changes PATH for this installer process and its future child processes only.</summary>
public static class ProcessPath
{
    public static void Prepend(string directory)
    {
        string? current = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Process);
        string[] entries = (current ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Any(entry => string.Equals(entry, directory, StringComparison.OrdinalIgnoreCase)))
            return;

        string updated = string.IsNullOrEmpty(current)
            ? directory
            : directory + Path.PathSeparator + current;
        Environment.SetEnvironmentVariable("PATH", updated, EnvironmentVariableTarget.Process);
    }
}
