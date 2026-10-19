using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using System.Text;
using SkiaSharp;

namespace Cairo
{
	internal enum SurfaceRecordingState
	{
		Native,
		Recording,
		NativeMaterialized,
		Materializing,
		Failed,
		Sealed,
		GpuRendered,
		Disposed
	}

	/// <summary>Immutable command stream for a newly-created ARGB32 image surface.</summary>
	internal sealed class SurfaceRecorder
	{
		// Observers are diagnostic-only and never run while the recorder lock is held.
		internal static event Action<string, bool> MaterializationCompleted;

		readonly Surface surface;
		readonly List<Command> commands = new List<Command> ();
		readonly object sync = new object ();
		SurfaceRecordingState state = SurfaceRecordingState.Recording;
		ExceptionDispatchInfo failure;
		string firstMaterializationTrigger;
		Context shadow;
		int shadowIndex;
		Matrix sourceMatrix = new Matrix();
		readonly Stack<Matrix> savedSources = new Stack<Matrix>();
		readonly List<RecordedResource> resources = new List<RecordedResource>();
		readonly List<RecordedSurfaceSource> sourceBindings = new List<RecordedSurfaceSource>();
		RecordedSurfaceSource currentSource;
		readonly Stack<RecordedSurfaceSource> savedSurfaceSources = new Stack<RecordedSurfaceSource>();
		readonly int width, height;
		internal readonly long Identity = System.Threading.Interlocked.Increment(ref nextIdentity);
		static long nextIdentity;
		readonly List<RecordedGuiClip> clips = new List<RecordedGuiClip>();
		readonly Stack<RecordedGuiClip[]> savedClips = new Stack<RecordedGuiClip[]>();
		readonly Stack<Antialias> savedAntialias = new Stack<Antialias>();
		int saveDepth;
		bool unqualifiedHelperDrawing, antialiasConfigured;

		internal Context Shadow {
			get {
				surface.EnsureRecordingOwnerThread();
				var profile = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.Shadow);
				try {
					if (shadow == null) { shadow = new Context(NativeMethods.cairo_create(surface.NativeHandleForRecorder), true); profile.CreatedShadow = true; }
					while (shadowIndex < commands.Count) { commands[shadowIndex++].Replay(shadow, false); profile.ReplayedCommands++; }
					return shadow;
				} finally { profile.Dispose(); }
			}
		}

		internal bool RecordNative(Action<Context> replay, IDisposable resource = null, Action<Context> shadowReplay = null, bool bindsSource = false, RecordedSurfaceSource sourceBinding = null)
		{
			Action<Context> update = shadowReplay ?? replay;
			if (bindsSource) { var operation = update; update = c => { operation(c); sourceMatrix = c.Matrix; currentSource = sourceBinding; }; }
			bool added = Add(new Command(replay, update));
			if (added && resource != null) resources.Add(new RecordedResource(resource));
			if (added && sourceBinding != null) sourceBindings.Add(sourceBinding);
			return added;
		}

		internal bool RecordDrawing(Action<Context> replay, Action<Context> shadowReplay, bool stroke = false, bool paintAll = false, double alpha = 1)
		{
			if (State != SurfaceRecordingState.Recording) return false;
			// These Cairo operators are not bounded by source: scaling source
			// alpha cannot replace their coverage mask. Preserve native replay
			// until the recorder has an equivalent mask compositor.
			if (alpha != 1 && Shadow.Operator is Operator.Clear or Operator.Source
				or Operator.In or Operator.Out or Operator.DestIn or Operator.DestAtop) return false;
			RecordedGuiDrawing drawing = RecordedGuiDrawing.Capture(Shadow, stroke, paintAll, clips.ToArray(), alpha: alpha, sourceMatrix: sourceMatrix, sourceSnapshot: currentSource?.Capture());
			if (!Add(new Command(c => drawing.Replay(c, replay), shadowReplay, drawing))) { drawing.Dispose(); return false; }
			resources.Add(new RecordedResource(drawing));
			return true;
		}

		internal bool RecordClip(bool preserve)
		{
			if (State != SurfaceRecordingState.Recording) return false;
			var clip = new RecordedGuiClip(Shadow);
			clips.Add(clip); resources.Add(new RecordedResource(clip));
			return RecordNative(preserve ? c => c.ClipPreserve() : c => c.Clip());
		}
		internal bool RecordResetClip() { clips.Clear(); return RecordNative(c => c.ResetClip()); }

		internal bool RecordText(Context ownerContext, byte[] text, bool pathOnly)
		{
			using var textPreparation = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.TextPreparation);
			byte[] copy = (byte[])text.Clone();
			int terminator = Array.IndexOf(copy, (byte)0);
			int length = terminator < 0 ? copy.Length : terminator;
			string decoded;
			try { decoded = new UTF8Encoding(false, true).GetString(copy, 0, length); }
			catch (DecoderFallbackException exception) { throw new InvalidOperationException("Skia GUI text requires valid UTF-8; refusing Cairo materialization.", exception); }
			if (pathOnly) return RecordNative(c => c.TextPath(copy), shadowReplay: c => RecordedPreparationCache.AppendTextPath(c, copy));
			Context context = Shadow;
			PointD point = context.HasCurrentPoint ? context.CurrentPoint : new PointD(0, 0);
			using FontOptions options = context.FontOptions;
			SKFont font = CreateSkiaFont(ownerContext, options.Antialias);
			float advance;
			using (SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.TextMeasurement))
				advance = font.MeasureText(decoded);
			var drawing = RecordedGuiDrawing.Capture(context, false, false, clips.ToArray(), sourceMatrix: sourceMatrix,
				sourceSnapshot: currentSource?.Capture(), text: decoded, textFont: font, textX: (float)point.X, textY: (float)point.Y);
			if (!Add(new Command(c => drawing.Replay(c, target => target.ShowText(copy)), c => c.MoveTo(point.X + advance, point.Y), drawing))) { drawing.Dispose(); return false; }
			resources.Add(new RecordedResource(drawing));
			return true;
		}

		internal bool RecordGlyphs(Context ownerContext, Glyph[] glyphs, bool pathOnly)
		{
			Glyph[] copy = (Glyph[])glyphs.Clone();
			if (pathOnly) return RecordNative(c => c.GlyphPath(copy), shadowReplay: c => RecordedPreparationCache.AppendTextPath(c, null, copy));
			Context context = Shadow;
			using FontOptions options = context.FontOptions;
			using SKFont font = CreateSkiaFont(ownerContext, options.Antialias);
			var glyphIndices = new ushort[copy.Length];
			var positions = new SKPoint[copy.Length];
			for (int i = 0; i < copy.Length; i++) {
				if (copy[i].Index < 0 || copy[i].Index > ushort.MaxValue)
					throw new InvalidOperationException("Skia glyph indices are 16-bit; Cairo materialization is disabled for glyph " + copy[i].Index + ".");
				glyphIndices[i] = (ushort)copy[i].Index;
				positions[i] = new SKPoint((float)copy[i].X, (float)copy[i].Y);
			}
			using var blobBuilder = new SKTextBlobBuilder();
			blobBuilder.AddPositionedRun(glyphIndices, font, positions);
			SKTextBlob blob = blobBuilder.Build() ?? throw new InvalidOperationException("Skia failed to create a positioned glyph run.");
			var drawing = RecordedGuiDrawing.Capture(context, false, false, clips.ToArray(), sourceMatrix: sourceMatrix,
				sourceSnapshot: currentSource?.Capture(), textBlob: blob);
			if (!Add(new Command(c => drawing.Replay(c, target => target.ShowGlyphs(copy)), c => { }, drawing))) { drawing.Dispose(); return false; }
			resources.Add(new RecordedResource(drawing));
			return true;
		}

		static SKFont CreateSkiaFont(Context context, Antialias antialias)
		{
			using var fontSelection = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.FontSelection);
			string family = context.RecordingFontFamily;
			if (string.IsNullOrWhiteSpace(family))
				throw new InvalidOperationException("The selected font face has no managed family metadata; Cairo materialization is disabled for recorded text.");
			var weight = context.RecordingFontWeight == FontWeight.Bold ? SKFontStyleWeight.Bold : SKFontStyleWeight.Normal;
			var slant = context.RecordingFontSlant == FontSlant.Italic ? SKFontStyleSlant.Italic :
				context.RecordingFontSlant == FontSlant.Oblique ? SKFontStyleSlant.Oblique : SKFontStyleSlant.Upright;
			using SKTypeface typeface = SKTypeface.FromFamilyName(family, weight, SKFontStyleWidth.Normal, slant)
				?? throw new InvalidOperationException("Skia could not resolve GUI font family '" + family + "'; Cairo materialization is disabled for recorded text.");
			return new SKFont(typeface, (float)context.RecordingFontSize) {
				Edging = antialias == Antialias.Subpixel ? SKFontEdging.SubpixelAntialias :
					antialias == Antialias.None ? SKFontEdging.Alias : SKFontEdging.Antialias,
				Subpixel = true
			};
		}

		void ClearCommands()
		{
			commands.Clear(); shadow?.Dispose(); shadow = null; shadowIndex = 0;
			foreach (IDisposable resource in resources) resource.Dispose();
			resources.Clear(); clips.Clear(); savedClips.Clear(); savedAntialias.Clear(); savedSources.Clear();
			foreach (var binding in sourceBindings) binding.Dispose();
			sourceBindings.Clear(); currentSource = null; savedSurfaceSources.Clear();
		}

		internal SurfaceRecorder (Surface surface) { this.surface = surface; width = NativeMethods.cairo_image_surface_get_width(surface.NativeHandleForMetadata); height = NativeMethods.cairo_image_surface_get_height(surface.NativeHandleForMetadata); }
		internal SurfaceRecorderSnapshot CaptureSnapshot() { lock (sync) { if (state != SurfaceRecordingState.Recording && state != SurfaceRecordingState.Sealed && state != SurfaceRecordingState.GpuRendered) return null; return new SurfaceRecorderSnapshot(commands.ToArray(), width, height, resources.ToArray(), Identity); } }
		internal SurfaceRecordingState State { get { lock (sync) return state; } }
		internal string FirstMaterializationTrigger { get { lock (sync) return firstMaterializationTrigger; } }
		internal void SetFirstMaterializationTrigger(string trigger)
		{
			if (string.IsNullOrEmpty(trigger)) return;
			lock (sync) if ((state == SurfaceRecordingState.Recording || state == SurfaceRecordingState.Sealed || state == SurfaceRecordingState.GpuRendered) && firstMaterializationTrigger == null) firstMaterializationTrigger = trigger;
		}

		internal bool TrySeal (out SurfaceRecorderSnapshot snapshot)
		{
			lock (sync) {
				if (state != SurfaceRecordingState.Recording && state != SurfaceRecordingState.GpuRendered || saveDepth != 0 || unqualifiedHelperDrawing) { snapshot = null; return false; }
				state = SurfaceRecordingState.Sealed;
				snapshot = new SurfaceRecorderSnapshot (commands.ToArray (), width, height, resources.ToArray(), Identity);
				return true;
			}
		}

		internal bool CommitGpuRender ()
		{
			lock (sync) {
				if (state != SurfaceRecordingState.Sealed) return false;
				state = SurfaceRecordingState.GpuRendered;
				return true;
			}
		}

		internal void ResumeRecording() { lock (sync) { if (state == SurfaceRecordingState.GpuRendered) state = SurfaceRecordingState.Recording; } }

		internal bool RecordSetSourceRGBA (double r, double g, double b, double a) => RecordNative(c => c.SetSourceRGBA(r, g, b, a), bindsSource: true);
		internal bool RecordPaint () => RecordDrawing(c => c.Paint(), c => { }, paintAll: true);
		internal bool RecordBlur (double range, int edge, int x1, int y1, int x2, int y2, bool full)
		{
			if (State != SurfaceRecordingState.Recording) return false;
			var blur = new RecordedBlur (range, edge, x1, y1, x2, y2, full);
			return Add (new Command (c => blur.ApplyToSurface (NativeMethods.cairo_get_target(c.NativeHandleForRecorder)), c => { }, null, blur));
		}
		internal bool RecordRectangle (double x, double y, double width, double height) => Add (new Command (CommandKind.Rectangle, x, y, width, height));
		internal bool RecordDemultiplyAlpha() => Add(new Command(c => {
			IntPtr target = NativeMethods.cairo_get_target(c.NativeHandleForRecorder);
			NativeMethods.cairo_surface_flush(target);
			SurfaceTransformDemulAlpha.ApplyToPixels(NativeMethods.cairo_image_surface_get_data(target), width, height);
			NativeMethods.cairo_surface_mark_dirty(target);
		}, c => { }, demultiply: true));
		internal bool RecordImage(SKBitmap bitmap, int x, int y, int width, int height)
		{
			if (State != SurfaceRecordingState.Recording) return false;
			var image = new RecordedGuiImage(bitmap, x, y, width, height);
			if (!Add(new Command(c => image.Replay(NativeMethods.cairo_get_target(c.NativeHandleForRecorder)), c => { }, image: image))) { image.Dispose(); return false; }
			resources.Add(new RecordedResource(image)); return true;
		}
		internal bool RecordPicture(SKPicture picture, int x, int y, int width, int height, int? tint, Action<Context> replay, bool textureColorOrder, RecordedPreparationCache.Picture pictureOwner = null)
		{
			if (State != SurfaceRecordingState.Recording) return false;
			var drawing = new RecordedGuiPicture(picture, x, y, width, height, tint, replay, textureColorOrder, pictureOwner?.Retain());
			if (!Add(new Command(replay, c => { }, picture: drawing))) { drawing.Dispose(); return false; }
			resources.Add(new RecordedResource(drawing)); return true;
		}
		internal bool RecordFill ()
		{
			return RecordDrawing(c => c.Fill(), c => c.NewPath());
		}
		internal bool RecordSave () { Antialias antialias = Shadow.Antialias; bool recorded = RecordNative(c => c.Save(), shadowReplay: c => { c.Save(); savedSources.Push((Matrix)sourceMatrix.Clone()); savedSurfaceSources.Push(currentSource); }); if (recorded) { savedClips.Push(clips.ToArray()); savedAntialias.Push(antialias); saveDepth++; } return recorded; }
		internal bool RecordRestore () { if (saveDepth == 0) return false; savedAntialias.Pop(); clips.Clear(); clips.AddRange(savedClips.Pop()); saveDepth--; return RecordNative(c => c.Restore(), shadowReplay: c => { c.Restore(); if (savedSources.Count > 0) sourceMatrix = savedSources.Pop(); if (savedSurfaceSources.Count > 0) currentSource = savedSurfaceSources.Pop(); }); }
		internal bool RecordAntialias (Antialias value)
		{
			bool recorded = Add (new Command (CommandKind.SetAntialias, (double)value));
			if (recorded && value != Antialias.Default) antialiasConfigured = true;
			return recorded;
		}
		internal bool RecordOperator (Operator value)
		{
			if (value == Operator.Saturate) return false;
			return Add (new Command (CommandKind.SetOperator, (double)value));
		}

		internal void FillRectangle (double x, double y, double width, double height, double red, double green, double blue, double alpha)
		{
			unqualifiedHelperDrawing |= !antialiasConfigured;
			if (!RecordSetSourceRGBA (red, green, blue, alpha) || !RecordRectangle (x, y, width, height) || !RecordDrawing(c => c.Fill(), c => c.NewPath()))
				throw new InvalidOperationException ("Recorder is no longer recording.");
		}

		bool Add (Command command)
		{
			surface.EnsureRecordingOwnerThread ();
			lock (sync) {
				if (state == SurfaceRecordingState.Disposed) throw new ObjectDisposedException (nameof (SurfaceRecorder));
				if (state == SurfaceRecordingState.Failed) failure.Throw ();
				if (state != SurfaceRecordingState.Recording) return false;
				commands.Add (command);
				return true;
			}
		}

		internal void Materialize (IntPtr contextHandle = default)
		{
			string trigger = null;
			ExceptionDispatchInfo materializationFailure = null;
			bool attempted = false;
			lock (sync) {
				if (state == SurfaceRecordingState.Failed) failure.Throw();
				if (state != SurfaceRecordingState.Recording && state != SurfaceRecordingState.Sealed && state != SurfaceRecordingState.GpuRendered) return;
				attempted = true;
				trigger = firstMaterializationTrigger ?? "Unknown";
				state = SurfaceRecordingState.Materializing;
				try {
					SurfaceRecordingDiagnostics.Materialize();
					IntPtr native = surface.NativeHandleForMetadata;
					ThrowIfError (NativeMethods.cairo_surface_status (native), "surface before replay");
					IntPtr replayHandle = contextHandle == IntPtr.Zero ? NativeMethods.cairo_create (native) : contextHandle;
					using (var context = new Context (replayHandle, contextHandle == IntPtr.Zero)) {
						ThrowIfError (context.Status, "context creation");
						for (int i = 0; i < commands.Count; i++) {
							commands[i].Replay (context);
							ThrowIfError (context.Status, "command replay");
							ThrowIfError (NativeMethods.cairo_surface_status (native), "surface replay");
						}
					}
					ClearCommands();
					state = SurfaceRecordingState.NativeMaterialized;
				} catch (Exception exception) {
					ClearCommands();
					failure = ExceptionDispatchInfo.Capture (exception);
					state = SurfaceRecordingState.Failed;
					materializationFailure = failure;
				}
			}
			if (attempted) {
				try { MaterializationCompleted?.Invoke(trigger, materializationFailure == null); }
				catch { /* Diagnostics must not change Cairo behavior. */ }
			}
			materializationFailure?.Throw();
		}

		internal void DisposeFromSurface ()
		{
			lock (sync) { ClearCommands (); state = SurfaceRecordingState.Disposed; }
		}

		static void ThrowIfError (Status status, string phase)
		{
			if (status != Status.Success) throw new InvalidOperationException ("Cairo " + phase + " failed with status " + status + ".");
		}

		internal readonly struct Command
		{
			internal readonly Action<Context> NativeReplay, ShadowReplay;
			internal readonly RecordedGuiDrawing Drawing;
			internal readonly RecordedBlur Blur;
			internal readonly bool Demultiply;
			internal readonly RecordedGuiImage Image;
			internal readonly RecordedGuiPicture Picture;
			internal Command(Action<Context> replay, Action<Context> shadowReplay, RecordedGuiDrawing drawing = null, RecordedBlur blur = null, bool demultiply = false, RecordedGuiImage image = null, RecordedGuiPicture picture = null) { Kind = CommandKind.Native; A = B = C = D = 0; NativeReplay = replay; ShadowReplay = shadowReplay; Drawing = drawing; Blur = blur; Demultiply = demultiply; Image = image; Picture = picture; }
			internal readonly CommandKind Kind;
			internal readonly double A, B, C, D;
			internal Command (CommandKind kind, double a = 0, double b = 0, double c = 0, double d = 0) { Kind = kind; A = a; B = b; C = c; D = d; NativeReplay = ShadowReplay = null; Drawing = null; Blur = null; Demultiply = false; Image = null; Picture = null; }
			internal void Replay (Context context, bool draw = true)
			{
				switch (Kind) {
				case CommandKind.Native: if (draw) NativeReplay(context); else ShadowReplay(context); break;
				case CommandKind.SetSourceRGBA: context.SetSourceRGBA (A, B, C, D); break;
				case CommandKind.Paint: if (draw) context.Paint (); break;
				case CommandKind.Rectangle: context.Rectangle (A, B, C, D); break;
				case CommandKind.Fill: if (draw) context.Fill (); else context.NewPath(); break;
				case CommandKind.Save: context.Save (); break;
				case CommandKind.Restore: context.Restore (); break;
				case CommandKind.SetAntialias: context.Antialias = (Antialias)A; break;
				case CommandKind.SetOperator: context.Operator = (Operator)A; break;
				}
			}
		}

		internal enum CommandKind { Native, SetSourceRGBA, Paint, Rectangle, Fill, Save, Restore, SetAntialias, SetOperator }
	}

	internal sealed class SurfaceRecorderSnapshot : IDisposable
	{
		readonly SurfaceRecorder.Command[] commands;
		readonly int width, height;
		readonly RecordedResource[] resources;
		internal int Width => width;
		internal int Height => height;
		internal long Identity { get; }
		internal int Generation => commands.Length;
		bool disposed;
		internal SurfaceRecorderSnapshot (SurfaceRecorder.Command[] commands, int width, int height, RecordedResource[] resources, long identity) { this.commands = commands; this.width = width; this.height = height; this.resources = resources; Identity = identity; foreach (var resource in resources) resource.Retain(); }
		internal SurfaceRecorderSnapshot Retain() => new SurfaceRecorderSnapshot(commands, width, height, resources, Identity);
		internal void ReplayNative(IntPtr target) { using (var context = new Context(NativeMethods.cairo_create(target), true)) { context.Operator = Operator.Clear; context.Paint(); context.Operator = Operator.Over; foreach (var command in commands) command.Replay(context); } }
		internal void ReplayNative(Context context) { SurfaceRecordingDiagnostics.Materialize(); foreach (var command in commands) command.Replay(context); }
		public void Dispose() { if (disposed) return; disposed = true; foreach (var resource in resources) resource.Dispose(); }

		internal void DrawTo (SKSurface target, RecordedGpuBlur blur)
		{
			using var profile = SurfaceRecordingDiagnostics.Profile(SurfaceRecordingDiagnostics.ProfileStage.GpuReplay);
			if (target == null) throw new ArgumentNullException (nameof (target));
			SKCanvas canvas = target.Canvas;
			int save = canvas.Save ();
			try {
				canvas.Clear (SKColors.Transparent);
				foreach (SurfaceRecorder.Command command in commands) {
					command.Drawing?.Draw (canvas, blur);
					if (command.Blur != null) blur.Apply (command.Blur, target, width, height);
					if (command.Demultiply) blur.Demultiply(target, width, height);
					if (command.Image != null) blur.DrawImage(command.Image, target, width, height);
					if (command.Picture != null) blur.DrawPicture(command.Picture, target, width, height);
				}
			} finally { canvas.RestoreToCount (save); }
		}

		internal void DrawTo (SKCanvas canvas)
		{
			if (canvas == null) throw new ArgumentNullException(nameof(canvas));
			int save = canvas.Save();
			try {
				canvas.Clear(SKColors.Transparent);
				bool hasBlur = false;
				foreach (SurfaceRecorder.Command command in commands) hasBlur |= command.Blur != null || command.Demultiply || command.Image != null || command.Picture != null;
				if (!hasBlur) { foreach (SurfaceRecorder.Command command in commands) command.Drawing?.Draw(canvas); return; }
				// The game's blur reads and rewrites pixels, so those segments run on a raster layer
				// (still Skia) and the result is composited back onto the target canvas.
				SKRuntimeEffect overlayEffect = null;
				try {
				using (var bitmap = new SKBitmap (new SKImageInfo (width, height, SKColorType.Bgra8888, SKAlphaType.Premul))) {
					using (var properties = new SKSurfaceProperties (SKPixelGeometry.RgbHorizontal))
					using (var rasterSurface = SKSurface.Create(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, properties)) {
						if (rasterSurface == null) throw new InvalidOperationException("Skia could not create a software GUI surface with pixel geometry.");
						SKCanvas raster = rasterSurface.Canvas;
						raster.Clear (SKColors.Transparent);
						foreach (SurfaceRecorder.Command command in commands) {
							command.Drawing?.Draw (raster);
							if (command.Blur != null) { raster.Flush (); command.Blur.ApplyToPixels (bitmap.GetPixels (), width, height); bitmap.NotifyPixelsChanged (); }
							if (command.Demultiply) { raster.Flush(); SurfaceTransformDemulAlpha.ApplyToPixels(bitmap.GetPixels(), width, height); bitmap.NotifyPixelsChanged(); }
							if (command.Image != null) {
								raster.Flush();
								overlayEffect ??= RecordedGpuBlur.CreateImageOverlayEffect("CPU image");
								RecordedGpuBlur.DrawImageCpu(command.Image, rasterSurface, width, height, overlayEffect);
								bitmap.NotifyPixelsChanged();
							}
							if (command.Picture != null) {
								raster.Flush();
								overlayEffect ??= RecordedGpuBlur.CreateImageOverlayEffect("CPU picture");
								RecordedGpuBlur.DrawPictureCpu(command.Picture, rasterSurface, width, height, overlayEffect);
								bitmap.NotifyPixelsChanged();
							}
						}
					}
					using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
						canvas.DrawBitmap (bitmap, 0, 0, paint);
				}
				} finally { overlayEffect?.Dispose(); }
			} finally { canvas.RestoreToCount(save); }
		}
	}
}
