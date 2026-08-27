using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Build;

namespace Optimum.Cli.Tests;

/// <summary>A scripted <see cref="IBuildDriver"/> so CLI tests never run a real build.</summary>
public sealed class FakeBuildDriver : IBuildDriver
{
    public bool WasRun { get; private set; }

    public Func<IBuildObserver, CancellationToken, BuildResult> Behaviour { get; set; } =
        static (observer, _) =>
        {
            observer.Phase(ProgressPhase.Decompile, 5, "extracting");
            observer.Phase(ProgressPhase.Decompile, 30, "ilspycmd");
            observer.Phase(ProgressPhase.Patch, 50, "applying patches");
            observer.Log(LogLevel.Warn, "innoextract not present; Windows package skipped");
            observer.Phase(ProgressPhase.Assemble, 80, "dotnet build");
            observer.Phase(ProgressPhase.Verify, 98, "package produced");
            return BuildResult.Success("/out/Optimum-v0.3.14-linux-x64");
        };

    public Task<BuildResult> RunAsync(BuildRequest request, IBuildObserver observer, CancellationToken forceful, CancellationToken graceful = default)
    {
        WasRun = true;
        forceful.ThrowIfCancellationRequested();
        return Task.FromResult(Behaviour(observer, forceful));
    }
}
