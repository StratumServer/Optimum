using System;
using System.Collections.Generic;

namespace Cairo
{
	/// <summary>Immutable-on-capture description for a gradient that has not needed a Cairo handle.</summary>
	internal sealed class ManagedGradientSource
	{
		internal readonly struct Stop
		{
			internal readonly double Offset;
			internal readonly double Red, Green, Blue, Alpha;
			internal Stop(double offset, Color color)
			{ Offset = offset; Red = color.R; Green = color.G; Blue = color.B; Alpha = color.A; }
		}

		readonly List<Stop> stops = new List<Stop>();
		internal readonly PatternType Type;
		internal readonly double X0, Y0, Radius0, X1, Y1, Radius1;
		internal Extend Extend { get; set; } = Extend.Pad;
		internal Matrix Matrix { get; set; } = new Matrix();
		internal int StopCount => stops.Count;
		internal IReadOnlyList<Stop> Stops => stops;

		internal ManagedGradientSource(PatternType type, double x0, double y0, double radius0, double x1, double y1, double radius1)
		{ Type = type; X0 = x0; Y0 = y0; Radius0 = radius0; X1 = x1; Y1 = y1; Radius1 = radius1; }

		internal void AddColorStop(double offset, Color color)
		{
			var stop = new Stop(offset, color);
			int index = stops.FindIndex(value => value.Offset > offset);
			if (index < 0) stops.Add(stop); else stops.Insert(index, stop);
		}

		internal ManagedGradientSource Clone()
		{
			var copy = new ManagedGradientSource(Type, X0, Y0, Radius0, X1, Y1, Radius1) {
				Extend = Extend,
				Matrix = (Matrix)Matrix.Clone()
			};
			copy.stops.AddRange(stops);
			return copy;
		}

		internal IntPtr CreateNativeHandle()
		{
			IntPtr handle = Type == PatternType.Linear
				? NativeMethods.cairo_pattern_create_linear(X0, Y0, X1, Y1)
				: NativeMethods.cairo_pattern_create_radial(X0, Y0, Radius0, X1, Y1, Radius1);
			if (handle == IntPtr.Zero) throw new InvalidOperationException("Cairo could not create the gradient pattern.");
			try {
				foreach (Stop stop in stops)
					NativeMethods.cairo_pattern_add_color_stop_rgba(handle, stop.Offset, stop.Red, stop.Green, stop.Blue, stop.Alpha);
				NativeMethods.cairo_pattern_set_extend(handle, Extend);
				NativeMethods.cairo_pattern_set_matrix(handle, Matrix);
				return handle;
			} catch {
				NativeMethods.cairo_pattern_destroy(handle);
				throw;
			}
		}
	}
}
