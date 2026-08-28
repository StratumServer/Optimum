using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Prerequisites;

/// <summary>
/// Finds a .NET 10 SDK. Ports <c>check_dotnet10</c> from
/// <c>scripts/install-linux.sh</c> and folds in the extra probe locations from
/// <c>Resolve-DotNetPath</c> in <c>scripts/install-windows.ps1</c>: PATH first,
/// then a per-platform candidate list, then run <c>--list-sdks</c> on each and
/// accept the one that reports a <c>10.</c> line. <c>OPTIMUM_DOTNET_CANDIDATES</c>
/// (separated by <c>:</c> on Unix, <c>;</c> on Windows) replaces the default
/// list, which is how the shell tests point detection at a stub.
/// </summary>
public static class DotnetSdkProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    public static string? Find(ISystemProbe probe)
    {
        foreach (string candidate in Candidates(probe))
        {
            if (!probe.IsExecutable(candidate))
                continue;
            ProcessOutcome outcome = probe.Run(candidate, ["--list-sdks"], ProbeTimeout);
            if (outcome.Started && HasNet10Line(outcome.StandardOutput))
                return candidate;
        }

        return null;
    }

    private static bool HasNet10Line(string listSdksOutput)
    {
        // Matches the shell's `grep -q '^10\.'`: anchored at column 0.
        foreach (string line in listSdksOutput.Split('\n'))
        {
            if (line.StartsWith("10.", StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> Candidates(ISystemProbe probe)
    {
        string? onPath = CommandSearch.Which(probe, "dotnet");
        if (onPath is not null)
            yield return onPath;

        string? overrideList = probe.GetEnvironmentVariable("OPTIMUM_DOTNET_CANDIDATES");
        if (!string.IsNullOrEmpty(overrideList))
        {
            // Split on the *simulated* platform's separator, not the host's, so a
            // Windows-shaped probe parses `C:\a;C:\b` even when the test runs on
            // Linux. In production probe.Os always matches the host.
            char separator = probe.Os == OsKind.Windows ? ';' : ':';
            foreach (string entry in overrideList.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                yield return entry;
            yield break;
        }

        string home = probe.HomeDirectory;
        if (probe.Os == OsKind.Windows)
        {
            string? programFiles = probe.GetEnvironmentVariable("ProgramFiles");
            string? programFilesX86 = probe.GetEnvironmentVariable("ProgramFiles(x86)");
            string? localAppData = probe.GetEnvironmentVariable("LOCALAPPDATA");
            if (programFiles is not null)
                yield return Path.Combine(programFiles, "dotnet", "dotnet.exe");
            if (programFilesX86 is not null)
                yield return Path.Combine(programFilesX86, "dotnet", "dotnet.exe");
            yield return Path.Combine(home, ".dotnet", "dotnet.exe");
            if (localAppData is not null)
            {
                yield return Path.Combine(localAppData, "Microsoft", "dotnet", "dotnet.exe");
                yield return Path.Combine(localAppData, "Programs", "dotnet", "dotnet.exe");
            }
            yield break;
        }

        yield return Path.Combine(home, ".dotnet", "dotnet");
        yield return Path.Combine(home, ".nix-profile", "bin", "dotnet");
        yield return "/usr/share/dotnet/dotnet";
        yield return "/usr/lib/dotnet/dotnet";
        yield return "/snap/dotnet-sdk/current/dotnet";
        if (probe.Os == OsKind.MacOs)
            yield return "/usr/local/share/dotnet/dotnet";
    }
}
