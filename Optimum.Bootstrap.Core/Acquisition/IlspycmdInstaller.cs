using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;

namespace Optimum.Bootstrap.Core.Acquisition;

/// <summary>
/// Installs or realigns the pinned ilspycmd through
/// <c>dotnet tool update -g ilspycmd --version &lt;pin&gt; --allow-downgrade</c>,
/// matching <c>scripts/bootstrap.sh:146</c>. The pin comes from
/// <c>.config/dotnet-tools.json</c>.
/// </summary>
public interface IIlspycmdAcquisition
{
    Task<ToolAcquisitionResult> InstallAsync(
        string repoRoot, IBuildObserver observer, CancellationToken cancellationToken);
}

public sealed class IlspycmdInstaller(ISystemProbe probe) : IIlspycmdAcquisition
{
    public async Task<ToolAcquisitionResult> InstallAsync(
        string repoRoot, IBuildObserver observer, CancellationToken cancellationToken)
    {
        string? dotnet = DotnetSdkProbe.Find(probe);
        if (dotnet is null)
        {
            return ToolAcquisitionResult.Failure(FailureReason.BadInput,
                "install the .NET SDK first; ilspycmd is a global dotnet tool");
        }

        IlspycmdCompatibility compat = ConfigFiles.ReadIlspycmdCompatibility(probe, repoRoot);
        observer.Phase(ProgressPhase.Decompile, 1, $"installing ilspycmd {compat.Pin}");

        // `dotnet tool update -g` writes into DOTNET_CLI_HOME/.dotnet/tools; keep
        // it under the user profile and off any machine-wide location.
        var environment = new Dictionary<string, string?>
        {
            ["DOTNET_ROOT"] = Path.GetDirectoryName(dotnet),
        };

        AcquisitionProcess.Outcome outcome = await AcquisitionProcess.RunAsync(
            dotnet, IlspycmdAcquisition.ToolArguments(compat.Pin), repoRoot,
            environment, observer, cancellationToken);
        if (!outcome.Ok)
        {
            return ToolAcquisitionResult.Failure(FailureReason.SourceUnavailable,
                outcome.Message ?? "the ilspycmd install failed");
        }

        string? installed = CommandSearch.Which(probe, "ilspycmd") ?? ToolPath();
        if (installed is null || !probe.FileExists(installed))
        {
            return ToolAcquisitionResult.Failure(FailureReason.VerificationFailed,
                "the ilspycmd install reported success but the tool was not found");
        }

        observer.Log(LogLevel.Info, $"Installed ilspycmd at {installed}");
        return ToolAcquisitionResult.Success(installed);
    }

    private string ToolPath()
    {
        string name = probe.Os == OsKind.Windows ? "ilspycmd.exe" : "ilspycmd";
        return Path.Combine(probe.HomeDirectory, ".dotnet", "tools", name);
    }
}
