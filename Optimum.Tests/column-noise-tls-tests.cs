using System;
using Vintagestory.API.MathTools;
using Xunit;

namespace Optimum.Tests;

public sealed class ColumnNoiseTlsTests
{
    [Fact]
    public void ColumnNoise_UsesThreadLocalStorageAndCalculatesDeterministicNoise()
    {
        var noise = NewNormalizedSimplexFractalNoise.FromDefaultOctaves(4, 0.5, 2.0, 12345);
        double[] amps = new double[] { 1.0, 0.5, 0.25, 0.125 };
        double[] th = new double[] { 0.0, 0.0, 0.0, 0.0 };

        var col1 = noise.ForColumn(1.0, amps, th, 100.5, 200.5);
        Assert.True(col1.UsedOctaves > 0);
        Assert.True(col1.BoundMax > col1.BoundMin);

        double val1 = col1.Noise(64.0);
        double sign1 = col1.NoiseSign(64.0, 0.0);

        // Second call on same thread reuses TLS arrays
        var col2 = noise.ForColumn(1.0, amps, th, 100.5, 200.5);
        Assert.Equal(col1.UsedOctaves, col2.UsedOctaves);
        Assert.Equal(col1.BoundMin, col2.BoundMin);
        Assert.Equal(col1.BoundMax, col2.BoundMax);

        double val2 = col2.Noise(64.0);
        double sign2 = col2.NoiseSign(64.0, 0.0);

        Assert.Equal(val1, val2);
        Assert.Equal(sign1, sign2);
    }
}
