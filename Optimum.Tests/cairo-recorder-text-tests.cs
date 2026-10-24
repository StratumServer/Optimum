using System;
using Path = System.IO.Path;
using Directory = System.IO.Directory;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Cairo;
using Vintagestory.API.Config;
using Xunit;

/// <summary>
/// Compares recorded text with the native Cairo oracle, including hinted glyph outlines,
/// GUI font presets and scaled contexts. The fixed local-AA policy keeps flat pixels within
/// one level and filtered edge difference at most 96.
/// </summary>
[Collection ("Cairo materialization metrics")]
public sealed class CairoRecorderTextTests
{
	static CairoRecorderTextTests () { _ = CairoAPI.Version; CairoBundledFonts.Register (); }

	static Action<Context> Text (string family, FontWeight weight, double size, double x, double y, string text, Antialias antialias = Antialias.Gray, FontSlant slant = FontSlant.Normal)
	{
		return context => {
			context.SetSourceRGBA (.12, .18, .24, .9);
			context.Rectangle (0, 0, 400, 400);
			context.Fill ();
			context.SetFontSize (size);
			context.SelectFontFace (family, slant, weight);
			using (var options = new FontOptions { Antialias = antialias }) context.FontOptions = options;
			context.SetSourceRGBA (.94, .93, .89, 1);
			context.MoveTo (x, y);
			context.ShowText (text);
			context.SetSourceRGBA (1, .6, .2, .8);
			context.ShowText (" +stack 64"); // continues from Cairo's advanced current point
		};
	}




	static void AssertCurrentPointMatchesCairo (Context recorded, Context reference)
	{
		Assert.Equal (reference.HasCurrentPoint, recorded.HasCurrentPoint);
		PointD expected = reference.CurrentPoint, actual = recorded.CurrentPoint;
		Assert.Equal (expected.X, actual.X, 10);
		Assert.Equal (expected.Y, actual.Y, 10);
	}



















	static void OutlinedText (Context context, string text, double strokeWidth = 1)
	{
		var stroke = new Color (0, 0, 0, .8);
		var fill = new Color (.9, .8, .1, 1);
		context.TextPath (text);
		context.SetSourceColor (stroke);
		context.LineWidth = strokeWidth;
		context.StrokePreserve ();
		context.SetSourceColor (fill);
		context.Fill ();
	}


	[Theory]
	[InlineData (FontWeight.Normal, .75)]
	[InlineData (FontWeight.Bold, 1.0)]
	[InlineData (FontWeight.Bold, 1.25)]
	[InlineData (FontWeight.Bold, 1.5)]
	[InlineData (FontWeight.Bold, 2.0)]
	public void OutlinedTextSkiaPathMatchesCairoTextPathWithinLocalAntialiasPolicy (FontWeight weight, double strokeWidth)
	{
		CairoRecorderParityTests.AssertLocalAaParity (200, 40, context => {
			context.SelectFontFace ("sans-serif", FontSlant.Normal, weight);
			context.SetFontSize (18);
			using (var options = new FontOptions { Antialias = Antialias.Subpixel }) context.FontOptions = options;
			context.MoveTo (3.3, 26.2);
			OutlinedText (context, "Outline", strokeWidth);
		});
	}

	[Theory]
	[InlineData (1f, 1d, HintStyle.Default)]
	[InlineData (1.5f, 1d, HintStyle.Default)]
	[InlineData (2f, 1d, HintStyle.Default)]
	[InlineData (1f, 2d, HintStyle.Default)]
	[InlineData (1.5f, 1d, HintStyle.Slight)]
	[InlineData (2f, 1d, HintStyle.Medium)]
	[InlineData (2f, 1d, HintStyle.Full)]
	public void HintedTextPathRetainsCairoOutlineGeometry (float uiScale, double contextScale, HintStyle hintStyle)
	{
		if (!OperatingSystem.IsLinux ()) return;
		float previous = RuntimeEnv.GUIScale;
		try {
			RuntimeEnv.GUIScale = uiScale;
			using var font = Vintagestory.API.Client.CairoFont.WhiteSmallText ();
			using var native = new ImageSurface (Format.Argb32, 300, 100); _ = native.Handle;
			using var managed = new ImageSurface (Format.Argb32, 300, 100); managed.BeginRecording ();
			var paths = new System.Collections.Generic.List<double[]> ();
			foreach (var surface in new[] { native, managed }) {
				using var context = new Context (surface);
				context.Scale (contextScale, contextScale);
				font.SetupContext (context);
				using (var options = context.FontOptions) {
					options.HintStyle = hintStyle;
					context.FontOptions = options;
				}
				context.MoveTo (5, 5 + font.GetFontExtents ().Ascent);
				context.TextPath ("15 / 15");
				using var path = context.CopyPath ();
				paths.Add (ReadPathCoordinates (path));
			}
			Assert.Equal (paths[0].Length, paths[1].Length);
			for (int i = 0; i < paths[0].Length; i++)
				Assert.True (Math.Abs (paths[0][i] - paths[1][i]) <= 1d / 256,
					$"Outline coordinate {i}: Cairo={paths[0][i]}, Skia={paths[1][i]}");
		} finally { RuntimeEnv.GUIScale = previous; }
	}

	static double[] ReadPathCoordinates (Cairo.Path path)
	{
		int count = Marshal.ReadInt32 (path.Handle, IntPtr.Size == 8 ? 16 : 8);
		IntPtr data = Marshal.ReadIntPtr (path.Handle, IntPtr.Size == 8 ? 8 : 4);
		var values = new System.Collections.Generic.List<double> ();
		for (int i = 0; i < count;) {
			int op = Marshal.ReadInt32 (data, i * 16), length = Marshal.ReadInt32 (data, i * 16 + 4);
			values.Add (op);
			for (int j = 1; j < length; j++) {
				values.Add (BitConverter.Int64BitsToDouble (Marshal.ReadInt64 (data, (i + j) * 16)));
				values.Add (BitConverter.Int64BitsToDouble (Marshal.ReadInt64 (data, (i + j) * 16 + 8)));
			}
			i += length;
		}
		return values.ToArray ();
	}

	[Theory]
	[InlineData (1f, false)]
	[InlineData (1.5f, false)]
	[InlineData (2f, false)]
	[InlineData (1f, true)]
	[InlineData (1.5f, true)]
	[InlineData (2f, true)]
	public void OutlinedHudValuesMatchCairoAtEveryGuiScale (float scale, bool fpsText)
	{
		float previous = RuntimeEnv.GUIScale;
		try {
			RuntimeEnv.GUIScale = scale;
			using var font = (fpsText ? Vintagestory.API.Client.CairoFont.WhiteSmallishText () : Vintagestory.API.Client.CairoFont.WhiteSmallText ())
				.WithStroke (Vintagestory.API.MathTools.ColorUtil.BlackArgbDouble, fpsText ? 1.5 : 0.75);
			CairoRecorderParityTests.AssertLocalAaParity (fpsText ? 640 : 240, 70, context => {
				if (!fpsText) {
					context.SetSourceRGBA (Vintagestory.API.Client.GuiStyle.DialogStrongBgColor);
					context.Paint ();
				}
				font.SetupContext (context);
				context.MoveTo (fpsText ? 319 : 5, 5 + font.GetFontExtents ().Ascent);
				context.TextPath (fpsText ? "Avg FPS: 5, Min FPS: 59" : "15 / 15");
				context.LineWidth = font.StrokeWidth;
				context.SetSourceRGBA (font.StrokeColor);
				context.StrokePreserve ();
				context.SetSourceRGBA (font.Color);
				context.Fill ();
			});
		} finally { RuntimeEnv.GUIScale = previous; }
	}

	[Fact]
	public void TextPathAndFontStateDoNotMaterialize ()
	{
		CairoRecorderParityTests.AssertLocalAaParity (200, 40, context => {
			context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Bold);
			context.SetFontSize (18);
			_ = context.FontExtents;
			_ = context.TextExtents ("measure");
			context.MoveTo (3, 26);
			context.TextPath ("Outline");
			context.SetSourceRGBA (.9, .8, .1, 1);
			context.FillPreserve ();
			context.SetSourceRGBA (0, 0, 0, .8);
			context.LineWidth = 1;
			context.Stroke ();
		});
	}
}
