using System.Linq;
using Vintagestory.API.Config;
using Xunit;

namespace Optimum.Tests;

public class OptimumStatusTests
{
    [Fact]
    public void DescribeTogglesCoversEveryPersistedField()
    {
        var names = OptimumConfig.DescribeToggles().Select(t => t.Name).ToHashSet();
        foreach (var prop in typeof(OptimumConfigData).GetProperties())
        {
            Assert.Contains(prop.Name, names);
        }
    }

    [Fact]
    public void DescribeTogglesReportsCurrentValues()
    {
        bool original = OptimumConfig.EntityShadowCull;
        try
        {
            OptimumConfig.EntityShadowCull = false;
            var entry = OptimumConfig.DescribeToggles().Single(t => t.Name == nameof(OptimumConfig.EntityShadowCull));
            Assert.Equal("False", entry.Value);
        }
        finally
        {
            OptimumConfig.EntityShadowCull = original;
        }
    }
}
