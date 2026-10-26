using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Cairo
{
	/// <summary>
	/// Managed model of the graphics state a Cairo context keeps: the CTM, stroke and
	/// fill parameters, the save/restore stack and the current path. It mirrors
	/// cairo.c, cairo-gstate.c and cairo-path-fixed.c so the recorder can answer
	/// state queries and hand geometry to Skia without a native context.
	/// </summary>
	/// <remarks>
	/// Cairo stores path points as 24.8 fixed-point device coordinates. Reads convert
	/// back through the inverse CTM, so every point is quantized here as well. Errors
	/// latch like <c>cairo_t</c>: the first failure sticks and later calls are ignored.
	/// </remarks>
	internal sealed class ManagedGraphicsState
	{
		internal enum PathOp : byte { MoveTo, LineTo, CurveTo, ClosePath }

		const double FixedScale = 256.0;
		// CAIRO_TOLERANCE_MINIMUM is one fixed-point unit.
		const double ToleranceMinimum = 1.0 / FixedScale;

		sealed class State
		{
			internal Matrix Ctm = new Matrix();
			internal double LineWidth = 2.0;
			internal LineCap LineCap = LineCap.Butt;
			internal LineJoin LineJoin = LineJoin.Miter;
			internal double MiterLimit = 10.0;
			internal FillRule FillRule = FillRule.Winding;
			internal Operator Operator = Operator.Over;
			internal Antialias Antialias = Antialias.Default;
			internal double Tolerance = 0.1;
			internal double[] Dashes = Array.Empty<double>();
			internal double DashOffset;
			// A solid source is kept as colour; any other source keeps the recorder-owned pattern.
			internal double SourceRed, SourceGreen, SourceBlue, SourceAlpha = 1;
			internal Pattern SourcePattern;
			// The CTM at the time the source was set and the recorded surface it samples, if any.
			internal Matrix SourceMatrix = new Matrix();
			internal RecordedSurfaceSource SourceBinding;
			// Font selection. A null family means the face was set from a native object and has no managed description.
			internal string FontFamily = "Sans";
			internal ManagedFileFontSource FontFileSource;
			internal FontSlant FontSlant = FontSlant.Normal;
			internal FontWeight FontWeight = FontWeight.Normal;
			internal double FontSize = 10;
			internal Matrix FontMatrix = FontScale(10);
			internal Antialias FontAntialias = Antialias.Default;
			internal HintStyle FontHintStyle = HintStyle.Default;
			internal HintMetrics FontHintMetrics = HintMetrics.Default;
			internal SubpixelOrder FontSubpixelOrder = SubpixelOrder.Default;

			internal static Matrix FontScale(double size) { var matrix = new Matrix(); matrix.InitScale(size, size); return matrix; }

			internal State Clone()
			{
				var copy = (State)MemberwiseClone();
				copy.Ctm = (Matrix)Ctm.Clone();
				copy.FontMatrix = (Matrix)FontMatrix.Clone();
				return copy;
			}
		}

		State current = new State();
		// A latched error leaves nothing to read: getters report a default state, as a nil cairo_t does.
		State View => status == Status.Success ? current : new State { SourceAlpha = 0, FontMatrix = new Matrix() };
		readonly Stack<State> saved = new Stack<State>();
		Status status = Status.Success;

		internal ManagedGraphicsState () { }
		internal ManagedGraphicsState (Status initialStatus) { status = initialStatus; }

		// Path, in the same representation as cairo_path_fixed_t: operations plus their
		// device-space fixed-point points, with a lazily emitted initial MOVE_TO.
		readonly List<PathOp> ops = new List<PathOp>();
		readonly List<(long X, long Y)> points = new List<(long, long)>();
		bool hasCurrentPoint;
		bool needsMoveTo = true;
		(long X, long Y) currentPoint;
		(long X, long Y) lastMovePoint;

		internal Status Status => status;
		internal int SaveDepth => saved.Count;
		internal RecordedSurfaceSource[] GetRetainedSourceBindings ()
		{
			var result = new List<RecordedSurfaceSource> (saved.Count + 1);
			if (current.SourceBinding != null) result.Add (current.SourceBinding);
			foreach (State state in saved)
				if (state.SourceBinding != null) result.Add (state.SourceBinding);
			return result.ToArray ();
		}

		// A Cairo group starts with the caller's graphics state, but maintains an
		// independent save stack while keeping the current path shared with the
		// caller. The recorder copies the state at group entry and copies only the
		// resulting path back at group exit.
		internal ManagedGraphicsState CloneForGroup()
		{
			var copy = new ManagedGraphicsState {
				current = current.Clone(), status = status,
				hasCurrentPoint = hasCurrentPoint, needsMoveTo = needsMoveTo,
				currentPoint = currentPoint, lastMovePoint = lastMovePoint
			};
			copy.ops.AddRange(ops);
			copy.points.AddRange(points);
			State[] stack = saved.ToArray();
			for (int i = stack.Length - 1; i >= 0; i--) copy.saved.Push(stack[i].Clone());
			return copy;
		}

		internal void CopyCurrentPathFrom(ManagedGraphicsState source)
		{
			ops.Clear(); ops.AddRange(source.ops);
			points.Clear(); points.AddRange(source.points);
			hasCurrentPoint = source.hasCurrentPoint;
			needsMoveTo = source.needsMoveTo;
			currentPoint = source.currentPoint;
			lastMovePoint = source.lastMovePoint;
		}

		internal Matrix Matrix {
			get => (Matrix)View.Ctm.Clone();
			set {
				if (status != Status.Success) return;
				if (!IsInvertible(value)) { Latch(Status.InvalidMatrix); return; }
				current.Ctm = (Matrix)value.Clone();
			}
		}

		internal double LineWidth { get => View.LineWidth; set { if (status == Status.Success) current.LineWidth = value < 0 ? 0 : value; } }
		internal LineCap LineCap { get => View.LineCap; set { if (status == Status.Success) current.LineCap = value; } }
		internal LineJoin LineJoin { get => View.LineJoin; set { if (status == Status.Success) current.LineJoin = value; } }
		internal double MiterLimit { get => View.MiterLimit; set { if (status == Status.Success) current.MiterLimit = value; } }
		internal FillRule FillRule { get => View.FillRule; set { if (status == Status.Success) current.FillRule = value; } }
		internal Operator Operator { get => View.Operator; set { if (status == Status.Success) current.Operator = value; } }
		internal Antialias Antialias { get => View.Antialias; set { if (status == Status.Success) current.Antialias = value; } }
		internal double Tolerance { get => View.Tolerance; set { if (status == Status.Success) current.Tolerance = value < ToleranceMinimum ? ToleranceMinimum : value; } }
		internal double DashOffset => View.DashOffset;
		internal double[] GetDashes() => (double[])View.Dashes.Clone();

		internal void Latch(Status error) { if (status == Status.Success) status = error; }

		static bool IsInvertible(Matrix m)
		{
			double determinant = m.Xx * m.Yy - m.Yx * m.Xy;
			return double.IsFinite(determinant) && determinant != 0;
		}

		static long ToFixed(double value)
		{
			// cairo_fixed_t uses a magic-number conversion that rounds half to even.
			double scaled = Math.Round(value * FixedScale, MidpointRounding.ToEven);
			if (!(scaled > int.MinValue)) return int.MinValue;
			if (!(scaled < int.MaxValue)) return int.MaxValue;
			return (long)scaled;
		}

		static double FromFixed(long value) => value / FixedScale;

		// ---- save / restore ----

		internal void Save()
		{
			if (status != Status.Success) return;
			saved.Push(current.Clone());
		}

		internal void Restore()
		{
			if (status != Status.Success) return;
			if (saved.Count == 0) { Latch(Status.InvalidRestore); return; }
			current = saved.Pop();
		}

		// ---- transforms ----

		internal void IdentityMatrix() { if (status == Status.Success) current.Ctm = new Matrix(); }

		internal void Translate(double tx, double ty)
		{
			if (status != Status.Success) return;
			var tmp = new Matrix(); tmp.InitTranslate(tx, ty);
			Apply(tmp);
		}

		internal void Scale(double sx, double sy)
		{
			if (status != Status.Success) return;
			var tmp = new Matrix(); tmp.InitScale(sx, sy);
			Apply(tmp);
		}

		internal void Rotate(double angle)
		{
			if (status != Status.Success) return;
			var tmp = new Matrix(); tmp.InitRotate(angle);
			Apply(tmp);
		}

		internal void Transform(Matrix matrix)
		{
			if (status != Status.Success) return;
			Apply((Matrix)matrix.Clone());
		}

		void Apply(Matrix tmp)
		{
			if (!IsInvertible(tmp)) { Latch(Status.InvalidMatrix); return; }
			Matrix product = Matrix.Multiply(tmp, current.Ctm);
			if (!IsInvertible(product)) { Latch(Status.InvalidMatrix); return; }
			current.Ctm = product;
		}

		internal void UserToDevice(ref double x, ref double y) { if (status == Status.Success) current.Ctm.TransformPoint(ref x, ref y); }
		internal void UserToDeviceDistance(ref double dx, ref double dy) { if (status == Status.Success) current.Ctm.TransformDistance(ref dx, ref dy); }

		internal void DeviceToUser(ref double x, ref double y)
		{
			if (status != Status.Success) return;
			Matrix inverse = Inverse();
			inverse.TransformPoint(ref x, ref y);
		}

		internal void DeviceToUserDistance(ref double dx, ref double dy)
		{
			if (status != Status.Success) return;
			Matrix inverse = Inverse();
			inverse.TransformDistance(ref dx, ref dy);
		}

		Matrix Inverse()
		{
			var inverse = (Matrix)current.Ctm.Clone();
			inverse.Invert();
			return inverse;
		}

		// ---- source ----

		static double Restrict(double value) => value < 0 ? 0 : value > 1 ? 1 : value;
		static int Short(double value) => (int)(value * 65535.0 + 0.5);

		internal void SetSourceRgba(double red, double green, double blue, double alpha)
		{
			if (status != Status.Success) return;
			red = Restrict(red); green = Restrict(green); blue = Restrict(blue); alpha = Restrict(alpha);
			// cairo_set_source_rgba keeps the current solid pattern when the new colour has the same
			// 16-bit premultiplied channels, so (r, g, b, 0) never replaces another transparent colour.
			bool unchanged = current.SourcePattern == null
				&& Short(current.SourceRed * current.SourceAlpha) == Short(red * alpha) && Short(current.SourceGreen * current.SourceAlpha) == Short(green * alpha)
				&& Short(current.SourceBlue * current.SourceAlpha) == Short(blue * alpha) && Short(current.SourceAlpha) == Short(alpha);
			if (!unchanged) {
				current.SourceRed = red; current.SourceGreen = green; current.SourceBlue = blue; current.SourceAlpha = alpha;
				current.SourcePattern = null;
			}
			current.SourceMatrix = (Matrix)current.Ctm.Clone();
			current.SourceBinding = null;
		}

		/// <summary>The pattern must stay alive for as long as the state can read it; the recorder owns it.</summary>
		internal void SetSource(Pattern pattern, RecordedSurfaceSource binding)
		{
			if (status != Status.Success) return;
			current.SourcePattern = pattern;
			current.SourceMatrix = (Matrix)current.Ctm.Clone();
			current.SourceBinding = binding;
		}

		/// <summary>Read-only: callers must clone before changing it.</summary>
		internal Matrix SourceMatrix => View.SourceMatrix;
		internal RecordedSurfaceSource SourceBinding => View.SourceBinding;

		/// <summary>The current source: a pattern, or null when it is the solid colour returned by <see cref="SourceColor"/>.</summary>
		internal Pattern SourcePattern => View.SourcePattern;

		internal (double Red, double Green, double Blue, double Alpha) SourceColor {
			get { State view = View; return (view.SourceRed, view.SourceGreen, view.SourceBlue, view.SourceAlpha); }
		}

		// ---- fonts ----

		internal string FontFamily => View.FontFamily;
		internal ManagedFileFontSource FontFileSource => View.FontFileSource;
		internal FontSlant FontSlant => View.FontSlant;
		internal FontWeight FontWeight => View.FontWeight;
		internal double FontSize => View.FontSize;
		internal Matrix FontMatrix { get => (Matrix)View.FontMatrix.Clone(); }

		internal void SelectFontFace(string family, FontSlant slant, FontWeight weight)
		{
			if (status != Status.Success) return;
			current.FontFileSource = null;
			current.FontFamily = family; current.FontSlant = slant; current.FontWeight = weight;
		}

		internal void SetContextFontFace(FontFace fontFace)
		{
			if (status != Status.Success) return;
			current.FontFileSource = fontFace?.ManagedFileSource;
			if (fontFace == null) { current.FontFamily = "Sans"; current.FontSlant = FontSlant.Normal; current.FontWeight = FontWeight.Normal; }
			else if (fontFace.HasManagedMetadata) {
				current.FontFamily = fontFace.ManagedFamily;
				current.FontSlant = fontFace.ManagedSlant;
				current.FontWeight = fontFace.ManagedWeight;
			} else current.FontFamily = null;
		}

		internal void SetScaledFont(ScaledFont scaledFont)
		{
			if (status != Status.Success) return;
            if (scaledFont.Status != Status.Success) { Latch (scaledFont.Status); return; }
			current.FontFileSource = scaledFont.ManagedFileSource;
			if (!scaledFont.HasManagedMetadata) { current.FontFamily = null; return; }
			current.FontFamily = scaledFont.ManagedFamily;
			current.FontSlant = scaledFont.ManagedSlant;
			current.FontWeight = scaledFont.ManagedWeight;
			current.FontMatrix = scaledFont.ManagedFontMatrix;
			current.FontSize = scaledFont.ManagedSize;
			using FontOptions options = scaledFont.GetManagedFontOptions();
			SetFontOptions(options);
		}

		/// <summary>cairo_set_font_size: the font matrix becomes a uniform scale. <see cref="FontSize"/> tracks only this call.</summary>
		internal void SetFontSize(double size)
		{
			if (status != Status.Success) return;
			current.FontSize = size;
			current.FontMatrix = State.FontScale(size);
		}

		internal void SetFontMatrix(Matrix matrix)
		{
			if (status != Status.Success) return;
			current.FontMatrix = (Matrix)matrix.Clone();
		}

		/// <summary>The face was replaced by a native scaled font or face; there is no managed description of it.</summary>
		internal void InvalidateFontFace()
		{
			if (status != Status.Success) return;
			current.FontFileSource = null;
			current.FontFamily = null;
		}

		internal void SetFontOptions(FontOptions options)
		{
			if (status != Status.Success) return;
			current.FontAntialias = options.Antialias; current.FontHintStyle = options.HintStyle;
			current.FontHintMetrics = options.HintMetrics; current.FontSubpixelOrder = options.SubpixelOrder;
		}

		internal static ManagedGraphicsState CreateFontMetricsState(string family, FontSlant slant, FontWeight weight,
			double size, Matrix fontMatrix, FontOptions options, ManagedFileFontSource fileSource = null)
		{
			var state = new ManagedGraphicsState();
			state.SelectFontFace(family, slant, weight);
			state.current.FontFileSource = fileSource;
			state.SetFontSize(size);
			state.SetFontMatrix(fontMatrix);
			state.SetFontOptions(options);
			return state;
		}

		/// <summary>cairo_get_font_options: the options set on the context. The surface's defaults (metrics hinting on) only apply when a scaled font is created.</summary>
		internal FontOptions GetFontOptions()
		{
			State view = View;
			return FontOptions.FromValues(view.FontAntialias, view.FontHintStyle, view.FontHintMetrics, view.FontSubpixelOrder);
		}

		// ---- dash ----

		internal void SetDash(double[] dashes, double offset)
		{
			if (status != Status.Success) return;
			if (dashes == null || dashes.Length == 0) { current.Dashes = Array.Empty<double>(); current.DashOffset = 0; return; }
			double total = 0;
			foreach (double value in dashes) {
				if (value < 0) { Latch(Status.InvalidDash); return; }
				total += value;
			}
			if (total == 0) { Latch(Status.InvalidDash); return; }
			if ((dashes.Length & 1) != 0) total *= 2;
			offset %= total;
			if (offset < 0) offset += total;
			if (offset <= 0) offset = 0;
			current.Dashes = (double[])dashes.Clone();
			current.DashOffset = offset;
		}

		// ---- path construction ----

		internal bool HasCurrentPoint => status == Status.Success && hasCurrentPoint;

		internal bool TryGetCurrentPoint(out double x, out double y)
		{
			x = 0; y = 0;
			if (status != Status.Success || !hasCurrentPoint) return false;
			x = FromFixed(currentPoint.X); y = FromFixed(currentPoint.Y);
			DeviceToUser(ref x, ref y);
			return true;
		}

		internal void NewPath()
		{
			if (status != Status.Success) return;
			ops.Clear(); points.Clear();
			hasCurrentPoint = false;
			needsMoveTo = true;
			currentPoint = default; lastMovePoint = default;
		}

		internal void NewSubPath()
		{
			if (status != Status.Success) return;
			PathNewSubPath();
		}

		internal void MoveTo(double x, double y)
		{
			if (status != Status.Success) return;
			current.Ctm.TransformPoint(ref x, ref y);
			PathMoveTo(ToFixed(x), ToFixed(y));
		}

		internal void LineTo(double x, double y)
		{
			if (status != Status.Success) return;
			current.Ctm.TransformPoint(ref x, ref y);
			PathLineTo(ToFixed(x), ToFixed(y));
		}

		internal void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
		{
			if (status != Status.Success) return;
			current.Ctm.TransformPoint(ref x1, ref y1);
			current.Ctm.TransformPoint(ref x2, ref y2);
			current.Ctm.TransformPoint(ref x3, ref y3);
			PathCurveTo(ToFixed(x1), ToFixed(y1), ToFixed(x2), ToFixed(y2), ToFixed(x3), ToFixed(y3));
		}

		internal void RelMoveTo(double dx, double dy)
		{
			if (!RelativeDelta(ref dx, ref dy, out long fx, out long fy)) return;
			PathMoveTo(currentPoint.X + fx, currentPoint.Y + fy);
		}

		internal void RelLineTo(double dx, double dy)
		{
			if (!RelativeDelta(ref dx, ref dy, out long fx, out long fy)) return;
			PathLineTo(currentPoint.X + fx, currentPoint.Y + fy);
		}

		internal void RelCurveTo(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
		{
			if (status != Status.Success) return;
			if (!hasCurrentPoint) { Latch(Status.NoCurrentPoint); return; }
			current.Ctm.TransformDistance(ref dx1, ref dy1);
			current.Ctm.TransformDistance(ref dx2, ref dy2);
			current.Ctm.TransformDistance(ref dx3, ref dy3);
			(long X, long Y) p = currentPoint;
			PathCurveTo(p.X + ToFixed(dx1), p.Y + ToFixed(dy1), p.X + ToFixed(dx2), p.Y + ToFixed(dy2), p.X + ToFixed(dx3), p.Y + ToFixed(dy3));
		}

		bool RelativeDelta(ref double dx, ref double dy, out long fx, out long fy)
		{
			fx = fy = 0;
			if (status != Status.Success) return false;
			if (!hasCurrentPoint) { Latch(Status.NoCurrentPoint); return false; }
			current.Ctm.TransformDistance(ref dx, ref dy);
			fx = ToFixed(dx); fy = ToFixed(dy);
			return true;
		}

		internal void Rectangle(double x, double y, double width, double height)
		{
			if (status != Status.Success) return;
			MoveTo(x, y);
			RelLineTo(width, 0);
			RelLineTo(0, height);
			RelLineTo(-width, 0);
			ClosePath();
		}

		internal void Arc(double xc, double yc, double radius, double angle1, double angle2) => ArcPathBuilder.Append(new ArcTarget(this), xc, yc, radius, angle1, angle2, false);
		internal void ArcNegative(double xc, double yc, double radius, double angle1, double angle2) => ArcPathBuilder.Append(new ArcTarget(this), xc, yc, radius, angle1, angle2, true);

		readonly struct ArcTarget : IArcPathTarget
		{
			readonly ManagedGraphicsState state;
			internal ArcTarget(ManagedGraphicsState state) { this.state = state; }
			Status IArcPathTarget.Status => state.Status;
			Matrix IArcPathTarget.Matrix => state.Matrix;
			double IArcPathTarget.Tolerance => state.Tolerance;
			void IArcPathTarget.LineTo(double x, double y) => state.LineTo(x, y);
			void IArcPathTarget.CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) => state.CurveTo(x1, y1, x2, y2, x3, y3);
		}

		[StructLayout(LayoutKind.Sequential)] struct NativePathHeader { public Status Status; public IntPtr Data; public int Count; }

		/// <summary>cairo_append_path for a native cairo_path_t: the data is user-space and replays through the public operations.</summary>
		internal void AppendNativePath(IntPtr pathHandle)
		{
			if (status != Status.Success) return;
			NativePathHeader header = Marshal.PtrToStructure<NativePathHeader>(pathHandle);
			if (header.Status != Status.Success) { Latch(header.Status); return; }
			for (int index = 0; index < header.Count;) {
				IntPtr element = IntPtr.Add(header.Data, checked(index * 16));
				int kind = Marshal.ReadInt32(element), length = Marshal.ReadInt32(element, 4);
				int expected = kind == 0 || kind == 1 ? 2 : kind == 2 ? 4 : kind == 3 ? 1 : 0;
				if (expected == 0 || length != expected || index + length > header.Count) { Latch(Status.InvalidPathData); return; }
				switch (kind) {
				case 0: MoveTo(Coordinate(element, 1, 0), Coordinate(element, 1, 1)); break;
				case 1: LineTo(Coordinate(element, 1, 0), Coordinate(element, 1, 1)); break;
				case 2: CurveTo(Coordinate(element, 1, 0), Coordinate(element, 1, 1), Coordinate(element, 2, 0), Coordinate(element, 2, 1), Coordinate(element, 3, 0), Coordinate(element, 3, 1)); break;
				default: ClosePath(); break;
				}
				index += length;
			}
		}

		static double Coordinate(IntPtr element, int slot, int axis) => BitConverter.Int64BitsToDouble(Marshal.ReadInt64(element, slot * 16 + axis * 8));

		internal void ClosePath()
		{
			if (status != Status.Success) return;
			if (!hasCurrentPoint) return;
			// A line back to the start is added first so degeneracies resolve, then dropped.
			PathLineTo(lastMovePoint.X, lastMovePoint.Y);
			if (LastOp == PathOp.LineTo) DropLineTo();
			needsMoveTo = true;
			ops.Add(PathOp.ClosePath);
		}

		PathOp? LastOp => ops.Count == 0 ? null : ops[ops.Count - 1];

		void PathNewSubPath()
		{
			if (!needsMoveTo) needsMoveTo = true;
			hasCurrentPoint = false;
		}

		void PathMoveTo(long x, long y)
		{
			PathNewSubPath();
			hasCurrentPoint = true;
			currentPoint = (x, y);
			lastMovePoint = currentPoint;
		}

		void MoveToApply()
		{
			if (!needsMoveTo) return;
			needsMoveTo = false;
			currentPoint = lastMovePoint;
			ops.Add(PathOp.MoveTo);
			points.Add(lastMovePoint);
		}

		void PathLineTo(long x, long y)
		{
			if (!hasCurrentPoint) { PathMoveTo(x, y); return; }
			MoveToApply();
			// A degenerate segment is dropped unless it directly follows the initial MOVE_TO.
			if (LastOp != PathOp.MoveTo && x == currentPoint.X && y == currentPoint.Y) return;
			if (LastOp == PathOp.LineTo) {
				(long X, long Y) penultimate = points[points.Count - 2];
				if (penultimate == currentPoint) DropLineTo();
				else {
					long dxPrev = currentPoint.X - penultimate.X, dyPrev = currentPoint.Y - penultimate.Y;
					long dxSelf = x - currentPoint.X, dySelf = y - currentPoint.Y;
					bool equal = dyPrev * dxSelf == dySelf * dxPrev;
					bool backwards = dxPrev * dxSelf + dyPrev * dySelf < 0;
					if (equal && !backwards) DropLineTo();
				}
			}
			ops.Add(PathOp.LineTo);
			points.Add((x, y));
			currentPoint = (x, y);
		}

		void PathCurveTo(long x0, long y0, long x1, long y1, long x2, long y2)
		{
			if (!hasCurrentPoint) PathMoveTo(x0, y0);
			// A curve that stays on the current point does nothing.
			if (currentPoint == (x0, y0) && currentPoint == (x1, y1) && currentPoint == (x2, y2)) return;
			MoveToApply();
			if (LastOp == PathOp.LineTo && points[points.Count - 2] == currentPoint) DropLineTo();
			ops.Add(PathOp.CurveTo);
			points.Add((x0, y0)); points.Add((x1, y1)); points.Add((x2, y2));
			currentPoint = (x2, y2);
		}

		void DropLineTo()
		{
			ops.RemoveAt(ops.Count - 1);
			points.RemoveAt(points.Count - 1);
		}

		/// <summary>cairo_copy_path_flat: curves become line segments by Cairo's adaptive de Casteljau subdivision at the context tolerance.</summary>
		internal PathElement[] CopyPathFlat()
		{
			var result = new List<PathElement>(ops.Count);
			if (status != Status.Success) return result.ToArray();
			Matrix inverse = Inverse();
			PathElement Element(PathOp op, (long X, long Y) point)
			{
				double x = FromFixed(point.X), y = FromFixed(point.Y);
				inverse.TransformPoint(ref x, ref y);
				return new PathElement(op, x, y);
			}
			double toleranceSquared = current.Tolerance * current.Tolerance;
			int index = 0;
			(long X, long Y) cursor = default, moveStart = default;
			foreach (PathOp op in ops) {
				switch (op) {
				case PathOp.MoveTo: cursor = moveStart = points[index++]; result.Add(Element(PathOp.MoveTo, cursor)); break;
				case PathOp.LineTo: cursor = points[index++]; result.Add(Element(PathOp.LineTo, cursor)); break;
				case PathOp.CurveTo: {
					(long X, long Y) b = points[index], c = points[index + 1], d = points[index + 2];
					index += 3;
					if (cursor == b && c == d) { cursor = d; result.Add(Element(PathOp.LineTo, d)); break; }
					var last = cursor;
					Decompose(cursor, b, c, d, toleranceSquared, ref last, point => result.Add(Element(PathOp.LineTo, point)));
					if (last != d) { result.Add(Element(PathOp.LineTo, d)); }
					cursor = d;
					break;
				}
				default: cursor = moveStart; result.Add(new PathElement(PathOp.ClosePath)); break;
				}
			}
			if (needsMoveTo && hasCurrentPoint) result.Add(Element(PathOp.MoveTo, currentPoint));
			return result.ToArray();
		}

		static void Decompose((long X, long Y) a, (long X, long Y) b, (long X, long Y) c, (long X, long Y) d, double toleranceSquared, ref (long X, long Y) last, Action<(long X, long Y)> addPoint)
		{
			if (SplineErrorSquared(a, b, c, d) < toleranceSquared) {
				if (last != a) { addPoint(a); last = a; }
				return;
			}
			(long X, long Y) ab = Half(a, b), bc = Half(b, c), cd = Half(c, d), abbc = Half(ab, bc), bccd = Half(bc, cd), final = Half(abbc, bccd);
			Decompose(a, ab, abbc, final, toleranceSquared, ref last, addPoint);
			Decompose(final, bccd, cd, d, toleranceSquared, ref last, addPoint);
		}

		static (long X, long Y) Half((long X, long Y) a, (long X, long Y) b) => (a.X + ((b.X - a.X) >> 1), a.Y + ((b.Y - a.Y) >> 1));

		static double SplineErrorSquared((long X, long Y) a, (long X, long Y) b, (long X, long Y) c, (long X, long Y) d)
		{
			double bdx = FromFixed(b.X - a.X), bdy = FromFixed(b.Y - a.Y), cdx = FromFixed(c.X - a.X), cdy = FromFixed(c.Y - a.Y);
			if (a != d) {
				double dx = FromFixed(d.X - a.X), dy = FromFixed(d.Y - a.Y), v = dx * dx + dy * dy;
				double u = bdx * dx + bdy * dy;
				if (u <= 0) { } else if (u >= v) { bdx -= dx; bdy -= dy; } else { bdx -= u / v * dx; bdy -= u / v * dy; }
				u = cdx * dx + cdy * dy;
				if (u <= 0) { } else if (u >= v) { cdx -= dx; cdy -= dy; } else { cdx -= u / v * dx; cdy -= u / v * dy; }
			}
			double berr = bdx * bdx + bdy * bdy, cerr = cdx * cdx + cdy * cdy;
			return berr > cerr ? berr : cerr;
		}

		// ---- geometry queries ----

		/// <summary>The user-space box of a device-space box: the bounds of its four transformed corners.</summary>
		Rectangle DeviceBoxToUser(double x1, double y1, double x2, double y2)
		{
			Matrix inverse = Inverse();
			double[] xs = { x1, x2, x1, x2 }, ys = { y1, y1, y2, y2 };
			double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
			for (int i = 0; i < 4; i++) {
				double x = xs[i], y = ys[i];
				inverse.TransformPoint(ref x, ref y);
				left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
			}
			return new Rectangle(left, top, right - left, bottom - top);
		}

		/// <summary>
		/// cairo_fill_extents: the box of the area a fill would cover. The flattened polygon (curves flattened at the context
		/// tolerance, each subpath implicitly closed) is resolved with the fill rule, so lines, collinear runs and
		/// back-tracked spikes add nothing. Cairo's trapezoid extents also keep spikes made of partially overlapping
		/// collinear edges; those cases give a larger box there than this one.
		/// </summary>
		internal Rectangle FillExtents()
		{
			if (status != Status.Success) return new Rectangle(0, 0, 0, 0);
			using var polygon = new SKPath { FillType = current.FillRule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding };
			foreach (List<(long X, long Y)> subpath in FlattenDeviceSubpaths()) {
				if (subpath.Count < 3 || DoubledArea(subpath) == 0) continue;
				polygon.MoveTo((float)FromFixed(subpath[0].X), (float)FromFixed(subpath[0].Y));
				for (int i = 1; i < subpath.Count; i++) polygon.LineTo((float)FromFixed(subpath[i].X), (float)FromFixed(subpath[i].Y));
				polygon.Close();
			}
			using SKPath area = polygon.Simplify();
			if (area == null || area.PointCount == 0) return new Rectangle(0, 0, 0, 0);
			SKRect bounds = area.TightBounds;
			if (bounds.Width <= 0 || bounds.Height <= 0) return new Rectangle(0, 0, 0, 0);
			return DeviceBoxToUser(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
		}

		// Twice the signed area of the closed polygon, exact in fixed-point integers.
		static long DoubledArea(List<(long X, long Y)> polygon)
		{
			long sum = 0;
			for (int i = 0; i < polygon.Count; i++) {
				(long X, long Y) from = polygon[i], to = polygon[(i + 1) % polygon.Count];
				sum += from.X * to.Y - to.X * from.Y;
			}
			return sum;
		}

		/// <summary>The path as device-space polylines, one per subpath.</summary>
		List<List<(long X, long Y)>> FlattenDeviceSubpaths()
		{
			var result = new List<List<(long X, long Y)>>();
			List<(long X, long Y)> run = null;
			double toleranceSquared = current.Tolerance * current.Tolerance;
			int index = 0;
			(long X, long Y) cursor = default;
			foreach (PathOp op in ops) {
				switch (op) {
				case PathOp.MoveTo: cursor = points[index++]; run = new List<(long X, long Y)> { cursor }; result.Add(run); break;
				case PathOp.LineTo: cursor = points[index++]; run?.Add(cursor); break;
				case PathOp.CurveTo: {
					(long X, long Y) b = points[index], c = points[index + 1], d = points[index + 2];
					index += 3;
					var last = cursor;
					if (cursor == b && c == d) run?.Add(d);
					else {
						Decompose(cursor, b, c, d, toleranceSquared, ref last, point => run?.Add(point));
						if (last != d) run?.Add(d);
					}
					cursor = d;
					break;
				}
				default: run = null; break;
				}
			}
			return result;
		}

		/// <summary>cairo_stroke_extents: the box of the stroked outline (caps, joins and dashes), as a user-space box. Round joins are not tessellated, so they differ from Cairo by less than the tolerance.</summary>
		internal Rectangle StrokeExtents()
		{
			if (status != Status.Success || ops.Count == 0 || current.LineWidth <= 0) return new Rectangle(0, 0, 0, 0);
			using SKPath outline = StrokeOutline();
			if (outline == null || outline.PointCount == 0) return new Rectangle(0, 0, 0, 0);
			outline.Transform(new SKMatrix((float)current.Ctm.Xx, (float)current.Ctm.Xy, (float)current.Ctm.X0, (float)current.Ctm.Yx, (float)current.Ctm.Yy, (float)current.Ctm.Y0, 0, 0, 1));
			SKRect device = outline.TightBounds;
			if (device.Width <= 0 && device.Height <= 0) return new Rectangle(0, 0, 0, 0);
			return DeviceBoxToUser(FromFixed(ToFixed(device.Left)), FromFixed(ToFixed(device.Top)), FromFixed(ToFixed(device.Right)), FromFixed(ToFixed(device.Bottom)));
		}

		/// <summary>The stroked area of the current path in user space, or null when nothing would be drawn.</summary>
		SKPath StrokeOutline()
		{
			// Cairo strokes the polygon it flattens at the context tolerance, so flatten first.
			using SKPath path = RecordedGuiDrawing.CopyPath(CopyPathFlat());
			using var paint = new SKPaint {
				Style = SKPaintStyle.Stroke, StrokeWidth = (float)current.LineWidth, StrokeMiter = (float)current.MiterLimit,
				StrokeCap = (SKStrokeCap)(int)current.LineCap, StrokeJoin = (SKStrokeJoin)(int)current.LineJoin
			};
			float[] intervals = current.Dashes.Length == 0 ? null : DashIntervals(current.Dashes);
			using SKPathEffect dash = intervals == null ? null : SKPathEffect.CreateDash(intervals, (float)current.DashOffset);
			paint.PathEffect = dash;
			return RecordedPreparationCache.StrokeOutline(path, paint, intervals, (float)current.DashOffset);
		}

		/// <summary>cairo_in_fill: whether a user-space point lies inside the filled area of the current path.</summary>
		internal bool InFill(double x, double y)
		{
			if (status != Status.Success || ops.Count == 0) return false;
			current.Ctm.TransformPoint(ref x, ref y);
			using SKPath path = RecordedGuiDrawing.CopyPath(CopyPathFlat());
			path.FillType = current.FillRule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
			path.Transform(RecordedGuiDrawing.ToSkia(current.Ctm));
			return path.Contains((float)x, (float)y);
		}

		/// <summary>cairo_in_stroke: whether a user-space point lies on the stroke of the current path.</summary>
		internal bool InStroke(double x, double y)
		{
			if (status != Status.Success || ops.Count == 0 || current.LineWidth <= 0) return false;
			using SKPath outline = StrokeOutline();
			return outline != null && outline.Contains((float)x, (float)y);
		}

		static float[] DashIntervals(double[] dashes)
		{
			var intervals = new float[dashes.Length % 2 == 0 ? dashes.Length : dashes.Length * 2];
			for (int i = 0; i < intervals.Length; i++) intervals[i] = (float)dashes[i % dashes.Length];
			return intervals;
		}

		// ---- path export ----

		/// <summary>One path element in user space, as cairo_copy_path reports it.</summary>
		internal readonly struct PathElement
		{
			internal readonly PathOp Op;
			internal readonly double X1, Y1, X2, Y2, X3, Y3;
			internal PathElement(PathOp op, double x1 = 0, double y1 = 0, double x2 = 0, double y2 = 0, double x3 = 0, double y3 = 0)
			{ Op = op; X1 = x1; Y1 = y1; X2 = x2; Y2 = y2; X3 = x3; Y3 = y3; }
		}

		internal bool HasCurves => ops.Contains(PathOp.CurveTo);
        internal bool HasFillSegments => ops.Contains (PathOp.LineTo) || ops.Contains (PathOp.CurveTo);

		internal PathElement[] CopyPath()
		{
			var result = new List<PathElement>(ops.Count);
			if (status != Status.Success) return result.ToArray();
			Matrix inverse = Inverse();
			int index = 0;
			foreach (PathOp op in ops) {
				switch (op) {
				case PathOp.MoveTo:
				case PathOp.LineTo: {
					double x = FromFixed(points[index].X), y = FromFixed(points[index].Y); index++;
					inverse.TransformPoint(ref x, ref y);
					result.Add(new PathElement(op, x, y));
					break;
				}
				case PathOp.CurveTo: {
					double x1 = FromFixed(points[index].X), y1 = FromFixed(points[index].Y);
					double x2 = FromFixed(points[index + 1].X), y2 = FromFixed(points[index + 1].Y);
					double x3 = FromFixed(points[index + 2].X), y3 = FromFixed(points[index + 2].Y); index += 3;
					inverse.TransformPoint(ref x1, ref y1); inverse.TransformPoint(ref x2, ref y2); inverse.TransformPoint(ref x3, ref y3);
					result.Add(new PathElement(op, x1, y1, x2, y2, x3, y3));
					break;
				}
				default:
					result.Add(new PathElement(PathOp.ClosePath));
					break;
				}
			}
			if (needsMoveTo && hasCurrentPoint) {
				// cairo_copy_path reports the pending initial MOVE_TO.
				double x = FromFixed(currentPoint.X), y = FromFixed(currentPoint.Y);
				inverse.TransformPoint(ref x, ref y);
				result.Add(new PathElement(PathOp.MoveTo, x, y));
			}
			return result.ToArray();
		}
	}
}
