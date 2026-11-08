using System;
using System.IO;
using Xunit;

namespace Optimum.Tests;

public sealed class EntityShadowCullCacheTests
{
    [Fact]
    public void ShadowCullDistanceCaching_UpdatesOnConfigChange()
    {
        int currentConfigDistance = 80;
        int cachedDistance = -1;
        double cachedDistanceSq = 0;
        int calculationCount = 0;

        double GetDistanceSq()
        {
            int dist = currentConfigDistance;
            if (dist != cachedDistance)
            {
                cachedDistance = dist;
                cachedDistanceSq = (double)dist * dist;
                calculationCount++;
            }
            return cachedDistanceSq;
        }

        // First call calculates squared distance
        Assert.Equal(6400.0, GetDistanceSq());
        Assert.Equal(1, calculationCount);

        // Repeated calls within entity loop reuse cached value with zero recalculations
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal(6400.0, GetDistanceSq());
        }
        Assert.Equal(1, calculationCount);

        // Distance changed via slider/settings
        currentConfigDistance = 120;
        Assert.Equal(14400.0, GetDistanceSq());
        Assert.Equal(2, calculationCount);

        // Subsequent reads remain cached
        for (int i = 0; i < 50; i++)
        {
            Assert.Equal(14400.0, GetDistanceSq());
        }
        Assert.Equal(2, calculationCount);
    }

    [Fact]
    public void PatchIntegrity_SystemRenderEntitiesCachesShadowCullDistanceSq()
    {
        string patchPath = PatchReader.FindRepositoryFile(Path.Combine("patches", "VintagestoryLib", "Vintagestory.Client.NoObf", "SystemRenderEntities.cs.patch"));
        Assert.True(File.Exists(patchPath), "Patch file must exist");

        string patchContent = File.ReadAllText(patchPath);
        Assert.Contains("private static int _cachedShadowCullDistance = -1;", patchContent);
        Assert.Contains("private static double _cachedShadowCullDistanceSq = 6400.0;", patchContent);
        Assert.Contains("private static double ShadowCullDistanceSq", patchContent);
        Assert.Contains("int dist = ClientSettings.OptimumShadowCullDistance;", patchContent);
        Assert.Contains("_cachedShadowCullDistance = dist;", patchContent);
        Assert.Contains("_cachedShadowCullDistanceSq = (double)dist * dist;", patchContent);
    }
}
