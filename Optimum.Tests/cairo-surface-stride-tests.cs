using Cairo;
using Xunit;

public sealed class CairoSurfaceStrideTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0, 1, 4)]
    [InlineData(0, 9, 36)]
    [InlineData(1, 7, 28)]
    [InlineData(2, 1, 4)]
    [InlineData(2, 5, 8)]
    [InlineData(3, 32, 4)]
    [InlineData(3, 33, 8)]
    [InlineData(4, 3, 8)]
    [InlineData(4, 8, 16)]
    [InlineData(5, 9, 36)]
    [InlineData(6, 3, 36)]
    [InlineData(7, 3, 48)]
    public void ComputesAlignedStrideForEveryCairoFormat(int format, int width, int expected)
    {
        Assert.Equal(expected, Surface.StrideForWidth((Format)format, width));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(8, 4)]
    [InlineData(0, -1)]
    [InlineData(0, 67_108_863)]
    [InlineData(2, 268_435_455)]
    [InlineData(3, 2_147_483_640)]
    [InlineData(4, 134_217_727)]
    [InlineData(6, 22_369_621)]
    [InlineData(7, 16_777_215)]
    public void ReturnsCairoErrorStrideForInvalidOrOverflowingInputs(int format, int width)
    {
        Assert.Equal(-1, Surface.StrideForWidth((Format)format, width));
    }
}
