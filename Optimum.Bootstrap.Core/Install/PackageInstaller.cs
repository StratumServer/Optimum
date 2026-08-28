using Optimum.Bootstrap.Core.Build;

namespace Optimum.Bootstrap.Core.Install;

/// <summary>The deploy step, behind an interface so the GUI can fake it in a headless test.</summary>
public interface IPackageInstaller
{
    DeployResult Deploy(DeployRequest request, IBuildObserver? observer = null);
}
