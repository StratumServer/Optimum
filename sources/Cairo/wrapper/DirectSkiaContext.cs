using System;
using System.Collections.Generic;
using System.Text;
using SkiaSharp;

#nullable disable

namespace Cairo
{
	internal sealed class DirectSkiaContext : IDisposable
	{
		readonly ImageSurface surface;
		readonly SKCanvas canvas;
		readonly Stack<DirectSkiaState> states = new();

		SKPath path = new();
		SKPaint paint = new() { IsAntialias = true, SubpixelText = true };
		SKPoint currentPoint = new(0, 0);

		string fontFamily = "Sans";
		double fontSize = 12.0;
		FontSlant fontSlant = FontSlant.Normal;
		FontWeight fontWeight = FontWeight.Normal;
		FontOptions fontOptions = new();

		Matrix cairoMatrix = new();
		Antialias antialias = Antialias.Default;
		FillRule fillRule = FillRule.Winding;
		Operator cairoOperator = Operator.Over;
		double tolerance = 0.1;
		bool disposed;

		internal DirectSkiaContext(ImageSurface surface)
		{
			this.surface = surface ?? throw new ArgumentNullException(nameof(surface));
			canvas = surface.DirectCanvas ?? throw new InvalidOperationException("DirectSkiaContext requires a valid DirectCanvas.");
		}

		internal void MoveTo(double x, double y)
		{
			currentPoint = new SKPoint((float)x, (float)y);
			path.MoveTo(currentPoint);
		}

		internal void LineTo(double x, double y)
		{
			currentPoint = new SKPoint((float)x, (float)y);
			path.LineTo(currentPoint);
		}

		internal void CurveTo(double x1, double y1, double x2, double y2, double x3, double y3)
		{
			path.CubicTo((float)x1, (float)y1, (float)x2, (float)y2, (float)x3, (float)y3);
			currentPoint = new SKPoint((float)x3, (float)y3);
		}

		internal void RelMoveTo(double dx, double dy) => MoveTo(currentPoint.X + dx, currentPoint.Y + dy);

		internal void RelLineTo(double dx, double dy) => LineTo(currentPoint.X + dx, currentPoint.Y + dy);

		internal void RelCurveTo(double dx1, double dy1, double dx2, double dy2, double dx3, double dy3)
			=> CurveTo(currentPoint.X + dx1, currentPoint.Y + dy1, currentPoint.X + dx2, currentPoint.Y + dy2, currentPoint.X + dx3, currentPoint.Y + dy3);

		internal void Rectangle(double x, double y, double width, double height)
		{
			path.AddRect(SKRect.Create((float)x, (float)y, (float)width, (float)height));
			currentPoint = new SKPoint((float)x, (float)y);
		}

		internal void Arc(double xc, double yc, double radius, double angle1, double angle2)
			=> AppendArc(xc, yc, radius, angle1, angle2, false);

		internal void ArcNegative(double xc, double yc, double radius, double angle1, double angle2)
			=> AppendArc(xc, yc, radius, angle1, angle2, true);

		void AppendArc(double xc, double yc, double radius, double angle1, double angle2, bool negative)
		{
			ArcPathBuilder.Append(new DirectArcTarget(this), xc, yc, radius, angle1, angle2, negative);
		}

		readonly struct DirectArcTarget : IArcPathTarget
		{
			readonly DirectSkiaContext context;
			internal DirectArcTarget(DirectSkiaContext context) { this.context = context; }
			Status IArcPathTarget.Status => Status.Success;
			Matrix IArcPathTarget.Matrix => context.cairoMatrix;
			double IArcPathTarget.Tolerance => context.tolerance;
			void IArcPathTarget.LineTo(double x, double y) => context.LineTo(x, y);
			void IArcPathTarget.CurveTo(double x1, double y1, double x2, double y2, double x3, double y3) => context.CurveTo(x1, y1, x2, y2, x3, y3);
		}

		internal void ClosePath() => path.Close();

		internal void NewPath() => path.Reset();

		internal bool HasCurrentPoint => path.PointCount > 0;

		internal PointD CurrentPoint => new(currentPoint.X, currentPoint.Y);

		internal void Save()
		{
			states.Push(new DirectSkiaState(currentPoint, paint, path, fontFamily, fontSize, fontSlant, fontWeight, fontOptions, cairoMatrix, antialias, fillRule, cairoOperator, tolerance));
			canvas.Save();
		}

		internal void Restore()
		{
			if (states.Count > 0)
			{
				var s = states.Pop();
				currentPoint = s.Point;
				paint.Dispose();
				paint = s.Paint;
				path.Dispose();
				path = s.Path;
				fontFamily = s.FontFamily;
				fontSize = s.FontSize;
				fontSlant = s.FontSlant;
				fontWeight = s.FontWeight;
				fontOptions = s.FontOptions;
				cairoMatrix = s.CairoMatrix;
				antialias = s.Antialias;
				fillRule = s.FillRule;
				cairoOperator = s.Operator;
				tolerance = s.Tolerance;
			}
			canvas.Restore();
		}

		internal void SetSourceRGBA(double r, double g, double b, double a)
		{
			byte red = (byte)Math.Clamp((int)Math.Round(r * 255.0), 0, 255);
			byte green = (byte)Math.Clamp((int)Math.Round(g * 255.0), 0, 255);
			byte blue = (byte)Math.Clamp((int)Math.Round(b * 255.0), 0, 255);
			byte alpha = (byte)Math.Clamp((int)Math.Round(a * 255.0), 0, 255);
			paint.Shader = null;
			paint.Color = new SKColor(red, green, blue, alpha);
		}

		internal void SetSourceRGB(double r, double g, double b) => SetSourceRGBA(r, g, b, 1.0);

		internal void SetSourceColor(Color color) => SetSourceRGBA(color.R, color.G, color.B, color.A);

		internal void SetSource(Pattern pattern)
		{
			if (pattern == null) return;
			if (pattern is SolidPattern solid)
			{
				paint.Shader = null;
				paint.Color = new SKColor(
					(byte)Math.Clamp((int)Math.Round(solid.Color.R * 255.0), 0, 255),
					(byte)Math.Clamp((int)Math.Round(solid.Color.G * 255.0), 0, 255),
					(byte)Math.Clamp((int)Math.Round(solid.Color.B * 255.0), 0, 255),
					(byte)Math.Clamp((int)Math.Round(solid.Color.A * 255.0), 0, 255));
				return;
			}
			if (pattern is SurfacePattern surfacePattern)
			{
				SetSourceSurfacePattern(surfacePattern);
				return;
			}
			if (pattern is LinearGradient linear)
			{
				paint.Shader = RecordedCairoGradient.Create(linear.Handle, PatternType.Linear, linear.Extend, SKMatrix.Identity, linear.Matrix, surface.Width, surface.Height, out _);
				return;
			}
			if (pattern is RadialGradient radial)
			{
				paint.Shader = RecordedCairoGradient.Create(radial.Handle, PatternType.Radial, radial.Extend, SKMatrix.Identity, radial.Matrix, surface.Width, surface.Height, out _);
			}
		}

		internal void SetSourceSurface(Surface sourceSurface, double x, double y)
		{
			if (sourceSurface is ImageSurface img)
			{
				img.FlushDirectSurface();
				SKImage image = img.DirectSurface != null ? img.DirectSurface.Snapshot() : null;
				if (image != null)
				{
					var matrix = SKMatrix.CreateTranslation((float)x, (float)y);
					paint.Shader = image.ToShader(SKShaderTileMode.Decal, SKShaderTileMode.Decal, matrix);
				}
			}
		}

		void SetSourceSurfacePattern(SurfacePattern pattern)
		{
			if (pattern.RecordedSource != null)
			{
				using var snapshot = pattern.RecordedSource.Capture();
				if (snapshot != null)
				{
					using var bmp = new SKBitmap(snapshot.Width, snapshot.Height);
					using (var c = new SKCanvas(bmp)) snapshot.DrawTo(c);
					using var img = SKImage.FromBitmap(bmp);
					SKShaderTileMode tile = pattern.Extend == Extend.Repeat ? SKShaderTileMode.Repeat :
						pattern.Extend == Extend.Reflect ? SKShaderTileMode.Mirror :
						pattern.Extend == Extend.Pad ? SKShaderTileMode.Clamp : SKShaderTileMode.Decal;
					paint.Shader = img.ToShader(tile, tile);
				}
			}
		}

		internal double LineWidth
		{
			get => paint.StrokeWidth;
			set => paint.StrokeWidth = (float)value;
		}

		internal LineCap LineCap
		{
			get => paint.StrokeCap switch
			{
				SKStrokeCap.Round => LineCap.Round,
				SKStrokeCap.Square => LineCap.Square,
				_ => LineCap.Butt
			};
			set => paint.StrokeCap = value switch
			{
				LineCap.Round => SKStrokeCap.Round,
				LineCap.Square => SKStrokeCap.Square,
				_ => SKStrokeCap.Butt
			};
		}

		internal LineJoin LineJoin
		{
			get => paint.StrokeJoin switch
			{
				SKStrokeJoin.Round => LineJoin.Round,
				SKStrokeJoin.Bevel => LineJoin.Bevel,
				_ => LineJoin.Miter
			};
			set => paint.StrokeJoin = value switch
			{
				LineJoin.Round => SKStrokeJoin.Round,
				LineJoin.Bevel => SKStrokeJoin.Bevel,
				_ => SKStrokeJoin.Miter
			};
		}

		internal double MiterLimit
		{
			get => paint.StrokeMiter;
			set => paint.StrokeMiter = (float)value;
		}

		internal Operator Operator
		{
			get => cairoOperator;
			set
			{
				cairoOperator = value;
				paint.BlendMode = MapOperator(value);
			}
		}

		internal Antialias Antialias
		{
			get => antialias;
			set
			{
				antialias = value;
				paint.IsAntialias = value != Antialias.None;
			}
		}

		internal FillRule FillRule
		{
			get => fillRule;
			set
			{
				fillRule = value;
				path.FillType = value == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
			}
		}

		internal double Tolerance
		{
			get => tolerance;
			set => tolerance = value;
		}

		internal Matrix Matrix
		{
			get => (Matrix)cairoMatrix.Clone();
			set
			{
				cairoMatrix = (Matrix)value.Clone();
				canvas.SetMatrix(ToSkiaMatrix(cairoMatrix));
			}
		}

		internal void IdentityMatrix()
		{
			cairoMatrix = new Matrix();
			canvas.SetMatrix(SKMatrix.Identity);
		}

		internal void Translate(double tx, double ty)
		{
			canvas.Translate((float)tx, (float)ty);
			cairoMatrix.Translate(tx, ty);
		}

		internal void Scale(double sx, double sy)
		{
			canvas.Scale((float)sx, (float)sy);
			cairoMatrix.Scale(sx, sy);
		}

		internal void Rotate(double angle)
		{
			canvas.RotateRadians((float)angle);
			cairoMatrix.Rotate(angle);
		}

		internal void Transform(Matrix matrix)
		{
			canvas.Concat(ToSkiaMatrix(matrix));
			cairoMatrix.Multiply(matrix);
		}

		internal void Fill()
		{
			paint.Style = SKPaintStyle.Fill;
			path.FillType = fillRule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
			canvas.DrawPath(path, paint);
			path.Reset();
		}

		internal void FillPreserve()
		{
			paint.Style = SKPaintStyle.Fill;
			path.FillType = fillRule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
			canvas.DrawPath(path, paint);
		}

		internal void Stroke()
		{
			paint.Style = SKPaintStyle.Stroke;
			canvas.DrawPath(path, paint);
			path.Reset();
		}

		internal void StrokePreserve()
		{
			paint.Style = SKPaintStyle.Stroke;
			canvas.DrawPath(path, paint);
		}

		internal void Paint()
		{
			paint.Style = SKPaintStyle.Fill;
			canvas.DrawPaint(paint);
		}

		internal void PaintWithAlpha(double alpha)
		{
			var previousAlpha = paint.Color.Alpha;
			paint.Color = paint.Color.WithAlpha((byte)Math.Clamp((int)Math.Round(previousAlpha * alpha), 0, 255));
			paint.Style = SKPaintStyle.Fill;
			canvas.DrawPaint(paint);
			paint.Color = paint.Color.WithAlpha(previousAlpha);
		}

		internal void Clip()
		{
			path.FillType = fillRule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
			canvas.ClipPath(path, antialias: antialias != Antialias.None);
			path.Reset();
		}

		internal void ClipPreserve()
		{
			path.FillType = fillRule == FillRule.EvenOdd ? SKPathFillType.EvenOdd : SKPathFillType.Winding;
			canvas.ClipPath(path, antialias: antialias != Antialias.None);
		}

		internal void ResetClip()
		{
			// Resetting clip without popping state restores to canvas root
		}

		internal Rectangle FillExtents()
		{
			var bounds = path.Bounds;
			return new Rectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height);
		}

		internal Rectangle StrokeExtents()
		{
			var bounds = path.Bounds;
			float halfWidth = (float)(paint.StrokeWidth / 2.0);
			return new Rectangle(bounds.Left - halfWidth, bounds.Top - halfWidth, bounds.Width + halfWidth * 2, bounds.Height + halfWidth * 2);
		}

		internal void SelectFontFace(string family, FontSlant slant, FontWeight weight)
		{
			fontFamily = family;
			fontSlant = slant;
			fontWeight = weight;
		}

		internal void SetFontSize(double size) => fontSize = size;

		internal void ShowText(string text)
		{
			if (string.IsNullOrEmpty(text)) return;
			using var font = SkiaGuiFont.Create(fontFamily, fontSlant, fontWeight, fontSize, fontOptions);
			paint.Style = SKPaintStyle.Fill;
			canvas.DrawText(text, currentPoint.X, currentPoint.Y, font, paint);
			float textWidth = font.MeasureText(text);
			currentPoint.X += textWidth;
		}

		internal void ShowText(byte[] utf8)
		{
			if (utf8 == null || utf8.Length == 0) return;
			string text = Encoding.UTF8.GetString(utf8).TrimEnd('\0');
			ShowText(text);
		}

		internal void TextPath(string text)
		{
			if (string.IsNullOrEmpty(text)) return;
			using var font = SkiaGuiFont.Create(fontFamily, fontSlant, fontWeight, fontSize, fontOptions);
			ushort[] glyphs = font.GetGlyphs(text);
			if (glyphs == null || glyphs.Length == 0) return;
			float[] widths = new float[glyphs.Length];
			font.GetGlyphWidths(glyphs, widths, null);
			float x = currentPoint.X;
			for (int i = 0; i < glyphs.Length; i++)
			{
				using var glyphPath = font.GetGlyphPath(glyphs[i]);
				if (glyphPath != null)
				{
					glyphPath.Offset(x, currentPoint.Y);
					path.AddPath(glyphPath);
				}
				x += widths[i];
			}
			currentPoint.X = x;
		}

		internal void TextPath(byte[] utf8)
		{
			if (utf8 == null || utf8.Length == 0) return;
			string text = Encoding.UTF8.GetString(utf8).TrimEnd('\0');
			TextPath(text);
		}

		internal TextExtents TextExtents(string text)
			=> OptimumSkiaTextMetrics.GetTextExtents(fontFamily, fontWeight, fontSlant, fontSize, text ?? string.Empty);

		internal TextExtents TextExtents(byte[] utf8)
		{
			string text = utf8 != null ? Encoding.UTF8.GetString(utf8).TrimEnd('\0') : string.Empty;
			return TextExtents(text);
		}

		internal FontExtents FontExtents
			=> OptimumSkiaTextMetrics.GetFontExtents(fontFamily, fontWeight, fontSlant, fontSize);

		static SKBlendMode MapOperator(Operator op) => op switch
		{
			Operator.Clear => SKBlendMode.Clear,
			Operator.Source => SKBlendMode.Src,
			Operator.Over => SKBlendMode.SrcOver,
			Operator.In => SKBlendMode.SrcIn,
			Operator.Out => SKBlendMode.SrcOut,
			Operator.Atop => SKBlendMode.SrcATop,
			Operator.Dest => SKBlendMode.Dst,
			Operator.DestOver => SKBlendMode.DstOver,
			Operator.DestIn => SKBlendMode.DstIn,
			Operator.DestOut => SKBlendMode.DstOut,
			Operator.DestAtop => SKBlendMode.DstATop,
			Operator.Xor => SKBlendMode.Xor,
			Operator.Add => SKBlendMode.Plus,
			Operator.Multiply => SKBlendMode.Multiply,
			Operator.Screen => SKBlendMode.Screen,
			Operator.Overlay => SKBlendMode.Overlay,
			Operator.Darken => SKBlendMode.Darken,
			Operator.Lighten => SKBlendMode.Lighten,
			Operator.ColorDodge => SKBlendMode.ColorDodge,
			Operator.ColorBurn => SKBlendMode.ColorBurn,
			Operator.HardLight => SKBlendMode.HardLight,
			Operator.SoftLight => SKBlendMode.SoftLight,
			Operator.Difference => SKBlendMode.Difference,
			Operator.Exclusion => SKBlendMode.Exclusion,
			_ => SKBlendMode.SrcOver
		};

		static SKMatrix ToSkiaMatrix(Matrix m) => new()
		{
			ScaleX = (float)m.Xx,
			SkewX = (float)m.Xy,
			TransX = (float)m.X0,
			SkewY = (float)m.Yx,
			ScaleY = (float)m.Yy,
			TransY = (float)m.Y0,
			Persp0 = 0f,
			Persp1 = 0f,
			Persp2 = 1f
		};

		public void Dispose()
		{
			if (disposed) return;
			disposed = true;
			while (states.Count > 0)
			{
				var s = states.Pop();
				s.Paint?.Dispose();
				s.Path?.Dispose();
			}
			paint?.Dispose();
			path?.Dispose();
		}

		sealed class DirectSkiaState
		{
			public readonly SKPoint Point;
			public readonly SKPaint Paint;
			public readonly SKPath Path;
			public readonly string FontFamily;
			public readonly double FontSize;
			public readonly FontSlant FontSlant;
			public readonly FontWeight FontWeight;
			public readonly FontOptions FontOptions;
			public readonly Matrix CairoMatrix;
			public readonly Antialias Antialias;
			public readonly FillRule FillRule;
			public readonly Operator Operator;
			public readonly double Tolerance;

			public DirectSkiaState(SKPoint point, SKPaint paint, SKPath path, string family, double size, FontSlant slant, FontWeight weight, FontOptions options, Matrix matrix, Antialias aa, FillRule fr, Operator op, double tol)
			{
				Point = point;
				Paint = paint.Clone();
				Path = new SKPath(path);
				FontFamily = family;
				FontSize = size;
				FontSlant = slant;
				FontWeight = weight;
				FontOptions = options?.Copy() ?? new FontOptions();
				CairoMatrix = (Matrix)matrix.Clone();
				Antialias = aa;
				FillRule = fr;
				Operator = op;
				Tolerance = tol;
			}
		}
	}
}
