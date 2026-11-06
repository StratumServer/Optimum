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

    /// <summary>
    /// Resolves the true .NET installation directory (DOTNET_ROOT) for the found SDK.
    /// If the executable is a symlink or installed in a bin directory (such as /usr/bin/dotnet),
    /// resolves to the actual SDK root containing host/ and sdk/ (e.g. /usr/lib/dotnet),
    /// or parses the directory from 'dotnet --list-sdks'.
    /// </summary>
    public static string? FindDotnetRoot(ISystemProbe probe)
    {
        foreach (string candidate in Candidates(probe))
        {
            if (!probe.IsExecutable(candidate))
                continue;
            ProcessOutcome outcome = probe.Run(candidate, ["--list-sdks"], ProbeTimeout);
            if (outcome.Started && HasNet10Line(outcome.StandardOutput))
                return ResolveDotnetRoot(probe, candidate, outcome.StandardOutput);
        }

        return null;
    }

    internal static string? ResolveDotnetRoot(ISystemProbe probe, string executablePath, string? listSdksOutput = null)
    {
        bool isUnixPath = executablePath.Contains('/') && !executablePath.Contains('\\');
        string? candidateDir = Path.GetDirectoryName(executablePath);
        if (isUnixPath && candidateDir is not null)
            candidateDir = candidateDir.Replace('\\', '/');

        if (candidateDir is not null
            && (probe.DirectoryExists(Path.Combine(candidateDir, "sdk"))
                || probe.DirectoryExists(Path.Combine(candidateDir, "shared"))
                || probe.DirectoryExists(Path.Combine(candidateDir, "host"))))
        {
            return candidateDir;
        }

        if (!string.IsNullOrWhiteSpace(listSdksOutput))
        {
            foreach (string rawLine in listSdksOutput.Split('\n'))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("10.", StringComparison.Ordinal))
                    continue;

                int openBracket = line.IndexOf('[');
                int closeBracket = line.IndexOf(']', openBracket + 1);
                if (openBracket >= 0 && closeBracket > openBracket)
                {
                    string sdkPath = line.Substring(openBracket + 1, closeBracket - openBracket - 1).Trim();
                    string? parentDir = Path.GetDirectoryName(sdkPath);
                    if (isUnixPath && parentDir is not null)
                        parentDir = parentDir.Replace('\\', '/');

                    if (!string.IsNullOrEmpty(parentDir)
                        && (probe.DirectoryExists(parentDir)
                            || probe.DirectoryExists(Path.Combine(parentDir, "sdk"))
                            || probe.DirectoryExists(Path.Combine(parentDir, "shared"))))
                    {
                        return parentDir;
                    }
                }
            }
        }

        if (candidateDir is not null && (candidateDir.EndsWith("/bin", StringComparison.Ordinal)
            || candidateDir.EndsWith("\\bin", StringComparison.Ordinal)))
        {
            return null;
        }

        return candidateDir;
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
