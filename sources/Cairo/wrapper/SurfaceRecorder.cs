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

		internal SurfaceRecorder (Surface surface) { this.surface = surface; }
		internal SurfaceRecordingState State { get { lock (sync) return state; } }

		internal bool TrySeal (out SurfaceRecorderSnapshot snapshot)
		{
			surface.EnsureRecordingOwnerThread ();
			lock (sync) {
				if (state != SurfaceRecordingState.Recording) { snapshot = null; return false; }
				state = SurfaceRecordingState.Sealed;
				snapshot = new SurfaceRecorderSnapshot (commands.ToArray ());
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

		internal bool RecordSetSourceRGBA (double r, double g, double b, double a) => Add (new Command (CommandKind.SetSourceRGBA, r, g, b, a));
		internal bool RecordPaint () => Add (new Command (CommandKind.Paint));
		internal bool RecordRectangle (double x, double y, double width, double height) => Add (new Command (CommandKind.Rectangle, x, y, width, height));
		internal bool RecordFill () => Add (new Command (CommandKind.Fill));
		internal bool RecordSave () => Add (new Command (CommandKind.Save));
		internal bool RecordRestore () => Add (new Command (CommandKind.Restore));
		internal bool RecordAntialias (Antialias value)
		{
			if (value == Antialias.Subpixel) return false;
			return Add (new Command (CommandKind.SetAntialias, (double)value));
		}
		internal bool RecordOperator (Operator value)
		{
			if (value != Operator.Over && value != Operator.Source) return false;
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
					commands.Clear ();
					state = SurfaceRecordingState.NativeMaterialized;
				} catch (Exception exception) {
					commands.Clear ();
					failure = ExceptionDispatchInfo.Capture (exception);
					state = SurfaceRecordingState.Failed;
					throw;
				}
			}
		}

		internal void DisposeFromSurface ()
		{
			lock (sync) { commands.Clear (); state = SurfaceRecordingState.Disposed; }
		}

		static void ThrowIfError (Status status, string phase)
		{
			if (status != Status.Success) throw new InvalidOperationException ("Cairo " + phase + " failed with status " + status + ".");
		}

		internal readonly struct Command
		{
			internal readonly CommandKind Kind;
			internal readonly double A, B, C, D;
			internal Command (CommandKind kind, double a = 0, double b = 0, double c = 0, double d = 0) { Kind = kind; A = a; B = b; C = c; D = d; }
			internal void Replay (Context context)
			{
				switch (Kind) {
				case CommandKind.SetSourceRGBA: context.SetSourceRGBA (A, B, C, D); break;
				case CommandKind.Paint: context.Paint (); break;
				case CommandKind.Rectangle: context.Rectangle (A, B, C, D); break;
				case CommandKind.Fill: context.Fill (); break;
				case CommandKind.Save: context.Save (); break;
				case CommandKind.Restore: context.Restore (); break;
				case CommandKind.SetAntialias: context.Antialias = (Antialias)A; break;
				case CommandKind.SetOperator: context.Operator = (Operator)A; break;
				}
			}
		}

		internal enum CommandKind { SetSourceRGBA, Paint, Rectangle, Fill, Save, Restore, SetAntialias, SetOperator }
	}

	internal sealed class SurfaceRecorderSnapshot
	{
		readonly SurfaceRecorder.Command[] commands;
		internal SurfaceRecorderSnapshot (SurfaceRecorder.Command[] commands) { this.commands = commands; }

		internal void DrawTo (SKCanvas canvas)
		{
			if (canvas == null) throw new ArgumentNullException (nameof (canvas));
			canvas.Clear (SKColors.Transparent);
			var path = new SKPath { FillType = SKPathFillType.Winding };
			var states = new Stack<DrawState> ();
			SKColor color = new SKColor (0, 0, 0, 255);
			SKBlendMode blend = SKBlendMode.SrcOver;
			bool antialias = true;
			try {
				for (int i = 0; i < commands.Length; i++) {
					var command = commands[i];
					switch (command.Kind) {
					case SurfaceRecorder.CommandKind.SetSourceRGBA:
						color = ToColor (command.A, command.B, command.C, command.D);
						break;
					case SurfaceRecorder.CommandKind.Paint:
						using (var paint = new SKPaint { Color = color, BlendMode = blend, IsAntialias = antialias }) canvas.DrawPaint (paint);
						break;
					case SurfaceRecorder.CommandKind.Rectangle:
						float x = (float)command.A, y = (float)command.B, right = (float)(command.A + command.C), bottom = (float)(command.B + command.D);
						var rect = new SKRect (Math.Min (x, right), Math.Min (y, bottom), Math.Max (x, right), Math.Max (y, bottom));
						if (rect.Width > 0 && rect.Height > 0) path.AddRect (rect);
						break;
					case SurfaceRecorder.CommandKind.Fill:
						using (var paint = new SKPaint { Color = color, BlendMode = blend, IsAntialias = antialias }) canvas.DrawPath (path, paint);
						path.Rewind ();
						break;
					case SurfaceRecorder.CommandKind.Save:
						canvas.Save (); states.Push (new DrawState (color, blend, antialias));
						break;
					case SurfaceRecorder.CommandKind.Restore:
						if (states.Count == 0) throw new InvalidOperationException ("Unbalanced Cairo restore in recorded display list.");
						canvas.Restore (); var saved = states.Pop (); color = saved.Color; blend = saved.Blend; antialias = saved.Antialias;
						break;
					case SurfaceRecorder.CommandKind.SetAntialias:
						antialias = (Antialias)command.A != Antialias.None;
						break;
					case SurfaceRecorder.CommandKind.SetOperator:
						blend = (Operator)command.A == Operator.Source ? SKBlendMode.Src : SKBlendMode.SrcOver;
						break;
					}
				}
				if (states.Count != 0) throw new InvalidOperationException ("Unbalanced Cairo save in recorded display list.");
			} finally { path.Dispose (); }
		}

		static SKColor ToColor (double r, double g, double b, double a)
		{
			return new SKColor (ToByte (r), ToByte (g), ToByte (b), ToByte (a));
		}
		static byte ToByte (double value) => (byte)Math.Round (Math.Max (0, Math.Min (1, value)) * 255.0, MidpointRounding.AwayFromZero);

		readonly struct DrawState
		{
			internal readonly SKColor Color; internal readonly SKBlendMode Blend; internal readonly bool Antialias;
			internal DrawState (SKColor color, SKBlendMode blend, bool antialias) { Color = color; Blend = blend; Antialias = antialias; }
		}
	}
}
