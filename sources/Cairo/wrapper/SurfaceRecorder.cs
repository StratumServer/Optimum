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
		readonly List<IDisposable> resources = new List<IDisposable>();
		readonly List<RecordedGuiClip> clips = new List<RecordedGuiClip>();
		readonly Stack<RecordedGuiClip[]> savedClips = new Stack<RecordedGuiClip[]>();

		internal Context Shadow {
			get {
				surface.EnsureRecordingOwnerThread();
				if (shadow == null) shadow = new Context(NativeMethods.cairo_create(surface.NativeHandleForRecorder), true);
				while (shadowIndex < commands.Count) commands[shadowIndex++].Replay(shadow, false);
				return shadow;
			}
		}

		internal bool RecordNative(Action<Context> replay, IDisposable resource = null, Action<Context> shadowReplay = null, bool bindsSource = false)
		{
			Action<Context> update = shadowReplay ?? replay;
			if (bindsSource) { var operation = update; update = c => { operation(c); sourceMatrix = c.Matrix; }; }
			bool added = Add(new Command(replay, update));
			if (added && resource != null) resources.Add(resource);
			return added;
		}

		internal bool RecordDrawing(Action<Context> replay, Action<Context> shadowReplay, bool stroke = false, bool paintAll = false, double alpha = 1)
		{
			if (State != SurfaceRecordingState.Recording) return false;
			RecordedGuiDrawing drawing = RecordedGuiDrawing.Capture(Shadow, stroke, paintAll, clips.ToArray(), alpha: alpha, sourceMatrix: sourceMatrix);
			if (!Add(new Command(c => drawing.Replay(c, replay), shadowReplay, drawing))) { drawing.Dispose(); return false; }
			resources.Add(drawing);
			return true;
		}

		internal bool RecordClip(bool preserve)
		{
			if (State != SurfaceRecordingState.Recording) return false;
			var clip = new RecordedGuiClip(Shadow);
			clips.Add(clip); resources.Add(clip);
			return RecordNative(preserve ? c => c.ClipPreserve() : c => c.Clip());
		}
		internal bool RecordResetClip() { clips.Clear(); return RecordNative(c => c.ResetClip()); }

		internal bool RecordText(byte[] text, bool pathOnly)
		{
			byte[] copy = (byte[])text.Clone();
			if (pathOnly) return RecordNative(c => c.TextPath(copy));
			Context context = Shadow;
			using (Path oldPath = context.CopyPath()) {
				PointD point = context.HasCurrentPoint ? context.CurrentPoint : new PointD(0, 0);
				TextExtents extents = context.TextExtents(copy);
				context.NewPath(); context.MoveTo(point); context.TextPath(copy);
				RecordedGuiDrawing drawing;
				try { drawing = RecordedGuiDrawing.Capture(context, false, false, clips.ToArray()); }
				finally { context.NewPath(); context.AppendPath(oldPath); }
				if (!Add(new Command(c => drawing.Replay(c, target => target.ShowText(copy)), c => c.MoveTo(point.X + extents.XAdvance, point.Y + extents.YAdvance), drawing))) { drawing.Dispose(); return false; }
				resources.Add(drawing);
				return true;
			}
		}

		internal bool RecordGlyphs(Glyph[] glyphs, bool pathOnly)
		{
			Glyph[] copy = (Glyph[])glyphs.Clone();
			if (pathOnly) return RecordNative(c => c.GlyphPath(copy));
			Context context = Shadow;
			using (Path oldPath = context.CopyPath()) {
				context.NewPath(); context.GlyphPath(copy);
				RecordedGuiDrawing drawing;
				try { drawing = RecordedGuiDrawing.Capture(context, false, false, clips.ToArray(), sourceMatrix: sourceMatrix); }
				finally { context.NewPath(); context.AppendPath(oldPath); }
				if (!Add(new Command(c => drawing.Replay(c, target => target.ShowGlyphs(copy)), c => { }, drawing))) { drawing.Dispose(); return false; }
				resources.Add(drawing); return true;
			}
		}

		void ClearCommands()
		{
			commands.Clear(); shadow?.Dispose(); shadow = null; shadowIndex = 0;
			foreach (IDisposable resource in resources) resource.Dispose();
			resources.Clear(); clips.Clear(); savedClips.Clear(); savedSources.Clear();
		}

		internal SurfaceRecorder (Surface surface) { this.surface = surface; }
		internal SurfaceRecordingState State { get { lock (sync) return state; } }

		internal bool TrySeal (out SurfaceRecorderSnapshot snapshot)
		{
			surface.EnsureRecordingOwnerThread ();
			lock (sync) {
				if (state != SurfaceRecordingState.Recording) { snapshot = null; return false; }
				state = SurfaceRecordingState.Sealed;
				snapshot = new SurfaceRecorderSnapshot (commands.ToArray (), NativeMethods.cairo_image_surface_get_width (surface.NativeHandleForMetadata), NativeMethods.cairo_image_surface_get_height (surface.NativeHandleForMetadata));
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

		internal bool RecordSetSourceRGBA (double r, double g, double b, double a) => RecordNative(c => c.SetSourceRGBA(r, g, b, a), bindsSource: true);
		internal bool RecordPaint () => RecordDrawing(c => c.Paint(), c => { }, paintAll: true);
		internal bool RecordBlur (double range, int edge, int x1, int y1, int x2, int y2, bool full)
		{
			if (State != SurfaceRecordingState.Recording) return false;
			var blur = new RecordedBlur (range, edge, x1, y1, x2, y2, full);
			IntPtr native = surface.NativeHandleForRecorder;
			return Add (new Command (c => blur.ApplyToSurface (native), c => { }, null, blur));
		}
		internal bool RecordRectangle (double x, double y, double width, double height) => Add (new Command (CommandKind.Rectangle, x, y, width, height));
		internal bool RecordFill () => RecordDrawing(c => c.Fill(), c => c.NewPath());
		internal bool RecordSave () { savedClips.Push(clips.ToArray()); return RecordNative(c => c.Save(), shadowReplay: c => { c.Save(); savedSources.Push((Matrix)sourceMatrix.Clone()); }); }
		internal bool RecordRestore () { if (savedClips.Count > 0) { clips.Clear(); clips.AddRange(savedClips.Pop()); } return RecordNative(c => c.Restore(), shadowReplay: c => { c.Restore(); if (savedSources.Count > 0) sourceMatrix = savedSources.Pop(); }); }
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
					IntPtr native = surface.NativeHandleForRecorder;
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
			internal Command(Action<Context> replay, Action<Context> shadowReplay, RecordedGuiDrawing drawing = null, RecordedBlur blur = null) { Kind = CommandKind.Native; A = B = C = D = 0; NativeReplay = replay; ShadowReplay = shadowReplay; Drawing = drawing; Blur = blur; }
			internal readonly CommandKind Kind;
			internal readonly double A, B, C, D;
			internal Command (CommandKind kind, double a = 0, double b = 0, double c = 0, double d = 0) { Kind = kind; A = a; B = b; C = c; D = d; NativeReplay = ShadowReplay = null; Drawing = null; Blur = null; }
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

	internal sealed class SurfaceRecorderSnapshot
	{
		readonly SurfaceRecorder.Command[] commands;
		readonly int width, height;
		internal SurfaceRecorderSnapshot (SurfaceRecorder.Command[] commands, int width, int height) { this.commands = commands; this.width = width; this.height = height; }

		internal void DrawTo (SKSurface target, RecordedGpuBlur blur)
		{
			if (target == null) throw new ArgumentNullException (nameof (target));
			SKCanvas canvas = target.Canvas;
			int save = canvas.Save ();
			try {
				canvas.Clear (SKColors.Transparent);
				foreach (SurfaceRecorder.Command command in commands) {
					command.Drawing?.Draw (canvas);
					if (command.Blur != null) blur.Apply (command.Blur, target, width, height);
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
				foreach (SurfaceRecorder.Command command in commands) hasBlur |= command.Blur != null;
				if (!hasBlur) { foreach (SurfaceRecorder.Command command in commands) command.Drawing?.Draw(canvas); return; }
				// The game's blur reads and rewrites pixels, so those segments run on a raster layer
				// (still Skia) and the result is composited back onto the target canvas.
				using (var bitmap = new SKBitmap (new SKImageInfo (width, height, SKColorType.Bgra8888, SKAlphaType.Premul))) {
					using (var raster = new SKCanvas (bitmap)) {
						raster.Clear (SKColors.Transparent);
						foreach (SurfaceRecorder.Command command in commands) {
							command.Drawing?.Draw (raster);
							if (command.Blur != null) { raster.Flush (); command.Blur.ApplyToPixels (bitmap.GetPixels (), width, height); bitmap.NotifyPixelsChanged (); }
						}
					}
					using (var paint = new SKPaint { BlendMode = SKBlendMode.Src })
						canvas.DrawBitmap (bitmap, 0, 0, paint);
				}
			} finally { canvas.RestoreToCount(save); }
		}
	}
}
