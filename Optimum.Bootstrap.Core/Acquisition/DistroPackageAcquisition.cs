using Optimum.Bootstrap.Core.Build;
using Optimum.Bootstrap.Core.Platform;
using Optimum.Bootstrap.Core.Prerequisites;

namespace Optimum.Bootstrap.Core.Acquisition;

public interface IDistroPackageAcquisition
{
    DistroPackagePlan? PlanFor(string command);
    Task<ToolAcquisitionResult> InstallAsync(
        DistroPackagePlan plan, IBuildObserver observer, CancellationToken cancellationToken);
}

/// <summary>Runs a confirmed Linux package install using pkexec, without a shell.</summary>
public sealed class DistroPackageAcquisition(ISystemProbe probe) : IDistroPackageAcquisition
{
    public DistroPackagePlan? PlanFor(string command) => DistroPackageHints.Plan(probe, command);

    public async Task<ToolAcquisitionResult> InstallAsync(
        DistroPackagePlan plan, IBuildObserver observer, CancellationToken cancellationToken)
    {
        try
        {
            observer.Log(LogLevel.Info, $"Installing package {plan.Package} with {plan.Manager}...");
            AcquisitionProcess.Outcome outcome = await AcquisitionProcess.RunAsync(
                plan.PkexecPath, plan.Arguments, "/", null, observer, cancellationToken);
            if (outcome.Ok)
                return ToolAcquisitionResult.Success(plan.Package);

            string details = outcome.Message ?? "the package manager returned a failure";
            observer.Log(LogLevel.Warn, $"Package installation failed: {details}");
            return ToolAcquisitionResult.Failure(FailureReason.SourceUnavailable,
                $"Could not install {plan.Package}: {details}. You can retry or use the command shown below.");
        }
        catch (OperationCanceledException)
        {
            return ToolAcquisitionResult.Failure(FailureReason.Cancelled,
                "Installation was cancelled. You can retry or use the command shown below.");
        }
    }
}
