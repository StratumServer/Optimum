using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

// Not parallelized against other test classes touching OptimumConfig.EntityTesselationFrameBudget
// or the budget-remaining state (there is only one; xUnit runs test classes in this project
// sequentially by default here, matching every other OptimumDiagnostics test's convention of
// mutating shared static state directly).
public class EntityTesselationBudgetTests
{
    public EntityTesselationBudgetTests()
    {
        OptimumDiagnostics.EntityTesselationBudget.Reset();
    }

    [Fact]
    public void ResetSetsRemainingToConfiguredBudget()
    {
        int original = OptimumConfig.EntityTesselationFrameBudget;
        try
        {
            OptimumConfig.EntityTesselationFrameBudget = 2;
            OptimumDiagnostics.ResetEntityTesselationBudget();

            Assert.True(OptimumDiagnostics.TryConsumeEntityTesselationBudget());
            Assert.True(OptimumDiagnostics.TryConsumeEntityTesselationBudget());
            Assert.False(OptimumDiagnostics.TryConsumeEntityTesselationBudget());
        }
        finally
        {
            OptimumConfig.EntityTesselationFrameBudget = original;
        }
    }

    [Fact]
    public void ResetEachFrameRefillsTheBudget()
    {
        int original = OptimumConfig.EntityTesselationFrameBudget;
        try
        {
            OptimumConfig.EntityTesselationFrameBudget = 1;
            OptimumDiagnostics.ResetEntityTesselationBudget();
            Assert.True(OptimumDiagnostics.TryConsumeEntityTesselationBudget());
            Assert.False(OptimumDiagnostics.TryConsumeEntityTesselationBudget());

            // Next frame's reset must refill it, not leave last frame's exhaustion stuck.
            OptimumDiagnostics.ResetEntityTesselationBudget();
            Assert.True(OptimumDiagnostics.TryConsumeEntityTesselationBudget());
        }
        finally
        {
            OptimumConfig.EntityTesselationFrameBudget = original;
        }
    }

    [Fact]
    public void ZeroBudgetDisablesTheCapEntirely()
    {
        int original = OptimumConfig.EntityTesselationFrameBudget;
        try
        {
            OptimumConfig.EntityTesselationFrameBudget = 0;
            OptimumDiagnostics.ResetEntityTesselationBudget();

            for (int i = 0; i < 50; i++)
            {
                Assert.True(OptimumDiagnostics.TryConsumeEntityTesselationBudget());
            }
        }
        finally
        {
            OptimumConfig.EntityTesselationFrameBudget = original;
        }
    }
}
