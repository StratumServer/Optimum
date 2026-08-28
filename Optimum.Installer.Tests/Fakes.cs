using Optimum.Bootstrap.Core;
using Optimum.Bootstrap.Core.Acquisition;
using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Install;
using Optimum.Bootstrap.Core.Tests;
using Optimum.Installer.Services;

namespace Optimum.Installer.Tests;

public sealed class FakeBuildDriver : IBuildDriver
{
    public Func<IBuildObserver, CancellationToken, BuildResult> Behaviour { get; set; } =
        static (observer, _) =>
        {
            observer.Phase(ProgressPhase.Decompile, 10, "decompiling");
            observer.Phase(ProgressPhase.Assemble, 80, "compiling");
            return BuildResult.Success("/tmp/pkg/Optimum-v0.3.14-linux-x64");
        };

    public int RunCount { get; private set; }

    public Task<BuildResult> RunAsync(BuildRequest request, IBuildObserver observer, CancellationToken forceful, CancellationToken graceful = default)
    {
        RunCount++;
        LastRepoRoot = request.RepoRoot;
        LastOutputDirectory = request.OutputDirectory;
        forceful.ThrowIfCancellationRequested();
        return Task.FromResult(Behaviour(observer, forceful));
    }

    public string? LastRepoRoot { get; private set; }
    public string? LastOutputDirectory { get; private set; }
}

/// <summary>
/// A driver that reports some progress then blocks until <see cref="Release"/> is
/// called, so a test can inspect the Progress screen mid-run and exercise cancel.
/// </summary>
public sealed class GatedBuildDriver : IBuildDriver
{
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool ObservedForcefulCancellation { get; private set; }

    public void Release() => _gate.TrySetResult();

    public async Task<BuildResult> RunAsync(
        BuildRequest request, IBuildObserver observer, CancellationToken forceful, CancellationToken graceful = default)
    {
        observer.Phase(ProgressPhase.Decompile, 30, "decompiling");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(forceful, graceful);
        try
        {
            await _gate.Task.WaitAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
            ObservedForcefulCancellation = forceful.IsCancellationRequested;
            return BuildResult.Failure(FailureReason.Cancelled, "the build was cancelled");
        }

        return BuildResult.Success("/tmp/pkg/Optimum-linux-x64");
    }
}

public sealed class FakePackageInstaller : IPackageInstaller
{
    public Func<DeployRequest, DeployResult> Behaviour { get; set; } =
        static request => DeployResult.Success(request.InstallDirectory, request.InstallDirectory + "/optimum-launch.sh");

    public DeployResult Deploy(DeployRequest request, IBuildObserver? observer = null) => Behaviour(request);
}

public sealed class FakeSourceProvider : ISourceProvider
{
    public Func<SourceRequest, SourceAcquisitionResult> Behaviour { get; set; } =
        static _ => SourceAcquisitionResult.Success("/downloaded-repo");

    public int Calls { get; private set; }

    public Task<SourceAcquisitionResult> EnsureAsync(
        SourceRequest request, IBuildObserver observer, CancellationToken cancellationToken)
    {
        Calls++;
        observer.Phase(ProgressPhase.Decompile, 1, "cloning");
        return Task.FromResult(Behaviour(request));
    }
}

public sealed class FakeAppimagetoolAcquisition : IAppimagetoolAcquisition
{
    public Func<string, ToolAcquisitionResult> Behaviour { get; set; } =
        static repoRoot => ToolAcquisitionResult.Success(AppimagetoolAcquisition.TargetPath(repoRoot));

    public int Calls { get; private set; }

    public Task<ToolAcquisitionResult> InstallAsync(
        string repoRoot, IBuildObserver observer, CancellationToken cancellationToken)
    {
        Calls++;
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Behaviour(repoRoot));
    }
}

public sealed class FakeUpdateService : IUpdateService
{
    public string? AvailableVersion { get; set; }
    public bool Applied { get; private set; }

    public Task<string?> CheckAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(AvailableVersion);

    public Task ApplyAsync(Action<int>? progress = null)
    {
        Applied = true;
        progress?.Invoke(100);
        return Task.CompletedTask;
    }
}

public static class TestServices
{
    public static InstallerServices Build(
        string? repoRoot = "/repo",
        FakeSystemProbe? probe = null,
        IBuildDriver? driver = null,
        IPackageInstaller? installer = null,
        IUpdateService? updates = null,
        ISourceProvider? sourceProvider = null,
        IAppimagetoolAcquisition? appimagetool = null,
        bool dotnetPresent = true)
    {
        probe ??= new FakeSystemProbe();
        probe.Path.Add("/usr/bin");
        foreach (string tool in new[] { "git", "perl", "python3", "curl", "tar", "chmod", "pwsh", "bash" })
            if (!probe.Files.Contains($"/usr/bin/{tool}"))
                probe.AddFile($"/usr/bin/{tool}");
        probe.Environment["OPTIMUM_DOTNET_CANDIDATES"] = dotnetPresent ? "/opt/dotnet/dotnet" : "/absent/dotnet";
        if (dotnetPresent)
        {
            probe.AddFile("/opt/dotnet/dotnet");
            probe.OnCommand("/opt/dotnet/dotnet", "--list-sdks", "10.0.100 [/x]\n");
            probe.OnCommand("/opt/dotnet/dotnet", "--version", "10.0.100\n");
        }
        probe.AddFile("/lib64/ld-linux-x86-64.so.2");
        if (repoRoot is not null)
        {
            probe.AddFile($"{repoRoot}/forks.json", """{ "vintageStoryVersion": "1.22.7" }""");
            probe.AddFile($"{repoRoot}/scripts/bootstrap.sh");
        }

        return new InstallerServices(
            probe,
            repoRoot,
            driver ?? new FakeBuildDriver(),
            installer ?? new FakePackageInstaller())
        {
            UiPost = action => action(),
            Updates = updates,
            SourceProvider = sourceProvider,
            Appimagetool = appimagetool,
        };
    }
}
