using System;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using SkiaSharp;

namespace Cairo
{
	internal enum SurfaceRecordingState
	{
		Native,
		Recording,
		NativeMaterialized,
		Failed,
		Sealed,
		GpuRendered,
		Disposed
	}

	/// <summary>Immutable command stream for a newly-created ARGB32 image surface.</summary>
	internal sealed class SurfaceRecorder
	{
		readonly Surface surface;
		readonly List<Command> commands = new List<Command> ();
		readonly object sync = new object ();
		SurfaceRecordingState state = SurfaceRecordingState.Recording;
		ExceptionDispatchInfo failure;
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

		internal bool RecordText(byte[] text, bool pathOnly)
		{
			byte[] copy = (byte[])text.Clone();
			if (pathOnly) return RecordNative(c => c.TextPath(copy), shadowReplay: c => RecordedPreparationCache.AppendTextPath(c, copy));
			Context context = Shadow;
			using (var prepared = RecordedPreparationCache.PrepareText(context, copy)) {
				if (prepared != null) {
					PointD point = context.HasCurrentPoint ? context.CurrentPoint : new PointD(0, 0);
					var drawing = RecordedGuiDrawing.Capture(context, false, false, clips.ToArray(), textPath: new SKPath(prepared.Outline), sourceMatrix: sourceMatrix, sourceSnapshot: currentSource?.Capture());
					double advanceX = prepared.Extents.XAdvance, advanceY = prepared.Extents.YAdvance;
					if (!Add(new Command(c => drawing.Replay(c, target => target.ShowText(copy)), c => c.MoveTo(point.X + advanceX, point.Y + advanceY), drawing))) { drawing.Dispose(); return false; }
					resources.Add(new RecordedResource(drawing)); return true;
				}
			}
			using (Path oldPath = context.CopyPath()) {
				PointD point = context.HasCurrentPoint ? context.CurrentPoint : new PointD(0, 0);
				TextExtents extents = context.TextExtents(copy);
				context.NewPath(); context.MoveTo(point); context.TextPath(copy);
				RecordedGuiDrawing drawing;
				try { drawing = RecordedGuiDrawing.Capture(context, false, false, clips.ToArray(), sourceMatrix: sourceMatrix, sourceSnapshot: currentSource?.Capture()); }
				finally { context.NewPath(); context.AppendPath(oldPath); }
				if (!Add(new Command(c => drawing.Replay(c, target => target.ShowText(copy)), c => c.MoveTo(point.X + extents.XAdvance, point.Y + extents.YAdvance), drawing))) { drawing.Dispose(); return false; }
				resources.Add(new RecordedResource(drawing));
				return true;
			}
		}

		internal bool RecordGlyphs(Glyph[] glyphs, bool pathOnly)
		{
			Glyph[] copy = (Glyph[])glyphs.Clone();
			if (pathOnly) return RecordNative(c => c.GlyphPath(copy), shadowReplay: c => RecordedPreparationCache.AppendTextPath(c, null, copy));
			Context context = Shadow;
			using (var prepared = RecordedPreparationCache.PrepareText(context, null, copy)) {
				if (prepared != null) {
					var drawing = RecordedGuiDrawing.Capture(context, false, false, clips.ToArray(), textPath: new SKPath(prepared.Outline), sourceMatrix: sourceMatrix, sourceSnapshot: currentSource?.Capture());
					if (!Add(new Command(c => drawing.Replay(c, target => target.ShowGlyphs(copy)), c => { }, drawing))) { drawing.Dispose(); return false; }
					resources.Add(new RecordedResource(drawing)); return true;
				}
			}
			using (Path oldPath = context.CopyPath()) {
				context.NewPath(); context.GlyphPath(copy);
				RecordedGuiDrawing drawing;
				try { drawing = RecordedGuiDrawing.Capture(context, false, false, clips.ToArray(), sourceMatrix: sourceMatrix, sourceSnapshot: currentSource?.Capture()); }
				finally { context.NewPath(); context.AppendPath(oldPath); }
				if (!Add(new Command(c => drawing.Replay(c, target => target.ShowGlyphs(copy)), c => { }, drawing))) { drawing.Dispose(); return false; }
				resources.Add(new RecordedResource(drawing)); return true;
			}
		}

		void ClearCommands()
		{
			commands.Clear(); shadow?.Dispose(); shadow = null; shadowIndex = 0;
			foreach (IDisposable resource in resources) resource.Dispose();
			resources.Clear(); clips.Clear(); savedClips.Clear(); savedSources.Clear();
			foreach (var binding in sourceBindings) binding.Dispose();
			sourceBindings.Clear(); currentSource = null; savedSurfaceSources.Clear();
		}

		internal SurfaceRecorder (Surface surface) { this.surface = surface; width = NativeMethods.cairo_image_surface_get_width(surface.NativeHandleForMetadata); height = NativeMethods.cairo_image_surface_get_height(surface.NativeHandleForMetadata); }
		internal SurfaceRecorderSnapshot CaptureSnapshot() { lock (sync) { if (state != SurfaceRecordingState.Recording && state != SurfaceRecordingState.Sealed && state != SurfaceRecordingState.GpuRendered) return null; return new SurfaceRecorderSnapshot(commands.ToArray(), width, height, resources.ToArray(), Identity); } }
		internal SurfaceRecordingState State { get { lock (sync) return state; } }

		internal bool TrySeal (out SurfaceRecorderSnapshot snapshot)
		{
			lock (sync) {
				if (state != SurfaceRecordingState.Recording && state != SurfaceRecordingState.GpuRendered) { snapshot = null; return false; }
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
		internal bool RecordFill () => RecordDrawing(c => c.Fill(), c => c.NewPath());
		internal bool RecordSave () { savedClips.Push(clips.ToArray()); return RecordNative(c => c.Save(), shadowReplay: c => { c.Save(); savedSources.Push((Matrix)sourceMatrix.Clone()); savedSurfaceSources.Push(currentSource); }); }
		internal bool RecordRestore () { if (savedClips.Count > 0) { clips.Clear(); clips.AddRange(savedClips.Pop()); } return RecordNative(c => c.Restore(), shadowReplay: c => { c.Restore(); if (savedSources.Count > 0) sourceMatrix = savedSources.Pop(); if (savedSurfaceSources.Count > 0) currentSource = savedSurfaceSources.Pop(); }); }
		internal bool RecordAntialias (Antialias value)
		{
			return Add (new Command (CommandKind.SetAntialias, (double)value));
		}
		internal bool RecordOperator (Operator value)
		{
			if (value == Operator.Saturate) return false;
			return Add (new Command (CommandKind.SetOperator, (double)value));
		}

		internal void FillRectangle (double x, double y, double width, double height, double red, double green, double blue, double alpha)
		{
			if (!RecordSetSourceRGBA (red, green, blue, alpha) || !RecordRectangle (x, y, width, height) || !RecordFill ())
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
			lock (sync) {
				if (state == SurfaceRecordingState.Failed) failure.Throw ();
				if (state != SurfaceRecordingState.Recording && state != SurfaceRecordingState.Sealed && state != SurfaceRecordingState.GpuRendered) return;
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
					ClearCommands ();
					state = SurfaceRecordingState.NativeMaterialized;
				} catch (Exception exception) {
					ClearCommands ();
					failure = ExceptionDispatchInfo.Capture (exception);
					state = SurfaceRecordingState.Failed;
					throw;
				}
			}
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
				using (var bitmap = new SKBitmap (new SKImageInfo (width, height, SKColorType.Bgra8888, SKAlphaType.Premul))) {
					using (var raster = new SKCanvas (bitmap)) {
						raster.Clear (SKColors.Transparent);
						foreach (SurfaceRecorder.Command command in commands) {
							command.Drawing?.Draw (raster);
							if (command.Blur != null) { raster.Flush (); command.Blur.ApplyToPixels (bitmap.GetPixels (), width, height); bitmap.NotifyPixelsChanged (); }
							if (command.Demultiply) { raster.Flush(); SurfaceTransformDemulAlpha.ApplyToPixels(bitmap.GetPixels(), width, height); bitmap.NotifyPixelsChanged(); }
							if (command.Image != null) {
								raster.Flush();
								using (var surface = new ImageSurface(bitmap.GetPixels(), Format.Argb32, width, height, bitmap.RowBytes))
									surface.Image(command.Image.Bitmap, command.Image.X, command.Image.Y, command.Image.Width, command.Image.Height);
								bitmap.NotifyPixelsChanged();
								}
							if (command.Picture != null) {
								raster.Flush();
								using (var surface = new ImageSurface(bitmap.GetPixels(), Format.Argb32, width, height, bitmap.RowBytes))
								using (var context = new Context(surface)) command.Picture.NativeReplay(context);
								bitmap.NotifyPixelsChanged();
							}
						}
					}
					using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
						canvas.DrawBitmap (bitmap, 0, 0, paint);
				}
			} finally { canvas.RestoreToCount(save); }
		}
	}
}
