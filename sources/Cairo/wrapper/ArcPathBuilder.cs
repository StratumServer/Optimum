using System;

namespace Cairo
{
	/// <summary>What <see cref="ArcPathBuilder"/> needs from a path under construction.</summary>
	internal interface IArcPathTarget
	{
		Status Status { get; }
		Matrix Matrix { get; }
		double Tolerance { get; }
		void LineTo (double x, double y);
		void CurveTo (double x1, double y1, double x2, double y2, double x3, double y3);
	}

	/// <summary>Cairo's arc expansion into cubic segments (cairo-arc.c), shared by native and managed contexts.</summary>
	internal static class ArcPathBuilder
	{
		static readonly (double Angle, double Error)[] angleErrorTable = {
			(Math.PI, 0.0185185185185185036127),
			(Math.PI / 2, 0.000272567143730179811158),
			(Math.PI / 3, 2.38647043651461047433e-05),
			(Math.PI / 4, 4.2455377443222443279e-06),
			(Math.PI / 5, 1.11281001494389081528e-06),
			(Math.PI / 6, 3.72662000942734705475e-07),
			(Math.PI / 7, 1.47783685574284411325e-07),
			(Math.PI / 8, 6.63240432022601149057e-08),
			(Math.PI / 9, 3.2715520137536980553e-08),
			(Math.PI / 10, 1.73863223499021216974e-08),
			(Math.PI / 11, 9.81410988043554039085e-09)
		};

		internal static void Append<T> (T target, double xc, double yc, double radius, double angle1, double angle2, bool negative) where T : IArcPathTarget
		{
			if (target.Status != Status.Success) return;
			if (radius <= 0) {
				target.LineTo (xc, yc);
				target.LineTo (xc, yc);
				return;
			}

			const double fullTurn = Math.PI * 2;
			if (!negative && angle2 < angle1) {
				angle2 = (angle2 - angle1) % fullTurn;
				if (angle2 < 0) angle2 += fullTurn;
				angle2 += angle1;
			} else if (negative && angle2 > angle1) {
				angle2 = (angle2 - angle1) % fullTurn;
				if (angle2 > 0) angle2 -= fullTurn;
				angle2 += angle1;
			}

			target.LineTo (xc + radius * Math.Cos (angle1), yc + radius * Math.Sin (angle1));
			if (target.Status != Status.Success) return;
			Matrix matrix = target.Matrix;
			double majorAxis = TransformedCircleMajorAxis (matrix, radius);
			double maxAngle = MaxAngleForToleranceNormalized (target.Tolerance / majorAxis);
			if (negative)
				AppendRange (target, xc, yc, radius, angle2, angle1, true, maxAngle);
			else
				AppendRange (target, xc, yc, radius, angle1, angle2, false, maxAngle);
		}

		static void AppendRange<T> (T target, double xc, double yc, double radius, double minimum, double maximum, bool reverse, double maxAngle) where T : IArcPathTarget
		{
			const double fullTurn = Math.PI * 2;
			const double maximumFullCircles = 65536;
			double angle = maximum - minimum;
			if (angle > fullTurn * maximumFullCircles) {
				maximum = (maximum - minimum) % fullTurn;
				minimum %= fullTurn;
				maximum += minimum + fullTurn * maximumFullCircles;
				angle = maximum - minimum;
			}

			if (angle > Math.PI) {
				double middle = minimum + angle / 2;
				if (reverse) {
					AppendRange (target, xc, yc, radius, middle, maximum, true, maxAngle);
					AppendRange (target, xc, yc, radius, minimum, middle, true, maxAngle);
				} else {
					AppendRange (target, xc, yc, radius, minimum, middle, false, maxAngle);
					AppendRange (target, xc, yc, radius, middle, maximum, false, maxAngle);
				}
				return;
			}

			if (maximum != minimum) {
				int segments = (int)Math.Ceiling (Math.Abs (angle) / maxAngle);
				double step = angle / segments;
				segments--;
				if (reverse) {
					double swap = minimum;
					minimum = maximum;
					maximum = swap;
					step = -step;
				}

				target.LineTo (xc + radius * Math.Cos (minimum), yc + radius * Math.Sin (minimum));
				for (int i = 0; i < segments; i++, minimum += step)
					AppendSegment (target, xc, yc, radius, minimum, minimum + step);
				AppendSegment (target, xc, yc, radius, minimum, maximum);
			} else {
				target.LineTo (xc + radius * Math.Cos (minimum), yc + radius * Math.Sin (minimum));
			}
		}

		static void AppendSegment<T> (T target, double xc, double yc, double radius, double angleA, double angleB) where T : IArcPathTarget
		{
			double radiusSinA = radius * Math.Sin (angleA);
			double radiusCosA = radius * Math.Cos (angleA);
			double radiusSinB = radius * Math.Sin (angleB);
			double radiusCosB = radius * Math.Cos (angleB);
			double h = 4.0 / 3.0 * Math.Tan ((angleB - angleA) / 4.0);
			target.CurveTo (
				xc + radiusCosA - h * radiusSinA,
				yc + radiusSinA + h * radiusCosA,
				xc + radiusCosB + h * radiusSinB,
				yc + radiusSinB - h * radiusCosB,
				xc + radiusCosB,
				yc + radiusSinB);
		}

		static double TransformedCircleMajorAxis (Matrix matrix, double radius)
		{
			double xx = matrix.Xx, yx = matrix.Yx, xy = matrix.Xy, yy = matrix.Yy;
			double trace = xx * xx + yx * yx + xy * xy + yy * yy;
			double determinant = xx * yy - xy * yx;
			double discriminant = Math.Max (0, trace * trace - 4 * determinant * determinant);
			return radius * Math.Sqrt ((trace + Math.Sqrt (discriminant)) / 2);
		}

		static double MaxAngleForToleranceNormalized (double tolerance)
		{
			foreach (var entry in angleErrorTable)
				if (entry.Error < tolerance) return entry.Angle;

			int denominator = angleErrorTable.Length + 1;
			double angle, error;
			do {
				angle = Math.PI / denominator++;
				double sine = Math.Sin (angle / 4);
				double cosine = Math.Cos (angle / 4);
				error = 2.0 / 27.0 * Math.Pow (sine, 6) / Math.Pow (cosine, 2);
			} while (error > tolerance);
			return angle;
		}
	}
}
