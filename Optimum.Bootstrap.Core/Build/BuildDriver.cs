using Optimum.Bootstrap.Core.Platform;

namespace Optimum.Bootstrap.Core.Build;

public sealed record BuildRequest(
    string RepoRoot,
    string OutputDirectory,
    string? ClientArchive = null,
    string? Version = null);

public sealed record BuildResult(bool Ok, FailureReason? Reason, string? Message, string? RuntimePath)
{
    public static BuildResult Success(string runtimePath) => new(true, null, null, runtimePath);

    public static BuildResult Failure(FailureReason reason, string message) => new(false, reason, message, null);
}

/// <summary>
/// The engine's build pipeline. The GUI drives one in-process; the CLI wraps one
/// per verb. Progress and log go to the observer so the front end owns the
/// presentation. Cancellation is two-tier: <paramref name="graceful"/> asks the
/// running subprocess to stop (SIGINT), <paramref name="forceful"/> kills it.
/// Passing only <paramref name="forceful"/> is a straight kill.
/// </summary>
public interface IBuildDriver
{
    Task<BuildResult> RunAsync(
        BuildRequest request,
        IBuildObserver observer,
        CancellationToken forceful,
        CancellationToken graceful = default);
}

/// <summary>Receives everything a running build has to say.</summary>
public interface IBuildObserver
{
    void Phase(ProgressPhase phase, int percent, string detail);

    void Log(LogLevel level, string message);

    /// <summary>A verbatim line from a subprocess. Not part of any contract.</summary>
    void RawOutput(bool isError, string line);
}

/// <summary>Discards everything. Useful in tests that only care about the result.</summary>
public sealed class NullBuildObserver : IBuildObserver
{
    public static readonly NullBuildObserver Instance = new();

    public void Phase(ProgressPhase phase, int percent, string detail) { }

    public void Log(LogLevel level, string message) { }

    public void RawOutput(bool isError, string line) { }
}
