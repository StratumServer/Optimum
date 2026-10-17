using System;
using Cairo;
using Xunit;

public sealed class CairoMatrixManagedTests
{
	[Fact]
	public void ConstructorsAndInitializersDoNotNeedNativeCairo ()
	{
		var matrix = new Matrix ();
		Assert.Equal (new Matrix (1, 0, 0, 1, 0, 0), matrix);
		matrix.InitTranslate (3, -4);
		Assert.Equal (new Matrix (1, 0, 0, 1, 3, -4), matrix);
		matrix.InitScale (2, 5);
		Assert.Equal (new Matrix (2, 0, 0, 5, 0, 0), matrix);
		matrix.InitRotate (Math.PI / 2);
		Assert.Equal (-1, matrix.Xy, 12);
		Assert.Equal (1, matrix.Yx, 12);
	}

	[Fact]
	public void MultiplyUsesCairoOrderAndSupportsAliasing ()
	{
		var matrix = new Matrix (2, 3, 5, 7, 11, 13);
		var other = new Matrix (17, 19, 23, 29, 31, 37);
		Matrix expected = Matrix.Multiply (matrix, other);
		var aliasedLeft = (Matrix)matrix.Clone ();
		aliasedLeft.Multiply (other);
		Assert.Equal (expected, aliasedLeft);
		Matrix.Multiply (matrix, matrix, other);
		Assert.Equal (expected, matrix);
		Assert.Equal (new Matrix (103, 125, 246, 298, 517, 623), expected);
	}

	[Fact]
	public void TranslateScaleAndRotatePrependTheirTransforms ()
	{
		var matrix = new Matrix (2, 3, 5, 7, 11, 13);
		matrix.Translate (17, 19);
		Assert.Equal (new Matrix (2, 3, 5, 7, 140, 197), matrix);
		matrix.Scale (2, 3);
		Assert.Equal (new Matrix (4, 6, 15, 21, 140, 197), matrix);
		matrix.InitIdentity ();
		matrix.Translate (4, 6);
		matrix.Rotate (Math.PI / 2);
		Assert.Equal (4, matrix.X0, 12);
		Assert.Equal (6, matrix.Y0, 12);
	}

	[Fact]
	public void TransformPointAndDistanceUseAffineAndLinearParts ()
	{
		var matrix = new Matrix (2, 3, 5, 7, 11, 13);
		double x = 17, y = 19;
		matrix.TransformPoint (ref x, ref y);
		Assert.Equal (140, x);
		Assert.Equal (197, y);
		double dx = 17, dy = 19;
		matrix.TransformDistance (ref dx, ref dy);
		Assert.Equal (129, dx);
		Assert.Equal (184, dy);
	}

	[Fact]
	public void InvertReturnsStatusAndPreservesCairoFailureMutationBehavior ()
	{
		var matrix = new Matrix (0, 0, 0, 2, 8, 10);
		Assert.Equal (Status.InvalidMatrix, matrix.Invert ());
		Assert.Equal (new Matrix (0, 0, 0, 2, -8, -10), matrix);

		matrix = new Matrix (0, 0, 0, 2, 8, 10);
		matrix.Init (0, 0, 0, 0, 8, 10);
		Assert.Equal (Status.InvalidMatrix, matrix.Invert ());
		Assert.Equal (new Matrix (0, 0, 0, 0, -8, -10), matrix);

		matrix = new Matrix (2, 3, 5, 7, 11, 13);
		Assert.Equal (Status.Success, matrix.Invert ());
		var identity = Matrix.Multiply (matrix, new Matrix (2, 3, 5, 7, 11, 13));
		Assert.Equal (1, identity.Xx, 12);
		Assert.Equal (0, identity.Yx, 12);
		Assert.Equal (0, identity.Xy, 12);
		Assert.Equal (1, identity.Yy, 12);
		Assert.Equal (0, identity.X0, 12);
		Assert.Equal (0, identity.Y0, 12);
	}
}
