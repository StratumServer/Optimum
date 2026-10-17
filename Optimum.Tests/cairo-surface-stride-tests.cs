using Cairo;
using Xunit;

public class CairoSurfaceStrideTests
{
	[Theory]
	[InlineData(0, 0, 0)]
	[InlineData(0, 1, 4)]
	[InlineData(0, 3, 12)]
	[InlineData(0, 9, 36)]
	[InlineData(1, 1, 4)]
	[InlineData(1, 9, 36)]
	[InlineData(2, 1, 4)]
	[InlineData(2, 5, 8)]
	[InlineData(2, 9, 12)]
	[InlineData(3, 1, 4)]
	[InlineData(3, 31, 4)]
	[InlineData(3, 32, 4)]
	[InlineData(3, 33, 8)]
	[InlineData(4, 1, 4)]
	[InlineData(4, 3, 8)]
	[InlineData(4, 9, 20)]
	[InlineData(5, 3, 12)]
	[InlineData(6, 3, 36)]
	[InlineData(7, 3, 48)]
	public void StrideMatchesCairoFormatAlignment(int format, int width, int expected)
	{
		Assert.Equal(expected, Surface.StrideForWidth((Format)format, width));
	}

	[Theory]
	[InlineData(-1, 0)]
	[InlineData(8, 1)]
	[InlineData(0, -1)]
	[InlineData(2, int.MaxValue)]
	public void InvalidFormatsAndWidthsReturnMinusOne(int format, int width)
	{
		Assert.Equal(-1, Surface.StrideForWidth((Format)format, width));
	}

	[Theory]
	[InlineData(0, 32)]
	[InlineData(2, 8)]
	[InlineData(3, 1)]
	[InlineData(4, 16)]
	[InlineData(6, 96)]
	[InlineData(7, 128)]
	public void WidthOverflowBoundaryMatchesCairo(int format, int bitsPerPixel)
	{
		int firstInvalidWidth = (int.MaxValue - 7) / bitsPerPixel;
		Assert.Equal(-1, Surface.StrideForWidth((Format)format, firstInvalidWidth));
		Assert.True(Surface.StrideForWidth((Format)format, firstInvalidWidth - 1) >= 0);
	}
}
