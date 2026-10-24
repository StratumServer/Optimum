using System;
using System.IO;
using System.Runtime.InteropServices;
using Cairo;
using SkiaSharp;
using Xunit;

[Collection ("GUI metrics")]
public sealed class CairoNativeFreeRecordedGuiTests
{
	[Theory]
	[InlineData (0)]
	[InlineData (1)]
	[InlineData (2)]
	[InlineData (3)]
	[InlineData (4)]
	[InlineData (5)]
	[InlineData (6)]
	[InlineData (7)]
	public void FormerParityGapsRenderWithCairoDenied (int scenario)
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		using var surface = ImageSurface.CreateForRecordedGui (Format.Argb32, 160, 80);
		using (var context = new Context (surface)) {
			context.SetSourceRGBA (.2, .3, .4, .7); context.Paint ();
			context.Operator = scenario == 0 ? Operator.Difference : scenario == 1 ? Operator.Exclusion : scenario < 4 ? Operator.Screen : Operator.Over;
			context.Antialias = scenario == 4 ? Antialias.None : scenario == 5 ? Antialias.Fast : Antialias.Gray;
			context.SetSourceRGBA (.8, .5, .1, .7);
			context.Arc (18, 18, 8.3, 0, 6.2); context.Fill ();
			if (scenario == 6 || scenario == 7) {
				context.LineWidth = scenario == 6 ? 2 : 2.25;
				context.LineCap = LineCap.Round;
				context.SetDash (scenario == 6 ? new[] { 0d, 4d } : new[] { 10d, .003 }, 0);
				context.MoveTo (2, 48); context.LineTo (82, 48); context.Stroke ();
			}
			if (scenario == 2 || scenario == 3) {
				context.SelectFontFace ("sans-serif", FontSlant.Normal, FontWeight.Normal); context.SetFontSize (14);
				using var options = new FontOptions { Antialias = scenario == 2 ? Antialias.Gray : Antialias.Subpixel };
				context.FontOptions = options;
				context.MoveTo (3.375, 70.625); context.ShowText ("Screen ffi 123");
			}
		}
		using var target = SKSurface.Create (new SKImageInfo (160, 80));
		Assert.True (surface.TryDrawRecordedCommands (target));
		using var image = target.Snapshot ();
		using var bitmap = SKBitmap.FromImage (image);
		Assert.Contains (bitmap.Pixels, pixel => pixel.Alpha > 0);
		AssertNoCairoNativeLoad (deniedBefore);
	}

	[Theory]
	[InlineData (1f)]
	[InlineData (1.5f)]
	[InlineData (2f)]
	public void HintedOutlinedHudTextNeedsNoNativeCairo (float scale)
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		float previous = Vintagestory.API.Config.RuntimeEnv.GUIScale;
		try {
			Vintagestory.API.Config.RuntimeEnv.GUIScale = scale;
			using var surface = new ImageSurface (Format.Argb32, 360, 80);
			surface.BeginRecording ();
			using (var context = new Context (surface)) {
				using var font = Vintagestory.API.Client.CairoFont.WhiteSmallishText ()
					.WithStroke (Vintagestory.API.MathTools.ColorUtil.BlackArgbDouble, 1.5);
				font.SetupContext (context);
				context.MoveTo (5, 5 + font.GetFontExtents ().Ascent);
				context.TextPath ("15 / 15 Avg FPS: 59");
				context.LineWidth = font.StrokeWidth;
				context.SetSourceRGBA (font.StrokeColor);
				context.StrokePreserve ();
				context.SetSourceRGBA (font.Color);
				context.Fill ();
			}
			using var target = SKSurface.Create (new SKImageInfo (360, 80));
			target.Canvas.Clear (SKColors.Transparent);
			Assert.True (surface.TryDrawRecordedCommands (target));
			using var image = target.Snapshot ();
			using var bitmap = SKBitmap.FromImage (image);
			int visible = 0;
			foreach (SKColor pixel in bitmap.Pixels) if (pixel.Alpha > 0) visible++;
			Assert.True (visible > 50, "The hinted outline must draw visible glyphs.");
			AssertNoCairoNativeLoad (deniedBefore);
		} finally { Vintagestory.API.Config.RuntimeEnv.GUIScale = previous; }
	}

	static void AssertNoCairoNativeLoad (int deniedBefore)
	{
		Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
		if (CairoNativeGuard.NativeAllowed) return;
		string[] mappings = CairoNativeGuard.LoadedLibcairoMappings ();
		if (mappings != null) Assert.Empty (mappings);
	}

	sealed class NativeSurfaceSubclass : Surface
	{
		internal NativeSurfaceSubclass () : base (new IntPtr (1), true) { }
	}

#pragma warning disable CS0612 // These tests intentionally exercise the obsolete subclass extension points.
	sealed class LegacyPatternSubclass : Pattern
	{
		internal LegacyPatternSubclass (IntPtr handle) : base () { pattern = handle; }
		internal void ClearHandle () { pattern = IntPtr.Zero; }
	}

	sealed class PatternModeSubclass : Pattern
	{
		internal PatternModeSubclass (bool managedOnly) : base (managedOnly) { }
	}

	sealed class ManagedPatternWithRawHandle : Pattern
	{
		internal ManagedPatternWithRawHandle (IntPtr handle) : base (true) { pattern = handle; }
		internal void ClearHandle () { pattern = IntPtr.Zero; }
	}

	sealed class ManagedSolidPatternWithRawHandle : SolidPattern
	{
		internal ManagedSolidPatternWithRawHandle (IntPtr handle) : base (new Color (.1, .2, .3, .4)) { pattern = handle; }
		internal void ClearHandle () { pattern = IntPtr.Zero; }
	}

	sealed class LegacyGradientSubclass : Gradient
	{
		internal LegacyGradientSubclass () : base () { }
	}

	sealed class GradientModeSubclass : Gradient
	{
		internal GradientModeSubclass (bool managedOnly) : base (managedOnly) { }
	}

	sealed class ManagedGradientWithRawHandle : Gradient
	{
		internal ManagedGradientWithRawHandle (IntPtr handle) : base (true) { pattern = handle; }
		internal void ClearHandle () { pattern = IntPtr.Zero; }
	}

	sealed class ManagedSurfacePatternWithRawHandle : SurfacePattern
	{
		internal ManagedSurfacePatternWithRawHandle (Surface source, IntPtr handle) : base (source) { pattern = handle; }
		internal void ClearHandle () { pattern = IntPtr.Zero; }
	}

	sealed class NativeGradientSubclass : Gradient
	{
		internal NativeGradientSubclass (IntPtr handle, bool owned) : base (handle, owned) { }
	}
#pragma warning restore CS0612

	[Fact]
	public void DefaultManagedImageContextAndMetadataNeedNoNativeCairo()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try
		{
			Surface.ConfigureAutomaticRecording (false);
			using var surface = new ImageSurface (Format.Argb32, 8, 6);
			Assert.Equal (SurfaceRecordingState.Recording, surface.RecordingState);
			Assert.Equal (Status.Success, surface.Status);
			Assert.Equal (Content.ColorAlpha, surface.Content);
			Assert.Equal (SurfaceType.Image, surface.SurfaceType);
			Assert.Equal (1u, surface.ReferenceCount);

			Marshal.WriteInt32 (surface.DataPtr, 0, unchecked ((int)0xffff0000));
			surface.MarkDirty ();
			surface.MarkDirty (new Rectangle (0, 0, 1, 1));
			using var source = new SolidPattern (0, 0, 1, 1);
			using (var context = new Context (surface)) {
				context.SetSource (source);
				context.Rectangle (2, 1, 4, 3);
				context.Fill ();
				Assert.Equal (Status.Success, context.Status);
			}
			using var bitmap = new SKBitmap (new SKImageInfo (8, 6, SKColorType.Bgra8888, SKAlphaType.Premul));
			using (var canvas = new SKCanvas (bitmap)) Assert.True (surface.TryDrawRecordedCommands (canvas));
			Assert.Equal (SKColors.Red, bitmap.GetPixel (0, 0));
			Assert.Equal (SKColors.Blue, bitmap.GetPixel (3, 2));
			Assert.Throws<NotSupportedException> (() => _ = surface.Handle);
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			AssertNoCairoNativeLoad (deniedBefore);
			surface.Flush ();
			byte[] data = surface.Data;
			Assert.Equal (0xff, data[2]);
			Assert.Equal (0xff, data[3]);
		}
		finally
		{
			Surface.ConfigureAutomaticRecording (automaticRecording);
		}
	}

	[Fact]
	public void SetTargetSwitchesManagedSurfacesWithoutMaterializingCairo()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			Surface.ConfigureAutomaticRecording (false);
			using var first = new ImageSurface (Format.Argb32, 4, 2);
			using var second = new ImageSurface (Format.Argb32, 4, 2);
			using (var previousTargetContext = new Context (second)) {
				previousTargetContext.Rectangle (0, 0, 1, 2);
				previousTargetContext.Clip ();
				previousTargetContext.SetSourceRGBA (0, 1, 0, 1);
				previousTargetContext.Paint ();
			}
			using var context = new Context (first);
			context.SetSourceRGBA (1, 0, 0, 1);
			context.Rectangle (0, 0, 2, 2);
			context.Fill ();

			context.SetTarget (second);
			Assert.Equal (Status.Success, context.Status);
			context.SetSourceRGBA (0, 0, 1, 1);
			context.Rectangle (2, 0, 2, 2);
			context.Fill ();

			byte[] firstPixels = first.Data;
			byte[] secondPixels = second.Data;
			Assert.Equal (0xff, firstPixels[2]);
			Assert.Equal (0xff, firstPixels[3]);
			Assert.Equal (0xff, secondPixels[1]);
			Assert.Equal (0xff, secondPixels[3]);
			Assert.Equal (0xff, secondPixels[4 * 2 + 0]);
			Assert.Equal (0xff, secondPixels[4 * 2 + 3]);
			Assert.Equal (0, secondPixels[0]);
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			AssertNoCairoNativeLoad (deniedBefore);
		}
		finally {
			Surface.ConfigureAutomaticRecording (automaticRecording);
		}
	}

	[Fact]
	public void SetTargetDiscardsManagedGroupsAndRetainsTheirSurfaces()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			Surface.ConfigureAutomaticRecording (false);
			using var first = new ImageSurface (Format.Argb32, 4, 2);
			using var second = new ImageSurface (Format.Argb32, 4, 2);
			using var context = new Context (first);
			context.SetSourceRGBA (1, 0, 0, 1);
			context.Rectangle (0, 0, 1, 2);
			context.Fill ();

			context.PushGroup (Content.ColorAlpha);
			context.SetSourceRGBA (0, 1, 0, 1);
			context.Rectangle (0, 0, 1, 2);
			context.Fill ();
			using Surface outerGroupTarget = context.GetGroupTarget ();
			context.PushGroup (Content.ColorAlpha);
			context.SetSourceRGBA (0, 1, 0, 1);
			context.Rectangle (1, 0, 1, 2);
			context.Fill ();
			using Surface groupTarget = context.GetGroupTarget ();
			context.SetTarget (second);
			Assert.Equal (Status.Success, context.Status);
			context.SetSourceRGBA (0, 0, 1, 1);
			context.Rectangle (2, 0, 2, 2);
			context.Fill ();

			AssertPixel (first, 0, 0, SKColors.Red);
			AssertPixel (second, 2, 0, SKColors.Blue);
			AssertPixel (outerGroupTarget, 0, 0, SKColors.Lime);
			AssertPixel (groupTarget, 1, 0, SKColors.Lime);
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			AssertNoCairoNativeLoad (deniedBefore);
		} finally {
			Surface.ConfigureAutomaticRecording (automaticRecording);
		}
	}

	[Fact]
	public void SetSourceSurfaceCanSnapshotItsManagedTargetWithoutCairo ()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			Surface.ConfigureAutomaticRecording (false);
			using var surface = new ImageSurface (Format.Argb32, 4, 2);
			using var context = new Context (surface);
			context.SetSourceRGBA (1, 0, 0, 1);
			context.Rectangle (0, 0, 1, 2);
			context.Fill ();

			context.SetSourceSurface (surface, 1, 0);
			context.Rectangle (1, 0, 1, 2);
			context.Fill ();
			context.SetSource (surface, 2.0, 0.0);
			context.Rectangle (2, 0, 1, 2);
			context.Fill ();
			context.SetSource (surface);
			context.Paint ();

			using var bitmap = new SKBitmap (4, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
			using (var canvas = new SKCanvas (bitmap)) Assert.True (surface.TryDrawRecordedCommands (canvas));
			Assert.Equal (SKColors.Red, bitmap.GetPixel (0, 0));
			Assert.Equal (SKColors.Red, bitmap.GetPixel (1, 0));
			Assert.Equal (SKColors.Red, bitmap.GetPixel (2, 0));
			Assert.Equal (SKColor.Empty, bitmap.GetPixel (3, 0));
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			AssertNoCairoNativeLoad (deniedBefore);
		} finally {
			Surface.ConfigureAutomaticRecording (automaticRecording);
		}
	}

	[Fact]
	public void ManagedA8A1AndRgb565SurfacePatternsRenderWithoutCairo ()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		using var alpha = new ImageSurface (Format.A8, 1, 1);
		using (var context = new Context (alpha)) {
			context.SetSourceRGBA (0, 0, 0, .5);
			context.Paint ();
		}
		using var oneBit = new ImageSurface (Format.A1, 1, 1);
		using (var context = new Context (oneBit)) {
			context.SetSourceRGBA (0, 0, 0, 1);
			context.Paint ();
		}
		Assert.Equal (1, oneBit.Data[0]);
		using var rgb565 = new ImageSurface (Format.Rgb16565, 1, 1);
		using (var context = new Context (rgb565)) {
			context.SetSourceRGBA (0, 0, 1, 1);
			context.Paint ();
		}

		using var target = new ImageSurface (Format.Argb32, 3, 1);
		using (var context = new Context (target)) {
			using (var pattern = new SurfacePattern (alpha) { Extend = Extend.Repeat, Filter = Filter.Nearest }) {
				context.SetSource (pattern);
				context.Rectangle (0, 0, 1, 1);
				context.Fill ();
			}
			using (var pattern = new SurfacePattern (oneBit) { Extend = Extend.Repeat, Filter = Filter.Nearest }) {
				context.SetSource (pattern);
				context.Rectangle (1, 0, 1, 1);
				context.Fill ();
			}
			using (var pattern = new SurfacePattern (rgb565) { Extend = Extend.Repeat, Filter = Filter.Nearest }) {
				context.SetSource (pattern);
				context.Rectangle (2, 0, 1, 1);
				context.Fill ();
			}
		}

		using var bitmap = new SKBitmap (3, 1, SKColorType.Bgra8888, SKAlphaType.Premul);
		using (var canvas = new SKCanvas (bitmap)) Assert.True (target.TryDrawRecordedCommands (canvas));
		SKColor alphaPixel = bitmap.GetPixel (0, 0);
		Assert.Equal (0, alphaPixel.Red);
		Assert.Equal (0, alphaPixel.Green);
		Assert.Equal (0, alphaPixel.Blue);
		Assert.InRange ((int)alphaPixel.Alpha, 126, 129);
		Assert.Equal (new SKColor (0, 0, 0, 255), bitmap.GetPixel (1, 0));
		Assert.Equal (SKColors.Blue, bitmap.GetPixel (2, 0));
		Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
		AssertNoCairoNativeLoad (deniedBefore);
	}

	static void AssertPixel (Surface surface, int x, int y, SKColor expected)
	{
		using var bitmap = new SKBitmap (new SKImageInfo (surface is ImageSurface image ? image.Width : 4, surface is ImageSurface target ? target.Height : 2, SKColorType.Bgra8888, SKAlphaType.Premul));
		using (var canvas = new SKCanvas (bitmap)) Assert.True (surface.TryDrawRecordedCommands (canvas));
		Assert.Equal (expected, bitmap.GetPixel (x, y));
	}

	[Fact]
	public void ManagedDashStateRendersWithoutMaterializingCairo()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			Surface.ConfigureAutomaticRecording (false);
			using var surface = new ImageSurface (Format.Argb32, 12, 4);
			using (var context = new Context (surface)) {
				context.Antialias = Antialias.None;
				context.LineWidth = 1;
				context.SetDash (new[] { 2.0, 2.0 }, 0);
				context.SetSourceRGBA (1, 1, 1, 1);
				context.MoveTo (0, 1.5);
				context.LineTo (12, 1.5);
				context.Stroke ();
			}

			byte[] pixels = surface.Data;
			int stride = surface.Stride;
			Assert.Equal (0xff, pixels[stride + 3]);
			Assert.Equal (0, pixels[stride + 2 * 4 + 3]);
			Assert.Equal (0xff, pixels[stride + 4 * 4 + 3]);
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			AssertNoCairoNativeLoad (deniedBefore);
		}
		finally {
			Surface.ConfigureAutomaticRecording (automaticRecording);
		}
	}

	[Fact]
	public void ManagedImageSurfaceCanWrapAndFlushAnUnmanagedPixelBuffer()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		IntPtr pixels = Marshal.AllocHGlobal (8);
		try {
			Surface.ConfigureAutomaticRecording (true);
			Marshal.WriteInt32 (pixels, 0, unchecked ((int)0xffff0000));
			using (var surface = new ImageSurface (pixels, Format.Argb32, 2, 1, 8)) {
				using (var context = new Context (surface)) {
					context.SetSourceRGBA (0, 0, 1, 1);
					context.Rectangle (1, 0, 1, 1);
					context.Fill ();
				}
				surface.Flush ();
				Assert.Equal (0xffff0000u, unchecked ((uint)Marshal.ReadInt32 (pixels, 0)));
				Assert.Equal (0xff0000ffu, unchecked ((uint)Marshal.ReadInt32 (pixels, 4)));
				Marshal.WriteInt32 (pixels, 0, unchecked ((int)0xff00ff00));
				surface.MarkDirty ();
				Assert.Equal (0xff00ff00u, BitConverter.ToUInt32 (surface.Data, 0));
				Assert.Equal (0xff0000ffu, BitConverter.ToUInt32 (surface.Data, 4));
			}
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			if (!CairoNativeGuard.NativeAllowed) AssertNoCairoNativeLoad (deniedBefore);
		} finally {
			Surface.ConfigureAutomaticRecording (automaticRecording);
			Marshal.FreeHGlobal (pixels);
		}
	}

	[Fact]
	public void ManagedDataPointerCheckpointsPendingCommandsAndKeepsContextState()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			Surface.ConfigureAutomaticRecording (true);
			using var surface = new ImageSurface (Format.Argb32, 4, 1);
			using var context = new Context (surface);
			context.SetSourceRGBA (1, 0, 0, 1);
			context.Rectangle (0, 0, 1, 1);
			context.Fill ();

			IntPtr pixels = surface.DataPtr;
			Assert.Equal (0xffff0000u, unchecked ((uint)Marshal.ReadInt32 (pixels, 0)));
			Marshal.WriteInt32 (pixels, 12, unchecked ((int)0xff0000ff));
			surface.MarkDirty (new Rectangle (3, 0, 1, 1));

			context.SetSourceRGBA (0, 1, 0, 1);
			context.Rectangle (1, 0, 1, 1);
			context.Fill ();
			byte[] data = surface.Data;
			Assert.Equal (0xffff0000u, BitConverter.ToUInt32 (data, 0));
			Assert.Equal (0xff00ff00u, BitConverter.ToUInt32 (data, 4));
			Assert.Equal (0xff0000ffu, BitConverter.ToUInt32 (data, 12));
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			if (!CairoNativeGuard.NativeAllowed) AssertNoCairoNativeLoad (deniedBefore);
		} finally {
			Surface.ConfigureAutomaticRecording (automaticRecording);
		}
	}

	[Fact]
	public void DefaultManagedModeRejectsNativeOnlySurfaceApisAndSupportsManagedFonts()
	{
		// This test verifies product defaults, not the deny resolver's instrumentation or explicit allow mode.
		if (CairoNativeGuard.Mode == "deny" || CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		string postscript = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-managed-default-" + Guid.NewGuid ().ToString ("N") + ".ps");
		try { using (new PSSurface (postscript, 10, 10)) { } }
		finally { if (File.Exists (postscript)) File.Delete (postscript); }
		Assert.Throws<NotSupportedException> (() => new Win32Surface (IntPtr.Zero));
		Assert.Throws<NotSupportedException> (() => new XlibSurface (IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, 1));
		Assert.Throws<NotSupportedException> (() => new XcbSurface (IntPtr.Zero, 0, IntPtr.Zero, 1, 1));
		Assert.Throws<NotSupportedException> (() => new GlitzSurface (IntPtr.Zero));
		Assert.Throws<NotSupportedException> (() => new DirectFBSurface (IntPtr.Zero, IntPtr.Zero));
		Assert.Throws<NotSupportedException> (() => new Context (new IntPtr (1), true));
		Assert.Throws<NotSupportedException> (() => new NativeSurfaceSubclass ());
		IntPtr externalPixels = Marshal.AllocHGlobal (4);
		try {
			using var externalSurface = new ImageSurface (externalPixels, Format.Argb32, 1, 1, 4);
			Assert.Equal (externalPixels, externalSurface.DataPtr);
		} finally { Marshal.FreeHGlobal (externalPixels); }
		Assert.Throws<NotSupportedException> (() => new FontFace (new IntPtr (1), true));
		using (var options = new FontOptions ())
			Assert.Throws<ArgumentNullException> (() => new ScaledFont (null, new Matrix (), new Matrix (), options));
		Assert.Throws<NotSupportedException> (() => Surface.Lookup (new IntPtr (1), true));
		Assert.Throws<NotSupportedException> (() => Pattern.Lookup (new IntPtr (1), true));
		using var surface = new ImageSurface (Format.Argb32, 4, 4);
		using var context = new Context (surface);
		using (FontFace face = context.GetContextFontFace ()) {
			Assert.Equal ("Sans", face.Family);
			Assert.Throws<NotSupportedException> (() => _ = face.Handle);
		}
		using (ScaledFont scaled = context.GetScaledFont ()) {
			Assert.Equal (Status.Success, scaled.Status);
			Assert.Throws<NotSupportedException> (() => _ = scaled.Handle);
		}
		using (Cairo.Path path = context.CopyPath ()) Assert.NotNull (path);
		context.LineWidth = 2;
		Assert.Equal (2, context.LineWidth);
		Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
		AssertNoCairoNativeLoad (deniedBefore);
	}

	[Fact]
	public void ManagedSvgSurfaceWritesVectorOutputWithoutCairo()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		string filename = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-managed-svg-" + Guid.NewGuid ().ToString ("N") + ".svg");
		try {
			using (var surface = new SvgSurface (filename, 48, 32)) {
				Assert.Equal (SurfaceType.Svg, surface.SurfaceType);
				Assert.Equal (Content.ColorAlpha, surface.Content);
				using (var context = new Context (surface)) {
					context.SetSourceRGBA (.15, .55, .9, 1);
					context.Rectangle (4, 5, 20, 12);
					context.Fill ();
					context.MoveTo (2, 28);
					context.LineTo (40, 28);
					context.LineWidth = 2;
					context.Stroke ();
					context.ShowPage ();
				}
				Assert.Equal (Status.Success, surface.Finish ());
			}
			string svg = File.ReadAllText (filename);
			Assert.Contains ("<svg", svg, StringComparison.OrdinalIgnoreCase);
			Assert.Contains ("<path", svg, StringComparison.OrdinalIgnoreCase);
			Assert.DoesNotContain ("<image", svg, StringComparison.OrdinalIgnoreCase);
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			AssertNoCairoNativeLoad (deniedBefore);
		} finally { if (File.Exists (filename)) File.Delete (filename); }
	}

	[Fact]
	public void ManagedSvgSurfaceFinalizesOnDisposeWithoutCairo()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		string filename = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-managed-svg-dispose-" + Guid.NewGuid ().ToString ("N") + ".svg");
		try {
			using (var surface = new SvgSurface (filename, 12, 8))
			using (var context = new Context (surface)) {
				context.SetSourceRGBA (0, .7, .2, 1);
				context.Rectangle (1, 1, 8, 5);
				context.Fill ();
			}
			Assert.Contains ("<svg", File.ReadAllText (filename), StringComparison.OrdinalIgnoreCase);
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			AssertNoCairoNativeLoad (deniedBefore);
		} finally { if (File.Exists (filename)) File.Delete (filename); }
	}

	[Fact]
	public void ManagedPdfSurfaceWritesVectorOutputWithoutCairo()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		string filename = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-managed-pdf-" + Guid.NewGuid ().ToString ("N") + ".pdf");
		try {
			using (var surface = new PdfSurface (filename, 48, 32)) {
				Assert.Equal (SurfaceType.Pdf, surface.SurfaceType);
				Assert.Equal (Content.ColorAlpha, surface.Content);
				surface.SetSize (64, 48);
				using (var context = new Context (surface)) {
					context.SetSourceRGBA (.8, .25, .1, 1);
					context.Rectangle (4, 5, 20, 12);
					context.Fill ();
					context.ShowPage ();
					context.SetSourceRGBA (.1, .7, .3, 1);
					context.Rectangle (8, 9, 12, 10);
					context.Fill ();
					context.CopyPage ();
				}
				Assert.Equal (Status.Success, surface.Finish ());
			}
			byte[] pdf = File.ReadAllBytes (filename);
			Assert.StartsWith ("%PDF-", System.Text.Encoding.ASCII.GetString (pdf, 0, Math.Min (5, pdf.Length)));
			string pdfText = System.Text.Encoding.ASCII.GetString (pdf);
			Assert.Contains ("/MediaBox", pdfText);
			Assert.Matches (@"/MediaBox\s*\[\s*0(?:\.0+)?\s+0(?:\.0+)?\s+64(?:\.0+)?\s+48(?:\.0+)?\s*\]", pdfText);
			Assert.DoesNotContain ("/Subtype /Image", pdfText);
			Assert.Equal (3, System.Text.RegularExpressions.Regex.Matches (pdfText, @"/Type\s*/Page(?!s)\b").Count);
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			AssertNoCairoNativeLoad (deniedBefore);
		} finally { if (File.Exists (filename)) File.Delete (filename); }
	}

	[Fact]
	public void ManagedPostScriptSurfaceWritesRasterPagesAndDscCommentsWithoutCairo()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		string filename = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-managed-ps-" + Guid.NewGuid ().ToString ("N") + ".ps");
		try {
			using (var surface = new PSSurface (filename, 48, 32)) {
				Assert.Equal (SurfaceType.PS, surface.SurfaceType);
				Assert.Equal (Content.ColorAlpha, surface.Content);
				surface.BeginSetup ();
				surface.DscComment ("%%DocumentData: Clean7Bit");
				surface.SetSize (64, 48);
				Assert.Equal (64, surface.RecordedWidth);
				Assert.Equal (48, surface.RecordedHeight);
				surface.BeginPageSetup ();
				surface.DscComment ("%%PageOrientation: Portrait");
				using (var context = new Context (surface)) {
					context.SetSourceRGBA (.8, .25, .1, 1);
					context.Rectangle (4, 5, 20, 12);
					context.Fill ();
					context.ShowPage ();
					context.SetSourceRGBA (.1, .7, .3, 1);
					context.Rectangle (8, 9, 12, 10);
					context.Fill ();
					context.CopyPage ();
				}
				Assert.Equal (Status.Success, surface.Finish ());
			}
			string ps = File.ReadAllText (filename, System.Text.Encoding.ASCII);
			Assert.StartsWith ("%!PS-Adobe-3.0", ps, StringComparison.Ordinal);
			Assert.Contains ("%%LanguageLevel: 2", ps, StringComparison.Ordinal);
			Assert.Contains ("%%Pages: 3", ps, StringComparison.Ordinal);
			Assert.Contains ("%%DocumentData: Clean7Bit", ps, StringComparison.Ordinal);
			Assert.Contains ("%%PageOrientation: Portrait", ps, StringComparison.Ordinal);
			Assert.Contains ("/PageSize [64 48]", ps, StringComparison.Ordinal);
			Assert.Equal (3, System.Text.RegularExpressions.Regex.Matches (ps, @"(?m)^%%Page: ").Count);
			Assert.Contains ("colorimage", ps, StringComparison.Ordinal);
			Assert.Contains ("/ASCII85Decode filter /FlateDecode filter", ps, StringComparison.Ordinal);
			Assert.Contains ("64 48", ps, StringComparison.Ordinal);
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			AssertNoCairoNativeLoad (deniedBefore);
		} finally { if (File.Exists (filename)) File.Delete (filename); }
	}

	[Fact]
	public void CustomPatternAndGradientNativeConstructorsRejectBeforeCairoWithoutAllow()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		Assert.Throws<NotSupportedException> (() => new LegacyPatternSubclass (new IntPtr (1)));
		Assert.Throws<NotSupportedException> (() => new PatternModeSubclass (false));
		Assert.Throws<NotSupportedException> (() => new LegacyGradientSubclass ());
		Assert.Throws<NotSupportedException> (() => new GradientModeSubclass (false));
		Assert.Throws<NotSupportedException> (() => new NativeGradientSubclass (new IntPtr (1), true));
		AssertNoCairoNativeLoad (deniedBefore);
	}

	[Fact]
	public void ManagedPatternSubclassesWithRawHandlesRejectOperationsAndFinalizeWithoutCairo()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;

		var pattern = new ManagedPatternWithRawHandle (new IntPtr (1));
		Assert.Throws<NotSupportedException> (() => _ = pattern.Status);
		Assert.Throws<NotSupportedException> (() => _ = pattern.Extend);
		Assert.Throws<NotSupportedException> (() => pattern.Extend = Extend.Repeat);
		Assert.Throws<NotSupportedException> (() => _ = pattern.Matrix);
		Assert.Throws<NotSupportedException> (() => pattern.Matrix = new Matrix ());
		Assert.Throws<NotSupportedException> (() => _ = pattern.PatternType);
		Assert.Throws<NotSupportedException> (() => _ = pattern.Handle);
		Assert.Throws<NotSupportedException> (pattern.Dispose);
		pattern.ClearHandle ();
		pattern.Dispose ();

		var solid = new ManagedSolidPatternWithRawHandle (new IntPtr (1));
		Assert.Throws<NotSupportedException> (() => _ = solid.Color);
		Assert.Throws<NotSupportedException> (() => _ = solid.Status);
		Assert.Throws<NotSupportedException> (solid.Dispose);
		solid.ClearHandle ();
		solid.Dispose ();

		var gradient = new ManagedGradientWithRawHandle (new IntPtr (1));
		Assert.Throws<NotSupportedException> (() => _ = gradient.ColorStopCount);
		Assert.Throws<NotSupportedException> (() => gradient.AddColorStop (0, new Color (1, 0, 0, 1)));
		Assert.Throws<NotSupportedException> (() => gradient.AddColorStopRgb (0, new Color (1, 0, 0, 1)));
		Assert.Throws<NotSupportedException> (() => _ = gradient.Extend);
		Assert.Throws<NotSupportedException> (() => _ = gradient.Matrix);
		Assert.Throws<NotSupportedException> (() => _ = gradient.PatternType);
		Assert.Throws<NotSupportedException> (gradient.Dispose);
		gradient.ClearHandle ();
		gradient.Dispose ();

		using var surface = new ImageSurface (Format.Argb32, 2, 2);
		var surfacePattern = new ManagedSurfacePatternWithRawHandle (surface, new IntPtr (1));
		Assert.Throws<NotSupportedException> (() => _ = surfacePattern.Filter);
		Assert.Throws<NotSupportedException> (() => surfacePattern.Filter = Filter.Nearest);
		Assert.Throws<NotSupportedException> (surfacePattern.Dispose);
		surfacePattern.ClearHandle ();
		surfacePattern.Dispose ();

		CreateTransientManagedPatternWithRawHandle ();
		GC.Collect ();
		GC.WaitForPendingFinalizers ();
		GC.Collect ();
		AssertNoCairoNativeLoad (deniedBefore);
	}

	static void CreateTransientManagedPatternWithRawHandle ()
	{
		_ = new ManagedPatternWithRawHandle (new IntPtr (1));
	}

	[Fact]
	public void BuiltInPatternTypesConstructAcrossManagedAndNativePolicies()
	{
		using var surface = new ImageSurface (Format.Argb32, 4, 4);
		using var solid = new SolidPattern (.2, .4, .6, .8);
		using var gradient = new LinearGradient (0, 0, 4, 4);
		using var surfacePattern = new SurfacePattern (surface);
		Assert.Equal (PatternType.Solid, solid.PatternType);
		Assert.Equal (PatternType.Linear, gradient.PatternType);
		Assert.Equal (PatternType.Surface, surfacePattern.PatternType);
		Assert.Equal (Status.Success, solid.Status);
		Assert.Equal (Status.Success, gradient.AddColorStop (0, new Color (1, 0, 0, 1)));
		Assert.Equal (Status.Success, surfacePattern.Status);

		if (!CairoNativeGuard.NativeAllowed) {
			using var managedPatternSubclass = new PatternModeSubclass (true);
			using var managedGradientSubclass = new GradientModeSubclass (true);
		}
	}

	[Fact]
	public void CustomPatternAndGradientNativeConstructorsRemainAvailableWithAllow()
	{
		if (!CairoNativeGuard.NativeAllowed) return;
		using var solid = new SolidPattern (.2, .4, .6, .8);
		var customPattern = new LegacyPatternSubclass (solid.Handle);
		Assert.Equal (PatternType.Solid, customPattern.PatternType);
		customPattern.ClearHandle ();
		customPattern.Dispose ();
		using var nativeModePattern = new PatternModeSubclass (false);

		using var gradient = new LinearGradient (0, 0, 4, 4);
		using var customGradient = new NativeGradientSubclass (gradient.Handle, false);
		Assert.Equal (Status.Success, customGradient.Status);
		using var legacyGradient = new LegacyGradientSubclass ();
		using var nativeModeGradient = new GradientModeSubclass (false);
	}

	[Fact]
	public void ManagedContextQueriesAndGroupsWorkWithoutCairo()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		using var surface = new ImageSurface (Format.Argb32, 32, 24);
		using var context = new Context (surface);
		Assert.Equal (1u, surface.ReferenceCount);
		Assert.Equal (1u, context.ReferenceCount);
		context.SelectFontFace ("sans", FontSlant.Normal, FontWeight.Normal);
		context.SetFontSize (12);
		Assert.True (context.FontExtents.Height > 0);
		Assert.True (context.TextExtents ("native free").XAdvance > 0);

		context.MoveTo (3, 4);
		Assert.True (context.HasCurrentPoint);
		PointD current = context.CurrentPoint;
		Assert.Equal (3, current.X, 5);
		Assert.Equal (4, current.Y, 5);
		context.Translate (5, 7);
		double x = 2, y = 3;
		context.UserToDevice (ref x, ref y);
		Assert.Equal (7, x, 5);
		Assert.Equal (10, y, 5);
		context.DeviceToUser (ref x, ref y);
		Assert.Equal (2, x, 5);
		Assert.Equal (3, y, 5);
		context.Rectangle (0, 0, 10, 10);
		Assert.True (context.InFill (1, 1));
		Assert.True (context.FillExtents ().Width > 0);
		using (Pattern source = context.GetSource ()) Assert.NotNull (source);
		using (var target = context.GetTarget ()) {
			Assert.IsType<ImageSurface> (target);
			Assert.Equal (surface.Data, ((ImageSurface)target).Data);
			using var targetContext = new Context (target);
			targetContext.LineWidth = 5;
			Assert.Equal (5, targetContext.LineWidth);
			Assert.Equal (5, context.LineWidth);
			targetContext.Rectangle (12, 12, 2, 2);
			targetContext.Fill ();
		}
		using (var bitmap = new SKBitmap (new SKImageInfo (32, 24, SKColorType.Bgra8888, SKAlphaType.Premul)))
		using (var canvas = new SKCanvas (bitmap)) {
			Assert.True (surface.TryDrawRecordedCommands (canvas));
			Assert.Equal (new SKColor (0, 0, 0, 255), bitmap.GetPixel (12, 12));
		}

		using (ScaledFont scaled = context.GetScaledFont ()) {
			Assert.Equal (Status.Success, scaled.Status);
			Assert.Throws<NotSupportedException> (() => _ = scaled.Handle);
		}
		Assert.Throws<ArgumentNullException> (() => context.SetScaledFont (null));
		using (FontFace face = context.GetContextFontFace ()) {
			Assert.Equal ("sans", face.Family);
			Assert.Throws<NotSupportedException> (() => _ = face.Handle);
		}
		using var saturateSurface = new ImageSurface (Format.Argb32, 32, 24);
		using (var saturateContext = new Context (saturateSurface)) {
			saturateContext.SetSourceRGBA (0, 0, 0, 1);
			saturateContext.Rectangle (12, 12, 2, 2);
			saturateContext.Fill ();
			saturateContext.Operator = Operator.Saturate;
			saturateContext.SetSourceRGBA (1, 0, 0, .75);
			saturateContext.Paint ();
		}
		using (var bitmap = new SKBitmap (new SKImageInfo (32, 24, SKColorType.Bgra8888, SKAlphaType.Premul)))
		using (var canvas = new SKCanvas (bitmap)) {
			Assert.True (saturateSurface.TryDrawRecordedCommands (canvas));
			Assert.Equal (new SKColor (255, 0, 0, 191), bitmap.GetPixel (0, 0));
			Assert.Equal (new SKColor (0, 0, 0, 255), bitmap.GetPixel (12, 12));
		}
		Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
		AssertNoCairoNativeLoad (deniedBefore);
	}

	[Fact]
	public void ManagedMaskSurfaceUsesSkiaAlphaWithoutLoadingCairo()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		using var target = new ImageSurface (Format.Argb32, 8, 4);
		using var mask = new ImageSurface (Format.A8, 8, 4);
		byte[] alpha = new byte[mask.Height * mask.Stride];
		for (int y = 0; y < mask.Height; y++) for (int x = 0; x < 4; x++) alpha[y * mask.Stride + x] = 255;
		Marshal.Copy (alpha, 0, mask.DataPtr, alpha.Length);
		mask.MarkDirty ();
		using (var context = new Context (target)) {
			context.SetSourceRGBA (1, 0, 0, 1);
			context.Rectangle (0, 0, 8, 4);
			context.MaskSurface (mask, 0, 0);
		}
		using var bitmap = new SKBitmap (new SKImageInfo (8, 4, SKColorType.Bgra8888, SKAlphaType.Premul));
		using var canvas = new SKCanvas (bitmap);
		Assert.True (target.TryDrawRecordedCommands (canvas));
		Assert.Equal (new SKColor (255, 0, 0, 255), bitmap.GetPixel (1, 1));
		Assert.Equal (new SKColor (0, 0, 0, 0), bitmap.GetPixel (6, 1));
		AssertNoCairoNativeLoad (deniedBefore);
	}

	[Fact]
	public void ManagedGradientMaskUsesSkiaWithoutLoadingCairo()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		using var target = new ImageSurface (Format.Argb32, 16, 4);
		using var mask = new LinearGradient (0, 0, 16, 0);
		mask.AddColorStop (0, new Color (1, 1, 1, 0));
		mask.AddColorStop (1, new Color (1, 1, 1, 1));
		using (var context = new Context (target)) {
			context.SetSourceRGBA (0, 1, 0, 1);
			context.Rectangle (0, 0, 16, 4);
			context.Mask (mask);
		}
		using var bitmap = new SKBitmap (new SKImageInfo (16, 4, SKColorType.Bgra8888, SKAlphaType.Premul));
		using var canvas = new SKCanvas (bitmap);
		Assert.True (target.TryDrawRecordedCommands (canvas));
		Assert.True (bitmap.GetPixel (1, 2).Alpha < bitmap.GetPixel (14, 2).Alpha);
		Assert.True (bitmap.GetPixel (14, 2).Green > bitmap.GetPixel (14, 2).Red);
		AssertNoCairoNativeLoad (deniedBefore);
	}

	[Fact]
	public void ManagedSurfacePatternMaskUsesSkiaWithoutLoadingCairo()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		using var target = new ImageSurface (Format.Argb32, 8, 4);
		using var mask = new ImageSurface (Format.Argb32, 8, 4);
		byte[] pixels = new byte[mask.Height * mask.Stride];
		for (int y = 0; y < mask.Height; y++) for (int x = 4; x < mask.Width; x++) pixels[y * mask.Stride + x * 4 + 3] = 255;
		Marshal.Copy (pixels, 0, mask.DataPtr, pixels.Length);
		mask.MarkDirty ();
		using var pattern = new SurfacePattern (mask);
		using (var context = new Context (target)) {
			context.SetSourceRGBA (1, 0, 0, 1);
			context.Rectangle (0, 0, 8, 4);
			context.Mask (pattern);
		}
		using var bitmap = new SKBitmap (new SKImageInfo (8, 4, SKColorType.Bgra8888, SKAlphaType.Premul));
		using var canvas = new SKCanvas (bitmap);
		Assert.True (target.TryDrawRecordedCommands (canvas));
		Assert.Equal ((byte)0, bitmap.GetPixel (1, 1).Alpha);
		Assert.True (bitmap.GetPixel (6, 1).Alpha > 240);
		AssertNoCairoNativeLoad (deniedBefore);
	}

	[Fact]
	public void ManagedGroupsKeepStateAndShareOneSnapshotAcrossCpuAndGpuReplay()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			Surface.ConfigureAutomaticRecording (true);
			using var surface = new ImageSurface (Format.Argb32, 16, 12);
			using var context = new Context (surface);
			context.Translate (3, 2);
			context.Rectangle (0, 0, 2, 2);
			context.Clip ();
			context.SetSourceRGBA (0, 1, 0, 1);
			context.PushGroup (Content.ColorAlpha);
			using (Surface groupTarget = context.GetGroupTarget ()) {
				using var groupContext = new Context (groupTarget);
				Assert.Equal (context.LineWidth, groupContext.LineWidth);
				groupContext.LineWidth = 7;
				groupContext.SetSourceRGBA (1, 0, 0, 1);
				groupContext.Rectangle (0, 0, 3, 3);
				groupContext.Fill ();
			}
			using (Pattern group = context.PopGroup ()) {
				context.SetSource (group);
				context.Paint ();
				context.Paint ();
			}
			Assert.Equal (2, context.LineWidth);
			context.MoveTo (0, 0);
			context.PushGroup ();
			using (Surface groupTarget = context.GetGroupTarget ()) {
				using var groupContext = new Context (groupTarget);
				groupContext.LineTo (1, 1);
			}
			Assert.Equal (1, context.CurrentPoint.X, 5);
			Assert.Equal (1, context.CurrentPoint.Y, 5);
			using (Pattern pathGroup = context.PopGroup ()) { }
			Assert.Equal (1, context.CurrentPoint.X, 5);
			Assert.Equal (1, context.CurrentPoint.Y, 5);
			context.NewPath ();
			context.PushGroup ();
			context.PushGroup ();
			context.SetSourceRGBA (0, 0, 1, 1);
			context.Rectangle (0, 0, 2, 2);
			context.Fill ();
			context.PopGroupToSource ();
			context.Paint ();
			using (Pattern nested = context.PopGroup ()) {
				context.SetSource (nested);
				context.Paint ();
			}
			Assert.Equal (Status.Success, context.Status);
			using SurfaceRecorderSnapshot snapshot = surface.CaptureRecordedSource ();
			Assert.NotNull (snapshot);
			using var cpuBitmap = new SKBitmap (new SKImageInfo (16, 12, SKColorType.Bgra8888, SKAlphaType.Premul));
			using (var cpuCanvas = new SKCanvas (cpuBitmap)) snapshot.DrawTo (cpuCanvas);
			using var replayBitmap = new SKBitmap (new SKImageInfo (16, 12, SKColorType.Bgra8888, SKAlphaType.Premul));
			using (var replaySurface = SKSurface.Create (replayBitmap.Info, replayBitmap.GetPixels (), replayBitmap.RowBytes))
				snapshot.DrawTo (replaySurface, null);
			Assert.Equal (new SKColor (0, 0, 255, 255), cpuBitmap.GetPixel (3, 2));
			Assert.Equal ((byte)0, cpuBitmap.GetPixel (5, 2).Alpha);
			Assert.Equal (cpuBitmap.GetPixel (3, 2), replayBitmap.GetPixel (3, 2));
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			AssertNoCairoNativeLoad (deniedBefore);
		} finally { Surface.ConfigureAutomaticRecording (automaticRecording); }
	}

	[Fact]
	public void NativeGroupsKeepCairoCompatibilityWhenAllowIsExplicit()
	{
		if (!CairoNativeGuard.NativeAllowed) return;
		using var surface = new ImageSurface (Format.Argb32, 8, 8);
		using var context = new Context (surface);
		context.PushGroup ();
		using (Surface target = context.GetGroupTarget ()) Assert.Equal (SurfaceType.Image, target.SurfaceType);
		context.SetSourceRGBA (0, 0, 1, 1);
		context.Rectangle (1, 1, 3, 3);
		context.Fill ();
		using (Pattern group = context.PopGroup ()) {
			context.SetSource (group);
			context.Paint ();
		}
		Assert.Equal (Status.Success, context.Status);
		Assert.Equal (255, surface.Data[1 * surface.Stride + 1 * 4 + 3]);
	}

	[Fact]
	public void DisposingContextWithOpenManagedGroupsRestoresParentRecorder()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		using var surface = new ImageSurface (Format.Argb32, 8, 8);
		using (var context = new Context (surface)) {
			context.LineWidth = 6;
			context.PushGroup ();
			context.PushGroup ();
		}
		using (var context = new Context (surface)) {
			Assert.Equal (6, context.LineWidth);
			context.SetSourceRGBA (0, 1, 0, 1);
			context.Rectangle (1, 1, 2, 2);
			context.Fill ();
		}
		using var bitmap = new SKBitmap (new SKImageInfo (8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
		using (var canvas = new SKCanvas (bitmap)) Assert.True (surface.TryDrawRecordedCommands (canvas));
		Assert.Equal (new SKColor (0, 255, 0, 255), bitmap.GetPixel (1, 1));
		Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
		AssertNoCairoNativeLoad (deniedBefore);
	}

	[Fact]
	public void ManagedColorAndAlphaGroupsUseMatchingImageFormats()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		foreach ((Content content, Format format) in new[] { (Content.Color, Format.Rgb24), (Content.Alpha, Format.A8) }) {
			using var surface = new ImageSurface (Format.Argb32, 6, 6);
			using var context = new Context (surface);
			context.PushGroup (content);
			using (Surface target = context.GetGroupTarget ()) {
				Assert.Equal (format, Assert.IsType<ImageSurface> (target).Format);
				using var groupContext = new Context (target);
				groupContext.SetSourceRGBA (1, 0, 0, .5);
				groupContext.Rectangle (1, 1, 2, 2);
				groupContext.Fill ();
			}
			using (Pattern pattern = context.PopGroup ()) {
				Assert.Equal (PatternType.Surface, pattern.PatternType);
				context.SetSource (pattern);
				context.Paint ();
			}
			using var bitmap = new SKBitmap (new SKImageInfo (6, 6, SKColorType.Bgra8888, SKAlphaType.Premul));
			using (var canvas = new SKCanvas (bitmap)) Assert.True (surface.TryDrawRecordedCommands (canvas));
		}
		Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
		AssertNoCairoNativeLoad (deniedBefore);
	}

	[Fact]
	public void ManagedSolidPatternMaskUsesManagedAlphaWithoutCairo()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			Surface.ConfigureAutomaticRecording (true);
			using var surface = new ImageSurface (Format.Argb32, 4, 4);
			using var mask = new SolidPattern (.8, .2, .1, .5);
			using (var context = new Context (surface)) {
				context.SetSourceRGBA (1, 0, 0, 1);
				context.Mask (mask);
			}

			using var bitmap = new SKBitmap (new SKImageInfo (4, 4, SKColorType.Bgra8888, SKAlphaType.Premul));
			using (var canvas = new SKCanvas (bitmap)) Assert.True (surface.TryDrawRecordedCommands (canvas));
			Assert.Equal (128, bitmap.GetPixel (2, 2).Alpha);
			if (!CairoNativeGuard.NativeAllowed) {
				Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
				AssertNoCairoNativeLoad (deniedBefore);
			}
		} finally { Surface.ConfigureAutomaticRecording (automaticRecording); }
	}

	[Fact]
	public void ManagedFontFacesAndScaledFontsRoundTripThroughContext()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			Surface.ConfigureAutomaticRecording (true);
			using var surface = new ImageSurface (Format.Argb32, 48, 32);
			using var context = new Context (surface);
			using var face = new FontFace ("DejaVu Sans", FontSlant.Italic, FontWeight.Bold);
			context.SetContextFontFace (face);
			context.SetFontSize (16);

			using (FontFace current = context.GetContextFontFace ()) {
				Assert.Equal ("DejaVu Sans", current.Family);
				Assert.Equal (FontSlant.Italic, current.Slant);
				Assert.Equal (FontWeight.Bold, current.Weight);
				Assert.Equal (Status.Success, current.Status);
				Assert.Equal (FontType.Toy, current.FontType);
			}

			FontExtents contextFontExtents = context.FontExtents;
			TextExtents contextTextExtents = context.TextExtents ("Managed Cairo");
			Assert.True (contextFontExtents.Ascent > 0);
			Assert.True (contextTextExtents.XAdvance > 0);

			using var scaled = context.GetScaledFont ();
			Assert.Equal (new Matrix (16, 0, 0, 16, 0, 0), scaled.FontMatrix);
			Assert.Equal (Status.Success, scaled.Status);
			if (CairoNativeGuard.NativeAllowed)
				Assert.Contains (scaled.FontType, new[] { FontType.FreeType, FontType.Win32, FontType.Atsui, FontType.Toy });
			else Assert.Equal (FontType.Toy, scaled.FontType);
			Assert.True (scaled.FontExtents.Ascent > 0);
			TextExtents glyphExtents = scaled.GlyphExtents (new[] { new Glyph (36, 0, 0), new Glyph (37, 10, 0) });
			Assert.True (glyphExtents.XAdvance > 0);

			context.Save ();
			using (var otherFace = new FontFace ("Serif", FontSlant.Normal, FontWeight.Normal)) {
				context.SetContextFontFace (otherFace);
				context.SetFontSize (28);
			}
			context.Restore ();
			using (FontFace restoredFace = context.GetContextFontFace ()) {
				Assert.Equal ("DejaVu Sans", restoredFace.Family);
				Assert.Equal (FontSlant.Italic, restoredFace.Slant);
				Assert.Equal (FontWeight.Bold, restoredFace.Weight);
			}
			Assert.Equal (contextFontExtents, context.FontExtents);
			Assert.Equal (contextTextExtents, context.TextExtents ("Managed Cairo"));

			using var scaledFace = new FontFace ("Serif", FontSlant.Oblique, FontWeight.Normal);
			using var options = new FontOptions { Antialias = Antialias.Gray, HintMetrics = HintMetrics.On };
			using var replacement = new ScaledFont (scaledFace, new Matrix (22, 0, 0, 22, 0, 0), new Matrix (), options);
			context.SetScaledFont (replacement);
			Assert.Equal (new Matrix (22, 0, 0, 22, 0, 0), context.FontMatrix);
			using (FontFace replacementFace = context.GetContextFontFace ()) {
				Assert.Equal ("Serif", replacementFace.Family);
				Assert.Equal (FontSlant.Oblique, replacementFace.Slant);
			}
			using var roundTrip = context.GetScaledFont ();
			Assert.Equal (replacement.FontMatrix, roundTrip.FontMatrix);
			Assert.Equal (replacement.FontExtents, roundTrip.FontExtents);
			Assert.Equal (replacement.GlyphExtents (new[] { new Glyph (36, 0, 0) }), roundTrip.GlyphExtents (new[] { new Glyph (36, 0, 0) }));

			if (CairoNativeGuard.NativeAllowed) {
				Assert.NotEqual (IntPtr.Zero, face.Handle);
				Assert.NotEqual (IntPtr.Zero, replacement.Handle);
			} else {
				Assert.Throws<NotSupportedException> (() => _ = face.Handle);
				Assert.Throws<NotSupportedException> (() => _ = replacement.Handle);
				Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
				AssertNoCairoNativeLoad (deniedBefore);
			}
		} finally { Surface.ConfigureAutomaticRecording (automaticRecording); }
	}

	[Fact]
	public void ManagedSourcePatternsPathAndFontOptionsAvoidNativeBoundaries()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		using var surface = new ImageSurface (Format.Argb32, 12, 10);
		using var context = new Context (surface);
		context.SetSourceRGBA (.2, .4, .8, .6);
		using (Pattern source = context.GetSource ()) {
			var solid = Assert.IsType<SolidPattern> (source);
			Assert.Equal (PatternType.Solid, source.PatternType);
			Assert.Equal (Status.Success, source.Status);
			Assert.Equal (.2, solid.Color.R, 5);
			Assert.Throws<NotSupportedException> (() => _ = source.Handle);
		}
		using (var gradient = new LinearGradient (0, 0, 12, 10)) {
			Assert.Equal (Status.Success, gradient.AddColorStop (0, new Color (1, 0, 0, 1)));
			Assert.Equal (Status.Success, gradient.AddColorStop (1, new Color (0, 0, 1, 1)));
			Assert.Equal (2, gradient.ColorStopCount);
			Assert.Equal (new PointD (0, 0), gradient.LinearPoints[0]);
			Assert.Equal (new PointD (12, 10), gradient.LinearPoints[1]);
			Assert.Throws<NotSupportedException> (() => _ = gradient.Handle);
			context.SetSource (gradient);
			context.Rectangle (0, 0, 3, 3);
			context.Fill ();
			using Pattern source = context.GetSource ();
			Assert.Equal (PatternType.Linear, source.PatternType);
		}

		using (var surfacePattern = new SurfacePattern (surface) {
			Filter = Filter.Nearest,
			Extend = Extend.Repeat,
			Matrix = new Matrix (1, 0, 0, 1, 2, 3)
		}) {
			Assert.Equal (PatternType.Surface, surfacePattern.PatternType);
			Assert.Equal (Filter.Nearest, surfacePattern.Filter);
			Assert.Equal (Extend.Repeat, surfacePattern.Extend);
			Assert.Equal (2, surfacePattern.Matrix.X0);
			Assert.Throws<NotSupportedException> (() => _ = surfacePattern.Handle);
			context.SetSource (surfacePattern);
			using Pattern clonedSource = context.GetSource ();
			Assert.Equal (PatternType.Surface, clonedSource.PatternType);
			Assert.Throws<NotSupportedException> (() => _ = clonedSource.Handle);
		}

		context.NewPath ();
		context.MoveTo (1, 2);
		context.LineTo (4, 5);
		using Cairo.Path path = context.CopyPath ();
		Assert.NotEqual (IntPtr.Zero, path.Handle);
		using var secondSurface = new ImageSurface (Format.Argb32, 12, 10);
		using var secondContext = new Context (secondSurface);
		secondContext.AppendPath (path);
		Assert.True (secondContext.HasCurrentPoint);
		Assert.Equal (4, secondContext.CurrentPoint.X, 5);
		Assert.Equal (5, secondContext.CurrentPoint.Y, 5);

		using var options = new FontOptions {
			Antialias = Antialias.Gray,
			HintStyle = HintStyle.Full,
			HintMetrics = HintMetrics.On,
			SubpixelOrder = SubpixelOrder.Rgb
		};
		using var copy = options.Copy ();
		Assert.Equal (options, copy);
		context.FontOptions = options;
		using var actual = context.FontOptions;
		Assert.Equal (Antialias.Gray, actual.Antialias);
		Assert.Equal (HintStyle.Full, actual.HintStyle);
		Assert.Equal (HintMetrics.On, actual.HintMetrics);
		Assert.Equal (SubpixelOrder.Rgb, actual.SubpixelOrder);
		Assert.Throws<NotSupportedException> (() => _ = actual.Handle);

		surface.DeviceOffset = new PointD (1, 2);
		Assert.Equal (new PointD (1, 2), surface.DeviceOffset);
		surface.SetFallbackResolution (72, 96);
		Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
		AssertNoCairoNativeLoad (deniedBefore);
	}

	[Fact]
	public void ManagedSurfaceShowAndDeviceOffsetRenderWithoutCairo()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			Surface.ConfigureAutomaticRecording (true);
			using var source = new ImageSurface (Format.Argb32, 1, 1);
			using (var sourceContext = new Context (source)) {
				sourceContext.SetSourceRGBA (.2, .4, .8, 1);
				sourceContext.Paint ();
			}

			using var target = new ImageSurface (Format.Argb32, 8, 6);
			target.DeviceOffset = new PointD (1, 1);
			target.SetFallbackResolution (72, 96);
			Assert.Equal (new PointD (1, 1), target.DeviceOffset);
			using (var context = new Context (target)) {
				source.Show (context, 2, 1);
				context.SetSourceRGBA (1, 0, 0, 1);
				context.Rectangle (0, 0, 1, 1);
				context.Fill ();
			}

			using var bitmap = new SKBitmap (new SKImageInfo (8, 6, SKColorType.Bgra8888, SKAlphaType.Premul));
			using (var canvas = new SKCanvas (bitmap)) Assert.True (target.TryDrawRecordedCommands (canvas));
			Assert.Equal (SKColors.Red, bitmap.GetPixel (1, 1));
			Assert.Equal (new SKColor (0x33, 0x66, 0xcc, 0xff), bitmap.GetPixel (3, 2));
			Assert.Equal (0, bitmap.GetPixel (2, 1).Alpha);
			AssertNoCairoNativeLoad (deniedBefore);
		} finally { Surface.ConfigureAutomaticRecording (automaticRecording); }
	}

	[Fact]
	public void NativeSurfaceShowAndDeviceOffsetRemainCairoCompatibleWhenAllowed()
	{
		if (!CairoNativeGuard.NativeAllowed) return;
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		try {
			Surface.ConfigureAutomaticRecording (false);
			using var source = new ImageSurface (Format.Argb32, 2, 2);
			source.DeviceOffset = new PointD (1, 0);
			source.SetFallbackResolution (144, 96);
			using (var context = new Context (source)) {
				context.SetSourceRGBA (.2, .4, .8, 1);
				context.Rectangle (0, 0, 2, 2);
				context.Fill ();
			}

			using var actual = new ImageSurface (Format.Argb32, 8, 6);
			using var expected = new ImageSurface (Format.Argb32, 8, 6);
			actual.DeviceOffset = expected.DeviceOffset = new PointD (1, 2);
			actual.SetFallbackResolution (72, 96);
			expected.SetFallbackResolution (72, 96);
			using (var context = new Context (actual)) source.Show (context, 2, 1);
			using (var context = new Context (expected)) {
				context.SetSourceSurface (source, 2, 1);
				context.Paint ();
			}
			Assert.Equal (new PointD (1, 2), actual.DeviceOffset);
			Assert.Equal (expected.Data, actual.Data);
		} finally { Surface.ConfigureAutomaticRecording (automaticRecording); }
	}

	static void CreateTransientManagedCairoObjects ()
	{
		var surface = new ImageSurface (Format.Argb32, 8, 8);
		var context = new Context (surface);
		var solid = new SolidPattern (.1, .2, .3, .4);
		var gradient = new LinearGradient (0, 0, 8, 8);
		gradient.AddColorStop (0, new Color (1, 0, 0, 1));
		context.SetSource (gradient);
		context.MoveTo (0, 0);
		context.LineTo (3, 3);
		var path = context.CopyPath ();
		var options = new FontOptions { Antialias = Antialias.Gray };
		context.FontOptions = options;
		GC.KeepAlive (solid);
		GC.KeepAlive (path);
		GC.KeepAlive (options);
	}

	[Fact]
	public void ManagedObjectFinalizersDoNotResolveCairo()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		CreateTransientManagedCairoObjects ();
		GC.Collect ();
		GC.WaitForPendingFinalizers ();
		GC.Collect ();
		Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
		AssertNoCairoNativeLoad (deniedBefore);
	}

	[Fact]
	public void ImageSurfacePageOperationsAreNoOpsWithoutCairoAndRemainNativeWhenAllowed()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			if (CairoNativeGuard.NativeAllowed) Surface.ConfigureAutomaticRecording (false);
			using var surface = new ImageSurface (Format.Argb32, 8, 8);
			using var context = new Context (surface);
			context.CopyPage ();
			context.ShowPage ();
			Assert.Equal (Status.Success, context.Status);
			if (!CairoNativeGuard.NativeAllowed) {
				Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
				AssertNoCairoNativeLoad (deniedBefore);
			}
		} finally {
			Surface.ConfigureAutomaticRecording (automaticRecording);
		}
	}

	[Fact]
	public void ManagedDataIsAnImmutableRenderedCopyAndFinishFlushAndCreateSimilarStayCairoFree()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			Surface.ConfigureAutomaticRecording (false);
			using var surface = new ImageSurface (Format.Argb32, 4, 3);
			using var context = new Context (surface);
			if (!CairoNativeGuard.NativeAllowed) {
				Marshal.WriteInt32 (surface.DataPtr, 0, unchecked ((int)0xffff0000));
				surface.MarkDirty ();
			} else {
				context.SetSourceRGBA (1, 0, 0, 1);
				context.Rectangle (0, 0, 1, 1);
				context.Fill ();
			}
			context.SetSourceRGBA (0, 0, 1, 1);
			context.Rectangle (1, 0, 1, 1);
			context.Fill ();

			byte[] first = surface.Data;
			int stride = surface.Stride;
			Assert.Equal (stride * 3, first.Length);
			Assert.Equal (255, first[2]);
			Assert.Equal (255, first[3]);
			Assert.Equal (255, first[4]);
			Assert.Equal (255, first[1 * 4 + 3]);
			if (!CairoNativeGuard.NativeAllowed) {
				Assert.Equal (255, first[2]);
				first[2] = 0;
				first[3] = 0;
				Assert.Equal (255, surface.Data[2]);
				Assert.Equal (0xff0000ffu, unchecked ((uint)Marshal.ReadInt32 (surface.DataPtr, 4)));
			}

			context.SetSourceRGBA (0, 1, 0, 1);
			context.Rectangle (2, 0, 1, 1);
			context.Fill ();
			Assert.Equal (255, surface.Data[2 * 4 + 1]);
			using Surface colorSimilar = surface.CreateSimilar (Content.Color, 2, 2);
			Assert.Equal (SurfaceType.Image, colorSimilar.SurfaceType);
			if (!CairoNativeGuard.NativeAllowed) {
				Assert.Equal (Format.Rgb24, ((ImageSurface)colorSimilar).Format);
				using Surface alphaSimilar = surface.CreateSimilar (Content.ColorAlpha, 2, 2);
				Assert.Equal (Format.Argb32, ((ImageSurface)alphaSimilar).Format);
				using Surface alphaOnlySimilar = surface.CreateSimilar (Content.Alpha, 2, 2);
				Assert.Equal (Format.A8, ((ImageSurface)alphaOnlySimilar).Format);
				Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
				AssertNoCairoNativeLoad (deniedBefore);
			} else {
				using Surface alphaSimilar = surface.CreateSimilar (Content.Alpha, 2, 2);
				Assert.Equal (SurfaceType.Image, alphaSimilar.SurfaceType);
			}
			surface.Flush ();
			byte[] beforeFinish = surface.Data;
			Assert.Equal (Status.Success, surface.Finish ());
			Assert.Equal (beforeFinish, surface.Data);
			if (!CairoNativeGuard.NativeAllowed)
				Assert.Throws<InvalidOperationException> (() => context.Rectangle (3, 0, 1, 1));
		} finally {
			Surface.ConfigureAutomaticRecording (automaticRecording);
		}
	}

	[Fact]
	public void ManagedA8A1AndRgb565SurfacesRenderExpectedPixels()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		try {
			Surface.ConfigureAutomaticRecording (false);
			using var alpha = new ImageSurface (Format.A8, 3, 2);
			Assert.Equal (Content.Alpha, alpha.Content);
			using (var context = new Context (alpha)) {
				context.SetSourceRGBA (1, 0, 0, .5);
				context.Rectangle (0, 0, 3, 2);
				context.Fill ();
			}
			byte[] alphaPixels = alpha.Data;
			Assert.Equal (4, alpha.Stride);
			Assert.Equal (8, alphaPixels.Length);
			Assert.InRange (alphaPixels[0], (byte)127, (byte)129);
			Assert.InRange (alphaPixels[2], (byte)127, (byte)129);

			using var rgb565 = new ImageSurface (Format.Rgb16565, 3, 2);
			Assert.Equal (Content.Color, rgb565.Content);
			using (var context = new Context (rgb565)) {
				context.SetSourceRGBA (1, 0, 0, 1);
				context.Rectangle (0, 0, 3, 2);
				context.Fill ();
			}
			byte[] colorPixels = rgb565.Data;
			Assert.Equal (8, rgb565.Stride);
			Assert.Equal (16, colorPixels.Length);
			Assert.Equal (0xf800, BitConverter.ToUInt16 (colorPixels, 0));
			Assert.Equal (0xf800, BitConverter.ToUInt16 (colorPixels, 4));

			using var a1 = new ImageSurface (Format.A1, 9, 2);
			Assert.Equal (Content.Alpha, a1.Content);
			Assert.Equal (4, a1.Stride);
			using (var context = new Context (a1)) {
				context.Antialias = Antialias.None;
				context.SetSourceRGBA (1, 1, 1, 1);
				context.Rectangle (8, 0, 1, 1);
				context.Fill ();
			}
			byte[] a1Pixels = a1.Data;
			Assert.Equal (8, a1Pixels.Length);
			Assert.Equal (0x00, a1Pixels[0]);
			Assert.Equal (0x01, a1Pixels[1]);
			Assert.Equal (0x00, a1Pixels[4]);
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			AssertNoCairoNativeLoad (deniedBefore);
		} finally {
			Surface.ConfigureAutomaticRecording (automaticRecording);
		}
	}

	[Fact]
	public void ManagedTargetAliasesReleaseSharedPixelPinInEitherDisposalOrder()
	{
		if (CairoNativeGuard.NativeAllowed) return;
		var firstOwner = new ImageSurface (Format.Argb32, 8, 8);
		var firstContext = new Context (firstOwner);
		var firstAlias = (ImageSurface)firstContext.GetTarget ();
		IntPtr firstPointer = firstAlias.DataPtr;
		Assert.NotEqual (IntPtr.Zero, firstPointer);
		firstOwner.Dispose ();
		Assert.False (firstAlias.ManagedPixelPinReleased);
		Assert.Equal (firstPointer, firstAlias.DataPtr);
		firstOwner.Dispose ();
		firstAlias.Dispose ();
		Assert.True (firstAlias.ManagedPixelPinReleased);
		firstContext.Dispose ();

		var secondOwner = new ImageSurface (Format.Argb32, 8, 8);
		var secondContext = new Context (secondOwner);
		var secondAlias = (ImageSurface)secondContext.GetTarget ();
		_ = secondAlias.DataPtr;
		secondAlias.Dispose ();
		Assert.False (secondAlias.ManagedPixelPinReleased);
		secondOwner.Dispose ();
		Assert.True (secondAlias.ManagedPixelPinReleased);
		secondContext.Dispose ();
		Assert.Equal (0, CairoNativeGuard.DeniedCount);
		AssertNoCairoNativeLoad (0);
	}

	[Fact]
	public void ManagedRgb24BufferRecordsAndWritesPngWithoutCairo()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		string filename = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-rgb24-" + Guid.NewGuid ().ToString ("N") + ".png");
		try
		{
			Surface.ConfigureAutomaticRecording (true);
			const int width = 4, height = 3;
			int stride = Surface.StrideForWidth (Format.Rgb24, width);
			var pixels = new byte[stride * height];
			for (int offset = 0; offset < pixels.Length; offset += 4)
				Buffer.BlockCopy (BitConverter.GetBytes (0x00ff0000), 0, pixels, offset, 4);
			using var surface = (ImageSurface) ImageSurface.CreateForImage (pixels, Format.Rgb24, width, height);
			Assert.Equal (SurfaceRecordingState.Recording, surface.RecordingState);
			using (var context = new Context (surface)) {
				context.SetSourceRGBA (0, 0, 1, 1);
				context.Rectangle (1, 1, 2, 1);
				context.Fill ();
			}

			surface.WriteToPng (filename);
			using var image = SKBitmap.Decode (filename);
			Assert.NotNull (image);
			Assert.Equal (new SKColor (255, 0, 0, 255), image.GetPixel (0, 0));
			Assert.Equal (new SKColor (0, 0, 255, 255), image.GetPixel (1, 1));
			// PNG export takes an immutable snapshot and keeps the drawing surface live.
			Assert.Equal (SurfaceRecordingState.Recording, surface.RecordingState);
			Assert.Equal (0, CairoNativeGuard.DeniedCount);
		}
		finally
		{
			Surface.ConfigureAutomaticRecording (automaticRecording);
			if (File.Exists (filename)) File.Delete (filename);
		}
	}

	[Fact]
	public void DisablingAutomaticRecordingKeepsNativeRgb24PngPath()
	{
		if (!CairoNativeGuard.NativeAllowed) return;
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		string filename = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-rgb24-native-" + Guid.NewGuid ().ToString ("N") + ".png");
		try
		{
			Surface.ConfigureAutomaticRecording (false);
			const int width = 2, height = 2;
			int stride = Surface.StrideForWidth (Format.Rgb24, width);
			var pixels = new byte[stride * height];
			for (int offset = 0; offset < pixels.Length; offset += 4)
				Buffer.BlockCopy (BitConverter.GetBytes (0x0000ff00), 0, pixels, offset, 4);
			using var surface = (ImageSurface) ImageSurface.CreateForImage (pixels, Format.Rgb24, width, height);
			Assert.NotEqual (IntPtr.Zero, surface.Handle);
			Assert.Equal (SurfaceRecordingState.Native, surface.RecordingState);
			surface.WriteToPng (filename);
			using var image = SKBitmap.Decode (filename);
			Assert.NotNull (image);
			Assert.Equal (new SKColor (0, 255, 0, 255), image.GetPixel (0, 0));
		}
		finally
		{
			Surface.ConfigureAutomaticRecording (automaticRecording);
			if (File.Exists (filename)) File.Delete (filename);
		}
	}

	[Fact]
	public void ManagedPngFilenameLoadPreservesPixelsWithoutCairo()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		string filename = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-png-load-" + Guid.NewGuid ().ToString ("N") + ".png");
		try
		{
			using (var source = new SKBitmap (new SKImageInfo (2, 1, SKColorType.Bgra8888, SKAlphaType.Premul))) {
				source.SetPixel (0, 0, new SKColor (220, 30, 60, 255));
				source.SetPixel (1, 0, new SKColor (40, 180, 90, 96));
				using SKData encoded = source.Encode (SKEncodedImageFormat.Png, 100);
				Assert.NotNull (encoded);
				using var output = File.Create (filename);
				encoded.SaveTo (output);
			}

			Surface.ConfigureAutomaticRecording (true);
			using var surface = new ImageSurface (filename);
			Assert.Equal (2, surface.Width);
			Assert.Equal (1, surface.Height);
			Assert.Equal (Format.Argb32, surface.Format);
			Assert.Equal (SurfaceRecordingState.Recording, surface.RecordingState);
			using var target = new SKBitmap (new SKImageInfo (2, 1, SKColorType.Bgra8888, SKAlphaType.Premul));
			using (var canvas = new SKCanvas (target)) Assert.True (surface.TryDrawRecordedCommands (canvas));
			Assert.Equal (new SKColor (220, 30, 60, 255), target.GetPixel (0, 0));
			SKColor translucent = target.GetPixel (1, 0);
			Assert.Equal (96, translucent.Alpha);
			Assert.InRange (translucent.Red, 39, 41);
			Assert.InRange (translucent.Green, 179, 181);
			Assert.InRange (translucent.Blue, 89, 91);
			Assert.Equal (0, CairoNativeGuard.DeniedCount);
			if (CairoNativeGuard.Mode == "deny") {
				InvalidOperationException exception = Assert.Throws<InvalidOperationException> (() => _ = surface.Handle);
				Assert.Contains ("Cairo materialization is forbidden", exception.Message);
				Assert.Equal (0, CairoNativeGuard.DeniedCount);
			}
		}
		finally
		{
			Surface.ConfigureAutomaticRecording (automaticRecording);
			if (File.Exists (filename)) File.Delete (filename);
		}
	}

	[Fact]
	public void DisablingAutomaticRecordingKeepsNativePngFilenameLoad()
	{
		if (!CairoNativeGuard.NativeAllowed) return;
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		string filename = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-png-native-" + Guid.NewGuid ().ToString ("N") + ".png");
		try
		{
			using (var source = new SKBitmap (new SKImageInfo (1, 1, SKColorType.Bgra8888, SKAlphaType.Premul))) {
				source.SetPixel (0, 0, new SKColor (12, 140, 230, 128));
				using SKData encoded = source.Encode (SKEncodedImageFormat.Png, 100);
				using var output = File.Create (filename);
				encoded.SaveTo (output);
			}
			Surface.ConfigureAutomaticRecording (false);
			using var surface = new ImageSurface (filename);
			Assert.NotEqual (IntPtr.Zero, surface.Handle);
			Assert.Equal (SurfaceRecordingState.Native, surface.RecordingState);
			Assert.Equal (Status.Success, surface.Status);
			byte[] data = surface.Data;
			Assert.Equal (new byte[] { 115, 70, 6, 128 }, data);
		}
		finally
		{
			Surface.ConfigureAutomaticRecording (automaticRecording);
			if (File.Exists (filename)) File.Delete (filename);
		}
	}

	[Fact]
	public void AutomaticPngLoadKeepsCairoErrorSurfaceForInvalidFiles()
	{
		if (!CairoNativeGuard.NativeAllowed) return;
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		string filename = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-png-invalid-" + Guid.NewGuid ().ToString ("N") + ".png");
		try
		{
			File.WriteAllBytes (filename, new byte[] { 1, 2, 3, 4 });
			Surface.ConfigureAutomaticRecording (false);
			using var surface = new ImageSurface (filename);
			Assert.NotEqual (IntPtr.Zero, surface.Handle);
			Assert.NotEqual (Status.Success, surface.Status);
		}
		finally
		{
			Surface.ConfigureAutomaticRecording (automaticRecording);
			if (File.Exists (filename)) File.Delete (filename);
		}
	}

	[Fact]
	public void ManagedPngLoadPreservesCairoErrorSurfaceAndContextStatuses()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		int deniedBefore = CairoNativeGuard.DeniedCount;
		string missing = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-png-missing-" + Guid.NewGuid ().ToString ("N") + ".png");
		string malformed = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-png-malformed-" + Guid.NewGuid ().ToString ("N") + ".png");
		string corrupt = System.IO.Path.Combine (System.IO.Path.GetTempPath (), "optimum-png-corrupt-" + Guid.NewGuid ().ToString ("N") + ".png");
		try {
			File.WriteAllBytes (malformed, new byte[] { 1, 2, 3, 4 });
			File.WriteAllBytes (corrupt, Convert.FromBase64String ("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/FZsAAAAASUVORK5CYII="));
			Surface.ConfigureAutomaticRecording (true);
			AssertManagedPngErrorSurface (missing, Status.FileNotFound);
			AssertManagedPngErrorSurface (malformed, Status.ReadError);
			AssertManagedPngErrorSurface (corrupt, Status.PngError);
			Assert.Equal (deniedBefore, CairoNativeGuard.DeniedCount);
			AssertNoCairoNativeLoad (deniedBefore);
		} finally {
			Surface.ConfigureAutomaticRecording (automaticRecording);
			if (File.Exists (malformed)) File.Delete (malformed);
			if (File.Exists (corrupt)) File.Delete (corrupt);
		}
	}

	static void AssertManagedPngErrorSurface (string filename, Status expectedStatus)
	{
		using var surface = new ImageSurface (filename);
		Assert.Equal (expectedStatus, surface.Status);
		Assert.Equal (0, surface.Width);
		Assert.Equal (0, surface.Height);
		Assert.Equal ((Format)(-1), surface.Format);
		Assert.Equal (0, surface.Stride);
		Assert.Equal (Content.Color, surface.Content);
		Assert.Equal (SurfaceType.Image, surface.SurfaceType);
		Assert.Null (surface.Data);
		Assert.Equal (IntPtr.Zero, surface.DataPtr);
		using var context = new Context (surface);
		Assert.Equal (expectedStatus, context.Status);
		context.SetSourceRGBA (1, 0, 0, 1);
		context.Rectangle (0, 0, 4, 4);
		context.Fill ();
		Assert.Equal (expectedStatus, context.Status);
		Assert.Equal (expectedStatus, surface.Finish ());
		if (!CairoNativeGuard.NativeAllowed) return;

		Surface.ConfigureAutomaticRecording (false);
		using var nativeSurface = new ImageSurface (filename);
		Assert.Equal (nativeSurface.Status, expectedStatus);
		Assert.Equal (nativeSurface.Width, surface.Width);
		Assert.Equal (nativeSurface.Height, surface.Height);
		Assert.Equal (nativeSurface.Format, surface.Format);
		Assert.Equal (nativeSurface.Stride, surface.Stride);
		Assert.Equal (nativeSurface.Content, surface.Content);
		Assert.Equal (nativeSurface.SurfaceType, surface.SurfaceType);
		Assert.Null (nativeSurface.Data);
		using var nativeContext = new Context (nativeSurface);
		Assert.Equal (nativeContext.Status, expectedStatus);
		Surface.ConfigureAutomaticRecording (true);
	}

	[Fact]
	public void ManagedByteArrayImageSurfaceKeepsAliasedPixelsForPatternRendering()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		try
		{
			Surface.ConfigureAutomaticRecording(true);
			byte[] pixels = BitConverter.GetBytes(unchecked((int)0xff3366cc));
			using var texture = new ImageSurface(pixels, Format.Argb32, 1, 1, 4);
			Marshal.WriteInt32(texture.DataPtr, 0, unchecked((int)0xffaa5500));
			texture.MarkDirty();
			Assert.Equal(unchecked((int)0xffaa5500), BitConverter.ToInt32(pixels, 0));

			using var pattern = new SurfacePattern(texture) { Filter = Filter.Nearest, Extend = Extend.Repeat };
			pattern.Matrix = new Matrix();
			using var surface = new ImageSurface(Format.Argb32, 8, 8);
			using (var context = new Context(surface))
			{
				context.SetSource(pattern);
				context.Rectangle(0, 0, 8, 8);
				context.Fill();
			}

			using var target = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
			using (var canvas = new SKCanvas(target)) Assert.True(surface.TryDrawRecordedCommands(canvas));
			Assert.Equal(new SKColor(0xaa, 0x55, 0x00, 0xff), target.GetPixel(4, 4));
			Assert.Equal(0, CairoNativeGuard.DeniedCount);
		}
		finally
		{
			Surface.ConfigureAutomaticRecording(automaticRecording);
		}
	}

	[Fact]
	public void DisablingAutomaticRecordingKeepsNativeByteArraySurfaceFactory()
	{
		if (!CairoNativeGuard.NativeAllowed) return;
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		try
		{
			Surface.ConfigureAutomaticRecording(false);
			byte[] pixels = BitConverter.GetBytes(unchecked((int)0xff3366cc));
			using var surface = new ImageSurface(pixels, Format.Argb32, 1, 1, 4);
			Assert.NotEqual(IntPtr.Zero, surface.Handle);
			Assert.Equal(pixels, surface.Data);
			Assert.Equal(SurfaceRecordingState.Native, surface.RecordingState);
		}
		finally
		{
			Surface.ConfigureAutomaticRecording(automaticRecording);
		}
	}

	[Fact]
	public void RecordedFontOptionsStayManagedUntilExplicitNativeMaterialization()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		try
		{
			Surface.ConfigureAutomaticRecording(true);
			using var surface = new ImageSurface(Format.Argb32, 4, 4);
			using var context = new Context(surface);
			using var options = new FontOptions {
				Antialias = Antialias.Gray,
				HintStyle = HintStyle.Full,
				HintMetrics = HintMetrics.On,
				SubpixelOrder = SubpixelOrder.Rgb
			};
			context.FontOptions = options;

			using var actual = context.FontOptions;
			Assert.Equal(Antialias.Gray, actual.Antialias);
			Assert.Equal(HintStyle.Full, actual.HintStyle);
			Assert.Equal(HintMetrics.On, actual.HintMetrics);
			Assert.Equal(SubpixelOrder.Rgb, actual.SubpixelOrder);
			using var overrides = new FontOptions { Antialias = Antialias.Subpixel };
			actual.Merge(overrides);
			Assert.Equal(Antialias.Subpixel, actual.Antialias);
			using var copy = actual.Copy();
			Assert.Equal(actual, copy);

			if (!CairoNativeGuard.NativeAllowed) {
				NotSupportedException exception = Assert.Throws<NotSupportedException>(() => _ = actual.Handle);
				Assert.Contains("explicitly enabled libcairo compatibility backend", exception.Message);
			} else {
				Assert.NotEqual(IntPtr.Zero, actual.Handle);
			}
		}
		finally
		{
			Surface.ConfigureAutomaticRecording(automaticRecording);
		}
	}

	[Fact]
	public void ManagedSolidPatternsRenderWithoutCairo()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		try
		{
			Surface.ConfigureAutomaticRecording(true);
			using var source = new SolidPattern(.2, .6, .9, .75);
			Assert.Equal(PatternType.Solid, source.PatternType);
			Assert.Equal(Status.Success, source.Status);
			Assert.Equal(new Color(.2, .6, .9, .75), source.Color);
			Assert.Equal(0, CairoNativeGuard.DeniedCount);

			using var surface = new ImageSurface(Format.Argb32, 8, 8);
			using (var context = new Context(surface))
			{
				context.SetSource(source);
				context.Rectangle(1, 1, 6, 6);
				context.Fill();
			}

			using var target = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
			using (var canvas = new SKCanvas(target)) Assert.True(surface.TryDrawRecordedCommands(canvas));
			Assert.Equal((byte)0, target.GetPixel(0, 0).Alpha);
			Assert.Equal(191, target.GetPixel(4, 4).Alpha);
			Assert.Equal(0, CairoNativeGuard.DeniedCount);
			using Pattern clone = source.CloneForRecorder();
			Assert.Equal(PatternType.Solid, clone.PatternType);
			Assert.Equal(Status.Success, clone.Status);
			Assert.Equal(source.Color, ((SolidPattern)clone).Color);
		}
		finally
		{
			Surface.ConfigureAutomaticRecording(automaticRecording);
		}
	}

	[Fact]
	public void AutomaticGuiSurfaceRecordsBeforeNativeCairoMaterialization()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		try
		{
			Surface.ConfigureAutomaticRecording(true);
			using var surface = new ImageSurface(Format.Argb32, 12, 8);
			using (var context = new Context(surface))
			{
				context.SetSourceRGBA(.2, .6, .9, 1);
				context.Rectangle(2, 1, 7, 5);
				context.Fill();
			}

			Assert.Equal(SurfaceRecordingState.Recording, surface.RecordingState);
			using (var target = new SKBitmap(new SKImageInfo(12, 8, SKColorType.Bgra8888, SKAlphaType.Premul)))
			{
				using (var canvas = new SKCanvas(target)) Assert.True(surface.TryDrawRecordedCommands(canvas));
				Assert.Equal(new SKColor(51, 153, 230, 255), target.GetPixel(4, 3));
				Assert.Equal((byte)0, target.GetPixel(0, 0).Alpha);
			}
			Assert.Equal(SurfaceRecordingState.Sealed, surface.RecordingState);

			byte[] pixels = surface.Data;
			int stride = surface.Stride;
			Assert.Equal((byte)51, pixels[3 * stride + 4 * 4 + 2]);
			Assert.Equal((byte)153, pixels[3 * stride + 4 * 4 + 1]);
			Assert.Equal((byte)230, pixels[3 * stride + 4 * 4]);
			Assert.Equal((byte)255, pixels[3 * stride + 4 * 4 + 3]);
			Assert.Equal(0, CairoNativeGuard.DeniedCount);
		}
		finally
		{
			Surface.ConfigureAutomaticRecording(automaticRecording);
		}
	}

	[Fact]
	public void ManagedTexturePixelsCanBeUsedByRecordedSurfacePatternsWithoutCairo()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		try
		{
			Surface.ConfigureAutomaticRecording(true);
			using var texture = new ImageSurface(Format.Argb32, 1, 1);
			Marshal.WriteInt32(texture.DataPtr, 0, unchecked((int)0xff3366cc));
			texture.MarkDirty();
			texture.MarkDirty(new Rectangle(0, 0, 1, 1));
			using var pattern = new SurfacePattern(texture) { Filter = Filter.Nearest, Extend = Extend.Repeat };
			pattern.Matrix = new Matrix();

			using var surface = new ImageSurface(Format.Argb32, 8, 8);
			using (var context = new Context(surface))
			{
				context.SetSource(pattern);
				context.Rectangle(0, 0, 8, 8);
				context.Fill();
			}

			Assert.Equal(SurfaceRecordingState.Recording, surface.RecordingState);
			using var target = new SKBitmap(new SKImageInfo(8, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
			using (var canvas = new SKCanvas(target)) Assert.True(surface.TryDrawRecordedCommands(canvas));
			Assert.Equal(new SKColor(0x33, 0x66, 0xcc, 0xff), target.GetPixel(4, 4));
			Assert.Equal(new SKColor(0x33, 0x66, 0xcc, 0xff), target.GetPixel(0, 0));
			if (!CairoNativeGuard.NativeAllowed) Assert.Equal(0, CairoNativeGuard.DeniedCount);
		}
		finally
		{
			Surface.ConfigureAutomaticRecording(automaticRecording);
		}
	}

	[Fact]
	public void ManagedSurfacePatternKeepsFilterExtendAndMatrixWithoutCairo()
	{
		bool automaticRecording = Surface.AutomaticRecordingEnabled;
		try
		{
			Surface.ConfigureAutomaticRecording(true);
			using var texture = new ImageSurface(Format.Argb32, 2, 1);
			Marshal.WriteInt32(texture.DataPtr, 0, unchecked((int)0xffff0000));
			Marshal.WriteInt32(texture.DataPtr, 4, unchecked((int)0xff00ff00));
			using var pattern = new SurfacePattern(texture) { Filter = Filter.Nearest, Extend = Extend.Repeat };
			pattern.Matrix = new Matrix();
			Assert.Equal(Filter.Nearest, pattern.Filter);
			Assert.Equal(Extend.Repeat, pattern.Extend);
			Assert.True(pattern.Matrix.IsIdentity());
			Assert.Equal(PatternType.Surface, pattern.PatternType);
			Assert.Equal(Status.Success, pattern.Status);
			Assert.Equal(0, CairoNativeGuard.DeniedCount);
		}
		finally
		{
			Surface.ConfigureAutomaticRecording(automaticRecording);
		}
	}

    [Fact]
    public void ManagedGuiSurfaceRecordsAndRendersWithoutCairo()
    {
        using var surface = ImageSurface.CreateForRecordedGui(Format.Argb32, 12, 8);
        using (var context = new Context(surface))
        {
            context.SetSourceRGBA(.2, .6, .9, 1);
            context.Rectangle(2, 1, 7, 5);
            context.Fill();
        }

        using var target = new SKBitmap(new SKImageInfo(12, 8, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target)) Assert.True(surface.TryDrawRecordedCommands(canvas));
        Assert.Equal(new SKColor(51, 153, 230, 255), target.GetPixel(4, 3));
        Assert.Equal((byte)0, target.GetPixel(0, 0).Alpha);
        Assert.Equal(SurfaceRecordingState.Sealed, surface.RecordingState);

        bool automaticRecording = Surface.AutomaticRecordingEnabled;
        try
        {
            Surface.ConfigureAutomaticRecording(true);
            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => _ = surface.Handle);
            Assert.Contains("Cairo materialization is forbidden", exception.Message);
            Assert.Equal(SurfaceRecordingState.Sealed, surface.RecordingState);
        }
        finally
        {
            Surface.ConfigureAutomaticRecording(automaticRecording);
        }
    }

    [Fact]
    public void ManagedGuiSurfaceCompositesImageAndPictureOverlaysWithoutCairo()
    {
        using var surface = ImageSurface.CreateForRecordedGui(Format.Argb32, 10, 6);
        using var bitmap = new SKBitmap(new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        bitmap.Erase(SKColors.Lime);
        Assert.True(surface.RecordImage(bitmap, 1, 1, 2, 2));

        using var pictureRecorder = new SKPictureRecorder();
        SKCanvas pictureCanvas = pictureRecorder.BeginRecording(SKRect.Create(3, 3));
        pictureCanvas.Clear(SKColors.Transparent);
        pictureCanvas.DrawColor(SKColors.Magenta);
        using SKPicture picture = pictureRecorder.EndRecording();
        Assert.True(surface.RecordPicture(picture, 5, 1, 3, 3, null,
            _ => throw new Xunit.Sdk.XunitException("Skia overlay replay invoked the native Cairo callback.")));

        using var target = new SKBitmap(new SKImageInfo(10, 6, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target)) Assert.True(surface.TryDrawRecordedCommands(canvas));

        Assert.Equal(SKColors.Lime, target.GetPixel(1, 1));
        Assert.Equal(SKColors.Magenta, target.GetPixel(6, 2));
        Assert.Equal((byte)0, target.GetPixel(0, 0).Alpha);
        Assert.Equal(SurfaceRecordingState.Sealed, surface.RecordingState);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinearAndRadialGradientsRecordAndRender(bool radial)
    {
        using var surface = ImageSurface.CreateForRecordedGui(Format.Argb32, 12, 6);
        using Pattern gradient = radial
            ? new RadialGradient(6, 3, 0, 6, 3, 6)
            : new LinearGradient(0, 3, 12, 3);
        Assert.Equal(Status.Success, ((Gradient)gradient).AddColorStop(0, new Color(1, 0, 0, 1)));
        Assert.Equal(Status.Success, ((Gradient)gradient).AddColorStop(1, new Color(0, 0, 1, 1)));
        gradient.Extend = Extend.Pad;
        gradient.Matrix = new Matrix();
        Assert.Equal(radial ? PatternType.Radial : PatternType.Linear, gradient.PatternType);
        Assert.Equal(2, ((Gradient)gradient).ColorStopCount);
        Assert.True(gradient.Matrix.IsIdentity());
        Assert.Equal(!CairoNativeGuard.NativeAllowed, !gradient.HandleValid);

        using (var context = new Context(surface))
        {
            context.SetSource(gradient);
            if (!CairoNativeGuard.NativeAllowed)
            {
                ((Gradient)gradient).AddColorStop(.5, new Color(0, 1, 0, 1));
                gradient.Matrix = new Matrix(1, 0, 0, 1, 1000, 1000);
            }
            context.Rectangle(1, 1, 10, 4);
            context.Fill();
        }

        if (!CairoNativeGuard.NativeAllowed)
            Assert.Throws<NotSupportedException>(() => _ = gradient.Handle);
        gradient.Dispose();

        using var target = new SKBitmap(new SKImageInfo(12, 6, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(target)) Assert.True(surface.TryDrawRecordedCommands(canvas));

        SKColor first = target.GetPixel(radial ? 6 : 2, 3);
        SKColor last = target.GetPixel(radial ? 10 : 9, 3);
        Assert.True(first.Red > first.Blue, $"Expected the first gradient sample to be red-dominant, got {first}.");
        Assert.True(last.Blue > last.Red, $"Expected the last gradient sample to be blue-dominant, got {last}.");
        Assert.Equal((byte)0, target.GetPixel(0, 0).Alpha);
        Assert.Equal(SurfaceRecordingState.Sealed, surface.RecordingState);
    }
}
